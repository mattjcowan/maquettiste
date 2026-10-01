// The Entities canvas model (phase2-design.md 4.8), kept free of React so it can be unit tested:
// which elements a view shows, where each card goes, how a relation edge attaches, how positions
// are written back into a diagram, and what "Add related to depth N" adds.
import type { AttributeDoc, DiagramDoc, DiagramMember, ElementSummary, RelationDoc } from "@/api/types";
import { indexPatchOf } from "@/api/indexPatch";
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
  ends?: readonly { entity: string }[];
}

/**
 * The entities and relations a view shows. A diagram shows its members in member order; a package
 * view shows every entity of the package and every relation (edges are later kept only when both
 * ends are on the canvas).
 */
/** "All of <domain>" (explorer-redesign.md 1.6) is offered for a domain of at most this many entities, and shows at most this many. */
export const ALL_OF_CAP = 300;

export function viewElements(
  view: CanvasView | null,
  rows: readonly IndexRow[],
  members: readonly DiagramMember[],
): { entityIds: string[]; relationIds: string[] } {
  if (!view) return { entityIds: [], relationIds: [] };
  if (view.type === "package") {
    const entityIds = rows
      .filter((r) => r.kind === "entity" && r.package === view.id)
      .map((r) => r.id)
      .slice(0, ALL_OF_CAP);
    const shown = new Set(entityIds);
    // A relation shows when both its ends are cards (the index row's `ends`); a row without ends (an older server) is
    // kept and the canvas drops an edge whose ends are not cards.
    const relationIds = rows.filter((r) => r.kind === "relation" && (!r.ends?.length || r.ends.every((e) => shown.has(e.entity)))).map((r) => r.id);
    return { entityIds, relationIds };
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

/**
 * True when the view has cards and none has a stored position: the full automatic layout runs on open. When only some
 * lack one, those are placed beside what is drawn (placement.ts) and the others stay where they are.
 */
export function needsLayout(
  view: CanvasView | null,
  entityIds: readonly string[],
  members: ReadonlyMap<string, DiagramMember>,
  packagePositions: Record<string, Point>,
): boolean {
  if (!view || !entityIds.length) return false;
  return entityIds.every((id) => storedPosition(view, members.get(id), packagePositions, id) === undefined);
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

/** The pill of a relation label: 11 px text at about 0.6 em per character, 8 px of padding each side, 22 px high. */
export const LABEL_TEXT_PX = 11;
export const LABEL_PADDING = 8;
export const LABEL_HEIGHT = 22;
/** The clear space the layout keeps between a label and the cards beside it. */
export const LABEL_GAP = 12;

/** The Display menu's attribute detail: every attribute, the key attributes only, or the names without types. */
export type DisplayMode = "all" | "keys" | "names";

/**
 * The Display menu's "Relation attributes" option: each relation's attributes drawn in a box hung off its label pill
 * (the UML association class), with the attribute detail of the cards and the type text the cards use.
 */
export interface AttributeBox {
  mode: DisplayMode;
  typeLabel: (attribute: AttributeDoc) => string;
}

/** The attribute box: 24 px rows (the cards' rows), a 24 px header with the relation's name, 2 px padding above and below
 * the rows, a 1 px border; between 120 px and a card's width wide. */
export const BOX_ROW = 24;
export const BOX_HEADER = 24;
export const BOX_MIN_WIDTH = 120;
export const BOX_MAX_WIDTH = NODE_WIDTH;
/** The space between the label pill and the box, which the dashed connector spans. */
export const BOX_GAP = 12;

/**
 * The attributes a relation's box lists. Keys only lists the key attributes, and a relation has no key of its own (its
 * ends identify it), so it lists none; All attributes and Names only list them all (Names only without their types).
 */
export function relationBoxRows(relation: Pick<RelationDoc, "attributes">, mode: DisplayMode): AttributeDoc[] {
  return mode === "keys" ? [] : [...(relation.attributes ?? [])];
}

/** The size of a relation's attribute box, or null when it would list nothing (no box is drawn, the pill keeps its badge). */
export function relationBoxSize(relation: Pick<RelationDoc, "name" | "attributes">, box: AttributeBox): { width: number; height: number } | null {
  const rows = relationBoxRows(relation, box.mode);
  if (!rows.length) return null;
  // The header: 12 px semibold text; a row: padding, the 16 px marker column, the name in 12 px, the type in 11 px monospace.
  const header = (relation.name ?? "").length * 12 * 0.62 + 2 * LABEL_PADDING;
  const row = (a: AttributeDoc) =>
    2 * LABEL_PADDING + 16 + 4 + (a.name ?? "").length * 12 * 0.6 + (box.mode === "names" ? 0 : 4 + box.typeLabel(a).length * LABEL_TEXT_PX * 0.6);
  const width = Math.ceil(Math.max(header, ...rows.map(row))) + 2;
  return { width: Math.min(BOX_MAX_WIDTH, Math.max(BOX_MIN_WIDTH, width)), height: 2 + BOX_HEADER + 4 + rows.length * BOX_ROW };
}

/**
 * The size of a relation's label from its text: the pill (the name, plus the attribute count when it has attributes and
 * no box lists them). With the attribute box, the pill and the box under it, as wide as the box on either side of the
 * line (where the label sits on an upright stretch the box moves beside it), so the layout makes room for both.
 */
export function relationLabelSize(relation: Pick<RelationDoc, "name" | "attributes">, box?: AttributeBox | null): { width: number; height: number } {
  const boxSize = box ? relationBoxSize(relation, box) : null;
  const badge = !boxSize && relation.attributes?.length ? 2 + String(relation.attributes.length).length : 0;
  const pill = { width: Math.ceil(((relation.name ?? "").length + badge) * LABEL_TEXT_PX * 0.6) + 2 * LABEL_PADDING, height: LABEL_HEIGHT };
  if (!boxSize) return pill;
  return { width: Math.max(pill.width, 2 * boxSize.width + BOX_GAP), height: pill.height + BOX_GAP + boxSize.height };
}

/** The layout input for a set of cards and edges, with measured sizes when React Flow has them, and each relation
 * edge's label size so the layout makes room for it (a label sitting on a card is unreadable on a large diagram); with
 * the attribute box on, the label is the pill and its box. */
export function layoutInput(
  nodes: readonly { id: string; measured?: { width?: number; height?: number } }[],
  edges: readonly {
    id: string;
    source: string;
    target: string;
    data?: { relation?: Pick<RelationDoc, "name" | "attributes">; attributeBox?: AttributeBox | null };
  }[],
): { nodes: LayoutNode[]; edges: LayoutEdge[] } {
  return {
    nodes: nodes.map((n) => ({ id: n.id, width: n.measured?.width || NODE_WIDTH, height: n.measured?.height || NODE_HEIGHT })),
    edges: edges.map((e) => {
      const relation = e.data?.relation;
      const label = relation ? relationLabelSize(relation, e.data?.attributeBox) : null;
      return {
        id: e.id,
        source: e.source,
        target: e.target,
        ...(label ? { label: { width: label.width + 2 * LABEL_GAP, height: label.height + LABEL_GAP } } : {}),
      };
    }),
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

// ------------------------------------------------------------------ the explorer and the canvas in step (EX 3.5, step 10)

/** The MIME type of explorer rows dragged onto the canvas: a JSON array of element ids. */
export const ELEMENTS_MIME = "application/x-maquettiste-elements";

/** Relations by entity and ends by relation, from the index (E5 relation ends): no document loads. */
export interface RelationLookup {
  relationsOf(entityId: string): readonly string[];
  endsOf(relationId: string): readonly string[] | undefined;
}

const relationLookups = new WeakMap<readonly unknown[], RelationLookup & { ends: Map<string, string[]> }>();

/**
 * Relation ends by relation, relations by entity, from the index rows; memoized per rows array. An array patched
 * from a memoized one without touching a relation (api/indexPatch.ts) keeps the previous lookup.
 */
export function relationLookup(rows: readonly Pick<IndexRow, "id" | "kind" | "ends">[]): RelationLookup {
  const cached = relationLookups.get(rows);
  if (cached) return cached;
  const patch = indexPatchOf(rows as readonly ElementSummary[]);
  const previous = patch ? relationLookups.get(patch.from) : undefined;
  if (patch && previous && !patch.upserts.some((u) => u.kind === "relation" || previous.ends.has(u.id)) && !patch.deleted.some((id) => previous.ends.has(id))) {
    relationLookups.set(rows, previous);
    return previous;
  }
  const lookup = buildRelationLookup(rows);
  relationLookups.set(rows, lookup);
  return lookup;
}

function buildRelationLookup(rows: readonly Pick<IndexRow, "id" | "kind" | "ends">[]): RelationLookup & { ends: Map<string, string[]> } {
  const relations = new Map<string, string[]>();
  const ends = new Map<string, string[]>();
  for (const r of rows) {
    if (r.kind !== "relation" || !r.ends) continue;
    const list = r.ends.map((e) => e.entity);
    ends.set(r.id, list);
    for (const e of new Set(list)) {
      const bucket = relations.get(e) ?? [];
      bucket.push(r.id);
      relations.set(e, bucket);
    }
  }
  return { relationsOf: (id) => relations.get(id) ?? [], endsOf: (id) => ends.get(id), ends };
}

/** The entities within `depth` relation hops of the start entities (the start entities included). */
export function relatedWithin(start: readonly string[], depth: number, lookup: RelationLookup): string[] {
  const seen = new Set(start);
  let frontier = [...start];
  for (let level = 0; level < depth && frontier.length; level++) {
    const next: string[] = [];
    for (const id of frontier)
      for (const rel of lookup.relationsOf(id))
        for (const end of lookup.endsOf(rel) ?? [])
          if (!seen.has(end)) {
            seen.add(end);
            next.push(end);
          }
    frontier = next;
  }
  return [...seen];
}

/**
 * The members to append to a diagram for entities and relations added from the explorer (drop, Add to diagram, Add
 * with related): the entities not yet on it, placed in a grid from `at` (or in columns right of the existing cards),
 * the relations asked for, and every relation whose ends are then all on the diagram.
 */
export function planMembers(input: {
  members: readonly DiagramMember[];
  entities: readonly string[];
  relations?: readonly string[];
  lookup: RelationLookup;
  at?: Point;
}): DiagramMember[] {
  const present = new Set(input.members.map((m) => m.element));
  const fresh = [...new Set(input.entities)].filter((id) => !present.has(id));
  let origin = input.at;
  if (!origin) {
    const placed = input.members.filter((m) => m.x !== undefined || m.y !== undefined);
    origin = placed.length ? { x: Math.max(...placed.map((m) => m.x ?? 0)) + GRID.x, y: Math.min(...placed.map((m) => m.y ?? 0)) } : { x: 0, y: 0 };
  }
  const columns = Math.max(1, Math.min(3, Math.ceil(Math.sqrt(fresh.length))));
  const out: DiagramMember[] = fresh.map((element, i) => ({
    element,
    x: Math.round(origin.x + (i % columns) * GRID.x),
    y: Math.round(origin.y + Math.floor(i / columns) * GRID.y),
  }));
  const onDiagram = new Set([...present, ...fresh]);
  const relations = new Set<string>();
  for (const r of input.relations ?? []) if (!present.has(r)) relations.add(r);
  for (const e of fresh)
    for (const r of input.lookup.relationsOf(e)) if (!present.has(r) && (input.lookup.endsOf(r) ?? []).every((x) => onDiagram.has(x))) relations.add(r);
  for (const r of relations) out.push({ element: r });
  return out;
}
