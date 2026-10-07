// The database diagram's foreign key edits (the owner: "seriously, how do I create a relationship/foreign key?"): a drag from a
// column (or a table's header) of table A onto a column (or the header) of table B becomes a draft the ForeignKeyDialog shows;
// Create or Save writes the key into table A (`foreignKeys` in schemas/v1/table.json) through the one way a table is written
// (`updateTable`, storeTables.ts): a table file is one save; a table not stored as a file yet (a legacy projected table) is stored
// as one first with the engine's materialize-tables, its real ids read from the batch's result, and the key written into the new
// file, both in one undo step. Delete removes a key the same way. The database side names no entities.
//
// Column keys: the draft names columns by the resolved view's keys; the edit runs on a document whose columns carry those keys
// (`replayOnFile`), so the same draft writes a stored table and a table stored on this very edit. A foreign key in the resolved
// view has no id: its file entry is found by name, else by its columns and referenced table.
import type { ColumnView, ForeignKeyView, TableView } from "@/api/types";
import { keyHolding, keysOfView, referencedKeyProblem } from "@/model/foreignKeyTarget";
import { tableFileTarget } from "./columnEdits";
import { keepOnDeleteColumns, setsColumns } from "./tableParts";

type Json = Record<string, unknown>;

export const FK_ACTIONS = ["no-action", "restrict", "cascade", "set-null", "set-default"] as const;
export type FkAction = (typeof FK_ACTIONS)[number];
export const FK_ACTION_LABELS: Record<FkAction, string> = {
  "no-action": "No action",
  restrict: "Restrict",
  cascade: "Cascade",
  "set-null": "Set null",
  "set-default": "Set default",
};

/** The built-in foreign key name pattern (the engine's EffectiveConventions.ForeignKeyName). */
export const DEFAULT_FK_PATTERN = "fk_{table}_{columns}";

/** When a key is checked (`deferrable`): not deferrable (the default, left out of the file), or deferrable. */
export const FK_DEFERRABLE = ["not-deferrable", "initially-immediate", "initially-deferred"] as const;
export type FkDeferrable = (typeof FK_DEFERRABLE)[number];
export const FK_DEFERRABLE_LABELS: Record<FkDeferrable, string> = {
  "not-deferrable": "Not deferrable",
  "initially-immediate": "Deferrable, checked per statement",
  "initially-deferred": "Deferrable, checked at commit",
};

/** The dialects whose foreign keys are checked on every statement: a deferrable key is left out there (MQ4056). */
export const NO_DEFERRABLE_DIALECTS: ReadonlySet<string> = new Set(["sqlserver", "mysql"]);
const DIALECT_WORDS: Record<string, string> = { postgresql: "PostgreSQL", sqlserver: "SQL Server", mysql: "MySQL", sqlite: "SQLite", oracle: "Oracle" };
/** The note a Deferrable choice carries on a dialect without deferrable keys, or undefined. */
export const deferrableNote = (dialect: string): string | undefined =>
  NO_DEFERRABLE_DIALECTS.has(dialect)
    ? `${DIALECT_WORDS[dialect] ?? dialect} checks every foreign key on each statement: a deferrable key is left out of its DDL (MQ4056).`
    : undefined;

/** One column pair: the referencing column of table A (a column key, or a new column to add) and the referenced column of B. */
export interface FkPair {
  /** A column key of table A, or null when the pair adds a new column (`newColumn`) or is not chosen yet. */
  column: string | null;
  /** The name of a column to add to table A, typed like the referenced column. */
  newColumn?: string;
  /** A column key of table B ("" when not chosen yet). */
  referenced: string;
}

/** What the foreign key dialog edits. */
export interface FkDraft {
  /** Table A's key: the table that holds the key. */
  table: string;
  /** The key being edited (as the view shows it), or null for a new key. */
  editing: Pick<ForeignKeyView, "name" | "columns" | "referencedTable"> | null;
  name: string;
  referencedTable: string;
  pairs: FkPair[];
  onDelete: FkAction;
  onUpdate: FkAction;
  deferrable: FkDeferrable;
}

/** A drop: where the drag started (a column of A, or A's header) and where it ended (a column of B, or B's header). */
export interface FkDrop {
  sourceTable: string;
  sourceColumn: string | null;
  targetTable: string;
  targetColumn: string | null;
}

