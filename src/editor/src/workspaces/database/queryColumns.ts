// A query's column references, as the engine resolves them (DatabaseRun.Queries.cs): each source of a query (its from and joins,
// and those of each nested query: a collection's query, an exists) has an alias, its own or else the source's name; a column
// reference is `alias.column` (the alias looked up from the innermost query outwards) or a bare column (the one source of the
// nearest query that has it), and its column part is the column's name (any case), its id or key, or an attribute id the
// column holds. Pure: what a table column's rename rewrites in a query and what its delete finds there; the opaque `sql`
// expressions per dialect are read as SQL text (sqlIdentifiers.ts).
import { mapMentions, mapMentionsColumn, renameInDialectMap } from "./sqlIdentifiers";

type Json = Record<string, unknown>;

const list = (doc: Json, member: string): Json[] => (Array.isArray(doc[member]) ? (doc[member] as Json[]) : []);
const loose = (s: string) => s.toLowerCase().replace(/_/g, "");

/** The table a query may read, and the ways a query names it and its columns. */
export interface QueryTable {
  /** The table's id (a table file) or its key (a table the model lays out). */
  id: string;
  name: string;
  database: string;
  /** What a query's source may name to mean the table: its id, its key, an entity bound to it (or laid out as it). */
  sources: ReadonlySet<string>;
  /** Attribute id → the column (id or name) it is bound to in the table, from the bindings that read or write the table. */
  attributes: ReadonlyMap<string, string>;
}

/** The ways a query names `table`: its id, the entities whose binding in its database reads or writes it, a laid-out key's entity. */
export function queryTable(table: Json, entities: readonly Json[]): QueryTable {
  const id = String(table.id ?? "");
  const database = String(table.database ?? "");
  const sources = new Set([id]);
  const at = id.indexOf("@");
  if (at > 0) sources.add(id.slice(0, at));
  const attributes = new Map<string, string>();
  for (const e of entities) {
    for (const b of list(e, "bindings")) {
      const write = b.write as Json | string | undefined;
      const reads = b.source === id || (typeof write === "object" && write?.table === id);
      if (!reads || (b.database && b.database !== database)) continue;
      if (b.source === id) sources.add(String(e.id));
      for (const f of list(b, "fields")) if (typeof f.attribute === "string" && typeof f.column === "string") attributes.set(f.attribute, f.column);
    }
  }
  return { id, name: String(table.name ?? id), database, sources, attributes };
}

interface Scope {
  aliases: Map<string, string>;
  sources: string[];
  outer: Scope | null;
}

/** One column reference of a query: the expression node, and whether it reads the table (true, false, or "maybe"). */
export interface ColumnRef {
  node: Json;
  /** The alias written before the column, or null. */
  alias: string | null;
  /** The column part (a name, an id, an attribute id). */
  column: string;
  ours: boolean | "maybe";
}

/** An opaque SQL expression of a query and whether the table is a source of its query (or one around it), alone or not. */
export interface SqlRef {
  node: Json;
  /** The table is among the sources in scope. */
  reads: boolean;
  /** The table is the only source in scope (its columns are the only ones the text may name without a qualifier). */
  alone: boolean;
}

