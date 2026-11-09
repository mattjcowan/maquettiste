// How a relation with an end of several types is drawn (several-types-design.md section 11, the owner's three looks), kept free
// of React so it can be unit tested. One end of the relation names one entity (the near end, a card); the other end (the far end)
// has an end with several types. The look depends on the far end's entries:
//   box       one stereotype entry, or any entity: one line from the near card to a box
//   bundle    two entity entries, both cards on the canvas: one line with the label that forks into a line to each card
//   junction  three or more entries, or two where one is a stereotype or not a card: one line to a node, one from it to each type
//   list      the toggle's collapsed look: one line to a box that lists every entry
// A diagram that has no such relation never reaches this file, so it draws and saves exactly as before.
import type { DiagramDoc, RelationDoc } from "@/api/types";
import { docEndHasSeveralTypes, docEndSingleEntity, type EndDocLike } from "@/model/endTypes";
import { crowsFoot, multiplicity, relationKind } from "@/model/model";
import { NODE_HEIGHT, NODE_WIDTH, type Point } from "./model";

export type SeveralLook = "box" | "bundle" | "junction" | "list";

/** One entry of the far end, in the order the file lists them. */
export interface PlanEntry {
  kind: "entity" | "stereotype" | "any";
  /** The entity's id, for an entity entry. */
  entity?: string;
  /** The stereotype's key, for a stereotype entry. */
  stereotype?: string;
  /** The text the entry shows: the entity's name, "controlledDocument (stereotype)" or "Any entity". */
  label: string;
  /** How many entities a stereotype entry covers (when the index says). */
  count?: number;
  /** Whether the entry can be drawn: an entity entry's card is on the canvas; a stereotype and any entity always draw as a box. */
  shown: boolean;
}

export interface SeveralPlan {
  relationId: string;
  /** Index of the end that names one entity (a card), and of the end with several types. */
  nearIndex: 0 | 1;
  farIndex: 0 | 1;
  nearEntity: string;
  look: SeveralLook;
  entries: PlanEntry[];
  /** Entries that get a line (a card or a box). */
  shown: PlanEntry[];
  /** Entries with no line because their card is not on the canvas. */
  hidden: PlanEntry[];
  /** The toggle is offered when the end has two or more entries. */
  canToggle: boolean;
  collapsed: boolean;
}

export interface PlanContext {
  /** Whether the entity's card is on the canvas. */
  onCanvas(entityId: string): boolean;
  nameOf(entityId: string): string | undefined;
  /** The number of entities each stereotype entry covers, by key, when the index can say. */
  stereotypeCount?(key: string): number | undefined;
  collapsed: boolean;
}

/** The node ids a plan draws: the hub (junction, box, list or fork) has the relation's id, so a stored position on the
 * relation's diagram member places it; a stereotype box in a junction look takes a suffixed id. */
export const HUB_SEPARATOR = "~";
export const boxNodeId = (relationId: string, index: number) => `${relationId}${HUB_SEPARATOR}s${index}`;
export const branchEdgeId = (relationId: string, index: number) => `${relationId}${HUB_SEPARATOR}${index}`;
/** The relation a node or edge id of this drawing belongs to. */
export const relationOfFlowId = (id: string) => (id.includes(HUB_SEPARATOR) ? id.slice(0, id.indexOf(HUB_SEPARATOR)) : id);

/** The entries of an end with several types, from the document. */
export function endEntries(end: EndDocLike, ctx: Pick<PlanContext, "onCanvas" | "nameOf" | "stereotypeCount">): PlanEntry[] {
  if (end.anyEntity) return [{ kind: "any", label: "Any entity", shown: true }];
  return (end.types ?? []).map((t): PlanEntry => {
    if (t.stereotype) {
      const count = ctx.stereotypeCount?.(t.stereotype);
      return { kind: "stereotype", stereotype: t.stereotype, label: `${t.stereotype} (stereotype)`, ...(count !== undefined ? { count } : {}), shown: true };
    }
    const id = t.entity ?? "";
    return { kind: "entity", entity: id, label: ctx.nameOf(id) ?? id, shown: ctx.onCanvas(id) };
  });
}

