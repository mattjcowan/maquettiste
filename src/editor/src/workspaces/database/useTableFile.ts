// The file behind a resolved table and the one way it is written, shared by the Database screen's column grid and the table
// inspector: a designed or imported table is its own file. A table the model lays out by convention is stored as a table file
// on its first edit (storeTables.ts), the edit then made on that file, as one undo step; one that cannot be stored yet (a child
// or junction table) keeps its adjustments in the file that adjusts it, found by what it adjusts and created on the first edit.
// Every write is one save of that file, so one undo step. Writes for a table run one after another across the grid, the
// inspector and the table editor's tabs (the one queue per table, `enqueueTableWrite`: a second edit before the first stored the
// table, or created its file, must not store or create it again), each reading the latest file. An edit is made against the
// table as shown when it was made, so one queued while the table was being stored lands on the stored file by its columns' names.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import type { ColumnView, TableView } from "@/api/types";
import { invalidateResolved, useDatabaseView, useElements, useIndex } from "@/api/queries";
import { useServices } from "@/app/context";
import { useElementEdits } from "@/editors/mappingEdit";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import {
  columnFieldProblem,
  emptyOverlay,
  isOverlayFor,
  overlayCandidates,
  overlayChangesSomething,
  setColumnField,
  tableFileTarget,
  type ColumnEditField,
  type ColumnValue,
} from "./columnEdits";
import { enqueueTableWrite, storableOwner, storedFileOf, storeThenEdit, useStorableTables } from "./storeTables";
import { renameTableColumn } from "./columnRename";

/** Per table (database and key): the overlay a write created before the index lists it. */
const created = new Map<string, string>();

/** One write of the table's file: `apply` edits the document (false: it has no place for the edit, said as `refused`). */
interface TableWrite {
  label: string;
  apply: (doc: Record<string, unknown>) => boolean;
  refused: string;
  /** Creates the overlay even when the edit leaves it changing nothing (the JSON tab's "Create the table's file"). */
  force?: boolean;
}

