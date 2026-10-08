// A table's parts (its columns, primary key, unique constraints, indexes, foreign keys and checks), pure: how the table editor's
// tabs, the explorer's table folders and the inspector name them, and the edits each makes to a table document
// (schemas/v1/table.json). The database side knows tables only: a table the model still lays out by convention (not stored as a
// table file yet) shows as the same document shape (`resolvedTableDoc`), its columns named by their resolved keys; the first
// edit stores it as a table file, after which the edit runs again on that file with its columns renamed to the file's ids
// (`withColumnIds`). Every gesture is one document change, so one save and one undo step.
import type { ElementSummary, ModelJson, SaveResult, TableView } from "@/api/types";
import type { UndoEntry } from "@/state/store";
import { clone } from "@/lib/json";
import { addTableColumn, deleteTableColumn, setEntryMember, tableColumns } from "@/editors/database/databaseDocs";

type Json = Record<string, unknown>;

export type TablePartKind = "column" | "primary-key" | "unique" | "index" | "foreign-key" | "check";

/** One part of a table: a column by its key, the primary key, or a constraint or index by its name (its id when it has none). */
export interface TablePart {
  kind: TablePartKind;
  id: string;
}

/** The table editor's tabs, in order, with their labels. */
export const TABLE_TABS = {
  general: "General",
  columns: "Columns",
  "primary-key": "Primary key",
  uniques: "Unique constraints",
  indexes: "Indexes",
  "foreign-keys": "Foreign keys",
  checks: "Checks",
  exclusions: "Exclusions",
  storage: "Storage",
  data: "Data",
  ddl: "DDL",
  references: "References",
  json: "JSON",
} as const;
export type TableTab = keyof typeof TABLE_TABS;

/** The tab that edits each kind of part. */
export const PART_TAB: Record<TablePartKind, TableTab> = {
  column: "columns",
  "primary-key": "primary-key",
  unique: "uniques",
  index: "indexes",
  "foreign-key": "foreign-keys",
  check: "checks",
};

/** A part kind in words ("New unique constraint", "Delete index ix_x"). */
export const PART_LABELS: Record<TablePartKind, string> = {
  column: "column",
  "primary-key": "primary key",
  unique: "unique constraint",
  index: "index",
  "foreign-key": "foreign key",
  check: "check",
};

/** The document member holding a list part. */
export const PART_MEMBER = { unique: "uniques", index: "indexes", "foreign-key": "foreignKeys", check: "checks" } as const;
export type ListPartKind = keyof typeof PART_MEMBER;

/** The explorer's table folders (key suffix) and the part kind each lists. */
export const FOLDER_PARTS: Record<string, TablePartKind> = {
  columns: "column",
  "primary-key": "primary-key",
  uniques: "unique",
  indexes: "index",
  "foreign-keys": "foreign-key",
  checks: "check",
};

export const isListPart = (kind: TablePartKind): kind is ListPartKind => kind in PART_MEMBER;

const list = (doc: Json, member: string): Json[] => (Array.isArray(doc[member]) ? (doc[member] as Json[]) : []);

function setList(doc: Json, member: string, items: Json[]): void {
  if (items.length) doc[member] = items;
  else delete doc[member];
}

/** Whether a table key is a table the model lays out by convention (not a table file): its key names its database after `@`. */
export const isLaidOutKey = (key: string): boolean => key.includes("@");

/** "billing.customers". */
export const qualifiedTable = (table: Pick<TableView, "schema" | "name">): string => (table.schema ? `${table.schema}.${table.name}` : table.name);

/** The view's foreign key actions are words ("no action"); the document's are keywords ("no-action"). */
const actionKeyword = (text: string | null | undefined): string | undefined => {
  const keyword = (text ?? "").trim().toLowerCase().replace(/\s+/g, "-");
  return keyword && keyword !== "no-action" ? keyword : undefined;
};

/**
 * A resolved table as a table document: what a table laid out by convention shows in the table editor before it is stored as a
 * file. Its columns are named by their resolved keys and its constraints by their names, so an edit made on it can be replayed
 * on the stored file (`withColumnIds`). Checks are not in the resolved view: none show until the table is a file.
 */
