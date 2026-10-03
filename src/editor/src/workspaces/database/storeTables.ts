// Storing the tables the model lays out by convention as table files (the engine's batch operation `materialize-tables`, erratum
// E43): the Database screen's "Store as table files" for all of them, and the first edit of any part of one such table, which
// stores that table first and then makes the edit on its file. The database side says only that a table is now stored as a
// file; what else the operation changes is not its business. Storing is one batch (all or nothing) and one undo step; the
// edit that asked for it joins that step. The ids the new files get are read from the batch's result, never from the preview
// (the engine draws them again when it applies). The batch carries the hashes of the documents read for the undo step, so a
// document saved meanwhile makes it a conflict instead of an undo that would restore a stale version; and the engine's schema
// snapshot remembers each stored table's former key, so undoing the store is no drop and create in the next migration.
//
// Every write of one table runs after the earlier ones (`enqueueTableWrite`): the column grid, the inspector, the table
// editor's tabs and the explorer share one queue per table, so a second edit made before the first stored the table never
// stores it twice.
import { useMemo } from "react";
import { useQuery, type QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, databaseViewQuery, elementQuery, invalidateResolved, keys, useIndex } from "@/api/queries";
import type { ElementSummary, ModelJson, TableView } from "@/api/types";
import { clone } from "@/lib/json";
import type { AppServices as Services } from "@/app/context";
import { loadPositions, savePositions } from "@/lib/positions";
import { tableKeyOf } from "@/search/engine";
import type { EditorStore } from "@/state/store";
import { batchUndoEntry, columnIdMap, isLaidOutKey, replayOnFile, resolvedTableDoc, storableTables } from "./tableParts";

type Json = Record<string, unknown>;

/** Per table (database and key): the queue of its writes. */
const queues = new Map<string, Promise<unknown>>();

/** Runs `write` after the table's earlier writes (whichever screen asked for them); resolves to what it resolves to. */
export function enqueueTableWrite<T>(database: string, key: string, write: () => Promise<T>): Promise<T> {
  const queueKey = `${database}|${key}`;
  const next = (queues.get(queueKey) ?? Promise.resolve()).catch(() => undefined).then(write);
  const settled = next.catch(() => undefined);
  queues.set(queueKey, settled);
  void settled.then(() => {
    if (queues.get(queueKey) === settled) queues.delete(queueKey);
  });
  return next;
}

/** What storing the tables of some elements would do, nothing written (the engine's materialize-tables preview). */
const previewStoreTables = (database: string, entities: readonly string[]) =>
  endpoints.previewMaterialize(database, { op: "materialize-tables", entities: [...entities] });

/** Under the database view's key, so whatever refreshes the resolved tables refreshes it too. */
const statusKey = (database: string) => [...keys.databaseView(database), "materialize"] as const;

/** The database's tables that can be stored as table files: table key → what the conversion names. */
export function useStorableTables(database: string | null, tables: readonly TableView[] | undefined): Map<string, string> {
  const index = useIndex();
  const laidOut = !!tables?.some((t) => t.origin === "synthesized");
  const status = useQuery({
    queryKey: statusKey(database ?? ""),
    queryFn: () => endpoints.getMaterializeStatus(database!),
    enabled: !!database && laidOut,
    staleTime: 0,
  });
  return useMemo(
    () => (database && tables && laidOut ? storableTables(tables, database, status.data?.entities, index.data) : new Map<string, string>()),
    [database, tables, laidOut, status.data, index.data],
  );
}

/** The tables stored in this session: old key → the file's id, so an edit queued before the screen caught up lands on the file. */
const storedKeys = new Map<string, string>();
/** The resolved table each of them was (its columns by their old keys), for such an edit's column names. */
const storedViews = new Map<string, TableView>();

/** The file a table was stored as in this session, if it was. */
export const storedFileOf = (key: string): string | undefined => storedKeys.get(key);

/** The file a table was stored as in this session, if it still is one (an undo of the store lays the table out again). */
export async function liveStoredFile(qc: QueryClient, key: string): Promise<string | undefined> {
  const file = storedKeys.get(key);
  if (!file) return undefined;
  const doc = await qc.fetchQuery({ ...elementQuery(file), staleTime: 0 }).catch(() => null);
  if (doc && (doc.json as unknown as Json).origin !== "synthesized") return file;
  storedKeys.delete(key);
  return undefined;
}

export interface StoreResult {
  ok: boolean;
  /** Old table key → the new table file's id. */
  files: Map<string, string>;
}

