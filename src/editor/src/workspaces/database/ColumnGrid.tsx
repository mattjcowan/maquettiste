// The Database screen's column editor: the selected table's resolved columns in a dense grid (name, type, length, precision,
// scale, native type, nullable, default, comment, description), each edit written to the table's file through the same path as
// the table inspector (useTableFile), so every committed cell is one save and one undo step; a table the model lays out is
// stored as a table file on its first edit, in the same step. Arrow keys move, Enter or F2 edits (Enter again commits), Escape
// cancels, Tab moves right, Space toggles Null.
import { useRef, useState, useEffect, type KeyboardEvent } from "react";
import { ChevronDown, ChevronUp, KeyRound, Link2 } from "lucide-react";
import type { ColumnView, TableView } from "@/api/types";
import { useServices } from "@/app/context";
import { useEditor } from "@/state/store";
import { BUILTIN_TYPES } from "@/model/model";
import { cn } from "@/lib/cn";
import { Button } from "@/components/ui/button";
import { columnText, physicalHint, type ColumnField } from "./columnEdits";
import { useTableFile } from "./useTableFile";
import { tableIdentity } from "./storeTables";
import { tableKeyOf } from "@/search/engine";

type GridColumn = {
  key: ColumnField | "flags";
  label: string;
  title: string;
  width: string;
  kind: "flags" | "text" | "long" | "type" | "toggle";
  /** A physical cell: its hint says what the column's type is. */
  physical?: boolean;
};

export const GRID_COLUMNS: GridColumn[] = [
  { key: "flags", label: "", title: "Primary key or foreign key", width: "w-8", kind: "flags" },
  { key: "name", label: "Name", title: "The column name", width: "min-w-28", kind: "text" },
  { key: "type", label: "Type", title: "The column's built-in type", width: "w-24", kind: "type", physical: true },
  { key: "length", label: "Length", title: "The column's length", width: "w-14", kind: "text", physical: true },
  { key: "precision", label: "Prec.", title: "The column's precision", width: "w-12", kind: "text", physical: true },
  { key: "scale", label: "Scale", title: "The column's scale", width: "w-12", kind: "text", physical: true },
  {
    key: "nativeType",
    label: "Native",
    title: "The database's type: from the type map, or the one typed here (cleared, the type map's again)",
    width: "min-w-24",
    kind: "text",
    physical: true,
  },
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
const MONO = new Set<GridColumn["key"]>(["name", "type", "nativeType", "length", "precision", "scale"]);

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
      {/* Keyed by the table across its store as a file: a cell being edited when the store lands stays open. */}
      {!collapsed && table && databaseId ? <ColumnGrid key={tableIdentity(table.key)} table={table} databaseId={databaseId} /> : null}
    </section>
  );
}

