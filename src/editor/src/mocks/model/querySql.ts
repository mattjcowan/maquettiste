// The mock's port of the engine's query SQL renderer (src/Maquettiste.Engine/Rendering/QuerySql.cs, engine-design.md 7
// "Queries"): a resolved query (queries.ts binds it) written as parameterised SQL for a dialect, one clause per line;
// identifiers quoted by the database's quoting setting (SqlDialects.Quote with the engine's reserved-word lists, which
// scripts/copy-fixture.mjs copies into src/mocks/fixture/reserved/), functions spelled per dialect, ilike lowered where the
// dialect has none, NULLS FIRST/LAST emulated, paging as LIMIT/OFFSET or OFFSET … FETCH NEXT, and each collection as a statement
// of its own keyed by the parent rows. The output is byte for byte the engine's for the same resolved query.
import type { Diagnostic } from "@/api/types";
import { nativeType } from "./physical";

type Json = Record<string, unknown>;

/** The dialect names the renderer writes (DialectTypeMaps.Name). */
export const QUERY_DIALECTS = ["postgresql", "sqlserver", "mysql", "sqlite", "oracle"] as const;
export type QueryDialect = (typeof QUERY_DIALECTS)[number];

/** A dialect name as templates write it (the database's `dialect` plus the common aliases), or null (SqlDialects.TryParse). */
export function parseDialect(name: string | null | undefined): QueryDialect | null {
  switch (name?.trim().toLowerCase()) {
    case "postgresql":
    case "postgres":
    case "pg":
    case "pgsql":
      return "postgresql";
    case "sqlserver":
    case "mssql":
    case "tsql":
      return "sqlserver";
    case "mysql":
    case "mariadb":
      return "mysql";
    case "sqlite":
      return "sqlite";
    case "oracle":
      return "oracle";
    default:
      return null;
  }
}

const reservedFiles = import.meta.glob("../fixture/reserved/*.txt", { query: "?raw", import: "default", eager: true }) as Record<string, string>;
const RESERVED = new Map<string, Set<string>>(
  Object.entries(reservedFiles).map(([path, text]) => [
    /reserved-([a-z]+)\.txt$/.exec(path)?.[1] ?? "",
    new Set(
      text
        .split(/\r?\n/)
        .map((w) => w.trim().toLowerCase())
        .filter(Boolean),
    ),
  ]),
);

/** Whether a name is a reserved word of the dialect, case-insensitively. */
export function isReserved(dialect: QueryDialect, name: string): boolean {
  return RESERVED.get(dialect)?.has(name.toLowerCase()) ?? false;
}

const REGULAR = /^[A-Za-z_][A-Za-z0-9_]*$/;

/**
 * SqlDialects.Quote: `always` quotes, `never` does not, `reserved` quotes reserved words, irregular names and names that start with
 * an underscore (Oracle's unquoted identifiers start with a letter).
 */
export function quoteIdentifier(name: string, dialect: QueryDialect, quoting: string): string {
  const mode = ["always", "reserved", "never"].includes(quoting.trim().toLowerCase()) ? quoting.trim().toLowerCase() : "reserved";
  const quote = mode === "always" ? true : mode === "never" ? false : !REGULAR.test(name) || name.startsWith("_") || isReserved(dialect, name);
  if (!quote) return name;
  if (dialect === "sqlserver") return `[${name.replaceAll("]", "]]")}]`;
  if (dialect === "mysql") return "`" + name.replaceAll("`", "``") + "`";
  return `"${name.replaceAll('"', '""')}"`;
}

/** SqlDialects.Literal for the values a query's JSON holds: strings, numbers, booleans, null. */
export function sqlLiteral(value: unknown, dialect: QueryDialect): string {
  if (value === null || value === undefined) return "NULL";
  if (typeof value === "string") {
    let escaped = value.replaceAll("'", "''");
    if (dialect === "mysql") escaped = escaped.replaceAll("\\", "\\\\");
    return (dialect === "sqlserver" ? "N'" : "'") + escaped + "'";
  }
  if (typeof value === "boolean") return dialect === "postgresql" ? (value ? "true" : "false") : value ? "1" : "0";
  if (typeof value === "number") return String(value);
  throw new Error(`A value of type '${typeof value}' has no SQL literal form.`);
}

// ------------------------------------------------------------------ the resolved query (RQuery and its parts)

export interface RDatabaseRef {
  id: string;
  name: string;
  dialect: string;
  quoting: string;
}