/**
 * The look for a relation, or null when it is not drawn this way: it has no end with several types (the ordinary drawing), or
 * its other end's card is not on the canvas. Only the first two ends count, as in the ordinary drawing.
 */
export function planSeveralTypes(relation: Pick<RelationDoc, "id" | "ends">, ctx: PlanContext): SeveralPlan | null {
  const [a, b] = relation.ends ?? [];
  if (!a || !b) return null;
  const aMany = docEndHasSeveralTypes(a as EndDocLike);
  const bMany = docEndHasSeveralTypes(b as EndDocLike);
  if (aMany === bMany) return null;
  const farIndex = aMany ? 0 : 1;
  const nearIndex = aMany ? 1 : 0;
  const near = (nearIndex === 0 ? a : b) as EndDocLike;
  const far = (farIndex === 0 ? a : b) as EndDocLike;
  const nearEntity = docEndSingleEntity(near);
  if (!nearEntity || !ctx.onCanvas(nearEntity)) return null;
  const entries = endEntries(far, ctx);
  const canToggle = entries.length >= 2;
  const collapsed = canToggle && ctx.collapsed;
  let look: SeveralLook;
  if (entries.length < 2) look = "box";
  else if (collapsed) look = "list";
  else if (entries.length === 2 && entries.every((e) => e.kind === "entity" && e.shown)) look = "bundle";
  else look = "junction";
  return {
    relationId: relation.id,
    nearIndex,
    farIndex,
    nearEntity,
    look,
    entries,
    shown: look === "list" || look === "box" ? [] : entries.filter((e) => e.shown),
    hidden: look === "list" || look === "box" ? [] : entries.filter((e) => !e.shown),
    canToggle,
    collapsed,
  };
}

/** The "+N not shown" text of a node or box, and the names for its tooltip; null when every entry is drawn. */
export function notShown(plan: Pick<SeveralPlan, "hidden">): { text: string; names: string } | null {
  if (!plan.hidden.length) return null;
  return { text: `+${plan.hidden.length} not shown`, names: plan.hidden.map((e) => e.label).join(", ") };
}

/** The tooltip of the toggle: what pressing it does. */
export const toggleLabel = (collapsed: boolean): string => (collapsed ? "Expand lines" : "Collapse lines");

// ------------------------------------------------------------------ the graph the canvas draws

export const JUNCTION_SIZE = 16;
export const FORK_SIZE = 8;
export const BOX_WIDTH = 190;
export const BOX_HEIGHT = 46;
const LIST_ROW = 18;

export type HubKind = "junction" | "fork" | "stereotype" | "any" | "list";

export interface GraphNode {
  id: string;
  kind: HubKind;
  position: Point;
  width: number;
  height: number;
  /** The nodes the person drags: the hub of a junction, box or list. The fork and a junction's stereotype boxes follow. */
  draggable: boolean;
  entry?: PlanEntry;
  entries?: PlanEntry[];
  count?: number;
}

export interface GraphEdge {
  id: string;
  source: string;
  target: string;
  sourceHandle: "r" | "sl";
  targetHandle: "l" | "tr";
  part: "trunk" | "branch";
  /** Which end's role and cardinality the edge shows at its card, and where. */
  endIndex: 0 | 1;
  /** For the one line into a box or list, which has no branches: the far end, whose role and cardinality show at the box. */
  farEndIndex?: 0 | 1;
  /** The relation's label (its name pill) sits on the trunk only. */
  labelled: boolean;
}

export interface SeveralGraph {
  nodes: GraphNode[];
  edges: GraphEdge[];
}

const centreOf = (p: Point, w: number, h: number): Point => ({ x: p.x + w / 2, y: p.y + h / 2 });

