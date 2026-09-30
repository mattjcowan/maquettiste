// Where the statechart's boxes go (phase-3-design.md 6.3), kept free of React so it can be unit tested. States have
// sizes computed from their text (so a layout never waits for the page to measure them); containers hold their
// children with a header row on top; a parallel state's regions sit side by side. Three operations:
// - `layoutChart`: the Layout command, the whole chart or one container, through the nested layout (canvas/layout.ts).
// - `placeMissing`: states without a saved placement (a new state) are placed inside their container beside what is
//   already there (canvas/placement.ts); nothing that has a placement moves, containers only grow to fit.
// - `resolveBoxes`: what the canvas draws: saved placements, containers at least as large as their children.
import { ROW_H } from "@/design/density";
import { fitTree, type Box, type Boxes, type HierarchyEngine, type LayoutEdge, type LayoutTree, type Padding, type TreeNode } from "../layout";
import { placeNodes } from "../placement";
import { descendants, representatives, visibleEdges, type Chart, type ChartEdge, type ChartState } from "./chartModel";
import type { Placement, Placements } from "./diagram";

/** A container's padding: the header row and a margin on top, 12 around. */
export const CONTAINER_PAD: Padding = { top: ROW_H + 8, left: 12, bottom: 12, right: 12 };
/** The gap between a parallel state's regions (a dashed line runs through its middle). */
export const REGION_GAP = 16;
/** The initial pseudo-state's dot. */
export const DOT = 12;
/** The initial dot's node id for a container (`init:` alone for the process's root). */
export const INIT = "init:";
/** Room left of a container's first state for its initial dot and arrow (dotBox). */
export const INITIAL_ROOM = DOT + 24;
/** A new state's gap to its siblings: the layout's layer and node spacing. */
export const PLACE_GAP = { x: 64, y: 32 } as const;
/** An internal transition's row inside its state. */
export const INTERNAL_ROW = 18;

const MIN_WIDTH = 88;
const MAX_WIDTH = 240;

/** An estimate of a text's width at a font size: the canvas sizes boxes without measuring them. */
export function textWidth(text: string, px = 12): number {
  return Math.ceil(text.length * px * 0.6);
}

/** The drawn size of a state that is not an open container: a box, or a symbol and its name. `hidden`: a collapsed container's count. */
export function leafSize(state: ChartState, hidden?: number): { width: number; height: number } {
  if (state.type === "final" || state.type === "history" || state.type === "choice") {
    return { width: Math.max(48, 20 + 6 + textWidth(state.label) + 6), height: ROW_H };
  }
  const header =
    16 +
    textWidth(state.label) +
    (state.bound ? 8 + textWidth(state.bound, 11) : 0) +
    (state.badges.length ? 22 : 0) +
    (hidden !== undefined ? 20 + 8 + textWidth(String(hidden), 11) + 8 : 0);
  const rows = state.internal.length;
  const internal = rows ? Math.max(...state.internal.map((r) => 16 + textWidth(r.label, 11))) : 0;
  return {
    width: Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, header, internal)),
    height: ROW_H + (rows ? rows * INTERNAL_ROW + 4 : 0),
  };
}

/** An open container's least size: its header's text and its padding. */
export function containerMinSize(state: ChartState): { width: number; height: number } {
  const header = 12 + 20 + textWidth(state.label) + (state.bound ? 8 + textWidth(state.bound, 11) : 0) + (state.badges.length ? 22 : 0) + 12;
  return { width: Math.max(MIN_WIDTH, header), height: CONTAINER_PAD.top + CONTAINER_PAD.bottom };
}

/** True when a state draws as an open container (it has children and is not collapsed). */
export function isOpen(state: ChartState | undefined, collapsed: ReadonlySet<string>): boolean {
  return !!state && state.container && state.children.length > 0 && !collapsed.has(state.id);
}

/** The child of `holder` (null: the root) whose branch holds a state, or null when it is not under `holder`. */
export function branchAt(chart: Chart, id: string, holder: string | null): string | null {
  let cur: string | null = id;
  while (cur) {
    const parent: string | null = chart.states.get(cur)?.parent ?? null;
    if (parent === holder) return cur;
    cur = parent;
  }
  return null;
}

/** The transitions between different children of `holder`, lifted to those children (their branches). */
export function liftedEdges(chart: Chart, holder: string | null): LayoutEdge[] {
  const out: LayoutEdge[] = [];
  for (const e of chart.edges) {
    const a = branchAt(chart, e.source, holder);
    const b = branchAt(chart, e.target, holder);
    if (a && b && a !== b) out.push({ id: e.id, source: a, target: b });
  }
  return out;
}

