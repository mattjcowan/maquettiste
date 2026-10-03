// Renames that rewrite what names the renamed thing, pure. A table column renamed: the same table's check expressions, index
// filters, computed expressions and per-dialect defaults name it by its physical name, and so may the bodies of the database's
// views, its queries (`alias.column` references and per-dialect SQL expressions) and the storage bindings that name it by name
// rather than by id; all of them change in the same batch as the column (one save, one undo step). SQL text is rewritten token
// by token (sqlIdentifiers.ts), only columns and only where the match is certain. A view whose body returns the column under
// its own name (`SELECT email`) while CREATE VIEW names no column list gets `mail AS email`, so the view's column keeps its
// name. Left as it is and listed to check by hand: a name that is also an SQL keyword, a name the text also uses as a table,
// an alias or a qualifier, a view that reads another table with a column of the same name, a view that returns `*`, a query's
// bare reference that another source may hold, and every routine and SQL object whose text names the column. A view's
// declared column renamed: its body follows when CREATE VIEW names no column list, and so do the bindings that read it. A
// table renamed: the views, queries, routines and SQL objects whose text names the old table are listed to check.
import type { TableView } from "@/api/types";
import { queryTable, renameInQuery } from "./queryColumns";
import { mapMentions, mapSelectsAll, renameIdentifier, renameInDialectMap, renameOutputInDialectMap, type Uncertainty } from "./sqlIdentifiers";

type Json = Record<string, unknown>;

const list = (doc: Json, member: string): Json[] => (Array.isArray(doc[member]) ? (doc[member] as Json[]) : []);
const same = (a: unknown, b: string) => typeof a === "string" && a.toLowerCase() === b.toLowerCase();

export interface RenamePlan {
  /** Where the name was rewritten on the database side, in words. */
  rewritten: string[];
  /** Where it may still be named, to check by hand. */
  check: string[];
  /** The documents whose storage bindings follow (by id; the notice says only how many). */
  bindings: string[];
  /** The changes, by document id (the table's included). */
  edits: Map<string, (doc: Json) => void>;
}

/** Renames a column name in the bindings of a document that read or write `source`; returns how many places changed. */
export function renameBindingColumns(entity: Json, source: string, renames: ReadonlyMap<string, string>): number {
  let n = 0;
  const to = (v: unknown): string | null => {
    if (typeof v !== "string") return null;
    for (const [from, next] of renames) if (same(v, from)) return next;
    return null;
  };
  for (const b of list(entity, "bindings")) {
    const write = b.write as Json | string | undefined;
    if (b.source !== source && !(typeof write === "object" && write?.table === source)) continue;
    for (const member of ["fields", "constants", "columns"])
      for (const e of list(b, member)) {
        const next = to(e.column);
        if (next) {
          e.column = next;
          n++;
        }
      }
    const soft = typeof b.delete === "object" ? ((b.delete as Json).soft as Json | undefined) : undefined;
    const next = soft ? to(soft.column) : null;
    if (soft && next) {
      soft.column = next;
      n++;
    }
  }
  return n;
}

/** "2 storage bindings follow", or nothing: the database side never names what the bindings belong to. */
export const bindingsFollow = (n: number): string => (n ? `${n} storage ${n === 1 ? "binding follows" : "bindings follow"}` : "");

const places = (n: number) => `${n} ${n === 1 ? "place" : "places"}`;

/** Why a text was left as it is, in words. */
function because(why: Uncertainty | undefined, from: string): string {
  if (why === "keyword") return ` (${from} is also an SQL word)`;
  if (why === "ambiguous") return ` (it also uses ${from} as a table, an alias or a qualifier)`;
  if (why === "star") return ` (it returns * and no column list: its column ${from} would be renamed)`;
  return "";
}

