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
  const entity = rows.find((r) => r.kind === "entity" && r.name === fix.entity);
  if (!entity) return `No entity is named '${fix.entity}'.`;
  const doc = (await endpoints.getElement(entity.id)).json as unknown as Json;
  const bindings = Array.isArray(doc.bindings) ? (doc.bindings as Json[]) : [];
  const value = /^-?\d+$/.test(fix.code) ? Number(fix.code) : fix.code;
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