/** The project's foreign key name pattern for a database: the database's conventions over the project's, else the default. */
export function fkNamePattern(settings: unknown, databaseName: string | null): string {
  const json = (settings ?? {}) as { conventions?: { foreignKeyName?: unknown }; databases?: Record<string, { foreignKeyName?: unknown } | undefined> };
  const own = databaseName ? json.databases?.[databaseName]?.foreignKeyName : undefined;
  if (typeof own === "string" && own) return own;
  const project = json.conventions?.foreignKeyName;
  return typeof project === "string" && project ? project : DEFAULT_FK_PATTERN;
}

/**
 * The default name of a key: the pattern with `{table}` and `{columns}` (the column names joined with `_`) filled in, as the
 * engine does when a key has no name. Without columns yet: `fk_<a>_<b>`.
 */
export function defaultFkName(pattern: string, tableName: string, columnNames: readonly string[], referencedName = ""): string {
  if (!columnNames.length) return `fk_${tableName}${referencedName ? `_${referencedName}` : ""}`;
  return pattern.replaceAll("{table}", tableName).replaceAll("{columns}", columnNames.join("_"));
}

const byKey = (tables: readonly TableView[], key: string) => tables.find((t) => t.key === key) ?? null;
const columnOf = (table: TableView | null, key: string | null) => (table && key ? (table.columns.find((c) => c.key === key) ?? null) : null);

/** A name for a column of A that references B's key column: `<b singular>_<key>`, as `invoice_id` for invoices.id. */
export function newColumnName(referenced: TableView, column: Pick<ColumnView, "name">, taken: readonly string[] = []): string {
  const base = referenced.name.endsWith("ies")
    ? `${referenced.name.slice(0, -3)}y`
    : referenced.name.endsWith("s") && !referenced.name.endsWith("ss")
      ? referenced.name.slice(0, -1)
      : referenced.name;
  let name = `${base}_${column.name}`;
  for (let i = 2; taken.includes(name); i++) name = `${base}_${column.name}_${i}`;
  return name;
}

/** B's key columns, the columns a new key references by default (none: one pair to choose). */
const keyOf = (table: TableView | null): string[] => table?.primaryKey?.columns ?? [];

/** The column of A a header drag pairs with B's key column: one with the same name, or the `<b singular>_<key>` name. */
function guessColumn(a: TableView, b: TableView, referenced: ColumnView | null): string | null {
  if (!referenced) return null;
  const wanted = [newColumnName(b, referenced), referenced.name === "id" ? "" : referenced.name].filter(Boolean);
  return a.columns.find((c) => wanted.includes(c.name))?.key ?? null;
}

/** The dialog's draft for a drop: the dragged column paired with the drop target or B's key, a default name, no action. */
export function draftFromDrop(tables: readonly TableView[], drop: FkDrop, pattern: string, dialect = "postgresql"): FkDraft {
  const a = byKey(tables, drop.sourceTable);
  const b = byKey(tables, drop.targetTable);
  const key = keyOf(b);
  // A drop on one column of a composite key references the whole key: the dragged column takes the dropped one's place.
  const holding = b && drop.targetColumn ? keyHolding(keysOfView(b), drop.targetColumn, dialect) : null;
  let pairs: FkPair[];
  if (drop.targetColumn && holding && holding.length > 1)
    pairs = holding.map((ref) => ({
      column: ref === drop.targetColumn ? drop.sourceColumn : a && b ? guessColumn(a, b, columnOf(b, ref)) : null,
      referenced: ref,
    }));
  else if (drop.targetColumn) pairs = [{ column: drop.sourceColumn, referenced: drop.targetColumn }];
  else if (key.length) {
    // B's key, column by column: the dragged column takes the first; the others are found by name or left to choose.
    pairs = key.map((ref, i) => ({
      column: i === 0 && drop.sourceColumn ? drop.sourceColumn : a && b ? guessColumn(a, b, columnOf(b, ref)) : null,
      referenced: ref,
    }));
  } else pairs = [{ column: drop.sourceColumn, referenced: "" }];
  const draft: FkDraft = {
    table: drop.sourceTable,
    editing: null,
    name: "",
    referencedTable: drop.targetTable,
    pairs,
    onDelete: "no-action",
    onUpdate: "no-action",
    deferrable: "not-deferrable",
  };
  return { ...draft, name: draftDefaultName(tables, draft, pattern) };
}

/** The default name for a draft's current columns. */
export function draftDefaultName(tables: readonly TableView[], draft: FkDraft, pattern: string): string {
  const a = byKey(tables, draft.table);
  const b = byKey(tables, draft.referencedTable);
  const names = draft.pairs.map((p) => p.newColumn?.trim() || columnOf(a, p.column)?.name || "").filter(Boolean);
  return defaultFkName(pattern, a?.name ?? "", names.length === draft.pairs.length ? names : [], b?.name ?? "");
}

