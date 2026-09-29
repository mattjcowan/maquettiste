// The Rows grid's model (reference-types-seeds-localization.md 2.1 and 4.4): the grid's columns from the reference
// type (the built-in code, label and description, then the user fields), its rows over the type's seeds, the edits as pure
// functions over a seed document (so every edit goes through the seed's draft, saves like any element and undoes),
// the tab-separated clipboard and the keyboard map. Pure and tested alone.
import type { AttributeDoc, ReferenceTypeDoc, RelationDoc, RelationEndDoc, SeedDoc } from "@/api/types";
import { isBuiltin } from "@/model/model";

export interface GridColumn {
  /** The seed column key: `code`, `label`, `description` or a user field's id. */
  key: string;
  label: string;
  builtin: boolean;
  /** The logical type ("string", "decimal", …), or "reference" for a reference-typed field. */
  type: string;
  collection: boolean;
  required: boolean;
  /** A translated label or description column (`@label:<locale>`, `@description:<locale>`, RT 4.4): its cells are the
   *  locale's shard, never the seed. */
  locale?: string;
  /** The translated field of a locale column. */
  field?: TranslatedField;
  /** Text that may span lines (the row description): its editor is a text area, where Shift+Enter adds a line. */
  multiline?: boolean;
  /** A relation-end column: the entity whose rows its cells name (a row id, picked from that entity's seeds). */
  end?: string;
}

export interface GridRow {
  id: string;
  seed: string;
  /** The row's index in its seed. */
  index: number;
  values: Record<string, unknown>;
}

type SeedRow = NonNullable<SeedDoc["rows"]>[number];

/** The row fields a locale translates. */
export type TranslatedField = "label" | "description";

/** The built-in columns of every reference type's rows, in grid order; a new seed lists them all. */
export const BUILTIN_COLUMNS = ["code", "label", "description"] as const;

/** The grid's columns: code (frozen on the left), label, description, then the user fields in order. */
export function gridColumns(type: Pick<ReferenceTypeDoc, "code" | "label" | "attributes">): GridColumn[] {
  const fields = ((type.attributes ?? []) as AttributeDoc[]).map((a) => ({
    key: a.id,
    label: a.name,
    builtin: false,
    type: isBuiltin(a.type) ? a.type : "reference",
    collection: a.collection === true,
    required: a.required === true,
  }));
  return [
    { key: "code", label: "code", builtin: true, type: type.code?.type ?? "string", collection: false, required: true },
    { key: "label", label: "label", builtin: true, type: "string", collection: false, required: true },
    { key: "description", label: "description", builtin: true, type: "text", collection: false, required: false, multiline: true },
    ...fields,
  ];
}

/**
 * The locales whose label column the Rows grid shows (RT 4.4): the content locale in effect only (none for the default
 * locale), or every translated locale with "All locales" on.
 */
export function shownLocales(locales: readonly string[], effective: string | null, all: boolean): string[] {
  if (all) return [...locales];
  return effective && locales.includes(effective) ? [effective] : [];
}

/** The share of rows whose label has a translation in a locale, as a whole percent (100 with no rows). */
export function labelCompleteness(rows: readonly { id: string }[], translated: ReadonlyMap<string, string> | undefined): number {
  if (!rows.length) return 100;
  const done = rows.reduce((n, r) => n + (translated?.has(r.id) ? 1 : 0), 0);
  return Math.floor((done * 100) / rows.length);
}

/** Per locale (RT 4.4 "inline localization"), a translated label column and a translated description column beside it,
 *  after the type's own columns. */
export function localeColumns(locales: readonly string[]): GridColumn[] {
  return locales.flatMap((locale) =>
    (["label", "description"] as const).map((field) => ({
      key: `@${field}:${locale}`,
      label: `${field} (${locale})`,
      builtin: true,
      type: field === "label" ? "string" : "text",
      collection: false,
      required: false,
      locale,
      field,
      ...(field === "description" ? { multiline: true } : {}),
    })),
  );
}

type Translations = ReadonlyMap<string, ReadonlyMap<string, string>>;