/** The handles for a line from a node centred at `fromX` to one centred at `toX`, as the ordinary edges choose them. */
export function handlesFor(fromX: number, toX: number): Pick<GraphEdge, "sourceHandle" | "targetHandle"> {
  const forward = fromX <= toX;
  return { sourceHandle: forward ? "r" : "sl", targetHandle: forward ? "l" : "tr" };
}

/** The size of a plan's hub node. */
export function hubSize(plan: SeveralPlan): { kind: HubKind; width: number; height: number } {
  if (plan.look === "bundle") return { kind: "fork", width: FORK_SIZE, height: FORK_SIZE };
  if (plan.look === "junction") return { kind: "junction", width: JUNCTION_SIZE, height: JUNCTION_SIZE };
  if (plan.look === "list") return { kind: "list", width: BOX_WIDTH, height: 28 + plan.entries.length * LIST_ROW };
  const entry = plan.entries[0];
  return { kind: entry?.kind === "any" ? "any" : "stereotype", width: BOX_WIDTH, height: BOX_HEIGHT };
}

/**
 * Where the hub sits with no saved position: a junction or fork midway between the near card and the cards it reaches; a box
 * or list beside the near card, on the side the types are (right when there are none). `cards` are the type cards' positions. `slot`
 * is the place of this relation among those drawn from the same card: each takes a step lower, so their nodes do not sit on one another.
 */
export function defaultHubPosition(plan: SeveralPlan, near: Point, cards: readonly Point[], slot = 0, nearSize?: { width: number; height: number }): Point {
  const size = hubSize(plan);
  // The near card's measured size when the canvas knows it: its handles are at the middle of what is drawn, not of the nominal card.
  const nc = centreOf(near, nearSize?.width || NODE_WIDTH, nearSize?.height || NODE_HEIGHT);
  const centroid = cards.length
    ? {
        x: cards.reduce((s, p) => s + p.x + NODE_WIDTH / 2, 0) / cards.length,
        y: cards.reduce((s, p) => s + p.y + NODE_HEIGHT / 2, 0) / cards.length,
      }
    : null;
  if (plan.look === "junction" || plan.look === "bundle") {
    // Midway in x, and at the near card's own height, so the trunk leaves the card's handle and runs straight through its label.
    const c = centroid ? { x: (nc.x + centroid.x) / 2, y: nc.y } : { x: nc.x + NODE_WIDTH / 2 + 90, y: nc.y };
    return { x: Math.round(c.x - size.width / 2), y: Math.round(c.y - size.height / 2 + slot * SLOT_STEP) };
  }
  const dir = centroid && centroid.x < nc.x ? -1 : 1;
  const x = dir > 0 ? near.x + NODE_WIDTH + 100 : near.x - 100 - size.width;
  return { x: Math.round(x), y: Math.round(nc.y - size.height / 2 + slot * SLOT_STEP) };
}

/** How far each further relation from one card sits below the one before, with no saved position. */
export const SLOT_STEP = 64;

export interface GraphInput {
  /** Positions of the cards on the canvas, by entity id. */
  positionOf: ReadonlyMap<string, Point>;
  /** The hub's position when one is stored or being dragged. */
  hub?: Point | undefined;
  /** Where this relation falls among those drawn from the same near card (0 for the first). */
  slot?: number;
  /** The measured sizes of the cards, by entity id, when the canvas has them. */
  sizeOf?: (entityId: string) => { width: number; height: number } | undefined;
}