/**
 * Stores the tables of `owners` (old table key → the id the operation names) as table files: a preview first (a refusal or an
 * error is said, nothing is written), then the batch, one undo step labelled `label`. The canvas keeps each table's place, and
 * the inspector, the Database screen and any open editor follow a table to its file.
 */
export async function storeTables(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  database: string,
  owners: ReadonlyMap<string, string>,
  label: string,
): Promise<StoreResult> {
  const { store, drafts } = services;
  const files = new Map<string, string>();
  const entities = [...new Set(owners.values())];
  if (!entities.length) return { ok: false, files };
  await drafts.flushAll();
  const plan = await previewStoreTables(database, entities);
  const blocking = plan.diagnostics.find((d) => d.severity === "error");
  if (!plan.valid || blocking) {
    store.getState().notify(`The tables were not stored as files: ${blocking?.message ?? "the change would not be valid."}`, "error");
    return { ok: false, files };
  }
  // Every document the batch changes or deletes, as it is now: the undo step restores them, and the batch expects them as read.
  const priors = new Map<string, ModelJson>();
  const expectedHashes: Record<string, string> = {};
  await Promise.all(
    [...plan.updates, ...plan.deletes].map(async (c) => {
      const doc = await qc.fetchQuery({ ...elementQuery(c.id), staleTime: 0 });
      priors.set(c.id, doc.json as ModelJson);
      expectedHashes[c.id] = doc.hash;
    }),
  );
  // The tables as resolved now, for an edit queued with their old keys before the screens catch up (updateTable).
  const laidOut = (await qc.fetchQuery(databaseViewQuery(qc, database)).catch(() => null))?.view?.tables ?? [];
  const result = await endpoints.applyBatch({ operations: [{ op: "materialize-tables", database, entities, expectedHashes } as never] });
  if (!endpoints.isBatchResult(result) || result.outcome !== "saved") {
    const why = endpoints.isBatchResult(result)
      ? result.outcome === "conflict"
        ? "a table or its element changed meanwhile; try again."
        : (result.items.find((i) => i.diagnostics.length)?.diagnostics[0]?.message ?? result.outcome)
      : (result.diagnostics[0]?.message ?? "invalid batch");
    store.getState().notify(`The tables were not stored as files: ${why}`, "error");
    return { ok: false, files };
  }
  applyBatchResult(qc, result);
  store.getState().pushUndo(batchUndoEntry(label, priors, result.items));
  // Each owner's new source in this database is its table's file.
  const sourceOf = new Map<string, string>();
  for (const item of result.items) {
    const json = item.current?.json as Json | undefined;
    if (!json || !Array.isArray(json.bindings)) continue;
    const binding = (json.bindings as Json[]).find((b) => b.database === database);
    if (typeof binding?.source === "string") sourceOf.set(String(json.id), binding.source);
  }
  const positionsKey = `db.${database}`;
  const positions = loadPositions(positionsKey);
  for (const [key, owner] of owners) {
    const file = sourceOf.get(owner);
    if (!file) continue;
    files.set(key, file);
    storedKeys.set(key, file);
    const shown = laidOut.find((t) => t.key === key);
    if (shown) storedViews.set(key, shown);
    if (positions[key] && !positions[file]) positions[file] = positions[key];
    followTable(store, database, key, file);
  }
  savePositions(positionsKey, positions);
  invalidateResolved(qc);
  void qc.invalidateQueries({ queryKey: keys.tables });
  return { ok: true, files };
}

/** Tables whose store was taken back (retractStep): forgotten, and the screens that followed them go back to their keys. */
export function forgetStored(store: EditorStore, database: string, files: ReadonlyMap<string, string>): void {
  for (const [key, file] of files) {
    if (storedKeys.get(key) === file) storedKeys.delete(key);
    storedViews.delete(key);
    followTable(store, database, file, key);
  }
}

/** The resolved table a table stored in this session was, by its old key (its columns by their old keys). */
export const storedViewOf = (key: string): TableView | undefined => storedViews.get(key);

/** The inspector, the Database screen's focus and an open editor tab move from a table's old key to its file. */
function followTable(store: EditorStore, database: string, key: string, file: string): void {
  const s = store.getState();
  const shown = s.inspectedTable;
  if (shown && shown.database === database && shown.key === key)
    s.inspectTable({ database, key: file, column: shown.column, part: shown.part ?? null }, tableKeyOf(database, file));
  if (s.databaseTable === key) s.setDatabaseTable(file);
  if (s.editors.tabs.some((t) => t.id === key)) s.updateEditors((e) => ({ ...e, tabs: e.tabs.map((t) => (t.id === key ? { ...t, id: file } : t)) }));
}

