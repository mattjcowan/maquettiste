// The database diagram's designer gestures (the canvas part of DatabaseWorkspace.tsx): the Display menu (all columns, keys only,
// names only; minimap), foreign keys by dragging a column's handle onto a column (or header) of another table, the selected key
// (shown alone in the inspector, under its table's breadcrumb, where it edits and deletes like any part; a double click on its
// edge edits it in the dialog), Delete on a selected key or table, and New table from the toolbar or a double click on the
// empty canvas (the table lands where the click was). The dialog is the editor's one foreign key dialog (ForeignKeyDialogHost);
// every write is one undo step through the one way a table is written (foreignKeys.ts, storeTables.ts).
import { useCallback, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent as ReactMouseEvent, type RefObject } from "react";
import type { OnConnectEnd, OnConnectStart, ReactFlowInstance } from "@xyflow/react";
import type { ForeignKeyView, TableView } from "@/api/types";
import { useQueryClient } from "@tanstack/react-query";
import { useDatabaseView, useSettings } from "@/api/queries";
import { useServices } from "@/app/context";
import { useEditor } from "@/state/store";
import { requestDelete } from "@/explorer/deleteRequest";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
} from "@/components/ui/menu";
import type { TableFlowNode } from "@/canvas/TableNode";
import type { ForeignKeyFlowEdge } from "@/canvas/ForeignKeyEdge";
import { loadDatabaseDisplay, saveDatabaseDisplay, type DatabaseDisplay, type TableDisplay } from "@/canvas/tableColumns";
import { draftForTable, draftFromDrop, END_WORDS, FK_ACTION_LABELS, asAction, fkEnds, fkNamePattern, isLegacy, type FkDraft } from "./fkEdits";
import { openEditForeignKey } from "./foreignKeys";

type XY = { x: number; y: number };

/** The edge id of a key: its table's key and its name. */
export const fkEdgeId = (table: string, fk: Pick<ForeignKeyView, "name">) => `${table}:${fk.name}`;

/** A key's tooltip: its columns, what it references, its rules and its ends in words. */
export function fkSummary(table: TableView, fk: ForeignKeyView, referenced: TableView | null): string {
  const names = (t: TableView | null, keys: string[]) => keys.map((k) => t?.columns.find((c) => c.key === k)?.name ?? k).join(", ");
  const ends = fkEnds(table, fk);
  return [
    `${fk.name}: ${table.name} (${names(table, fk.columns)}) → ${referenced?.name ?? fk.referencedTable} (${names(referenced, fk.referencedColumns)})`,
    `On delete ${FK_ACTION_LABELS[asAction(fk.onDelete)].toLowerCase()}, on update ${FK_ACTION_LABELS[asAction(fk.onUpdate)].toLowerCase()}`,
    `Each ${table.name} row references ${END_WORDS[ends.target]} ${referenced?.name ?? "row"} row; each ${referenced?.name ?? "referenced"} row is referenced by ${END_WORDS[ends.source]} ${table.name} rows`,
  ].join("\n");
}