export function resolvedTableDoc(table: TableView, database: string): Json {
  const doc: Json = { kind: "table", id: table.key, name: table.name, database };
  if (table.displayName) doc.displayName = table.displayName;
  if (table.description) doc.description = table.description;
  if (table.stereotypes.length) doc.stereotypes = [...table.stereotypes];
  if (table.tags.length) doc.tags = [...table.tags];
  if (table.comment) doc.comment = table.comment;
  if (Object.keys(table.properties ?? {}).length) doc.properties = clone(table.properties);
  doc.columns = table.columns.map((c) => {
    const entry: Json = { id: c.key, name: c.name, type: c.type };
    if (c.length !== null) entry.length = c.length;
    if (c.precision !== null) entry.precision = c.precision;
    if (c.scale !== null) entry.scale = c.scale;
    if (!c.nullable) entry.nullable = false;
    if (c.default !== null && c.default !== undefined) entry.default = c.default;
    if (c.comment) entry.comment = c.comment;
    if (c.description) entry.description = c.description;
    return entry;
  });
  if (table.primaryKey) doc.primaryKey = { name: table.primaryKey.name, columns: [...table.primaryKey.columns] };
  setList(
    doc,
    "uniques",
    table.uniques.map((u) => ({ id: u.name, name: u.name, columns: [...u.columns] })),
  );
  setList(
    doc,
    "indexes",
    table.indexes.map((ix) => {
      const entry: Json = {
        id: ix.name,
        name: ix.name,
        columns: ix.columns.map((c) => (c.descending ? { column: c.column, descending: true } : { column: c.column })),
      };
      if (ix.unique) entry.unique = true;
      if (ix.where) entry.where = ix.where;
      return entry;
    }),
  );
  setList(
    doc,
    "foreignKeys",
    table.foreignKeys.map((fk) => {
      const entry: Json = { id: fk.name, name: fk.name, columns: [...fk.columns], referencesTable: fk.referencedTable };
      if (fk.referencedColumns.length) entry.referencesColumns = [...fk.referencedColumns];
      const onDelete = actionKeyword(fk.onDelete);
      const onUpdate = actionKeyword(fk.onUpdate);
      if (onDelete) entry.onDelete = onDelete;
      if (onUpdate) entry.onUpdate = onUpdate;
      return entry;
    }),
  );
  return doc;
}

/** Old column key → new column id, matched by column name (a stored table keeps its columns' names). */
export function columnIdMap(from: readonly { key: string; name: string }[], to: Json): Map<string, string> {
  const byName = new Map(tableColumns(to).map((c) => [c.name, c.id]));
  const out = new Map<string, string>();
  for (const c of from) {
    const id = byName.get(c.name);
    if (id !== undefined) out.set(c.key, id);
  }
  return out;
}

/** The map the other way round. */
export const invertMap = (map: ReadonlyMap<string, string>): Map<string, string> => new Map([...map].map(([a, b]) => [b, a]));

/**
 * Renames a table document's column ids and every reference to them (the primary key, uniques, indexes and their include
 * columns, foreign keys, and the referenced columns of a foreign key to the table itself). Mutates and returns the document.
 */
export function withColumnIds(doc: Json, map: ReadonlyMap<string, string>): Json {
  const to = (id: unknown) => (typeof id === "string" ? (map.get(id) ?? id) : id);
  const ids = (v: unknown) => (Array.isArray(v) ? v.map(to) : v);
  for (const c of list(doc, "columns")) c.id = to(c.id);
  const pk = doc.primaryKey as Json | undefined;
  if (pk) pk.columns = ids(pk.columns);
  for (const u of list(doc, "uniques")) u.columns = ids(u.columns);
  for (const ix of list(doc, "indexes")) {
    for (const c of Array.isArray(ix.columns) ? (ix.columns as Json[]) : []) if (c.column !== undefined) c.column = to(c.column);
    if (Array.isArray(ix.include)) ix.include = ids(ix.include);
  }
  for (const fk of list(doc, "foreignKeys")) {
    fk.columns = ids(fk.columns);
    if (fk.referencesTable === doc.id && Array.isArray(fk.referencesColumns)) fk.referencesColumns = ids(fk.referencesColumns);
  }
  return doc;
}

