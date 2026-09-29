// The search worker's parts (explorer-redesign.md 3.1): the query syntax, the ranking, the index and its message
// protocol, and the client that talks to it (in-process here: jsdom has no Worker).
import { describe, expect, it } from "vitest";
import type { ElementSummary, TableSummary } from "@/api/types";
import { chipsOf, isEmptyQuery, matchRange, parseQuery, termMatcher, tokens, withoutQualifier } from "@/search/query";
import { Tier, compareRanked, subsequence, tierOf, wordStartMatch, wordsOf } from "@/search/rank";
import { SearchIndex, createSearchHandler, encodeRows, encodeTables, idsOf, tableKeyOf, type FromWorker } from "@/search/engine";
import { SearchClient } from "@/search/client";
import { emptyFilter, matches } from "@/explorer/filter";

const row = (id: string, kind: string, name: string, extra: Partial<ElementSummary> = {}): ElementSummary =>
  ({ id, kind, name, package: null, tags: [], category: null, stereotypes: [], hash: "h", path: `${id}.json`, ...extra }) as ElementSummary;

const ROWS: ElementSummary[] = [
  row("P1", "package", "Billing"),
  row("P2", "package", "Invoicing", { package: "P1" }),
  row("P3", "package", "Shipping"),
  row("E1", "entity", "Invoice", { package: "P2", tags: ["pii"], stereotypes: ["aggregate"], category: "C2" }),
  row("E2", "entity", "InvoiceLine", { package: "P2", displayName: "Ligne de facture" }),
  row("E3", "entity", "Order", { package: "P3" }),
  row("E4", "entity", "OrderLine", { package: "P3", tags: ["PII"] }),
  row("N1", "enum", "InvoiceStatus", { package: "P1" }),
  row("R1", "reference-type", "Currency"),
  row("D1", "diagram", "Invoice overview"),
  row("DB", "database", "main"),
  row("T1", "tag-vocabulary", "Tags"),
];
const TABLES = [
  { key: "invoice_lines", name: "invoice_lines", schema: "billing", entityId: "E2" },
  { key: "orders", name: "orders", schema: "sales", entityId: "E3" },
] as unknown as TableSummary[];

function loaded(withTables = true): SearchIndex {
  const index = new SearchIndex();
  index.loadRows(1, encodeRows(ROWS));
  if (withTables) index.loadTables("DB", encodeTables("DB", TABLES));
  index.loadCategories([
    { id: "C1", name: "Finance", parent: null },
    { id: "C2", name: "Receivables", parent: "C1" },
  ]);
  return index;
}

/** A filter answer with its ids split. */
const filterOf = (index: SearchIndex, ...args: Parameters<SearchIndex["filter"]>) => {
  const answer = index.filter(...args);
  return { ...answer, ids: idsOf(answer.ids) };
};

describe("search/query", () => {
  it("parses the four operators, contains by default", () => {
    expect(parseQuery("line")).toMatchObject({ term: "line", op: "contains", explicit: false });
    expect(parseQuery("*Line")).toMatchObject({ term: "line", op: "contains", explicit: true });
    expect(parseQuery("^inv")).toMatchObject({ term: "inv", op: "prefix", explicit: true });
    expect(parseQuery("~inv%line")).toMatchObject({ term: "inv%line", op: "like" });
    expect(parseQuery("=Invoice")).toMatchObject({ term: "invoice", op: "equals" });
  });

  it("matches case-insensitively per operator", () => {
    const m = (q: string, s: string) => termMatcher(parseQuery(q))!(s.toLowerCase());
    expect(m("line", "InvoiceLine")).toBe(true);
    expect(m("^inv", "InvoiceLine")).toBe(true);
    expect(m("^line", "InvoiceLine")).toBe(false);
    expect(m("~inv%line", "InvoiceLine")).toBe(true);
    expect(m("~inv%line", "InvoiceLines")).toBe(false);
    expect(m("~%line", "OrderLine")).toBe(true);
    expect(m("~a.b", "axb")).toBe(false);
    expect(m("=invoice", "Invoice")).toBe(true);
    expect(m("=invoice", "InvoiceLine")).toBe(false);
    expect(termMatcher(parseQuery("kind:enum"))).toBeUndefined();
  });

  it("reads the qualifiers, quoted values, and leaves empty ones out", () => {
    const q = parseQuery('kind:enum in:"Order taking" tag:PII st:aggregate cat:finance ^inv kind:');
    expect(q.q).toEqual({ kind: ["enum"], in: ["order taking"], tag: ["pii"], st: ["aggregate"], cat: ["finance"] });
    expect(q).toMatchObject({ term: "inv", op: "prefix" });
    expect(tokens('a "b c" d')).toEqual(["a", "b c", "d"]);
    expect(isEmptyQuery(parseQuery("  "))).toBe(true);
    expect(isEmptyQuery(parseQuery("tag:x"))).toBe(false);
  });

  it("shows the qualifiers as chips and removes one", () => {
    const text = 'line kind:entity in:"Order taking"';
    expect(chipsOf(text).map((c) => c.label)).toEqual(["Kind: entity", "In: Order taking"]);
    expect(withoutQualifier(text, "kind", "entity")).toBe('line in:"Order taking"');
    expect(withoutQualifier(text, "in", "Order taking")).toBe("line kind:entity");
  });

  it("gives the highlighted span", () => {
    expect(matchRange("InvoiceLine", parseQuery("line"))).toEqual([7, 11]);
    expect(matchRange("InvoiceLine", parseQuery("^inv"))).toEqual([0, 3]);
    expect(matchRange("InvoiceLine", parseQuery("~inv%"))).toBeUndefined();
  });

  it("filter.ts's one-row test uses the same syntax", () => {
    expect(matches(ROWS[4], { ...emptyFilter, text: "^invoicel" })).toBe(true);
    expect(matches(ROWS[4], { ...emptyFilter, text: "facture" })).toBe(true);
    expect(matches(ROWS[4], { ...emptyFilter, text: "=invoice" })).toBe(false);
  });
});