/** Renames a column of a table document and what names it in the table's SQL texts; says where, and what to check by hand. */
function renameInTable(d: Json, column: string, from: string, to: string, dialect?: string): { rewritten: string[]; check: string[] } {
  const rewritten: string[] = [];
  const check: string[] = [];
  const tableName = String(d.name ?? "");
  const own = list(d, "columns").find((c) => c.id === column);
  if (own) own.name = to;
  const note = (what: string, r: { count: number; uncertain: boolean; why?: Uncertainty }) => {
    if (r.uncertain) check.push(`${what} of ${tableName}${because(r.why, from)}`);
    else if (r.count) rewritten.push(what);
  };
  for (const ck of list(d, "checks")) {
    const r = renameInDialectMap(ck.expression, from, to, { dialect });
    if (r.map && r.count && !r.uncertain) ck.expression = r.map;
    note(`check ${String(ck.name ?? ck.id)}`, r);
  }
  for (const ix of list(d, "indexes")) {
    for (const ic of list(ix, "columns")) {
      if (!ic.expression || typeof ic.expression !== "object") continue;
      const r = renameInDialectMap(ic.expression, from, to, { dialect });
      if (r.map && r.count && !r.uncertain) ic.expression = r.map;
      note(`an expression of index ${String(ix.name ?? ix.id)}`, r);
    }
    if (typeof ix.where !== "string") continue;
    const r = renameIdentifier(ix.where, from, to, { dialect });
    if (r.count) ix.where = r.text;
    note(`the filter of index ${String(ix.name ?? ix.id)}`, r);
  }
  for (const c of list(d, "columns")) {
    if (typeof c.computed === "string") {
      const r = renameIdentifier(c.computed, from, to, { dialect });
      if (r.count) c.computed = r.text;
      note(`the expression of column ${String(c.name)}`, r);
    }
    if (c.defaultSql && typeof c.defaultSql === "object") {
      const r = renameInDialectMap(c.defaultSql, from, to, { dialect });
      if (r.map && r.count && !r.uncertain) c.defaultSql = r.map;
      note(`the default of column ${String(c.name)}`, r);
    }
  }
  return { rewritten, check };
}

/** Whether a view's output column names are fixed by its column list (CREATE VIEW v (a, b) AS …), not by its body. */
const namesItsColumns = (view: Json) => view.columnList === true && list(view, "columns").length > 0;

/** The routines and SQL objects whose text names `name`: places to check by hand, in words. */
export function textsNaming(docs: readonly Json[], name: string, dialect?: string): string[] {
  const out: string[] = [];
  for (const d of docs) {
    if (d.kind !== "routine" && d.kind !== "sql-object") continue;
    if (mapMentions(d.body, name, dialect)) out.push(`${d.kind === "routine" ? "routine" : "SQL object"} ${String(d.name ?? d.id)} (its text names ${name})`);
  }
  return out;
}

/**
 * The plan of renaming column `column` (an id) of `table` (its file) to `to`. `docs` are the database's views, queries, routines
 * and SQL objects and the documents whose bindings use the table; `tables` the database's resolved tables (which other tables
 * have a column of that name); `dialect` the database's (the dialect of a "*" text).
 */
export function planColumnRename(input: {
  table: Json;
  column: string;
  to: string;
  docs: readonly Json[];
  tables: readonly TableView[];
  dialect?: string;
}): RenamePlan {
  const { table, column, docs, tables, dialect } = input;
  const to = input.to.trim();
  const tableId = String(table.id ?? "");
  const tableName = String(table.name ?? "");
  const entry = list(table, "columns").find((c) => c.id === column);
  const from = String(entry?.name ?? "");
  const rewritten: string[] = [];
  const check: string[] = [];
  const bindings: string[] = [];
  const edits = new Map<string, (doc: Json) => void>();
  if (!entry || !to || from === to) return { rewritten, check, bindings, edits };

  // The table itself: the column, then what names it in its SQL texts.
  const own = renameInTable(JSON.parse(JSON.stringify(table)) as Json, column, from, to, dialect);
  rewritten.push(...own.rewritten);
  check.push(...own.check);
  edits.set(tableId, (d) => void renameInTable(d, column, from, to, dialect));

  // Views of the database that read the table: rewritten only where nothing else could be meant.
  const others = tables.filter((t) => t.key !== tableId && t.columns.some((c) => same(c.name, from)));
  for (const view of docs.filter((d) => d.kind === "view")) {
    const body = view.body as Record<string, unknown> | undefined;
    if (!mapMentions(body, tableName, dialect)) continue;
    const name = `view ${String(view.name ?? view.id)}`;
    const fixed = namesItsColumns(view);
    if (!mapMentions(body, from, dialect)) {
      // `SELECT *` returns the column under its new name: the view's column changes with it.
      if (!fixed && mapSelectsAll(body, dialect)) check.push(`${name}${because("star", from)}`);
      continue;
    }
    const alsoReads = others.filter((t) => mapMentions(body, t.name, dialect)).map((t) => t.name);
    if (alsoReads.length) {
      check.push(`${name} (it also reads ${alsoReads.join(", ")}, which ${alsoReads.length === 1 ? "has" : "have"} a column ${from})`);
      continue;
    }
    if (!fixed && mapSelectsAll(body, dialect)) {
      check.push(`${name}${because("star", from)}`);
      continue;
    }
    const options = { dialect, keepOutput: !fixed };
    const probe = renameInDialectMap(body, from, to, options);
    if (probe.uncertain) {
      check.push(`${name}${because(probe.why, from)}`);
      continue;
    }
    if (!probe.count) continue;
    rewritten.push(`${name} (${places(probe.count)})`);
    edits.set(String(view.id), (d) => {
      const r = renameInDialectMap(d.body, from, to, options);
      if (r.map && !r.uncertain) d.body = r.map;
    });
  }

  // Queries of the database that read the table: references by name (alias.column), and their SQL expressions.
  const target = queryTable(table, docs);
  for (const query of docs.filter((d) => d.kind === "query")) {
    const probe = renameInQuery(JSON.parse(JSON.stringify(query)) as Json, target, from, to, dialect ?? "postgresql");
    const name = `query ${String(query.name ?? query.id)}`;
    if (probe.check) check.push(`${name} (it may name ${from} where it cannot be told which source holds it)`);
    if (!probe.count) continue;
    rewritten.push(`${name} (${places(probe.count)})`);
    edits.set(String(query.id), (d) => void renameInQuery(d, target, from, to, dialect ?? "postgresql"));
  }

  // Routines and SQL objects: their text is checked by hand.
  check.push(...textsNaming(docs, from, dialect));

  // Storage bindings that name the column by its name (a binding may name it by id, which a rename leaves as it is).
  const renames = new Map([[from, to]]);
  for (const doc of docs.filter((d) => Array.isArray(d.bindings))) {
    const probe = JSON.parse(JSON.stringify(doc)) as Json;
    if (!renameBindingColumns(probe, tableId, renames)) continue;
    bindings.push(String(doc.id));
    edits.set(String(doc.id), (d) => void renameBindingColumns(d, tableId, renames));
  }
  return { rewritten, check, bindings, edits };
}

