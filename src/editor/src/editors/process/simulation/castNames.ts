// The cast shows each object's values by the attribute's name, not its id: a scenario file keys values by attribute id,
// so the names come from the object's entity and the entities it is based on. Pure, so the wording is tested alone.
import type { QueryClient } from "@tanstack/react-query";
import { elementQuery } from "@/api/queries";

interface EntityLike {
  base?: string | null;
  attributes?: { id: string; name: string; displayName?: string }[];
}

/** The name a person reads for an attribute: its display name when it has one, else its name. */
export const attributeLabel = (a: { name: string; displayName?: string }) => a.displayName || a.name;

/** Attribute id to label over some entity documents (the entity and its bases). */
export function attributeLabels(entities: readonly EntityLike[]): Map<string, string> {
  const labels = new Map<string, string>();
  for (const e of entities) for (const a of e.attributes ?? []) if (!labels.has(a.id)) labels.set(a.id, attributeLabel(a));
  return labels;
}

/** The entity, then its bases, as far as they can be read (a cycle or a missing base stops the walk). */
export async function entityChain(qc: QueryClient, entity: string): Promise<EntityLike[]> {
  const chain: EntityLike[] = [];
  const seen = new Set<string>();
  for (let id: string | null | undefined = entity; id && !seen.has(id);) {
    seen.add(id);
    try {
      const doc = (await qc.fetchQuery(elementQuery(id))).json as unknown as EntityLike;
      chain.push(doc);
      id = doc.base;
    } catch {
      break;
    }
  }
  return chain;
}

/** An object's values as "name value" pairs, by attribute name (the id stays when it is not found). */
export function castValues(values: Record<string, unknown> | undefined, labels: ReadonlyMap<string, string>, show: (v: unknown) => string): string {
  return Object.entries(values ?? {})
    .map(([k, v]) => `${labels.get(k) ?? k} ${show(v)}`)
    .join(", ");
}