describe("search/rank", () => {
  it("splits words at camel, snake, space and digit boundaries", () => {
    expect(wordsOf("InvoiceLine")).toEqual(["invoice", "line"]);
    expect(wordsOf("invoice_lines")).toEqual(["invoice", "lines"]);
    expect(wordsOf("HTTPServer2 log")).toEqual(["http", "server", "2", "log"]);
  });

  it("finds word starts and subsequences", () => {
    expect(wordStartMatch("invli", wordsOf("InvoiceLine"))).toBe(true);
    expect(wordStartMatch("il", wordsOf("InvoiceLine"))).toBe(true);
    expect(wordStartMatch("nvl", wordsOf("InvoiceLine"))).toBe(false);
    expect(subsequence("ivln", "invoiceline")).toBe(true);
  });

  it("orders the tiers: exact, prefix, word start, substring, fuzzy", () => {
    expect(tierOf("invoice", "Invoice")).toBe(Tier.Exact);
    expect(tierOf("inv", "InvoiceLine")).toBe(Tier.Prefix);
    expect(tierOf("invli", "InvoiceLine")).toBe(Tier.WordStart);
    expect(tierOf("inv_li", "invoice_lines")).toBe(Tier.WordStart);
    expect(tierOf("oiceli", "InvoiceLine")).toBe(Tier.Substring);
    expect(tierOf("ivcln", "InvoiceLine")).toBe(Tier.Fuzzy);
    expect(tierOf("xyz", "InvoiceLine")).toBe(Tier.None);
  });

  it("breaks ties by kind weight, nearness, recency, length, then name", () => {
    const base = { tier: Tier.Prefix, near: 2, recent: -1 };
    const sorted = [
      { ...base, kind: "diagram", name: "Ab" },
      { ...base, kind: "entity", name: "Abcd" },
      { ...base, kind: "entity", name: "Abc", near: 0 },
      { ...base, kind: "entity", name: "Abx", recent: 0 },
      { ...base, kind: "entity", name: "Abe" },
      { ...base, kind: "entity", name: "Abd" },
      { ...base, kind: "table", name: "a" },
    ].sort(compareRanked);
    expect(sorted.map((s) => s.name)).toEqual(["Abc", "Abx", "Abd", "Abe", "Abcd", "a", "Ab"]);
  });
});

