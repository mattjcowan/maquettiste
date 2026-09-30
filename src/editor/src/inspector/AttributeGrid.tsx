// The attribute grid (S14 "spreadsheet-style attribute grid with keyboard entry"): TanStack Table
// for the columns; arrow keys move, Enter or F2 edits (Enter again commits and saves), Escape
// cancels, Tab moves right, Ctrl+Enter adds a row, Ctrl+Delete removes one. Edits go through the
// element's draft, so the canvas card changes as you type.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent } from "react";
import { createColumnHelper, flexRender, getCoreRowModel, useReactTable } from "@tanstack/react-table";
import { KeyRound, Plus, Trash2 } from "lucide-react";
import type { AttributeDoc, ElementSummary, EntityDoc, ModelJson } from "@/api/types";
import { isBuiltin } from "@/model/model";
import { TypePicker, type TypePick } from "./TypePicker";
import { newId } from "@/lib/ids";
import { cn } from "@/lib/cn";
import { Button } from "@/components/ui/button";
import type { Diagnostic } from "@/api/types";

type ColumnKey = "key" | "name" | "type" | "length" | "precision" | "scale" | "required" | "unique" | "indexed" | "default";

const COLUMNS: { key: ColumnKey; label: string; kind: "toggle" | "text" | "number" | "type" | "key"; width: string }[] = [
  { key: "key", label: "Key", kind: "key", width: "w-9" },
  { key: "name", label: "Name", kind: "text", width: "min-w-28" },
  { key: "type", label: "Type", kind: "type", width: "min-w-28" },
  { key: "length", label: "Len", kind: "number", width: "w-14" },
  { key: "precision", label: "Prec", kind: "number", width: "w-14" },
  { key: "scale", label: "Scale", kind: "number", width: "w-14" },
  { key: "required", label: "Req", kind: "toggle", width: "w-10" },
  { key: "unique", label: "Uniq", kind: "toggle", width: "w-10" },
  { key: "indexed", label: "Idx", kind: "toggle", width: "w-10" },
  { key: "default", label: "Default", kind: "text", width: "min-w-20" },
];

export interface AttributeGridProps {
  /** The element the attributes belong to: a different one resets the active cell and closes the editor. */
  owner?: string;
  label: string;
  attributes: AttributeDoc[];
  keyIds?: string[];
  typeOptions: ElementSummary[];
  /** Go to definition on a type cell (F12, Ctrl/Cmd+click; inspector/definition.ts); true when handled. */
  definition?: {
    onKey(target: string | undefined, e: KeyboardEvent<HTMLElement>): boolean;
    onClick(target: string | undefined, e: MouseEvent<HTMLElement>): boolean;
  };
  diagnostics?: Diagnostic[];
  pointerBase?: string;
  onChange: (update: (json: ModelJson) => void, commit: boolean) => void;
  /** The JSON array the rows live in ("attributes"). */
  field?: "attributes";
  withKey?: boolean;
}

function typeRefId(a: AttributeDoc): string | undefined {
  return isBuiltin(a.type) ? undefined : (a.type as { ref?: string } | undefined)?.ref;
}

function typeValue(a: AttributeDoc): string {
  return isBuiltin(a.type) ? a.type : `ref:${typeRefId(a) ?? ""}`;
}

/** An attribute's type as the grid shows it (`decimal`, `Money[]`, `→ Unit of measure`). */
export function attributeTypeLabel(a: AttributeDoc, typeOptions: ElementSummary[]): string {
  return display(a, "type", typeOptions);
}

function display(a: AttributeDoc, key: ColumnKey, typeOptions: ElementSummary[]): string {
  switch (key) {
    case "type": {
      const many = a.collection ? "[]" : "";
      if (isBuiltin(a.type)) return a.type + many;
      const t = typeOptions.find((o) => o.id === typeRefId(a));
      if (!t) return "?";
      // A reference-typed attribute shows `→ Unit of measure` (RT 1.9).
      return t.kind === "reference-type" ? `→ ${t.displayName || t.name}${many}` : t.name + many;
    }
    case "default":
      return a.default === undefined ? "" : typeof a.default === "string" ? a.default : JSON.stringify(a.default);
    case "length":
    case "precision":
    case "scale":
      return a[key] === undefined ? "" : String(a[key]);
    case "name":
      return a.name;
    default:
      return "";
  }
}

const NUMERIC = new Set(["int16", "int32", "int64", "decimal", "float", "double"]);

