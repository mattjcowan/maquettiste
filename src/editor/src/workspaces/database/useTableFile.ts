// The file behind a resolved table and the one way it is written, shared by the Database screen's column grid and the table
// inspector: every table is its own file (its key is the file id). Every write is one save of that file, so one undo step.
// Writes for a table run one after another across the grid, the inspector and the table editor's tabs (the one queue per
// table, `enqueueTableWrite`), each reading the latest file.
import { useCallback, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import type { ColumnView, TableView } from "@/api/types";
import { invalidateResolved, useElements } from "@/api/queries";
import { useServices } from "@/app/context";
import { useElementEdits } from "@/editors/mappingEdit";
import { clone } from "@/lib/json";
import { columnFieldProblem, setColumnField, type ColumnEditField, type ColumnValue } from "./columnEdits";
import { enqueueTableWrite } from "./tableQueue";
import { renameTableColumn } from "./columnRename";

/** One write of the table's file: `apply` edits the document (false: it has no place for the edit, said as `refused`). */
interface TableWrite {
  label: string;
  apply: (doc: Record<string, unknown>) => boolean;
  refused: string;
}

export function useTableFile(table: TableView | null, databaseId: string | null) {
  const qc = useQueryClient();
  const services = useServices();
  const { store } = services;
  const edits = useElementEdits();
  const fileId = table && databaseId ? table.key : null;
  const docs = useElements(fileId ? [fileId] : []);
  const fileJson = fileId ? (docs.byId.get(fileId)?.json as Record<string, unknown> | undefined) : undefined;
  const latest = useRef({ fileId, fileJson });
  latest.current = { fileId, fileJson };
  const [busy, setBusy] = useState(0);

  const run = useCallback(
    async (write: TableWrite): Promise<boolean> => {
      const { fileId: id, fileJson: json } = latest.current;
      if (!id || !databaseId) return false;
      // Tried on a copy first: an edit the file has no place for (a column missing from it, a description kept in a sidecar
      // file) is said, not saved.
      if (json && !write.apply(clone(json))) {
        store.getState().notify(write.refused, "error");
        return false;
      }
      await edits.update(id, (doc) => void write.apply(doc));
      invalidateResolved(qc);
      return true;
    },
    [databaseId, store, edits, qc],
  );

  /** Queues a write behind the table's earlier ones; a failure is said, keyed by `subject`. Resolves to whether it was written. */
  const enqueue = useCallback(
    (subject: string, write: TableWrite): Promise<boolean> => {
      setBusy((n) => n + 1);
      return enqueueTableWrite(databaseId ?? "", table?.key ?? "", () => run(write))
        .catch((e: unknown) => {
          store.getState().notify(`${subject}: ${(e as Error).message}`, "error");
          return false;
        })
        .finally(() => setBusy((n) => n - 1));
    },
    [databaseId, table?.key, run, store],
  );

  /** Writes one field of one column; a value the field cannot take is said, not saved. */
  const write = useCallback(
    (column: ColumnView, field: ColumnEditField, value: ColumnValue) => {
      const problem = typeof value === "string" ? columnFieldProblem(field, value) : null;
      if (problem) {
        store.getState().notify(`Column ${column.name}: ${problem}`, "error");
        return;
      }
      const edit = {
        label: `Edit column ${column.name}`,
        apply: (doc: Record<string, unknown>) => setColumnField(doc, column, field, value),
        refused: `Column ${column.name}: this ${field} cannot be edited here; use the table's JSON view.`,
      };
      // A rename rewrites what names the column too (columnRename.ts).
      if (field === "name" && typeof value === "string" && table && databaseId) {
        if (!value.trim() || value.trim() === column.name) return;
        void renameTableColumn(services, qc, { database: databaseId, key: table.key, column: column.key, to: value }).catch((e: unknown) =>
          store.getState().notify(`Column ${column.name}: ${(e as Error).message}`, "error"),
        );
        return;
      }
      void enqueue(`Column ${column.name}`, edit);
    },
    [enqueue, store, services, qc, table, databaseId],
  );

  /** Writes the table's own fields (`update` edits the document). */
  const writeTable = useCallback(
    (update: (doc: Record<string, unknown>) => void) =>
      enqueue(`Table ${table?.name ?? ""}`, {
        label: `Edit table ${table?.name ?? ""}`,
        apply: (doc) => {
          update(doc);
          return true;
        },
        refused: "",
      }),
    [enqueue, table?.name],
  );

  return { fileId, fileJson, pending: docs.pending, write, writeTable, busy: busy > 0 };
}
