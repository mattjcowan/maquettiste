// Auto-layout (phase2-design.md 4.9, PD2): elkjs `layered` (direction right, orthogonal edge
// routing, node spacing 48, layer spacing 96) in a web worker. Only if creating the worker fails
// or ELK rejects does the same interface fall back to dagre on the main thread, logged once.
// Callers take a LayoutEngine, so tests can use a fake without workers.
import type { ElkNode } from "elkjs/lib/elk-api";

export interface LayoutNode {
  id: string;
  width: number;
  height: number;
}
export interface LayoutEdge {
  id: string;
  source: string;
  target: string;
}
export type Positions = Map<string, { x: number; y: number }>;

export interface LayoutEngine {
  readonly name: string;
  layout(nodes: LayoutNode[], edges: LayoutEdge[]): Promise<Positions>;
}

export const ELK_OPTIONS = {
  "elk.algorithm": "layered",
  "elk.direction": "RIGHT",
  "elk.edgeRouting": "ORTHOGONAL",
  "elk.spacing.nodeNode": "48",
  "elk.layered.spacing.nodeNodeBetweenLayers": "96",
  "elk.layered.considerModelOrder.strategy": "NODES_AND_EDGES",
};

/** The part of the ELK API the engine uses (elk-api's ELK, or elk.bundled's in tests). */
export interface ElkLike {
  layout(graph: ElkNode): Promise<ElkNode>;
}

/** The browser ELK: elk-api on the main thread, the layered algorithm in elk-worker.min.js. */
function workerElk(): Promise<ElkLike> {
  return Promise.all([import("elkjs/lib/elk-api.js"), import("elkjs/lib/elk-worker.min.js?url")]).then(([api, worker]) => {
    const ELK = (api as unknown as { default: new (options: { workerFactory: () => Worker }) => ElkLike }).default;
    return new ELK({ workerFactory: () => new Worker(worker.default) });
  });
}

/** The ELK graph for a set of nodes and edges: dangling edges and self-loops are left out. */
export function elkGraph(nodes: LayoutNode[], edges: LayoutEdge[]): ElkNode {
  const ids = new Set(nodes.map((n) => n.id));
  const seen = new Set<string>();
  return {
    id: "root",
    layoutOptions: ELK_OPTIONS,
    children: nodes.map((n) => ({ id: n.id, width: n.width, height: n.height })),
    edges: edges
      .filter((e) => ids.has(e.source) && ids.has(e.target) && e.source !== e.target && !seen.has(e.id) && seen.add(e.id))
      .map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] })),
  };
}

export function elkEngine(create: () => Promise<ElkLike> = workerElk): LayoutEngine {
  let instance: Promise<ElkLike> | null = null;
  const get = () => {
    if (!instance) {
      const started = create();
      instance = started;
      // A failed start is not cached, so a later call can try again (the fallback decides first).
      started.catch(() => {
        if (instance === started) instance = null;
      });
    }
    return instance;
  };
  return {
    name: "elk",
    async layout(nodes, edges) {
      if (!nodes.length) return new Map();
      const elk = await get();
      const graph = elkGraph(nodes, edges);
      const result = await elk.layout(graph);
      return new Map((result.children ?? []).map((c) => [c.id, { x: Math.round(c.x ?? 0), y: Math.round(c.y ?? 0) }]));
    },
  };
}

export function dagreEngine(): LayoutEngine {
  return {
    name: "dagre",
    async layout(nodes, edges) {
      if (!nodes.length) return new Map();
      const dagre = (await import("@dagrejs/dagre")).default;
      const g = new dagre.graphlib.Graph();
      g.setGraph({ rankdir: "LR", nodesep: 48, ranksep: 96 });
      g.setDefaultEdgeLabel(() => ({}));
      for (const n of nodes) g.setNode(n.id, { width: n.width, height: n.height });
      const ids = new Set(nodes.map((n) => n.id));
      for (const e of edges) if (ids.has(e.source) && ids.has(e.target) && e.source !== e.target) g.setEdge(e.source, e.target);
      dagre.layout(g);
      return new Map(
        nodes.map((n) => {
          const p = g.node(n.id) as { x: number; y: number };
          return [n.id, { x: Math.round(p.x - n.width / 2), y: Math.round(p.y - n.height / 2) }];
        }),
      );
    },
  };
}

/** ELK first; dagre when the worker cannot be created or ELK rejects (reported once). */
export function withFallback(primary: LayoutEngine, fallback: LayoutEngine, report: (message: string) => void = (m) => console.warn(m)): LayoutEngine {
  let broken = false;
  let reported = false;
  return {
    get name() {
      return broken ? fallback.name : primary.name;
    },
    async layout(nodes, edges) {
      if (!broken) {
        try {
          return await primary.layout(nodes, edges);
        } catch (error) {
          broken = true;
          if (!reported) {
            reported = true;
            report(`Auto-layout: ${primary.name} failed (${(error as Error)?.message ?? error}); using ${fallback.name} from now on.`);
          }
        }
      }
      return fallback.layout(nodes, edges);
    },
  };
}

let shared: LayoutEngine | null = null;
export function defaultLayoutEngine(): LayoutEngine {
  return (shared ??= withFallback(elkEngine(), dagreEngine()));
}