/** The draft of an existing key (double-click on its edge, Edit… in the inspector); `entry` is its entry in the table's file,
 * when there is one (the view does not carry `deferrable`). */
export function draftFromForeignKey(table: TableView, fk: ForeignKeyView, tables: readonly TableView[], entry?: Json): FkDraft {
  const b = byKey(tables, fk.referencedTable);
  const referenced = fk.referencedColumns.length ? fk.referencedColumns : keyOf(b);
  return {
    table: table.key,
    editing: { name: fk.name, columns: [...fk.columns], referencedTable: fk.referencedTable },
    name: fk.name,
    referencedTable: fk.referencedTable,
    pairs: fk.columns.map((column, i) => ({ column, referenced: referenced[i] ?? "" })),
    onDelete: asAction(fk.onDelete),
    onUpdate: asAction(fk.onUpdate),
    deferrable: (FK_DEFERRABLE as readonly string[]).includes(String(entry?.deferrable)) ? (entry!.deferrable as FkDeferrable) : "not-deferrable",
  };
}

/** A new key on table A from the explorer or the Foreign keys tab: no columns or referenced table chosen yet. */
export function draftForTable(tables: readonly TableView[], table: string, pattern: string): FkDraft {
  return { ...draftFromDrop(tables, { sourceTable: table, sourceColumn: null, targetTable: "", targetColumn: null }, pattern), referencedTable: "" };
}

/** The view says "no action" or "no-action", "set null" or "set-null"; the file says the kebab form. */
export function asAction(text: string): FkAction {
  const kebab = text
    .trim()
    .toLowerCase()
    .replace(/[\s_]+/g, "-");
  return (FK_ACTIONS as readonly string[]).includes(kebab) ? (kebab as FkAction) : "no-action";
}

const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** What keeps a draft from being saved (empty: nothing), and warnings that do not (native types that differ). */
export function fkProblems(tables: readonly TableView[], draft: FkDraft, dialect = "postgresql"): { errors: string[]; warnings: string[] } {
  const errors: string[] = [];
  const warnings: string[] = [];
  const a = byKey(tables, draft.table);
  const b = byKey(tables, draft.referencedTable);
  if (!a) errors.push("The table is no longer in the database.");
  if (!b) errors.push("Choose the referenced table.");
  if (draft.name.trim() && !IDENTIFIER.test(draft.name.trim())) errors.push("Use letters, digits and underscores in the name, not starting with a digit.");
  if (!draft.pairs.length) errors.push("Add at least one column pair.");
  const used = new Set<string>();
  draft.pairs.forEach((p, i) => {
    const n = draft.pairs.length > 1 ? ` ${i + 1}` : "";
    const newName = p.newColumn?.trim();
    if (p.newColumn !== undefined) {
      if (!newName) errors.push(`Name the new column of pair${n}.`);
      else if (!IDENTIFIER.test(newName)) errors.push(`Use letters, digits and underscores in the new column's name${n ? ` (pair${n})` : ""}.`);
      else if (a?.columns.some((c) => c.name.toLowerCase() === newName.toLowerCase())) errors.push(`${a.name} already has a column ${newName}.`);
    } else if (!p.column) errors.push(`Choose the column of pair${n}.`);
    if (!p.referenced) errors.push(`Choose the referenced column of pair${n}.`);
    const id = p.newColumn !== undefined ? `new:${newName}` : (p.column ?? "");
    if (id && used.has(id)) errors.push(`A column is used twice.`);
    used.add(id);
    const from = columnOf(a, p.column);
    const to = columnOf(b, p.referenced);
    if (from && to && from.nativeType !== to.nativeType) warnings.push(`${from.name} is ${from.nativeType}; ${b!.name}.${to.name} is ${to.nativeType}.`);
  });
  // MQ4059: what a key references is the referenced table's primary key or one of its unique keys, each column once; on MySQL an
  // index of it must start with them in the key's order.
  const referenced = draft.pairs.map((p) => p.referenced).filter(Boolean);
  const problem = b && referenced.length === draft.pairs.length ? referencedKeyProblem(keysOfView(b), referenced, dialect) : null;
  if (b && problem) {
    const names = referenced.map((k) => columnOf(b, k)?.name ?? k).join(", ");
    if (problem.kind === "twice")
      errors.push(`${b.name}.${columnOf(b, problem.column)?.name ?? problem.column} is referenced twice: reference each column of the key once (MQ4059).`);
    else if (problem.kind === "order")
      errors.push(
        `MySQL needs an index of ${b.name} whose first columns are (${names}) in this order (MQ4059): pair the columns in the key's order, or add such an index first.`,
      );
    else
      errors.push(
        `${b.name} (${names}) is neither its primary key nor one of its unique keys: a foreign key references a key (MQ4059). Pick the key's columns, or add a unique constraint on these first.`,
      );
  }
  return { errors: [...new Set(errors)], warnings };
}

