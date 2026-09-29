// Explorer filters, scopes and the pinned filter (explorer-redesign.md 3.2), favorites and recents (3.3), and the
// related-element highlighting and presence maps (1.9, 1.10).
import { afterEach, describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import {
  applyScope,
  categoryScope,
  chipCount,
  domainTest,
  emptyFilter,
  isFiltering,
  kindTest,
  matchingIds,
  normalizeFilter,
  sameChips,
  scopeOf,
  type ExplorerFilter,
} from "@/explorer/filter";
import { SearchIndex, encodeRows, idsOf } from "@/search/engine";
import { openTab } from "@/editors/tabs";
import { buildForest, presenceCounts, relatedCounts, relatedKeys } from "@/explorer/tree";
import { inExplorer } from "@/explorer/Favorites";
import { createEditorStore } from "@/state/store";

const row = (id: string, kind: string, name: string, extra: Partial<ElementSummary> = {}): ElementSummary =>
  ({ id, kind, name, package: null, tags: [], category: null, stereotypes: [], hash: "h", path: `${id}.json`, ...extra }) as ElementSummary;

const ROWS: ElementSummary[] = [
  row("P1", "package", "Billing"),
  row("P2", "package", "Invoicing", { package: "P1" }),
  row("P3", "package", "Sales"),
  row("E1", "entity", "Party", { package: "P1" }),
  row("E2", "entity", "Customer", { package: "P2", base: "E1", tags: ["pii"], category: "C2", stereotypes: ["audited"] }),
  row("E3", "entity", "Order", { package: "P3" }),
  row("R1", "relation", "places", {
    package: "P3",
    ends: [
      { entity: "E2", role: "customer" },
      { entity: "E3", role: "orders" },
    ],
  }),
  row("N1", "enum", "Status", { package: "P2" }),
  row("D1", "diagram", "Overview", { package: "P1", memberCount: 2 }),
];
const byId = new Map(ROWS.map((r) => [r.id, r]));
const PARENTS = new Map<string, string | null>([
  ["C1", null],
  ["C2", "C1"],
  ["C3", null],
]);
const f = (patch: Partial<ExplorerFilter>): ExplorerFilter => ({ ...emptyFilter, ...patch });
const ids = (filter: ExplorerFilter, ctx = {}) => matchingIds(ROWS, filter, { byId, categoryParents: PARENTS, ...ctx }).sort();

describe("filter predicates (3.2)", () => {
  it("builds the kind, domain and category tests", () => {
    expect(kindTest([])).toBeUndefined();
    expect(kindTest(["entity", "enum"])!("enum")).toBe(true);
    expect(kindTest(["entity"])!("relation")).toBe(false);
    const inBilling = domainTest("P1", (id) => byId.get(id)?.package);
    expect([inBilling("P1"), inBilling("P2"), inBilling("P3"), inBilling(null)]).toEqual([true, true, false, false]);
    expect([...categoryScope(["C1"], PARENTS)].sort()).toEqual(["C1", "C2"]);
    expect([...categoryScope(["C2"], new Map())]).toEqual(["C2"]);
  });

  it("guards the domain walk against a cycle", () => {
    const loop = domainTest("X", (id) => (id === "A" ? "B" : "A"));
    expect(loop("A")).toBe(false);
  });

  it("filters by kind, domain scope (sub-domains included), category subtree, errors and diagram", () => {
    expect(ids(f({ kinds: ["entity"] }))).toEqual(["E1", "E2", "E3"]);
    expect(ids(f({ domain: "P1" }))).toEqual(["D1", "E1", "E2", "N1", "P1", "P2"]);
    expect(ids(f({ categories: ["C1"] }))).toEqual(["E2"]);
    expect(ids(f({ categories: ["C3"] }))).toEqual([]);
    expect(ids(f({ errors: true }), { errorIds: new Set(["E3", "R1"]) })).toEqual(["E3", "R1"]);
    expect(ids(f({ errors: true }))).toEqual([]);
    expect(ids(f({ diagram: "D1" }), { diagramMembers: new Set(["E1", "E2"]) })).toEqual(["E1", "E2"]);
  });

  it("combines the chips with each other and with the search text", () => {
    expect(ids(f({ kinds: ["entity"], domain: "P1" }))).toEqual(["E1", "E2"]);
    expect(ids(f({ kinds: ["entity"], tags: ["pii"], stereotypes: ["audited"] }))).toEqual(["E2"]);
    expect(ids(f({ text: "ord", kinds: ["entity", "relation"] }))).toEqual(["E3"]);
  });

  it("counts chips and knows when it filters", () => {
    expect(isFiltering(emptyFilter)).toBe(false);
    expect(isFiltering(f({ errors: true }))).toBe(true);
    expect(chipCount(f({ kinds: ["entity", "enum"], domain: "P1", errors: true, diagram: "D1" }))).toBe(5);
  });
});

describe("the search worker's chips (3.2)", () => {
  const index = new SearchIndex();
  index.loadRows(1, encodeRows(ROWS));
  const extra = { tags: [], categories: [], stereotypes: [] };

  it("narrows by kind, domain, errors and diagram members", () => {
    expect(idsOf(index.filter("", "domain-model", { ...extra, kinds: ["entity"] }).ids).sort()).toEqual(["E1", "E2", "E3"]);
    expect(idsOf(index.filter("", "domain-model", { ...extra, kinds: ["entity"], domain: "P1" }).ids).sort()).toEqual(["E1", "E2"]);
    expect(idsOf(index.filter("", "domain-model", { ...extra, errorIds: ["E3"] }).ids)).toEqual(["E3"]);
    expect(idsOf(index.filter("", "domain-model", { ...extra, errorIds: [] }).ids)).toEqual([]);
    const onDiagram = index.filter("", "domain-model", { ...extra, members: ["E1", "E2"] });
    expect(idsOf(onDiagram.ids).sort()).toEqual(["E1", "E2"]);
    expect(onDiagram.counts.diagrams).toBe(0);
  });

  it("answers nothing when no chip narrows and the text is empty", () => {
    expect(idsOf(index.filter("", "domain-model", extra).ids)).toEqual([]);
  });
});

describe("scopes and the pinned filter (3.2)", () => {
  afterEach(() => window.localStorage.clear());

  it("saves the chips without the text and applies them keeping the text", () => {
    const filter = f({ text: "inv", kinds: ["entity"], domain: "P1", tags: ["pii"], errors: true });
    const scope = scopeOf("Billing core", filter);
    expect(scope).toEqual({
      name: "Billing core",
      kinds: ["entity"],
      domain: "P1",
      tags: ["pii"],
      stereotypes: [],
      categories: [],
      errors: true,
      diagram: null,
    });
    const applied = applyScope(f({ text: "cust" }), scope);
    expect(applied.text).toBe("cust");
    expect(sameChips(applied, filter)).toBe(true);
    expect(sameChips(applied, f({ kinds: ["entity"] }))).toBe(false);
  });

  it("reads a stored or shared filter defensively", () => {
    expect(normalizeFilter(null)).toEqual(emptyFilter);
    expect(normalizeFilter({ kinds: ["entity", 3, ""], domain: 5, errors: "yes", text: "x" })).toEqual(f({ kinds: ["entity"], text: "x" }));
  });

  it("keeps a pinned filter and the user's scopes across a reload", () => {
    const store = createEditorStore();
    store.getState().setExplorerFilter("domain-model", f({ kinds: ["enum"] }));
    store.getState().pinExplorerFilter("domain-model", true);
    store.getState().setExplorerFilter("domain-model", f({ kinds: ["entity"] }));
    store.getState().saveScope("Entities", f({ kinds: ["entity"] }));
    const reloaded = createEditorStore();
    expect(reloaded.getState().explorer.views["domain-model"].pinnedFilter).toBe(true);
    expect(reloaded.getState().explorer.views["domain-model"].filter.kinds).toEqual(["entity"]);
    expect(reloaded.getState().explorer.views.databases.pinnedFilter).toBe(false);
    expect(reloaded.getState().explorer.scopes.map((s) => s.name)).toEqual(["Entities"]);
    reloaded.getState().pinExplorerFilter("domain-model", false);
    expect(createEditorStore().getState().explorer.views["domain-model"].filter).toEqual(emptyFilter);
  });

  it("keeps favorites and the last 20 opened elements, and highlighting is on by default", () => {
    const store = createEditorStore();
    expect(store.getState().explorer.highlightRelated).toBe(true);
    store.getState().toggleFavorite("E2");
    for (let i = 0; i < 25; i++) store.getState().noteRecent(`X${i}`);
    store.getState().noteRecent("E1");
    const reloaded = createEditorStore();
    expect(reloaded.getState().explorer.favorites).toEqual(["E2"]);
    expect(reloaded.getState().recent[0]).toBe("E1");
    expect(reloaded.getState().recent).toHaveLength(20);
    reloaded.getState().setHighlightRelated(false);
    expect(createEditorStore().getState().explorer.highlightRelated).toBe(false);
  });
});

describe("recent elements are the opened ones (3.3)", () => {
  afterEach(() => localStorage.clear());

  it("does not count a selection as opened", () => {
    const store = createEditorStore();
    store.getState().select(["E1"]);
    store.getState().select(["E2", "E3"]);
    expect(store.getState().recent).toEqual([]);
    expect(createEditorStore().getState().recent).toEqual([]);
  });

  it("counts a pinned editor open and a go-to, not a preview open or the General-mode tab following", () => {
    const store = createEditorStore();
    store.getState().updateEditors((e) => openTab(e, { id: "E1", kind: "entity" }, { pin: false }));
    expect(store.getState().recent).toEqual([]);
    store.getState().updateEditors((e) => openTab(e, { id: "E1", kind: "entity" }, { pin: true }));
    expect(store.getState().recent).toEqual(["E1"]);
    store.getState().updateEditors((e) => openTab(e, { id: "E2", kind: "entity" }, { pin: true }));
    store.getState().updateEditors((e) => openTab(e, { id: "E1", kind: "entity" }, { pin: true }));
    expect(store.getState().recent).toEqual(["E2", "E1"]);
    store.getState().requestCenter("E3");
    expect(store.getState().recent).toEqual(["E3", "E2", "E1"]);
  });
});

describe("related highlighting and presence maps (1.9, 1.10)", () => {
  const forest = buildForest({ rows: ROWS });
  const key = (id: string) => forest.place.get(id)!;

  it("relates an entity to its relations, their other ends, its base and its derived entities", () => {
    const customer = relatedKeys(forest, key("E2"));
    expect(customer).toEqual(new Set([key("R1"), key("E3"), key("E1")]));
    expect(relatedKeys(forest, key("E1"))).toEqual(new Set([key("E2")]));
    expect(relatedKeys(forest, key("R1"))).toEqual(new Set([key("E2"), key("E3")]));
  });

  it("counts related rows on every ancestor, never the row itself", () => {
    const counts = relatedCounts(forest, relatedKeys(forest, key("E2")));
    expect(counts.get(key("P3"))).toBe(2);
    expect(counts.get(key("P1"))).toBe(1);
    expect(counts.has(key("E1"))).toBe(false);
  });

  it("rolls presence up as distinct people per ancestor", () => {
    const counts = presenceCounts(
      forest,
      new Map([
        ["E2", ["Ana", "Ben"]],
        ["E1", ["Ana"]],
        ["E3", ["Cy"]],
        ["gone", ["Dee"]],
      ]),
    );
    expect(counts.get(key("P2"))).toBe(2);
    expect(counts.get(key("P1"))).toBe(2);
    expect(counts.get(key("P3"))).toBe(1);
    expect(counts.get(forest.roots["domain-model"])).toBe(3);
  });

  it("lists in each explorer only the favorites and recents it holds", () => {
    expect(inExplorer(forest, "domain-model", ["D1", "E2", "missing", "E1"])).toEqual(["E2", "E1"]);
    expect(inExplorer(forest, "diagrams", ["D1", "E2"])).toEqual(["D1"]);
    expect(inExplorer(forest, "domain-model", ["E1", "E2", "E3"], 2)).toEqual(["E1", "E2"]);
  });
});
