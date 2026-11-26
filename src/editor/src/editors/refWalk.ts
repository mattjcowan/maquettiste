// Walks from one entity through what references it, one element at a time (the where-used call), so a screen never loads or scans the
// whole model: the relations that touch an entity, the entities derived from it, the lifecycles that name it.
import type { QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";
import type { ReferenceInfo } from "@/api/types";

/** The most entities, relations and levels one walk visits: a screen says so when a walk is cut. */
export const WALK_LIMITS = { entities: 200, relations: 500, depth: 12 } as const;

/** A where-used reader for one walk: each element is asked once (fresh from the server, kept in the cache the Where used tab shares). */
export function referenceReader(qc: QueryClient): (id: string) => Promise<ReferenceInfo[]> {
  const asked = new Map<string, Promise<ReferenceInfo[]>>();
  return (id) => {
    let p = asked.get(id);
    if (!p) asked.set(id, (p = qc.fetchQuery({ queryKey: keys.references(id), queryFn: () => endpoints.getReferences(id), staleTime: 0 })));
    return p;
  };
}

/** The ids of the top-level elements of a kind that reference an element, in the order first seen. `kindOf` reads the index row by id. */
export function referrersOf(refs: readonly ReferenceInfo[], kindOf: (id: string) => string | undefined, kind: string, field?: string): string[] {
  const out: string[] = [];
  for (const r of refs)
    if (kindOf(r.fromElementId) === kind && (!field || r.field === field || r.jsonPointer === `/${field}`) && !out.includes(r.fromElementId))
      out.push(r.fromElementId);
  return out;
}

/**
 * The entities derived from an entity at any depth, found level by level (the calls of one level run together) through the entities whose
 * `base` references each. `truncated` when the limits cut the walk.
 */
export async function derivedOf(qc: QueryClient, id: string, kindOf: (id: string) => string | undefined): Promise<{ ids: string[]; truncated: boolean }> {
  const read = referenceReader(qc);
  const ids: string[] = [];
  const seen = new Set([id]);
  let level = [id];
  for (let depth = 0; level.length && depth < WALK_LIMITS.depth; depth++) {
    const next: string[] = [];
    for (const refs of await Promise.all(level.map(read)))
      for (const d of referrersOf(refs, kindOf, "entity", "base")) {
        if (seen.has(d)) continue;
        if (ids.length >= WALK_LIMITS.entities) return { ids, truncated: true };
        seen.add(d);
        ids.push(d);
        next.push(d);
      }
    level = next;
  }
  return { ids, truncated: level.length > 0 };
}
