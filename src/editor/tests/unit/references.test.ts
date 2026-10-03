// The References tab's data, breadcrumbs, the selection history and the canvas membership plan
// (explorer-redesign.md 3.3 and 3.5, steps 10 and 11).
import { afterEach, describe, expect, it } from "vitest";
import type { ElementSummary, ReferenceInfo } from "@/api/types";
import { groupReferences, indexReferences, referenceCount, referenceLabel, sideReferences } from "@/references/data";
import { breadcrumbPath } from "@/app/Breadcrumbs";
import { emptyHistory, HISTORY_LIMIT, prune, travel, visit } from "@/state/history";
import { createEditorStore } from "@/state/store";
import { treeKeyAction } from "@/explorer/useTreeKeyboard";
import { planMembers, relatedWithin, relationLookup } from "@/canvas/model";

const row = (id: string, kind: ElementSummary["kind"], name: string, extra: Partial<ElementSummary> = {}): ElementSummary => ({
  id,
  kind,
  name,
  package: null,
  tags: [],
  category: null,
  stereotypes: [],
  hash: "h",
  path: `${id}.json`,
  ...extra,
});

const rows: ElementSummary[] = [
  row("SALES", "package", "Sales"),
  row("ORDERS", "package", "Orders", { package: "SALES" }),
  row("BILLING", "package", "Billing"),
  row("ORDER", "entity", "Order", { package: "ORDERS" }),
  row("LINE", "entity", "OrderLine", { package: "ORDERS" }),
  row("SPECIAL", "entity", "SpecialOrder", { package: "ORDERS", base: "ORDER" }),
  row("INVOICE", "entity", "Invoice", { package: "BILLING" }),
  row("HAS", "relation", "has", {
    package: "ORDERS",
    ends: [
      { entity: "ORDER", role: "order" },
      { entity: "LINE", role: "lines" },
    ],
  }),
  row("BILLS", "relation", "bills", {
    package: "BILLING",
    ends: [
      { entity: "INVOICE", role: "invoice" },
      { entity: "ORDER", role: "order" },
    ],
  }),
  row("DIAG", "diagram", "Billing overview", { package: "BILLING" }),
  row("MAIN", "database", "main"),
  row("MAP", "mapping", "Order mapping", { entity: "ORDER", database: "MAIN" }),
];
const byId = new Map(rows.map((r) => [r.id, r]));

describe("where used (3.3)", () => {
  it("answers from the index's related fields, in the server's shape", () => {
    const refs = indexReferences("ORDER", rows);
    const keys = refs.map((r) => `${r.fromElementId}${r.jsonPointer}`).sort();
    expect(keys).toEqual(["BILLS/ends/1/entity", "HAS/ends/0/entity", "MAP/entity", "SPECIAL/base"]);
    expect(
      indexReferences("ORDERS", rows)
        .map((r) => r.fromElementId)
        .sort(),
    ).toEqual(["HAS", "LINE", "ORDER", "SPECIAL"]);
    expect(indexReferences("SALES", rows)).toEqual([{ fromElementId: "ORDERS", fromId: "ORDERS", jsonPointer: "/parent", field: "parent", toId: "SALES" }]);
  });

  it("labels a sub-element, a diagram membership and a plain field", () => {
    const ref = (fromElementId: string, jsonPointer: string, field: string): ReferenceInfo => ({
      fromElementId,
      fromId: fromElementId,
      jsonPointer,
      field,
      toId: "X",
    });
    expect(
      referenceLabel(ref("LINE", "/attributes/2/type", "type"), byId.get("LINE"), (_, c, i) => (c === "attributes" && i === 2 ? "amount" : undefined)),
    ).toBe("OrderLine · attribute amount → type");
    expect(referenceLabel(ref("LINE", "/attributes/0/type", "type"), byId.get("LINE"))).toBe("OrderLine · attribute 1 → type");
    expect(referenceLabel(ref("DIAG", "/members/3/element", "element"), byId.get("DIAG"))).toBe("on diagram Billing overview");
    expect(referenceLabel(ref("SPECIAL", "/base", "base"), byId.get("SPECIAL"))).toBe("SpecialOrder → base");
    expect(referenceLabel(ref("GONE", "/base", "base"), undefined)).toBe("GONE → base");
  });

  it("groups by kind in folder order, then by domain, Not in a domain last, and drops duplicates", () => {
    const refs = indexReferences("ORDER", rows);
    const list = groupReferences([...refs, refs[0]], byId);
    expect(referenceCount(list)).toBe(4);
    const shape = list.map((r) => (r.type === "reference" ? `  ${r.label}` : r.type === "domain" ? ` ${r.label} (${r.count})` : `${r.label} (${r.count})`));
    expect(shape).toEqual([
      "Entities (1)",
      " Orders (1)",
      "  SpecialOrder → base",
      "Relationships (2)",
      " Billing (1)",
      "  bills · end 2 → entity",
      " Orders (1)",
      "  has · end 1 → entity",
      "Customised mappings (1)",
      " Not in a domain (1)",
      "  Order mapping → entity",
    ]);
    const first = list.find((r) => r.type === "reference");
    expect(first?.type === "reference" && first.elementId).toBe("SPECIAL");
  });
});