/** The nodes and edges the canvas draws for a plan. */
export function buildGraph(plan: SeveralPlan, input: GraphInput): SeveralGraph {
  const near = input.positionOf.get(plan.nearEntity);
  if (!near) return { nodes: [], edges: [] };
  // A list or box draws no line to the cards, but sits on the side of the cards it stands for.
  const cardEntries = (plan.look === "list" || plan.look === "box" ? plan.entries : plan.shown).filter(
    (e) => e.kind === "entity" && e.entity && input.positionOf.has(e.entity),
  );
  const cardPositions = cardEntries.map((e) => input.positionOf.get(e.entity!)!);
  const size = hubSize(plan);
  // The fork of a bundle is placed by the cards and never saved: a stored position (a hub the person dragged before the look became a bundle) is ignored.
  const hubAt =
    (plan.look === "bundle" ? undefined : input.hub) ?? defaultHubPosition(plan, near, cardPositions, input.slot ?? 0, input.sizeOf?.(plan.nearEntity));
  const hub: GraphNode = {
    id: plan.relationId,
    kind: size.kind,
    position: hubAt,
    width: size.width,
    height: size.height,
    draggable: plan.look !== "bundle",
    ...(plan.look === "list" ? { entries: plan.entries } : plan.look === "box" ? { entry: plan.entries[0] } : {}),
    ...(plan.look === "box" && plan.entries[0]?.count !== undefined ? { count: plan.entries[0].count } : {}),
  };
  const nodes: GraphNode[] = [hub];
  const hubCentre = centreOf(hubAt, size.width, size.height);
  const nearCentreX = near.x + (input.sizeOf?.(plan.nearEntity)?.width || NODE_WIDTH) / 2;
  const edges: GraphEdge[] = [
    {
      id: plan.relationId,
      source: plan.nearEntity,
      target: hub.id,
      ...handlesFor(nearCentreX, hubCentre.x),
      part: "trunk",
      endIndex: plan.nearIndex,
      ...(plan.look === "box" || plan.look === "list" ? { farEndIndex: plan.farIndex } : {}),
      labelled: true,
    },
  ];
  if (plan.look === "box" || plan.look === "list") return { nodes, edges };

  // The boxes for the stereotype entries stack on the far side of the hub, away from the near card.
  const boxes = plan.entries.map((e, i) => ({ e, i })).filter(({ e }) => e.kind === "stereotype");
  const dir = hubCentre.x >= nearCentreX ? 1 : -1;
  const boxNodes = new Map<string, GraphNode>();
  boxes.forEach(({ e, i }, n) => {
    const x = dir > 0 ? hubAt.x + size.width + 110 : hubAt.x - 110 - BOX_WIDTH;
    const y = hubCentre.y + (n - (boxes.length - 1) / 2) * (BOX_HEIGHT + 14) - BOX_HEIGHT / 2;
    const node: GraphNode = {
      id: boxNodeId(plan.relationId, i),
      kind: "stereotype",
      position: { x: Math.round(x), y: Math.round(y) },
      width: BOX_WIDTH,
      height: BOX_HEIGHT,
      draggable: false,
      entry: e,
      ...(e.count !== undefined ? { count: e.count } : {}),
    };
    boxNodes.set(node.id, node);
    nodes.push(node);
  });
  plan.entries.forEach((e, i) => {
    if (!e.shown) return;
    let target: string;
    let targetCentreX: number;
    if (e.kind === "stereotype") {
      const node = boxNodes.get(boxNodeId(plan.relationId, i))!;
      target = node.id;
      targetCentreX = node.position.x + node.width / 2;
    } else {
      const p = input.positionOf.get(e.entity!);
      if (!p) return;
      target = e.entity!;
      targetCentreX = p.x + NODE_WIDTH / 2;
    }
    edges.push({
      id: branchEdgeId(plan.relationId, i),
      source: hub.id,
      target,
      ...handlesFor(hubCentre.x, targetCentreX),
      part: "branch",
      endIndex: plan.farIndex,
      labelled: false,
    });
  });
  return { nodes, edges };
}

// ------------------------------------------------------------------ what each line shows

export type MarkerName = string | undefined;

/**
 * The markers of one line, in today's notation. The owner or whole end of a composition or aggregation (the relation's first end)
 * keeps its diamond; in crow's foot a line shows its end's cardinality at its card. A line to a node carries nothing at the node.
 */
