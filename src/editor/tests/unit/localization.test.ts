// The editor's localization (reference-types-seeds-localization.md 3.10): hidden with one locale, the completeness
// matrix, the translation queue's walk, the settings draft, and the label patching from model.changed `translations`.
import { afterEach, describe, expect, it } from "vitest";
import type { ElementSummary, LocalizationStatus, RealtimeModelChanged } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { indexPatchOf } from "@/api/indexPatch";
import { indexQuery, keys } from "@/api/queries";
import { connectRealtime, keepLocalizedNames } from "@/realtime/sync";
import { resetContentLocale, setEffectiveContentLocale } from "@/l10n/contentLocale";
import {
  addLocale,
  applyTranslations,
  completenessMatrix,
  completenessOf,
  effectiveContentLocale,
  localizationEnabled,
  localizationProblems,
  nextOpen,
  percent,
  removeLocale,
  setFallbacks,
  shardLabel,
  toggleRequire,
  translatedLocales,
  normalizeLocaleTag,
  localeTagProblem,
} from "@/l10n/model";
import { l10nKeys } from "@/l10n/queries";
import { IDS, useMockApi } from "./harness";

const settle = () => new Promise((r) => setTimeout(r, 30));
const row = (over: Partial<ElementSummary>): ElementSummary =>
  ({ id: "X", kind: "entity", name: "X", package: null, tags: [], category: null, stereotypes: [], path: "", hash: "", ...over }) as ElementSummary;

const status = (declared: string[], shards: Record<string, [string, number, number, number, number][]> = {}): LocalizationStatus => ({
  defaultLocale: declared[0] ?? null,
  declared,
  locales: declared.slice(1).map((locale) => ({
    locale,
    chain: [locale, declared[0]],
    shards: (shards[locale] ?? []).map(([shard, expected, translated, missing, stale]) => ({ shard, expected, translated, missing, stale })),
  })),
});

describe("hidden with one locale", () => {
  it("shows nothing without localization or with one declared locale", () => {
    expect(localizationEnabled(undefined)).toBe(false);
    expect(localizationEnabled({ defaultLocale: null, declared: [], locales: [] })).toBe(false);
    expect(localizationEnabled(status(["en"]))).toBe(false);
    expect(translatedLocales(status(["en"]))).toEqual([]);
    expect(effectiveContentLocale("fr", status(["en"]))).toBeNull();
    expect(localizationEnabled(status(["en", "fr"]))).toBe(true);
  });

  it("uses the preferred content locale only when it is declared and not the default", () => {
    const s = status(["en", "fr", "fr-CA"]);
    expect(effectiveContentLocale("fr", s)).toBe("fr");
    expect(effectiveContentLocale("en", s)).toBeNull();
    expect(effectiveContentLocale("de", s)).toBeNull();
    expect(effectiveContentLocale(null, s)).toBeNull();
  });

  describe("over the mock without locales", () => {
    const api = useMockApi();
    it("answers no locales, so the editor stays single-language", async () => {
      const s = await endpoints.getLocalization();
      expect(s.defaultLocale).toBeNull();
      expect(localizationEnabled(s)).toBe(false);
    });
    it("keeps the index untouched by a translations member when no content locale is in effect", () => {
      const rows = [row({ id: IDS.invoice, name: "Invoice" })];
      expect(applyTranslations(rows, [{ locale: "fr", displayNames: { [IDS.invoice]: "Facture" } }], null)).toBe(rows);
      void api;
    });
  });
});

describe("completeness", () => {
  const s = status(["en", "fr", "de"], {
    fr: [
      [".maquettiste/model/locales/fr/billing.json", 10, 7, 2, 1],
      [".maquettiste/model/locales/fr/_reference-data.json", 4, 4, 0, 0],
      [".maquettiste/model/locales/fr/_root.json", 2, 0, 2, 0],
      [".maquettiste/model/locales/fr/billing/catalog.json", 3, 3, 0, 0],
    ],
    de: [[".maquettiste/model/locales/de/billing.json", 10, 0, 10, 0]],
  });

  it("totals a locale and rounds down", () => {
    expect(completenessOf(s, "fr")).toEqual({ expected: 19, translated: 14, missing: 4, stale: 1 });
    expect(percent({ expected: 19, translated: 14 })).toBe(73);
    expect(percent({ expected: 1000, translated: 999 })).toBe(99);
    expect(percent({ expected: 0, translated: 0 })).toBe(100);
  });

  it("builds the matrix: domains A to Z, then Not in a domain, then Reference data", () => {
    const m = completenessMatrix(s);
    expect(m.locales).toEqual(["fr", "de"]);
    expect(m.rows.map((r) => r.label)).toEqual(["billing", "billing › catalog", "Not in a domain", "Reference data"]);
    expect(m.rows[0].cells.fr).toMatchObject({ percent: 70, stale: 1 });
    expect(m.rows[0].cells.de).toMatchObject({ percent: 0 });
    expect(m.rows[1].cells.de).toBeUndefined();
    expect(shardLabel("_root")).toBe("Not in a domain");
  });
});

