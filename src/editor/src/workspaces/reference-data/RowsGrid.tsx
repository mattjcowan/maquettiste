// The Rows grid (reference-types-seeds-localization.md 4.4), for every seed target: a reference type's Rows tab and
// the Seed data tab of an entity or a relation. A virtualized grid over all the target's seeds, the first column
// frozen on the left, errors as a red cell outline with the diagnostic as tooltip, and the SPEC section 14
// spreadsheet keys (rowsModel.gridAction). Every edit goes through the seed's draft, so it saves like any element
// and undoes; tab-separated paste writes cells and adds rows past the end; CSV import previews, then applies as one
// save; CSV export downloads the seed. Import CSV and Export CSV sit in the grid's header. A relation-end column's
// cell is a row of the far entity's seeds, edited with a picker; a description cell is a text area (Shift+Enter adds a
// line); locale label and description columns belong to reference types.
import { rowHeight } from "@/design/density";
import { useCallback, useMemo, useRef, useState, type ClipboardEvent, type KeyboardEvent } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { Download, Plus, Upload } from "lucide-react";
import { useElements, useValidation } from "@/api/queries";
import type { Diagnostic, ModelJson, ReferenceTypeDoc, SeedDoc } from "@/api/types";
import type { AppServices } from "@/app/context";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Spinner } from "@/components/ui/misc";
import { useDraftDocument } from "@/inspector/useDraft";
import { cn } from "@/lib/cn";
import { newId } from "@/lib/ids";
import { useEditor } from "@/state/store";
import { createSeed, exportCsv } from "./actions";
import { useQueries } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import type { TranslationEntry } from "@/api/types";
import { l10nKeys, useLocalization, writeTranslations } from "@/l10n/queries";
import { useContentLocale } from "@/l10n/contentLocale";
import { ImportCsvDialog } from "./dialogs";
import {
  deleteRows,
  duplicateRow,
  formatCell,
  gridAction,
  labelCompleteness,
  shownLocales,
  gridColumns,
  gridRows,
  insertRow,
  localeColumns,
  moveRows,
  parseCell,
  parseDelimited,
  pasteMatrix,
  setCells,
  toTsv,
  withSeedColumns,
  withTranslatedLabels,
  ONE_LOCALE_HINT,
  type EndOption,
  type TranslatedField,
  type GridAction,
  type GridColumn,
  type GridRow,
} from "./rowsModel";

const NO_LOCALES: string[] = [];
const NO_OPTIONS: ReadonlyMap<string, EndOption[]> = new Map();

const WIDTH: Record<string, number> = { code: 128, label: 200, description: 240 };
const POINTER = /^\/rows\/(\d+)(?:\/values\/(\d+))?$/;

interface Cell {
  row: number;
  col: number;
}

/** A reference type's Rows tab: code, label and description, the user fields, and label and description columns per shown locale. */
export function RowsTab({ typeId, seeds, onTab, onFocusSearch }: { typeId: string; seeds: string[]; onTab(index: number): void; onFocusSearch(): void }) {
  const services = useServices();
  const type = useDraftDocument(typeId).json as unknown as ReferenceTypeDoc | undefined;
  const columns = useMemo(() => (type ? gridColumns(type) : null), [type]);
  if (!type || !columns) return <Spinner />;
  return (
    <SeedGrid
      name={type.name}
      columns={columns}
      seeds={seeds}
      localized
      createSeed={() => createSeed(services, type)}
      onTab={onTab}
      onFocusSearch={onFocusSearch}
    />
  );
}

