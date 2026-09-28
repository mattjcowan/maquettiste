// The Entities canvas model (phase2-design.md 4.8), kept free of React so it can be unit tested:
// which elements a view shows, where each card goes, how a relation edge attaches, how positions
// are written back into a diagram, and what "Add related to depth N" adds.
import type { DiagramDoc, DiagramMember, RelationDoc } from "@/api/types";
import type { LayoutEdge, LayoutNode } from "./layout";

export const NODE_WIDTH = 240;
export const NODE_HEIGHT = 120;
/** The grid used for cards that have no stored position yet. */
export const GRID = { columns: 6, x: 280, y: 220 } as const;

export type CanvasView = { type: "diagram"; id: string } | { type: "package"; id: string };
export type Point = { x: number; y: number };

/** The active-diagram value in the store: a diagram id, or `pkg:<packageId>` for a package view. */
export function parseView(value: string | null | undefined): CanvasView | null {
  if (!value) return null;
  return value.startsWith("pkg:") ? { type: "package", id: value.slice(4) } : { type: "diagram", id: value };
}

export function viewKey(view: CanvasView | null): string {
  return view ? `${view.type}:${view.id}` : "none";
}

interface IndexRow {
  id: string;
  kind: string;
  package?: string | null;
}

/**
 * The entities and relations a view shows. A diagram shows its members in member order; a package
 * view shows every entity of the package and every relation (edges are later kept only when both
 * ends are on the canvas).
 */
export function viewElements(
  view: CanvasView | null,
  rows: readonly IndexRow[],
  members: readonly DiagramMember[],
): { entityIds: string[]; relationIds: string[] } {
  if (!view) return { entityIds: [], relationIds: [] };
  if (view.type === "package") {
    return {
      entityIds: rows.filter((r) => r.kind === "entity" && r.package === view.id).map((r) => r.id),
      relationIds: rows.filter((r) => r.kind === "relation").map((r) => r.id),
    };
  }
  const kinds = new Map(rows.map((r) => [r.id, r.kind]));
  const entityIds: string[] = [];
  const relationIds: string[] = [];
  const seen = new Set<string>();
  for (const m of members) {
    if (seen.has(m.element)) continue;
    seen.add(m.element);
    const kind = kinds.get(m.element);
    if (kind === "entity") entityIds.push(m.element);
    else if (kind === "relation") relationIds.push(m.element);
  }
  return { entityIds, relationIds };
}

/** The stored position of a card: the diagram member's x/y, or the per-browser package position. */
export function storedPosition(
  view: CanvasView | null,
  member: DiagramMember | undefined,
  packagePositions: Record<string, Point>,
  id: string,
): Point | undefined {
  if (view?.type === "package") return packagePositions[id];
  if (!member || (member.x === undefined && member.y === undefined)) return undefined;
  return { x: member.x ?? 0, y: member.y ?? 0 };
}

/** A card's position: while dragging the live one, else the stored one, else its grid slot. */
export function cardPosition(index: number, dragging: Point | undefined, stored: Point | undefined): Point {
  return dragging ?? stored ?? { x: (index % GRID.columns) * GRID.x, y: Math.floor(index / GRID.columns) * GRID.y };
}

/** True when the view has cards but none (diagram) or some (package) have a stored position: lay out on open. */
export function needsLayout(
  view: CanvasView | null,
  entityIds: readonly string[],
  members: ReadonlyMap<string, DiagramMember>,
  packagePositions: Record<string, Point>,
): boolean {
  if (!view || !entityIds.length) return false;
  if (view.type === "diagram") return entityIds.every((id) => storedPosition(view, members.get(id), packagePositions, id) === undefined);
  return entityIds.some((id) => !packagePositions[id]);
}

export interface EdgeEnds {
  source: string;
  target: string;
  sourceHandle: "r" | "sl";
  targetHandle: "l" | "tr";
}

/**
 * How a relation attaches: from its first end to its second, leaving the right side of the left
 * card and entering the left side of the right card (the hidden handles serve the reverse case).
 * Null when the relation lacks two ends or either end is not on the canvas.
 */