export interface RParameter {
  name: string;
  type: string | null;
  dbTypeId: string | null;
  dbTypeName: string | null;
  length: number | null;
  precision: number | null;
  scale: number | null;
  nativeType: string;
  codeType: string;
  collection: boolean;
  default: unknown;
  description: string | null;
}

export interface RSource {
  alias: string;
  /** from, inner, left, right, full or cross. */
  joinKind: string;
  source: string;
  tableKey: string | null;
  viewId: string | null;
  name: string;
  schema: string | null;
  on: RPredicate | null;
  optional: boolean;
}

export interface RWhen {
  when: RPredicate;
  then: RExpression;
}

export interface RExpression {
  node: "column" | "param" | "value" | "null" | "op" | "call" | "case" | "cast" | "sql";
  column?: string;
  alias?: string;
  columnName?: string;
  source?: RSource;
  isOuter?: boolean;
  param?: RParameter;
  value?: unknown;
  /** A typed decimal literal's text as SQL writes it (`{ "value": "2.0", "type": "decimal" }`). */
  literalText?: string;
  op?: string;
  call?: string;
  routine?: { id: string; name: string; schema: string | null };
  args: RExpression[];
  case: RWhen[];
  else?: RExpression;
  cast?: RExpression;
  sql: Record<string, string>;
  sqlText?: string | null;
  type: string | null;
  nativeType: string | null;
  codeType: string | null;
  nullable: boolean;
}

export interface RPredicate {
  node: "and" | "or" | "not" | "compare" | "exists";
  and: RPredicate[];
  or: RPredicate[];
  not?: RPredicate;
  op?: string;
  left?: RExpression;
  right?: RExpression;
  values: RExpression[];
  exists?: RQuery;
}

export interface RField {
  name: string;
  /** The attribute it fills (a value object member's attribute), or null. */
  attributeId: string | null;
  /** The value object member it fills (the file writes `attributeId.memberId`), or null. */
  memberId: string | null;
  /** The relation end a to-one navigation leads to, whose foreign key it fills, or null. */
  endId: string | null;
  expression: RExpression;
  type: string | null;
  nativeType: string | null;
  codeType: string | null;
  nullable: boolean;
}

export interface ROrder {
  expression: RExpression;
  direction: string;
  nulls: string | null;
}

export interface RPaging {
  offsetParameter: RParameter | null;
  offset: number | null;
  limitParameter: RParameter | null;
  limit: number | null;
}

export interface RKey {
  outer: RExpression;
  inner: RExpression;
  parentField: string;
  hidden: boolean;
  childField: string;
  parameter: string;
  type: string | null;
  nativeType: string | null;
  codeType: string | null;
}

export interface RCollection {
  name: string;
  attribute: string;
  entityId: string | null;
  query: RQuery;
  keys: RKey[];
  index: number;
  keyConjuncts: Set<RPredicate>;
  /** The collection as the file writes it. */
  definition: Json;
}

export interface RQuery {
  id: string;
  name: string;
  database: RDatabaseRef;
  entityId: string | null;
  parameters: RParameter[];
  from: RSource;
  joins: RSource[];
  select: RField[];
  where: RPredicate | null;
  groupBy: RExpression[];
  having: RPredicate | null;
  orderBy: ROrder[];
  distinct: boolean;
  paging: RPaging | null;
  collections: RCollection[];
  parent: RQuery | null;
  /** The SQL for the database's dialect, "" when the query has errors (the top query only). */
  sql: string;
  /** The ids (a synthesized table's key) of the tables, views, routines and database types it reads (the top query only). */
  uses: string[];
}

// ------------------------------------------------------------------ rendering

export interface QuerySqlOptions {
  /** `@` (`@name`, the default), `:` (`:name`) or `$` (`$1`, numbered by first appearance). */
  placeholder?: string;
  /** `expand` (`x IN @ids`, the default) or `any` (`x = ANY(@ids)` on PostgreSQL). */
  lists?: string;
}

/** A rendered statement: the SQL, the parameters in first-appearance order, and what could not be rendered (MQ4029). */
export interface QuerySqlText {
  sql: string;
  parameters: string[];
  diagnostics: Diagnostic[];
}

/** The engine's spelling of the functions it knows, per dialect; null for any other name (written as given). */
export function functionName(name: string, dialect: QueryDialect): string | null {
  switch (name.toLowerCase()) {
    case "lower":
      return "LOWER";
    case "upper":
      return "UPPER";
    case "coalesce":
      return "COALESCE";
    case "count":
      return "COUNT";
    case "sum":
      return "SUM";
    case "min":
      return "MIN";
    case "max":
      return "MAX";
    case "avg":
      return "AVG";
    case "length":
      return dialect === "sqlserver" ? "LEN" : dialect === "mysql" ? "CHAR_LENGTH" : "LENGTH";
    case "now":
      return "CURRENT_TIMESTAMP";
    default:
      return null;
  }
}

