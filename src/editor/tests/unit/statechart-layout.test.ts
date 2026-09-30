// The statechart's nested layout (phase-3-design.md 6.3): containers sized bottom-up to their children plus padding
// (over a fake engine and over real ELK), the hierarchical ELK graph, parallel regions side by side, the incremental
// placement of a new state that moves nothing else, and the boxes the canvas draws.
import { describe, expect, it, vi } from "vitest";
import ELK from "elkjs/lib/elk.bundled.js";
import type { ElkNode } from "elkjs/lib/elk-api";
import {
  bottomUpEngine,
  elkHierarchyEngine,
  elkTreeGraph,
  fitTree,
  HIERARCHY_OPTIONS,
  withHierarchyFallback,
  type Box,
  type Boxes,
  type HierarchyEngine,
  type LayoutEngine,
  type LayoutTree,
} from "@/canvas/layout";
import { buildChart } from "@/canvas/statechart/chartModel";
import {
  chartTree,
  CONTAINER_PAD,
  grownSizes,
  INIT,
  layoutChart,
  leafSize,
  liftedEdges,
  placeMissing,
  regionSeparators,
  resolveBoxes,
} from "@/canvas/statechart/chartLayout";
import type { Placement } from "@/canvas/statechart/diagram";
import { addState, type ProcessDoc } from "@/model/process";
import { purchase } from "./statechart-fixture";

const pad = { top: 32, left: 12, bottom: 12, right: 12 };

/** A flat engine that puts nodes in one row, 10 apart, and records the edges it was given. */
function rowEngine(calls: { ids: string[]; edges: string[] }[] = []): LayoutEngine {
  return {
    name: "row",
    async layout(nodes, edges) {
      calls.push({ ids: nodes.map((n) => n.id), edges: edges.map((e) => `${e.source}>${e.target}`) });
      let x = 0;
      return new Map(
        nodes.map((n) => {
          const p = { x, y: 0 };
          x += n.width + 10;
          return [n.id, p];
        }),
      );
    },
  };
}

const inside = (child: Box, parent: { width: number; height: number }) =>
  child.x >= 0 && child.y >= 0 && child.x + child.width <= parent.width && child.y + child.height <= parent.height;

function overlapping(boxes: Box[]): boolean {
  for (let i = 0; i < boxes.length; i++)
    for (let j = i + 1; j < boxes.length; j++) {
      const a = boxes[i];
      const b = boxes[j];
      if (a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height) return true;
    }
  return false;
}

