// The Entities canvas model (src/canvas/model.ts): view contents, card positions, edge attachment,
// writing positions back, layout input and "Add related to depth N".
import { describe, expect, it, vi } from "vitest";
import type { DiagramMember, RelationDoc } from "@/api/types";
import {
  applyPositions,
  applySelectChanges,
  cardPosition,
  expandRelated,
  GRID,
  layoutInput,
  needsLayout,
  NODE_HEIGHT,
  NODE_WIDTH,
  parseView,
  relationEnds,
  relationBoxRows,
  relationBoxSize,
  relationLabelSize,
  selectedEntities,
  storedPosition,
  viewElements,
  viewKey,
} from "@/canvas/model";
import { reapplyDiagram } from "@/state/drafts";
import { elkGraph } from "@/canvas/layout";
import { attributeBoxPlacement, labelOffset } from "@/canvas/RelationEdge";
import type { ModelJson } from "@/api/types";

const rows = [
  { id: "pkg", kind: "package" },
  { id: "other", kind: "package" },
  { id: "customer", kind: "entity", package: "pkg" },
  { id: "invoice", kind: "entity", package: "pkg" },
  { id: "line", kind: "entity", package: "pkg" },
  { id: "audit", kind: "entity", package: "other" },
  { id: "r-ci", kind: "relation", package: "pkg" },
  { id: "r-il", kind: "relation", package: "pkg" },
  { id: "diagram", kind: "diagram", package: "pkg" },
];

type Rel = Pick<RelationDoc, "id" | "ends">;
const relation = (id: string, a: string, b: string): Rel =>
  ({
    id,
    ends: [
      { id: `${id}-a`, entity: a },
      { id: `${id}-b`, entity: b },
    ],
  }) as unknown as Rel;

describe("views", () => {
  it("parses diagram and package views and keys them", () => {
    expect(parseView("diagram")).toEqual({ type: "diagram", id: "diagram" });
    expect(parseView("pkg:pkg")).toEqual({ type: "package", id: "pkg" });
    expect(parseView(null)).toBeNull();
    expect(viewKey(parseView("pkg:pkg"))).toBe("package:pkg");
    expect(viewKey(null)).toBe("none");
  });

  it("shows a diagram's entity and relation members once, in member order, skipping other kinds", () => {
    const members: DiagramMember[] = [
      { element: "invoice" },
      { element: "r-ci" },
      { element: "customer" },
      { element: "invoice" },
      { element: "pkg" },
      { element: "missing" },
    ];
    expect(viewElements(parseView("diagram"), rows, members)).toEqual({ entityIds: ["invoice", "customer"], relationIds: ["r-ci"] });
  });

  it("shows a package's entities and every relation", () => {
    expect(viewElements(parseView("pkg:pkg"), rows, [])).toEqual({ entityIds: ["customer", "invoice", "line"], relationIds: ["r-ci", "r-il"] });
    expect(viewElements(null, rows, [])).toEqual({ entityIds: [], relationIds: [] });
  });
});