/** The aggregate functions (QuerySql.Aggregates): a query whose select list, having or order calls one is grouped. */
const AGGREGATES = new Set([
  "COUNT",
  "COUNT_BIG",
  "SUM",
  "MIN",
  "MAX",
  "AVG",
  "STRING_AGG",
  "ARRAY_AGG",
  "GROUP_CONCAT",
  "LISTAGG",
  "JSON_AGG",
  "JSONB_AGG",
  "JSON_ARRAYAGG",
  "JSON_OBJECTAGG",
  "JSON_GROUP_ARRAY",
  "JSON_GROUP_OBJECT",
  "BOOL_AND",
  "BOOL_OR",
  "EVERY",
  "BIT_AND",
  "BIT_OR",
  "BIT_XOR",
  "STDDEV",
  "STDDEV_POP",
  "STDDEV_SAMP",
  "STDEV",
  "STDEVP",
  "VARIANCE",
  "VAR_POP",
  "VAR_SAMP",
  "VAR",
  "VARP",
  "ANY_VALUE",
]);

/** Whether an expression calls an aggregate function (outside nested queries). */
export function hasAggregate(e: RExpression): boolean {
  return (
    (e.node === "call" && !e.routine && !!e.call && AGGREGATES.has(e.call.toUpperCase())) ||
    e.args.some(hasAggregate) ||
    (!!e.cast && hasAggregate(e.cast)) ||
    e.case.some((w) => predicateHasAggregate(w.when) || hasAggregate(w.then)) ||
    (!!e.else && hasAggregate(e.else))
  );
}

function predicateHasAggregate(p: RPredicate): boolean {
  return (
    p.and.some(predicateHasAggregate) ||
    p.or.some(predicateHasAggregate) ||
    (!!p.not && predicateHasAggregate(p.not)) ||
    (!!p.left && hasAggregate(p.left)) ||
    (!!p.right && hasAggregate(p.right)) ||
    p.values.some(hasAggregate)
  );
}

/** Whether a query is grouped: a GROUP BY, or an aggregate in its select list, having or order. */
export function isGrouped(q: RQuery): boolean {
  return (
    q.groupBy.length > 0 ||
    q.select.some((f) => hasAggregate(f.expression)) ||
    (!!q.having && predicateHasAggregate(q.having)) ||
    q.orderBy.some((o) => hasAggregate(o.expression))
  );
}

/** QuerySql.MySqlCastType: MySQL casts only to CHAR, SIGNED, UNSIGNED, DECIMAL(p,s), DATE, DATETIME, TIME, BINARY, JSON, DOUBLE. */
export function mysqlCastType(keyword: string, decimalType: string): string {
  switch (keyword) {
    case "int16":
    case "int32":
    case "int64":
    case "bool":
    case "duration":
      return "SIGNED";
    case "binary":
      return "BINARY";
    case "decimal":
      return decimalType.toUpperCase();
    case "date":
      return "DATE";
    case "datetime":
    case "datetimeOffset":
      return "DATETIME";
    case "time":
      return "TIME";
    case "json":
      return "JSON";
    case "float":
    case "double":
      return "DOUBLE";
    default:
      return "CHAR";
  }
}

/** QuerySql.SqlFor: an sql expression's text for a dialect, else its "*" text; a blank text counts as none. */
export function sqlFor(texts: Readonly<Record<string, string>>, dialect: string): string | null {
  const own = texts[dialect];
  if (own !== undefined && own.trim() !== "") return own;
  const any = texts["*"];
  return any !== undefined && any.trim() !== "" ? any : null;
}

/** One expression as the query's statement writes it for its database's dialect (QuerySql.ExpressionText). */
export function expressionText(query: RQuery, expression: RExpression): string {
  return new Writer(query, null, null).text(expression);
}

/** Renders a query (its collections excluded, their hidden key columns included). Throws on an unknown dialect or option. */
export function renderQuery(query: RQuery, dialect?: string | null, options?: QuerySqlOptions | null): QuerySqlText {
  const writer = new Writer(query, dialect ?? null, options ?? null);
  writer.top(query);
  return writer.result();
}