describe("translation queue walk", () => {
  const entries = [{ state: "translated" }, { state: "missing" }, { state: "translated" }, { state: "stale" }, { state: "fallback" }] as const;
  it("goes to the next entry that needs work, and back", () => {
    expect(nextOpen(entries, -1)).toBe(1);
    expect(nextOpen(entries, 1)).toBe(3);
    expect(nextOpen(entries, 3)).toBe(4);
    expect(nextOpen(entries, 4)).toBe(-1);
    expect(nextOpen(entries, 4, -1)).toBe(3);
    expect(nextOpen(entries, 1, -1)).toBe(-1);
  });
});

describe("Settings › Locales draft", () => {
  it("adds and removes locales with their fallbacks, and checks the settings like MQ7201", () => {
    let l = addLocale({ defaultLocale: "en", locales: ["en"] }, "fr");
    l = addLocale(addLocale(l, "fr-CA"), "fr");
    expect(l.locales).toEqual(["en", "fr", "fr-CA"]);
    l = setFallbacks(l, "fr-CA", "fr, en");
    expect(l.fallbacks).toEqual({ "fr-CA": ["fr", "en"] });
    expect(localizationProblems(l)).toEqual([]);
    expect(localizationProblems(setFallbacks(l, "fr", "fr-CA"))).toContain("The fallbacks of 'fr-CA' come back to it.");
    expect(localizationProblems({ ...l, defaultLocale: "de" })).toContain("The default locale 'de' is not among the supported locales.");
    expect(localizationProblems(addLocale(l, "not a tag"))[0]).toMatch(/BCP 47/);
    const removed = removeLocale(l, "fr");
    expect(removed.locales).toEqual(["en", "fr-CA"]);
    expect(removed.fallbacks).toEqual({ "fr-CA": ["en"] });
    expect(removeLocale(l, "en")).toBe(l);
    expect(setFallbacks(l, "fr-CA", " ").fallbacks).toBeUndefined();
  });

  it("toggles completeness kinds in a stable order and drops an empty list", () => {
    let l = toggleRequire({ defaultLocale: "en" }, "reference-row", true);
    l = toggleRequire(l, "entity", true);
    expect(l.require).toEqual(["entity", "reference-row"]);
    expect(toggleRequire(toggleRequire(l, "entity", false), "reference-row", false).require).toBeUndefined();
  });
});

