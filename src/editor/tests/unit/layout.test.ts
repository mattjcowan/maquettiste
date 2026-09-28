// Auto-layout (phase2-design.md 4.9): the ELK graph and options, real ELK (the bundled build, which
// runs the same layered algorithm as the worker without a Worker), the dagre fallback, and the
// fallback wrapper's once-only switch and report.
import { describe, expect, it, vi } from "vitest";
import ELK from "elkjs/lib/elk.bundled.js";
import {
  dagreEngine,
  ELK_OPTIONS,
  elkEngine,
  elkGraph,
  withFallback,
  type LayoutEdge,
  type LayoutEngine,
  type LayoutNode,
  type Positions,
} from "@/canvas/layout";

const nodes: LayoutNode[] = [
  { id: "customer", width: 240, height: 140 },
  { id: "invoice", width: 240, height: 220 },
  { id: "line", width: 240, height: 120 },
  { id: "payment", width: 240, height: 160 },
];
const edges: LayoutEdge[] = [
  { id: "r1", source: "customer", target: "invoice" },
  { id: "r2", source: "invoice", target: "line" },
  { id: "r3", source: "invoice", target: "payment" },
];

function overlaps(positions: Positions, list: LayoutNode[]): boolean {
  const boxes = list.map((n) => ({ ...positions.get(n.id)!, w: n.width, h: n.height }));
  for (let i = 0; i < boxes.length; i++)
    for (let j = i + 1; j < boxes.length; j++) {
      const a = boxes[i];
      const b = boxes[j];
      if (a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h) return true;
    }
  return false;
}

const bundledElk = () => Promise.resolve(new ELK());

describe("ELK graph", () => {
  it("uses layered, direction right, orthogonal routing, spacing 48 and 96", () => {
    expect(ELK_OPTIONS["elk.algorithm"]).toBe("layered");
    expect(ELK_OPTIONS["elk.direction"]).toBe("RIGHT");
    expect(ELK_OPTIONS["elk.edgeRouting"]).toBe("ORTHOGONAL");
    expect(ELK_OPTIONS["elk.spacing.nodeNode"]).toBe("48");
    expect(ELK_OPTIONS["elk.layered.spacing.nodeNodeBetweenLayers"]).toBe("96");
  });

  it("keeps measured sizes and drops self-loops, dangling and duplicate edges", () => {
    const graph = elkGraph(nodes, [
      ...edges,
      { id: "self", source: "invoice", target: "invoice" },
      { id: "gone", source: "invoice", target: "not-on-canvas" },
      { id: "r1", source: "customer", target: "invoice" },
    ]);
    expect(graph.children?.find((c) => c.id === "invoice")).toMatchObject({ width: 240, height: 220 });
    expect(graph.edges?.map((e) => e.id)).toEqual(["r1", "r2", "r3"]);
  });
});

describe("ELK engine", () => {
  it("lays the billing shape out left to right without overlaps", async () => {
    const positions = await elkEngine(bundledElk).layout(nodes, edges);
    expect([...positions.keys()].sort()).toEqual(["customer", "invoice", "line", "payment"]);
    expect(overlaps(positions, nodes)).toBe(false);
    const x = (id: string) => positions.get(id)!.x;
    expect(x("customer")).toBeLessThan(x("invoice"));
    expect(x("invoice")).toBeLessThan(x("line"));
    // Layer spacing 96 between the right edge of one layer and the left edge of the next.
    expect(x("invoice") - (x("customer") + 240)).toBeGreaterThanOrEqual(96);
    for (const p of positions.values()) expect(Number.isInteger(p.x) && Number.isInteger(p.y)).toBe(true);
  });

  it("lays out 200 entities", async () => {
    const many: LayoutNode[] = Array.from({ length: 200 }, (_, i) => ({ id: `e${i}`, width: 240, height: 100 + (i % 5) * 20 }));
    const links: LayoutEdge[] = many.slice(1).map((n, i) => ({ id: `r${i}`, source: `e${Math.floor(i / 3)}`, target: n.id }));
    const positions = await elkEngine(bundledElk).layout(many, links);
    expect(positions.size).toBe(200);
    expect(overlaps(positions, many)).toBe(false);
  });

  it("returns nothing for an empty canvas without starting ELK", async () => {
    const create = vi.fn(bundledElk);
    expect((await elkEngine(create).layout([], [])).size).toBe(0);
    expect(create).not.toHaveBeenCalled();
  });

  it("starts ELK once and reuses it", async () => {
    const create = vi.fn(bundledElk);
    const engine = elkEngine(create);
    await engine.layout(nodes, edges);
    await engine.layout(nodes, edges);
    expect(create).toHaveBeenCalledTimes(1);
  });

  it("rejects when the worker cannot be created, and tries again next time", async () => {
    const create = vi.fn<() => Promise<InstanceType<typeof ELK>>>().mockRejectedValueOnce(new Error("Worker is not defined")).mockImplementation(bundledElk);
    const engine = elkEngine(create);
    await expect(engine.layout(nodes, edges)).rejects.toThrow("Worker is not defined");
    expect((await engine.layout(nodes, edges)).size).toBe(4);
  });
});

describe("dagre engine", () => {
  it("lays out left to right with top-left positions and no overlaps", async () => {
    const positions = await dagreEngine().layout(nodes, [...edges, { id: "self", source: "line", target: "line" }]);
    expect(positions.size).toBe(4);
    expect(overlaps(positions, nodes)).toBe(false);
    expect(positions.get("customer")!.x).toBeLessThan(positions.get("invoice")!.x);
  });
});

describe("fallback", () => {
  const fixed = (name: string, x: number): LayoutEngine => ({ name, layout: async (list) => new Map(list.map((n) => [n.id, { x, y: 0 }])) });
  const failing = (): LayoutEngine & { calls: number } => {
    const engine = {
      name: "elk",
      calls: 0,
      async layout(): Promise<Positions> {
        engine.calls++;
        throw new Error("boom");
      },
    };
    return engine;
  };

  it("uses the primary engine while it works", async () => {
    const report = vi.fn();
    const engine = withFallback(fixed("elk", 1), fixed("dagre", 2), report);
    expect((await engine.layout(nodes, edges)).get("invoice")).toEqual({ x: 1, y: 0 });
    expect(engine.name).toBe("elk");
    expect(report).not.toHaveBeenCalled();
  });

  it("switches to dagre when ELK rejects, reports once and stays switched", async () => {
    const report = vi.fn();
    const primary = failing();
    const engine = withFallback(primary, fixed("dagre", 2), report);
    expect((await engine.layout(nodes, edges)).get("invoice")).toEqual({ x: 2, y: 0 });
    expect((await engine.layout(nodes, edges)).get("invoice")).toEqual({ x: 2, y: 0 });
    expect(primary.calls).toBe(1);
    expect(engine.name).toBe("dagre");
    expect(report).toHaveBeenCalledTimes(1);
    expect(report.mock.calls[0][0]).toContain("boom");
  });

  it("falls back to real dagre when the worker cannot start", async () => {
    const report = vi.fn();
    const engine = withFallback(
      elkEngine(() => Promise.reject(new Error("no Worker"))),
      dagreEngine(),
      report,
    );
    const positions = await engine.layout(nodes, edges);
    expect(positions.size).toBe(4);
    expect(overlaps(positions, nodes)).toBe(false);
    expect(report).toHaveBeenCalledOnce();
  });
});
