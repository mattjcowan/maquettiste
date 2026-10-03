// One table as the table editor, the explorer's table actions and the inspector's part sections edit it: its document (the
// table file, or for a table the model lays out by convention its resolved document) and the one way to change it. A file's
// change is one save of the file (one undo step); a laid-out table is stored as a table file first and the change made on the
// file, together one undo step (storeTables.ts). A laid-out table that cannot be stored yet keeps its keys as resolved.
import { useCallback, useMemo } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useDatabaseView } from "@/api/queries";
import type { Diagnostic, TableView } from "@/api/types";
import { useServices } from "@/app/context";
import { useDraftDocument } from "@/inspector/useDraft";
import { updateTable, useStorableTables } from "./storeTables";
import { isLaidOutKey, resolvedTableDoc } from "./tableParts";

type Json = Record<string, unknown>;

export type TableDocMode = "file" | "storable" | "fixed";

export interface TableDoc {
  database: string;
  key: string;
  /** The resolved table, once its database is resolved. */
  table: TableView | null;
  /** Every table of the database (a foreign key's targets). */
  tables: TableView[];
  doc: Json | undefined;
  mode: TableDocMode;
  /** What storing a laid-out table as a file names (a storable table only). */
  owner: string | undefined;
  /** The table file's id (a file only). */
  fileId: string | null;
  /** The file's diagnostics after its last save. */
  diagnostics: Diagnostic[];
  pending: boolean;
  /** Changes the table: one save (and one undo step) labelled `label`; `edit` may refuse (return false). False when nothing was
   * written, or the save was refused or is in conflict. Runs after the table's earlier writes (one queue per table). */
  update: (label: string, edit: (doc: Json) => void | boolean) => Promise<boolean>;
}

/** The table `key` of `database` as a document to edit. */
export function useTableDoc(database: string | null, key: string | null): TableDoc {
  const services = useServices();
  const qc = useQueryClient();
  const view = useDatabaseView(database);
  const tables = useMemo(() => view.data?.view?.tables ?? [], [view.data]);
  const table = useMemo(() => tables.find((t) => t.key === key) ?? null, [tables, key]);
  const storable = useStorableTables(database, tables);
  const fileId = key && !isLaidOutKey(key) ? key : null;
  const draft = useDraftDocument(fileId);
  const owner = key ? storable.get(key) : undefined;
  const mode: TableDocMode = fileId ? "file" : owner ? "storable" : "fixed";
  const resolved = useMemo(() => (!fileId && table && database ? resolvedTableDoc(table, database) : undefined), [fileId, table, database]);
  const doc = fileId ? (draft.json as unknown as Json | undefined) : resolved;

  const update = useCallback(
    async (label: string, edit: (doc: Json) => void | boolean): Promise<boolean> => {
      if (!database || !key) return false;
      try {
        return await updateTable(services, qc, database, key, label, edit);
      } catch (e) {
        services.store.getState().notify(`${label}: ${(e as Error).message}`, "error");
        return false;
      }
    },
    [database, key, qc, services],
  );

  return {
    database: database ?? "",
    key: key ?? "",
    table,
    tables,
    doc,
    mode,
    owner,
    fileId,
    diagnostics: draft.draft?.diagnostics ?? [],
    pending: fileId ? draft.element.isPending && !draft.json : view.isPending,
    update,
  };
}