/** Rows with the translations in their locale columns (`labels`, `descriptions`: locale → row id → text). */
export function withTranslatedLabels(rows: readonly GridRow[], labels: Translations, descriptions: Translations = new Map()): GridRow[] {
  if (labels.size === 0 && descriptions.size === 0) return rows as GridRow[];
  return rows.map((r) => {
    const values = { ...r.values };
    for (const [field, byLocale] of [
      ["label", labels],
      ["description", descriptions],
    ] as const)
      for (const [locale, map] of byLocale) {
        const text = map.get(r.id);
        if (text !== undefined) values[`@${field}:${locale}`] = text;
      }
    return { ...r, values };
  });
}

/** The hint the Rows grid's status bar shows while the project declares one locale only. */
export const ONE_LOCALE_HINT = "Declare a second locale under Settings › Locales to translate labels and descriptions.";

/** The rows of every seed of the type, in (seed order given, file order). */
export function gridRows(seeds: readonly SeedDoc[]): GridRow[] {
  return seeds.flatMap((seed) =>
    (seed.rows ?? []).map((row, index) => {
      const values: Record<string, unknown> = {};
      seed.columns.forEach((c, i) => {
        const v = (row.values as unknown[] | undefined)?.[i];
        if (v !== undefined && v !== null) values[c] = v;
      });
      return { id: row.id, seed: seed.id, index, values };
    }),
  );
}

const NUMERIC = new Set(["int16", "int32", "int64", "decimal", "float", "double"]);
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A cell as text: codes and plain values as written, collections `;`-joined, objects as compact JSON. */
export function formatCell(value: unknown): string {
  if (value === null || value === undefined) return "";
  if (Array.isArray(value)) return value.map(formatCell).join(";");
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}

/** Text typed into a cell as the column's JSON literal; empty text is no value (null). */
export function parseCell(text: string, column: Pick<GridColumn, "type" | "collection">): unknown {
  const raw = text.trim();
  if (raw === "") return null;
  if (column.collection)
    return raw
      .split(";")
      .map((s) => s.trim())
      .filter(Boolean);
  if (NUMERIC.has(column.type) && /^-?\d+(\.\d+)?$/.test(raw)) return Number(raw);
  // A uuid is stored in its canonical form (MQ7013): lowercase, hyphenated.
  if (column.type === "uuid" && UUID.test(raw)) return raw.toLowerCase();
  if (column.type === "bool" && (raw === "true" || raw === "false")) return raw === "true";
  return text;
}

function trim(values: unknown[]): unknown[] {
  let n = values.length;
  while (n > 0 && (values[n - 1] === null || values[n - 1] === undefined)) n--;
  return values.slice(0, n).map((v) => (v === undefined ? null : v));
}

function writeRow(id: string, values: unknown[]): SeedRow {
  const kept = trim(values);
  return (kept.length ? { id, values: kept } : { id }) as SeedRow;
}

/** The column index of a key, adding the column at the end when the seed does not list it yet. */
function columnIndex(seed: SeedDoc, key: string): number {
  let at = seed.columns.indexOf(key);
  if (at < 0) {
    seed.columns = [...seed.columns, key];
    at = seed.columns.length - 1;
  }
  return at;
}

function valuesOf(row: SeedRow): unknown[] {
  return [...((row.values as unknown[] | undefined) ?? [])];
}

/** Sets cells of one row (`values` by column key); null clears a cell. Mutates the seed (a draft's copy). */
export function setCells(seed: SeedDoc, rowId: string, values: Record<string, unknown>): void {
  const rows = seed.rows ?? [];
  const at = rows.findIndex((r) => r.id === rowId);
  if (at < 0) return;
  const next = valuesOf(rows[at]);
  for (const [key, value] of Object.entries(values)) {
    if (key.startsWith("@")) continue; // a locale column: written to the locale's shard, not the seed
    const i = columnIndex(seed, key);
    while (next.length <= i) next.push(null);
    next[i] = value;
  }
  rows[at] = writeRow(rowId, next);
  seed.rows = rows;
}