/** The nested layout's input for the whole chart (`scope` null) or one container and what it holds. */
export function chartTree(chart: Chart, collapsed: ReadonlySet<string>, scope: string | null): LayoutTree {
  const edges: LayoutEdge[] = [];
  const inTree = new Set<string>();
  const node = (id: string): TreeNode => {
    const s = chart.states.get(id)!;
    inTree.add(id);
    if (!isOpen(s, collapsed)) return { id, ...leafSize(s, s.container && collapsed.has(id) ? descendants(chart, id).length : undefined) };
    const children = s.children.map(node);
    if (s.initialChild) {
      children.unshift({ id: `${INIT}${id}`, width: DOT, height: DOT });
      edges.push({ id: `${INIT}${id}`, source: `${INIT}${id}`, target: s.initialChild });
    }
    // Regions side by side: a chain of edges puts each in the layer after the previous one.
    if (s.type === "parallel")
      for (let i = 1; i < s.children.length; i++) edges.push({ id: `chain:${s.children[i]}`, source: s.children[i - 1], target: s.children[i] });
    return { id, ...containerMinSize(s), padding: CONTAINER_PAD, children };
  };
  let top: TreeNode[];
  if (scope) top = [node(scope)];
  else {
    top = chart.roots.map(node);
    if (chart.rootInitial) {
      top.unshift({ id: INIT, width: DOT, height: DOT });
      edges.push({ id: INIT, source: INIT, target: chart.rootInitial });
    }
  }
  const rep = representatives(chart, collapsed);
  for (const e of visibleEdges(chart, rep))
    if (inTree.has(e.source) && inTree.has(e.target)) edges.push({ id: e.id, source: e.source, target: e.target, label: labelSize(e) });
  return { children: top, edges };
}

/** The room an edge's label pill takes (TransitionEdge): its text, the gate badge and a problem badge. */
export function labelSize(e: Pick<ChartEdge, "label" | "gate" | "badges">): { width: number; height: number } {
  return { width: 14 + textWidth(e.label, 11) + (e.gate ? 18 + textWidth(e.gate.text, 11) : 0) + (e.badges.length ? 16 : 0), height: 18 };
}

/** Puts each open parallel state's regions side by side, top-aligned, in document order (positions only). */
export function repackRegions(chart: Chart, collapsed: ReadonlySet<string>, boxes: Boxes): void {
  for (const id of chart.order) {
    const s = chart.states.get(id)!;
    if (s.type !== "parallel" || !isOpen(s, collapsed)) continue;
    let x = CONTAINER_PAD.left;
    for (const r of s.children) {
      const b = boxes.get(r);
      if (!b) continue;
      boxes.set(r, { ...b, x, y: CONTAINER_PAD.top });
      x += b.width + REGION_GAP;
    }
  }
}

/**
 * The Layout command: new placements for every state of the chart (`scope` null) or of what one container holds. A
 * laid-out container gets its new size (`size` for the scope itself, which keeps its position); a collapsed container
 * keeps its saved size and its hidden children their places. The initial dots are laid out with the states but not
 * saved (they follow their state).
 */
export async function layoutChart(
  chart: Chart,
  collapsed: ReadonlySet<string>,
  scope: string | null,
  engine: HierarchyEngine,
): Promise<{ placements: Placements; size: { width: number; height: number } | null }> {
  const tree = chartTree(chart, collapsed, scope);
  const boxes = await engine.layoutTree(tree);
  fitTree(tree, boxes);
  repackRegions(chart, collapsed, boxes);
  fitTree(tree, boxes);
  const placements: Placements = new Map();
  let size: { width: number; height: number } | null = null;
  for (const [id, b] of boxes) {
    const s = chart.states.get(id);
    if (!s) continue;
    const open = isOpen(s, collapsed);
    if (id === scope) size = { width: b.width, height: b.height };
    else placements.set(id, { x: b.x, y: b.y, ...(open ? { width: b.width, height: b.height } : {}) });
  }
  return { placements, size };
}

/**
 * The placements of the states a diagram does not place yet (a new state, a state from the States grid), and the
 * sizes of the containers that must grow to hold them. Each is placed among its siblings, inside its container only
 * (beside a sibling it has a transition with, else in rows under them); a new container's children are placed first.
 * Placed states never move; containers only grow. Empty when every state has a placement.
 */