/**
 * Runs an edit made against a resolved table's document (`resolvedTableDoc`) on its stored file: columns renamed both ways, and
 * the table's own key (`oldKey`, what a foreign key of the table to itself names in the resolved view) read as the file's id.
 * Returns the edited file, or null when the edit refused (returned false).
 */
export function replayOnFile(
  file: Json,
  resolved: readonly { key: string; name: string }[],
  edit: (doc: Json) => void | boolean,
  oldKey?: string,
): Json | null {
  const map = columnIdMap(resolved, file);
  const doc = withColumnIds(clone(file), invertMap(map));
  const fileId = doc.id;
  const self = (to: unknown, from: unknown) => {
    if (!oldKey || oldKey === fileId) return;
    for (const fk of list(doc, "foreignKeys")) if (fk.referencesTable === from) fk.referencesTable = to;
  };
  self(oldKey, fileId);
  if (edit(doc) === false) return null;
  self(fileId, oldKey);
  return withColumnIds(doc, map);
}

/** The resolved name of the n-th entry of a list part (the view lists them in the file's order). */
function resolvedNameAt(view: TableView | null | undefined, kind: ListPartKind, index: number): string | undefined {
  if (!view) return undefined;
  if (kind === "unique") return view.uniques[index]?.name;
  if (kind === "index") return view.indexes[index]?.name;
  if (kind === "foreign-key") return view.foreignKeys[index]?.name;
  return undefined;
}

/** The entries of a list part. */
export const partEntries = (doc: Json, kind: ListPartKind): Json[] => list(doc, PART_MEMBER[kind]);

/** A list part's identity at an index: its own name, else its resolved name, else its id. */
export function partIdAt(doc: Json, view: TableView | null | undefined, kind: ListPartKind, index: number): string {
  const entry = partEntries(doc, kind)[index];
  const own = typeof entry?.name === "string" && entry.name ? entry.name : undefined;
  return own ?? resolvedNameAt(view, kind, index) ?? String(entry?.id ?? index);
}

/** The shown name of a list part at an index (the placeholder when only the convention names it). */
export function partNameAt(doc: Json, view: TableView | null | undefined, kind: ListPartKind, index: number): string {
  return partIdAt(doc, view, kind, index);
}

/** The index of a list part by its identity (name, resolved name or id), or -1. */
export function findPart(doc: Json, view: TableView | null | undefined, kind: ListPartKind, id: string): number {
  const entries = partEntries(doc, kind);
  for (let i = 0; i < entries.length; i++) if (entries[i].name === id || entries[i].id === id || resolvedNameAt(view, kind, i) === id) return i;
  return -1;
}

/** A name for a new part that no part of the table uses yet: `base`, `base_2`, ... */
export function freshPartName(doc: Json, base: string): string {
  const taken = new Set<string>();
  for (const member of ["uniques", "indexes", "foreignKeys", "checks"])
    for (const e of list(doc, member)) if (typeof e.name === "string") taken.add(e.name.toLowerCase());
  const pk = doc.primaryKey as Json | undefined;
  if (typeof pk?.name === "string") taken.add(pk.name.toLowerCase());
  if (!taken.has(base.toLowerCase())) return base;
  for (let n = 2; ; n++) if (!taken.has(`${base}_${n}`.toLowerCase())) return `${base}_${n}`;
}

/** The column a new key starts on: one named id, else the first. */
function firstColumn(doc: Json): { id: string; name: string } | undefined {
  const columns = tableColumns(doc);
  return columns.find((c) => c.name.toLowerCase() === "id") ?? columns[0];
}

export interface NewPartOptions {
  /** A foreign key's referenced table (its primary key is referenced). */
  referencesTable?: string;
  /** A check's dialect (its expression is written for it). */
  dialect?: string;
}

/**
 * Adds a part, valid as added: a column; the primary key on the id column (or the first); a unique constraint or an index on the
 * first column; a foreign key from the first column to `referencesTable`; a check `<first column> IS NOT NULL` for the dialect.
 * Each constraint gets a name so it can be picked at once. Returns the new part, or null when the table has no column yet
 * (or already has a primary key, or a foreign key has no table to reference).
 */
