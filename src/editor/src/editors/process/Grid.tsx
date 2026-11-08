// The process editor's grids (phase-3-design.md 6.2): one dense keyboard grid for states, transitions, guards,
// actions, events, meanings, scenarios and steps, entered as the attribute grid is (inspector/AttributeGrid.tsx):
// arrow keys move, Enter or F2 edits (Enter again commits), Escape cancels, Tab moves right, Ctrl+Enter adds a row,
// Ctrl+Delete removes one, and a toggle flips on Enter or Space. Each commit is one draft change, so one undo step.
// Rows are ROW_H high (--mq-row-h); a row's problems show as a badge on its first cell, a cell's marker on the cell.
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { GripVertical, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/cn";
import { DEFINITION_HINT } from "@/inspector/definition";
import { SEVERITY_ICON, SEVERITY_TONE } from "@/problems/findings";

/** A `popover` cell opens a dialog of its own (`onOpen`) in place of an inline editor: Enter, F2, Space or a double click. */
export type CellKind = "text" | "select" | "toggle" | "multi" | "pick" | "readonly" | "popover";

export interface GridOption {
  value: string;
  label: string;
  /** A select cell lists options with one group together under its name. */
  group?: string;
}

/** What F12 on a cell goes to, and what Shift+F12 lists the uses of (explorer-redesign.md, the keyboard model). */
export interface GridTarget {
  go: () => void;
  whereUsed: () => void;
}

const stickyStyle = (sticky: GridColumn<never>["sticky"]) =>
  sticky && typeof sticky === "object" ? { left: sticky.left, width: sticky.width, minWidth: sticky.width, maxWidth: sticky.width } : undefined;

/** Options in runs: those without a group stay loose, those of one group sit together. */
function groupedOptions(options: GridOption[]): { group: string | undefined; options: GridOption[] }[] {
  const out: { group: string | undefined; options: GridOption[] }[] = [];
  for (const o of options) {
    const last = out[out.length - 1];
    if (last && last.group === o.group) last.options.push(o);
    else out.push({ group: o.group, options: [o] });
  }
  return out;
}

export interface GridColumn<R> {
  key: string;
  label: string;
  /** A tooltip on the column header. */
  tooltip?: string;
  /** Fixed, or per row (a transition's detail is a select for an event, text for a duration). */
  kind: CellKind | ((row: R) => CellKind);
  width?: string;
  /** The widest the cell grows before its text is cut off (a max-w class); the full text is the cell's tooltip. */
  clip?: string;
  /**
   * Stays at the left edge while the grid scrolls sideways (the row's name). Several sticky columns in a row each give their
   * `left` edge and a fixed `width` (CSS lengths), so each rests against the one before it.
   */
  sticky?: boolean | { left: string; width: string };
  mono?: boolean;
  /** The text the cell shows. */
  value: (row: R) => string;
  /** The editor's starting text (text, multi) or value (select); defaults to `value`. */
  editValue?: (row: R) => string;
  /** The choices of a select cell; a multi cell's hint; a pick cell's checklist (the cell's text is the chosen values, comma-separated, in `editValue`). */
  options?: (row: R) => GridOption[];
  /** A toggle's state. */
  on?: (row: R) => boolean;
  /** A cell that cannot be edited on this row (shown dimmed). */
  locked?: (row: R) => boolean;
  /** A problem on the cell (a rule and its message): a red ring and a marker. */
  marker?: (row: R) => { rule: string; message: string } | null;
  /** The element or process node the cell references: F12 goes to it, Shift+F12 lists where it is used. */
  definition?: (row: R) => GridTarget | null;
  /** What an empty text cell says while it is edited (the format it takes). */
  placeholder?: string;
  /** A tooltip on the cell (a marker's message wins). */
  cellTitle?: (row: R) => string | undefined;
  /** A popover cell's dialog: opened by the keys and the double click that start an edit. */
  onOpen?: (row: R) => void;
  /** What a click on the cell also does (a locked cell that stands for something else, such as a transition's verb that is its event's). */
  onCellClick?: (row: R) => void;
  /** Custom content in place of the text (badges). */
  render?: (row: R) => ReactNode;
  /** A toggle's second box in the same cell, after the first (so the column does not widen). */
  after?: (row: R) => ReactNode;
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
  problems?: (row: R) => { rule: string; message: string; severity?: "error" | "warning" | "info" }[];
  /** Extra keys on a cell (true when handled). */
  onKey?: (e: KeyboardEvent<HTMLElement>, row: R) => boolean;
  /** Extra per-row buttons, before Remove. */
  actions?: (row: R) => ReactNode;
  hint?: string;
  empty?: string;
  /** Extra buttons beside Add. */
  toolbar?: ReactNode;
  /** Rows can be dragged (by the handle) or moved with Alt+Up and Alt+Down: the row and the index it moves to. */
  onReorder?: (row: R, to: number) => void;
  /** The Add button's text when it asks something first ("Add right…"); defaults to "Add <noun>". */
  addLabel?: string;
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
  addLabel,
  onReorder,
}: GridProps<R>) {
  const [dragging, setDragging] = useState<string | null>(null);
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
    // A pick cell's checklist sits in a portal, outside the cell.
    const picker = editing ? document.querySelector<HTMLElement>('[data-testid="pick-editor"]') : null;
    const target = editing
      ? (picker?.querySelector<HTMLInputElement>("input") ?? cell?.querySelector<HTMLInputElement | HTMLSelectElement>("input, select"))
      : cell;
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
    if (kind === "popover") {
      column.onOpen?.(row);
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
    if (e.altKey && (e.key === "ArrowUp" || e.key === "ArrowDown") && onReorder && row) {
      e.preventDefault();
      const to = r + (e.key === "ArrowUp" ? -1 : 1);
      if (to >= 0 && to < rows.length) {
        onReorder(row, to);
        // The moved row stays the active, selected one (focusCell would select the row now at `to`, which is the other one).
        setActive({ row: to, col: c });
        pendingFocus.current = true;
        onSelect?.(row);
      }
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
        <table ref={gridRef} role="grid" aria-label={label} className="w-full min-w-max border-collapse text-12">
          <thead>
            <tr className="bg-app">
              {columns.map((c) => (
                <th
                  key={c.key}
                  scope="col"
                  title={c.tooltip}
                  style={stickyStyle(c.sticky)}
                  className={cn("h-[var(--mq-row-h)] px-1.5 text-left text-11 font-semibold text-secondary", c.width, c.sticky && "sticky left-0 z-10 bg-app")}
                >
                  {c.label}
                </th>
              ))}
              {onRemove || actions || onReorder ? (
                <th className="sticky right-0 z-10 w-8 bg-app">
                  <span className="sr-only">Actions</span>
                </th>
              ) : null}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, r) => {
              const rowProblems = problems?.(row) ?? [];
              return (
                <tr
                  key={row.id}
                  className={cn("h-[var(--mq-row-h)] border-t border-default", dragging === row.id && "opacity-50")}
                  data-testid={`${testid}-row`}
                  data-id={row.id}
                  aria-rowindex={r + 2}
                  onDragOver={onReorder && dragging ? (e) => e.preventDefault() : undefined}
                  onDrop={
                    onReorder && dragging
                      ? (e) => {
                          e.preventDefault();
                          const moved = rows.find((x) => x.id === dragging);
                          setDragging(null);
                          if (moved && moved.id !== row.id) onReorder(moved, r);
                        }
                      : undefined
                  }
                >
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
                        title={
                          marker ? `${marker.rule} ${marker.message}` : (column.cellTitle?.(row) ?? (column.definition?.(row) ? DEFINITION_HINT : undefined))
                        }
                        onClick={() => {
                          if (active.row !== r || active.col !== c) setActive({ row: r, col: c });
                          // The first row is active before anything is selected: a click still selects it.
                          if (active.row !== r || (selected !== undefined && selected !== row.id)) onSelect?.(row);
                          column.onCellClick?.(row);
                        }}
                        onDoubleClick={() => startEdit(r, c)}
                        onKeyDown={(e) => onCellKeyDown(e, r, c)}
                        style={stickyStyle(column.sticky)}
                        className={cn(
                          "h-[var(--mq-row-h)] max-w-64 px-1.5 py-0 align-middle outline-none",
                          column.clip,
                          column.sticky && "sticky left-0 z-10 bg-surface shadow-[1px_0_0_var(--mq-border-default)]",
                          // A sticky cell stays opaque when active, or the cells that slide under it show through.
                          isActive && (column.sticky ? "bg-[image:linear-gradient(var(--mq-accent-subtle),var(--mq-accent-subtle))]" : "bg-accent-subtle"),
                          isActive && "ring-1 ring-inset ring-accent",
                          marker && "ring-1 ring-inset ring-danger",
                          column.mono && "font-mono",
                          locked && "text-secondary",
                        )}
                      >
                        {isEditing ? (
                          kind === "pick" ? (
                            <PickEditor
                              label={`${column.label} of ${rowName(row)}`}
                              anchor={() => gridRef.current?.querySelector<HTMLElement>(`[data-cell="${r}:${c}"]`) ?? null}
                              options={column.options?.(row) ?? []}
                              value={editing.value}
                              onChange={(value) => setEditing({ value })}
                              onDone={(move) => commit(move)}
                            />
                          ) : kind === "select" ? (
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
                              {groupedOptions(column.options?.(row) ?? []).map((g) =>
                                g.group === undefined ? (
                                  g.options.map((o) => (
                                    <option key={o.value} value={o.value}>
                                      {o.label}
                                    </option>
                                  ))
                                ) : (
                                  <optgroup key={g.group} label={g.group}>
                                    {g.options.map((o) => (
                                      <option key={o.value} value={o.value}>
                                        {o.label}
                                      </option>
                                    ))}
                                  </optgroup>
                                ),
                              )}
                            </select>
                          ) : (
                            <input
                              aria-label={`${column.label} of ${rowName(row)}`}
                              className={cn("h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12", column.mono && "font-mono")}
                              value={editing.value}
                              placeholder={column.placeholder}
                              title={kind === "multi" ? `Comma-separated: ${(column.options?.(row) ?? []).map((o) => o.label).join(", ")}` : undefined}
                              onChange={(e) => setEditing({ value: e.target.value })}
                              onKeyDown={onEditorKeyDown}
                              onBlur={() => commit("blur")}
                            />
                          )
                        ) : (
                          <span className="flex min-w-0 items-center gap-1" style={c === 0 && depth ? { paddingLeft: `${depth(row) * 12}px` } : undefined}>
                            {c === 0 && rowProblems.length ? <RowProblemIcon problems={rowProblems} /> : null}
                            {kind === "toggle" ? (
                              <span
                                aria-label={column.on?.(row) ? "yes" : "no"}
                                onClick={() => startEdit(r, c)}
                                className={cn("inline-block size-3 rounded-[3px] border border-input", column.on?.(row) && "border-accent bg-accent")}
                              />
                            ) : column.render ? (
                              column.render(row)
                            ) : (
                              <span className="truncate" title={column.clip ? column.value(row) : undefined}>
                                {column.value(row)}
                              </span>
                            )}
                            {kind === "toggle" ? column.after?.(row) : null}
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
                  {onRemove || actions || onReorder ? (
                    <td className="sticky right-0 z-10 whitespace-nowrap bg-surface px-1 shadow-[-1px_0_0_var(--mq-border-default)]">
                      <span className="flex items-center justify-end gap-0.5">
                        {onReorder ? (
                          <span
                            draggable
                            role="img"
                            title={`Drag to move ${noun} ${rowName(row)}, or press Alt+Up or Alt+Down on a cell`}
                            aria-label={`Move ${noun} ${rowName(row)}`}
                            data-testid={`${testid}-drag`}
                            className="inline-flex size-5 cursor-grab items-center justify-center text-secondary"
                            onDragStart={(e) => {
                              e.dataTransfer.effectAllowed = "move";
                              e.dataTransfer.setData("text/plain", row.id);
                              setDragging(row.id);
                            }}
                            onDragEnd={() => setDragging(null)}
                          >
                            <GripVertical className="size-3.5" />
                          </span>
                        ) : null}
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
              <Plus /> {addLabel ?? `Add ${noun}`}
            </Button>
          ) : null}
          {toolbar}
        </span>
        {onCommit || onKey || onRemove ? <span className="text-11 text-secondary">{hint}</span> : null}
      </div>
    </div>
  );
}

/** A pick cell's editor: a checklist, grouped, under the cell. Enter, Escape or leaving it keeps what is ticked. */
function PickEditor({
  label,
  anchor,
  options,
  value,
  onChange,
  onDone,
}: {
  label: string;
  /** The cell the checklist opens under. */
  anchor: () => HTMLElement | null;
  options: GridOption[];
  value: string;
  onChange: (value: string) => void;
  onDone: (move: "none" | "blur") => void;
}) {
  const chosen = new Set(value.split(",").filter(Boolean));
  const toggle = (id: string) => {
    const next = new Set(chosen);
    if (!next.delete(id)) next.add(id);
    onChange(
      options
        .filter((o) => next.has(o.value))
        .map((o) => o.value)
        .join(","),
    );
  };
  // A click anywhere outside the checklist saves it, as Enter does (a blur alone is not raised by every click); once.
  const settled = useRef(false);
  const done = useRef(onDone);
  done.current = onDone;
  useEffect(() => {
    const outside = (e: PointerEvent) => {
      if (settled.current || box.current?.contains(e.target as Node | null)) return;
      settled.current = true;
      done.current("blur");
    };
    document.addEventListener("pointerdown", outside, true);
    return () => document.removeEventListener("pointerdown", outside, true);
  }, []);
  // The checklist sits in a portal at the cell's corner, so the grid's scrolling box never clips it.
  const box = useRef<HTMLDivElement>(null);
  const [at, setAt] = useState<{ top: number; left: number } | null>(null);
  useLayoutEffect(() => {
    const rect = anchor()?.getBoundingClientRect();
    if (!rect) return;
    const height = Math.min(box.current?.offsetHeight ?? 240, 240);
    setAt({
      top: Math.max(4, Math.min(rect.top, window.innerHeight - height - 4)),
      left: Math.max(4, Math.min(rect.left, window.innerWidth - 232)),
    });
  }, [anchor, options.length]);
  return createPortal(
    <div
      ref={box}
      role="group"
      aria-label={label}
      data-testid="pick-editor"
      style={{ top: at?.top ?? 0, left: at?.left ?? 0, visibility: at ? "visible" : "hidden" }}
      className="fixed z-50 flex max-h-60 min-w-56 flex-col overflow-y-auto rounded-control border border-input bg-surface p-1 text-12 shadow-md"
      onClick={(e) => e.stopPropagation()}
      onDoubleClick={(e) => e.stopPropagation()}
      onKeyDown={(e) => {
        e.stopPropagation();
        // The boxes change the choice as they are ticked, so Escape closes the list and keeps it, as a click outside does.
        if (e.key === "Escape" || e.key === "Enter") {
          e.preventDefault();
          settled.current = true;
          onDone("none");
        }
      }}
      onBlur={(e) => {
        if (settled.current || e.currentTarget.contains(e.relatedTarget as Node | null)) return;
        settled.current = true;
        onDone("blur");
      }}
    >
      {!options.length ? <span className="px-1 text-secondary">Nothing to pick.</span> : null}
      {groupedOptions(options).map((g, i) => (
        <div key={g.group ?? `loose-${i}`} role="group" aria-label={g.group}>
          {g.group !== undefined ? <div className="px-1 pt-0.5 text-11 font-semibold text-secondary">{g.group}</div> : null}
          {g.options.map((o) => (
            <label key={o.value} className="flex h-[var(--mq-row-h)] cursor-pointer items-center gap-1.5 px-1 hover:bg-accent-subtle">
              <input type="checkbox" checked={chosen.has(o.value)} onChange={() => toggle(o.value)} />
              <span className="truncate">{o.label}</span>
            </label>
          ))}
        </div>
      ))}
    </div>,
    document.body,
  );
}

/** The icon of a row's findings: the worst severity decides its shape and colour (a warning is never red). */
export function RowProblemIcon({ problems }: { problems: { rule: string; message: string; severity?: "error" | "warning" | "info" }[] }) {
  const worst = (["error", "warning", "info"] as const).find((s) => problems.some((p) => p.severity === s)) ?? "error";
  const Icon = SEVERITY_ICON[worst];
  return (
    <Icon
      className={cn("size-3.5 shrink-0", SEVERITY_TONE[worst])}
      role="img"
      aria-label={problems.map((p) => `${p.rule} ${p.message}`).join("; ")}
      data-testid="row-problem"
      data-severity={worst}
    />
  );
}
