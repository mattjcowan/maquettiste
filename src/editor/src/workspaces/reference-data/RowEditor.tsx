// The row editor: a side panel beside a seed grid (the Rows tab, and an entity's or relation's Seed data tab) that
// shows one row with a labelled control per grid column, so long text reads and edits in full. Each control carries
// the field's description under its label; text fields are text areas that grow with their text, a relation end is a
// picker over the far entity's rows. Save (Ctrl+S) writes every changed cell in one save of the seed, so one undo step;
// Cancel (Esc) closes it without writing. The previous and next row buttons (Alt+Up, Alt+Down) save first.
import { useEffect, useMemo, useState, type KeyboardEvent, type MutableRefObject } from "react";
import { ChevronDown, ChevronUp } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Select, Textarea } from "@/components/ui/input";
import { cn } from "@/lib/cn";
import { hasRowChanges, isLongText, rowEditChanges, rowFormValues, type EndOption, type GridColumn, type GridRow, type RowEditChanges } from "./rowsModel";

export interface RowEditorProps {
  columns: readonly GridColumn[];
  row: GridRow;
  /** The row's position in the grid, from 1. */
  number: number;
  total: number;
  /** The seed's name, shown when the target has several seeds. */
  seedName?: string;
  endOptions: ReadonlyMap<string, EndOption[]>;
  /** A locale column's fallback text for this row (the placeholder of an untranslated cell). */
  fallback(column: GridColumn): string | undefined;
  /** The error a cell (or the row, key "*") has, if any. */
  errorOf(key: string): string | undefined;
  onSave(changes: RowEditChanges): void;
  onClose(): void;
  /** Moves to the previous (-1) or next (1) row. */
  onStep(delta: -1 | 1): void;
  /** Set to this editor's save, so opening another row saves this one first. */
  saveRef?: MutableRefObject<(() => void) | null>;
}