export function addPart(doc: Json, kind: TablePartKind, newId: () => string, options: NewPartOptions = {}): TablePart | null {
  const table = String(doc.name ?? "table") || "table";
  if (kind === "column") {
    const id = addTableColumn(doc, newId);
    return { kind, id };
  }
  const first = firstColumn(doc);
  if (!first) return null;
  if (kind === "primary-key") {
    if (doc.primaryKey) return null;
    doc.primaryKey = { name: freshPartName(doc, `pk_${table}`), columns: [first.id] };
    return { kind, id: "primary-key" };
  }
  const name = freshPartName(doc, `${{ unique: "uq", index: "ix", "foreign-key": "fk", check: "ck" }[kind]}_${table}_${first.name}`);
  let entry: Json;
  if (kind === "unique") entry = { id: newId(), name, columns: [first.id] };
  else if (kind === "index") entry = { id: newId(), name, columns: [{ column: first.id }] };
  else if (kind === "foreign-key") {
    if (!options.referencesTable) return null;
    entry = { id: newId(), name, columns: [first.id], referencesTable: options.referencesTable };
  } else entry = { id: newId(), name, expression: { [options.dialect ?? "*"]: `${first.name} IS NOT NULL` } };
  doc[PART_MEMBER[kind]] = [...partEntries(doc, kind), entry];
  return { kind, id: name };
}

/** Removes a part (a column also leaves the keys and indexes it was in). False when the table has no such part. */
export function removePart(doc: Json, part: TablePart, view?: TableView | null): boolean {
  if (part.kind === "column") return deleteTableColumn(doc, part.id);
  if (part.kind === "primary-key") {
    if (!doc.primaryKey) return false;
    delete doc.primaryKey;
    return true;
  }
  const at = findPart(doc, view, part.kind, part.id);
  if (at < 0) return false;
  setList(
    doc,
    PART_MEMBER[part.kind],
    partEntries(doc, part.kind).filter((_, i) => i !== at),
  );
  return true;
}

/** Why a name cannot be a part's name, or null. */
export function partNameProblem(name: string): string | null {
  return name.trim() ? null : "A name cannot be empty.";
}

/** Renames a part; returns its new identity, or null when there is no such part or the name is empty. */
export function renamePart(doc: Json, part: TablePart, name: string, view?: TableView | null): TablePart | null {
  const next = name.trim();
  if (partNameProblem(next)) return null;
  if (part.kind === "column") {
    const column = list(doc, "columns").find((c) => c.id === part.id);
    if (!column) return null;
    column.name = next;
    return part;
  }
  if (part.kind === "primary-key") {
    const pk = doc.primaryKey as Json | undefined;
    if (!pk) return null;
    pk.name = next;
    return part;
  }
  const at = findPart(doc, view, part.kind, part.id);
  if (at < 0) return null;
  partEntries(doc, part.kind)[at].name = next;
  return { kind: part.kind, id: next };
}

/** A part's name as shown: a column's name, the primary key's, a constraint's own or resolved name. */
export function partName(doc: Json, part: TablePart, view?: TableView | null): string {
  if (part.kind === "column")
    return String(list(doc, "columns").find((c) => c.id === part.id)?.name ?? view?.columns.find((c) => c.key === part.id)?.name ?? part.id);
  if (part.kind === "primary-key") return String((doc.primaryKey as Json | undefined)?.name ?? view?.primaryKey?.name ?? "primary key");
  const at = findPart(doc, view, part.kind, part.id);
  return at < 0 ? part.id : partNameAt(doc, view, part.kind, at);
}

/**
 * An index column: a column of the table by its id, or an expression per dialect (`expression`, "*" for any) indexed instead of a
 * column; either may sort descending and take a key prefix `length` (MySQL). Index columns are found by their place in the list
 * (`at`), or a column one by its column id.
 */
const indexColumns = (entry: Json): Json[] => (Array.isArray(entry.columns) ? (entry.columns as Json[]) : []);
const indexColumnAt = (entry: Json, at: string | number): number =>
  typeof at === "number" ? (at >= 0 && at < indexColumns(entry).length ? at : -1) : indexColumns(entry).findIndex((c) => c.column === at);

