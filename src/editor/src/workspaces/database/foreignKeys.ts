// Opening the foreign key dialog and writing what it saves, the same way from the diagram (a drag, a double click on an edge),
// the explorer (New foreign key on a table or its Foreign keys folder), the table editor's Foreign keys tab (Add foreign key,
// Edit…) and the inspector (Edit…): every write goes through `updateTable`, so a table not stored as a file yet is stored first
// and the key written into the new file in the same undo step, its ids read from the store's result.
import type { QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { databaseViewQuery, elementQuery, keys, useDatabaseView, useSettings } from "@/api/queries";
import type { ForeignKeyView, TableView } from "@/api/types";
import type { AppServices as Services } from "@/app/context";
import { newId } from "@/lib/ids";
import { applyDraft, draftForTable, draftFromForeignKey, fkEntryOf, fkNamePattern, type FkDraft } from "./fkEdits";
import { liveStoredFile, storedFileOf, updateTable } from "./storeTables";
import { isLaidOutKey } from "./tableParts";

type Json = Record<string, unknown>;

/** The foreign key naming pattern of a database (its conventions over the project's). */
export function useForeignKeyPattern(database: string | null): string {
  const settings = useSettings();
  const view = useDatabaseView(database);
  return fkNamePattern(settings.data?.json, view.data?.view?.name ?? null);
}

/** New foreign key outside a screen that holds the tables (the explorer): the dialog on a new key of `table`. */
export async function openNewForeignKeyFor(services: Pick<Services, "store">, qc: QueryClient, database: string, table: string): Promise<void> {
  const [view, settings] = await Promise.all([
    qc.fetchQuery(databaseViewQuery(qc, database)),
    qc.fetchQuery({ queryKey: keys.settings, queryFn: endpoints.getSettings }),
  ]);
  const tables = view.view?.tables ?? [];
  openNewForeignKey(services, database, tables, table, fkNamePattern(settings.json, view.view?.name ?? null));
}

/** Opens the dialog on a new key of `table` (nothing chosen yet). */
export function openNewForeignKey(services: Pick<Services, "store">, database: string, tables: readonly TableView[], table: string, pattern: string): void {
  services.store.getState().requestForeignKeyDialog({ database, draft: draftForTable(tables, table, pattern) });
}

/** Opens the dialog on an existing key, starting from its entry in the table's file (what the view does not carry). */
export async function openEditForeignKey(
  services: Pick<Services, "store">,
  qc: QueryClient,
  database: string,
  tables: readonly TableView[],
  table: TableView,
  fk: ForeignKeyView,
): Promise<void> {
  const file = isLaidOutKey(table.key) ? await liveStoredFile(qc, table.key) : table.key;
  const doc = file ? await qc.fetchQuery({ ...elementQuery(file), staleTime: 0 }).catch(() => null) : null;
  const entry = doc ? fkEntryOf(doc.json as unknown as Json, fk) : undefined;
  services.store.getState().requestForeignKeyDialog({ database, draft: draftFromForeignKey(table, fk, tables, entry) });
}

/**
 * Writes a draft into its table: one undo step (with the store of a table not stored as a file yet). A draft the table has no
 * place for is said and nothing is written. Resolves to whether it was saved; the key is then the inspector's subject.
 */
export async function saveForeignKey(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  input: { database: string; draft: FkDraft; tables: readonly TableView[]; pattern: string },
): Promise<boolean> {
  const { database, draft: d, tables, pattern } = input;
  const table = tables.find((t) => t.key === d.table);
  if (!table) return false;
  let problem: string | null = null;
  const label = d.editing ? `Edit foreign key ${d.editing.name}` : `New foreign key on ${table.name}`;
  const ok = await updateTable(services, qc, database, d.table, label, (doc) => {
    problem = applyDraft(doc, d, { tables, pattern, newId });
    return problem ? false : undefined;
  });
  const s = services.store.getState();
  if (problem) {
    s.notify(`${label}: ${problem}`, "error");
    return false;
  }
  if (!ok) return false;
  const name = d.name.trim() || d.editing?.name;
  s.notify(d.editing ? `Foreign key ${name ?? ""} saved.` : name ? `Foreign key ${name} created.` : "Foreign key created.");
  if (name) s.inspectTable({ database, key: storedFileOf(d.table) ?? d.table, column: null, part: { kind: "foreign-key", id: name } });
  return true;
}