export function lineMarkers(
  relation: Pick<RelationDoc, "ends" | "relationKind">,
  notation: "uml" | "crowsfoot",
  edge: { part: "trunk" | "branch"; endIndex: 0 | 1; farEndIndex?: 0 | 1 | undefined },
): { start: MarkerName; end: MarkerName } {
  const kind = relationKind(relation as RelationDoc);
  const diamond = kind === "composition" ? "url(#mq-diamond-filled)" : kind === "aggregation" ? "url(#mq-diamond-hollow)" : undefined;
  const markerOf = (index: 0 | 1): MarkerName => {
    const end = relation.ends?.[index];
    const foot = notation === "crowsfoot" && end ? `url(#mq-cf-${crowsFoot(end)})` : undefined;
    return index === 0 && diamond ? diamond : foot;
  };
  const marker = markerOf(edge.endIndex);
  // A trunk starts at the near card (and, into a box, ends at the box with the far end's marker); a branch ends at a type's card.
  if (edge.part === "trunk") return { start: marker, end: edge.farEndIndex === undefined ? undefined : markerOf(edge.farEndIndex) };
  return { start: undefined, end: marker };
}

/** The text at a line's card: the end's role, and in UML its multiplicity ("attachedTo 1"); in crow's foot the marker says the cardinality. */
export function lineText(relation: Pick<RelationDoc, "ends">, notation: "uml" | "crowsfoot", endIndex: 0 | 1): string {
  const end = relation.ends?.[endIndex];
  if (!end) return "";
  return notation === "uml" ? [end.role, multiplicity(end)].filter(Boolean).join(" ") : (end.role ?? "");
}

// ------------------------------------------------------------------ the toggle

/**
 * Flips `collapsed` on the diagram's member for the relation, in place (the existing diagram save path writes it). The flag is left
 * out when false, so a diagram that never collapses a relation keeps its file. Returns the new value, or null when the diagram has no such member.
 */
export function toggleMemberCollapsed(diagram: Pick<DiagramDoc, "members">, id: string): boolean | null {
  const m = diagram.members?.find((x) => x.element === id);
  if (!m) return null;
  if (m.collapsed) delete m.collapsed;
  else m.collapsed = true;
  return m.collapsed === true;
}

/**
 * How many entities each stereotype entry of a relation's end with several types covers. The index row's `covers` lists the carriers of
 * all the end's stereotypes together: with one stereotype entry that is its count; with several, an entity counts for the entry whose key
 * is among its own stereotypes (the row carries an entity's own keys, not package defaults), so the count can fall short.
 */
export function stereotypeCounter(
  relation: Pick<RelationDoc, "ends">,
  row: { ends?: readonly { covers?: readonly string[] | undefined }[] | undefined } | undefined,
  stereotypesOf: (entityId: string) => readonly string[] | undefined,
): (key: string) => number | undefined {
  const farIndex = relation.ends?.findIndex((e) => docEndHasSeveralTypes(e as EndDocLike)) ?? -1;
  const covers = farIndex >= 0 ? row?.ends?.[farIndex]?.covers : undefined;
  // The engine leaves an empty `covers` out of the row, so a known row without it means no carriers.
  if (!covers) return row ? () => 0 : () => undefined;
  const keys = ((relation.ends?.[farIndex] as EndDocLike | undefined)?.types ?? []).flatMap((t) => t.stereotype ?? []);
  if (keys.length <= 1) return () => covers.length;
  return (key) => covers.filter((id) => stereotypesOf(id)?.includes(key)).length;
}

/** Whether an index row's relation has an end with several types, so the canvas treats it as one of these. */
export function hasSeveralTypesEnd(relation: Pick<RelationDoc, "ends">): boolean {
  const [a, b] = relation.ends ?? [];
  return !!a && !!b && (docEndHasSeveralTypes(a as EndDocLike) || docEndHasSeveralTypes(b as EndDocLike));
}
