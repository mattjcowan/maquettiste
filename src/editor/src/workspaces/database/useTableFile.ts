// The file behind a resolved table and the one way its columns are written, shared by the Database screen's column grid and
// the table inspector: a designed or imported table is its own file; a synthesized (projected) table's file is its overlay,
// found by what it overrides, and created on the first column edit. Every write is one save of that file (or one create of
// the overlay), so one undo step. Writes for a table run one after another across the grid and the inspector (a second edit
// before the first overlay exists must not create a second overlay), each reading the latest file.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import type { ColumnView, TableView } from "@/api/types";
import { invalidateResolved, useElements, useIndex } from "@/api/queries";
import { useServices } from "@/app/context";
import { useElementEdits } from "@/editors/mappingEdit";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import {
  columnDerivations,
  columnFieldProblem,
  isOverlayFor,
  newOverlay,
  overlayCandidates,
  setColumnField,
  tableFileTarget,
  type ColumnField,
  type Derivation,
} from "./columnEdits";

/** Per table (database and key): the queue of its writes, and the overlay a write created before the index lists it. */
const queues = new Map<string, Promise<void>>();
const created = new Map<string, string>();

export function useTableFile(table: TableView | null, databaseId: string | null) {
  const qc = useQueryClient();
  const { store } = useServices();
  const edits = useElementEdits();
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
  const latest = useRef({ fileId, fileJson, pending: docs.pending, target });
  latest.current = { fileId, fileJson, pending: docs.pending, target };
  const [busy, setBusy] = useState(0);

  const run = useCallback(
    async (column: ColumnView, field: ColumnField, value: string | boolean) => {
      const { fileId: known, fileJson: json, pending, target: to } = latest.current;
      if (!to || !databaseId) return;
      const id = known ?? created.get(queueKey) ?? null;
      if (!id && pending && to.kind === "overlay") {
        store.getState().notify("The table's files are still loading; try again in a moment.", "error");
        return;
      }
      if (id) {
        // Tried on a copy first: an edit the file has no place for (a designed column missing from it, a description kept in a
        // sidecar file) is said, not saved.
        if (json && id === known && !setColumnField(clone(json), column, field, value, newId)) {
          store.getState().notify(`Column ${column.name}: this ${field} cannot be edited here; use the table's JSON view.`, "error");
          return;
        }
        await edits.update(id, (doc) => void setColumnField(doc, column, field, value, newId));
      } else if (to.kind === "overlay") {
        const doc = newOverlay(to, databaseId, column, field, value, newId);
        if (doc && (await edits.create(doc, `Edit column ${column.name}`))) created.set(queueKey, String(doc.id));
      }
      invalidateResolved(qc);
    },
    [databaseId, queueKey, store, edits, qc],
  );

  /** Writes one field of one column; a value the field cannot take is said, not saved. */
  const write = useCallback(
    (column: ColumnView, field: ColumnField, value: string | boolean) => {
      const problem = typeof value === "string" ? columnFieldProblem(field, value) : null;
      if (problem) {
        store.getState().notify(`Column ${column.name}: ${problem}`, "error");
        return;
      }
      setBusy((n) => n + 1);
      const next = (queues.get(queueKey) ?? Promise.resolve())
        .then(() => run(column, field, value))
        .catch((e: unknown) => store.getState().notify(`Column ${column.name}: ${(e as Error).message}`, "error"))
        .finally(() => setBusy((n) => n - 1));
      queues.set(queueKey, next);
    },
    [queueKey, run, store],
  );

  return { target, fileId, fileJson, pending: docs.pending, write, busy: busy > 0 };
}

/** What each column of a table derives from (`columnDerivations`): its entity's document, its base entities' and the stereotypes'. */
export function useColumnDerivations(table: TableView | null): Map<string, Derivation> {
  const index = useIndex();
  type Doc = { id?: unknown; key?: unknown; name?: unknown; base?: unknown; stereotypes?: unknown; attributes?: unknown };
  // The entity and its bases, one level per load (a base's id is in the document above it), at most 16 deep.
  const [chain, setChain] = useState<string[]>([]);
  const ids = useMemo(() => (table?.entityId ? [table.entityId, ...chain] : []), [table?.entityId, chain]);
  const docs = useElements(ids);
  const stereotypeIds = useMemo(() => (index.data ?? []).filter((r) => r.kind === "stereotype").map((r) => r.id), [index.data]);
  const stereotypeDocs = useElements(stereotypeIds);
  const lineage = ids.map((id) => docs.byId.get(id)?.json as Doc | undefined);
  const last = lineage[lineage.length - 1];
  const nextBase = typeof last?.base === "string" && !ids.includes(last.base) && ids.length < 16 ? last.base : null;
  useEffect(() => {
    if (nextBase) setChain((c) => (c.includes(nextBase) ? c : [...c, nextBase]));
  }, [nextBase]);
  return useMemo(() => {
    if (!table) return new Map<string, Derivation>();
    const rows = index.data ?? [];
    const docOf = (id: string) => docs.byId.get(id)?.json as Doc | undefined;
    return columnDerivations(
      table,
      docOf(ids[0] ?? ""),
      (ref) => {
        const row = rows.find((r) => r.id === ref);
        return row ? row.displayName || row.name : undefined;
      },
      {
        bases: ids
          .slice(1)
          .map(docOf)
          .filter((d): d is Doc => !!d),
        stereotypes: stereotypeIds.map((id) => stereotypeDocs.byId.get(id)?.json as Doc | undefined).filter((d): d is Doc => !!d),
      },
    );
  }, [table, ids, index.data, docs.byId, stereotypeIds, stereotypeDocs.byId]);
}