describe("positions", () => {
  const diagram = parseView("diagram");
  const pkg = parseView("pkg:pkg");

  it("reads stored positions from members or from the per-browser package store", () => {
    expect(storedPosition(diagram, { element: "a", x: 10, y: 20 }, {}, "a")).toEqual({ x: 10, y: 20 });
    expect(storedPosition(diagram, { element: "a", x: 10 }, {}, "a")).toEqual({ x: 10, y: 0 });
    expect(storedPosition(diagram, { element: "a" }, {}, "a")).toBeUndefined();
    expect(storedPosition(pkg, { element: "a", x: 1, y: 1 }, { a: { x: 5, y: 6 } }, "a")).toEqual({ x: 5, y: 6 });
  });

  it("prefers the live drag position, then the stored one, then a grid slot", () => {
    expect(cardPosition(0, { x: 1, y: 2 }, { x: 3, y: 4 })).toEqual({ x: 1, y: 2 });
    expect(cardPosition(0, undefined, { x: 3, y: 4 })).toEqual({ x: 3, y: 4 });
    expect(cardPosition(GRID.columns + 1, undefined, undefined)).toEqual({ x: GRID.x, y: GRID.y });
  });

  it("lays out a diagram only when no card has a position, a package view when any lacks one", () => {
    const placed = new Map<string, DiagramMember>([
      ["a", { element: "a", x: 0, y: 0 }],
      ["b", { element: "b" }],
    ]);
    expect(needsLayout(diagram, ["a", "b"], placed, {})).toBe(false);
    expect(needsLayout(diagram, ["b"], placed, {})).toBe(true);
    expect(needsLayout(diagram, [], placed, {})).toBe(false);
    // Some placed: only the others are placed (placement.ts), no full layout.
    expect(needsLayout(pkg, ["a", "b"], placed, { a: { x: 0, y: 0 } })).toBe(false);
    expect(needsLayout(pkg, ["a", "b"], placed, {})).toBe(true);
    expect(needsLayout(pkg, ["a"], placed, { a: { x: 0, y: 0 } })).toBe(false);
  });

  it("writes rounded positions into members and counts real changes", () => {
    const doc = { members: [{ element: "a", x: 0, y: 0 }, { element: "b" }, { element: "c", x: 7, y: 8 }] as DiagramMember[] };
    expect(applyPositions(doc, { a: { x: 10.4, y: 19.6 }, b: { x: 1, y: 2 }, c: { x: 7, y: 8 }, zzz: { x: 0, y: 0 } })).toBe(2);
    expect(doc.members).toEqual([
      { element: "a", x: 10, y: 20 },
      { element: "b", x: 1, y: 2 },
      { element: "c", x: 7, y: 8 },
    ]);
  });

  it("re-applies our positions onto the disk version after a 409 (keeps their new members)", () => {
    const base = { kind: "diagram", members: [{ element: "a", x: 0, y: 0 }] } as unknown as ModelJson;
    const disk = { kind: "diagram", members: [{ element: "a", x: 0, y: 0 }, { element: "new" }] } as unknown as ModelJson;
    const ours = {
      kind: "diagram",
      members: [
        { element: "a", x: 50, y: 60, width: 300, height: 120, collapsed: true },
        { element: "mine", x: 1, y: 1 },
      ],
    } as unknown as ModelJson;
    expect((reapplyDiagram(disk, ours, base) as unknown as { members: DiagramMember[] }).members).toEqual([
      { element: "a", x: 50, y: 60, width: 300, height: 120, collapsed: true },
      { element: "new" },
      { element: "mine", x: 1, y: 1 },
    ]);
  });

  it("re-applies only what our draft changed: a state another window moved keeps its place, an expand is re-applied", () => {
    const base = {
      kind: "diagram",
      members: [
        { element: "a", x: 0, y: 0 },
        { element: "b", x: 10, y: 10, width: 200, height: 100, collapsed: true },
        { element: "gone", x: 5, y: 5 },
      ],
      viewport: { x: 0, y: 0, zoom: 1 },
    } as unknown as ModelJson;
    // Another window moved a and removed gone; we moved nothing but b, which we expanded and grew.
    const disk = {
      kind: "diagram",
      members: [
        { element: "a", x: 400, y: 300 },
        { element: "b", x: 10, y: 10, width: 200, height: 100, collapsed: true },
      ],
      viewport: { x: 9, y: 9, zoom: 2 },
    } as unknown as ModelJson;
    const ours = {
      kind: "diagram",
      members: [
        { element: "a", x: 0, y: 0 },
        { element: "b", x: 10, y: 10, width: 260, height: 100 },
        { element: "gone", x: 5, y: 5 },
      ],
      viewport: { x: 0, y: 0, zoom: 1 },
    } as unknown as ModelJson;
    const merged = reapplyDiagram(disk, ours, base) as unknown as { members: DiagramMember[]; viewport: unknown };
    expect(merged.members).toEqual([
      { element: "a", x: 400, y: 300 },
      { element: "b", x: 10, y: 10, width: 260, height: 100 },
    ]);
    // Our viewport did not change: theirs stays.
    expect(merged.viewport).toEqual({ x: 9, y: 9, zoom: 2 });
  });
});

