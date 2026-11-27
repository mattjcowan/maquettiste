// A designed table's document as the Storage tab's table-per-type and child table sections read it, free of React: its columns as
// source columns, the keys, and which foreign keys fit what a binding asks of them.
import type { SourceColumn } from "./fieldMap";

export interface TableDocColumn {
  id: string;
  name?: string;
  type?: unknown;
  nullable?: boolean;
  identity?: unknown;
  computed?: unknown;
  default?: unknown;
  defaultSql?: unknown;
}
export interface TableDocFk {
  id: string;
  name?: string;
  columns: string[];
  referencesTable: string;
  referencesColumns?: string[];
  onDelete?: string;
}
export interface TableDoc {
  id: string;
  name?: string;
  columns?: TableDocColumn[];
  primaryKey?: { columns: string[] };
  uniques?: { columns: string[] }[];
  foreignKeys?: TableDocFk[];
}

/** A key's column refs (ids or names) as the table's column ids; an unknown ref stays as written. */
export function columnIds(table: TableDoc | undefined, refs: readonly string[]): string[] {
  return refs.map((r) => table?.columns?.find((c) => c.id === r || c.name === r)?.id ?? r);
}

/** The table's columns as binding source columns (the key is the column's id). */
export function tableDocColumns(table: TableDoc | undefined): SourceColumn[] {
  const pk = new Set(columnIds(table, table?.primaryKey?.columns ?? []));
  return (table?.columns ?? []).map((c) => ({
    key: c.id,
    name: c.name ?? c.id,
    type: typeof c.type === "string" ? c.type : null,
    nullable: c.nullable !== false && !pk.has(c.id),
    identity: !!c.identity,
    computed: !!c.computed,
    hasDefault: (c.default !== undefined && c.default !== null) || !!c.defaultSql,
    isPrimaryKey: pk.has(c.id),
  }));
}

const sameSet = (a: readonly string[], b: readonly string[]) => a.length === b.length && a.every((x) => b.includes(x));

/** Whether the columns (ids) are exactly the table's primary key. */
export function isWholePrimaryKey(table: TableDoc | undefined, columns: readonly string[]): boolean {
  const pk = table?.primaryKey?.columns;
  return !!pk?.length && sameSet(columnIds(table, pk), columns);
}

/** Whether the columns (ids) are the primary key or a unique constraint of the table. */
export function isPrimaryOrUnique(table: TableDoc | undefined, columns: readonly string[]): boolean {
  if (isWholePrimaryKey(table, columns)) return true;
  return (table?.uniques ?? []).some((u) => sameSet(columnIds(table, u.columns), columns));
}

/** The table key a binding keeps its rows in, as a plain table id; undefined for a projected (`key@database`) table. */
export function plainTable(key: string | undefined | null): string | undefined {
  return key && !key.includes("@") ? key : undefined;
}

/** "fk_name (col, col → table)". */
export function foreignKeyLabel(fk: TableDocFk, holder: TableDoc | undefined, referenced: TableDoc | undefined): string {
  const names = columnIds(holder, fk.columns).map((c) => holder?.columns?.find((x) => x.id === c)?.name ?? c);
  return `${fk.name ?? fk.id} (${names.join(", ")} → ${referenced?.name ?? fk.referencesTable})`;
}