export function relationEnds(relation: Pick<RelationDoc, "ends">, positionOf: ReadonlyMap<string, Point>, width: number = NODE_WIDTH): EdgeEnds | null {
  const [a, b] = relation.ends ?? [];
  if (!a || !b) return null;
  const pa = positionOf.get(a.entity);
  const pb = positionOf.get(b.entity);
  if (!pa || !pb) return null;
  const forward = pa.x + width / 2 <= pb.x + width / 2;
  return { source: a.entity, target: b.entity, sourceHandle: forward ? "r" : "sl", targetHandle: forward ? "l" : "tr" };
}

/** Writes rounded positions into the diagram's members (in place); members not listed are untouched. */
export function applyPositions(diagram: Pick<DiagramDoc, "members">, positions: Record<string, Point>): number {
  let changed = 0;
  for (const m of diagram.members ?? []) {
    const p = positions[m.element];
    if (!p) continue;
    const x = Math.round(p.x);
    const y = Math.round(p.y);
    if (m.x === x && m.y === y) continue;
    m.x = x;
    m.y = y;
    changed++;
  }
  return changed;
}

/** The layout input for a set of cards and edges, with measured sizes when React Flow has them. */
export function layoutInput(
  nodes: readonly { id: string; measured?: { width?: number; height?: number } }[],
  edges: readonly { id: string; source: string; target: string }[],
): { nodes: LayoutNode[]; edges: LayoutEdge[] } {
  return {
    nodes: nodes.map((n) => ({ id: n.id, width: n.measured?.width || NODE_WIDTH, height: n.measured?.height || NODE_HEIGHT })),
    edges: edges.map((e) => ({ id: e.id, source: e.source, target: e.target })),
  };
}

export interface ExpandInput {
  /** The selected entities to start from. */
  start: readonly string[];
  depth: number;
  /** Elements already in the diagram. */
  present: ReadonlySet<string>;
  /** The relations that have the entity as an end. */
  relationsOf(entityId: string): Promise<readonly Pick<RelationDoc, "id" | "ends">[]>;
  /** Where the first start entity sits; new cards go in columns to its right, one per level. */
  origin: Point;
}

/**
 * "Add related to depth N": a breadth-first walk over relations from the start entities. Each level
 * adds the relations it crosses and the entities at their other ends (new entities get a position
 * in a column 320 px further right per level); entities already present are walked through but not
 * added again.
 */
export async function expandRelated(input: ExpandInput): Promise<DiagramMember[]> {
  const present = new Set(input.present);
  const seen = new Set(input.start);
  const added: DiagramMember[] = [];
  let frontier = [...input.start];
  for (let level = 1; level <= input.depth && frontier.length; level++) {
    const next: string[] = [];
    let row = 0;
    for (const entityId of frontier) {
      for (const relation of await input.relationsOf(entityId)) {
        if (!present.has(relation.id)) {
          present.add(relation.id);
          added.push({ element: relation.id });
        }
        for (const end of relation.ends ?? []) {
          if (seen.has(end.entity)) continue;
          seen.add(end.entity);
          next.push(end.entity);
          if (present.has(end.entity)) continue;
          present.add(end.entity);
          added.push({ element: end.entity, x: Math.round(input.origin.x + 320 * level), y: Math.round(input.origin.y + 180 * row++) });
        }
      }
    }
    frontier = next;
  }
  return added;
}

/** The ids of the elements selected on the canvas that are entities, in selection order. */
export function selectedEntities(selection: readonly string[], kindOf: (id: string) => string | undefined): string[] {
  return selection.filter((id) => kindOf(id) === "entity");
}

/**
 * Applies React Flow `select` changes (nodes or edges) to the current selection. The canvases are
 * controlled, so a click or Enter on a card only reaches the store through these changes. Returns
 * null when nothing changed.
 */
export function applySelectChanges(current: readonly string[], changes: readonly { type: string; id?: string; selected?: boolean }[]): string[] | null {
  const next = [...current];
  let changed = false;
  for (const change of changes) {
    if (change.type !== "select" || !change.id) continue;
    const at = next.indexOf(change.id);
    if (change.selected && at < 0) {
      next.push(change.id);
      changed = true;
    } else if (!change.selected && at >= 0) {
      next.splice(at, 1);
      changed = true;
    }
  }
  return changed ? next : null;
}