/** The grid of one table's columns: the Database screen's column panel, and the Columns tab of a designed table's editor. */
export function ColumnGrid({ table, databaseId }: { table: TableView; databaseId: string }) {
  const { store } = useServices();
  const { write, busy } = useTableFile(table, databaseId);
  const columns = table.columns;
  // The row the inspector shows: the one picked here (a click, the arrow keys), none until then.
  // The inspected table is this one by its key, or by its file's while the grid still shows it by the key it was stored from.
  const isThisTable = (shown: { key: string; database: string } | null) =>
    !!shown && shown.database === databaseId && tableIdentity(shown.key) === tableIdentity(table.key);
  const picked = useEditor(store, (s) => (s.inspectedTable && isThisTable(s.inspectedTable) ? s.inspectedTable.column : null));
  const [active, setActiveCell] = useState(() => ({
    row: Math.max(
      0,
      columns.findIndex((c) => c.key === picked),
    ),
    col: 1,
  }));
  const setActive = (next: { row: number; col: number }) => {
    setActiveCell(next);
    const key = columns[next.row]?.key ?? null;
    if (key === picked) return;
    // The table's editor shows the grid without the table inspected yet: the pick inspects it with that column.
    const shown = store.getState().inspectedTable;
    if (isThisTable(shown)) store.getState().inspectColumn(key);
    else store.getState().inspectTable({ database: databaseId, key: table.key, column: key }, tableKeyOf(databaseId, table.key));
  };
  const [editing, setEditing] = useState<{ value: string } | null>(null);
  // The open editor as of now: a blur that follows a commit (the editor unmounting) must not commit twice.
  const editingRef = useRef(editing);
  editingRef.current = editing;
  const gridRef = useRef<HTMLTableElement>(null);
  // What takes the focus once rendered: the active cell, or the editor just opened (a render that does not show it yet, on a busy
  // screen, leaves the request for the one that does).
  const pendingFocus = useRef<"cell" | "editor" | null>(null);

  useEffect(() => {
    if (!pendingFocus.current || (pendingFocus.current === "editor" && !editing)) return;
    const cell = gridRef.current?.querySelector<HTMLElement>(`[data-cell="${active.row}:${active.col}"]`);
    const focusTarget = editing ? cell?.querySelector<HTMLElement>("input, select, textarea") : cell;
    if (!focusTarget) return;
    pendingFocus.current = null;
    focusTarget.focus();
    // The caret goes after the text, so typing over a cell continues the character that opened its editor.
    if (focusTarget instanceof HTMLInputElement || focusTarget instanceof HTMLTextAreaElement)
      focusTarget.setSelectionRange(focusTarget.value.length, focusTarget.value.length);
  });

  const focusCell = (row: number, col: number) => {
    setActive({ row: Math.max(0, Math.min(columns.length - 1, row)), col: Math.max(0, Math.min(GRID_COLUMNS.length - 1, col)) });
    pendingFocus.current = "cell";
  };

  const valueOf = (column: ColumnView, key: GridColumn["key"]): string => (key === "flags" || key === "nullable" ? "" : columnText(column, key));

  const startEdit = (row: number, col: number, initial?: string) => {
    const gc = GRID_COLUMNS[col];
    const column = columns[row];
    if (!gc || !column || !EDITABLE(gc)) return;
    if (gc.kind === "toggle") {
      write(column, "nullable", !column.nullable);
      return;
    }
    setEditing({ value: initial ?? valueOf(column, gc.key) });
    pendingFocus.current = "editor";
  };

  const commit = (move: "down" | "right" | "left" | "none" | "blur", value = editingRef.current?.value) => {
    if (editingRef.current === null || value === undefined) return;
    editingRef.current = null;
    const gc = GRID_COLUMNS[active.col];
    const column = columns[active.row];
    setEditing(null);
    if (column && gc && EDITABLE(gc) && gc.key !== "flags" && gc.key !== "nullable" && value !== valueOf(column, gc.key)) write(column, gc.key, value);
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
    <div className="min-h-0 flex-1 overflow-auto" aria-busy={busy || undefined}>
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
          {/* Rows by position, as the active cell is: a table stored as a file gives its columns new keys, and the row being
              edited keeps its editor. */}
          {columns.map((column, r) => (
            <tr key={r} className="h-[var(--mq-row-h)] border-t border-default" data-testid={`column-row-${column.name}`}>
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
                    title={cellTitle(gc, text, column)}
                    onClick={() => setActive({ row: r, col: c })}
                    onDoubleClick={() => startEdit(r, c)}
                    onKeyDown={(e) => onCellKeyDown(e, r, c)}
                    className={cn(
                      "h-[var(--mq-row-h)] px-1.5 align-middle outline-none",
                      isActive && "bg-accent-subtle ring-1 ring-inset ring-accent",
                      MONO.has(gc.key) && "font-mono",
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

/** A cell's hover text: a long text in full; on a physical cell, what the column's type is. */
function cellTitle(gc: GridColumn, text: string, column: ColumnView): string | undefined {
  if (gc.kind === "long" || gc.key === "comment") return text || undefined;
  if (gc.physical) return physicalHint(column, null);
  return undefined;
}
