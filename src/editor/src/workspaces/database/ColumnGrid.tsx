// The Database screen's column editor: the selected table's resolved columns in a dense grid (name, type, native type,
// nullable, default, comment, description), each edit written to the table's file through the same save path as the
// inspector (useElementEdits: the draft manager for an existing file, one create for a new overlay), so every committed cell
// is one save and one undo step. Arrow keys move, Enter or F2 edits (Enter again commits), Escape cancels, Tab moves right,
// Space toggles Null. A synthesized table without an overlay gets one on its first edit, holding just that column's change.
import { useMemo, useRef, useState, useEffect, type KeyboardEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { ChevronDown, ChevronUp, KeyRound, Link2 } from "lucide-react";
import type { ColumnView, TableView } from "@/api/types";
import { invalidateResolved, useElements, useIndex } from "@/api/queries";
import { useServices } from "@/app/context";
import { useElementEdits } from "@/editors/mappingEdit";
import { BUILTIN_TYPES } from "@/model/model";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import { cn } from "@/lib/cn";
import { Button } from "@/components/ui/button";
import { columnText, isOverlayFor, newOverlay, overlayCandidates, setColumnField, tableFileTarget, type ColumnField } from "./columnEdits";

type GridColumn = {
  key: ColumnField | "flags" | "native";
  label: string;
  title: string;
  width: string;
  kind: "flags" | "text" | "long" | "type" | "toggle" | "readonly";
};

const GRID_COLUMNS: GridColumn[] = [
  { key: "flags", label: "", title: "Primary key or foreign key", width: "w-8", kind: "flags" },
  { key: "name", label: "Name", title: "The column name; cleared, a synthesized column takes its conventional name again", width: "min-w-28", kind: "text" },
  { key: "type", label: "Type", title: "The built-in type", width: "w-28", kind: "type" },
  { key: "native", label: "Native", title: "The database's type, from the type map", width: "min-w-24", kind: "readonly" },
  { key: "nullable", label: "Null", title: "Whether the column accepts nulls (Space toggles)", width: "w-10", kind: "toggle" },
  { key: "default", label: "Default", title: "The literal default", width: "min-w-20", kind: "text" },
  {
    key: "comment",
    label: "Comment",
    title: "The comment the database gets: an explicit comment, else (with the comments convention) the description",
    width: "min-w-32 max-w-64",
    kind: "text",
  },
  { key: "description", label: "Description", title: "What the column means; Shift+Enter adds a line", width: "min-w-40 max-w-80", kind: "long" },
];

const EDITABLE = (c: GridColumn) => c.kind === "text" || c.kind === "long" || c.kind === "type" || c.kind === "toggle";

export function ColumnPanel({ table, databaseId }: { table: TableView | null; databaseId: string | null }) {
  const [collapsed, setCollapsed] = useState(false);
  return (
    <section
      className={cn("flex shrink-0 flex-col border-t border-default bg-surface", collapsed ? "" : "max-h-[45%] min-h-24")}
      aria-label="Columns"
      data-testid="column-panel"
    >
      <div className="flex h-6 shrink-0 items-center gap-2 border-b border-default px-2 text-12">
        <span className="font-semibold">Columns</span>
        <span className="min-w-0 flex-1 truncate text-secondary" data-testid="column-panel-table">
          {table ? `${table.schema ? `${table.schema}.` : ""}${table.name} · ${table.columns.length} columns` : "Pick a table to edit its columns"}
        </span>
        <Button
          variant="ghost"
          size="icon-row"
          label={collapsed ? "Show columns" : "Hide columns"}
          onClick={() => setCollapsed((c) => !c)}
          data-testid="toggle-columns"
        >
          {collapsed ? <ChevronUp /> : <ChevronDown />}
        </Button>
      </div>
      {!collapsed && table && databaseId ? <ColumnGrid key={table.key} table={table} databaseId={databaseId} /> : null}
    </section>
  );
}

function ColumnGrid({ table, databaseId }: { table: TableView; databaseId: string }) {
  const qc = useQueryClient();
  const { store } = useServices();
  const edits = useElementEdits();
  const index = useIndex();
  const target = useMemo(() => tableFileTarget(table, databaseId), [table, databaseId]);
  const candidates = useMemo(() => overlayCandidates(index.data ?? [], table, target, databaseId), [index.data, table, target, databaseId]);
  const docs = useElements(target.kind === "file" ? [target.id] : candidates);
  const fileId = useMemo(() => {
    if (target.kind === "file") return target.id;
    for (const id of candidates) if (isOverlayFor(docs.byId.get(id)?.json as Record<string, unknown> | undefined, target, databaseId)) return id;
    return null;
  }, [target, candidates, docs.byId, databaseId]);
  const fileJson = fileId ? (docs.byId.get(fileId)?.json as Record<string, unknown> | undefined) : undefined;
  const columns = table.columns;
  const [active, setActive] = useState({ row: 0, col: 1 });
  const [editing, setEditing] = useState<{ value: string } | null>(null);
  // The open editor as of now: a blur that follows a commit (the editor unmounting) must not commit twice.
  const editingRef = useRef(editing);
  editingRef.current = editing;
  // Edits run one after another (a second edit before the first overlay exists must not create a second overlay), each
  // reading the latest file: the one found in the index, else the overlay this grid has just created.
  const [busy, setBusy] = useState(0);
  const queue = useRef<Promise<void>>(Promise.resolve());
  const created = useRef<string | null>(null);
  if (fileId) created.current = null;
  const latest = useRef({ fileId, fileJson, pending: docs.pending });
  latest.current = { fileId, fileJson, pending: docs.pending };
  const gridRef = useRef<HTMLTableElement>(null);
  const pendingFocus = useRef(false);

  useEffect(() => {
    if (!pendingFocus.current) return;
    const cell = gridRef.current?.querySelector<HTMLElement>(`[data-cell="${active.row}:${active.col}"]`);
    const focusTarget = editing ? cell?.querySelector<HTMLElement>("input, select, textarea") : cell;
    if (!focusTarget) return;
    pendingFocus.current = false;
    focusTarget.focus();
    // The caret goes after the text, so typing over a cell continues the character that opened its editor.
    if (focusTarget instanceof HTMLInputElement || focusTarget instanceof HTMLTextAreaElement)
      focusTarget.setSelectionRange(focusTarget.value.length, focusTarget.value.length);
  });

  const focusCell = (row: number, col: number) => {
    setActive({ row: Math.max(0, Math.min(columns.length - 1, row)), col: Math.max(0, Math.min(GRID_COLUMNS.length - 1, col)) });
    pendingFocus.current = true;
  };

  /** Writes one field of one column: one save of the table's file (or one create of its overlay), so one undo step. */
  const run = async (column: ColumnView, field: ColumnField, value: string | boolean) => {
    const { fileId: known, fileJson: json, pending } = latest.current;
    const id = known ?? created.current;
    if (!id && pending && target.kind === "overlay") {
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
    } else if (target.kind === "overlay") {
      const doc = newOverlay(target, databaseId, column, field, value, newId);
      if (doc && (await edits.create(doc, `Edit column ${column.name}`))) created.current = String(doc.id);
    }
    invalidateResolved(qc);
  };
  const write = (column: ColumnView, field: ColumnField, value: string | boolean) => {
    setBusy((n) => n + 1);
    queue.current = queue.current
      .then(() => run(column, field, value))
      .catch((e: unknown) => store.getState().notify(`Column ${column.name}: ${(e as Error).message}`, "error"))
      .finally(() => setBusy((n) => n - 1));
  };

  const valueOf = (column: ColumnView, key: GridColumn["key"]): string =>
    key === "flags" || key === "nullable" ? "" : key === "native" ? column.nativeType : columnText(column, key);

  const startEdit = (row: number, col: number, initial?: string) => {
    const gc = GRID_COLUMNS[col];
    const column = columns[row];
    if (!gc || !column || !EDITABLE(gc)) return;
    if (gc.kind === "toggle") {
      write(column, "nullable", !column.nullable);
      return;
    }
    setEditing({ value: initial ?? valueOf(column, gc.key) });
    pendingFocus.current = true;
  };

  const commit = (move: "down" | "right" | "left" | "none" | "blur", value = editingRef.current?.value) => {
    if (editingRef.current === null || value === undefined) return;
    editingRef.current = null;
    const gc = GRID_COLUMNS[active.col];
    const column = columns[active.row];
    setEditing(null);
    if (column && gc && gc.key !== "flags" && gc.key !== "native" && gc.key !== "nullable" && value !== valueOf(column, gc.key)) write(column, gc.key, value);
    if (move === "down") focusCell(active.row + 1, active.col);
    else if (move === "right") focusCell(active.row, active.col + 1);
    else if (move === "left") focusCell(active.row, active.col - 1);
    else if (move === "none") focusCell(active.row, active.col);
  };

  const onCellKeyDown = (e: KeyboardEvent<HTMLElement>, row: number, col: number) => {
    if (editing) return;
    const moves: Record<string, [number, number]> = { ArrowDown: [1, 0], ArrowUp: [-1, 0], ArrowRight: [0, 1], ArrowLeft: [0, -1] };
    if (moves[e.key]) {
      e.preventDefault();
      focusCell(row + moves[e.key][0], col + moves[e.key][1]);
    } else if (e.key === "Tab" && (e.shiftKey ? col > 0 : col < GRID_COLUMNS.length - 1)) {
      e.preventDefault();
      focusCell(row, col + (e.shiftKey ? -1 : 1));
    } else if (e.key === "Enter" || e.key === "F2" || e.key === " ") {
      e.preventDefault();
      startEdit(row, col);
    } else if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey && ["text", "long"].includes(GRID_COLUMNS[col].kind)) {
      e.preventDefault();
      startEdit(row, col, e.key);
    }
  };

  const onEditorKeyDown = (e: KeyboardEvent<HTMLElement>) => {
    if (e.key === "Enter" && e.shiftKey && e.currentTarget instanceof HTMLTextAreaElement) return; // a new line
    if (e.key === "Enter") {
      e.preventDefault();
      commit("down");
    } else if (e.key === "Escape") {
      e.preventDefault();
      setEditing(null);
      focusCell(active.row, active.col);
    } else if (e.key === "Tab") {
      e.preventDefault();
      commit(e.shiftKey ? "left" : "right");
    }
  };

  return (
    <div className="min-h-0 flex-1 overflow-auto" aria-busy={busy > 0 || undefined}>
      <table ref={gridRef} role="grid" aria-label={`Columns of ${table.name}`} className="w-full border-collapse text-12" data-testid="column-grid">
        <thead className="sticky top-0 z-[1] bg-app">
          <tr>
            {GRID_COLUMNS.map((gc) => (
              <th key={gc.key} scope="col" title={gc.title} className={cn("h-6 px-1.5 text-left text-11 font-semibold text-secondary", gc.width)}>
                {gc.label || <span className="sr-only">Keys</span>}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {columns.map((column, r) => (
            <tr key={column.key} className="h-[var(--mq-row-h)] border-t border-default" data-testid={`column-row-${column.name}`}>
              {GRID_COLUMNS.map((gc, c) => {
                const isActive = active.row === r && active.col === c;
                const isEditing = isActive && editing !== null;
                const text = valueOf(column, gc.key);
                return (
                  <td
                    key={gc.key}
                    role="gridcell"
                    data-cell={`${r}:${c}`}
                    data-column={gc.key}
                    tabIndex={isActive && !isEditing ? 0 : -1}
                    aria-selected={isActive}
                    aria-readonly={!EDITABLE(gc) || undefined}
                    title={gc.kind === "long" || gc.key === "comment" ? text || undefined : undefined}
                    onClick={() => setActive({ row: r, col: c })}
                    onDoubleClick={() => startEdit(r, c)}
                    onKeyDown={(e) => onCellKeyDown(e, r, c)}
                    className={cn(
                      "h-[var(--mq-row-h)] px-1.5 align-middle outline-none",
                      isActive && "bg-accent-subtle ring-1 ring-inset ring-accent",
                      (gc.key === "type" || gc.key === "native" || gc.key === "name") && "font-mono",
                      gc.kind === "readonly" && "text-secondary",
                    )}
                  >
                    {isEditing ? (
                      gc.kind === "type" ? (
                        <select
                          aria-label={`Type of ${column.name}`}
                          className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12"
                          value={editing.value}
                          onChange={(e) => commit("none", e.target.value)}
                          onKeyDown={onEditorKeyDown}
                          onBlur={() => commit("blur")}
                        >
                          {(BUILTIN_TYPES as string[]).includes(editing.value) ? null : (
                            <option value={editing.value} disabled>
                              {editing.value}
                            </option>
                          )}
                          {BUILTIN_TYPES.map((t) => (
                            <option key={t} value={t}>
                              {t}
                            </option>
                          ))}
                        </select>
                      ) : gc.kind === "long" ? (
                        <textarea
                          aria-label={`${gc.label} of ${column.name}`}
                          title="Shift+Enter adds a line"
                          rows={Math.min(6, Math.max(2, editing.value.split("\n").length))}
                          className="block w-full min-w-48 resize-y rounded-[4px] border border-input bg-surface px-1 text-12 leading-5"
                          value={editing.value}
                          onChange={(e) => setEditing({ value: e.target.value })}
                          onKeyDown={onEditorKeyDown}
                          onBlur={() => commit("blur")}
                        />
                      ) : (
                        <input
                          aria-label={`${gc.label} of ${column.name}`}
                          className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12"
                          value={editing.value}
                          onChange={(e) => setEditing({ value: e.target.value })}
                          onKeyDown={onEditorKeyDown}
                          onBlur={() => commit("blur")}
                        />
                      )
                    ) : gc.kind === "flags" ? (
                      <span className="flex items-center gap-0.5">
                        {column.isPrimaryKey ? <KeyRound className="size-3.5 text-accent" aria-label="primary key" /> : null}
                        {column.isForeignKey ? <Link2 className="size-3.5 text-secondary" aria-label="foreign key" /> : null}
                      </span>
                    ) : gc.kind === "toggle" ? (
                      <span
                        aria-label={column.nullable ? "nullable" : "not null"}
                        className={cn("inline-block size-3 rounded-[3px] border border-input", column.nullable && "border-accent bg-accent")}
                      />
                    ) : (
                      <span className="block truncate">{text}</span>
                    )}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