const sameList = (x: unknown, y: readonly string[]) => Array.isArray(x) && x.length === y.length && x.every((v, i) => v === y[i]);

/** A key's entry in table A's document: by name, else by its columns and referenced table. */
export function fkEntryOf(doc: Json, fk: Pick<ForeignKeyView, "name" | "columns" | "referencedTable">): Json | undefined {
  const list = Array.isArray(doc.foreignKeys) ? (doc.foreignKeys as Json[]) : [];
  const named = list.find((e) => e.name === fk.name);
  if (named) return named;
  return list.find((e) => sameList(e.columns, fk.columns) && e.referencesTable === fk.referencedTable);
}

/**
 * Writes a draft into table A's document (mutating it; its columns named by the view's keys): new columns typed like the column
 * they reference, then the key, an edited entry starting from what it held (its id, comment, properties and anything else the
 * dialog does not show survive). The name is written unless it is the default the convention would give anyway; a "no action"
 * rule and "not deferrable" are left out (the defaults). Returns an error, or null.
 */
export function applyDraft(doc: Json, draft: FkDraft, ctx: { tables: readonly TableView[]; pattern: string; newId: () => string }): string | null {
  const a = byKey(ctx.tables, draft.table);
  const b = byKey(ctx.tables, draft.referencedTable);
  if (!a || !b) return "The table is no longer in the database.";
  const columns = Array.isArray(doc.columns) ? (doc.columns as Json[]) : [];
  const fkColumns: string[] = [];
  for (const pair of draft.pairs) {
    if (pair.newColumn !== undefined) {
      const like = columnOf(b, pair.referenced);
      const id = ctx.newId();
      columns.push(newColumnEntry(id, pair.newColumn.trim(), like));
      fkColumns.push(id);
    } else {
      if (!pair.column || !columns.some((c) => c.id === pair.column))
        return `Column ${columnOf(a, pair.column)?.name ?? pair.column ?? ""} is not in the table.`;
      fkColumns.push(pair.column);
    }
  }
  if (columns.length) doc.columns = columns;
  const list = Array.isArray(doc.foreignKeys) ? (doc.foreignKeys as Json[]) : [];
  const old = draft.editing ? fkEntryOf(doc, draft.editing) : undefined;
  if (draft.editing && !old) return `Foreign key ${draft.editing.name} is not in the table.`;
  const names = draft.pairs.map((p) => p.newColumn?.trim() || columnOf(a, p.column)?.name || "");
  const conventional = defaultFkName(ctx.pattern, a.name, names);
  const name = draft.name.trim();
  const entry: Json = {
    ...(old ?? {}),
    id: old?.id ?? ctx.newId(),
    columns: fkColumns,
    referencesTable: draft.referencedTable,
    referencesColumns: draft.pairs.map((p) => p.referenced),
  };
  if (name && name !== conventional) entry.name = name;
  else delete entry.name;
  for (const [member, value, none] of [
    ["onDelete", draft.onDelete, "no-action"],
    ["onUpdate", draft.onUpdate, "no-action"],
    ["deferrable", draft.deferrable ?? "not-deferrable", "not-deferrable"],
  ] as const) {
    if (value === none) delete entry[member];
    else entry[member] = value;
  }
  // The columns the on-delete sets (picked in the inspector) stay while it still sets columns and they are still in the key.
  if (setsColumns(entry.onDelete)) keepOnDeleteColumns(entry);
  else delete entry.onDeleteColumns;
  if (old) list[list.indexOf(old)] = entry;
  else list.push(entry);
  doc.foreignKeys = list;
  return null;
}

/** A new column of A typed like the referenced column (built-in type and facets; nullable, as a new column is). */
function newColumnEntry(id: string, name: string, like: ColumnView | null): Json {
  const entry: Json = { id, name, type: like?.type ?? "string" };
  for (const facet of ["length", "precision", "scale"] as const) if (like && like[facet] !== null && like[facet] !== undefined) entry[facet] = like[facet];
  return entry;
}