describe("edges", () => {
  const at = new Map([
    ["customer", { x: 0, y: 0 }],
    ["invoice", { x: 400, y: 0 }],
  ]);

  it("leaves the right side of the left card for the left side of the right one", () => {
    expect(relationEnds(relation("r", "customer", "invoice"), at)).toEqual({ source: "customer", target: "invoice", sourceHandle: "r", targetHandle: "l" });
  });

  it("uses the reverse handles when the first end is on the right", () => {
    expect(relationEnds(relation("r", "invoice", "customer"), at)).toEqual({ source: "invoice", target: "customer", sourceHandle: "sl", targetHandle: "tr" });
  });

  it("drops relations with an end off the canvas or fewer than two ends", () => {
    expect(relationEnds(relation("r", "customer", "line"), at)).toBeNull();
    expect(relationEnds({ ends: [] } as unknown as Rel, at)).toBeNull();
  });
});

describe("layout input", () => {
  it("gives every relation edge its label's size with a gap, so the layout makes room for it", () => {
    const input = layoutInput(
      [{ id: "a" }, { id: "b" }],
      [{ id: "r", source: "a", target: "b", data: { relation: { name: "places", attributes: [{ id: "x", name: "quantity", type: "int32" }] } } }],
    );
    const label = relationLabelSize({ name: "places", attributes: [{ id: "x", name: "quantity", type: "int32" }] });
    expect(label).toEqual({ width: Math.ceil(9 * 11 * 0.6) + 16, height: 22 });
    expect(input.edges[0].label).toEqual({ width: label.width + 24, height: label.height + 12 });
    expect(layoutInput([{ id: "a" }], [{ id: "e", source: "a", target: "a" }]).edges[0].label).toBeUndefined();
  });

  it("puts an edge label in the layout graph with its size", () => {
    const graph = elkGraph(
      [
        { id: "a", width: 10, height: 10 },
        { id: "b", width: 10, height: 10 },
      ],
      [{ id: "r", source: "a", target: "b", label: { width: 80, height: 30 } }],
    );
    expect(graph.edges?.[0].labels).toEqual([{ id: "r:label", text: "r", width: 80, height: 30 }]);
    expect(graph.layoutOptions?.["elk.spacing.edgeLabel"]).toBe("12");
  });

  it("moves a label off a short edge so it never covers a card", () => {
    const label = { width: 100, height: 22 };
    expect(labelOffset({ sourceX: 0, sourceY: 0, targetX: 300, targetY: 0 }, label)).toEqual({ dx: 0, dy: 0 });
    expect(labelOffset({ sourceX: 0, sourceY: 0, targetX: 96, targetY: 4 }, label)).toEqual({ dx: 0, dy: -17 });
    expect(labelOffset({ sourceX: 0, sourceY: 0, targetX: 2, targetY: 30 }, label)).toEqual({ dx: -56, dy: 0 });
  });

  describe("relation attributes on the diagram (the attribute box)", () => {
    const settles = {
      name: "settles",
      attributes: [
        { id: "a1", name: "allocated", type: { ref: "money" }, required: true },
        { id: "a2", name: "note", type: "string" as const },
      ],
    } as Pick<RelationDoc, "name" | "attributes">;
    const typeLabel = (a: { type: unknown }) => (typeof a.type === "string" ? a.type : "Money");
    const all = { mode: "all" as const, typeLabel };

    it("sizes the box from its widest row, one 24 px row per attribute under a 24 px header", () => {
      const size = relationBoxSize(settles, all);
      // "allocated" (9 chars, 12 px) and "Money" (5 chars, 11 px mono): 16 + 16 + 4 + 64.8 + 4 + 33 = 137.8, plus the border.
      expect(size).toEqual({ width: 140, height: 2 + 24 + 4 + 2 * 24 });
      // Names only drops the type column: narrower, never under the minimum width.
      expect(relationBoxSize(settles, { mode: "names", typeLabel })).toEqual({ width: 120, height: 78 });
      // A long type makes the box wider, up to a card's width.
      expect(relationBoxSize(settles, { mode: "all", typeLabel: () => "x".repeat(80) })?.width).toBe(NODE_WIDTH);
    });

    it("draws no box for Keys only (a relation has no key of its own) or a relation without attributes", () => {
      expect(relationBoxRows(settles, "keys")).toEqual([]);
      expect(relationBoxRows(settles, "names").map((a) => a.name)).toEqual(["allocated", "note"]);
      expect(relationBoxSize(settles, { mode: "keys", typeLabel })).toBeNull();
      expect(relationBoxSize({ name: "places" }, all)).toBeNull();
      // No box: the pill keeps its attribute count badge.
      expect(relationLabelSize(settles, { mode: "keys", typeLabel })).toEqual(relationLabelSize(settles));
    });

    it("gives the layout the pill and the box under it, without the badge the box replaces", () => {
      const pill = relationLabelSize({ name: "settles" });
      expect(relationLabelSize(settles).width).toBeGreaterThan(pill.width);
      // As wide as the box on either side of the line (it moves beside an upright stretch), the box under the pill.
      expect(relationLabelSize(settles, all)).toEqual({ width: 2 * 140 + 12, height: pill.height + 12 + 78 });
      const input = layoutInput([{ id: "a" }, { id: "b" }], [{ id: "r", source: "a", target: "b", data: { relation: settles, attributeBox: all } }]);
      expect(input.edges[0].label).toEqual({ width: 292 + 24, height: 22 + 12 + 78 + 12 });
      // Off: the pill alone, as before.
      const off = layoutInput([{ id: "a" }, { id: "b" }], [{ id: "r", source: "a", target: "b", data: { relation: settles, attributeBox: null } }]);
      expect(off.edges[0].label).toEqual({ width: relationLabelSize(settles).width + 24, height: 22 + 12 });
    });

    it("hangs the box off the pill, clear of the line, joined by a short connector", () => {
      const pill = { width: 60, height: 22 };
      const box = { width: 140, height: 78 };
      // The line level through the label: the pill on the line, the box centered below it.
      expect(attributeBoxPlacement({ sourceX: 0, sourceY: 0, targetX: 400, targetY: 20 }, pill, box)).toEqual({
        pill: { dx: 0, dy: 0 },
        box: { dx: 0, dy: 11 + 12 + 39 },
        connector: { x1: 0, y1: 11, x2: 0, y2: 23 },
      });
      // A short horizontal run: the pill moved above the line (labelOffset), the box above the pill.
      expect(attributeBoxPlacement({ sourceX: 0, sourceY: 0, targetX: 50, targetY: 0 }, pill, box)).toEqual({
        pill: { dx: 0, dy: -17 },
        box: { dx: 0, dy: -17 - 11 - 12 - 39 },
        connector: { x1: 0, y1: -28, x2: 0, y2: -40 },
      });
      // A short vertical run: the pill moved left of the line, the line too short to reach the box below.
      expect(attributeBoxPlacement({ sourceX: 0, sourceY: 0, targetX: 0, targetY: 30 }, pill, box)).toEqual({
        pill: { dx: -36, dy: 0 },
        box: { dx: -36, dy: 11 + 12 + 39 },
        connector: { x1: -36, y1: 11, x2: -36, y2: 23 },
      });
      // The label on an upright stretch, the target lower and to the right: the line turns right at the bottom, so the
      // box hangs below and left of the stretch, the connector from the pill's left half.
      expect(attributeBoxPlacement({ sourceX: 0, sourceY: 0, targetX: 200, targetY: 100 }, pill, box)).toEqual({
        pill: { dx: 0, dy: 0 },
        box: { dx: -(6 + 70), dy: 11 + 12 + 39 },
        connector: { x1: -15, y1: 11, x2: -15, y2: 23 },
      });
      // The target higher: the lower end turns left toward the source, so the box goes right.
      expect(attributeBoxPlacement({ sourceX: 0, sourceY: 100, targetX: 200, targetY: 0 }, pill, box).box).toEqual({ dx: 6 + 70, dy: 62 });
      // A long vertical run (the target below, a little right): the same rule.
      expect(attributeBoxPlacement({ sourceX: 0, sourceY: 0, targetX: 10, targetY: 300 }, pill, box).box).toEqual({ dx: -76, dy: 62 });
    });
  });

  it("uses measured sizes and falls back to the default card size", () => {
    const input = layoutInput(
      [{ id: "a", measured: { width: 260, height: 300 } }, { id: "b" }, { id: "c", measured: { width: 0, height: 0 } }],
      [{ id: "r", source: "a", target: "b", data: {} } as { id: string; source: string; target: string }],
    );
    expect(input.nodes).toEqual([
      { id: "a", width: 260, height: 300 },
      { id: "b", width: NODE_WIDTH, height: NODE_HEIGHT },
      { id: "c", width: NODE_WIDTH, height: NODE_HEIGHT },
    ]);
    expect(input.edges).toEqual([{ id: "r", source: "a", target: "b" }]);
  });
});

