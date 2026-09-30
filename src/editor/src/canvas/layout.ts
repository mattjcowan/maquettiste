// Auto-layout (phase2-design.md 4.9, PD2): elkjs `layered` (direction right, orthogonal edge
// routing, node spacing 48, layer spacing 96) in a web worker. Only if creating the worker fails
// or ELK rejects does the same interface fall back to dagre on the main thread, logged once.
// Callers take a LayoutEngine, so tests can use a fake without workers.
// The statechart canvas (phase-3-design.md 6.3) lays out nested boxes: a HierarchyEngine takes a tree of nodes (leaves
// with their sizes, containers with their padding) and returns each node's box relative to its container. ELK runs it
// in one call in its hierarchical mode (INCLUDE_CHILDREN: node spacing 32, layer spacing 64, edges that cross
// containers laid out with the rest); the fallback lays each container out bottom-up with a flat engine. `fitTree` then
// sizes every container, bottom-up, to its children plus its padding.
import type { ElkExtendedEdge, ElkNode } from "elkjs/lib/elk-api";

export interface LayoutNode {
  id: string;
  width: number;
  height: number;
}
export interface LayoutEdge {
  id: string;
  source: string;
  target: string;
  /** The size of the edge's label, when the layout should make room for it (the statechart's transitions). */
  label?: { width: number; height: number };
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
  // Edge labels (a relation's name) are laid out with their size and kept off the cards: the engine makes room for a
  // label between layers, and keeps edges and labels this far from the nodes they pass.
  "elk.edgeLabels.placement": "CENTER",
  "elk.spacing.edgeLabel": "12",
  "elk.spacing.edgeNode": "24",
  "elk.layered.spacing.edgeNodeBetweenLayers": "24",
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
      .map((e) => ({
        id: e.id,
        sources: [e.source],
        targets: [e.target],
        // The engine makes room only for a label that has text; the text itself is never drawn, so the id serves.
        ...(e.label ? { labels: [{ id: `${e.id}:label`, text: e.id, width: e.label.width, height: e.label.height }] } : {}),
      })),
  };
}

