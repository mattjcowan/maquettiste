// Carries out the storage quick fixes that need more than one document (storageFix.ts): finds a mapping or a binding by name, then edits it
// through the draft manager (one undo step each), or opens the relation's Storage tab, or copies the check. Null when done, else the reason.
import type { QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { indexQuery } from "@/api/queries";
import type { ElementSummary, ModelJson } from "@/api/types";
import { newId } from "@/lib/ids";
import { openTab, setView } from "@/editors/tabs";
import type { useElementEdits } from "@/editors/mappingEdit";
import type { EditorStore } from "@/state/store";
import { boundTableOf } from "@/editors/storage/relationStorage";
import type { StorageFix } from "./storageFix";

type Json = Record<string, unknown>;

/** The first mapping of the database whose `match` holds, read from its document (the index has no relation on a mapping row). */
async function findMapping(rows: ElementSummary[], database: string, match: (doc: Json) => boolean): Promise<Json | null> {
  for (const row of rows.filter((r) => r.kind === "mapping" && r.database === database)) {
    const doc = (await endpoints.getElement(row.id)).json as unknown as Json;
    if (match(doc)) return doc;
  }
  return null;
}

export async function applyStorageFix(
  fix: StorageFix,
  ctx: { qc: QueryClient; store: EditorStore; edits: ReturnType<typeof useElementEdits> },
): Promise<string | null> {
  const { qc, store, edits } = ctx;
  if (fix.action === "open") {
    const s = store.getState();
    s.select([fix.relation]);
    s.updateEditors((e) => openTab(setView(e, "relation", "storage"), { id: fix.relation, kind: "relation" }, { pin: true }));
    return null;
  }
  if (fix.action === "copy") {
    try {
      await navigator.clipboard.writeText(fix.text);
    } catch {
      return "The clipboard is not available here.";
    }
    return null;
  }
  if (fix.action === "move-key") return moveKey(fix, await qc.ensureQueryData(indexQuery), edits);
  const rows = await qc.ensureQueryData(indexQuery);
  const database = rows.find((r) => r.kind === "database" && r.name === fix.database);
  if (!database) return `No database is named '${fix.database}'.`;
  if (fix.action === "ignore") {
    const relationName = rows.find((r) => r.id === fix.relation)?.name ?? "relation";
    const mapping = await findMapping(rows, database.id, (m) => m.relation === fix.relation);
    if (mapping) await edits.update(String(mapping.id), (j) => void (j.ignore = true));
    else {
      const created: Json = {
        kind: "mapping",
        id: newId(),
        name: `${relationName} in ${database.name}`,
        database: database.id,
        relation: fix.relation,
        ignore: true,
      };
      if (!(await edits.create(created, `Store ${relationName} in ${database.name}`))) return "The mapping could not be created.";
    }
    return null;
  }
  // keep-code: the entity's binding to the database, else its mapping element there (created when there is none).
  const entity = rows.find((r) => r.kind === "entity" && r.id === fix.entity);
  if (!entity) return "The entity is not in the model any more.";
  const doc = (await endpoints.getElement(entity.id)).json as unknown as Json;
  const bindings = Array.isArray(doc.bindings) ? (doc.bindings as Json[]) : [];
  // An integer code stays a number and a text code ('007') stays text: the finding prints each as the model holds it.
  const value = fix.code;
  if (bindings.some((b) => b.database === database.id)) {
    await edits.update(entity.id, (j) => {
      const binding = (j.bindings as Json[]).find((b) => b.database === database.id);
      if (binding) binding.discriminatorValue = value;
    });
    return null;
  }
  const mapping = await findMapping(rows, database.id, (m) => m.entity === entity.id);
  if (mapping) await edits.update(String(mapping.id), (j) => void (j.discriminatorValue = value));
  else {
    const created: Json = {
      kind: "mapping",
      id: newId(),
      name: `${entity.name} in ${database.name}`,
      database: database.id,
      entity: entity.id,
      discriminatorValue: value,
    };
    if (!(await edits.create(created, `Keep the stored code of ${entity.name}`))) return "The mapping could not be created.";
  }
  return null;
}

export type { ModelJson };

/** The entities an end lists (a one-type end names one; an end with several types lists them). Any entity and stereotype entries name none. */
const endEntities = (end: Json): string[] =>
  typeof end.entity === "string" ? [end.entity] : ((end.types as Json[] | undefined) ?? []).flatMap((t) => (typeof t.entity === "string" ? [t.entity] : []));

/**
 * Moves a mapping's top-level `foreignKey` to the entry of the end whose table the key refers to (the key points at that end). Null when done,
 * else the reason. On an end with several types, a key to the table of every listed type stays the entry's `foreignKey`; a key to one type's
 * table becomes that type's `types` item (a key per type), so it never names a table that holds only some of the end's types. When no end's
 * table is the one the key refers to, or it is the table of some but not one or all of an end's types, nothing moves.
 */
async function moveKey(
  fix: Extract<StorageFix, { action: "move-key" }>,
  rows: ElementSummary[],
  edits: ReturnType<typeof useElementEdits>,
): Promise<string | null> {
  const mapping = (await endpoints.getElement(fix.mapping)).json as unknown as Json;
  const relation = (await endpoints.getElement(fix.relation)).json as unknown as Json;
  const database = String(mapping.database ?? "");
  // The table the key refers to: found among the database's tables by the key's id.
  let referenced: string | null = null;
  for (const row of rows.filter((r) => r.kind === "table" && r.database === database)) {
    const table = (await endpoints.getElement(row.id)).json as unknown as { foreignKeys?: { id: string; referencesTable: string }[] };
    const key = table.foreignKeys?.find((k) => k.id === fix.key);
    if (key) {
      referenced = key.referencesTable;
      break;
    }
  }
  if (!referenced) return "The key was not found in a table of this database; remove it instead.";
  const entries = (mapping.ends as Json[] | undefined) ?? [];
  const ends = (relation.ends as Json[] | undefined) ?? [];
  let target: string | null = null;
  let entry: Json | null = null;
  for (const end of ends) {
    const listed = endEntities(end);
    const matching: string[] = [];
    for (const entity of listed) {
      const doc = (await endpoints.getElement(entity)).json as unknown as Json;
      let table = boundTableOf(doc, database);
      if (!table)
        for (const row of rows.filter((r) => r.kind === "mapping" && r.entity === entity && r.database === database)) {
          const file = (await endpoints.getElement(row.id)).json as unknown as Json;
          if (file.ignore !== true && typeof file.table === "string") table = file.table;
        }
      if (table === referenced) matching.push(entity);
    }
    if (matching.length === 0) continue;
    target = String(end.id);
    const several = typeof end.entity !== "string";
    if (!several || (matching.length === listed.length && end.anyEntity !== true)) entry = { end: target, foreignKey: fix.key };
    else if (matching.length === 1) entry = { end: target, types: [{ entity: matching[0], foreignKey: fix.key }] };
    break;
  }
  if (!target) return "The key does not refer to the table of either end; remove it instead.";
  if (!entry) return "The key refers to a table that holds some of the end's types; remove it and choose how the end is stored instead.";
  if (entries.some((e) => e.end === target)) return "That end already has an entry; remove the key instead.";
  const moved = entry;
  await edits.update(fix.mapping, (j) => {
    j.ends = [...((j.ends as Json[] | undefined) ?? []), moved];
    delete j.foreignKey;
    delete j.foreignKeyEnd;
  });
  return null;
}
