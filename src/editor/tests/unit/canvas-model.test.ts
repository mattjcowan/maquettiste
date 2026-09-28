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
  selectedEntities,
  storedPosition,
  viewElements,
  viewKey,
} from "@/canvas/model";
import { reapplyDiagram } from "@/state/drafts";
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
    expect(needsLayout(pkg, ["a", "b"], placed, { a: { x: 0, y: 0 } })).toBe(true);
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
    const disk = { kind: "diagram", members: [{ element: "a", x: 0, y: 0 }, { element: "new" }] } as unknown as ModelJson;
    const ours = {
      kind: "diagram",
      members: [
        { element: "a", x: 50, y: 60, collapsed: true },
        { element: "mine", x: 1, y: 1 },
      ],
    } as unknown as ModelJson;
    expect((reapplyDiagram(disk, ours) as unknown as { members: DiagramMember[] }).members).toEqual([
      { element: "a", x: 50, y: 60, collapsed: true },
      { element: "new" },
      { element: "mine", x: 1, y: 1 },
    ]);
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
