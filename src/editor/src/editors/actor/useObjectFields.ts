// The fields an object's entities offer a picker (actors-design.md decisions 9 and 11): the attributes (own, inherited,
// from stereotypes) and to-one ends every given entity has by id. Loads the documents it reads.
import { useMemo } from "react";
import { useElements, useIndex } from "@/api/queries";
import { indexLookup } from "@/model/index";
import { commonFields, objectFields, type ObjectField } from "@/model/objectFields";

type Rec = Record<string, unknown>;

export function useObjectFields(entities: readonly string[]): { fields: ObjectField[]; nameOf: (id: string) => string; ready: boolean } {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const chain = useMemo(() => {
    const ids: string[] = [];
    for (const entity of entities) for (let id: string | undefined = entity; id && !ids.includes(id); id = lookup.byId.get(id)?.base) ids.push(id);
    return ids;
  }, [entities, lookup]);
  const chainSet = new Set(chain);
  const relationIds = lookup
    .ofKind("relation")
    .filter((r) => (r.ends ?? []).some((e) => chainSet.has(e.entity)))
    .map((r) => r.id);
  const stereotypeIds = chain.some((id) => (lookup.byId.get(id)?.stereotypes ?? []).length > 0) ? lookup.ofKind("stereotype").map((s) => s.id) : [];
  const held = useElements([...chain, ...relationIds, ...stereotypeIds]);
  const docs = held.byId;
  const doc = (id: string) => docs.get(id)?.json as Rec | undefined;
  const sources = {
    doc,
    relations: relationIds.map(doc).filter((d): d is Rec => !!d),
    stereotype: (key: string) => stereotypeIds.map(doc).find((s) => s?.key === key),
    kindOf: (id: string) => lookup.byId.get(id)?.kind,
  };
  const fields = commonFields(entities.map((e) => objectFields(e, sources)));
  // An end leads to an entity whose name the picker shows, so ends are named for what they lead to when they have no role.
  return { fields, nameOf: (id) => fields.find((f) => f.id === id)?.name ?? id, ready: !held.pending };
}