/** Removes a key from table A's document; false when the document does not hold it. */
export function removeForeignKey(doc: Json, fk: Pick<ForeignKeyView, "name" | "columns" | "referencedTable">): boolean {
  const entry = fkEntryOf(doc, fk);
  if (!entry) return false;
  const rest = (doc.foreignKeys as Json[]).filter((e) => e !== entry);
  if (rest.length) doc.foreignKeys = rest;
  else delete doc.foreignKeys;
  return true;
}

/** Crow's foot ends of a key's edge, from A's side (the referencing rows) to B's (the referenced row). */
export type FkEnd = "one" | "zero-or-one" | "many";

/**
 * The ends: at A, many rows per B row unless A's key columns are unique (its primary key, a unique constraint or a unique
 * index on exactly those columns), then at most one; at B, exactly one when every key column is not null, else zero or one.
 */
export function fkEnds(table: TableView, fk: Pick<ForeignKeyView, "columns">): { source: FkEnd; target: FkEnd } {
  const set = (cols: readonly string[]) => cols.length === fk.columns.length && fk.columns.every((c) => cols.includes(c));
  const unique =
    (table.primaryKey ? set(table.primaryKey.columns) : false) ||
    table.uniques.some((u) => set(u.columns)) ||
    table.indexes.some((i) => i.unique && set(i.columns.map((c) => c.column)));
  const mandatory = fk.columns.every((key) => table.columns.find((c) => c.key === key)?.nullable === false);
  return { source: unique ? "zero-or-one" : "many", target: mandatory ? "one" : "zero-or-one" };
}

/** Readable words for an end, for the edge's tooltip. */
export const END_WORDS: Record<FkEnd, string> = { one: "exactly one", "zero-or-one": "zero or one", many: "zero or many" };

/** A column's single-column unique marker: a unique constraint or a unique index on that column alone (the key excluded). */
export function uniqueColumns(table: TableView): Set<string> {
  const out = new Set<string>();
  for (const u of table.uniques) if (u.columns.length === 1) out.add(u.columns[0]);
  for (const i of table.indexes) if (i.unique && i.columns.length === 1) out.add(i.columns[0].column);
  return out;
}

/** The entity a legacy projected table converts with (its own table, not a child or junction table), or null. */
export function legacyEntityOf(table: Pick<TableView, "key" | "entityId" | "relationId" | "origin">, databaseId: string): string | null {
  if (table.origin !== "synthesized") return null;
  const target = tableFileTarget(table, databaseId);
  return target.kind === "overlay" && "entity" in target && !target.attribute ? target.entity : null;
}

/** Whether a table is not stored as a table file yet (a legacy projected table). */
export const isLegacy = (table: Pick<TableView, "origin">) => table.origin === "synthesized";

/**
 * A draft whose tables were stored as table files while the dialog was open (another edit stored them): the draft follows them
 * to their files, its columns matched by name (a stored table keeps its columns' names). `fileOf` names a stored table's file
 * and `before` the table as it was resolved then. Returns the followed draft, or null when nothing moved.
 */
export function followStoredDraft(
  draft: FkDraft,
  tables: readonly TableView[],
  fileOf: (key: string) => string | undefined,
  before: (key: string) => TableView | undefined,
): FkDraft | null {
  const present = (key: string) => tables.some((t) => t.key === key);
  const move = (key: string) => {
    if (!key || present(key)) return null;
    const file = fileOf(key);
    const now = file ? tables.find((t) => t.key === file) : undefined;
    if (!file || !now) return null;
    const old = before(key);
    const columns = new Map<string, string>();
    for (const c of old?.columns ?? []) {
      const same = now.columns.find((x) => x.name === c.name);
      if (same) columns.set(c.key, same.key);
    }
    return { file, column: (k: string | null) => (k ? (columns.get(k) ?? k) : k) };
  };
  const a = move(draft.table);
  const b = move(draft.referencedTable);
  const editingB = draft.editing ? move(draft.editing.referencedTable) : null;
  if (!a && !b && !editingB) return null;
  return {
    ...draft,
    table: a?.file ?? draft.table,
    referencedTable: b?.file ?? draft.referencedTable,
    pairs: draft.pairs.map((p) => ({ ...p, column: a ? a.column(p.column) : p.column, referenced: b ? (b.column(p.referenced) ?? "") : p.referenced })),
    editing: draft.editing
      ? {
          ...draft.editing,
          columns: a ? draft.editing.columns.map((c) => a.column(c) ?? c) : draft.editing.columns,
          referencedTable: editingB?.file ?? draft.editing.referencedTable,
        }
      : null,
  };
}
