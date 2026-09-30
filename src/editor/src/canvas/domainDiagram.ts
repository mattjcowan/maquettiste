// A domain's own diagram (the precedent of phase-3-design.md 2 for processes: "one diagram per process is created the
// first time the chart is laid out and saved"). "All of <domain>" draws every entity of a domain; the first time the
// user arranges it (drags a card, runs Auto-layout, pans or zooms) a diagram is created for the domain with membership
// "package", and from then on that diagram is the domain's canvas: its members follow the domain's entities. The
// membership decides it, never a name (renaming the domain or the diagram changes nothing). Kept free of React so it
// can be unit tested.
import type { DiagramDoc, DiagramMember } from "@/api/types";
import { viewElements, type Point } from "./model";

interface Row {
  id: string;
  kind: string;
  name?: string;
  package?: string | null;
  ends?: readonly { entity: string }[];
}

const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

/** The diagrams in a domain, in ordinal id order: the candidates for its canvas (their documents say which follows it). */
export function diagramsInDomain(rows: readonly Row[], packageId: string): string[] {
  return rows
    .filter((r) => r.kind === "diagram" && r.package === packageId)
    .map((r) => r.id)
    .sort(ordinal);
}

/** The domain's diagram: the first, in ordinal id order, of the domain's diagrams whose membership is "package". */
export function domainDiagramId(diagrams: readonly Pick<DiagramDoc, "id" | "package" | "membership">[], packageId: string): string | null {
  return (
    diagrams
      .filter((d) => d.package === packageId && d.membership === "package")
      .map((d) => d.id)
      .sort(ordinal)[0] ?? null
  );
}

/** The domain a diagram follows (membership "package"), or null when its members list is the diagram. */
export function domainOfDiagram(diagram: Pick<DiagramDoc, "package" | "membership"> | undefined): string | null {
  return diagram?.membership === "package" && diagram.package ? diagram.package : null;
}

/**
 * The members a domain's diagram should have: the domain's entities (as "All of <domain>" shows them) and the
 * relationships between them. Members still wanted keep their order, positions and flags; entities and relationships
 * that joined come last without a position (placed on the canvas); those that left are dropped. Null when nothing changes.
 */
export function syncDomainMembers(members: readonly DiagramMember[], rows: readonly Row[], packageId: string): DiagramMember[] | null {
  const { entityIds, relationIds } = viewElements({ type: "package", id: packageId }, rows, []);
  const wanted = new Set([...entityIds, ...relationIds]);
  const kept = members.filter((m) => wanted.has(m.element));
  const have = new Set(kept.map((m) => m.element));
  const added = [...entityIds, ...relationIds].filter((id) => !have.has(id)).map((element) => ({ element }));
  if (!added.length && kept.length === members.length) return null;
  return [...kept, ...added];
}

/** The new diagram for a domain: its entities with the positions they are drawn at, the relationships between them and the viewport. */
export function newDomainDiagram(input: {
  id: string;
  domain: { id: string; name: string; displayName?: string };
  rows: readonly Row[];
  positions: Readonly<Record<string, Point>>;
  viewport?: { x: number; y: number; zoom: number } | null;
}): DiagramDoc {
  const members = (syncDomainMembers([], input.rows, input.domain.id) ?? []).map((m) => {
    const p = input.positions[m.element];
    return p ? { element: m.element, x: Math.round(p.x), y: Math.round(p.y) } : m;
  });
  return {
    kind: "diagram",
    id: input.id,
    name: input.domain.name,
    ...(input.domain.displayName ? { displayName: input.domain.displayName } : {}),
    package: input.domain.id,
    membership: "package",
    members,
    ...(input.viewport ? { viewport: input.viewport } : {}),
  } as DiagramDoc;
}