describe("label patching", () => {
  const rows = [row({ id: "A", name: "Invoice", path: "a" }), row({ id: "B", name: "Payment", displayName: "Payment", path: "b" })];

  it("replaces only the rows whose name changed, as a registered patch", () => {
    const next = applyTranslations(rows, [{ locale: "fr", displayNames: { A: "Facture", B: "Payment" } }], "fr");
    expect(next).not.toBe(rows);
    expect(next.map((r) => r.displayName)).toEqual(["Facture", "Payment"]);
    expect(next[1]).toBe(rows[1]);
    expect(indexPatchOf(next)?.upserts.map((r) => r.id)).toEqual(["A"]);
    expect(applyTranslations(rows, [{ locale: "de", displayNames: { A: "Rechnung" } }], "fr")).toBe(rows);
  });

  it("keeps the localized name of a changed element until the index refetch answers", () => {
    const localized = [row({ id: "A", name: "Invoice", displayName: "Facture" })];
    const set = {
      changed: [{ id: "A", hash: "h", summary: row({ id: "A", name: "Invoice", displayName: "Invoice", hash: "h" }) }],
      deleted: [],
    } as unknown as RealtimeModelChanged;
    expect(keepLocalizedNames(set, localized).changed[0].summary?.displayName).toBe("Facture");
  });

  describe("over the mock with locales", () => {
    const api = useMockApi({ scenarios: ["locales"] });
    afterEach(() => resetContentLocale());

    it("serves per-domain shards, marks a translation stale when its source changes, and confirms it", async () => {
      const s = await endpoints.getLocalization();
      expect(translatedLocales(s)).toEqual(["fr", "fr-CA"]);
      const shards = s.locales[0].shards.map((x) => x.shard);
      expect(shards.some((p) => p.endsWith("/fr/billing.json"))).toBe(true);
      const read = await endpoints.getTranslations("fr", { owner: IDS.invoice });
      const name = read.entries.find((e) => e.field === "displayName")!;
      expect(name).toMatchObject({ translation: "Facture", state: "translated" });
      expect(name.shard).toMatch(/\/fr\/billing\.json$/);
      // The default display name changes: the French one is stale until confirmed.
      const doc = api.backend.model.docs().get(IDS.invoice)!;
      doc.displayName = "Customer invoice";
      const stale = (await endpoints.getTranslations("fr", { owner: IDS.invoice })).entries.find((e) => e.field === "displayName")!;
      expect(stale.state).toBe("stale");
      const confirmed = await endpoints.saveTranslations("fr", {
        entries: [{ id: IDS.invoice, field: "displayName", value: "Facture", confirm: true }],
        expected: { [stale.shard]: stale.shardHash! },
      });
      expect(confirmed.status).toBe(200);
      expect((await endpoints.getTranslations("fr", { owner: IDS.invoice })).entries.find((e) => e.field === "displayName")!.state).toBe("translated");
    });

    it("patches the index labels in the content locale from model.changed translations", async () => {
      const services = api.services;
      const qc = services.queryClient;
      await qc.fetchQuery({ queryKey: l10nKeys.status, queryFn: endpoints.getLocalization });
      setEffectiveContentLocale("fr");
      const rows = await qc.fetchQuery(indexQuery);
      expect(rows.find((r) => r.id === IDS.invoice)?.displayName).toBe("Facture");
      const stop = connectRealtime({ ...services, reload: () => undefined, delays: { index: 10_000, preview: 0 } });
      await settle();
      const read = await endpoints.getTranslations("fr", { owner: IDS.invoice });
      const entry = read.entries.find((e) => e.field === "displayName")!;
      await endpoints.saveTranslations("fr", {
        entries: [{ id: IDS.invoice, field: "displayName", value: "Facture client" }],
        expected: { [entry.shard]: entry.shardHash! },
      });
      await settle();
      const patched = qc.getQueryData<ElementSummary[]>(keys.index)!;
      expect(patched.find((r) => r.id === IDS.invoice)?.displayName).toBe("Facture client");
      expect(indexPatchOf(patched)?.upserts.map((r) => r.id)).toEqual([IDS.invoice]);
      stop();
    });

    it("follows the settings: a locale added in maquettiste.json is served at once", async () => {
      const doc = await endpoints.getSettings();
      const json = { ...doc.json, localization: { defaultLocale: "en", locales: ["en", "fr", "fr-CA", "de"], fallbacks: { "fr-CA": ["fr"] } } };
      const saved = await endpoints.saveSettings(json as typeof doc.json, doc.hash);
      expect(saved.outcome).toBe("saved");
      expect(translatedLocales(await endpoints.getLocalization())).toEqual(["fr", "fr-CA", "de"]);
    });
  });
});

describe("Rows grid locale columns", () => {
  it("shows translated labels in their locale columns and never writes them into the seed", async () => {
    const { gridRows, localeColumns, setCells, withTranslatedLabels } = await import("@/workspaces/reference-data/rowsModel");
    const seed = { kind: "seed", id: "S", name: "Unit", target: "T", columns: ["code", "label"], rows: [{ id: "R1", values: ["kg", "Kilogram"] }] } as never;
    const rows = withTranslatedLabels(gridRows([seed]), new Map([["fr", new Map([["R1", "Kilogramme"]])]]));
    expect(rows[0].values).toMatchObject({ code: "kg", label: "Kilogram", "@label:fr": "Kilogramme" });
    expect(localeColumns(["fr"])[0]).toMatchObject({ key: "@label:fr", locale: "fr", label: "label (fr)" });
    setCells(seed, "R1", { "@label:fr": "x", label: "kilogram" });
    expect((seed as { columns: string[] }).columns).toEqual(["code", "label"]);
    expect((seed as { rows: { values: unknown[] }[] }).rows[0].values).toEqual(["kg", "kilogram"]);
  });
});

describe("locale input normalization", () => {
  it("normalizes as typed and says why a tag cannot be added", () => {
    expect(normalizeLocaleTag("zh_cn")).toBe("zh-CN");
    expect(normalizeLocaleTag("ZH_CN")).toBe("zh-CN");
    expect(normalizeLocaleTag(" fr-ca ")).toBe("fr-CA");
    expect(normalizeLocaleTag("zh_hant_tw")).toBe("zh-Hant-TW");
    expect(normalizeLocaleTag("es-419")).toBe("es-419");
    expect(normalizeLocaleTag("zh-")).toBe("zh-");
    expect(localeTagProblem("", ["en"])).toBeNull();
    expect(localeTagProblem("zh-CN", ["en"])).toBeNull();
    expect(localeTagProblem("en", ["en"])).toBe("en is already a supported locale.");
    expect(localeTagProblem("zh-", ["en"])).toContain("use language-REGION with a hyphen, such as zh-CN");
    expect(localeTagProblem("zh cn", ["en"])).toContain("no spaces");
  });
});