/** Inserts an empty row after `index` (-1 inserts first). */
export function insertRow(seed: SeedDoc, index: number, id: string): void {
  const rows = [...(seed.rows ?? [])];
  rows.splice(index + 1, 0, { id } as SeedRow);
  seed.rows = rows;
}

/** Ctrl+D: a copy of the row below it with a new id and an empty code (codes are unique, MQ7001). */
export function duplicateRow(seed: SeedDoc, index: number, id: string): void {
  const rows = [...(seed.rows ?? [])];
  const source = rows[index];
  if (!source) return;
  const values = valuesOf(source);
  const code = seed.columns.indexOf("code");
  if (code >= 0 && code < values.length) values[code] = null;
  rows.splice(index + 1, 0, writeRow(id, values));
  seed.rows = rows;
}

export function deleteRows(seed: SeedDoc, ids: ReadonlySet<string>): void {
  seed.rows = (seed.rows ?? []).filter((r) => !ids.has(r.id));
}

/** Alt+Up, Alt+Down: moves the rows from..to (inclusive) by delta; false when they are already at the edge. */
export function moveRows(seed: SeedDoc, from: number, to: number, delta: -1 | 1): boolean {
  const rows = [...(seed.rows ?? [])];
  if (from < 0 || to >= rows.length || (delta < 0 && from === 0) || (delta > 0 && to === rows.length - 1)) return false;
  const block = rows.splice(from, to - from + 1);
  rows.splice(from + delta, 0, ...block);
  seed.rows = rows;
  return true;
}

/**
 * Tab-separated (or comma-separated) text as rows of cells, RFC 4180 style: a quoted cell may hold the delimiter,
 * line breaks and doubled quotes. Spreadsheets put a line break after the last row; it adds no empty row.
 */
export function parseDelimited(text: string, delimiter = "\t"): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = "";
  let quoted = false;
  let started = false;
  const s = text.startsWith("﻿") ? text.slice(1) : text;
  const endRow = () => {
    row.push(cell);
    rows.push(row);
    row = [];
    cell = "";
    started = false;
  };
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (quoted) {
      if (c === '"' && s[i + 1] === '"') {
        cell += '"';
        i++;
      } else if (c === '"') quoted = false;
      else cell += c;
    } else if (c === '"' && cell === "") {
      quoted = true;
      started = true;
    } else if (c === delimiter) {
      row.push(cell);
      cell = "";
      started = true;
    } else if (c === "\r" || c === "\n") {
      if (c === "\r" && s[i + 1] === "\n") i++;
      endRow();
    } else {
      cell += c;
      started = true;
    }
  }
  if (started || cell !== "" || row.length) endRow();
  return rows;
}