export interface SeedGridProps {
  /** The target's name: the grid's label, and the seed listed first (RT 1.2: the seed named after the type). */
  name: string;
  /** The target's columns; null while they load. */
  columns: GridColumn[] | null;
  seeds: string[];
  /** Label and description columns per locale (reference types: their rows' labels and descriptions are translated). */
  localized?: boolean;
  /** Also show the columns a seed lists that the target no longer offers (entity and relation seeds). */
  keepStoredColumns?: boolean;
  /** Relation-end columns: entity id → the rows its picker lists. */
  endOptions?: ReadonlyMap<string, EndOption[]>;
  /** The seed new rows go to when the target has none. */
  createSeed(): Promise<string | null>;
  onTab?(index: number): void;
  onFocusSearch?(): void;
}

export function SeedGrid({
  name,
  columns: baseColumns,
  seeds,
  localized = false,
  keepStoredColumns = false,
  endOptions = NO_OPTIONS,
  createSeed: makeSeed,
  onTab,
  onFocusSearch,
}: SeedGridProps) {
  const services: AppServices = useServices();
  const { store, drafts } = services;
  const loaded = useElements(seeds);
  const draftMap = useEditor(store, (s) => s.drafts);
  const validation = useValidation();
  const seedDocs = useMemo(() => {
    const docs = seeds.map((id) => (draftMap[id]?.json ?? loaded.byId.get(id)?.json) as unknown as SeedDoc | undefined).filter((s): s is SeedDoc => !!s);
    // The seed named after the type first (RT 1.2), then by name: the grid's order is stable.
    return docs.sort((a, b) => Number(b.name === name) - Number(a.name === name) || a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
  }, [seeds, draftMap, loaded.byId, name]);
  // RT 4.4: with two or more locales, a translated label and description column per locale, edited into the locale's shard.
  const localization = useLocalization();
  const l10n = useMemo(
    () => ({ ...localization, locales: localized ? localization.locales : NO_LOCALES, enabled: localized && localization.enabled }),
    [localization, localized],
  );
  const combine = useCallback(
    (results: { data?: { entries: TranslationEntry[] } }[]) => {
      const out = new Map<string, Map<string, string>>();
      const descriptions = new Map<string, Map<string, string>>();
      const entries = new Map<string, TranslationEntry>();
      l10n.locales.forEach((locale, li) => {
        const map = new Map<string, string>();
        const described = new Map<string, string>();
        out.set(locale, map);
        descriptions.set(locale, described);
        for (let si = 0; si < seeds.length; si++)
          for (const e of results[li * seeds.length + si]?.data?.entries ?? []) {
            if (e.field !== "label" && e.field !== "description") continue;
            entries.set(`${locale}|${e.field}|${e.id}`, e);
            if (e.translation !== null) (e.field === "label" ? map : described).set(e.id, e.translation);
          }
      });
      return { out, descriptions, entries };
    },
    [l10n.locales, seeds.length],
  );
  const labels = useQueries({
    queries: l10n.locales.flatMap((locale) =>
      seeds.map((seed) => ({ queryKey: l10nKeys.owner(locale, seed), queryFn: () => endpoints.getTranslations(locale, { owner: seed }), staleTime: 5_000 })),
    ),
    combine,
  });
  // The label column of the content locale only; "All locales" shows every translated locale's (RT 4.4).
  const { effective: contentLocale } = useContentLocale();
  const [allLocales, setAllLocales] = useState(false);
  const shown = useMemo(() => shownLocales(l10n.locales, contentLocale, allLocales), [l10n.locales, contentLocale, allLocales]);
  const columns = useMemo(
    () => (baseColumns ? [...(keepStoredColumns ? withSeedColumns(baseColumns, seedDocs) : baseColumns), ...localeColumns(shown)] : []),
    [baseColumns, keepStoredColumns, seedDocs, shown],
  );
  const rows = useMemo(() => withTranslatedLabels(gridRows(seedDocs), labels.out, labels.descriptions), [seedDocs, labels]);
  const primary = seedDocs[0] ?? null;

  const errors = useMemo(() => {
    const out = new Map<string, Diagnostic>();
    const bySeed = new Map(seedDocs.map((s) => [s.id, s]));
    const all = [...(validation.data?.diagnostics ?? []), ...seeds.flatMap((id) => draftMap[id]?.diagnostics ?? [])];
    for (const d of all) {
      const seed = d.elementId ? bySeed.get(d.elementId) : undefined;
      const m = seed && d.jsonPointer ? POINTER.exec(d.jsonPointer) : null;
      if (!seed || !m || d.severity !== "error") continue;
      const rowId = seed.rows?.[Number(m[1])]?.id;
      if (rowId) out.set(m[2] === undefined ? `${rowId}:*` : `${rowId}:${seed.columns[Number(m[2])]}`, d);
    }
    return out;
  }, [validation.data, draftMap, seeds, seedDocs]);

  const [active, setActive] = useState<Cell>({ row: 0, col: 0 });
  const [anchor, setAnchor] = useState<Cell>({ row: 0, col: 0 });
  const [editing, setEditingState] = useState<{ value: string } | null>(null);
  // Set when Enter, Tab or Escape closes the editor, so the blur of the input going away does not commit again.
  const closing = useRef(false);
  const setEditing = (value: { value: string } | null, fresh = false) => {
    if (fresh) closing.current = false;
    setEditingState(value);
  };
  const [find, setFind] = useState("");
  const [importing, setImporting] = useState(false);
  const gridRef = useRef<HTMLDivElement>(null);
  const findRef = useRef<HTMLInputElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const ROW_H = useMemo(() => rowHeight(), []);
  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => ROW_H, overscan: 20 });

  if (!baseColumns || loaded.pending) return <Spinner />;

  const range = {
    r0: Math.min(active.row, anchor.row),
    r1: Math.max(active.row, anchor.row),
    c0: Math.min(active.col, anchor.col),
    c1: Math.max(active.col, anchor.col),
  };
  const inRange = (r: number, c: number) => r >= range.r0 && r <= range.r1 && c >= range.c0 && c <= range.c1;
  const rangeRows = rows.slice(range.r0, range.r1 + 1);

  const editSeed = (seedId: string, change: (seed: SeedDoc) => void, commit = true) => {
    drafts.edit(seedId, (json: ModelJson) => change(json as unknown as SeedDoc), { base: loaded.byId.get(seedId) });
    if (commit) void drafts.flush(seedId);
  };
  const bySeed = (list: GridRow[]) => {
    const out = new Map<string, GridRow[]>();
    for (const r of list) out.set(r.seed, [...(out.get(r.seed) ?? []), r]);
    return out;
  };
  // Synchronous, so the next key goes to the grid; focusing it blurs a closing editor, which `closing` ignores.
  const focusGrid = () => gridRef.current?.focus();
  const goTo = (cell: Cell, extend = false) => {
    const next = { row: Math.max(0, Math.min(rows.length - 1, cell.row)), col: Math.max(0, Math.min(columns.length - 1, cell.col)) };
    setActive(next);
    if (!extend) setAnchor(next);
    virtualizer.scrollToIndex(next.row, { align: "auto" });
  };
  /** The seed new rows go to: the primary one, created on first use. */
  const targetSeed = async (): Promise<string | null> => primary?.id ?? (await makeSeed());

  const insertBelow = async () => {
    const row = rows[active.row];
    const seedId = row?.seed ?? (await targetSeed());
    if (!seedId) return;
    const id = newId();
    const seed = seedDocs.find((s) => s.id === seedId);
    editSeed(seedId, (s) => insertRow(s, row ? row.index : (seed?.rows?.length ?? 0) - 1, id), false);
    const at = row ? active.row + 1 : rows.length;
    setActive({ row: at, col: 0 });
    setAnchor({ row: at, col: 0 });
    setEditing({ value: "" }, true);
  };

  const commit = (move: "down" | "right" | "left" | "none") => {
    closing.current = true;
    const row = rows[active.row];
    const column = columns[active.col];
    if (editing && row && column?.locale) void writeTranslation(column.locale, column.field ?? "label", row.id, editing.value);
    else if (editing && row && column) editSeed(row.seed, (s) => setCells(s, row.id, { [column.key]: parseCell(editing.value, column) }));
    setEditing(null);
    goTo({ row: active.row + (move === "down" ? 1 : 0), col: active.col + (move === "right" ? 1 : move === "left" ? -1 : 0) });
    focusGrid();
  };

  const writeTranslation = async (locale: string, field: TranslatedField, rowId: string, value: string) => {
    // A row with no description yet has no description entry: the label's entry names the same shard and its hash.
    const own = labels.entries.get(`${locale}|${field}|${rowId}`);
    const label = labels.entries.get(`${locale}|label|${rowId}`);
    const entry = own ?? (label ? { ...label, field, translation: null } : undefined);
    if (!entry || value === (entry.translation ?? "")) return;
    const result = await writeTranslations(services.queryClient, locale, [{ id: rowId, field, value }], [entry]);
    if (!result.ok) store.getState().notify(result.message, "error");
  };

  const run = (action: GridAction) => {
    switch (action.type) {
      case "move":
        goTo({ row: active.row + action.dr, col: active.col + action.dc }, action.extend);
        return;
      case "edit":
        if (rows[active.row]) setEditing({ value: formatCell(rows[active.row].values[columns[active.col].key]) }, true);
        return;
      case "type":
        // An end cell opens its picker on the current row; typed text is no row id.
        if (rows[active.row])
          setEditing({ value: columns[active.col]?.end ? formatCell(rows[active.row].values[columns[active.col].key]) : action.text }, true);
        return;
      case "commit":
        commit(action.move);
        return;
      case "cancel":
        closing.current = true;
        setEditing(null);
        focusGrid();
        return;
      case "insert-below":
        void insertBelow();
        return;
      case "duplicate": {
        const row = rows[active.row];
        if (!row) return;
        editSeed(row.seed, (s) => duplicateRow(s, row.index, newId()), false);
        goTo({ row: active.row + 1, col: 0 });
        return;
      }
      case "clear": {
        const keys = columns.slice(range.c0, range.c1 + 1).map((c) => c.key);
        for (const [seedId, list] of bySeed(rangeRows))
          editSeed(seedId, (s) => list.forEach((r) => setCells(s, r.id, Object.fromEntries(keys.map((k) => [k, null])))));
        return;
      }
      case "delete-rows": {
        for (const [seedId, list] of bySeed(rangeRows)) editSeed(seedId, (s) => deleteRows(s, new Set(list.map((r) => r.id))));
        const at = Math.max(0, Math.min(range.r0, rows.length - rangeRows.length - 1));
        setActive({ row: at, col: active.col });
        setAnchor({ row: at, col: active.col });
        return;
      }
      case "move-rows": {
        const first = rangeRows[0];
        const last = rangeRows[rangeRows.length - 1];
        if (!first || !last || first.seed !== last.seed) return;
        let moved = false;
        editSeed(first.seed, (s) => void (moved = moveRows(s, first.index, last.index, action.delta)));
        if (!moved) return;
        setActive({ row: active.row + action.delta, col: active.col });
        setAnchor({ row: anchor.row + action.delta, col: anchor.col });
        return;
      }
      case "copy": {
        const matrix = rangeRows.map((r) => columns.slice(range.c0, range.c1 + 1).map((c) => formatCell(r.values[c.key])));
        void navigator.clipboard?.writeText(toTsv(matrix)).catch(() => undefined);
        return;
      }
      case "find":
        findRef.current?.focus();
        return;
      case "tab":
        onTab?.(action.index);
        return;
      case "focus-search":
        onFocusSearch?.();
        return;
    }
  };

  const onGridKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (editing) return;
    const action = gridAction(e, false, { col: active.col, cols: columns.length });
    if (!action) return;
    e.preventDefault();
    run(action);
  };

  const onPaste = async (e: ClipboardEvent<HTMLDivElement>) => {
    if (editing) return;
    const text = e.clipboardData.getData("text/plain");
    if (!text) return;
    e.preventDefault();
    const matrix = parseDelimited(text, "\t");
    const row = rows[active.row];
    const seedId = row?.seed ?? (await targetSeed());
    if (!seedId) return;
    let added = 0;
    let unmatched = 0;
    editSeed(seedId, (s) => {
      const result = pasteMatrix(s, columns, { row: row ? row.index : 0, col: active.col }, matrix, newId, endOptions);
      added = result.added.length;
      unmatched = result.unmatched;
    });
    if (unmatched)
      store
        .getState()
        .notify(
          `Pasted ${matrix.length} ${matrix.length === 1 ? "row" : "rows"}; ${unmatched} end ${unmatched === 1 ? "cell matched" : "cells matched"} no row of the far entity and kept ${unmatched === 1 ? "its" : "their"} value.`,
          "error",
        );
    else if (added) store.getState().notify(`Pasted ${matrix.length} ${matrix.length === 1 ? "row" : "rows"}, ${added} new.`);
  };

  const onFind = () => {
    const term = find.trim().toLowerCase();
    if (!term) return;
    for (let i = 1; i <= rows.length; i++) {
      const r = (active.row + i) % rows.length;
      if (columns.some((c) => formatCell(rows[r].values[c.key]).toLowerCase().includes(term))) {
        goTo({ row: r, col: active.col });
        return;
      }
    }
    store.getState().notify(`No row contains "${find}".`);
  };

  const several = seedDocs.length > 1;
  const width = (key: string) => WIDTH[key] ?? 140;
  const total = columns.reduce((n, c) => n + width(c.key), 40 + (several ? 120 : 0));
  const activeId = rows[active.row] ? `cell-${active.row}-${active.col}` : undefined;

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex items-center gap-2 border-b border-default px-2 py-1">
        <Button size="sm" variant="ghost" onClick={() => void insertBelow()}>
          <Plus /> Add row
        </Button>
        <Button size="sm" variant="ghost" disabled={!primary} onClick={() => setImporting(true)} title={`Import a CSV file into ${primary?.name ?? name}`}>
          <Upload /> Import CSV
        </Button>
        <Button
          size="sm"
          variant="ghost"
          disabled={!primary}
          title={`Download ${primary?.name ?? name} as a CSV file`}
          onClick={() => primary && void exportCsv(primary).catch((e: Error) => store.getState().notify(e.message, "error"))}
        >
          <Download /> Export CSV
        </Button>
        <div className="flex-1" />
        <Input
          ref={findRef}
          aria-label="Find in rows"
          placeholder="Find (Ctrl+F)"
          className="h-7 w-48 text-12"
          value={find}
          onChange={(e) => setFind(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") onFind();
            if (e.key === "Escape") focusGrid();
          }}
        />
      </div>
      <div
        ref={(el) => {
          // The grid is its own scroller, so the scrollable region is the focusable grid (axe scrollable-region-focusable).
          gridRef.current = el;
          scrollRef.current = el;
        }}
        role="grid"
        aria-label={`Rows of ${name}`}
        aria-rowcount={rows.length + 1}
        aria-colcount={columns.length}
        aria-activedescendant={editing ? undefined : activeId}
        aria-multiselectable
        tabIndex={0}
        className="flex min-h-0 flex-1 flex-col overflow-auto outline-none focus-visible:ring-1 focus-visible:ring-inset focus-visible:ring-accent"
        onKeyDown={onGridKeyDown}
        onPaste={(e) => void onPaste(e)}
        data-testid="rows-grid"
      >
        <div className="min-h-0 flex-1">
          <div style={{ width: total, minWidth: "100%" }}>
            <div role="row" aria-rowindex={1} className="sticky top-0 z-20 flex h-7 border-b border-default bg-app text-11 font-semibold text-secondary">
              <div className="sticky left-0 z-10 w-10 shrink-0 bg-app" aria-hidden />
              {several ? (
                <div role="columnheader" className="w-[120px] shrink-0 px-1.5 leading-7">
                  Seed
                </div>
              ) : null}
              {columns.map((c, i) => (
                <div
                  key={c.key}
                  role="columnheader"
                  className={cn("shrink-0 truncate px-1.5 leading-7", c.builtin && "font-mono", i === 0 && "sticky left-10 z-10 bg-app")}
                  style={{ width: width(c.key) }}
                  title={c.required ? `${c.label} (required)` : c.label}
                >
                  {c.label}
                  {c.collection ? "[]" : ""}
                </div>
              ))}
            </div>
            <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
              {virtualizer.getVirtualItems().map((v) => {
                const row = rows[v.index];
                const r = v.index;
                const rowError = errors.get(`${row.id}:*`);
                return (
                  <div
                    key={row.id}
                    role="row"
                    aria-rowindex={r + 2}
                    className="absolute left-0 flex border-b border-default text-12"
                    style={{ top: 0, height: ROW_H, width: "100%", transform: `translateY(${v.start}px)` }}
                    data-row={row.id}
                  >
                    <div
                      className={cn(
                        "sticky left-0 z-10 w-10 shrink-0 bg-surface pr-1 text-right font-mono text-11 leading-7 text-secondary",
                        rowError && "text-danger",
                      )}
                      title={rowError?.message}
                      aria-hidden
                    >
                      {r + 1}
                    </div>
                    {several ? (
                      <div className="w-[120px] shrink-0 truncate px-1.5 leading-7 text-secondary">{seedDocs.find((s) => s.id === row.seed)?.name}</div>
                    ) : null}
                    {columns.map((c, ci) => {
                      const isActive = active.row === r && active.col === ci;
                      const error = errors.get(`${row.id}:${c.key}`);
                      return (
                        <div
                          key={c.key}
                          id={`cell-${r}-${ci}`}
                          role="gridcell"
                          aria-selected={inRange(r, ci)}
                          aria-invalid={error ? true : undefined}
                          data-cell={`${r}:${ci}`}
                          title={error?.message}
                          className={cn(
                            "shrink-0 truncate px-1.5 leading-7",
                            ci === 0 && "sticky left-10 z-10 bg-surface font-mono",
                            inRange(r, ci) && "bg-accent-subtle",
                            isActive && "ring-1 ring-inset ring-accent",
                            error && "ring-1 ring-inset ring-danger",
                          )}
                          style={{ width: width(c.key) }}
                          onMouseDown={(e) => {
                            if (editing) return;
                            e.preventDefault();
                            goTo({ row: r, col: ci }, e.shiftKey);
                            gridRef.current?.focus();
                          }}
                          onDoubleClick={() => run({ type: "edit" })}
                        >
                          {isActive && editing && c.end ? (
                            <select
                              autoFocus
                              aria-label={`${c.label} of row ${r + 1}`}
                              className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12"
                              value={editing.value}
                              onChange={(e) => setEditing({ value: e.target.value })}
                              onKeyDown={(e) => {
                                const action = gridAction(e, true);
                                if (!action) return;
                                e.preventDefault();
                                e.stopPropagation();
                                run(action);
                              }}
                              onBlur={() => {
                                if (closing.current) closing.current = false;
                                else commit("none");
                              }}
                            >
                              <option value="">(none)</option>
                              {(endOptions.get(c.end) ?? []).map((o) => (
                                <option key={o.id} value={o.id}>
                                  {o.label}
                                </option>
                              ))}
                              {editing.value && !(endOptions.get(c.end) ?? []).some((o) => o.id === editing.value) ? (
                                <option value={editing.value}>{editing.value}</option>
                              ) : null}
                            </select>
                          ) : isActive && editing && c.multiline ? (
                            <textarea
                              autoFocus
                              aria-label={`${c.label} of row ${r + 1}`}
                              title="Shift+Enter adds a line"
                              onFocus={(e) => e.currentTarget.setSelectionRange(e.currentTarget.value.length, e.currentTarget.value.length)}
                              rows={1}
                              className="block h-6 w-full resize-none overflow-y-auto rounded-[4px] border border-input bg-surface px-1 text-12 leading-5"
                              value={editing.value}
                              onChange={(e) => setEditing({ value: e.target.value })}
                              onKeyDown={(e) => {
                                if (e.key === "Enter" && e.shiftKey) return; // a new line in the text
                                const action = gridAction(e, true);
                                if (!action) return;
                                e.preventDefault();
                                e.stopPropagation();
                                run(action);
                              }}
                              onBlur={() => {
                                if (closing.current) closing.current = false;
                                else commit("none");
                              }}
                            />
                          ) : isActive && editing ? (
                            <input
                              autoFocus
                              aria-label={`${c.label} of row ${r + 1}`}
                              className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 text-12"
                              value={editing.value}
                              onChange={(e) => setEditing({ value: e.target.value })}
                              onKeyDown={(e) => {
                                const action = gridAction(e, true);
                                if (!action) return;
                                e.preventDefault();
                                e.stopPropagation();
                                run(action);
                              }}
                              onBlur={() => {
                                if (closing.current) closing.current = false;
                                else commit("none");
                              }}
                            />
                          ) : c.locale && row.values[c.key] === undefined ? (
                            <span className="italic text-secondary">{labels.entries.get(`${c.locale}|${c.field ?? "label"}|${row.id}`)?.effective ?? ""}</span>
                          ) : c.end && row.values[c.key] !== undefined ? (
                            (endOptions.get(c.end)?.find((o) => o.id === row.values[c.key])?.label ?? formatCell(row.values[c.key]))
                          ) : (
                            formatCell(row.values[c.key])
                          )}
                        </div>
                      );
                    })}
                  </div>
                );
              })}
            </div>
            {rows.length === 0 ? (
              <p className="p-2 text-12 text-secondary">No rows yet. Ctrl+Enter or Add row adds one; pasting tab-separated cells adds several.</p>
            ) : null}
          </div>
        </div>
      </div>
      <footer className="flex items-center gap-2 border-t border-default px-2 py-1 text-11 text-secondary" data-testid="rows-status">
        <span>
          {rows.length} {rows.length === 1 ? "row" : "rows"} · {errors.size} {errors.size === 1 ? "error" : "errors"}
          {(contentLocale && l10n.locales.includes(contentLocale) ? [contentLocale] : []).map((locale) => (
            <span key={locale} data-testid="rows-completeness">
              {" "}
              · {locale} {labelCompleteness(rows, labels.out.get(locale))} %
            </span>
          ))}
        </span>
        {localized && localization.isSuccess && !localization.enabled ? (
          <span className="whitespace-nowrap" data-testid="rows-locale-hint">
            {ONE_LOCALE_HINT}
          </span>
        ) : null}
        {l10n.enabled && l10n.locales.length ? (
          <label className="flex items-center gap-1 whitespace-nowrap">
            <input type="checkbox" checked={allLocales} onChange={(e) => setAllLocales(e.target.checked)} data-testid="rows-all-locales" />
            All locales
          </label>
        ) : null}
        <span className="hidden flex-1 truncate md:inline">
          Enter edits · Tab moves · Ctrl+Enter inserts · Ctrl+D duplicates · Ctrl+Delete deletes · Alt+Up/Down moves
        </span>
      </footer>
      {primary ? <ImportCsvDialog open={importing} onOpenChange={setImporting} seed={primary} /> : null}
    </div>
  );
}
