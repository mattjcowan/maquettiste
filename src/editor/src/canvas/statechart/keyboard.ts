// The statechart canvas's keyboard model (phase-3-design.md 6.3), kept free of React so it can be unit tested: arrows
// move to the nearest state in that direction among the same container's children, Enter enters a container (its
// initial child), Escape goes to the parent, Tab and Shift+Tab walk the selected state's outgoing transitions; while
// a transition is being drawn (T) the arrows pick its target among every state drawn.
import type { Box } from "../layout";
import { isOpen } from "./chartLayout";
import type { Chart } from "./chartModel";

export type Direction = "left" | "right" | "up" | "down";

export function directionOf(key: string): Direction | null {
  return key === "ArrowLeft" ? "left" : key === "ArrowRight" ? "right" : key === "ArrowUp" ? "up" : key === "ArrowDown" ? "down" : null;
}

const center = (b: Box) => ({ x: b.x + b.width / 2, y: b.y + b.height / 2 });

/**
 * The candidate nearest to `from` in a direction: its centre must lie that way (strictly). Candidates within 45° of the
 * direction come first; among them the distance along the direction counts once and the offset across it twice, so
 * a box straight ahead wins over a nearer one off to the side. Ties go to the earlier candidate. Boxes must share one
 * coordinate space (siblings, or absolute boxes).
 */
export function nearestInDirection(from: Box, candidates: readonly { id: string; box: Box }[], direction: Direction): string | null {
  const f = center(from);
  let best: { id: string; cone: boolean; score: number } | null = null;
  for (const c of candidates) {
    const p = center(c.box);
    const dx = p.x - f.x;
    const dy = p.y - f.y;
    const along = direction === "right" ? dx : direction === "left" ? -dx : direction === "down" ? dy : -dy;
    const across = direction === "right" || direction === "left" ? Math.abs(dy) : Math.abs(dx);
    if (along <= 0) continue;
    const cone = across <= along;
    const score = along + 2 * across;
    if (!best || (cone && !best.cone) || (cone === best.cone && score < best.score)) best = { id: c.id, cone, score };
  }
  return best?.id ?? null;
}

/** The states drawn beside a state: the other children of its container (the root states for a root state). */
export function siblings(chart: Chart, id: string): string[] {
  const parent = chart.states.get(id)?.parent ?? null;
  const list = parent ? (chart.states.get(parent)?.children ?? []) : chart.roots;
  return list.filter((x) => x !== id);
}

/** The arrow keys' move: the nearest sibling in that direction (null at the edge). `boxes` are relative to their container. */
export function moveSelection(chart: Chart, boxes: ReadonlyMap<string, Box>, id: string, direction: Direction): string | null {
  const from = boxes.get(id);
  if (!from) return null;
  const candidates = siblings(chart, id).flatMap((s) => {
    const box = boxes.get(s);
    return box ? [{ id: s, box }] : [];
  });
  return nearestInDirection(from, candidates, direction);
}

/** Enter: an open container's initial child (a parallel state's first region); null for a state that holds nothing drawn. */
export function enterTarget(chart: Chart, id: string, collapsed: ReadonlySet<string>): string | null {
  const s = chart.states.get(id);
  if (!s || !isOpen(s, collapsed)) return null;
  return s.initialChild ?? s.children[0] ?? null;
}

/** Escape: the parent state, or null at the root (the process itself: nothing selected). */
export function parentTarget(chart: Chart, id: string): string | null {
  return chart.states.get(id)?.parent ?? null;
}

/** The transitions leaving a state, in document order (drawn edges and the internal transitions alike). */
export function outgoing(chart: Chart, state: string): string[] {
  return [...chart.transitions.values()]
    .filter((t) => t.transition.source === state)
    .sort((a, b) => a.index - b.index)
    .map((t) => t.transition.id);
}

/** Tab and Shift+Tab: the next (`step` 1) or previous (-1) transition after `current`, wrapping; the first or last when `current` is not one of them. */
export function walk(list: readonly string[], current: string | null, step: 1 | -1): string | null {
  if (!list.length) return null;
  const at = current ? list.indexOf(current) : -1;
  if (at < 0) return step > 0 ? list[0] : list[list.length - 1];
  return list[(at + step + list.length) % list.length];
}