/** A literal default typed like its attribute: numbers for numeric types, booleans for bool. */
export function parseDefault(a: AttributeDoc, raw: string): unknown {
  if (isBuiltin(a.type) && NUMERIC.has(a.type) && /^-?\d+(\.\d+)?$/.test(raw)) return Number(raw);
  if (a.type === "bool" && (raw === "true" || raw === "false")) return raw === "true";
  return raw;
}

const helper = createColumnHelper<AttributeDoc>();

export function AttributeGrid({
  label,
  attributes,
  keyIds = [],
  typeOptions,
  diagnostics = [],
  pointerBase = "/attributes",
  onChange,
  withKey = true,
  definition,
  owner,
}: AttributeGridProps) {
  const columns = useMemo(() => COLUMNS.filter((c) => withKey || c.key !== "key"), [withKey]);
  const table = useReactTable({
    data: attributes,
    columns: columns.map((c) => helper.display({ id: c.key, header: c.label })),
    getCoreRowModel: getCoreRowModel(),
    getRowId: (row) => row.id,
  });
  const [active, setActive] = useState<{ row: number; col: number }>({ row: 0, col: withKey ? 1 : 0 });
  // `select` selects the editor's text when it opens (a new row's generated name is replaced by typing).
  const [editing, setEditing] = useState<{ value: string; select?: boolean } | null>(null);
  // The entity editor keeps the grid mounted from one entity to the next (General mode, §4.5): another owner starts
  // on its first row with no cell open (state reset during render, before the stale cell could show).
  const [shownOwner, setShownOwner] = useState(owner);
  if (owner !== shownOwner) {
    setShownOwner(owner);
    setActive({ row: 0, col: withKey ? 1 : 0 });
    setEditing(null);
  }
  const gridRef = useRef<HTMLTableElement>(null);
  // Focus follows the grid state after React renders it: the active cell, or its editor while editing. The request
  // stays pending until the target exists, so a row added through the draft is focused once it is rendered.
  const pendingFocus = useRef(false);

  useEffect(() => {
    if (!pendingFocus.current) return;
    const cell = gridRef.current?.querySelector<HTMLElement>(`[data-cell="${active.row}:${active.col}"]`);
    const target = editing ? cell?.querySelector<HTMLInputElement | HTMLSelectElement>("input, select") : cell;
    if (!target) return;
    pendingFocus.current = false;
    target.focus();
    if (editing?.select && target instanceof HTMLInputElement) target.select();
  });

  const invalidAt = (row: number, key: ColumnKey) =>
    diagnostics.some((d) => d.jsonPointer === `${pointerBase}/${row}/${key}` || (key === "name" && d.jsonPointer === `${pointerBase}/${row}`));

  const focusCell = (row: number, col: number) => {
    setActive({ row, col });
    pendingFocus.current = true;
  };

  const apply = (row: number, key: ColumnKey, raw: string | boolean, commit: boolean) => {
    const id = attributes[row]?.id;
    onChange((json) => {
      const list = (json as { attributes?: AttributeDoc[] }).attributes ?? [];
      const a = list.find((x) => x.id === id);
      if (!a) return;
      const record = a as unknown as Record<string, unknown>;
      if (key === "name") a.name = String(raw);
      else if (key === "type") {
        const v = String(raw);
        a.type = v.startsWith("ref:") ? { ref: v.slice(4) } : (v as AttributeDoc["type"]);
        if (a.type !== "string" && a.type !== "binary") delete a.length;
        if (a.type !== "decimal") {
          delete a.precision;
          delete a.scale;
        }
      } else if (key === "length" || key === "precision" || key === "scale") {
        const n = String(raw).trim();
        if (n === "") delete record[key];
        else if (/^\d+$/.test(n)) record[key] = Number(n);
      } else if (key === "default") {
        const v = String(raw);
        if (v === "") delete a.default;
        else a.default = parseDefault(a, v);
      } else if (key === "required" || key === "unique" || key === "indexed") {
        if (raw) record[key] = true;
        else delete record[key];
      } else if (key === "key") {
        const entity = json as EntityDoc;
        const ids = new Set(entity.key?.attributes ?? []);
        if (ids.has(a.id)) ids.delete(a.id);
        else ids.add(a.id);
        if (ids.size === 0) delete entity.key;
        else entity.key = { ...(entity.key ?? {}), attributes: [...ids] } as EntityDoc["key"];
      }
    }, commit);
  };

  const applyType = (row: number, pick: TypePick) => {
    const id = attributes[row]?.id;
    onChange((json) => {
      const a = ((json as { attributes?: AttributeDoc[] }).attributes ?? []).find((x) => x.id === id);
      if (!a) return;
      const v = pick.value;
      a.type = v.startsWith("ref:") ? { ref: v.slice(4) } : (v as AttributeDoc["type"]);
      if (a.type !== "string" && a.type !== "binary") delete a.length;
      if (a.type !== "decimal") {
        delete a.precision;
        delete a.scale;
      }
      if (pick.collection) a.collection = true;
      else delete a.collection;
      if (pick.required) a.required = true;
      else delete a.required;
    }, true);
  };

  const addRow = () => {
    const id = newId();
    const used = new Set(attributes.map((a) => a.name));
    let n = attributes.length + 1;
    while (used.has(`attribute${n}`)) n++;
    onChange((json) => {
      const record = json as { attributes?: AttributeDoc[] };
      record.attributes = [...(record.attributes ?? []), { id, name: `attribute${n}`, type: "string" }];
    }, false);
    focusCell(attributes.length, withKey ? 1 : 0);
    setEditing({ value: `attribute${n}`, select: true });
  };

  const removeRow = (row: number) => {
    const id = attributes[row]?.id;
    onChange((json) => {
      const record = json as EntityDoc;
      record.attributes = (record.attributes ?? []).filter((a) => a.id !== id);
      if (record.key) {
        const ids = record.key.attributes.filter((x) => x !== id);
        if (ids.length) record.key.attributes = ids;
        else delete record.key;
      }
    }, true);
    focusCell(Math.max(0, row - 1), active.col);
  };

  const startEdit = (row: number, col: number, initial?: string) => {
    const column = columns[col];
    const a = attributes[row];
    if (!a || !column) return;
    if (column.kind === "toggle") {
      apply(row, column.key, !(a as unknown as Record<string, unknown>)[column.key], true);
      return;
    }
    if (column.kind === "key") {
      apply(row, "key", true, true);
      return;
    }
    setEditing({ value: initial ?? (column.kind === "type" ? typeValue(a) : display(a, column.key, typeOptions)) });
    pendingFocus.current = true;
  };

  // "blur" commits without taking focus back: focus already moved somewhere else.
  const commit = (move: "down" | "right" | "left" | "none" | "blur") => {
    if (!editing) return;
    const column = columns[active.col];
    apply(active.row, column.key, editing.value, true);
    setEditing(null);
    if (move === "down") focusCell(Math.min(attributes.length - 1, active.row + 1), active.col);
    else if (move === "right") focusCell(active.row, Math.min(columns.length - 1, active.col + 1));
    else if (move === "left") focusCell(active.row, Math.max(0, active.col - 1));
    else if (move === "none") focusCell(active.row, active.col);
  };

  const onCellKeyDown = (e: KeyboardEvent<HTMLElement>, row: number, col: number) => {
    if (editing) return;
    if (columns[col]?.kind === "type" && attributes[row] && definition?.onKey(typeRefId(attributes[row]), e)) return;
    if ((e.ctrlKey || e.metaKey) && e.key === "Enter") {
      e.preventDefault();
      addRow();
      return;
    }
    if ((e.ctrlKey || e.metaKey) && (e.key === "Delete" || e.key === "Backspace")) {
      e.preventDefault();
      removeRow(row);
      return;
    }
    const moves: Record<string, [number, number]> = { ArrowDown: [1, 0], ArrowUp: [-1, 0], ArrowRight: [0, 1], ArrowLeft: [0, -1] };
    if (moves[e.key]) {
      e.preventDefault();
      const [dr, dc] = moves[e.key];
      focusCell(Math.max(0, Math.min(attributes.length - 1, row + dr)), Math.max(0, Math.min(columns.length - 1, col + dc)));
      return;
    }
    if (e.key === "Tab" && !e.shiftKey && col < columns.length - 1) {
      e.preventDefault();
      focusCell(row, col + 1);
      return;
    }
    if (e.key === "Tab" && e.shiftKey && col > 0) {
      e.preventDefault();
      focusCell(row, col - 1);
      return;
    }
    if (e.key === "Enter" || e.key === "F2" || e.key === " ") {
      e.preventDefault();
      startEdit(row, col);
      return;
    }
    if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey && ["text", "number"].includes(columns[col].kind)) {
      e.preventDefault();
      startEdit(row, col, e.key);
    }
  };

  const onEditorKeyDown = (e: KeyboardEvent<HTMLElement>) => {
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

  const keySet = new Set(keyIds);

  return (
    <div className="flex flex-col gap-1">
      <div className="overflow-x-auto rounded-control border border-default">
        <table ref={gridRef} role="grid" aria-label={label} className="w-full border-collapse text-12" data-testid="attribute-grid">
          <thead>
            {table.getHeaderGroups().map((group) => (
              <tr key={group.id} className="bg-app">
                {group.headers.map((h, i) => (
                  <th key={h.id} scope="col" className={cn("h-7 px-1.5 text-left text-11 font-semibold text-secondary", columns[i].width)}>
                    {flexRender(h.column.columnDef.header, h.getContext())}
                  </th>
                ))}
                <th className="w-8" aria-label="Actions" />
              </tr>
            ))}
          </thead>
          <tbody>
            {table.getRowModel().rows.map((row, r) => {
              const a = row.original;
              return (
                <tr key={row.id} className="h-[var(--mq-row-h)] border-t border-default" aria-rowindex={r + 2}>
                  {columns.map((column, c) => {
                    const isActive = active.row === r && active.col === c;
                    const isEditing = isActive && editing !== null;
                    const record = a as unknown as Record<string, unknown>;
                    const invalid = invalidAt(r, column.key);
                    return (
                      <td
                        key={column.key}
                        role="gridcell"
                        data-cell={`${r}:${c}`}
                        data-column={column.key}
                        tabIndex={isActive && !isEditing ? 0 : -1}
                        aria-selected={isActive}
                        aria-invalid={invalid || undefined}
                        onClick={(e) => {
                          if (column.kind === "type" && !isEditing && definition?.onClick(typeRefId(a), e)) return;
                          setActive({ row: r, col: c });
                        }}
                        title={column.kind === "type" && typeRefId(a) ? "F12 or Ctrl+click: go to the type" : undefined}
                        onDoubleClick={() => startEdit(r, c)}
                        onKeyDown={(e) => onCellKeyDown(e, r, c)}
                        className={cn(
                          "h-[var(--mq-row-h)] px-1.5 align-middle outline-none",
                          isActive && "bg-accent-subtle ring-1 ring-inset ring-accent",
                          invalid && "text-danger ring-1 ring-inset ring-danger",
                          column.kind === "number" && "text-right font-mono",
                          column.key === "type" && "font-mono",
                        )}
                      >
                        {isEditing ? (
                          column.kind === "type" ? (
                            <TypePicker
                              label={`Type of ${a.name}`}
                              value={typeValue(a)}
                              collection={a.collection === true}
                              required={a.required === true}
                              options={typeOptions}
                              onPick={(pick, move) => {
                                applyType(r, pick);
                                setEditing(null);
                                if (move === "down") focusCell(Math.min(attributes.length - 1, r + 1), c);
                                else if (move === "right") focusCell(r, Math.min(columns.length - 1, c + 1));
                                else if (move === "left") focusCell(r, Math.max(0, c - 1));
                                else focusCell(r, c);
                              }}
                              onCancel={(refocus) => {
                                setEditing(null);
                                if (refocus) focusCell(r, c);
                              }}
                            />
                          ) : (
                            <input
                              aria-label={`${column.label} of ${a.name}`}
                              className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12"
                              value={editing.value}
                              onChange={(e) => setEditing({ value: e.target.value })}
                              onKeyDown={onEditorKeyDown}
                              onBlur={() => commit("blur")}
                            />
                          )
                        ) : column.kind === "key" ? (
                          keySet.has(a.id) ? (
                            <KeyRound className="size-3.5 text-accent" aria-label="key" />
                          ) : (
                            <span className="sr-only">not key</span>
                          )
                        ) : column.kind === "toggle" ? (
                          <span
                            aria-label={record[column.key] ? "yes" : "no"}
                            className={cn("inline-block size-3 rounded-[3px] border border-input", record[column.key] === true && "border-accent bg-accent")}
                          />
                        ) : (
                          <span className="block truncate">{display(a, column.key, typeOptions)}</span>
                        )}
                      </td>
                    );
                  })}
                  <td className="px-1">
                    <Button variant="ghost" size="icon-row" label={`Remove attribute ${a.name}`} onClick={() => removeRow(r)} tabIndex={-1}>
                      <Trash2 />
                    </Button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="flex items-center justify-between">
        <Button size="sm" variant="ghost" onClick={addRow}>
          <Plus /> Add attribute
        </Button>
        <span className="text-11 text-secondary">Enter edits · Tab moves · Ctrl+Enter adds · Ctrl+Delete removes</span>
      </div>
    </div>
  );
}