/** Walks a query's column references and SQL expressions, each with the scope it resolves in. */
export function walkQuery(query: Json, table: QueryTable, visit: { column?: (ref: ColumnRef) => void; sql?: (ref: SqlRef) => void }): void {
  const ours = (source: string) => table.sources.has(source);
  const lookup = (scope: Scope | null, alias: string): string | undefined => {
    for (let s = scope; s; s = s.outer) {
      const hit = s.aliases.get(alias);
      if (hit !== undefined) return hit;
    }
    return undefined;
  };
  const inScope = (scope: Scope | null) => {
    const all: string[] = [];
    for (let s = scope; s; s = s.outer) all.push(...s.sources);
    return all;
  };
  const enter = (q: Json, outer: Scope | null): Scope => {
    const scope: Scope = { aliases: new Map(), sources: [], outer };
    for (const s of [q.from as Json | undefined, ...list(q, "joins")]) {
      if (!s || typeof s.source !== "string") continue;
      scope.sources.push(s.source);
      // The alias, else the source's name (known for the table; another source's own name cannot clash with it).
      const alias = typeof s.alias === "string" && s.alias ? s.alias : ours(s.source) ? table.name : null;
      if (alias) scope.aliases.set(alias, s.source);
    }
    return scope;
  };
  const visitNode = (node: unknown, scope: Scope): void => {
    if (Array.isArray(node)) {
      for (const n of node) visitNode(n, scope);
      return;
    }
    if (!node || typeof node !== "object") return;
    const obj = node as Json;
    if (obj.from && typeof obj.from === "object") {
      walk(obj, scope);
      return;
    }
    if (typeof obj.column === "string") {
      const text = obj.column;
      const dot = text.indexOf(".");
      const source = dot > 0 ? lookup(scope, text.slice(0, dot)) : undefined;
      if (source !== undefined) visit.column?.({ node: obj, alias: text.slice(0, dot), column: text.slice(dot + 1), ours: ours(source) });
      else {
        const sources = inScope(scope);
        const mine = sources.filter(ours).length;
        visit.column?.({ node: obj, alias: null, column: text, ours: !mine ? false : mine === sources.length ? true : "maybe" });
      }
    }
    if (obj.sql && typeof obj.sql === "object") {
      const sources = inScope(scope);
      const mine = sources.filter(ours).length;
      visit.sql?.({ node: obj, reads: mine > 0, alone: mine > 0 && mine === sources.length });
    }
    for (const [k, v] of Object.entries(obj)) if (k !== "sql" && v && typeof v === "object") visitNode(v, scope);
  };
  const walk = (q: Json, outer: Scope | null) => {
    const scope = enter(q, outer);
    for (const [k, v] of Object.entries(q)) {
      if (k === "from" || k === "parameters" || k === "paging" || !v || typeof v !== "object") continue;
      visitNode(v, scope);
    }
  };
  walk(query, null);
}

/** Whether a reference's column part names column `id` (named `name`) of the table: its name in any case, its id, or a bound attribute. */
export function namesColumn(table: QueryTable, column: string, id: string, name: string): boolean {
  if (column === id || loose(column) === loose(name)) return true;
  const bound = table.attributes.get(column.split(".")[0]);
  return bound !== undefined && (bound === id || loose(bound) === loose(name));
}

/** What a column's rename does to a query: the references rewritten, and whether some are left to check. */
export interface QueryRename {
  count: number;
  /** References or SQL texts left as they are (a bare name the query's other sources may have, an uncertain SQL text). */
  check: boolean;
}

/** Renames column `from` of the table to `to` in a query's references by name and its SQL expressions; mutates the query. */
export function renameInQuery(query: Json, table: QueryTable, from: string, to: string, dialect: string): QueryRename {
  let count = 0;
  let check = false;
  walkQuery(query, table, {
    column: (ref) => {
      // By name only: an id or an attribute id still names the column after the rename.
      if (ref.ours === false || loose(ref.column) !== loose(from)) return;
      if (ref.ours === "maybe") {
        check = true;
        return;
      }
      ref.node.column = ref.alias === null ? to : `${ref.alias}.${to}`;
      count++;
    },
    sql: (ref) => {
      if (!ref.reads || !mapMentions(ref.node.sql, from, dialect)) return;
      if (!ref.alone) {
        if (mapMentionsColumn(ref.node.sql, from, dialect)) check = true;
        return;
      }
      const r = renameInDialectMap(ref.node.sql, from, to, { dialect });
      if (r.uncertain) check = true;
      else if (r.map && r.count) {
        ref.node.sql = r.map;
        count += r.count;
      }
    },
  });
  return { count, check };
}

/** How a query uses a column the delete removes: references that resolve to it, and SQL texts that may name it. */
export function queryReadsColumn(query: Json, table: QueryTable, id: string, name: string, dialect: string): { reads: number; maybe: boolean; sql: boolean } {
  let reads = 0;
  let maybe = false;
  let sql = false;
  walkQuery(query, table, {
    column: (ref) => {
      if (ref.ours === false || !namesColumn(table, ref.column, id, name)) return;
      if (ref.ours === true) reads++;
      else maybe = true;
    },
    sql: (ref) => {
      if (ref.reads && mapMentionsColumn(ref.node.sql, name, dialect)) sql = true;
    },
  });
  return { reads, maybe, sql };
}
