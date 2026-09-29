// The measured optimizations of the scale round (explorer-redesign.md 4.5): the search worker takes the index's own JSON
// text, and the vocabulary lookups are cached per index array. Each must give what the uncached path gives.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { rememberIndexText, takeIndexText } from "@/api/indexText";
import { createIndexLoader } from "@/api/indexLoader";
import { createSearchHandler, encodeRows, type FromWorker } from "@/search/engine";
import { filterVocabularies, ownVocabulary, vocabulariesOnChain } from "@/model/vocabularies";

const row = (id: string, kind: string, name: string, extra: Partial<ElementSummary> = {}): ElementSummary =>
  ({ id, kind, name, package: null, tags: [], category: null, stereotypes: [], hash: "h", path: `${id}.json`, ...extra }) as ElementSummary;

const ROWS: ElementSummary[] = [
  row("P1", "package", "Billing"),
  row("P2", "package", "Invoicing", { package: "P1" }),
  row("E1", "entity", "Invoice", { package: "P2", tags: ["core"] }),
  row("T0", "tag-vocabulary", "global"),
  row("T2", "tag-vocabulary", "invoicing", { package: "P2" }),
  row("T1", "tag-vocabulary", "billing", { package: "P1" }),
  row("C0", "category-tree", "global"),
];

describe("search worker handoff from the index text", () => {
  it("loads the same rows from the JSON text as from the encoded rows", () => {
    const viaRows = createSearchHandler();
    const encoded = viaRows({ type: "rows", version: 1, data: encodeRows(ROWS) }) as Extract<FromWorker, { type: "ready" }>;
    const handle = createSearchHandler();
    const fromText = handle({ type: "rows", version: 1, json: JSON.stringify(ROWS) }) as Extract<FromWorker, { type: "ready" }>;
    expect(fromText.count).toBe(encoded.count);
    const ask = { type: "rank", seq: 2, text: "invoic", limit: 5 } as const;
    const { ms: _a, ...expected } = viaRows(ask) as Extract<FromWorker, { type: "rank" }>;
    const { ms: _b, ...actual } = handle(ask) as Extract<FromWorker, { type: "rank" }>;
    expect(actual).toEqual(expected);
    expect(actual.total).toBeGreaterThan(0);
    // In slices: nothing until the last one, then the same answer.
    const sliced = createSearchHandler();
    const text = JSON.stringify(ROWS);
    expect(sliced({ type: "rows", version: 3, json: text.slice(0, 50), first: true, more: true })).toBeUndefined();
    expect(sliced({ type: "rows", version: 3, json: text.slice(50) })).toMatchObject({ type: "ready", version: 3, count: encoded.count });
    const { ms: _c, ...slicedAnswer } = sliced(ask) as Extract<FromWorker, { type: "rank" }>;
    expect(slicedAnswer).toEqual(expected);
  });

  it("keeps the text beside the parsed rows until it is taken once", async () => {
    const text = JSON.stringify(ROWS);
    const loader = createIndexLoader({ fetch: async () => ({ notModified: false, text, etag: '"e"' }) as never });
    const rows = await loader.load();
    expect(takeIndexText(rows)).toBe(text);
    expect(takeIndexText(rows)).toBeUndefined();
    const other: unknown[] = [];
    rememberIndexText(other, "[]");
    expect(takeIndexText([])).toBeUndefined();
  });
});

describe("vocabulary lookups cached per index", () => {
  it("gives the nearest-first chain, the same array for the same rows, and a new one for new rows", () => {
    const chain = vocabulariesOnChain("tag-vocabulary", "P2", ROWS);
    expect(chain.map((v) => [v.id, v.scope, v.domain])).toEqual([
      ["T2", "P2", "Invoicing"],
      ["T1", "P1", "Billing"],
      ["T0", null, null],
    ]);
    expect(vocabulariesOnChain("tag-vocabulary", "P2", ROWS)).toBe(chain);
    expect(vocabulariesOnChain("tag-vocabulary", "P2", [...ROWS])).not.toBe(chain);
    expect(vocabulariesOnChain("tag-vocabulary", "P2", [...ROWS])).toEqual(chain);
    expect(ownVocabulary("tag-vocabulary", null, ROWS)?.id).toBe("T0");
    expect(ownVocabulary("category-tree", "P1", ROWS)).toBeUndefined();
    expect(filterVocabularies("tag-vocabulary", null, ROWS).map((v) => v.id)).toEqual(["T0", "T1", "T2"]);
  });
});