export function useTableFile(table: TableView | null, databaseId: string | null) {
  const qc = useQueryClient();
  const services = useServices();
  const { store } = services;
  const edits = useElementEdits();
  const view = useDatabaseView(databaseId);
  const storable = useStorableTables(databaseId, view.data?.view?.tables);
  const owner = table ? storable.get(table.key) : undefined;
  const index = useIndex();
  const target = useMemo(() => (table && databaseId ? tableFileTarget(table, databaseId) : null), [table, databaseId]);
  const candidates = useMemo(
    () => (table && target && databaseId ? overlayCandidates(index.data ?? [], table, target, databaseId) : []),
    [index.data, table, target, databaseId],
  );
  const docs = useElements(target?.kind === "file" ? [target.id] : candidates);
  const fileId = useMemo(() => {
    if (!target || !databaseId) return null;
    if (target.kind === "file") return target.id;
    for (const id of candidates) if (isOverlayFor(docs.byId.get(id)?.json as Record<string, unknown> | undefined, target, databaseId)) return id;
    return null;
  }, [target, candidates, docs.byId, databaseId]);
  const fileJson = fileId ? (docs.byId.get(fileId)?.json as Record<string, unknown> | undefined) : undefined;
  const queueKey = `${databaseId ?? ""}|${table?.key ?? ""}`;
  useEffect(() => {
    if (fileId) created.delete(queueKey);
  }, [fileId, queueKey]);
  const latest = useRef({ fileId, fileJson, pending: docs.pending, target, owner, table });
  latest.current = { fileId, fileJson, pending: docs.pending, target, owner, table };
  const [busy, setBusy] = useState(0);

  const run = useCallback(
    async (write: TableWrite, made: { table: TableView | null; owner: string | undefined }): Promise<boolean> => {
      const { fileId: known, fileJson: json, pending, target: to } = latest.current;
      if (!to || !databaseId) return false;
      // An edit made on a table the model lays out (as the screen showed it when the edit was made: its column keys are that
      // table's): stored as a table file first, the edit then made on the file. One that waited for an earlier edit to store the
      // table lands on that file (storeThenEdit), whatever the screen shows by now. One made before the screen knew which tables
      // can be stored reads that now.
      const against = made.table;
      const stored = !!against && !!storedFileOf(against.key);
      const owner = made.owner ?? (against && !stored ? await storableOwner(qc, databaseId, against, false) : undefined);
      if (against && (owner || stored)) {
        const done = await storeThenEdit(services, qc, {
          database: databaseId,
          table: against,
          owner: owner ?? "",
          label: write.label,
          edit: (doc) => void write.apply(doc),
        });
        return !!done;
      }
      const id = known ?? created.get(queueKey) ?? null;
      if (!id && pending && to.kind === "overlay") {
        store.getState().notify("The table's files are still loading; try again in a moment.", "error");
        return false;
      }
      let done = false;
      if (id) {
        // Tried on a copy first: an edit the file has no place for (a designed column missing from it, a description kept in a
        // sidecar file) is said, not saved.
        if (json && id === known && !write.apply(clone(json))) {
          store.getState().notify(write.refused, "error");
          return false;
        }
        await edits.update(id, (doc) => void write.apply(doc));
        done = true;
      } else if (to.kind === "overlay") {
        const doc = emptyOverlay(to, databaseId, newId());
        write.apply(doc);
        if ((write.force || overlayChangesSomething(doc)) && (await edits.create(doc, write.label))) {
          created.set(queueKey, String(doc.id));
          done = true;
        }
      }
      invalidateResolved(qc);
      return done;
    },
    [databaseId, queueKey, store, edits, qc, services],
  );

  /** Queues a write behind the table's earlier ones; a failure is said, keyed by `subject`. Resolves to whether it was written. */
  const enqueue = useCallback(
    (subject: string, write: TableWrite): Promise<boolean> => {
      setBusy((n) => n + 1);
      // The table as shown now, when the edit is made: the write may run after the screen has moved on to the table's file.
      const made = { table: latest.current.table, owner: latest.current.owner };
      return enqueueTableWrite(databaseId ?? "", table?.key ?? "", () => run(write, made))
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
        apply: (doc: Record<string, unknown>) => setColumnField(doc, column, field, value, newId),
        refused: `Column ${column.name}: this ${field} cannot be edited here; use the table's JSON view.`,
      };
      // A rename rewrites what names the column too (columnRename.ts); a table that keeps its columns in an adjusting file
      // (a child or junction table) takes the name there.
      if (field === "name" && typeof value === "string" && table && databaseId) {
        if (!value.trim() || value.trim() === column.name) return;
        void renameTableColumn(services, qc, { database: databaseId, key: table.key, column: column.key, to: value })
          .then((done) => (done === null ? enqueue(`Column ${column.name}`, edit) : done))
          .catch((e: unknown) => store.getState().notify(`Column ${column.name}: ${(e as Error).message}`, "error"));
        return;
      }
      void enqueue(`Column ${column.name}`, edit);
    },
    [enqueue, store, services, qc, table, databaseId],
  );

  /** Writes the table's own fields (`update` edits the document); a projected table's first write creates its overlay. */
  const writeTable = useCallback(
    (update: (doc: Record<string, unknown>) => void, options: { force?: boolean } = {}) =>
      enqueue(`Table ${table?.name ?? ""}`, {
        label: options.force ? `Create the file of table ${table?.name ?? ""}` : `Edit table ${table?.name ?? ""}`,
        apply: (doc) => {
          update(doc);
          return true;
        },
        refused: "",
        force: options.force,
      }),
    [enqueue, table?.name],
  );

  return { target, fileId, fileJson, pending: docs.pending, write, writeTable, busy: busy > 0, owner };
}