describe("nested layout", () => {
  const tree: LayoutTree = {
    children: [
      { id: "a", width: 100, height: 24 },
      {
        id: "outer",
        width: 60,
        height: 44,
        padding: pad,
        children: [
          {
            id: "inner",
            width: 60,
            height: 44,
            padding: pad,
            children: [
              { id: "x", width: 80, height: 24 },
              { id: "y", width: 50, height: 24 },
            ],
          },
          { id: "z", width: 40, height: 24 },
        ],
      },
    ],
    edges: [
      { id: "e1", source: "a", target: "x" },
      { id: "e2", source: "x", target: "y" },
      { id: "e3", source: "y", target: "z" },
      { id: "e4", source: "outer", target: "x" },
      { id: "self", source: "a", target: "a" },
    ],
  };

  it("sizes each container bottom-up to its children plus padding, then lays out its parent with that size", async () => {
    const calls: { ids: string[]; edges: string[] }[] = [];
    const boxes = await bottomUpEngine(rowEngine(calls)).layoutTree(tree);
    // The innermost container first, then its parent, then the top: edges lifted to the children that hold their ends.
    expect(calls).toEqual([
      { ids: ["x", "y"], edges: ["x>y"] },
      { ids: ["inner", "z"], edges: ["inner>z"] },
      { ids: ["a", "outer"], edges: ["a>outer"] },
    ]);
    expect(boxes.get("x")).toEqual({ x: 12, y: 32, width: 80, height: 24 });
    expect(boxes.get("y")).toEqual({ x: 102, y: 32, width: 50, height: 24 });
    // inner: 12 + 80 + 10 + 50 + 12 wide, 32 + 24 + 12 high.
    expect(boxes.get("inner")).toEqual({ x: 12, y: 32, width: 164, height: 68 });
    expect(boxes.get("z")).toEqual({ x: 186, y: 32, width: 40, height: 24 });
    expect(boxes.get("outer")).toEqual({ x: 110, y: 0, width: 238, height: 112 });
  });

  it("fits containers to what they hold and keeps a larger minimum", () => {
    const boxes: Boxes = new Map([
      ["a", { x: 0, y: 0, width: 1, height: 1 }],
      ["outer", { x: 150, y: 0, width: 10, height: 10 }],
      ["inner", { x: 12, y: 32, width: 10, height: 10 }],
      ["x", { x: 12, y: 32, width: 0, height: 0 }],
      ["y", { x: 112, y: 40, width: 0, height: 0 }],
      ["z", { x: 12, y: 200, width: 0, height: 0 }],
    ]);
    fitTree(tree, boxes);
    expect(boxes.get("x")).toMatchObject({ width: 80, height: 24 });
    expect(boxes.get("inner")).toEqual({ x: 12, y: 32, width: 112 + 50 + 12, height: 40 + 24 + 12 });
    expect(boxes.get("outer")).toEqual({ x: 150, y: 0, width: Math.max(12 + 174, 12 + 40) + 12, height: 200 + 24 + 12 });
    const small = fitTree(
      { children: [{ id: "c", width: 300, height: 90, padding: pad, children: [{ id: "k", width: 10, height: 10 }] }], edges: [] },
      new Map([["k", { x: 12, y: 32, width: 10, height: 10 }]]),
    );
    expect(small.get("c")).toMatchObject({ width: 300, height: 90 });
  });

  it("gives ELK one hierarchical graph: containers with padding, each edge in the innermost container holding both ends", () => {
    const graph = elkTreeGraph(tree);
    expect(graph.layoutOptions).toMatchObject(HIERARCHY_OPTIONS);
    expect(HIERARCHY_OPTIONS).toMatchObject({
      "elk.hierarchyHandling": "INCLUDE_CHILDREN",
      "elk.direction": "RIGHT",
      "elk.spacing.nodeNode": "32",
      "elk.layered.spacing.nodeNodeBetweenLayers": "64",
      "elk.edgeRouting": "ORTHOGONAL",
    });
    const outer = graph.children![1];
    expect(outer.width).toBeUndefined();
    expect(outer.layoutOptions?.["elk.padding"]).toBe("[top=32,left=12,bottom=12,right=12]");
    // e4 enters its own source's container and the self-loop goes nowhere: ELK cannot route them.
    expect(graph.edges!.map((e) => e.id)).toEqual(["e1"]);
    expect(outer.edges!.map((e) => e.id)).toEqual(["e3"]);
    expect(outer.children![0].edges!.map((e) => e.id)).toEqual(["e2"]);
    expect(outer.layoutOptions).toMatchObject({ "elk.spacing.nodeNode": "32", "elk.layered.spacing.nodeNodeBetweenLayers": "64" });
    expect(outer.layoutOptions?.["elk.layered.considerModelOrder.strategy"]).toBeUndefined();
    const labelled = elkTreeGraph({
      children: [
        { id: "p", width: 10, height: 10 },
        { id: "q", width: 10, height: 10 },
      ],
      edges: [{ id: "pq", source: "p", target: "q", label: { width: 120, height: 18 } }],
    });
    expect(labelled.edges![0].labels).toEqual([{ id: "pq:label", text: "pq", width: 120, height: 18 }]);
  });

  it("reads ELK's boxes relative to their containers", async () => {
    const fake = {
      layout: vi.fn(async (g: ElkNode) => ({
        ...g,
        children: [
          { id: "a", x: 0.4, y: 10, width: 100, height: 24 },
          { id: "outer", x: 164, y: 0, width: 400, height: 200, children: [{ id: "z", x: 12.6, y: 32, width: 40, height: 24 }] },
        ],
      })),
    };
    const boxes = await elkHierarchyEngine(() => Promise.resolve(fake)).layoutTree(tree);
    expect(fake.layout).toHaveBeenCalledOnce();
    expect(boxes.get("a")).toEqual({ x: 0, y: 10, width: 100, height: 24 });
    expect(boxes.get("z")).toEqual({ x: 13, y: 32, width: 40, height: 24 });
  });

  it("falls back to the bottom-up engine once, when ELK cannot start", async () => {
    const report = vi.fn();
    const engine = withHierarchyFallback(
      elkHierarchyEngine(() => Promise.reject(new Error("no Worker"))),
      bottomUpEngine(rowEngine()),
      report,
    );
    expect((await engine.layoutTree(tree)).get("inner")!.width).toBe(164);
    await engine.layoutTree(tree);
    expect(report).toHaveBeenCalledOnce();
    expect(engine.name).toBe("row (bottom-up)");
  });
});

