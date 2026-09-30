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
  relationLabelSize,
  selectedEntities,
  storedPosition,
  viewElements,
  viewKey,
} from "@/canvas/model";
import { reapplyDiagram } from "@/state/drafts";
import { elkGraph } from "@/canvas/layout";
import { labelOffset } from "@/canvas/RelationEdge";
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
