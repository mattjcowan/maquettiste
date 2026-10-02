// The Database screen's table section (explorer-redesign.md 1.3): the table summaries filtered by name, sorted by
// name, capped so the list stays quick on a 10,000-table database. Pure.
import type { TableSummary } from "@/api/types";

export const TABLE_LIST_CAP = 300;

/** Tables whose name (or schema.name) contains every word of the filter, case-insensitively; at most `cap`. */
export function filterTables(tables: readonly TableSummary[], filter: string, cap = TABLE_LIST_CAP): { tables: TableSummary[]; total: number; more: number } {
  const words = filter.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const matching = tables
    .filter((t) => {
      const text = `${t.schema ?? ""}.${t.name}`.toLowerCase();
      return words.every((w) => text.includes(w));
    })
    .sort((a, b) => a.name.localeCompare(b.name) || (a.schema ?? "").localeCompare(b.schema ?? ""));
  return { tables: matching.slice(0, cap), total: matching.length, more: Math.max(0, matching.length - cap) };
}

/**
 * The databases the Database screen offers: the index's database rows (fresh after a create, a rename or a delete),
 * in the project's order, new ones by name after it; the project's list alone until the index has loaded.
 */
export function databaseList<T extends { id: string; kind: string; name: string }>(project: readonly T[] | undefined, index: readonly T[] | undefined): T[] {
  if (!index) return [...(project ?? [])];
  const order = new Map((project ?? []).map((d, i) => [d.id, i]));
  const at = (id: string) => order.get(id) ?? Number.MAX_SAFE_INTEGER;
  return index.filter((r) => r.kind === "database").sort((a, b) => at(a.id) - at(b.id) || a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
}

/** The Database screen draws at most this many tables (explorer-redesign.md 1.3). */
export const CANVAS_CAP = 300;

export type TableScope = { mode: "all" } | { mode: "scoped"; focus: string; keys: Set<string>; more: number } | { mode: "list"; total: number };

/**
 * What the Database screen draws: every table up to the cap; above it, the selected table and its foreign-key
 * neighbours (both directions, sorted by key, the focus first, at most `cap`; `more` counts the neighbours left
 * out), or with no table selected the list-and-DDL form (no canvas). Pure and deterministic.
 */
export function scopeTables(
  tables: readonly { key: string; foreignKeys: readonly { referencedTable: string }[] }[],
  focus: string | null,
  cap = CANVAS_CAP,
): TableScope {
  if (tables.length <= cap) return { mode: "all" };
  if (!focus || !tables.some((t) => t.key === focus)) return { mode: "list", total: tables.length };
  const neighbours = new Set<string>();
  for (const t of tables) {
    if (t.key === focus) for (const fk of t.foreignKeys) neighbours.add(fk.referencedTable);
    else if (t.foreignKeys.some((fk) => fk.referencedTable === focus)) neighbours.add(t.key);
  }
  neighbours.delete(focus);
  const known = new Set(tables.map((t) => t.key));
  const sorted = [...neighbours].filter((k) => known.has(k)).sort();
  const shown = sorted.slice(0, Math.max(0, cap - 1));
  return { mode: "scoped", focus, keys: new Set([focus, ...shown]), more: sorted.length - shown.length };
}

/**
 * The resolved key of the table a table file shapes, from the database's table summaries: a designed or imported table's key
 * is its file id; an overlay on an entity is that entity's projected table (`<entityId>@<databaseId>`). Null when the
 * summaries do not say (not loaded, an overlay of a child table or a junction): the caller falls back to the file.
 */
export function tableKeyOfFile(
  file: { id: string; entity?: string; database?: string },
  tables: readonly Pick<TableSummary, "key" | "entityId" | "isJunction" | "origin">[] | undefined,
): string | null {
  if (!tables || !file.database) return null;
  if (tables.some((t) => t.key === file.id)) return file.id;
  const projected = `${file.entity ?? ""}@${file.database}`;
  if (file.entity && tables.some((t) => t.key === projected && t.origin === "synthesized" && !t.isJunction)) return projected;
  return null;
}

/** What the Database screen's list shows: its tables, views, sequences, routines, database types or SQL objects (a kind chip
 * picks). */
export type ListKind = "table" | ObjectListKind;
/** The kinds the list shows from the resolved database view (everything but tables, which come from the table summaries). */
export type ObjectListKind = "view" | "sequence" | "routine" | "database-type" | "sql-object";
export const LIST_KINDS: readonly { kind: ListKind; label: string }[] = [
  { kind: "table", label: "Tables" },
  { kind: "view", label: "Views" },
  { kind: "sequence", label: "Sequences" },
  { kind: "routine", label: "Routines" },
  { kind: "database-type", label: "Types" },
  { kind: "sql-object", label: "Objects" },
];

/** The member of the resolved database view that lists each kind. */
export const OBJECT_LIST_MEMBERS: Record<ObjectListKind, "views" | "sequences" | "routines" | "types" | "objects"> = {
  view: "views",
  sequence: "sequences",
  routine: "routines",
  "database-type": "types",
  "sql-object": "objects",
};

/** A database's views, sequences, routines, database types or SQL objects whose name (or schema.name) contains every word of the filter, by name; at most `cap`. */
export function filterObjects<T extends { id: string; name: string; schema: string | null }>(
  objects: readonly T[],
  filter: string,
  cap = TABLE_LIST_CAP,
): { items: T[]; total: number } {
  const words = filter.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const matching = objects
    .filter((o) => words.every((w) => `${o.schema ?? ""}.${o.name}`.toLowerCase().includes(w)))
    .sort((a, b) => a.name.localeCompare(b.name) || (a.schema ?? "").localeCompare(b.schema ?? ""));
  return { items: matching.slice(0, cap), total: matching.length };
}