/**
 * The first edit of a table the model lays out: stores the table as a file, then makes `edit` on that file (written against
 * the table's resolved document, its columns named by their resolved keys), as one undo step labelled `label`, and says so
 * once. An edit that refuses (returns false) on the resolved document stores nothing. Resolves to the file's id and the column
 * map (old key → the file's column id), or null when nothing was written.
 */
export async function storeThenEdit(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  args: { database: string; table: TableView; owner: string; label: string; edit: (doc: Json) => void | boolean },
): Promise<{ file: string; columns: Map<string, string> } | null> {
  const { store, drafts } = services;
  const { database, table, owner, label, edit } = args;
  // A table stored earlier whose store was undone since is laid out again: it is stored again.
  let file = await liveStoredFile(qc, table.key);
  let stored = false;
  if (!file) {
    if (!owner) return null;
    // Tried on the resolved document first: an edit with no place there stores nothing.
    if (edit(clone(resolvedTableDoc(table, database))) === false) return null;
    const result = await storeTables(services, qc, database, new Map([[table.key, owner]]), label);
    file = result.files.get(table.key);
    if (!file) return null;
    stored = true;
  }
  const base = await qc.fetchQuery({ ...elementQuery(file), staleTime: 0 });
  const columns = columnIdMap(table.columns, base.json as unknown as Json);
  if (!replayOnFile(clone(base.json as unknown as Json), table.columns, edit, table.key)) return null;
  drafts.edit(
    file,
    (json) => {
      const next = replayOnFile(json as unknown as Json, table.columns, edit, table.key);
      if (!next) return;
      const rec = json as unknown as Json;
      for (const k of Object.keys(rec)) delete rec[k];
      Object.assign(rec, next);
    },
    // The edit joins the step that stored the table: one undo puts both back.
    { base, followUp: stored, label },
  );
  const saved = await drafts.flushSaved(file);
  invalidateResolved(qc);
  if (stored) store.getState().notify(`${table.name} is now stored as a table file.`);
  return saved ? { file, columns } : null;
}

/**
 * Changes one table (any table of the database, by its key) outside a screen that holds it (the explorer's actions, the table
 * editor and the inspector go through here too): a file is one save; a laid-out table is stored as a file first and the change
 * made on it, one undo step; a laid-out table that cannot be stored yet is said, not changed. `edit` may refuse (return false):
 * nothing is written. Runs after the table's earlier writes. Resolves to whether it was saved (false too when the save was
 * refused, or the file is in conflict with a newer version).
 */
export function updateTable(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  database: string,
  key: string,
  label: string,
  edit: (doc: Json) => void | boolean,
): Promise<boolean> {
  return enqueueTableWrite(database, key, async () => {
    const { store, drafts } = services;
    if (!isLaidOutKey(key)) {
      const base = await qc.fetchQuery({ ...elementQuery(key), staleTime: 0 });
      if (store.getState().drafts[key]?.status === "conflict") {
        store.getState().notify(`${label}: the table changed on disk; resolve the conflict first.`, "error");
        return false;
      }
      if (edit(clone((drafts.current(key) ?? base.json) as unknown as Json)) === false) return false;
      drafts.edit(key, (json) => void edit(json as unknown as Json), { base, label });
      const saved = await drafts.flushSaved(key);
      invalidateResolved(qc);
      if (!saved) {
        const draft = store.getState().drafts[key];
        const why =
          draft?.status === "conflict"
            ? "the table changed on disk; resolve the conflict first"
            : (draft?.diagnostics.find((d) => d.severity === "error")?.message ?? draft?.error ?? "the save was refused");
        store.getState().notify(`${label}: ${why}.`, "error");
      }
      return saved;
    }
    // A table the model lays out, or one stored in this session whose screens have not caught up yet (the edit lands on its file).
    const view = await qc.fetchQuery(databaseViewQuery(qc, database));
    const table = view.view?.tables.find((t) => t.key === key) ?? storedViews.get(key);
    if (!table) return false;
    const status = await qc.fetchQuery({ queryKey: statusKey(database), queryFn: () => endpoints.getMaterializeStatus(database), staleTime: 0 });
    const owner = storableTables([table], database, status.entities, qc.getQueryData<ElementSummary[]>(keys.index)).get(key);
    if (!owner && !storedKeys.has(key)) {
      store.getState().notify("This table's keys and constraints are set by the model; they cannot be edited here yet.", "error");
      return false;
    }
    return !!(await storeThenEdit(services, qc, { database, table, owner: owner ?? "", label, edit }));
  });
}