/**
 * A view's declared column renamed (its editor's Columns tab): when CREATE VIEW names no column list, the body's select list
 * says the name, so it follows (the alias renamed, or `AS` added); where the body does not say it plainly, it is to check.
 */
export function renameViewOutput(view: Json, from: string, to: string, dialect?: string): { body: Record<string, string> | null; check: boolean } {
  if (namesItsColumns(view) || !view.body) return { body: null, check: false };
  const r = renameOutputInDialectMap(view.body, from, to, dialect);
  return r.uncertain || !r.map ? { body: null, check: true } : { body: r.map, check: false };
}

/** A table renamed: the views, queries, routines and SQL objects that may still name it by its old name, to check by hand. */
export function planTableRename(input: { table: Json; from: string; docs: readonly Json[]; dialect?: string }): string[] {
  const { table, from, docs, dialect } = input;
  const sources = queryTable(table, docs).sources;
  const out: string[] = [];
  for (const d of docs) {
    const name = String(d.name ?? d.id);
    if (d.kind === "view" && mapMentions(d.body, from, dialect)) out.push(`view ${name}`);
    else if ((d.kind === "routine" || d.kind === "sql-object") && mapMentions(d.body, from, dialect))
      out.push(`${d.kind === "routine" ? "routine" : "SQL object"} ${name}`);
    else if (d.kind === "query" && queryNamesTable(d, sources, from, dialect)) out.push(`query ${name}`);
  }
  return out;
}

/** Whether a query names a table by its name: a source of it without an alias (its references say `name.column`), or SQL text. */
function queryNamesTable(query: Json, sources: ReadonlySet<string>, name: string, dialect?: string): boolean {
  let found = false;
  const visit = (node: unknown): void => {
    if (found || !node || typeof node !== "object") return;
    if (Array.isArray(node)) return node.forEach(visit);
    const obj = node as Json;
    if (typeof obj.source === "string" && sources.has(obj.source) && !obj.alias) found = true;
    if (obj.sql && mapMentions(obj.sql, name, dialect)) found = true;
    for (const v of Object.values(obj)) visit(v);
  };
  visit(query);
  return found;
}

/** A view's or query's output columns renamed in place (same position, another name): old name → new name. */
export function renamedOutputColumns(before: Json | null, after: Json): Map<string, string> {
  const out = new Map<string, string>();
  if (!before || before.kind !== after.kind) return out;
  const member = after.kind === "view" ? "columns" : after.kind === "query" ? "select" : null;
  if (!member) return out;
  const a = list(before, member);
  const b = list(after, member);
  if (a.length !== b.length) return out;
  a.forEach((x, i) => {
    const y = b[i];
    const same = member === "columns" || JSON.stringify(x.expression) === JSON.stringify(y.expression);
    if (same && typeof x.name === "string" && typeof y.name === "string" && x.name !== y.name) out.set(x.name, y.name);
  });
  return out;
}