export function placeMissing(chart: Chart, saved: ReadonlyMap<string, Placement>): Placements {
  const out: Placements = new Map();
  const missing = chart.order.filter((id) => !saved.has(id));
  if (!missing.length) return out;
  const known = (id: string) => out.get(id) ?? saved.get(id);
  const size = (id: string): { width: number; height: number } => {
    const s = chart.states.get(id)!;
    if (!(s.container && s.children.length)) return leafSize(s);
    const min = containerMinSize(s);
    const p = known(id);
    let right = CONTAINER_PAD.left;
    let bottom = CONTAINER_PAD.top;
    for (const c of s.children) {
      const cp = known(c);
      if (!cp) continue;
      const cs = size(c);
      right = Math.max(right, cp.x + cs.width);
      bottom = Math.max(bottom, cp.y + cs.height);
    }
    return {
      width: Math.max(min.width, p?.width ?? 0, right + CONTAINER_PAD.right),
      height: Math.max(min.height, p?.height ?? 0, bottom + CONTAINER_PAD.bottom),
    };
  };
  // Deepest containers first, so a new container is sized by its children before it is placed among its siblings.
  const holders = [...new Set(missing.map((id) => chart.states.get(id)!.parent))].sort(
    (a, b) => (b ? chart.states.get(b)!.depth : -1) - (a ? chart.states.get(a)!.depth : -1),
  );
  const grown = new Set<string>();
  for (const holder of holders) {
    const kids = holder ? chart.states.get(holder)!.children : chart.roots;
    const unplaced = kids.filter((k) => !known(k));
    if (!unplaced.length) continue;
    const placed = kids.filter((k) => known(k)).map((k) => ({ id: k, x: known(k)!.x, y: known(k)!.y, ...size(k) }));
    const positions = placeNodes({
      placed,
      unplaced: unplaced.map((k) => ({ id: k, ...size(k) })),
      edges: liftedEdges(chart, holder),
      gap: PLACE_GAP,
    });
    // The first states of a container (or of the chart) start inside its padding, with room for the initial dot.
    const offset = placed.length ? { x: 0, y: 0 } : holder ? { x: CONTAINER_PAD.left + INITIAL_ROOM, y: CONTAINER_PAD.top } : { x: INITIAL_ROOM, y: 0 };
    for (const k of unplaced) {
      const p = positions[k];
      const s = chart.states.get(k)!;
      out.set(k, { x: p.x + offset.x, y: p.y + offset.y, ...(s.container && s.children.length ? size(k) : {}) });
    }
    for (let up = holder; up; up = chart.states.get(up)!.parent) grown.add(up);
  }
  // The containers that now hold a new state grow to fit it, deepest first; their positions stay.
  for (const id of [...grown].sort((a, b) => chart.states.get(b)!.depth - chart.states.get(a)!.depth)) {
    const p = known(id);
    if (!p) continue;
    const next = size(id);
    if (p.width === next.width && p.height === next.height) continue;
    out.set(id, { ...p, ...next });
  }
  return out;
}

/** The initial dot's box: left of its state, level with the state's middle (where the arrow enters it). */
export function dotBox(target: Box): Box {
  return { x: Math.max(2, target.x - INITIAL_ROOM), y: Math.round(target.y + target.height / 2 - DOT / 2), width: DOT, height: DOT };
}

/**
 * What the canvas draws for each visible state: its saved (or placed) position relative to its container, a leaf's
 * computed size, an open container at least as large as its children plus padding (and its saved size); a parallel
 * state's regions share the tallest one's height. States inside a collapsed container are left out.
 */
export function resolveBoxes(chart: Chart, placements: ReadonlyMap<string, Placement>, collapsed: ReadonlySet<string>): Boxes {
  const out: Boxes = new Map();
  const visit = (id: string): Box => {
    const s = chart.states.get(id)!;
    const p = placements.get(id) ?? { x: 0, y: 0 };
    if (!isOpen(s, collapsed)) {
      const box = { x: p.x, y: p.y, ...leafSize(s, s.container && s.children.length ? descendants(chart, id).length : undefined) };
      out.set(id, box);
      return box;
    }
    const kids = s.children.map(visit);
    if (s.type === "parallel" && kids.length) {
      const tallest = Math.max(...kids.map((k) => k.height));
      s.children.forEach((c, i) => {
        kids[i] = { ...kids[i], height: tallest };
        out.set(c, kids[i]);
      });
    }
    const min = containerMinSize(s);
    const right = Math.max(CONTAINER_PAD.left, ...kids.map((k) => k.x + k.width));
    const bottom = Math.max(CONTAINER_PAD.top, ...kids.map((k) => k.y + k.height));
    const box = {
      x: p.x,
      y: p.y,
      width: Math.max(min.width, p.width ?? 0, right + CONTAINER_PAD.right),
      height: Math.max(min.height, p.height ?? 0, bottom + CONTAINER_PAD.bottom),
    };
    out.set(id, box);
    return box;
  };
  chart.roots.forEach(visit);
  return out;
}

/** The x of each dashed line between a parallel state's regions (relative to the parallel state's box). */
export function regionSeparators(chart: Chart, id: string, boxes: Boxes): number[] {
  const s = chart.states.get(id);
  if (!s || s.type !== "parallel") return [];
  const regions = s.children.map((c) => boxes.get(c)).filter((b): b is Box => !!b);
  const sorted = [...regions].sort((a, b) => a.x - b.x);
  const out: number[] = [];
  for (let i = 1; i < sorted.length; i++) out.push(Math.round((sorted[i - 1].x + sorted[i - 1].width + sorted[i].x) / 2));
  return out;
}

/** The containers whose saved size is smaller than what they now draw (after a move or a placement): their new sizes. */
export function grownSizes(chart: Chart, placements: ReadonlyMap<string, Placement>, boxes: Boxes, ids: Iterable<string>): Placements {
  const out: Placements = new Map();
  for (const id of ids)
    for (let up = chart.states.get(id)?.parent ?? null; up; up = chart.states.get(up)?.parent ?? null) {
      const p = placements.get(up);
      const b = boxes.get(up);
      if (!p || !b || out.has(up)) continue;
      if ((p.width ?? 0) >= b.width && (p.height ?? 0) >= b.height) continue;
      out.set(up, { x: p.x, y: p.y, width: b.width, height: b.height });
    }
  return out;
}