export function useDiagramDesigner(input: {
  databaseId: string | null;
  databaseName: string | null;
  allTables: readonly TableView[];
  flow: ReactFlowInstance<TableFlowNode, ForeignKeyFlowEdge>;
  canvasRef: RefObject<HTMLDivElement | null>;
  selectedTable: string | null;
}) {
  const { databaseId, databaseName, allTables, flow, canvasRef, selectedTable } = input;
  const services = useServices();
  const { store } = services;
  const qc = useQueryClient();
  const settings = useSettings();
  const pattern = fkNamePattern(settings.data?.json, databaseName);
  const dialect = useDatabaseView(databaseId).data?.view?.dialect ?? "postgresql";

  const [display, setDisplay] = useState<DatabaseDisplay>(loadDatabaseDisplay);
  const changeDisplay = (next: DatabaseDisplay) => {
    setDisplay(next);
    saveDatabaseDisplay(next);
  };

  /** Opens the editor's foreign key dialog (ForeignKeyDialogHost) on a draft. */
  const setDraft = useCallback(
    (draft: FkDraft) => {
      if (databaseId) store.getState().requestForeignKeyDialog({ database: databaseId, draft });
    },
    [store, databaseId],
  );
  const [confirming, setConfirming] = useState<{ table: TableView; fk: ForeignKeyView } | null>(null);
  const byEdge = useMemo(() => {
    const map = new Map<string, { table: TableView; fk: ForeignKeyView }>();
    for (const t of allTables) for (const fk of t.foreignKeys) map.set(fkEdgeId(t.key, fk), { table: t, fk });
    return map;
  }, [allTables]);
  // The selected key is the inspector's subject: a foreign key part of a table of this database (one selection, whichever
  // way it was picked: its edge, the explorer, the table editor).
  const inspected = useEditor(store, (s) => s.inspectedTable);
  const selectedFk =
    inspected && inspected.database === databaseId && inspected.part?.kind === "foreign-key" ? fkEdgeId(inspected.key, { name: inspected.part.id }) : null;
  const picked = selectedFk ? (byEdge.get(selectedFk) ?? null) : null;
  /** Selects a key's edge (the inspector shows the key alone), or none (the inspector goes back to its table). */
  const setSelectedFk = useCallback(
    (edgeId: string | null) => {
      const s = store.getState();
      if (!edgeId) {
        if (s.inspectedTable?.part?.kind === "foreign-key") s.inspectPart(null);
        return;
      }
      const hit = byEdge.get(edgeId);
      if (hit && databaseId) s.inspectTable({ database: databaseId, key: hit.table.key, column: null, part: { kind: "foreign-key", id: hit.fk.name } });
    },
    [store, byEdge, databaseId],
  );

  /** The keyboard's way to drag: the dialog with that column (or none) and no referenced table yet. */
  const onLink = useCallback(
    (table: string, column: string | null) => {
      setDraft({ ...draftForTable(allTables, table, pattern), pairs: [{ column, referenced: "" }] });
    },
    [allTables, pattern, setDraft],
  );

  // A drag from a handle: where it started, then the column or header under the pointer where it ended.
  const from = useRef<{ table: string; column: string | null } | null>(null);
  const onConnectStart: OnConnectStart = useCallback((_, { nodeId, handleId }) => {
    from.current = nodeId ? { table: nodeId, column: handleId?.startsWith("R:") ? handleId.slice(2) || null : null } : null;
  }, []);
  const onConnectEnd: OnConnectEnd = useCallback(
    (e) => {
      const start = from.current;
      from.current = null;
      if (!start) return;
      const point = "changedTouches" in e ? e.changedTouches[0] : e;
      if (!point) return;
      for (const hit of document.elementsFromPoint(point.clientX, point.clientY)) {
        const node = hit.closest<HTMLElement>(".react-flow__node[data-id]");
        if (!node) continue;
        if (!canvasRef.current?.contains(node)) return;
        const target = node.dataset.id ?? "";
        const column = hit.closest<HTMLElement>("[data-column-key]")?.dataset.columnKey ?? null;
        // A drop back on where it started (or on its own table's header) is no key.
        if (target === start.table && (!column || column === start.column)) return;
        setDraft(
          draftFromDrop(allTables, { sourceTable: start.table, sourceColumn: start.column, targetTable: target, targetColumn: column }, pattern, dialect),
        );
        return;
      }
    },
    [allTables, pattern, canvasRef, setDraft, dialect],
  );

  /** The key's delete: its plan (a relation mapping naming it loses that) and one undo step, through the part delete. */
  const removeKey = useCallback(
    (table: TableView, fk: ForeignKeyView) => {
      if (!databaseId) return;
      store.getState().requestPartDelete({ database: databaseId, key: table.key, part: { kind: "foreign-key", id: fk.name } });
    },
    [databaseId, store],
  );

  /** Delete on the canvas: the selected key (after a confirmation), else the selected table (its delete plan). */
  const onKeyDown = useCallback(
    (e: ReactKeyboardEvent) => {
      if (e.key !== "Delete" && e.key !== "Backspace") return;
      const el = e.target as HTMLElement;
      if (el.closest("input, textarea, select, [contenteditable=true]")) return;
      if (picked) {
        e.preventDefault();
        setConfirming(picked);
        return;
      }
      const table = allTables.find((t) => t.key === selectedTable);
      if (!table) return;
      e.preventDefault();
      if (isLegacy(table)) {
        store.getState().notify(`Table ${table.name} is not stored as a table file yet; store the database's tables as files to delete it.`, "error");
        return;
      }
      requestDelete({ ids: [table.key], names: new Map([[table.key, table.name]]) });
    },
    [picked, allTables, selectedTable, store],
  );

  // New table: the existing New table dialog; the table it creates lands where the canvas was double-clicked (else where the
  // placement puts a new table).
  const pending = useRef<{ at: XY; known: Set<string> } | null>(null);
  const newTable = useCallback(
    (at: XY | null) => {
      if (!databaseId) return;
      pending.current = at ? { at, known: new Set(allTables.map((t) => t.key)) } : null;
      store.getState().requestNewDatabaseObject({ kind: "table", database: databaseId });
    },
    [databaseId, allTables, store],
  );
  const onDoubleClick = useCallback(
    (e: ReactMouseEvent) => {
      if (!(e.target as HTMLElement).classList.contains("react-flow__pane")) return;
      newTable(flow.screenToFlowPosition({ x: e.clientX, y: e.clientY }));
    },
    [flow, newTable],
  );
  /** Positions for tables that appeared since New table was asked from a double click (taken once). */
  const takePlaced = useCallback((missing: readonly string[]): Record<string, XY> => {
    const p = pending.current;
    if (!p) return {};
    const fresh = missing.filter((k) => !p.known.has(k));
    if (!fresh.length) return {};
    pending.current = null;
    return { [fresh[0]]: { x: Math.round(p.at.x), y: Math.round(p.at.y) } };
  }, []);

  const displayMenu = (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button size="sm" variant="ghost" data-testid="database-display-options" title="What the table cards show">
          Display
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuLabel>Columns</DropdownMenuLabel>
        <DropdownMenuRadioGroup value={display.mode} onValueChange={(v) => changeDisplay({ ...display, mode: v as TableDisplay })}>
          <DropdownMenuRadioItem value="all">All columns</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="keys">Keys only</DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="names">Names only</DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
        <DropdownMenuCheckboxItem
          checked={display.minimap}
          onCheckedChange={(checked) => changeDisplay({ ...display, minimap: checked === true })}
          data-testid="database-display-minimap"
        >
          Minimap
        </DropdownMenuCheckboxItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );

  const overlays = (
    <>
      <Dialog open={!!confirming} onOpenChange={(open) => !open && setConfirming(null)}>
        {confirming ? (
          <DialogContent title={`Delete foreign key ${confirming.fk.name}?`}>
            <p data-testid="fk-delete-confirm">
              The key is removed from table {confirming.table.name}
              {isLegacy(confirming.table) ? ", which is stored as a table file first" : ""}; its columns stay. Undo brings it back.
            </p>
            <div className="flex justify-end gap-2">
              <Button variant="ghost" onClick={() => setConfirming(null)}>
                Cancel
              </Button>
              <Button
                variant="danger"
                data-testid="fk-delete-confirm-button"
                onClick={() => {
                  const target = confirming;
                  setConfirming(null);
                  removeKey(target.table, target.fk);
                }}
              >
                Delete
              </Button>
            </div>
          </DialogContent>
        ) : null}
      </Dialog>
    </>
  );

  return {
    display,
    displayMenu,
    overlays,
    onLink,
    onConnectStart,
    onConnectEnd,
    onKeyDown,
    onDoubleClick,
    newTable,
    takePlaced,
    selectedFk,
    selectFk: setSelectedFk,
    editFk: (edgeId: string) => {
      const hit = byEdge.get(edgeId);
      if (hit && databaseId) void openEditForeignKey(services, qc, databaseId, allTables, hit.table, hit.fk);
    },
  };
}