describe("the chart's layout", () => {
  const chart = buildChart(purchase);
  const none = new Set<string>();
  const elk: HierarchyEngine = elkHierarchyEngine(() => Promise.resolve(new ELK()));

  it("builds the tree with initial dots and a chain between regions; a collapsed container is a leaf", () => {
    const tree = chartTree(chart, none, null);
    expect(tree.children.map((c) => c.id)).toEqual([INIT, "draft", "review", "approval", "hist", "done"]);
    const review = tree.children[2];
    expect(review.children!.map((c) => c.id)).toEqual(["budget", "compliance"]);
    expect(review.children![0].children!.map((c) => c.id)).toEqual([`${INIT}budget`, "checking", "ok"]);
    const ids = tree.edges.map((e) => e.id);
    expect(ids).toEqual(expect.arrayContaining([INIT, `${INIT}budget`, `${INIT}compliance`, "chain:compliance", "t1", "t6#1"]));
    expect(tree.edges.find((e) => e.id === `${INIT}compliance`)!.target).toBe("cleared");
    const folded = chartTree(chart, new Set(["review"]), null);
    expect(folded.children[2].children).toBeUndefined();
    expect(folded.edges.find((e) => e.id === "t6#1")).toMatchObject({ source: "review", target: "approval" });
    expect(folded.edges.find((e) => e.id === "t2")).toBeUndefined();
  });

  it("lays the whole chart out with real ELK: containers hold their children, regions side by side, no overlaps", async () => {
    const { placements, size } = await layoutChart(chart, none, null, elk);
    expect(size).toBeNull();
    expect([...placements.keys()].sort()).toEqual([...chart.order].sort());
    const boxes = resolveBoxes(chart, placements, none);
    for (const id of chart.order) {
      const s = chart.states.get(id)!;
      const kids = s.children.map((c) => boxes.get(c)!);
      for (const k of kids) expect(inside(k, boxes.get(id)!)).toBe(true);
      expect(overlapping(kids)).toBe(false);
    }
    expect(overlapping(chart.roots.map((r) => boxes.get(r)!))).toBe(false);
    // The saved container sizes are what the canvas draws.
    expect(placements.get("review")).toMatchObject({ width: boxes.get("review")!.width, height: boxes.get("review")!.height });
    const [budget, compliance] = [boxes.get("budget")!, boxes.get("compliance")!];
    expect(budget.y).toBe(CONTAINER_PAD.top);
    expect(compliance.y).toBe(CONTAINER_PAD.top);
    expect(compliance.x).toBe(budget.x + budget.width + 16);
    expect(budget.height).toBe(compliance.height);
    expect(regionSeparators(chart, "review", boxes)).toEqual([budget.x + budget.width + 8]);
    // Left to right: Drafting before Review before Approval.
    expect(boxes.get("draft")!.x).toBeLessThan(boxes.get("review")!.x);
    expect(boxes.get("review")!.x).toBeLessThan(boxes.get("approval")!.x);
    for (const p of placements.values()) expect(Number.isInteger(p.x) && Number.isInteger(p.y)).toBe(true);
  });

  it("lays out one container: its children and its size, nothing outside it", async () => {
    const { placements, size } = await layoutChart(chart, none, "budget", elk);
    expect([...placements.keys()].sort()).toEqual(["checking", "ok"]);
    expect(size!.width).toBeGreaterThan(leafSize(chart.states.get("checking")!).width);
  });

  it("lays out 400 nested states with lifted edges in the fallback", async () => {
    const big: ProcessDoc = { kind: "process", id: "B", name: "Big", states: [], transitions: [] };
    for (let r = 0; r < 10; r++) {
      const root = addState(big, null);
      for (let c = 0; c < 4; c++) {
        const mid = addState(big, root);
        for (let k = 0; k < 8; k++) addState(big, mid);
      }
    }
    const ids = [...buildChart(big).order];
    for (let i = 1; i < ids.length; i += 3) big.transitions!.push({ id: `t${i}`, source: ids[i - 1], targets: [ids[i]] });
    const bigChart = buildChart(big);
    expect(bigChart.order.length).toBe(370);
    const { placements } = await layoutChart(bigChart, none, null, bottomUpEngine(rowEngine()));
    expect(placements.size).toBe(370);
    const boxes = resolveBoxes(bigChart, placements, none);
    for (const id of bigChart.order) expect(overlapping(bigChart.states.get(id)!.children.map((c) => boxes.get(c)!))).toBe(false);
  });
});