/** A range of cells as tab-separated text (Ctrl+C); cells holding a tab, a line break or a quote are quoted. */
export function toTsv(matrix: readonly (readonly string[])[]): string {
  const cell = (v: string) => (/[\t\r\n"]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v);
  return matrix.map((r) => r.map(cell).join("\t")).join("\n");
}

export interface PasteResult {
  /** Row ids the paste changed, then the ids of the rows it added. */
  changed: string[];
  added: string[];
  /** End cells whose text named no row of the far entity (by id or by the label the grid shows): left as they were. */
  unmatched: number;
}

/**
 * Pastes a matrix of text at (row, column): cells past the last column are dropped, rows past the last row are
 * added (Ctrl+V adding rows, RT 4.4). Mutates the seed.
 */
export function pasteMatrix(
  seed: SeedDoc,
  columns: readonly GridColumn[],
  at: { row: number; col: number },
  matrix: readonly (readonly string[])[],
  newId: () => string,
  endOptions?: ReadonlyMap<string, readonly EndOption[]>,
): PasteResult {
  const result: PasteResult = { changed: [], added: [], unmatched: 0 };
  matrix.forEach((cells, r) => {
    const index = at.row + r;
    let id = seed.rows?.[index]?.id;
    if (!id) {
      id = newId();
      insertRow(seed, (seed.rows ?? []).length - 1, id);
      result.added.push(id);
    } else result.changed.push(id);
    const values: Record<string, unknown> = {};
    cells.forEach((text, c) => {
      const column = columns[at.col + c];
      if (!column) return;
      if (column.end) {
        // An end cell holds a row id: a pasted id or a pasted label (what the grid shows) of the far entity's rows.
        const wanted = text.trim();
        if (!wanted) values[column.key] = null;
        else {
          const options = endOptions?.get(column.end) ?? [];
          const match =
            options.find((o) => o.id === wanted) ??
            options.find((o) => o.label === wanted) ??
            options.find((o) => o.label.toLowerCase() === wanted.toLowerCase());
          if (match) values[column.key] = match.id;
          else result.unmatched++;
        }
        return;
      }
      values[column.key] = parseCell(text, column);
    });
    setCells(seed, id, values);
  });
  return result;
}

// ------------------------------------------------------------------ keyboard

export type GridAction =
  | { type: "move"; dr: number; dc: number; extend: boolean }
  | { type: "edit" }
  | { type: "type"; text: string }
  | { type: "commit"; move: "down" | "right" | "left" }
  | { type: "cancel" }
  | { type: "insert-below" }
  | { type: "duplicate" }
  | { type: "clear" }
  | { type: "delete-rows" }
  | { type: "move-rows"; delta: -1 | 1 }
  | { type: "copy" }
  | { type: "find" }
  | { type: "tab"; index: number }
  | { type: "focus-search" };

export interface KeyLike {
  key: string;
  ctrlKey?: boolean;
  metaKey?: boolean;
  shiftKey?: boolean;
  altKey?: boolean;
}

/**
 * The SPEC section 14 spreadsheet keys, as RT 4.4 lists them. Paste is not here: it arrives as a paste event with
 * the clipboard text. Returns null for keys the grid leaves alone. As in the Fields tab's attribute grid, Tab leaves
 * the grid from the last column (Shift+Tab from the first) when `at` gives the active column and the column count,
 * and Space starts an edit.
 */
export function gridAction(e: KeyLike, editing: boolean, at?: { col: number; cols: number }): GridAction | null {
  const mod = e.ctrlKey || e.metaKey;
  if (editing) {
    if (e.key === "Enter" && !mod) return { type: "commit", move: "down" };
    if (e.key === "Tab") return { type: "commit", move: e.shiftKey ? "left" : "right" };
    if (e.key === "Escape") return { type: "cancel" };
    return null;
  }
  const arrows: Record<string, [number, number]> = { ArrowDown: [1, 0], ArrowUp: [-1, 0], ArrowRight: [0, 1], ArrowLeft: [0, -1] };
  if (e.altKey && (e.key === "ArrowUp" || e.key === "ArrowDown")) return { type: "move-rows", delta: e.key === "ArrowUp" ? -1 : 1 };
  if (arrows[e.key] && !mod) return { type: "move", dr: arrows[e.key][0], dc: arrows[e.key][1], extend: e.shiftKey === true };
  if (mod && e.key === "Enter") return { type: "insert-below" };
  if (mod && (e.key === "d" || e.key === "D")) return { type: "duplicate" };
  if (mod && (e.key === "Delete" || e.key === "Backspace")) return { type: "delete-rows" };
  if (mod && (e.key === "c" || e.key === "C")) return { type: "copy" };
  if (mod && (e.key === "f" || e.key === "F")) return { type: "find" };
  if (mod && /^[1-4]$/.test(e.key)) return { type: "tab", index: Number(e.key) - 1 };
  if (e.key === "Tab") {
    if (at && (e.shiftKey ? at.col <= 0 : at.col >= at.cols - 1)) return null;
    return { type: "move", dr: 0, dc: e.shiftKey ? -1 : 1, extend: false };
  }
  if (e.key === "Enter" || e.key === "F2" || (e.key === " " && !mod)) return { type: "edit" };
  if (e.key === "Delete" || e.key === "Backspace") return { type: "clear" };
  if (e.key === "/") return { type: "focus-search" };
  if (e.key.length === 1 && !mod && !e.altKey) return { type: "type", text: e.key };
  return null;
}

// ------------------------------------------------------------------ entity and relation seeds

/** A to-one end an entity's seed can hold a column for, with the relation it belongs to. */
export interface SeedEnd {
  relation: string;
  end: RelationEndDoc;
}

const endColumn = (end: RelationEndDoc, nameOf: (id: string) => string | undefined): GridColumn => ({
  key: end.id,
  label: end.role || nameOf(end.entity) || end.entity,
  builtin: false,
  type: "end",
  collection: false,
  required: (end.min ?? 0) >= 1,
  end: end.entity,
});

const attributeColumn = (a: AttributeDoc): GridColumn => ({
  key: a.id,
  label: a.name,
  builtin: false,
  type: isBuiltin(a.type) ? a.type : "reference",
  collection: a.collection === true,
  required: a.required === true,
});

/**
 * The ends an entity's seed can name, as the engine resolves seed columns (MQ7105, MQ7106): of each two-ended
 * relation whose other end is the entity or one of its bases, the far end when it is to-one, unless the relation has
 * seeds of its own (its links then live there). A self relation can offer both of its ends.
 */
export function entitySeedEnds(lineage: ReadonlySet<string>, relations: readonly RelationDoc[], hasSeeds: (relation: string) => boolean): SeedEnd[] {
  const out: SeedEnd[] = [];
  for (const relation of relations) {
    const ends = relation.ends ?? [];
    if (ends.length !== 2 || hasSeeds(relation.id)) continue;
    ends.forEach((end, i) => {
      if (lineage.has(ends[1 - i].entity) && end.max === 1) out.push({ relation: relation.id, end });
    });
  }
  return out;
}

/** An entity seed's columns: its attributes (the bases' first, as the engine flattens them), then its to-one ends. */
export function entityGridColumns(attributes: readonly AttributeDoc[], ends: readonly SeedEnd[], nameOf: (id: string) => string | undefined): GridColumn[] {
  return [...attributes.map(attributeColumn), ...ends.map((e) => endColumn(e.end, nameOf))];
}

/** A relation seed's columns: both ends (every link names both), then the relation's attributes. */
export function relationGridColumns(relation: Pick<RelationDoc, "ends" | "attributes">, nameOf: (id: string) => string | undefined): GridColumn[] {
  return [...(relation.ends ?? []).map((e) => endColumn(e, nameOf)), ...((relation.attributes ?? []) as AttributeDoc[]).map(attributeColumn)];
}

/** The columns with any column a seed lists that the target no longer offers, so no stored cell is hidden. */
export function withSeedColumns(columns: readonly GridColumn[], seeds: readonly Pick<SeedDoc, "columns">[]): GridColumn[] {
  const known = new Set(columns.map((c) => c.key));
  const extra: GridColumn[] = [];
  for (const seed of seeds)
    for (const key of seed.columns ?? [])
      if (!known.has(key)) {
        known.add(key);
        extra.push({ key, label: key, builtin: false, type: "string", collection: false, required: false });
      }
  return [...columns, ...extra];
}

/** A row an end cell can name. */
export interface EndOption {
  id: string;
  label: string;
}

/**
 * The rows of an entity's seeds as an end picker lists them: each labelled by its first two non-empty cells (the
 * product reads no column name as special), else by its id; in seed order.
 */
export function endRowOptions(seeds: readonly SeedDoc[]): EndOption[] {
  return gridRows(seeds).map((row) => {
    const seed = seeds.find((s) => s.id === row.seed);
    const cells = (seed?.columns ?? []).map((c) => formatCell(row.values[c])).filter((t) => t !== "");
    return { id: row.id, label: cells.slice(0, 2).join(" · ") || row.id };
  });
}

/** A new seed document for a target: named after it, listing the given columns (the caller checks there is one). */
export function newSeedDocument(id: string, target: { id: string; name: string }, columns: readonly string[]): SeedDoc {
  return { kind: "seed", id, name: target.name, target: target.id, columns: [...columns], rows: [] } as unknown as SeedDoc;
}