/** A lazily started ELK: one start per engine; a failed start is not cached, so a later call can try again. */
function lazyElk(create: () => Promise<ElkLike>): () => Promise<ElkLike> {
  let instance: Promise<ElkLike> | null = null;
  return () => {
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
}

export function elkEngine(create: () => Promise<ElkLike> = workerElk): LayoutEngine {
  const get = lazyElk(create);
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

/** Dagre on the main thread, left to right; `spacing` between nodes of a layer and between layers (the entity canvas's 48 and 96 by default). */
export function dagreEngine(spacing: { node: number; layer: number } = { node: 48, layer: 96 }): LayoutEngine {
  return {
    name: "dagre",
    async layout(nodes, edges) {
      if (!nodes.length) return new Map();
      const dagre = (await import("@dagrejs/dagre")).default;
      const g = new dagre.graphlib.Graph();
      g.setGraph({ rankdir: "LR", nodesep: spacing.node, ranksep: spacing.layer });
      g.setDefaultEdgeLabel(() => ({}));
      for (const n of nodes) g.setNode(n.id, { width: n.width, height: n.height });
      const ids = new Set(nodes.map((n) => n.id));
      for (const e of edges)
        if (ids.has(e.source) && ids.has(e.target) && e.source !== e.target)
          g.setEdge(e.source, e.target, e.label ? { width: e.label.width, height: e.label.height, labelpos: "c" } : {});
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

// ------------------------------------------------------------------ nested boxes (the statechart canvas)

/** The statechart's layout options (phase-3-design.md 6.3): layered, direction right, node spacing 32, layer spacing 64,
 * orthogonal edges, and the hierarchical mode so edges between states of different containers shape the whole layout. */
export const HIERARCHY_OPTIONS = {
  "elk.algorithm": "layered",
  "elk.direction": "RIGHT",
  "elk.edgeRouting": "ORTHOGONAL",
  "elk.spacing.nodeNode": "32",
  "elk.layered.spacing.nodeNodeBetweenLayers": "64",
  "elk.hierarchyHandling": "INCLUDE_CHILDREN",
  "elk.layered.considerModelOrder.strategy": "NODES_AND_EDGES",
  // A transition back to an earlier state (in document order) is the one reversed, so the chart reads in document order.
  "elk.layered.cycleBreaking.strategy": "MODEL_ORDER",
  "elk.edgeLabels.placement": "CENTER",
};

/** The options every container repeats (ELK reads spacing per parent node); the ordering strategies are the top's
 * only, since ELK's hierarchical mode fails when a container sets them. */
const CONTAINER_OPTIONS = Object.fromEntries(
  Object.entries(HIERARCHY_OPTIONS).filter(
    ([k]) => k !== "elk.hierarchyHandling" && k !== "elk.layered.considerModelOrder.strategy" && k !== "elk.layered.cycleBreaking.strategy",
  ),
);

/** The flat engine's spacing for the bottom-up fallback, as HIERARCHY_OPTIONS. */
export const HIERARCHY_GAP = { node: 32, layer: 64 } as const;

export interface Padding {
  top: number;
  left: number;
  bottom: number;
  right: number;
}

/** A node of a nested layout: a leaf with its size, or a container (children) whose width and height are its minimum. */
export interface TreeNode {
  id: string;
  width: number;
  height: number;
  /** A container's padding around its children (the header row goes in `top`). */
  padding?: Padding;
  children?: TreeNode[];
}

export interface LayoutTree {
  children: TreeNode[];
  /** Edges between any two nodes of the tree, whatever their containers. */
  edges: LayoutEdge[];
}

/** A node's box: x and y relative to its container's top-left (the tree's top level: to the origin). */
export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}
export type Boxes = Map<string, Box>;

export interface HierarchyEngine {
  readonly name: string;
  layoutTree(tree: LayoutTree): Promise<Boxes>;
}

const NO_PADDING: Padding = { top: 0, left: 0, bottom: 0, right: 0 };

/** Walks a tree depth first, parents before children. */
export function eachTreeNode(nodes: readonly TreeNode[], visit: (node: TreeNode, parent: TreeNode | null) => void, parent: TreeNode | null = null): void {
  for (const n of nodes) {
    visit(n, parent);
    if (n.children?.length) eachTreeNode(n.children, visit, n);
  }
}

/** The tree's edges without dangling ends, self-loops, duplicates, or an end inside the other (ELK cannot route those). */
function usableEdges(tree: LayoutTree): { edges: LayoutEdge[]; path: Map<string, string[]> } {
  const path = new Map<string, string[]>();
  eachTreeNode(tree.children, (n, parent) => path.set(n.id, [...(parent ? path.get(parent.id)! : []), n.id]));
  const seen = new Set<string>();
  const edges = tree.edges.filter((e) => {
    const a = path.get(e.source);
    const b = path.get(e.target);
    if (!a || !b || e.source === e.target || seen.has(e.id)) return false;
    seen.add(e.id);
    return !a.includes(e.target) && !b.includes(e.source);
  });
  return { edges, path };
}

/**
 * The ELK graph of a tree: containers carry their padding and no size (ELK sizes them); each edge sits in the innermost
 * container that holds both its ends (where ELK makes room for its label).
 */
export function elkTreeGraph(tree: LayoutTree): ElkNode {
  const { edges, path } = usableEdges(tree);
  const edgesOf = new Map<string, ElkExtendedEdge[]>();
  for (const e of edges) {
    const a = path.get(e.source)!;
    const b = path.get(e.target)!;
    let i = 0;
    while (a[i] === b[i]) i++;
    const holder = i === 0 ? "root" : a[i - 1];
    const list = edgesOf.get(holder) ?? [];
    list.push({
      id: e.id,
      sources: [e.source],
      targets: [e.target],
      // ELK makes room only for a label that has text.
      ...(e.label ? { labels: [{ id: `${e.id}:label`, text: e.id, width: e.label.width, height: e.label.height }] } : {}),
    });
    edgesOf.set(holder, list);
  }
  const node = (n: TreeNode): ElkNode =>
    n.children?.length
      ? {
          id: n.id,
          layoutOptions: {
            ...CONTAINER_OPTIONS,
            "elk.padding": `[top=${n.padding?.top ?? 0},left=${n.padding?.left ?? 0},bottom=${n.padding?.bottom ?? 0},right=${n.padding?.right ?? 0}]`,
            "elk.nodeSize.constraints": "MINIMUM_SIZE",
            "elk.nodeSize.minimum": `(${n.width},${n.height})`,
          },
          children: n.children.map(node),
          ...(edgesOf.has(n.id) ? { edges: edgesOf.get(n.id) } : {}),
        }
      : { id: n.id, width: n.width, height: n.height };
  return {
    id: "root",
    layoutOptions: { ...HIERARCHY_OPTIONS, "elk.padding": "[top=0,left=0,bottom=0,right=0]" },
    children: tree.children.map(node),
    edges: edgesOf.get("root") ?? [],
  };
}

/** The boxes of an ELK result, rounded, relative to their containers. */
export function elkBoxes(result: ElkNode): Boxes {
  const boxes: Boxes = new Map();
  const walk = (list: ElkNode[] | undefined) => {
    for (const c of list ?? []) {
      boxes.set(c.id, { x: Math.round(c.x ?? 0), y: Math.round(c.y ?? 0), width: Math.round(c.width ?? 0), height: Math.round(c.height ?? 0) });
      walk(c.children);
    }
  };
  walk(result.children);
  return boxes;
}

/** ELK's hierarchical mode in its worker: the whole tree in one call. */
export function elkHierarchyEngine(create: () => Promise<ElkLike> = workerElk): HierarchyEngine {
  const get = lazyElk(create);
  return {
    name: "elk",
    async layoutTree(tree) {
      if (!tree.children.length) return new Map();
      const elk = await get();
      return elkBoxes(await elk.layout(elkTreeGraph(tree)));
    },
  };
}

/**
 * The bottom-up fallback: each container's children are laid out first by the flat engine (edges between states of
 * different children lifted to those children), the container is sized to fit them plus its padding, then its parent
 * is laid out with that size.
 */
export function bottomUpEngine(flat: LayoutEngine): HierarchyEngine {
  return {
    get name() {
      return `${flat.name} (bottom-up)`;
    },
    async layoutTree(tree) {
      const boxes: Boxes = new Map();
      const { edges, path } = usableEdges(tree);
      // Each edge belongs to the container that holds both ends' branches: there it joins the two branches.
      const lifted = new Map<string, LayoutEdge[]>();
      for (const e of edges) {
        const a = path.get(e.source)!;
        const b = path.get(e.target)!;
        let i = 0;
        while (a[i] === b[i]) i++;
        const holder = i === 0 ? "" : a[i - 1];
        const list = lifted.get(holder) ?? [];
        list.push({ id: e.id, source: a[i], target: b[i], ...(e.label ? { label: e.label } : {}) });
        lifted.set(holder, list);
      }
      const lay = async (holder: string, children: TreeNode[], padding: Padding): Promise<{ width: number; height: number }> => {
        const sized: LayoutNode[] = [];
        for (const c of children) {
          if (c.children?.length) {
            const inner = await lay(c.id, c.children, c.padding ?? NO_PADDING);
            sized.push({ id: c.id, width: Math.max(c.width, inner.width), height: Math.max(c.height, inner.height) });
          } else sized.push({ id: c.id, width: c.width, height: c.height });
        }
        const positions = await flat.layout(sized, lifted.get(holder) ?? []);
        const minX = Math.min(...sized.map((n) => positions.get(n.id)?.x ?? 0));
        const minY = Math.min(...sized.map((n) => positions.get(n.id)?.y ?? 0));
        let right = 0;
        let bottom = 0;
        for (const n of sized) {
          const p = positions.get(n.id) ?? { x: 0, y: 0 };
          const box = { x: Math.round(p.x - minX + padding.left), y: Math.round(p.y - minY + padding.top), width: n.width, height: n.height };
          boxes.set(n.id, box);
          right = Math.max(right, box.x + box.width);
          bottom = Math.max(bottom, box.y + box.height);
        }
        return { width: right + padding.right, height: bottom + padding.bottom };
      };
      if (tree.children.length) await lay("", tree.children, NO_PADDING);
      return boxes;
    },
  };
}

/**
 * Sizes every container of a laid-out tree, bottom-up, to fit its children plus its padding (never under its minimum
 * size); leaves keep their sizes. Positions are not changed. Returns the same map.
 */
export function fitTree(tree: LayoutTree, boxes: Boxes): Boxes {
  const fit = (n: TreeNode): void => {
    const box = boxes.get(n.id);
    if (!n.children?.length) {
      if (box) boxes.set(n.id, { ...box, width: n.width, height: n.height });
      return;
    }
    n.children.forEach(fit);
    const pad = n.padding ?? NO_PADDING;
    let right = pad.left;
    let bottom = pad.top;
    for (const c of n.children) {
      const b = boxes.get(c.id);
      if (!b) continue;
      right = Math.max(right, b.x + b.width);
      bottom = Math.max(bottom, b.y + b.height);
    }
    boxes.set(n.id, { x: box?.x ?? 0, y: box?.y ?? 0, width: Math.max(n.width, right + pad.right), height: Math.max(n.height, bottom + pad.bottom) });
  };
  tree.children.forEach(fit);
  return boxes;
}

/** ELK's hierarchical mode first; the bottom-up fallback when the worker cannot be created or ELK rejects (reported once). */
export function withHierarchyFallback(
  primary: HierarchyEngine,
  fallback: HierarchyEngine,
  report: (message: string) => void = (m) => console.warn(m),
): HierarchyEngine {
  let broken = false;
  return {
    get name() {
      return broken ? fallback.name : primary.name;
    },
    async layoutTree(tree) {
      if (!broken) {
        try {
          return await primary.layoutTree(tree);
        } catch (error) {
          broken = true;
          report(`Layout: ${primary.name} failed (${(error as Error)?.message ?? error}); using ${fallback.name} from now on.`);
        }
      }
      return fallback.layoutTree(tree);
    },
  };
}

let sharedHierarchy: HierarchyEngine | null = null;
export function defaultHierarchyEngine(): HierarchyEngine {
  return (sharedHierarchy ??= withHierarchyFallback(elkHierarchyEngine(), bottomUpEngine(dagreEngine(HIERARCHY_GAP))));
}

let warmed = false;
/** Starts the hierarchical engine's worker with a tiny layout, once per page, so the first Layout does not pay for it. */
export function warmHierarchyEngine(engine: HierarchyEngine = defaultHierarchyEngine()): void {
  if (warmed) return;
  warmed = true;
  const pad = { top: 8, left: 8, bottom: 8, right: 8 };
  const leaf = (id: string) => ({ id, width: 40, height: 20 });
  void engine
    .layoutTree({
      children: [leaf("a"), { id: "c", width: 40, height: 40, padding: pad, children: [leaf("b"), leaf("d")] }],
      edges: [
        { id: "ab", source: "a", target: "b", label: { width: 30, height: 16 } },
        { id: "bd", source: "b", target: "d" },
      ],
    })
    .catch(() => undefined);
}
