// Where a transition runs when a straight line would cross other states (phase-3-design.md 6.3, as built): routes are
// not saved, so each render derives them from the states' boxes. A transition whose run between its two states would
// pass through another drawn state is lifted over the states it crosses (a forward one above them, a backward one
// below), and transitions that share that stretch take separate lanes, so a chain such as Draft, Issued, Paid, Void
// shows Draft to Paid arcing over Issued and Draft to Void over both. Self loops and edges with a clear run are left
// to the edge's own stepped path.
import { ROW_H } from "@/design/density";
import type { Box } from "../layout";

/** The height an edge meets a state at: a box's middle, a container's header row (TransitionEdge draws the same). */
export const meet = (b: Box) => b.y + (b.height > ROW_H * 2 ? ROW_H / 2 : b.height / 2);

/** A forward edge leaves the source's right side for the target's left; the rule TransitionEdge's ends follow. */
export const isForward = (from: Box, to: Box) => from.x + from.width / 2 <= to.x + to.width / 2;

export interface RouteInput {
  id: string;
  from: Box;
  to: Box;
}

/** A lifted route: the y of the run over (or under) the crossed states, and whether it runs above them. */
export interface Detour {
  y: number;
  above: boolean;
}

/** The first lane's clearance from the crossed states and from the edge's own ends, and the gap between lanes. */
export const DETOUR_CLEARANCE = 20;
export const LANE_GAP = 24;
/** Two detours share a lane only if their stretches do not overlap, or their bases are far enough apart vertically. */
const LANE_REACH = 48;

const right = (b: Box) => b.x + b.width;
const bottom = (b: Box) => b.y + b.height;
const overlaps = (a: Box, b: Box) => a.x < right(b) && right(a) > b.x && a.y < bottom(b) && bottom(a) > b.y;

/**
 * The detours of the edges that would cross a drawn state, by edge id. `boxes` holds every drawn state (containers
 * included: a container that holds an edge's end is not an obstacle to it).
 */
export function detours(edges: RouteInput[], boxes: Box[]): Map<string, Detour> {
  const out = new Map<string, Detour>();
  type Pending = { id: string; xL: number; xR: number; base: number; above: boolean };
  const pending: Pending[] = [];
  for (const e of edges) {
    if (e.from === e.to) continue;
    const forward = isForward(e.from, e.to);
    const xL = forward ? right(e.from) : right(e.to);
    const xR = forward ? e.to.x : e.from.x;
    if (xR <= xL) continue;
    const sy = meet(e.from);
    const ty = meet(e.to);
    const band: Box = { x: xL, y: Math.min(sy, ty) - 1, width: xR - xL, height: Math.abs(sy - ty) + 2 };
    let base: number | null = null;
    for (const c of boxes) {
      if (c === e.from || c === e.to || overlaps(c, e.from) || overlaps(c, e.to) || !overlaps(c, band)) continue;
      const edge = forward ? c.y : bottom(c);
      base = base === null ? edge : forward ? Math.min(base, edge) : Math.max(base, edge);
    }
    if (base === null) continue;
    // The run clears the edge's own ends too.
    base = forward ? Math.min(base, sy, ty) : Math.max(base, sy, ty);
    pending.push({ id: e.id, xL, xR, base, above: forward });
  }
  // Lanes, from the inside out: a detour takes one lane per detour that shares its stretch (same side, overlapping x,
  // bases within reach) and is shorter, so a transition over many states runs outside those over fewer.
  const length = (p: Pending) => p.xR - p.xL;
  for (const p of pending) {
    let inner = 0;
    for (const o of pending) {
      if (o === p || o.above !== p.above || o.xL >= p.xR || o.xR <= p.xL || Math.abs(o.base - p.base) >= LANE_REACH) continue;
      if (length(o) < length(p) || (length(o) === length(p) && o.id < p.id)) inner += 1;
    }
    const offset = DETOUR_CLEARANCE + inner * LANE_GAP;
    out.set(p.id, { y: p.above ? p.base - offset : p.base + offset, above: p.above });
  }
  return out;
}

/** An orthogonal path through `points` with rounded corners of radius `r`. */
export function roundedPath(points: { x: number; y: number }[], r = 6): string {
  if (points.length < 2) return "";
  let d = `M ${points[0]!.x} ${points[0]!.y}`;
  for (let i = 1; i < points.length - 1; i++) {
    const p = points[i - 1]!;
    const c = points[i]!;
    const n = points[i + 1]!;
    const inLen = Math.hypot(c.x - p.x, c.y - p.y);
    const outLen = Math.hypot(n.x - c.x, n.y - c.y);
    const k = Math.min(r, inLen / 2, outLen / 2);
    const ax = c.x - ((c.x - p.x) / (inLen || 1)) * k;
    const ay = c.y - ((c.y - p.y) / (inLen || 1)) * k;
    const bx = c.x + ((n.x - c.x) / (outLen || 1)) * k;
    const by = c.y + ((n.y - c.y) / (outLen || 1)) * k;
    d += ` L ${ax} ${ay} Q ${c.x} ${c.y} ${bx} ${by}`;
  }
  const last = points[points.length - 1]!;
  return `${d} L ${last.x} ${last.y}`;
}

/** How far a lifted edge runs straight out of its state before turning. */
export const DETOUR_STUB = 12;

/** The lifted path and its label point, from the edge's ends (TransitionEdge's `edgeEnds`) and its detour. */
export function detourPath(sourceX: number, sourceY: number, targetX: number, targetY: number, detour: Detour): [string, number, number] {
  const forward = detour.above;
  const s1 = forward ? sourceX + DETOUR_STUB : sourceX - DETOUR_STUB;
  const t1 = forward ? targetX - DETOUR_STUB : targetX + DETOUR_STUB;
  const path = roundedPath([
    { x: sourceX, y: sourceY },
    { x: s1, y: sourceY },
    { x: s1, y: detour.y },
    { x: t1, y: detour.y },
    { x: t1, y: targetY },
    { x: targetX, y: targetY },
  ]);
  return [path, (s1 + t1) / 2, detour.y];
}
