// A query written as SQL text, as the editor reads it: which text the database runs, the tables the text names (Find in SQL), and the
// reads and writes lists. Pure; the screens are in editors/database/SqlQueryParts.tsx.
import { scanSqlText, sqlDialectOf, sqlTextMentions } from "./sqlText";

/** A table or view of the database a query may read. `id` is what `reads` and `writes` hold (a table's key, a view's id). */
export interface TableChoice {
  id: string;
  name: string;
  schema: string | null;
  kind: "table" | "view";
}

/** Whether a document is a query written as SQL text. */
export const isSqlTextQuery = (doc: Record<string, unknown>): boolean =>
  doc.kind === "query" && !!doc.sql && typeof doc.sql === "object" && doc.from === undefined;

/** The text the database runs: its own dialect's, else the `*` text; null when there is neither. */
export function runningText(texts: Record<string, string> | undefined, dialect: string): { key: string; text: string } | null {
  for (const key of [dialect, "*"]) if (texts?.[key] !== undefined && texts[key].trim() !== "") return { key, text: texts[key] };
  return null;
}

/**
 * Find in SQL: the tables and views whose names appear in the text the database runs (ignoring case), not yet in `already`, in the
 * order of `choices`. It reads names only, as the engine's check does; it never says what the SQL reads.
 */
export function findTablesInSql(
  texts: Record<string, string> | undefined,
  dialect: string,
  choices: readonly TableChoice[],
  already: readonly string[],
): TableChoice[] {
  const found = runningText(texts, dialect);
  if (!found) return [];
  const scan = scanSqlText(found.text, sqlDialectOf(found.key, dialect));
  const have = new Set(already);
  return choices.filter((c) => !have.has(c.id) && sqlTextMentions(scan, c.name));
}

/** Adds ids to a list of the document (`reads` or `writes`), each once, in order; the member goes when it ends up empty. */
export function addToList(doc: Record<string, unknown>, member: "reads" | "writes", ids: readonly string[]): void {
  const list = Array.isArray(doc[member]) ? [...(doc[member] as string[])] : [];
  for (const id of ids) if (!list.includes(id)) list.push(id);
  if (list.length) doc[member] = list;
  else delete doc[member];
}

/** Removes the entry at `index` of `reads` or `writes`. */
export function removeFromList(doc: Record<string, unknown>, member: "reads" | "writes", index: number): void {
  const list = (Array.isArray(doc[member]) ? (doc[member] as string[]) : []).filter((_, i) => i !== index);
  if (list.length) doc[member] = list;
  else delete doc[member];
}

/** Adds a declared column (nullable, string) named for the first free `column_n`. */
export function addColumn(doc: Record<string, unknown>): void {
  const columns = (Array.isArray(doc.columns) ? doc.columns : []) as Record<string, unknown>[];
  const taken = new Set(columns.map((c) => String(c.name ?? "").toLowerCase()));
  let n = columns.length + 1;
  while (taken.has(`column_${n}`)) n++;
  doc.columns = [...columns, { name: `column_${n}`, type: "string" }];
}

/** Removes the declared column at `index`; `columns` goes when it ends up empty. */
export function removeColumn(doc: Record<string, unknown>, index: number): void {
  const columns = (Array.isArray(doc.columns) ? doc.columns : []).filter((_, i) => i !== index);
  if (columns.length) doc.columns = columns;
  else delete doc.columns;
}
