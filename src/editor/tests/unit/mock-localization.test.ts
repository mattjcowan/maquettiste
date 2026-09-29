// The mock backend's localization, seed CSV and reference type usage operations
// (reference-types-seeds-localization.md 3.9), each JSON answer validated against the contract.
import { describe, expect, it } from "vitest";
import { validatorAt } from "@/mocks/contract";
import { readCsv, writeCsv } from "@/mocks/model/localization";
import { IDS, useMockApi } from "./harness";

type Json = Record<string, unknown>;
const esc = (s: string) => s.replace(/~/g, "~0").replace(/\//g, "~1");

describe("mock localization (locales scenario)", () => {
  const mock = useMockApi({ scenarios: ["locales"] });

  async function call(method: string, path: string, template?: string, body?: unknown, headers: Record<string, string> = {}) {
    const response = await fetch(mock.url(path), {
      method,
      headers: { "Content-Type": "application/json", ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const type = response.headers.get("Content-Type") ?? "";
    if (!/json/.test(type)) return { status: response.status, text: await response.text(), json: null as unknown as Json };
    const json = (await response.json()) as Json;
    if (template && response.status < 400) {
      const validate = validatorAt(`/paths/${esc(template)}/${method.toLowerCase()}/responses/${response.status}/content/application~1json/schema`);
      expect(validate(json), JSON.stringify(validate.errors?.slice(0, 3))).toBe(true);
    }
    return { status: response.status, text: "", json };
  }

  it("serves status, entries and the paged queue, and 404s an undeclared locale", async () => {
    const status = await call("GET", "/api/localization", "/api/localization");
    expect(status.json.defaultLocale).toBe("en");
    expect((status.json.locales as Json[]).map((l) => l.locale)).toEqual(["fr", "fr-CA"]);
    const owner = await call("GET", `/api/localization/fr/entries?owner=${IDS.invoice}`, "/api/localization/{locale}/entries");
    expect((owner.json.entries as Json[]).find((e) => e.field === "displayName")!.translation).toBe("Facture");
    const queue = await call("GET", "/api/localization/fr-CA/entries?missing=true", "/api/localization/{locale}/entries");
    const invoice = (queue.json.entries as Json[]).find((e) => e.id === IDS.invoice && e.field === "displayName")!;
    expect(invoice.state).toBe("fallback");
    expect(invoice.effective).toBe("Facture");
    expect((await call("GET", "/api/localization/de/entries")).status).toBe(404);
  });

  it("writes with the shard hash, answers 409 on a stale hash and 422 on an unknown node, and publishes display names", async () => {
    const read = await call("GET", `/api/localization/fr/entries?owner=${IDS.invoice}`);
    const entry = (read.json.entries as Json[])[0];
    const expected = { [String(entry.shard)]: String(entry.shardHash) };
    const saved = await call("PUT", "/api/localization/fr/entries", "/api/localization/{locale}/entries", {
      entries: [{ id: IDS.invoice, field: "displayName", value: "Facture client" }],
      expected,
    });
    expect(saved.status).toBe(200);
    const stale = await call("PUT", "/api/localization/fr/entries", "/api/localization/{locale}/entries", {
      entries: [{ id: IDS.invoice, field: "displayName", value: "x" }],
      expected,
    });
    expect(stale.status).toBe(409);
    const invalid = await call("PUT", "/api/localization/fr/entries", "/api/localization/{locale}/entries", {
      entries: [{ id: IDS.invoice, field: "label", value: "x" }],
      expected: {},
    });
    expect(invalid.status).toBe(422);
    const event = mock.backend.realtime.published.filter((p) => p.event === "model.changed").at(-1)!.payload as Json;
    const translations = event.translations as { locale: string; displayNames: Record<string, string> }[];
    expect(translations.map((t) => t.locale)).toEqual(["fr", "fr-CA"]);
    expect(translations[1].displayNames[IDS.invoice]).toBe("Facture client");
  });

  it("fills the index from a locale's chain with its own ETag, and 400s an undeclared locale", async () => {
    const plain = await call("GET", "/api/model/index", "/api/model/index");
    const french = await call("GET", "/api/model/index?locale=fr-CA", "/api/model/index");
    const row = (french.json as unknown as Json[]).find((r) => r.id === IDS.invoice)!;
    expect(row.displayName).toBe("Facture");
    expect((plain.json as unknown as Json[]).find((r) => r.id === IDS.invoice)!.displayName).not.toBe("Facture");
    expect((await call("GET", "/api/model/index?locale=de")).status).toBe(400);
  });

  it("exports XLIFF and CSV and imports them back: no change, then a preview, then an applied change", async () => {
    const xliff = await call("GET", "/api/localization/fr/export?format=xliff");
    expect(xliff.text).toContain(`<unit id="${IDS.invoice}/displayName">`);
    const same = await call("POST", "/api/localization/fr/import", "/api/localization/{locale}/import", { format: "xliff", content: xliff.text });
    expect(same.json.changed).toEqual([]);
    const edited = xliff.text.replace("<target>Facture</target>", "<target>Note</target>");
    const preview = await call("POST", "/api/localization/fr/import", "/api/localization/{locale}/import", { format: "xliff", content: edited });
    expect((preview.json.changed as Json[])[0].after).toBe("Note");
    const applied = await call("POST", "/api/localization/fr/import?dryRun=false", "/api/localization/{locale}/import", { format: "xliff", content: edited });
    expect(applied.json.applied).toBe(true);
    const csv = await call("GET", "/api/localization/fr/export?format=csv");
    expect(csv.text.startsWith("id,field,source,translation,state,shard\n")).toBe(true);
    const csvSame = await call("POST", "/api/localization/fr/import", "/api/localization/{locale}/import", { format: "csv", content: csv.text });
    expect(csvSame.json.changed).toEqual([]);
  });

  it("answers 404 for seed CSV and usage of ids that are not seeds or reference types", async () => {
    expect((await call("GET", `/api/seeds/${IDS.invoice}/csv`)).status).toBe(404);
    expect((await call("POST", `/api/seeds/${IDS.invoice}/csv`, undefined, { content: "@id\n" })).status).toBe(404);
    expect((await call("GET", `/api/reference-types/${IDS.invoice}/usage`)).status).toBe(404);
  });
});

describe("mock CSV", () => {
  it("round trips quotes, commas and line breaks, and the spreadsheet form", () => {
    const rows = [
      ["@id", "@label"],
      ["A", 'say "hi", twice'],
      ["B", "two\nlines"],
    ];
    expect(readCsv(writeCsv(rows))).toEqual(rows);
    expect(writeCsv([["a"]], true)).toBe("﻿a\r\n");
    expect(readCsv(writeCsv(rows, true))).toEqual(rows);
  });
});

describe("mock localization off by default", () => {
  const mock = useMockApi();
  it("reports no locales", async () => {
    const response = await fetch(mock.url("/api/localization"));
    expect(await response.json()).toEqual({ defaultLocale: null, declared: [], locales: [] });
  });
});
