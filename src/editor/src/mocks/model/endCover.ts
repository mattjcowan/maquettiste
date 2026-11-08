// What the mock knows of a relation end with several types when a scenario or a run uses it (several-types-design.md decisions 15, 16 and
// 18, section 8): which entities the end covers (the listed entities, the carriers of its stereotypes and `anyEntity`, each with what is
// derived from them), and the words for the types it leads to. A link or a connect step through such an end is checked against it; a link
// through a one-type end keeps today's checks, which don't look at the target's entity.
import { docEndEntities, docEndHasSeveralTypes, type EndDocLike } from "@/model/endTypes";
import { entityChain } from "@/model/objectFields";
import { Coverage } from "./coverage";
import type { ModelEntry } from "./validate";

type Json = Record<string, unknown>;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

export interface SeveralEnd {
  /** The end's id: the end a link names, which leads to the types. */
  id: string;
  /** "AttachedTo.attachedTo": the relation, then the end's navigation or role. */
  name: string;
  /** Whether an object of this entity can be at the end: the entity or a base is covered. */
  covers(entityId: string): boolean;
  /** The entries in words: "Part, Assembly, or an entity derived from one". */
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
  for (const r of relations)
    for (const end of arr(r.json.ends)) {
      if (!docEndHasSeveralTypes(end as EndDocLike) || typeof end.id !== "string") continue;
      const doc = end as EndDocLike;
      const listed = docEndEntities(doc);
      const stereotypes = (doc.types ?? []).flatMap((t) => t.stereotype ?? []);
      const covered = new Set([...listed, ...stereotypes.flatMap((k) => coverage.carriers(k))]);
      const words = [...listed.map(nameOf), ...stereotypes.map((k) => `an entity with stereotype ${k}`)];
      out.set(end.id, {
        id: end.id,
        name: `${String(r.json.name)}.${String(end.navigation ?? end.role ?? end.id)}`,
        covers: (entityId) => doc.anyEntity === true || entityChain(entityId, (id) => docs.get(id)).some((d) => covered.has(String(d.id))),
        leadsTo:
          doc.anyEntity === true
            ? "any entity"
            : words.length > 1
              ? `${words.join(", ")}, or an entity derived from one`
              : `${words[0] ?? ""}, or an entity derived from it`,
      });
    }
  return out;
}

/** The entities that carry the stereotypes a relation document's end names, for the pickers' field sources. */
export function carriersOf(relation: Json, endIndex: number, coverage: Coverage): string[] {
  const end = arr(relation.ends)[endIndex] as EndDocLike | undefined;
  return [...new Set((end?.types ?? []).flatMap((t) => (t.stereotype ? coverage.carriers(t.stereotype) : [])))];
}
