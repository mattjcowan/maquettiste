/**
 * What a relation end covers (several-types-design.md decisions 2, 4 and 5, section 11). One place answers "the entities this
 * end covers" and "the single entity or null" for index rows, so no reader asks `end.entity` and trips over an end with
 * several types. A one-type end behaves exactly as it did before: it has `entity` and nothing else.
 */
import type { ElementSummary } from "@/api/types";

/** An index row's end, as the contract writes it. */
export type EndSummary = NonNullable<ElementSummary["ends"]>[number];

/** An end as the model index and the relation document write it; every member but `role` is optional. */
export interface EndLike {
  entity?: string | undefined;
  role?: string | undefined;
  /** The ids of the entity entries of an end that writes `types` (index rows). */
  entities?: readonly string[] | undefined;
  /** The ids of the stereotypes the end's `types` entries name (index rows). */
  stereotypes?: readonly string[] | undefined;
  anyEntity?: boolean | undefined;
  /** The ids of the entities that carry the stereotypes the end names (index rows). */
  covers?: readonly string[] | undefined;
}

/** Whether the end is one written with `anyEntity`, or with several entries or a stereotype entry (decision 2). Reads the row only. */
export function endHasSeveralTypes(end: EndLike): boolean {
  return !end.entity && (end.anyEntity === true || (end.entities?.length ?? 0) + (end.stereotypes?.length ?? 0) > 0);
}

/** The one entity of a one-type end, else null. An end with no `entity` and no list (an older server's row) gives null. */
export function endSingleEntity(end: EndLike): string | null {
  return end.entity || null;
}

/** The entities the end lists by id: its `entity`, or its entity entries in entry order. Derived entities and carriers are not included. */
export function endListedEntities(end: EndLike): string[] {
  if (end.entity) return [end.entity];
  return [...(end.entities ?? [])];
}

/**
 * The entities the end covers by what the index row holds: the listed entities and the carriers of its stereotype entries. A derived
 * entity is not listed (it is reached through its base; pass `baseOf` to `endCoversEntity` to include one). `anyEntity` covers
 * everything, so it lists nothing here.
 */
export function endCoveredEntities(end: EndLike): string[] {
  return [...new Set([...endListedEntities(end), ...(end.covers ?? [])])];
}

/**
 * Whether the end covers the entity or one of its bases (`baseOf` gives an entity's base id). An `anyEntity` end covers every entity.
 * A stereotype entry covers its carriers through `covers`, which holds them.
 */
export function endCoversEntity(end: EndLike, entityId: string, baseOf?: (id: string) => string | undefined): boolean {
  if (end.anyEntity) return true;
  const covered = new Set(endCoveredEntities(end));
  if (covered.size === 0) return false;
  const seen = new Set<string>();
  for (let id: string | undefined = entityId; id && !seen.has(id); id = baseOf?.(id)) {
    if (covered.has(id)) return true;
    seen.add(id);
  }
  return false;
}

/** Whether the end holds an `entity` or an entity entry of this id, as the document wrote it, with no derivation. */
export function endNamesEntity(end: EndLike, entityId: string): boolean {
  return endListedEntities(end).includes(entityId);
}

/**
 * The end's label for explorer and list text: the entity's name, or its entries' names joined as "Part, Assembly or approvable
 * (stereotype)", or "any entity". `nameOf` gives an element's name; `stereotypeNameOf` a stereotype id's key.
 */
export function endLabel(end: EndLike, nameOf: (id: string) => string | undefined, stereotypeNameOf: (id: string) => string | undefined = nameOf): string {
  if (end.entity) return nameOf(end.entity) ?? end.entity;
  if (end.anyEntity) return "any entity";
  const parts = [...(end.entities ?? []).map((id) => nameOf(id) ?? id), ...(end.stereotypes ?? []).map((id) => `${stereotypeNameOf(id) ?? id} (stereotype)`)];
  if (parts.length <= 1) return parts[0] ?? "";
  return `${parts.slice(0, -1).join(", ")} or ${parts[parts.length - 1]}`;
}

/** The text a related list shows for how an entity is reached through the end: "through stereotype approvable", "any entity", or "". */
export function endReach(end: EndLike, entityId: string, nameOfStereotype: (id: string) => string | undefined): string {
  if (end.anyEntity) return "any entity";
  if (end.entity || endNamesEntity(end, entityId)) return "";
  const via = (end.stereotypes ?? []).map((id) => nameOfStereotype(id) ?? id);
  return via.length ? `through stereotype ${via.join(", ")}` : "";
}

/** An end as the relation document writes it: exactly one of `entity`, `types` or `anyEntity`. */
export interface EndDocLike {
  entity?: string | undefined;
  types?: readonly { entity?: string | undefined; stereotype?: string | undefined }[] | undefined;
  anyEntity?: boolean | undefined;
}

/**
 * The one entity of a document end that is a one-type end: `entity`, or a `types` list that holds a single entity entry (the same
 * meaning, decision 2). Null for an end with several types.
 */
export function docEndSingleEntity(end: EndDocLike): string | null {
  if (end.entity) return end.entity;
  if (end.anyEntity) return null;
  const types = end.types ?? [];
  return types.length === 1 && types[0]?.entity ? types[0].entity : null;
}

/** Whether a document end is an end with several types (decision 2): `anyEntity`, or `types` with two entries or a stereotype entry. */
export function docEndHasSeveralTypes(end: EndDocLike): boolean {
  return !end.entity && (end.anyEntity === true || ((end.types?.length ?? 0) > 0 && docEndSingleEntity(end) === null));
}

/**
 * The index row's summary of a document end, as the engine writes it (RelationEndSummary): `entity` on a one-type end; `entities`,
 * `stereotypes` (ids, an unknown key left out) and `anyEntity` on an end with several types. `covers` is left to the next index read,
 * since carriers depend on package defaults the document alone doesn't show.
 */
export function endSummaryOf(end: Record<string, unknown>, stereotypeIds?: ReadonlyMap<string, string>): EndSummary {
  const role = String(end.role ?? "");
  const doc = end as EndDocLike;
  const single = docEndSingleEntity(doc);
  if (single) return { entity: single, role };
  if (doc.anyEntity === true) return { role, anyEntity: true as const };
  const types = doc.types ?? [];
  if (types.length === 0) return { entity: String(end.entity ?? ""), role };
  const entities = types.flatMap((t) => t.entity ?? []);
  const stereotypes = types.flatMap((t) => (t.stereotype ? (stereotypeIds?.get(t.stereotype) ?? []) : []));
  return { role, ...(entities.length ? { entities } : {}), ...(stereotypes.length ? { stereotypes } : {}) };
}

/** The entity ids a document end lists (its `entity`, or its entity entries in order). */
export function docEndEntities(end: EndDocLike): string[] {
  if (end.entity) return [end.entity];
  return (end.types ?? []).flatMap((t) => t.entity ?? []);
}