describe("search/engine", () => {
  it("filters one explorer and counts every place", () => {
    const r = filterOf(loaded(), "line", "domain-model");
    expect(r.ids.sort()).toEqual(["E2", "E4"]);
    expect(r.counts).toMatchObject({ "domain-model": 2, databases: 1, diagrams: 0 });
    expect(filterOf(loaded(), "line", "databases").ids).toEqual([tableKeyOf("DB", "invoice_lines")]);
  });

  it("matches the display name and the physical name", () => {
    expect(filterOf(loaded(), "facture", "domain-model").ids).toEqual(["E2"]);
    expect(filterOf(loaded(), "^orders", "databases").ids).toEqual([tableKeyOf("DB", "orders")]);
  });

  it("applies the qualifiers", () => {
    const ids = (text: string, place: "domain-model" | "databases" = "domain-model") => filterOf(loaded(), text, place).ids.sort();
    expect(ids("kind:enum")).toEqual(["N1"]);
    expect(ids("inv kind:entities")).toEqual(["E1", "E2"]);
    expect(ids("kind:domain ^b")).toEqual(["P1"]);
    expect(ids("in:billing")).toEqual(["E1", "E2", "N1", "P1", "P2"]);
    expect(ids("in:billing", "databases")).toEqual([tableKeyOf("DB", "invoice_lines")]);
    expect(ids("tag:pii")).toEqual(["E1", "E4"]);
    expect(ids("st:AGGREGATE")).toEqual(["E1"]);
    expect(ids("cat:finance")).toEqual(["E1"]);
    expect(filterOf(loaded(), "", "domain-model", { tags: ["pii"], categories: [], stereotypes: [] }).ids.sort()).toEqual(["E1", "E4"]);
  });

  it("says when table names are still loading", () => {
    expect(filterOf(loaded(false), "line", "domain-model").tablesLoading).toBe(true);
    expect(filterOf(loaded(true), "line", "domain-model").tablesLoading).toBe(false);
  });

  it("ranks for quick open with paths, limits and totals", () => {
    const r = loaded().rank("invoice", 3);
    expect(r.total).toBe(5);
    expect(r.hits.map((h) => h.name)).toEqual(["Invoice", "InvoiceLine", "invoice_lines"]);
    expect(r.hits[0]).toMatchObject({ path: "Billing / Invoicing", place: "domain-model", tier: Tier.Exact });
    expect(r.hits[2]).toMatchObject({ kind: "table", db: "DB", path: "main · billing" });
    expect(loaded().rank("invli", 5).hits[0].name).toBe("InvoiceLine");
    expect(
      loaded()
        .rank("inv_li", 5)
        .hits.map((h) => h.name),
    ).toContain("invoice_lines");
  });

  it("prefers the selection's domain and recent elements among equals", () => {
    expect(
      loaded()
        .rank("line", 2)
        .hits.map((h) => h.name),
    ).toEqual(["OrderLine", "InvoiceLine"]);
    expect(loaded().rank("line", 2, { domain: "P1", recent: [] }).hits[0].name).toBe("InvoiceLine");
    expect(loaded().rank("line", 2, { domain: null, recent: ["E2"] }).hits[0].name).toBe("InvoiceLine");
  });

  it("uses the operator's plain match when one is typed", () => {
    expect(
      loaded()
        .rank("=invoice", 10)
        .hits.map((h) => h.name),
    ).toEqual(["Invoice"]);
    expect(
      loaded()
        .rank("~%line", 10)
        .hits.map((h) => h.name),
    ).toEqual(["OrderLine", "InvoiceLine"]);
  });
});

describe("search worker protocol", () => {
  it("answers rows with ready, and each question with its sequence number", () => {
    const handle = createSearchHandler();
    const ready = handle({ type: "rows", version: 7, data: encodeRows(ROWS) }) as Extract<FromWorker, { type: "ready" }>;
    expect(ready).toMatchObject({ type: "ready", version: 7, count: ROWS.length });
    expect(handle({ type: "tables", db: "DB", data: encodeTables("DB", TABLES) })).toBeUndefined();
    expect(handle({ type: "categories", data: [] })).toBeUndefined();
    expect(handle({ type: "filter", seq: 3, text: "order", place: "databases" })).toMatchObject({
      type: "filter",
      seq: 3,
      ids: tableKeyOf("DB", "orders"),
      tablesLoading: false,
    });
    expect(handle({ type: "rank", seq: 4, text: "^curr", limit: 5 })).toMatchObject({ type: "rank", seq: 4, total: 1, hits: [{ id: "R1" }] });
  });

  it("keeps names with the separators intact enough to search", () => {
    const handle = createSearchHandler();
    handle({ type: "rows", version: 1, data: encodeRows([row("X", "entity", "a\u001fb")]) });
    expect(handle({ type: "filter", seq: 1, text: "a b", place: "domain-model" })).toMatchObject({ ids: "X" });
  });

  it("the client pairs answers, dedupes feeds and bumps its version", async () => {
    const client = new SearchClient(null);
    const seen: number[] = [];
    client.subscribe(() => seen.push(client.getVersion()));
    client.setRows(ROWS);
    client.setRows(ROWS);
    client.setTables("DB", undefined);
    const loading = await client.filter("line", "domain-model");
    expect(loading.tablesLoading).toBe(true);
    client.setTables("DB", TABLES);
    const [a, b] = await Promise.all([client.filter("line", "databases"), client.rank("order", 10)]);
    expect(a.ids).toEqual([tableKeyOf("DB", "invoice_lines")]);
    expect(b.hits.map((h) => h.name)).toEqual(["Order", "OrderLine", "orders"]);
    expect(client.ready).toBe(true);
    expect(seen.length).toBe(4);
  });
});