describe("breadcrumbs (3.3)", () => {
  it("walks the explorer, the domain chain and the kind folder, with siblings", () => {
    const path = breadcrumbPath("LINE", rows, byId);
    expect(path.map((c) => c.label)).toEqual(["Domain model", "Sales", "Orders", "Entities", "OrderLine"]);
    expect(path[1].siblings.map((s) => s.label)).toEqual(["Billing", "Sales"]);
    expect(path[3].siblings.map((s) => s.label)).toEqual(["Order", "OrderLine", "SpecialOrder"]);
    expect(path.at(-1)?.id).toBe("LINE");
    expect(breadcrumbPath("ORDERS", rows, byId).map((c) => c.label)).toEqual(["Domain model", "Sales", "Orders"]);
    expect(breadcrumbPath("DIAG", rows, byId).map((c) => c.label)).toEqual(["Diagrams", "Billing overview"]);
    expect(breadcrumbPath("nope", rows, byId)).toEqual([]);
  });

  it("survives a domain cycle", () => {
    const loop = [row("A", "package", "A", { package: "B" }), row("B", "package", "B", { package: "A" }), row("E", "entity", "E", { package: "A" })];
    const path = breadcrumbPath("E", loop, new Map(loop.map((r) => [r.id, r])));
    expect(path.map((c) => c.label)).toEqual(["Domain model", "B", "A", "Entities", "E"]);
  });
});

describe("selection history (3.3)", () => {
  afterEach(() => localStorage.clear());

  it("records visits, goes back and forward, and forgets the forward list on a new visit", () => {
    let h = visit(visit(visit(emptyHistory, ["a"]), ["b"]), ["c"]);
    expect(h).toEqual({ back: [["a"], ["b"]], current: ["c"], forward: [] });
    expect(visit(h, ["c"])).toBe(h);
    expect(visit(h, [])).toBe(h);
    h = travel(h, "back")!;
    h = travel(h, "back")!;
    expect(h.current).toEqual(["a"]);
    expect(travel(h, "back")).toBeNull();
    h = travel(h, "forward")!;
    expect(h).toEqual({ back: [["a"]], current: ["b"], forward: [["c"]] });
    h = visit(h, ["d"]);
    expect(h).toEqual({ back: [["a"], ["b"]], current: ["d"], forward: [] });
    expect(travel(h, "forward")).toBeNull();
  });

  it("keeps a bounded list and prunes deleted elements", () => {
    let h = emptyHistory;
    for (let i = 0; i < HISTORY_LIMIT + 10; i++) h = visit(h, [`e${i}`]);
    expect(h.back).toHaveLength(HISTORY_LIMIT);
    const p = prune(visit(visit(emptyHistory, ["a", "x"]), ["x"]), (id) => id !== "x");
    expect(p).toEqual({ back: [["a"]], current: null, forward: [] });
  });

  it("leaves Alt+arrows to the shell's history", () => {
    const rows = [{ key: "a", depth: 0, label: "a", expandable: true, expanded: true }];
    expect(treeKeyAction(rows, 0, { key: "ArrowLeft", alt: true })).toBeNull();
    expect(treeKeyAction(rows, 0, { key: "ArrowLeft" })).not.toBeNull();
  });

  it("drives the store's selection without recording the travel as a visit", () => {
    const store = createEditorStore();
    const s = () => store.getState();
    s().select(["a"]);
    s().select(["b"]);
    s().select([]);
    expect(s().history.current).toEqual(["b"]);
    expect(s().travel("back")).toEqual(["a"]);
    expect(s().selection).toEqual(["a"]);
    expect(s().travel("forward")).toEqual(["b"]);
    expect(s().travel("forward")).toBeNull();
    s().showReferences("a");
    expect(s().bottomTab).toBe("references");
    expect(s().references).toBe("a");
  });
});

describe("canvas in step (3.5)", () => {
  const lookup = relationLookup(rows);

  it("walks relations from the index to a depth", () => {
    expect(relatedWithin(["LINE"], 1, lookup).sort()).toEqual(["LINE", "ORDER"]);
    expect(relatedWithin(["LINE"], 2, lookup).sort()).toEqual(["INVOICE", "LINE", "ORDER"]);
    expect(relatedWithin(["LINE"], 0, lookup)).toEqual(["LINE"]);
  });

  it("plans new members at the drop point with the relations whose ends are then all on the diagram", () => {
    const added = planMembers({ members: [{ element: "ORDER", x: 100, y: 50 }], entities: ["LINE", "ORDER"], lookup, at: { x: 400, y: 300 } });
    expect(added).toEqual([{ element: "LINE", x: 400, y: 300 }, { element: "HAS" }]);
    const right = planMembers({ members: [{ element: "ORDER", x: 100, y: 50 }], entities: ["INVOICE"], lookup });
    expect(right[0]).toMatchObject({ element: "INVOICE", y: 50 });
    expect(right[0].x).toBeGreaterThan(100);
    expect(right.map((m) => m.element)).toEqual(["INVOICE", "BILLS"]);
    expect(planMembers({ members: [{ element: "ORDER" }, { element: "LINE" }, { element: "HAS" }], entities: ["ORDER"], lookup })).toEqual([]);
  });
});

describe("Used on the database side", () => {
  it("lists only database-side referrers for a database-side element, everything for the others", () => {
    const kinds: Record<string, string> = { T2: "table", V: "view", Q: "query", E: "entity", M: "mapping", R: "routine" };
    const refs = ["T2", "V", "Q", "E", "M", "R"].map((id) => ({ fromElementId: id }));
    const kindOf = (id: string) => kinds[id];
    expect(sideReferences("table", refs, kindOf).map((r) => r.fromElementId)).toEqual(["T2", "V", "Q", "R"]);
    expect(sideReferences("query", refs, kindOf).map((r) => r.fromElementId)).toEqual(["T2", "V", "Q", "R"]);
    expect(sideReferences("entity", refs, kindOf)).toHaveLength(6);
    expect(sideReferences(undefined, refs, kindOf)).toHaveLength(6);
  });
});
