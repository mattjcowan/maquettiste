// What the mock knows of a relation end with several types when a scenario, a guard or a run uses it (several-types-design.md decisions 4, 5,
// 15, 16 and 18, section 8), as EndCoverage and RelationTypeRules.TypesText know it in the engine: which entities the end covers (the listed
// entities, the carriers of its stereotypes and `anyEntity`, each with what is derived from them), its types (the covered entities with no
// covered base, in the order of the entries), and the words for them. A link or a connect step through such an end is checked against it; a
// link through a one-type end keeps today's checks, which don't look at the target's entity.
import { docEndHasSeveralTypes, type EndDocLike } from "@/model/endTypes";
import { Coverage } from "./coverage";
import type { ModelEntry } from "./validate";

type Json = Record<string, unknown>;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

export interface SeveralEnd {
  /** The end's id: the end a link names, which leads to the types. */
  id: string;
  /** "AttachedTo.attachedTo": the relation, then the end's role. */
  name: string;
  /** Whether an object of this entity can be at the end: the entity or a base is covered. */
  covers(entityId: string): boolean;
  /** The end's types by id, in order (decision 5). */
  types: string[];
  /** The types in words, as the engine's TypesText says them: "Part, Assembly, or an entity derived from one", or "no entity yet". */
  leadsTo: string;
}

/** The ends with several types of a model, by end id. Empty (and cheap) when the model has none. */
export function severalEnds(entries: Iterable<ModelEntry>): Map<string, SeveralEnd> {
  const all = [...entries];
  const out = new Map<string, SeveralEnd>();
  const relations = all.filter((e) => e.json.kind === "relation" && arr(e.json.ends).some((x) => docEndHasSeveralTypes(x as EndDocLike)));
  if (!relations.length) return out;
  const docs = new Map(all.map((e) => [e.id, e.json] as const));
  const coverage = new Coverage(all);
  const nameOf = (id: string) => String(docs.get(id)?.name ?? id);
  const chainOf = (id: string): string[] => {
    const doc = docs.get(id);
    return doc ? coverage.chain(doc) : [id];
  };
  const entityIds = all.filter((e) => e.json.kind === "entity").map((e) => e.id);
  for (const r of relations)
    for (const end of arr(r.json.ends)) {
      if (!docEndHasSeveralTypes(end as EndDocLike) || typeof end.id !== "string") continue;
      const any = end.anyEntity === true;
      const entries2 = arr(end.types);
      // The entities the entries name directly.
      const named = new Set<string>(any ? entityIds : []);
      for (const t of entries2) {
        if (typeof t.entity === "string") named.add(t.entity);
        else if (typeof t.stereotype === "string") for (const c of coverage.carriers(t.stereotype)) named.add(c);
      }
      const covered = new Set(entityIds.filter((id) => chainOf(id).some((x) => named.has(x))));
      // Decision 5: the covered entities with no covered base, each where the walk over the entries first reaches it.
      const types: string[] = [];
      const offer = (id: string) => {
        if (
          docs.has(id) &&
          covered.has(id) &&
          !chainOf(id)
            .slice(1)
            .some((x) => covered.has(x)) &&
          !types.includes(id)
        )
          types.push(id);
      };
      if (any) coverage.roots().forEach(offer);
      else
        for (const t of entries2) {
          if (typeof t.entity === "string") offer(t.entity);
          else if (typeof t.stereotype === "string") coverage.carriers(t.stereotype).forEach(offer);
        }
      out.set(end.id, {
        id: end.id,
        name: `${String(r.json.name)}.${String(end.role || end.id)}`,
        covers: (entityId) => chainOf(entityId).some((x) => named.has(x)) && docs.has(entityId),
        types,
        leadsTo: types.length ? `${types.map(nameOf).join(", ")}, or an entity derived from one` : "no entity yet",
      });
    }
  return out;
}

/** The entities that carry the stereotypes a relation document's end names, for the pickers' field sources. */
export function carriersOf(relation: Json, endIndex: number, coverage: Coverage): string[] {
  const end = arr(relation.ends)[endIndex] as EndDocLike | undefined;
  return [...new Set((end?.types ?? []).flatMap((t) => (t.stereotype ? coverage.carriers(t.stereotype) : [])))];
}