/** Renders the statement of one collection: its rows for every parent row at once, matched by the key columns. */
export function renderCollection(collection: RCollection, dialect?: string | null, options?: QuerySqlOptions | null): QuerySqlText {
  const writer = new Writer(collection.query, dialect ?? null, options ?? null);
  writer.collection(collection);
  return writer.result();
}

export function rootOf(query: RQuery): RQuery {
  let q = query;
  while (q.parent) q = q.parent;
  return q;
}

class Writer {
  private readonly root: RQuery;
  private readonly dialect: QueryDialect;
  private readonly quoting: string;
  private readonly placeholder: string;
  private readonly lists: string;
  private readonly parameters: string[] = [];
  private readonly diagnostics: Diagnostic[] = [];
  private sb = "";
  private newline = "\n";

  constructor(query: RQuery, dialect: string | null, options: QuerySqlOptions | null) {
    this.root = rootOf(query);
    const name = dialect ?? this.root.database.dialect;
    const parsed = parseDialect(name);
    if (!parsed) throw new Error(`'${name}' is not a dialect (postgresql, sqlserver, mysql, sqlite, oracle).`);
    this.dialect = parsed;
    this.quoting = this.root.database.quoting;
    this.placeholder = options?.placeholder ?? "@";
    this.lists = options?.lists ?? "expand";
    if (!["@", ":", "$"].includes(this.placeholder)) throw new Error(`The placeholder style '${this.placeholder}' is not @, : or $.`);
    if (!["expand", "any"].includes(this.lists)) throw new Error(`The list style '${this.lists}' is not expand or any.`);
  }

  result(): QuerySqlText {
    return { sql: this.sb, parameters: [...this.parameters], diagnostics: [...this.diagnostics] };
  }

  text(expression: RExpression): string {
    return this.expression(expression);
  }

  top(query: RQuery): void {
    const columns = query.select.map((f) => this.expression(f.expression) + " AS " + this.q(f.name));
    const hidden = new Set<string>();
    const hiddenKeys: RExpression[] = [];
    for (const collection of query.collections)
      for (const key of collection.keys)
        if (key.hidden && !hidden.has(key.parentField)) {
          hidden.add(key.parentField);
          columns.push(this.expression(key.outer) + " AS " + this.q(key.parentField));
          hiddenKeys.push(key.outer);
        }
    this.select(query.distinct, columns);
    // A grouped parent groups by its hidden key columns too, or they would be neither grouped nor aggregated.
    this.body(query, query.where, null, isGrouped(query) ? hiddenKeys : []);
    this.order(query.orderBy, query.paging !== null);
    this.paging(query.paging);
  }

  collection(collection: RCollection): void {
    const query = collection.query;
    const columns = query.select.map((f) => this.expression(f.expression) + " AS " + this.q(f.name));
    for (const key of collection.keys) columns.push(this.expression(key.inner) + " AS " + this.q(key.childField));
    this.select(query.distinct, columns);
    // A grouped collection groups by its key columns too: each group then belongs to one parent row.
    this.body(query, query.where, collection, isGrouped(query) ? collection.keys.map((k) => k.inner) : []);
    this.order(query.orderBy, false);
  }

  private select(distinct: boolean, columns: string[]): void {
    this.sb += (distinct ? "SELECT DISTINCT " : "SELECT ") + columns.join(", ");
  }

  /** FROM, the joins, WHERE, GROUP BY (with `keys`, the key columns a grouped statement adds, each once) and HAVING. */
  private body(query: RQuery, where: RPredicate | null, collection: RCollection | null, keys: readonly RExpression[]): void {
    this.sb += this.newline + "FROM " + this.source(query.from);
    for (const join of query.joins) {
      const keyword = { left: "LEFT JOIN ", right: "RIGHT JOIN ", full: "FULL JOIN ", cross: "CROSS JOIN " }[join.joinKind] ?? "INNER JOIN ";
      this.sb += this.newline + keyword + this.source(join);
      if (join.joinKind !== "cross" && join.on) this.sb += " ON " + this.predicate(join.on, true);
    }
    const conditions: string[] = [];
    if (where) {
      if (collection) {
        for (const conjunct of where.node === "and" ? where.and : [where])
          if (!collection.keyConjuncts.has(conjunct)) conditions.push(this.predicate(conjunct, false));
      } else conditions.push(this.predicate(where, true));
    }
    if (collection) for (const key of collection.keys) conditions.push(this.inList(this.expression(key.inner), key.parameter, false));
    if (conditions.length) this.sb += this.newline + "WHERE " + conditions.join(" AND ");
    const groups = query.groupBy.map((g) => this.expression(g));
    for (const key of keys) {
      const text = this.expression(key);
      if (!groups.includes(text)) groups.push(text);
    }
    if (groups.length) this.sb += this.newline + "GROUP BY " + groups.join(", ");
    if (query.having) this.sb += this.newline + "HAVING " + this.predicate(query.having, true);
  }