describe("placing new states", () => {
  const chart = buildChart(purchase);
  const saved = new Map<string, Placement>([
    ["draft", { x: 0, y: 0 }],
    ["review", { x: 200, y: 0, width: 400, height: 120 }],
    ["budget", { x: 12, y: 32, width: 180, height: 80 }],
    ["checking", { x: 12, y: 32 }],
    ["ok", { x: 120, y: 32 }],
    ["compliance", { x: 208, y: 32, width: 180, height: 80 }],
    ["pending", { x: 12, y: 32 }],
    ["cleared", { x: 110, y: 32 }],
    ["approval", { x: 700, y: 0 }],
    ["hist", { x: 700, y: 100 }],
    ["done", { x: 900, y: 0 }],
  ]);

  it("places nothing when every state has a place", () => {
    expect(placeMissing(chart, saved).size).toBe(0);
  });

  it("places a new state inside its container only, grows the containers, and moves nothing", () => {
    const doc = structuredClone(purchase);
    const id = addState(doc, "budget");
    const next = buildChart(doc);
    const placed = placeMissing(next, saved);
    const p = placed.get(id)!;
    // Under the drawing of Budget (rows along the bottom), inside its padding.
    expect(p.x).toBeGreaterThanOrEqual(12);
    expect(p.y).toBeGreaterThan(32);
    for (const [k, v] of placed) if (k !== id) expect([v.x, v.y]).toEqual([saved.get(k)!.x, saved.get(k)!.y]);
    expect(placed.get("budget")!.height).toBeGreaterThan(80);
    expect([...placed.keys()].sort()).toEqual(["budget", id, "review"].sort());
    const boxes = resolveBoxes(next, new Map([...saved, ...placed]), new Set());
    const siblings = next.states.get("budget")!.children.map((c) => boxes.get(c)!);
    expect(overlapping(siblings)).toBe(false);
  });

  it("places a new container's children first, then the container beside its siblings", () => {
    const doc = structuredClone(purchase);
    const parent = addState(doc, null);
    const a = addState(doc, parent);
    const b = addState(doc, parent);
    doc.transitions!.push({ id: "tn", source: "done", targets: [a] });
    const next = buildChart(doc);
    const placed = placeMissing(next, saved);
    // Inside the padding, with room for the initial dot.
    expect(placed.get(a)).toEqual({ x: 12 + 36, y: 32 });
    expect(placed.get(b)!.y).toBe(32);
    const box = placed.get(parent)!;
    expect(box.width).toBeGreaterThanOrEqual(12 + 36 + leafSize(next.states.get(a)!).width * 2 + 64 + 12);
    // Beside Ordered, which it has a transition with (lifted to the root).
    expect(liftedEdges(next, null)).toContainEqual({ id: "tn", source: "done", target: parent });
    expect(box.x).toBe(900 + leafSize(next.states.get("done")!).width + 64);
    expect(box.y).toBe(0);
  });
});

describe("the boxes drawn", () => {
  const chart = buildChart(purchase);

  it("draws a container at least as large as its children and its saved size, and leaves out a collapsed one's", () => {
    const placements = new Map<string, Placement>([
      ["review", { x: 0, y: 0, width: 50, height: 50 }],
      ["budget", { x: 12, y: 32, width: 500, height: 30 }],
      ["checking", { x: 12, y: 32 }],
      ["ok", { x: 300, y: 90 }],
      ["compliance", { x: 530, y: 32 }],
      ["pending", { x: 12, y: 32 }],
      ["cleared", { x: 12, y: 80 }],
    ]);
    const boxes = resolveBoxes(chart, placements, new Set());
    expect(boxes.get("budget")).toMatchObject({ width: 500, height: 90 + 24 + 12 });
    expect(boxes.get("compliance")!.height).toBe(boxes.get("budget")!.height);
    expect(boxes.get("review")!.width).toBe(530 + boxes.get("compliance")!.width + 12);
    const folded = resolveBoxes(chart, placements, new Set(["review"]));
    expect(folded.has("budget")).toBe(false);
    expect(folded.get("review")!.height).toBe(24);
    const grown = grownSizes(chart, placements, boxes, ["ok"]);
    expect([...grown.keys()].sort()).toEqual(["budget", "review"]);
    expect(grown.get("review")).toMatchObject({ x: 0, y: 0, width: boxes.get("review")!.width });
  });
});