/** An index column's members in the schema's order (column, expression, descending, length), the defaults left out. */
function indexColumnEntry(c: Json): Json {
  const out: Json = {};
  if (typeof c.column === "string") out.column = c.column;
  if (c.expression && typeof c.expression === "object") out.expression = c.expression;
  if (c.descending === true) out.descending = true;
  if (typeof c.length === "number") out.length = c.length;
  return out;
}

/** An index's column list with one column's sort order set (by its column id or its place); its other members are kept. */
export function setIndexColumnOrder(entry: Json, column: string | number, descending: boolean): void {
  const at = indexColumnAt(entry, column);
  if (at < 0) return;
  entry.columns = indexColumns(entry).map((c, i) => (i !== at ? c : indexColumnEntry({ ...c, descending })));
}

/** An index's column list with one column moved up or down (by its column id or its place); false at an end. */
export function moveIndexColumn(entry: Json, column: string | number, by: -1 | 1): boolean {
  const columns = [...indexColumns(entry)];
  const at = indexColumnAt(entry, column);
  const to = at + by;
  if (at < 0 || to < 0 || to >= columns.length) return false;
  [columns[at], columns[to]] = [columns[to], columns[at]];
  entry.columns = columns;
  return true;
}

/**
 * Ticks or unticks a column of an index: a column not in it is added at the end, one in it removed; its expressions stay. False
 * when the change would leave the index with no column or expression (the schema's minimum).
 */
export function toggleIndexColumn(entry: Json, column: string): boolean {
  const columns = indexColumns(entry);
  if (!columns.some((c) => c.column === column)) {
    entry.columns = [...columns, { column }];
    return true;
  }
  const next = columns.filter((c) => c.column !== column);
  if (!next.length) return false;
  entry.columns = next;
  return true;
}

/** Adds an expression to an index (for `dialect`, "*" for any), starting from `text`; returns its place. */
export function addIndexExpression(entry: Json, dialect: string, text: string): number {
  entry.columns = [...indexColumns(entry), { expression: { [dialect]: text } }];
  return indexColumns(entry).length - 1;
}

/**
 * Sets an index expression's text for one dialect ("*": any). An empty text removes that dialect's unless it is the last one
 * (an expression keeps one text); false when the place holds no expression or the edit would empty it.
 */
export function setIndexExpression(entry: Json, at: number, dialect: string, text: string): boolean {
  const columns = indexColumns(entry);
  const c = columns[at];
  if (!c || c.column !== undefined) return false;
  const map = { ...((c.expression as Record<string, string> | undefined) ?? {}) };
  const trimmed = text.trim();
  if (trimmed) map[dialect] = trimmed;
  else if (Object.keys(map).some((k) => k !== dialect)) delete map[dialect];
  else return false;
  entry.columns = columns.map((x, i) => (i === at ? indexColumnEntry({ ...x, expression: map }) : x));
  return true;
}

/** Why a typed key prefix length cannot be saved, or null: a whole number from 1, or empty (none). */
export function indexLengthProblem(raw: string): string | null {
  const text = raw.trim();
  if (text === "" || (/^\d+$/.test(text) && Number(text) >= 1)) return null;
  return "A prefix length is a whole number from 1, or empty.";
}

/** Sets or clears (undefined) an index column's key prefix length; false when the place is not in the index. */
export function setIndexColumnLength(entry: Json, at: number, length: number | undefined): boolean {
  const columns = indexColumns(entry);
  if (!columns[at]) return false;
  entry.columns = columns.map((c, i) => {
    if (i !== at) return c;
    const next = { ...c };
    if (length === undefined) delete next.length;
    else next.length = length;
    return indexColumnEntry(next);
  });
  return true;
}

/** Removes the index column at a place; false when it is the index's last one. */
export function removeIndexColumnAt(entry: Json, at: number): boolean {
  const columns = indexColumns(entry);
  if (columns.length < 2 || !columns[at]) return false;
  entry.columns = columns.filter((_, i) => i !== at);
  return true;
}

/** An index column as the tabs show it: the column's name, or the expression for `dialect` (else any dialect's) in parentheses. */
export function indexColumnText(c: Json, dialect: string, nameOf: (id: string) => string): string {
  if (typeof c.column === "string") return nameOf(c.column);
  const map = (c.expression as Record<string, string> | undefined) ?? {};
  const text = map[dialect] ?? map["*"];
  return text !== undefined ? `(${text})` : `(no expression for ${dialect})`;
}