  private order(orderBy: readonly ROrder[], paged: boolean): void {
    const terms: string[] = [];
    for (const order of orderBy) {
      const expression = this.expression(order.expression);
      const direction = order.direction === "desc" ? " DESC" : " ASC";
      if (order.nulls) {
        if (this.dialect === "postgresql" || this.dialect === "oracle" || this.dialect === "sqlite") {
          terms.push(expression + direction + (order.nulls === "first" ? " NULLS FIRST" : " NULLS LAST"));
          continue;
        }
        // No NULLS FIRST/LAST: sort on whether the value is null first.
        terms.push("CASE WHEN " + expression + " IS NULL THEN " + (order.nulls === "first" ? "0 ELSE 1" : "1 ELSE 0") + " END");
      }
      terms.push(expression + direction);
    }
    if (terms.length === 0 && paged && this.dialect === "sqlserver") terms.push("(SELECT NULL)"); // OFFSET … FETCH needs an ORDER BY
    if (terms.length) this.sb += "\nORDER BY " + terms.join(", ");
  }

  private paging(paging: RPaging | null): void {
    if (!paging) return;
    const offset = paging.offsetParameter ? this.placeholderOf(paging.offsetParameter.name) : paging.offset !== null ? String(paging.offset) : null;
    const limit = paging.limitParameter ? this.placeholderOf(paging.limitParameter.name) : paging.limit !== null ? String(paging.limit) : null;
    if (offset === null && limit === null) return;
    switch (this.dialect) {
      case "sqlserver":
        this.sb += "\nOFFSET " + (offset ?? "0") + " ROWS";
        if (limit !== null) this.sb += " FETCH NEXT " + limit + " ROWS ONLY";
        break;
      case "oracle":
        if (offset !== null) this.sb += "\nOFFSET " + offset + " ROWS";
        if (limit !== null) this.sb += "\nFETCH NEXT " + limit + " ROWS ONLY";
        break;
      default:
        if (limit !== null) this.sb += "\nLIMIT " + limit;
        else if (this.dialect === "sqlite") this.sb += "\nLIMIT -1";
        else if (this.dialect === "mysql") this.sb += "\nLIMIT 18446744073709551615";
        if (offset !== null) this.sb += (limit === null && this.dialect === "postgresql" ? "\n" : " ") + "OFFSET " + offset;
        break;
    }
  }

  private source(source: RSource | { name: string; schema: string | null; alias?: string }): string {
    const name = source.schema && this.dialect !== "sqlite" ? this.q(source.schema) + "." + this.q(source.name) : this.q(source.name);
    return "alias" in source && source.alias !== undefined ? name + " " + this.q(source.alias) : name;
  }

  private q(name: string): string {
    return quoteIdentifier(name, this.dialect, this.quoting);
  }

  private placeholderOf(name: string): string {
    let index = this.parameters.indexOf(name);
    if (index < 0) {
      this.parameters.push(name);
      index = this.parameters.length - 1;
    }
    return this.placeholder === "$" ? "$" + String(index + 1) : this.placeholder === ":" ? ":" + name : "@" + name;
  }

  private inList(left: string, parameter: string, negate: boolean): string {
    if (this.lists === "any" && this.dialect === "postgresql") return left + (negate ? " <> ALL(" : " = ANY(") + this.placeholderOf(parameter) + ")";
    return left + (negate ? " NOT IN " : " IN ") + this.placeholderOf(parameter);
  }

  private predicate(predicate: RPredicate, top: boolean): string {
    switch (predicate.node) {
      case "and":
      case "or": {
        const items = predicate.node === "and" ? predicate.and : predicate.or;
        const text = items.map((p) => this.predicate(p, false)).join(predicate.node === "and" ? " AND " : " OR ");
        return top || items.length === 1 ? text : "(" + text + ")";
      }
      case "not":
        return "NOT (" + this.predicate(predicate.not!, true) + ")";
      case "exists":
        return "EXISTS (" + this.subquery(predicate.exists!) + ")";
      default:
        return this.compare(predicate);
    }
  }