describe("add related", () => {
  // customer -r1- invoice -r2- line -r3- product; invoice -r4- payment
  const graph: Record<string, Rel[]> = {
    customer: [relation("r1", "customer", "invoice")],
    invoice: [relation("r1", "customer", "invoice"), relation("r2", "invoice", "line"), relation("r4", "payment", "invoice")],
    line: [relation("r2", "invoice", "line"), relation("r3", "line", "product")],
    payment: [relation("r4", "payment", "invoice")],
    product: [relation("r3", "line", "product")],
  };
  const relationsOf = vi.fn(async (id: string) => graph[id] ?? []);
  const run = (depth: number, present: string[] = ["customer"], start = ["customer"]) =>
    expandRelated({ start, depth, present: new Set(present), origin: { x: 100, y: 50 }, relationsOf });

  it("adds the relations and the entities one level out", async () => {
    expect(await run(1)).toEqual([{ element: "r1" }, { element: "invoice", x: 420, y: 50 }]);
  });

  it("walks further per level, placing each level in its own column", async () => {
    const added = await run(2);
    expect(added.map((m) => m.element)).toEqual(["r1", "invoice", "r2", "line", "r4", "payment"]);
    expect(added.find((m) => m.element === "line")).toMatchObject({ x: 740, y: 50 });
    expect(added.find((m) => m.element === "payment")).toMatchObject({ x: 740, y: 230 });
  });

  it("walks through entities already on the diagram without adding them again", async () => {
    const added = await run(2, ["customer", "invoice", "r1"]);
    expect(added.map((m) => m.element)).toEqual(["r2", "line", "r4", "payment"]);
  });

  it("adds nothing when everything is already present", async () => {
    expect(await run(1, ["customer", "invoice", "r1"])).toEqual([]);
  });

  it("stops early when the frontier is empty", async () => {
    relationsOf.mockClear();
    await run(5, ["product"], ["product"]);
    // product -> line -> invoice -> customer and payment -> nothing new
    expect(relationsOf.mock.calls.map((c) => c[0])).toEqual(["product", "line", "invoice", "customer", "payment"]);
  });

  it("keeps only entities from a mixed selection", () => {
    const kinds = new Map(rows.map((r) => [r.id, r.kind]));
    expect(selectedEntities(["r-ci", "invoice", "pkg", "customer"], (id) => kinds.get(id))).toEqual(["invoice", "customer"]);
  });
});

describe("selection", () => {
  it("applies React Flow select changes to the store selection", () => {
    expect(
      applySelectChanges(
        ["a"],
        [
          { type: "select", id: "b", selected: true },
          { type: "select", id: "a", selected: false },
        ],
      ),
    ).toEqual(["b"]);
    expect(applySelectChanges(["a"], [{ type: "select", id: "b", selected: true }])).toEqual(["a", "b"]);
  });

  it("returns null when nothing changes (no store write, no render loop)", () => {
    expect(
      applySelectChanges(
        ["a"],
        [
          { type: "select", id: "a", selected: true },
          { type: "select", id: "z", selected: false },
        ],
      ),
    ).toBeNull();
    expect(
      applySelectChanges(
        ["a"],
        [
          { type: "position", id: "a" },
          { type: "dimensions", id: "a" },
        ],
      ),
    ).toBeNull();
  });

  it("keeps selected elements that are not on the canvas", () => {
    expect(applySelectChanges(["attr-1", "a"], [{ type: "select", id: "a", selected: false }])).toEqual(["attr-1"]);
  });
});