/** What a key prefix length or an index expression does on a dialect (the sql-ddl pack's DDL and MQ4056), or null. */
export function indexColumnNote(dialect: string, has: { expression: boolean; length: boolean; operatorClass?: boolean }): string | null {
  const notes: string[] = [];
  if (has.expression && dialect === "sqlserver")
    notes.push("SQL Server indexes no expression: the index is left out (MQ4056); index a computed column instead.");
  if (has.expression && dialect === "mysql") notes.push("MySQL indexes expressions from 8.0.13 (not MariaDB).");
  if (has.length && dialect !== "mysql") notes.push("Only MySQL takes a prefix length; this dialect leaves it out (MQ4056).");
  if (has.operatorClass && dialect !== "postgresql") notes.push("Only PostgreSQL takes an operator class; this dialect leaves it out (MQ4056).");
  return notes.length ? notes.join(" ") : null;
}

/** Sets an index column's operator class (PostgreSQL), or removes it for an empty text; false when there is no such column. */
export function setIndexColumnOperatorClass(entry: Json, at: number, text: string): boolean {
  const column = ((entry.columns as Json[] | undefined) ?? [])[at];
  if (!column) return false;
  const value = text.trim();
  if (value) column.operatorClass = value;
  else delete column.operatorClass;
  return true;
}

/** What a dialect does with a temporal key (WITHOUT OVERLAPS, PERIOD): null on PostgreSQL (18 and later), else MQ4056. */
export function temporalNote(dialect: string): string | null {
  return dialect === "postgresql"
    ? "PostgreSQL 18 and later; the other columns of a temporal key need the btree_gist extension."
    : "Only PostgreSQL (18 and later) has temporal keys; this dialect leaves it out (MQ4056).";
}

/** What a unique constraint's nulls-not-distinct does on a dialect, or null where it is written (PostgreSQL). */
export function nullsNotDistinctNote(dialect: string): string | null {
  return dialect === "postgresql" ? null : "Only PostgreSQL (15 and later) has NULLS NOT DISTINCT; this dialect leaves it out (MQ4056).";
}

/** The on-delete actions that set columns, and so may name some of the key's columns (`onDeleteColumns`). */
export const SETS_COLUMNS = ["set-null", "set-default"] as const;

/** Whether an on-delete action sets columns (set null, set default). */
export function setsColumns(action: unknown): boolean {
  return (SETS_COLUMNS as readonly unknown[]).includes(action);
}

/** A foreign key's on-delete or on-update action; an on-delete that sets no columns drops the columns it set (MQ4060). */
export function setForeignKeyAction(entry: Json, member: "onDelete" | "onUpdate", action: string): void {
  setEntryMember(entry, member, action === "no-action" ? undefined : action);
  if (member === "onDelete" && !setsColumns(action)) delete entry.onDeleteColumns;
}

/** Ticks or unticks one of a foreign key's columns in the columns its on-delete sets; none left sets them all. */
export function toggleOnDeleteColumn(entry: Json, column: string): void {
  const current = (entry.onDeleteColumns as string[] | undefined) ?? [];
  const next = current.includes(column) ? current.filter((c) => c !== column) : [...current, column];
  // In the key's column order, as the DDL lists them.
  const order = (entry.columns as string[] | undefined) ?? [];
  setEntryMember(entry, "onDeleteColumns", next.length ? order.filter((c) => next.includes(c)) : undefined);
}

/** A foreign key's columns changed: the columns its on-delete sets keep only those still in the key (MQ4060). */
export function keepOnDeleteColumns(entry: Json): void {
  const current = entry.onDeleteColumns as string[] | undefined;
  if (!current) return;
  const columns = (entry.columns as string[] | undefined) ?? [];
  const next = current.filter((c) => columns.includes(c));
  setEntryMember(entry, "onDeleteColumns", next.length ? next : undefined);
}

/** What setting only some of a key's columns on delete does on a dialect, or null where it is written (PostgreSQL). */
export function onDeleteColumnsNote(dialect: string): string | null {
  return dialect === "postgresql"
    ? null
    : "Only PostgreSQL (15 and later) sets some of the key's columns; this dialect leaves the list out and sets them all (MQ4056).";
}

