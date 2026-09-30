// The process diagram (phase-3-design.md 2.7 and 6.5), kept free of React so it can be unit tested: an ordinary
// diagram with `process` set whose members are the process's states. A member's `x` and `y` are relative to its parent
// state's box (the canvas's origin for a root state); compound and parallel states carry `width` and `height`;
// `collapsed` hides a container's children. One diagram per process, created the first time the chart is arranged.
import type { DiagramDoc, DiagramMember } from "@/api/types";

/** Where a state sits: relative to its parent's box; a container's size when it has one. */
export interface Placement {
  x: number;
  y: number;
  width?: number;
  height?: number;
}
export type Placements = Map<string, Placement>;

interface Row {
  id: string;
  kind: string;
  process?: string | null;
}

const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

/** The diagram of a process: the first, in ordinal id order, of the diagrams whose index row names the process. */
export function processDiagramId(rows: readonly Row[], process: string): string | null {
  return (
    rows
      .filter((r) => r.kind === "diagram" && r.process === process)
      .map((r) => r.id)
      .sort(ordinal)[0] ?? null
  );
}

/** True when an index row is a process's diagram (the Domain model canvas never shows one). */
export function isProcessDiagram(row: Row | undefined): boolean {
  return row?.kind === "diagram" && !!row.process;
}

/** The saved placements of the states a diagram positions (members with `x` or `y`); members of other elements are skipped. */
export function savedPlacements(diagram: Pick<DiagramDoc, "members"> | undefined, states: ReadonlySet<string>): Placements {
  const out: Placements = new Map();
  for (const m of diagram?.members ?? []) {
    if (!states.has(m.element) || (m.x === undefined && m.y === undefined)) continue;
    out.set(m.element, {
      x: m.x ?? 0,
      y: m.y ?? 0,
      ...(m.width !== undefined ? { width: m.width } : {}),
      ...(m.height !== undefined ? { height: m.height } : {}),
    });
  }
  return out;
}

/** The collapsed containers of a diagram. */
export function collapsedStates(diagram: Pick<DiagramDoc, "members"> | undefined): Set<string> {
  return new Set((diagram?.members ?? []).filter((m) => m.collapsed === true).map((m) => m.element));
}

/** A member in the schema's key order (element, x, y, width, height, collapsed), values rounded. */
function member(element: string, p: Placement | undefined, collapsed: boolean | undefined): DiagramMember {
  const m: DiagramMember = { element };
  if (p) {
    m.x = Math.round(p.x);
    m.y = Math.round(p.y);
    if (p.width !== undefined) m.width = Math.round(p.width);
    if (p.height !== undefined) m.height = Math.round(p.height);
  }
  if (collapsed) m.collapsed = true;
  return m;
}

/**
 * Writes placements into the diagram's members (in place): a listed state's `x` and `y`, and `width` and `height`
 * when the placement has them; a state without a member gains one at the end. Members not listed are untouched, and a
 * member's `collapsed` is kept. Returns how many members changed.
 */
export function writePlacements(diagram: Pick<DiagramDoc, "members">, placements: ReadonlyMap<string, Placement>): number {
  const members = (diagram.members ??= []);
  const at = new Map(members.map((m, i) => [m.element, i]));
  let changed = 0;
  for (const [id, p] of placements) {
    const i = at.get(id);
    const current = i === undefined ? undefined : members[i];
    const next = member(id, { x: p.x, y: p.y, width: p.width ?? current?.width, height: p.height ?? current?.height }, current?.collapsed);
    if (
      current &&
      current.x === next.x &&
      current.y === next.y &&
      current.width === next.width &&
      current.height === next.height &&
      (current.collapsed ?? false) === (next.collapsed ?? false)
    )
      continue;
    if (i === undefined) {
      at.set(id, members.length);
      members.push(next);
    } else members[i] = next;
    changed++;
  }
  return changed;
}

/** Collapses or expands a container (in place); a state without a member gains one with its placement. Returns true when it changed. */
export function setCollapsed(diagram: Pick<DiagramDoc, "members">, id: string, collapsed: boolean, placement?: Placement): boolean {
  const members = (diagram.members ??= []);
  const m = members.find((x) => x.element === id);
  if (!m) {
    if (!collapsed) return false;
    members.push(member(id, placement, true));
    return true;
  }
  if ((m.collapsed ?? false) === collapsed) return false;
  if (collapsed) m.collapsed = true;
  else delete m.collapsed;
  return true;
}

/** Removes the members of the given states (a deleted state and its subtree). Returns how many were removed. */
export function removeMembers(diagram: Pick<DiagramDoc, "members">, ids: ReadonlySet<string>): number {
  const before = diagram.members?.length ?? 0;
  if (!before) return 0;
  diagram.members = diagram.members!.filter((m) => !ids.has(m.element));
  return before - diagram.members.length;
}

/** The new diagram of a process: every state with its placement (in document order), the collapsed containers, the viewport. */
export function newProcessDiagram(input: {
  id: string;
  process: { id: string; name: string; displayName?: string; package?: string };
  order: readonly string[];
  placements: ReadonlyMap<string, Placement>;
  collapsed?: ReadonlySet<string>;
  viewport?: { x: number; y: number; zoom: number } | null;
}): DiagramDoc {
  return {
    kind: "diagram",
    id: input.id,
    name: input.process.name,
    ...(input.process.displayName ? { displayName: input.process.displayName } : {}),
    ...(input.process.package ? { package: input.process.package } : {}),
    process: input.process.id,
    members: input.order.map((id) => member(id, input.placements.get(id), input.collapsed?.has(id))),
    ...(input.viewport ? { viewport: input.viewport } : {}),
  } as DiagramDoc;
}