  private compare(predicate: RPredicate): string {
    const left = this.expression(predicate.left!);
    const op = predicate.op ?? "eq";
    switch (op) {
      case "isNull":
        return left + " IS NULL";
      case "isNotNull":
        return left + " IS NOT NULL";
      case "in":
      case "notIn":
        if (predicate.right?.node === "param" && predicate.right.param?.collection) return this.inList(left, predicate.right.param.name, op === "notIn");
        return left + (op === "notIn" ? " NOT IN (" : " IN (") + predicate.values.map((v) => this.expression(v)).join(", ") + ")";
      case "between":
        return left + " BETWEEN " + this.expression(predicate.values[0]) + " AND " + this.expression(predicate.values[1]);
      case "ilike":
        if (this.dialect !== "postgresql") return "LOWER(" + left + ") LIKE LOWER(" + this.expression(predicate.right!) + ")";
        break;
    }
    const symbol = { eq: " = ", ne: " <> ", lt: " < ", le: " <= ", gt: " > ", ge: " >= ", like: " LIKE ", ilike: " ILIKE " }[op as "eq"] ?? " " + op + " ";
    return left + symbol + this.expression(predicate.right!);
  }

  /**
   * A nested query inline (an exists condition): SELECT 1 and its body, each clause on a line of its own indented one level deeper
   * than the statement around it (the text of literals and sql expressions is written as is).
   */
  private subquery(query: RQuery): string {
    const saved = this.sb;
    const newline = this.newline;
    this.sb = "";
    this.newline = newline + "    ";
    this.select(false, ["1"]);
    this.body(query, query.where, null, []);
    const text = this.sb;
    this.newline = newline;
    this.sb = saved;
    return text;
  }

  private expression(expression: RExpression): string {
    switch (expression.node) {
      case "column":
        return this.q(expression.alias!) + "." + this.q(expression.columnName!);
      case "param":
        return this.placeholderOf(expression.param!.name);
      case "value":
        return expression.literalText ?? sqlLiteral(expression.value, this.dialect);
      case "null":
        return "NULL";
      case "op":
        return this.operation(expression);
      case "call":
        return this.call(expression);
      case "case": {
        let text = "CASE";
        for (const when of expression.case) text += " WHEN " + this.predicate(when.when, true) + " THEN " + this.expression(when.then);
        if (expression.else) text += " ELSE " + this.expression(expression.else);
        return text + " END";
      }
      case "cast":
        return "CAST(" + this.expression(expression.cast!) + " AS " + this.castType(expression) + ")";
      case "sql": {
        const text = sqlFor(expression.sql, this.dialect);
        if (text !== null) return text;
        this.diagnostics.push({
          rule: "MQ4029",
          severity: "error",
          message: `Query '${this.root.name}' has an sql expression without a text for ${this.dialect} (and no "*" text), so it cannot be rendered there.`,
          elementId: this.root.id,
          filePath: null,
          jsonPointer: null,
          line: null,
          column: null,
        });
        return "NULL";
      }
      default:
        return "NULL";
    }
  }

  private operation(expression: RExpression): string {
    const args = expression.args.map((a) => this.expression(a));
    if (args.length === 1) return "(" + (expression.op === "-" ? "-" : "") + args[0] + ")";
    if (expression.op === "concat")
      return this.dialect === "sqlserver" || this.dialect === "mysql" ? "CONCAT(" + args.join(", ") + ")" : "(" + args.join(" || ") + ")";
    if (expression.op === "%" && this.dialect === "oracle") return args.slice(1).reduce((left, right) => "MOD(" + left + ", " + right + ")", args[0]);
    return "(" + args.join(" " + expression.op + " ") + ")";
  }

  private call(expression: RExpression): string {
    const args = expression.args.map((a) => this.expression(a));
    if (expression.routine) return this.source({ name: expression.routine.name, schema: expression.routine.schema }) + "(" + args.join(", ") + ")";
    const call = expression.call!;
    const spelled = functionName(call, this.dialect);
    if (spelled === "CURRENT_TIMESTAMP") return spelled;
    if (spelled === "COUNT" && args.length === 0) return "COUNT(*)";
    return (spelled ?? call) + "(" + args.join(", ") + ")";
  }

  private castType(expression: RExpression): string {
    const keyword = expression.type ?? "string";
    if (this.dialect === this.root.database.dialect && expression.nativeType) return expression.nativeType;
    if (this.dialect === "mysql") return mysqlCastType(keyword, nativeType("mysql", "decimal", {}));
    return nativeType(this.dialect, keyword, {});
  }
}