/** The foreign keys of the database's other tables that reference a table ("Referenced by"). */
export function incomingForeignKeys(tables: readonly TableView[], key: string): { table: TableView; name: string; columns: string[] }[] {
  const out: { table: TableView; name: string; columns: string[] }[] = [];
  for (const t of tables)
    for (const fk of t.foreignKeys)
      if (fk.referencedTable === key) out.push({ table: t, name: fk.name, columns: fk.columns.map((c) => t.columns.find((x) => x.key === c)?.name ?? c) });
  return out.sort((a, b) => a.table.name.localeCompare(b.table.name) || a.name.localeCompare(b.name));
}

/** The inspector's breadcrumb: the table, then the part ("billing.customers › email"). */
export function partCrumb(table: Pick<TableView, "schema" | "name">, partLabel: string | null): string {
  return partLabel ? `${qualifiedTable(table)} › ${partLabel}` : qualifiedTable(table);
}

/** What the materialize status says about one entity (the subset this module reads). */
export interface StatusEntity {
  id: string;
  projected: boolean;
}

/**
 * The tables of a database that can be stored as table files, by key, with what the conversion names (an id the database side
 * never shows): a table the model lays out on its own for one element, whose element the status lists as laid out here and
 * which is in no hierarchy (an element with a base, or the base of another, is converted by hand). Child tables and junction
 * tables are not among them.
 */
export function storableTables(
  tables: readonly Pick<TableView, "key" | "origin" | "entityId" | "isJunction">[],
  database: string,
  status: readonly StatusEntity[] | undefined,
  rows: readonly Pick<ElementSummary, "id" | "base">[] | undefined,
): Map<string, string> {
  const out = new Map<string, string>();
  if (!status) return out;
  const projected = new Set(status.filter((e) => e.projected).map((e) => e.id));
  const bases = new Set((rows ?? []).map((r) => r.base).filter((b): b is string => typeof b === "string"));
  const withBase = new Set((rows ?? []).filter((r) => typeof r.base === "string" && r.base).map((r) => r.id));
  for (const t of tables) {
    const owner = t.entityId;
    if (t.origin !== "synthesized" || t.isJunction || !owner || t.key !== `${owner}@${database}`) continue;
    if (!projected.has(owner) || bases.has(owner) || withBase.has(owner)) continue;
    out.set(t.key, owner);
  }
  return out;
}

/**
 * The undo entry of a saved batch whose operations expanded into creates, updates and deletes: each item's document before
 * (`priors`, read before the batch; none for a create) and after (its current document; none for a delete).
 */
export function batchUndoEntry(label: string, priors: ReadonlyMap<string, ModelJson>, items: readonly SaveResult[]): UndoEntry {
  // Created elements last: undoing deletes them after the changes that stop referring to them are put back. An item with
  // neither a document before nor after (nothing the undo could do) is left out.
  const saved = items
    .filter((i): i is SaveResult & { id: string } => typeof i.id === "string" && (priors.has(i.id) || !!i.current))
    .sort((a, b) => Number(!priors.has(a.id)) - Number(!priors.has(b.id)));
  return {
    label,
    ids: saved.map((i) => i.id),
    before: saved.map((i) => (priors.has(i.id) ? clone(priors.get(i.id)!) : null)),
    after: saved.map((i) => (i.current ? (clone(i.current.json) as ModelJson) : null)),
    afterHashes: saved.map((i) => i.hash ?? null),
  };
}

/** A list of ids without the ones that no longer exist (the same list when none went). */
export function existingIds(ids: readonly string[], exists: (id: string) => boolean): readonly string[] {
  const kept = ids.filter(exists);
  return kept.length === ids.length ? ids : kept;
}

/**
 * A part's edit on a table document (`entry` is the part's entry in it): refused (false) when the table no longer has the part
 * or the change changes nothing, so nothing is written and a table not stored as a file yet is not stored for it.
 */
export function editPart(doc: Json, entry: Json | undefined, change: (entry: Json, doc: Json) => void): false | undefined {
  if (!entry) return false;
  const before = JSON.stringify(doc);
  change(entry, doc);
  return JSON.stringify(doc) === before ? false : undefined;
}
