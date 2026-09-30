// The process editor's grids (phase-3-design.md 6.2): one dense keyboard grid for states, transitions, guards,
// actions, events, meanings, scenarios and steps, entered as the attribute grid is (inspector/AttributeGrid.tsx):
// arrow keys move, Enter or F2 edits (Enter again commits), Escape cancels, Tab moves right, Ctrl+Enter adds a row,
// Ctrl+Delete removes one, and a toggle flips on Enter or Space. Each commit is one draft change, so one undo step.
// Rows are ROW_H high (--mq-row-h); a row's problems show as a badge on its first cell, a cell's marker on the cell.
import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { CircleAlert, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/cn";
import { DEFINITION_HINT } from "@/inspector/definition";

export type CellKind = "text" | "select" | "toggle" | "multi" | "readonly";

export interface GridOption {
  value: string;
  label: string;
}

/** What F12 on a cell goes to, and what Shift+F12 lists the uses of (explorer-redesign.md, the keyboard model). */
export interface GridTarget {
  go: () => void;
  whereUsed: () => void;
}

export interface GridColumn<R> {
  key: string;
  label: string;
  /** Fixed, or per row (a transition's detail is a select for an event, text for a duration). */
  kind: CellKind | ((row: R) => CellKind);
  width?: string;
  mono?: boolean;
  /** The text the cell shows. */
  value: (row: R) => string;
  /** The editor's starting text (text, multi) or value (select); defaults to `value`. */
  editValue?: (row: R) => string;
  /** The choices of a select cell; a multi cell's hint. */
  options?: (row: R) => GridOption[];
  /** A toggle's state. */
  on?: (row: R) => boolean;
  /** A cell that cannot be edited on this row (shown dimmed). */
  locked?: (row: R) => boolean;
  /** A problem on the cell (a rule and its message): a red ring and a marker. */
  marker?: (row: R) => { rule: string; message: string } | null;
  /** The element or process node the cell references: F12 goes to it, Shift+F12 lists where it is used. */
  definition?: (row: R) => GridTarget | null;
  /** Custom content in place of the text (badges). */
  render?: (row: R) => ReactNode;
}

export interface GridProps<R extends { id: string }> {
  label: string;
  testid: string;
  /** The row noun for buttons ("state", "transition"). */
  noun: string;
  rows: R[];
  columns: GridColumn<R>[];
  rowName: (row: R) => string;
  onCommit?: (row: R, key: string, value: string | boolean) => void;
  onAdd?: (after: R | undefined) => void;
  onRemove?: (row: R) => void;
  /** The row to show active (a navigation or an added row). */
  selected?: string | null;
  onSelect?: (row: R) => void;
  /** Tree grid: the first column's indent level. */
  depth?: (row: R) => number;
  /** A row's problems (validation badges from the Problems store). */
  problems?: (row: R) => { rule: string; message: string }[];
  /** Extra keys on a cell (true when handled). */
  onKey?: (e: KeyboardEvent<HTMLElement>, row: R) => boolean;
  /** Extra per-row buttons, before Remove. */
  actions?: (row: R) => ReactNode;
  hint?: string;
  empty?: string;
  /** Extra buttons beside Add. */
  toolbar?: ReactNode;
}

const kindOf = <R,>(column: GridColumn<R>, row: R): CellKind => (typeof column.kind === "function" ? column.kind(row) : column.kind);

export function Grid<R extends { id: string }>({
  label,
  testid,
  noun,
  rows,
  columns,
  rowName,
  onCommit,
  onAdd,
  onRemove,
  selected,
  onSelect,
  depth,
  problems,
  onKey,
  actions,
  hint = "Enter edits · Tab moves · Ctrl+Enter adds · Ctrl+Delete removes",
  empty,
  toolbar,
}: GridProps<R>) {
  const [active, setActive] = useState({ row: 0, col: 0 });
  const [editing, setEditing] = useState<{ value: string; select?: boolean } | null>(null);
  const gridRef = useRef<HTMLTableElement>(null);
  const pendingFocus = useRef(false);
  // A select moved by the keyboard (arrow keys, letters) keeps its choice until Enter, Tab or blur commits it; a
  // pointer choice commits at once. One commit per gesture, so one change and one undo step.
  const keyedSelect = useRef(false);

  // A navigation (or an added row) moves the active row to the row asked for, and focuses it once rendered.
  const [shown, setShown] = useState<string | null | undefined>(undefined);
  if (selected !== shown) {
    const at = selected ? rows.findIndex((r) => r.id === selected) : -1;
    // A row asked for before the draft that adds it renders: checked again on the next render.
    if (at >= 0 || !selected) {
      setShown(selected);
      if (at >= 0 && at !== active.row) {
        setActive({ row: at, col: active.col });
        setEditing(null);
      }
      if (at >= 0 && shown !== undefined) pendingFocus.current = true;
    }
  }

  useEffect(() => {
    if (!pendingFocus.current) return;
    const cell = gridRef.current?.querySelector<HTMLElement>(`[data-cell="${active.row}:${active.col}"]`);
    const target = editing ? cell?.querySelector<HTMLInputElement | HTMLSelectElement>("input, select") : cell;
    if (!target) return;
    pendingFocus.current = false;
    target.focus();
    if (editing?.select && target instanceof HTMLInputElement) target.select();
  });

  const focusCell = (row: number, col: number) => {
    const r = Math.max(0, Math.min(rows.length - 1, row));
    setActive({ row: r, col: Math.max(0, Math.min(columns.length - 1, col)) });
    pendingFocus.current = true;
    if (rows[r] && r !== active.row) onSelect?.(rows[r]);
  };

  const editable = (r: number, c: number) => {
    const column = columns[c];
    const row = rows[r];
    return !!row && !!column && !!onCommit && kindOf(column, row) !== "readonly" && !column.locked?.(row);
  };

  const startEdit = (r: number, c: number, initial?: string) => {
    if (!editable(r, c)) return;
    const column = columns[c];
    const row = rows[r];
    const kind = kindOf(column, row);
    if (kind === "toggle") {
      onCommit!(row, column.key, !column.on?.(row));
      return;
    }
    keyedSelect.current = false;
    setEditing({ value: initial ?? (column.editValue ?? column.value)(row), select: initial === undefined && kind !== "select" });
    pendingFocus.current = true;
  };

  const commit = (move: "down" | "right" | "left" | "none" | "blur", value = editing?.value) => {
    if (editing === null || value === undefined) return;
    const column = columns[active.col];
    const row = rows[active.row];
    setEditing(null);
    if (row && value !== (column.editValue ?? column.value)(row)) onCommit?.(row, column.key, value);
    if (move === "down") focusCell(active.row + 1, active.col);
    else if (move === "right") focusCell(active.row, active.col + 1);
    else if (move === "left") focusCell(active.row, active.col - 1);
    else if (move === "none") focusCell(active.row, active.col);
  };

  const add = () => {
    onAdd?.(rows[active.row]);
  };

  const onCellKeyDown = (e: KeyboardEvent<HTMLElement>, r: number, c: number) => {
    if (editing) return;
    const row = rows[r];
    if (row && onKey?.(e, row)) return;
    const target = e.key === "F12" && !e.ctrlKey && !e.metaKey && !e.altKey && row ? columns[c]?.definition?.(row) : null;
    if (target) {
      e.preventDefault();
      e.stopPropagation();
      if (e.shiftKey) target.whereUsed();
      else target.go();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && e.key === "Enter" && onAdd) {
      e.preventDefault();
      add();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && (e.key === "Delete" || e.key === "Backspace") && onRemove && row) {
      e.preventDefault();
      onRemove(row);
      focusCell(r - 1, c);
      return;
    }
    const moves: Record<string, [number, number]> = { ArrowDown: [1, 0], ArrowUp: [-1, 0], ArrowRight: [0, 1], ArrowLeft: [0, -1] };
    if (moves[e.key]) {
      e.preventDefault();
      focusCell(r + moves[e.key][0], c + moves[e.key][1]);
      return;
    }
    if (e.key === "Tab" && !e.shiftKey && c < columns.length - 1) {
      e.preventDefault();
      focusCell(r, c + 1);
      return;
    }
    if (e.key === "Tab" && e.shiftKey && c > 0) {
      e.preventDefault();
      focusCell(r, c - 1);
      return;
    }
    if (e.key === "Enter" || e.key === "F2" || e.key === " ") {
      e.preventDefault();
      startEdit(r, c);
      return;
    }
    const kind = row && columns[c] ? kindOf(columns[c], row) : null;
    if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey && (kind === "text" || kind === "multi") && editable(r, c)) {
      e.preventDefault();
      startEdit(r, c, e.key);
    }
  };

  const onEditorKeyDown = (e: KeyboardEvent<HTMLInputElement | HTMLSelectElement>) => {
    e.stopPropagation();
    if (e.key === "Enter") {
      e.preventDefault();
      commit(e.shiftKey ? "none" : "down", (e.target as HTMLInputElement).value);
    } else if (e.key === "Escape") {
      e.preventDefault();
      setEditing(null);
      focusCell(active.row, active.col);
    } else if (e.key === "Tab") {
      e.preventDefault();
      commit(e.shiftKey ? "left" : "right", (e.target as HTMLInputElement).value);
    }
  };

  return (
    <div className="flex flex-col gap-1" data-testid={testid}>
      <div className="overflow-x-auto rounded-control border border-default">
        <table ref={gridRef} role="grid" aria-label={label} className="w-full border-collapse text-12">
          <thead>
            <tr className="bg-app">
              {columns.map((c) => (
                <th key={c.key} scope="col" className={cn("h-[var(--mq-row-h)] px-1.5 text-left text-11 font-semibold text-secondary", c.width)}>
                  {c.label}
                </th>
              ))}
              {onRemove || actions ? (
                <th className="w-8">
                  <span className="sr-only">Actions</span>
                </th>
              ) : null}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, r) => {
              const rowProblems = problems?.(row) ?? [];
              return (
                <tr key={row.id} className="h-[var(--mq-row-h)] border-t border-default" data-testid={`${testid}-row`} data-id={row.id} aria-rowindex={r + 2}>
                  {columns.map((column, c) => {
                    const isActive = active.row === r && active.col === c;
                    const isEditing = isActive && editing !== null;
                    const marker = column.marker?.(row) ?? null;
                    const kind = kindOf(column, row);
                    const locked = kind === "readonly" || !!column.locked?.(row);
                    return (
                      <td
                        key={column.key}
                        role="gridcell"
                        data-cell={`${r}:${c}`}
                        data-column={column.key}
                        tabIndex={isActive && !isEditing ? 0 : -1}
                        aria-selected={isActive}
                        aria-readonly={locked || undefined}
                        aria-invalid={marker ? true : undefined}
                        title={marker ? `${marker.rule} ${marker.message}` : column.definition?.(row) ? DEFINITION_HINT : undefined}
                        onClick={() => {
                          if (active.row !== r || active.col !== c) setActive({ row: r, col: c });
                          // The first row is active before anything is selected: a click still selects it.
                          if (active.row !== r || (selected !== undefined && selected !== row.id)) onSelect?.(row);
                        }}
                        onDoubleClick={() => startEdit(r, c)}
                        onKeyDown={(e) => onCellKeyDown(e, r, c)}
                        className={cn(
                          "h-[var(--mq-row-h)] max-w-64 px-1.5 py-0 align-middle outline-none",
                          isActive && "bg-accent-subtle ring-1 ring-inset ring-accent",
                          marker && "ring-1 ring-inset ring-danger",
                          column.mono && "font-mono",
                          locked && "text-secondary",
                        )}
                      >
                        {isEditing ? (
                          kind === "select" ? (
                            <select
                              aria-label={`${column.label} of ${rowName(row)}`}
                              className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12"
                              value={editing.value}
                              onChange={(e) => (keyedSelect.current ? setEditing({ value: e.target.value }) : commit("none", e.target.value))}
                              onKeyDown={(e) => {
                                if (!["Enter", "Escape", "Tab"].includes(e.key)) keyedSelect.current = true;
                                onEditorKeyDown(e);
                              }}
                              onBlur={() => commit("blur")}
                            >
                              {(column.options?.(row) ?? []).map((o) => (
                                <option key={o.value} value={o.value}>
                                  {o.label}
                                </option>
                              ))}
                            </select>
                          ) : (
                            <input
                              aria-label={`${column.label} of ${rowName(row)}`}
                              className={cn("h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12", column.mono && "font-mono")}
                              value={editing.value}
                              title={kind === "multi" ? `Comma-separated: ${(column.options?.(row) ?? []).map((o) => o.label).join(", ")}` : undefined}
                              onChange={(e) => setEditing({ value: e.target.value })}
                              onKeyDown={onEditorKeyDown}
                              onBlur={() => commit("blur")}
                            />
                          )
                        ) : (
                          <span className="flex min-w-0 items-center gap-1" style={c === 0 && depth ? { paddingLeft: `${depth(row) * 12}px` } : undefined}>
                            {c === 0 && rowProblems.length ? (
                              <CircleAlert
                                className="size-3.5 shrink-0 text-danger"
                                role="img"
                                aria-label={rowProblems.map((p) => `${p.rule} ${p.message}`).join("; ")}
                                data-testid="row-problem"
                              />
                            ) : null}
                            {kind === "toggle" ? (
                              <span
                                aria-label={column.on?.(row) ? "yes" : "no"}
                                className={cn("inline-block size-3 rounded-[3px] border border-input", column.on?.(row) && "border-accent bg-accent")}
                              />
                            ) : column.render ? (
                              column.render(row)
                            ) : (
                              <span className="truncate">{column.value(row)}</span>
                            )}
                            {marker ? (
                              <span
                                className="ml-auto shrink-0 rounded-[3px] border border-danger px-1 font-mono text-11 leading-none text-danger"
                                data-testid="cell-marker"
                                data-rule={marker.rule}
                              >
                                {marker.rule}
                              </span>
                            ) : null}
                          </span>
                        )}
                      </td>
                    );
                  })}
                  {onRemove || actions ? (
                    <td className="whitespace-nowrap px-1">
                      <span className="flex items-center justify-end gap-0.5">
                        {actions?.(row)}
                        {onRemove ? (
                          <Button variant="ghost" size="icon-row" label={`Remove ${noun} ${rowName(row)}`} onClick={() => onRemove(row)} tabIndex={-1}>
                            <Trash2 />
                          </Button>
                        ) : null}
                      </span>
                    </td>
                  ) : null}
                </tr>
              );
            })}
            {!rows.length ? (
              <tr>
                <td colSpan={columns.length + 1} className="h-[var(--mq-row-h)] px-1.5 text-secondary">
                  {empty ?? `No ${noun}s yet.`}
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
      <div className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-1">
          {onAdd ? (
            <Button size="sm" variant="ghost" onClick={add} data-testid={`${testid}-add`}>
              <Plus /> Add {noun}
            </Button>
          ) : null}
          {toolbar}
        </span>
        {onCommit || onKey || onRemove ? <span className="text-11 text-secondary">{hint}</span> : null}
      </div>
    </div>
  );
}