export function RowEditor({ columns, row, number, total, seedName, endOptions, fallback, errorOf, onSave, onClose, onStep, saveRef }: RowEditorProps) {
  const shown = useMemo(() => rowFormValues(row, columns), [row, columns]);
  const [base, setBase] = useState(shown);
  const [values, setValues] = useState(shown);
  const changes = rowEditChanges(columns, base, values);
  const dirty = hasRowChanges(changes);
  // The row changed elsewhere (an edit in the grid, undo, a save's canonical form): a clean form follows it.
  const shownKey = JSON.stringify(shown);
  useEffect(() => {
    if (dirty || JSON.stringify(base) === shownKey) return;
    setBase(shown);
    setValues(shown);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- follows the row's text only
  }, [shownKey, dirty]);

  const save = () => {
    if (!dirty) return;
    onSave(changes);
    setBase(values);
  };
  useEffect(() => {
    if (!saveRef) return;
    saveRef.current = save;
    return () => {
      if (saveRef.current === save) saveRef.current = null;
    };
  });
  const step = (delta: -1 | 1) => {
    save();
    onStep(delta);
  };
  const onKeyDown = (e: KeyboardEvent<HTMLElement>) => {
    const mod = e.ctrlKey || e.metaKey;
    if (mod && (e.key === "s" || e.key === "S")) {
      e.preventDefault();
      e.stopPropagation();
      save();
    } else if (e.key === "Escape") {
      e.preventDefault();
      e.stopPropagation();
      onClose();
    } else if (e.altKey && (e.key === "ArrowUp" || e.key === "ArrowDown")) {
      e.preventDefault();
      e.stopPropagation();
      step(e.key === "ArrowUp" ? -1 : 1);
    }
  };
  const code = values.code;
  const rowError = errorOf("*");

  return (
    <aside aria-label="Row editor" className="flex min-h-0 min-w-0 flex-1 flex-col bg-surface" data-testid="row-editor" onKeyDown={onKeyDown}>
      <header className="flex items-center gap-1 border-b border-default px-2 py-1">
        <h3 className="min-w-0 flex-1 truncate text-13 font-semibold" data-testid="row-editor-title">
          Row {number} of {total}
          {code ? <span className="font-mono font-normal text-secondary"> · {code}</span> : null}
          {seedName ? <span className="font-normal text-secondary"> · {seedName}</span> : null}
        </h3>
        <Button size="icon-row" variant="ghost" label="Previous row, saving this one" shortcut="Alt+Up" disabled={number <= 1} onClick={() => step(-1)}>
          <ChevronUp />
        </Button>
        <Button size="icon-row" variant="ghost" label="Next row, saving this one" shortcut="Alt+Down" disabled={number >= total} onClick={() => step(1)}>
          <ChevronDown />
        </Button>
      </header>
      {rowError ? (
        <p role="alert" className="border-b border-default px-2 py-1 text-12 text-danger">
          {rowError}
        </p>
      ) : null}
      <form
        className="flex min-h-0 flex-1 flex-col"
        onSubmit={(e) => {
          e.preventDefault();
          save();
        }}
      >
        <div className="flex min-h-0 flex-1 flex-col gap-2 overflow-auto p-2">
          {columns.map((c, i) => {
            const id = `row-editor-${c.key.replace(/[^A-Za-z0-9_-]/g, "_")}`;
            const value = values[c.key] ?? "";
            const set = (v: string) => setValues((prev) => ({ ...prev, [c.key]: v }));
            const error = errorOf(c.key);
            const shownName = c.title ?? c.label;
            const control = c.end ? (
              <Select
                id={id}
                autoFocus={i === 0}
                className="h-7 text-12"
                value={value}
                aria-invalid={error ? true : undefined}
                onChange={(e) => set(e.target.value)}
              >
                <option value="">(none)</option>
                {(endOptions.get(c.end) ?? []).map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.label}
                  </option>
                ))}
                {value && !(endOptions.get(c.end) ?? []).some((o) => o.id === value) ? <option value={value}>{value}</option> : null}
              </Select>
            ) : isLongText(c) ? (
              <Textarea
                id={id}
                autoFocus={i === 0}
                className="min-h-0 resize-y py-1 text-12 leading-5"
                rows={Math.min(10, Math.max(c.multiline ? 3 : 1, value.split("\n").length, Math.ceil(value.length / 48)))}
                value={value}
                placeholder={c.locale ? fallback(c) : undefined}
                aria-invalid={error ? true : undefined}
                onChange={(e) => set(e.target.value)}
              />
            ) : (
              <Input
                id={id}
                autoFocus={i === 0}
                className={cn("h-7 text-12", (c.key === "code" || c.type !== "string") && "font-mono")}
                value={value}
                placeholder={c.collection ? "codes separated by ;" : c.locale ? fallback(c) : undefined}
                aria-invalid={error ? true : undefined}
                onChange={(e) => set(e.target.value)}
              />
            );
            return (
              <div key={c.key} className="flex flex-col gap-0.5" data-testid={`row-editor-field-${c.label}`}>
                <label htmlFor={id} className="flex items-baseline gap-1 text-12 font-medium text-secondary">
                  <span className="text-primary">{shownName}</span>
                  {c.title && c.title !== c.label ? <span className="font-mono text-11">{c.label}</span> : null}
                  {c.required ? <span className="text-11">required</span> : null}
                  {c.collection ? <span className="text-11">many</span> : null}
                </label>
                {c.help ? <p className="whitespace-pre-line text-11 text-secondary">{c.help}</p> : null}
                {control}
                {error ? <p className="text-11 text-danger">{error}</p> : null}
              </div>
            );
          })}
        </div>
        <footer className="flex items-center gap-2 border-t border-default px-2 py-1">
          <span className="min-w-0 flex-1 truncate text-11 text-secondary">{dirty ? "Unsaved changes" : "No changes"}</span>
          <Button size="sm" variant="ghost" type="button" onClick={onClose} title="Close without saving (Esc)">
            Cancel
          </Button>
          <Button size="sm" variant="primary" type="submit" disabled={!dirty} title="Save the row as one change you can undo (Ctrl+S)">
            Save
          </Button>
        </footer>
      </form>
    </aside>
  );
}
