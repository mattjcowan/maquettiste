// The mock's query binding: a port of the engine's DatabaseRun.Queries.cs (engine-design.md 7, "Queries") over a resolved
// DatabaseView (the mock's own resolveDatabase output, or the engine's recorded view) and the model's documents. Each query file
// of the database binds its sources to the view's tables and views, its column references to their columns (by physical name,
// column key or attribute id, then loosely), its parameters to native types, its trees to RExpression and RPredicate; what a file
// gets wrong is reported with the engine's rule, message and JSON pointer (MQ3001, MQ4018, MQ4021 to MQ4043). A query names no
// entity: its rows have the shape of its select list. The queries project to the API's QueryView as QueryViews.cs does, and
// `preview` answers GET /api/model/queries/{id}/sql's statements (QueryViews.Preview).
import type { components } from "@/api/schema";
import type { DatabaseView, Diagnostic } from "@/api/types";
import { BUILTIN_TYPES } from "@/model/model";
import { scanSqlText, sqlTextMentions, sqlTextNamesNoParameter, sqlTextPosition, type SqlDialect } from "@/model/sqlText";
import { annotationsOf, nativeType } from "./physical";
import {
  expressionText,
  isGrouped,
  mysqlCastType,
  sqlTextMissing,
  parseDialect,
  renderQuery,
  sqlFor,
  type QuerySqlOptions,
  type RExpression,
  type RField,
  type RNamedQuery,
  type RParameter,
  type RPredicate,
  type RQuery,
  type RSource,
  type RWhen,
} from "./querySql";
import validationRules from "../recorded/validation-rules.json";
import { removedQueryKeys } from "./removedQueryKeys";

type Json = Record<string, unknown>;
type S = components["schemas"];
export type QueryView = S["QueryView"];
export type QuerySqlPreview = S["QuerySqlPreview"];
type QueryFieldView = S["QueryFieldView"];
type TableView = DatabaseView["tables"][number];
type ColumnView = TableView["columns"][number];
type ViewView = DatabaseView["views"][number];
type ViewColumn = ViewView["columns"][number];
type RoutineView = NonNullable<DatabaseView["routines"]>[number];
type DatabaseTypeView = NonNullable<DatabaseView["types"]>[number];

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const ID = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/;
/** IdFormat.IsValid. */
const isId = (value: string) => ID.test(value);
const INTEGER_TYPES = ["int16", "int32", "int64"];
const SEVERITY = new Map((validationRules as { id: string; defaultSeverity: string }[]).map((r) => [r.id, r.defaultSeverity as Diagnostic["severity"]]));
/** The findings that leave a query renderable; every other one leaves a node unresolved or the SQL wrong, so the query has no SQL. */
const RENDERABLE = new Set(["MQ3001", "MQ4030", "MQ4037", "MQ4040", "MQ4096", "MQ4097", "MQ4098"]);
/** The verbs that change data, and the ones that return rows (MQ4098; sql-queries-design.md decision 6). */
const WRITE_VERBS = new Set(["INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "TRUNCATE"]);
const ROW_VERBS = new Set(["SELECT", "VALUES", "TABLE"]);
/** A function name a call may write (MQ4034). */
const FUNCTION_NAME = /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$/;
/** The text of a typed decimal literal (MQ4043). */
const DECIMAL = /^-?[0-9]+(\.[0-9]+)?$/;

/** Casing.Words: split on non-alphanumerics and at lower→upper, digit→upper and acronym→word boundaries; lowercase words. */
function casingWords(name: string): string[] {
  const words: string[] = [];
  let current = "";
  const letterOrDigit = (c: string) => /[\p{L}\p{N}]/u.test(c);
  const upper = (c: string) => /\p{Lu}/u.test(c);
  const lower = (c: string) => /\p{Ll}/u.test(c);
  const digit = (c: string) => /\p{Nd}/u.test(c);
  const chars = [...name];
  chars.forEach((c, i) => {
    if (!letterOrDigit(c)) {
      if (current) words.push(current);
      current = "";
      return;
    }
    if (current && upper(c)) {
      const previous = chars[i - 1];
      const nextIsLower = i + 1 < chars.length && lower(chars[i + 1]);
      if (lower(previous) || digit(previous) || (upper(previous) && nextIsLower)) {
        words.push(current);
        current = "";
      }
    }
    current += c.toLowerCase();
  });
  if (current) words.push(current);
  return words;
}

const capitalize = (w: string) => (w ? w.slice(0, 1).toUpperCase() + w.slice(1) : w);
/** Casing.Pascal. */
export const enginePascal = (name: string) => casingWords(name).map(capitalize).join("");

/** DatabaseRun.QueryClassName: Pascal-cased, with Q in front when it would start with a digit or be empty. */
export function queryClassName(name: string): string {
  const pascal = enginePascal(name);
  return pascal === "" || /^[0-9]/.test(pascal) ? "Q" + pascal : pascal;
}

/** DatabaseRun.DecimalText: a decimal number's text as SQL writes it. */
function decimalText(text: string): string {
  const negative = text.startsWith("-");
  const digits = negative ? text.slice(1) : text;
  const dot = digits.indexOf(".");
  const whole = (dot < 0 ? digits : digits.slice(0, dot)).replace(/^0+/, "");
  const fraction = dot < 0 ? "" : digits.slice(dot);
  const result = (whole === "" ? "0" : whole) + fraction;
  return negative && /[1-9]/.test(result) ? "-" + result : result;
}

// The messages of the SQL text rules, in one place (sql-queries-design.md section 9; the engine's catalog is the model).
const words = (keys: string[]) => (keys.length === 1 ? keys[0] : `${keys.slice(0, -1).join(", ")} and ${keys[keys.length - 1]}`);
const sqlTextParameterUnused = (query: string, name: string, dialects: string[] | null) =>
  dialects === null
    ? `Parameter '${name}' of query '${query}' appears as :${name} in none of its texts.`
    : `Parameter '${name}' of query '${query}' does not appear as :${name} in its ${words(dialects)} ${dialects.length === 1 ? "text" : "texts"}.`;
const sqlTextNamesNothing = (query: string, field: "reads" | "writes", kind: string, name: string) =>
  `Query '${query}' ${field} ${kind} '${name}', whose name appears in none of its texts: was it renamed, or declared by mistake?`;
const article = (verb: string) => (verb === "INSERT" || verb === "UPDATE" ? "an" : "a");
const sqlTextWritesNothing = (key: string, query: string, verb: string, afterWith: boolean) =>
  `The ${key} text of query '${query}' is ${article(verb)} ${verb} statement${afterWith ? " (after its WITH)" : ""}, but the query declares no table it writes: list them in writes.`;
const sqlTextHasNoColumns = (key: string, query: string, verb: string, afterWith: boolean) =>
  `The ${key} text of query '${query}' is ${article(verb)} ${verb} statement${afterWith ? " (after its WITH)" : ""}, but the query declares no columns, so it would be generated as a command that returns the number of rows changed: declare its columns.`;

const isReservedName = (name: string) => name.toLowerCase().startsWith("mq_");

/** The sources, by alias, a query (or nested query) declares, with the scope around it. */
interface Scope {
  query: RQuery;
  outer: Scope | null;
  aliases: Map<string, RSource>;
  sources: RSource[];
}

interface Found {
  table?: TableView;
  view?: ViewView;
  named?: RNamedQuery;
}

/** Per source: what it resolved to (the renderer only needs names; the binder needs the columns). */
const sourceTargets = new WeakMap<RSource, Found>();

/** One database's queries, resolved. */
export interface ResolvedQueries {
  /** Each query projected as QueryView, by name then id. */
  queries: QueryView[];
  /** What the queries get wrong (MQ3001, MQ4018, MQ4021 to MQ4043), with their pointers. */
  diagnostics: Diagnostic[];
  /** The resolved queries by id. */
  resolved: Map<string, RQuery>;
  /** A query's statements for a dialect (QueryViews.Preview), or null when no query of the database has the id. Throws on an
   * unknown dialect or option. */
  preview(id: string, dialect?: string | null, options?: QuerySqlOptions | null): { preview: QuerySqlPreview; diagnostics: Diagnostic[] } | null;
}

/** Resolves every query file of the view's database against the view's tables, views, routines and database types. */
export function resolveQueries(view: DatabaseView, docs: ReadonlyMap<string, Json>, paths: ReadonlyMap<string, string> = new Map()): ResolvedQueries {
  const db = view.id;
  const all = [...docs.values()];
  // The sources a reference may name: a table key, a table file's id (a designed table's is its key, an overlay names its entity's
  // or relation's projected table), a view's id.
  const sources = new Map<string, Found>();
  for (const t of view.tables) if (!sources.has(t.key)) sources.set(t.key, { table: t });
  for (const d of all.filter((x) => x.kind === "table" && x.database === db)) {
    const key = typeof d.entity === "string" && !d.attribute ? `${d.entity}@${db}` : typeof d.relation === "string" ? `${d.relation}@${db}` : String(d.id);
    const table = view.tables.find((t) => t.key === key);
    if (table && !sources.has(String(d.id))) sources.set(String(d.id), { table });
  }
  for (const v of view.views) if (!sources.has(v.id)) sources.set(v.id, { view: v });
  const routines = new Map((view.routines ?? []).map((r) => [r.id, r]));
  const types = new Map((view.types ?? []).map((t) => [t.id, t]));
  const stereotypes = new Map(all.filter((d) => d.kind === "stereotype").map((s) => [String(s.key), s]));
  const database = { id: db, name: view.name, dialect: view.dialect, quoting: view.quoting };

  const diagnostics: Diagnostic[] = [];
  const resolved = new Map<string, RQuery>();
  // A file with a key 0.28.0 removed does not load (MQ4127, reported with the schema findings).
  const files = all.filter((d) => d.kind === "query" && d.database === db && removedQueryKeys(d).length === 0).sort((a, b) => cmp(String(a.id), String(b.id)));
  for (const file of files) {
    const query = new Binder(file, view, docs, sources, routines, types, database, (d) =>
      diagnostics.push({ ...d, filePath: paths.get(String(file.id)) ?? null }),
    ).build();
    resolved.set(query.id, query);
  }

  const ordered = files
    .map((f) => ({ file: f, query: resolved.get(String(f.id))! }))
    .sort((a, b) => cmp(a.query.name, b.query.name) || cmp(a.query.id, b.query.id));
  // Each query becomes a class named after it: two names that differ only once Pascal-cased collide (MQ3001 reports names that
  // are equal ignoring case).
  const classes = new Map<string, RQuery>();
  for (const { query } of ordered) {
    const name = queryClassName(query.name);
    const taken = classes.get(name);
    if (!taken) classes.set(name, query);
    else if (taken.name.toLowerCase() !== query.name.toLowerCase())
      diagnostics.push({
        rule: "MQ4040",
        severity: SEVERITY.get("MQ4040") ?? "error",
        message: `Query '${query.name}' becomes class '${name}', as query '${taken.name}' of database '${view.name}' does: rename one of them.`,
        elementId: query.id,
        filePath: paths.get(query.id) ?? null,
        jsonPointer: "/name",
        line: null,
        column: null,
      });
  }
  const queries = ordered.map(({ file, query }) => project(query, file, stereotypes));
  return {
    queries,
    diagnostics,
    resolved,
    preview(id, dialect, options) {
      const query = resolved.get(id);
      if (!query) return null;
      const main = renderQuery(query, dialect, options);
      const found = [...main.diagnostics];
      const name = (dialect ? parseDialect(dialect) : null) ?? (query.database.dialect as QuerySqlPreview["dialect"]);
      const seen = new Set<string>();
      const distinct = found.filter((d) => !seen.has(`${d.rule}|${d.message}`) && !!seen.add(`${d.rule}|${d.message}`));
      return {
        preview: { id: query.id, name: query.name, database: query.database.id, dialect: name, sql: main.sql, parameters: main.parameters },
        diagnostics: distinct,
      };
    },
  };
}

const cmp = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);

/** Outcomes.Sort: path, line, column, rule, message, element, pointer, without duplicates. */
export function sortDiagnostics(diagnostics: readonly Diagnostic[]): Diagnostic[] {
  const seen = new Set<string>();
  return diagnostics
    .filter((d) => {
      const key = JSON.stringify([d.rule, d.severity, d.message, d.elementId, d.filePath, d.jsonPointer, d.line, d.column]);
      return !seen.has(key) && !!seen.add(key);
    })
    .sort(
      (a, b) =>
        cmp(a.filePath ?? "", b.filePath ?? "") ||
        (a.line ?? 0) - (b.line ?? 0) ||
        (a.column ?? 0) - (b.column ?? 0) ||
        cmp(a.rule, b.rule) ||
        cmp(a.message, b.message) ||
        cmp(a.elementId ?? "", b.elementId ?? "") ||
        cmp(a.jsonPointer ?? "", b.jsonPointer ?? ""),
    );
}

// ------------------------------------------------------------------ projection (QueryViews.cs)

const tree = <T>(value: unknown): T | null => (value === undefined || value === null ? null : (structuredClone(value) as T));

function fields(select: readonly RField[], file: Json[]): QueryFieldView[] {
  return select.map((f, i) => ({
    name: f.name,
    expression: (i < file.length ? tree(file[i].expression) : null) ?? {},
    type: f.type,
    nativeType: f.nativeType,
    codeType: f.codeType,
    nullable: f.nullable,
  })) as QueryFieldView[];
}

/** The declared columns of a query written as SQL text: no expression, and the description when there is one. */
function declaredColumns(select: readonly RField[], file: Json[]): QueryFieldView[] {
  return select.map((f, i) => {
    const description = i < file.length ? file[i].description : undefined;
    return {
      name: f.name,
      type: f.type,
      nativeType: f.nativeType,
      codeType: f.codeType,
      nullable: f.nullable,
      ...(typeof description === "string" && description !== "" ? { description } : {}),
    };
  });
}

function sourceView(source: RSource, on: unknown): NonNullable<QueryView["from"]> {
  const found = sourceTargets.get(source);
  return {
    source: source.source,
    alias: source.alias,
    kind: source.joinKind as NonNullable<QueryView["from"]>["kind"],
    tableKey: found?.table?.key ?? null,
    viewId: found?.view?.id ?? null,
    ...(source.named ? { namedQuery: source.named.name } : {}),
    name: source.name,
    schema: source.schema,
    on: tree(on),
  };
}

function project(query: RQuery, file: Json, stereotypes: ReadonlyMap<string, Json>): QueryView {
  const rendered = query.sql.length === 0 ? null : renderQuery(query);
  const joins = arr(file.joins);
  const annotations = annotationsOf(file, stereotypes);
  const sqlText = query.form === "sql";
  return {
    id: query.id,
    name: query.name,
    parameters: query.parameters.map((p) => ({
      name: p.name,
      type: p.type,
      dbTypeId: p.dbTypeId,
      length: p.length,
      precision: p.precision,
      scale: p.scale,
      nativeType: p.nativeType,
      codeType: p.codeType,
      collection: p.collection,
      default: p.default ?? null,
      description: p.description,
    })),
    ...(sqlText ? { form: "sql" as const, texts: { ...query.texts }, ...(query.writes?.length ? { writes: [...query.writes] } : {}) } : {}),
    ...(sqlText ? {} : { from: sourceView(query.from, null) }),
    joins: query.joins.map((j, i) => sourceView(j, i < joins.length ? joins[i].on : null)),
    ...(query.with?.length
      ? {
          with: query.with.map((n): NonNullable<QueryView["with"]>[number] => {
            const entry = arr(file.with)[n.index];
            const { recursive, ...anchor } = entry;
            return {
              name: n.name,
              query: (tree(anchor) ?? {}) as NonNullable<QueryView["with"]>[number]["query"],
              columns: fields(n.columns, arr(entry.select)),
              ...(n.recursive
                ? { recursive: (tree(recursive) ?? {}) as NonNullable<NonNullable<QueryView["with"]>[number]["recursive"]>, union: n.union }
                : {}),
            };
          }),
        }
      : {}),
    select: sqlText ? declaredColumns(query.select, arr(file.columns)) : fields(query.select, arr(file.select)),
    where: tree(file.where),
    groupBy: arr(file.groupBy).map((g) => tree(g)!) as QueryView["groupBy"],
    having: tree(file.having),
    orderBy: arr(file.orderBy).map((o) => tree(o)!) as QueryView["orderBy"],
    distinct: query.distinct,
    paging: tree(file.paging),
    sql: rendered?.sql ?? null,
    sqlParameters: rendered?.parameters ?? [],
    uses: [...query.uses],
    displayName: annotations.displayName || null,
    pluralName: annotations.pluralName || null,
    description: annotations.description,
    stereotypes: annotations.stereotypes,
    tags: annotations.tags,
    category: annotations.category,
    properties: annotations.properties,
    generation: annotations.generation,
  };
}

// ------------------------------------------------------------------ binding (DatabaseRun.Queries.cs, QueryBuilder)

const nullExpression = (): RExpression => ({
  node: "null",
  args: [],
  case: [],
  sql: {},
  type: null,
  nativeType: null,
  codeType: null,
  nullable: true,
});

const expr = (node: RExpression["node"], rest: Partial<RExpression> = {}): RExpression => ({
  node,
  args: [],
  case: [],
  sql: {},
  type: null,
  nativeType: null,
  codeType: null,
  nullable: true,
  ...rest,
});

const pred = (node: RPredicate["node"], rest: Partial<RPredicate> = {}): RPredicate => ({ node, and: [], or: [], values: [], ...rest });

/** DatabaseRun.DefaultFits: whether a parameter's default is a value of its code type (MQ4043). */
function defaultFits(value: unknown, codeType: string): boolean {
  switch (codeType) {
    case "bool":
      return typeof value === "boolean";
    case "int16":
      return Number.isInteger(value) && (value as number) >= -32768 && (value as number) <= 32767;
    case "int32":
      return Number.isInteger(value) && (value as number) >= -2147483648 && (value as number) <= 2147483647;
    case "int64":
      return Number.isInteger(value);
    case "decimal":
    case "double":
    case "float":
      return typeof value === "number";
    case "uuid":
      return typeof value === "string" && /^[{(]?[0-9A-Fa-f]{8}-?[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{4}-?[0-9A-Fa-f]{12}[)}]?$/.test(value);
    default:
      return typeof value === "string";
  }
}

const describeValue = (value: unknown) =>
  typeof value === "string" ? `the text '${value}'` : typeof value === "boolean" || typeof value === "number" ? String(value) : "a value";

/** A JSON literal as a plain value: a string, a number, a boolean. */
const plain = (value: unknown): unknown => (typeof value === "string" || typeof value === "number" || typeof value === "boolean" ? value : null);

class Binder {
  private readonly parameters = new Map<string, RParameter>();
  private readonly uses: string[] = [];
  private failed = false;
  /** The one expression a list parameter may be: the whole right side of the in or notIn being resolved. */
  private listOperand: Json | null = null;
  private query!: RQuery;
  private readonly fileName: string;

  constructor(
    private readonly file: Json,
    private readonly view: DatabaseView,
    private readonly docs: ReadonlyMap<string, Json>,
    private readonly sources: ReadonlyMap<string, Found>,
    private readonly routines: ReadonlyMap<string, RoutineView>,
    private readonly types: ReadonlyMap<string, DatabaseTypeView>,
    private readonly database: RQuery["database"],
    private readonly report: (d: Diagnostic) => void,
  ) {
    this.fileName = String(file.name ?? "");
  }

  build(): RQuery {
    const file = this.file;
    this.query = {
      id: String(file.id),
      name: this.fileName,
      database: this.database,
      parameters: [],
      from: null as unknown as RSource,
      joins: [],
      select: [],
      where: null,
      groupBy: [],
      having: null,
      orderBy: [],
      distinct: false,
      paging: null,
      parent: null,
      sql: "",
      uses: [],
    };
    const query = this.query;
    if (file.from === undefined && file.sql !== null && typeof file.sql === "object") return this.buildSqlText();
    query.parameters = this.resolveParameters();
    const scope: Scope = { query, outer: null, aliases: new Map(), sources: [] };
    this.resolveWith();
    this.resolveBody(query, scope, file, "");
    query.distinct = file.distinct === true;
    query.paging = this.resolvePaging();
    this.checkWithNames();
    query.uses = [...this.uses];
    if (!this.failed) this.checkDistinctOrder(query);
    // A query with errors has none: they are reported, and generation stops on them before any template reads it.
    query.sql = this.failed ? "" : renderQuery(query).sql;
    return query;
  }

  // ---------------------------------------------------------------- a query written as SQL text (sql-queries-design.md)

  /** The query's texts, the declared columns, `reads` and `writes`, and the checks of section 9 that read them. */
  private buildSqlText(): RQuery {
    const query = this.query;
    const file = this.file;
    query.form = "sql";
    query.parameters = this.resolveParameters();
    const texts: Record<string, string> = {};
    for (const [key, value] of Object.entries(file.sql as Json)) if (typeof value === "string") texts[key] = value;
    query.texts = texts;
    this.resolveColumns();
    // What a hand-written query uses is what it reads; the database types of its parameters and columns are not reads.
    this.uses.length = 0;
    this.resolveReadsAndWrites();
    const own = sqlFor(texts, this.view.dialect);
    if (own === null) this.add("MQ4029", sqlTextMissing(this.fileName, this.view.dialect, this.view.name), "/sql");
    this.checkTexts(texts);
    query.uses = [...this.uses];
    query.sql = this.failed ? "" : renderQuery(query).sql;
    return query;
  }

  /** The dialect a text's key stands for: its own, or the database's for `*`. */
  private dialectOfText(key: string): SqlDialect {
    return (parseDialect(key) ?? parseDialect(this.view.dialect) ?? "postgresql") as SqlDialect;
  }

  private resolveColumns(): void {
    const names = new Map<string, number>();
    const properties = new Map<string, string>();
    arr(this.file.columns).forEach((c, i) => {
      const pointer = `/columns/${i}`;
      const name = String(c.name ?? "");
      const num = (v: unknown) => (typeof v === "number" ? v : null);
      const { keyword, dbType, native } = this.slot(c.type, { length: num(c.length), precision: num(c.precision), scale: num(c.scale) });
      if (!keyword && !dbType)
        this.add(
          "MQ4018",
          `Column '${name}' of query '${this.fileName}' has type '${String(c.type ?? "")}', which is neither a built-in type nor a database type of database '${this.view.name}'.`,
          pointer + "/type",
        );
      if (dbType) this.use(dbType.id);
      this.query.select.push({
        name,
        expression: nullExpression(),
        type: keyword,
        nativeType: native,
        codeType: keyword ?? (dbType?.typeKind === "domain" && dbType.base ? dbType.base : "string"),
        nullable: c.nullable !== false,
      });
      const key = name.toLowerCase();
      if (names.has(key))
        this.add(
          "MQ3001",
          `Column name '${name}' is already used at index ${names.get(key)} of the columns of query '${this.fileName}' (ignoring case).`,
          pointer + "/name",
        );
      else {
        names.set(key, i);
        const property = enginePascal(name);
        if (isReservedName(name))
          this.add(
            "MQ4040",
            `Column '${name}' of query '${this.fileName}' starts with mq_, which generated code reserves for its own names.`,
            pointer + "/name",
          );
        else if (properties.has(property))
          this.add(
            "MQ4040",
            `Column '${name}' of query '${this.fileName}' becomes property '${property}', as column '${properties.get(property)}' does: give it another name.`,
            pointer + "/name",
          );
        else properties.set(property, name);
      }
    });
  }

  private resolveReadsAndWrites(): void {
    const sequences = new Map((this.view.sequences ?? []).map((q) => [q.id, q]));
    const uses: string[] = [];
    const writes: string[] = [];
    const list = (value: unknown): string[] => (Array.isArray(value) ? value.filter((x): x is string => typeof x === "string") : []);
    // An entity id stands for its table in this database (`<id>@<db>`), as it does for a `from.source`.
    const sourceOf = (ref: string): Found | undefined => this.sources.get(ref) ?? this.sources.get(`${ref}@${this.view.id}`);
    list(this.file.reads).forEach((ref, i) => {
      const found = sourceOf(ref);
      const id = found?.table?.key ?? found?.view?.id ?? this.routines.get(ref)?.id ?? sequences.get(ref)?.id ?? this.types.get(ref)?.id;
      if (!id) this.add("MQ4021", `Query '${this.fileName}' reads '${ref}', which ${this.whatIsReadable(ref)}.`, `/reads/${i}`);
      else if (!uses.includes(id)) uses.push(id);
    });
    list(this.file.writes).forEach((ref, i) => {
      const found = sourceOf(ref);
      if (!found?.table) this.add("MQ4021", `Query '${this.fileName}' writes '${ref}', which ${this.whatIsWritable(ref)}.`, `/writes/${i}`);
      else if (!writes.includes(found.table.key)) writes.push(found.table.key);
    });
    for (const id of uses) this.use(id);
    this.query.writes = writes;
  }

  private whatIsReadable(ref: string): string {
    const element = this.docs.get(ref.split("@")[0]);
    if (element && element.database !== undefined && element.database !== this.view.id)
      return `is ${String(element.kind)} '${String(element.name ?? "")}' of database '${String(this.docs.get(String(element.database))?.name ?? element.database)}', not of '${this.view.name}'`;
    if (element)
      return `is ${String(element.kind)} '${String(element.name ?? "")}', not a table, view, routine, sequence or database type of database '${this.view.name}'`;
    return `is no table, view, routine, sequence or database type of database '${this.view.name}'`;
  }

  private whatIsWritable(ref: string): string {
    const element = this.docs.get(ref.split("@")[0]);
    if (this.sources.get(ref)?.view) return `is view '${String(element?.name ?? ref)}', not a table`;
    if (element && element.database !== undefined && element.database !== this.view.id)
      return `is ${String(element.kind)} '${String(element.name ?? "")}' of database '${String(this.docs.get(String(element.database))?.name ?? element.database)}', not of '${this.view.name}'`;
    if (element?.kind === "entity") return `is entity '${String(element.name ?? "")}', which has no table in database '${this.view.name}'`;
    if (element) return `is ${String(element.kind)} '${String(element.name ?? "")}', not a table`;
    return `is no table of database '${this.view.name}'`;
  }

  /** MQ4024, MQ4096 and MQ4098 over every text. */
  private checkTexts(texts: Record<string, string>): void {
    const query = this.query;
    const declared = new Set(query.parameters.map((p) => p.name));
    const scans = Object.entries(texts)
      .sort((a, b) => cmp(a[0], b[0]))
      .map(([key, text]) => ({
        key,
        text,
        dialect: this.dialectOfText(key),
        scan: scanSqlText(text, this.dialectOfText(key)),
      }));
    for (const { key, text, scan } of scans)
      for (const p of scan.placeholders) {
        if (declared.has(p.name)) continue;
        const { line, column } = sqlTextPosition(text, p.offset);
        this.add("MQ4024", sqlTextNamesNoParameter(this.fileName, p.name, line, column, key), `/sql/${key}`);
      }
    if (scans.length > 0)
      query.parameters.forEach((p, i) => {
        const without = scans.filter((x) => !x.scan.placeholders.some((ph) => ph.name === p.name));
        if (without.length === 0) return;
        this.add(
          "MQ4096",
          sqlTextParameterUnused(this.fileName, p.name, without.length === scans.length ? null : without.map((x) => x.key)),
          `/parameters/${i}`,
        );
      });
    if (scans.length === 0) return;
    const named = (field: "reads" | "writes") =>
      (Array.isArray(this.file[field]) ? (this.file[field] as unknown[]) : []).forEach((value, i) => {
        const found = typeof value === "string" ? (this.sources.get(value) ?? this.sources.get(`${value}@${this.view.id}`)) : undefined;
        const target = found?.table ?? found?.view;
        if (target && !scans.some((x) => sqlTextMentions(x.scan, target.name)))
          this.add("MQ4098", sqlTextNamesNothing(this.fileName, field, found?.table ? "table" : "view", target.name), `/${field}/${i}`);
      });
    named("reads");
    named("writes");
    const hasWrites = Array.isArray(this.file.writes) && this.file.writes.length > 0;
    const hasColumns = arr(this.file.columns).length > 0;
    for (const { key, scan } of [...scans].sort((a, b) => cmp(a.key, b.key))) {
      const verb = scan.verb;
      if (!verb) continue;
      const afterWith = scan.firstWord === "WITH";
      if (!hasWrites && WRITE_VERBS.has(verb)) this.add("MQ4098", sqlTextWritesNothing(key, this.fileName, verb, afterWith), `/sql/${key}`);
      else if (!hasColumns && ROW_VERBS.has(verb)) this.add("MQ4098", sqlTextHasNoColumns(key, this.fileName, verb, afterWith), `/sql/${key}`);
    }
  }

  /** MQ4038: a distinct query orders only by selected expressions, and not by nulls first or last where the dialect emulates them. */
  private checkDistinctOrder(target: RQuery): void {
    if (!target.distinct || target.orderBy.length === 0) return;
    const selected = new Set(target.select.map((f) => expressionText(target, f.expression)));
    target.orderBy.forEach((order, i) => {
      const pointer = `/orderBy/${i}`;
      if (!selected.has(expressionText(target, order.expression)))
        this.add(
          "MQ4038",
          `Query '${this.fileName}' is distinct and orders by an expression its select list does not have: select it, or order by a selected one.`,
          pointer + "/expression",
        );
      else if (order.nulls && (this.view.dialect === "sqlserver" || this.view.dialect === "mysql"))
        this.add(
          "MQ4038",
          `Query '${this.fileName}' is distinct and orders nulls ${order.nulls}, which ${this.view.dialect} writes as an extra order term the select list does not have: leave nulls out.`,
          pointer + "/nulls",
        );
    });
  }

  private add(rule: string, message: string, pointer: string): void {
    if (!RENDERABLE.has(rule)) this.failed = true;
    this.report({
      rule,
      severity: SEVERITY.get(rule) ?? "error",
      message,
      elementId: String(this.file.id),
      filePath: null,
      jsonPointer: pointer,
      line: null,
      column: null,
    });
  }

  private use(id: string): void {
    if (!this.uses.includes(id)) this.uses.push(id);
  }

  /** A type slot (`type` with facets): a built-in keyword, or a database type of the database, with its native type. */
  private slot(type: unknown, facets: { length: number | null; precision: number | null; scale: number | null }) {
    const text = typeof type === "string" ? type : "";
    const keyword = (BUILTIN_TYPES as readonly string[]).includes(text) ? text : null;
    const dbType = keyword ? undefined : this.types.get(text);
    const native = keyword
      ? nativeType(this.view.dialect, keyword, {
          length: facets.length ?? undefined,
          precision: facets.precision ?? undefined,
          scale: facets.scale ?? undefined,
        })
      : (dbType?.nativeName ?? "");
    return { keyword, dbType, native };
  }

  private keywordNativeType(keyword: string): string {
    return nativeType(this.view.dialect, keyword, {});
  }

  private resolveParameters(): RParameter[] {
    const list: RParameter[] = [];
    const names = new Set<string>();
    arr(this.file.parameters).forEach((p, i) => {
      const pointer = `/parameters/${i}`;
      const num = (v: unknown) => (typeof v === "number" ? v : null);
      const facets = { length: num(p.length), precision: num(p.precision), scale: num(p.scale) };
      const { keyword, dbType, native } = this.slot(p.type, facets);
      if (!keyword && !dbType)
        this.add(
          "MQ4018",
          `Parameter '${String(p.name)}' of query '${this.fileName}' has type '${String(p.type ?? "")}', which is neither a built-in type nor a database type of database '${this.view.name}'.`,
          pointer + "/type",
        );
      if (dbType) this.use(dbType.id);
      const resolved: RParameter = {
        name: String(p.name ?? ""),
        type: keyword,
        dbTypeId: dbType?.id ?? null,
        dbTypeName: dbType?.name ?? null,
        ...facets,
        nativeType: native,
        codeType: keyword ?? (dbType?.typeKind === "domain" && dbType.base ? dbType.base : "string"),
        collection: p.collection === true,
        default: null,
        description: typeof p.description === "string" ? p.description : null,
      };
      if (p.default !== undefined && p.default !== null) {
        const value = plain(p.default);
        if (typeof value === "number" && !Number.isFinite(value))
          this.add("MQ4043", `The default of parameter '${resolved.name}' of query '${this.fileName}' is a number too large to hold.`, pointer + "/default");
        else if (!defaultFits(value, resolved.codeType))
          this.add(
            "MQ4043",
            `The default of parameter '${resolved.name}' of query '${this.fileName}' is ${describeValue(value)}, which is not a value of its type '${resolved.codeType}'.`,
            pointer + "/default",
          );
        else resolved.default = value;
      }
      const folded = resolved.name.toLowerCase();
      if (names.has(folded))
        this.add(
          "MQ3001",
          `Parameter name '${resolved.name}' is already used in query '${this.fileName}'${this.parameters.has(resolved.name) ? "" : " (ignoring case)"}.`,
          pointer + "/name",
        );
      else {
        names.add(folded);
        if (isReservedName(resolved.name))
          this.add(
            "MQ4040",
            `Parameter '${resolved.name}' of query '${this.fileName}' starts with mq_, which generated code reserves for its own names.`,
            pointer + "/name",
          );
      }
      if (!this.parameters.has(resolved.name)) this.parameters.set(resolved.name, resolved);
      list.push(resolved);
    });
    return list;
  }

  private resolvePaging(): RQuery["paging"] {
    const paging = this.file.paging as Json | undefined;
    if (!paging || typeof paging !== "object") return null;
    const bound = (value: unknown, pointer: string): [RParameter | null, number | null] => {
      if (typeof value === "number" && Number.isInteger(value)) return [null, value];
      if (typeof value !== "string") return [null, null];
      const parameter = this.parameters.get(value);
      if (!parameter) {
        this.add("MQ4024", `Query '${this.fileName}' pages by parameter '${value}', which it does not declare.`, pointer);
        return [null, null];
      }
      if (!INTEGER_TYPES.includes(parameter.codeType) || parameter.collection)
        this.add(
          "MQ4030",
          `Query '${this.fileName}' pages by parameter '${value}', whose type '${parameter.type ?? parameter.dbTypeName ?? ""}' is not an integer type (int16, int32 or int64).`,
          pointer,
        );
      return [parameter, null];
    };
    const [offsetParameter, offset] = bound(paging.offset, "/paging/offset");
    const [limitParameter, limit] = bound(paging.limit, "/paging/limit");
    return { offsetParameter, offset, limitParameter, limit };
  }

  /** The sources, select list, conditions, grouping and ordering of a query or nested query (`body` as the file writes it). */
  private resolveBody(target: RQuery, scope: Scope, body: Json, at: string, withSelect = true): void {
    const from = (body.from as Json | undefined) ?? {};
    target.from = this.addSource(scope, String(from.source ?? ""), typeof from.alias === "string" ? from.alias : null, "from", at + "/from");
    const joined: RSource[] = [];
    arr(body.joins).forEach((join, i) => {
      const pointer = `${at}/joins/${i}`;
      const kind = typeof join.kind === "string" ? join.kind : "inner";
      const source = this.addSource(scope, String(join.source ?? ""), typeof join.alias === "string" ? join.alias : null, kind, pointer);
      if (kind === "right" || kind === "full") for (const earlier of scope.sources) if (earlier !== source) earlier.optional = true;
      if (kind === "left" || kind === "full") source.optional = true;
      const hasOn = !!join.on && typeof join.on === "object";
      if (kind === "cross" && hasOn)
        this.add(
          "MQ4035",
          `Query '${this.fileName}' cross joins '${source.alias}' with a condition: a cross join takes none (make it an inner join, or remove on).`,
          pointer + "/on",
        );
      else if (kind !== "cross" && !hasOn)
        this.add(
          "MQ4035",
          `Query '${this.fileName}' joins '${source.alias}' (${kind}) without a condition: give the join an on, or make it a cross join.`,
          pointer,
        );
      if (kind === "full" && this.view.dialect === "mysql")
        this.add(
          "MQ4036",
          `Query '${this.fileName}' full joins '${source.alias}', which MySQL (database '${this.view.name}') cannot run: it has no full join.`,
          pointer + "/kind",
        );
      else if ((kind === "right" || kind === "full") && this.view.dialect === "sqlite")
        this.add(
          "MQ4037",
          `Query '${this.fileName}' ${kind} joins '${source.alias}', which SQLite (database '${this.view.name}') runs from version 3.39 only.`,
          pointer + "/kind",
        );
      if (hasOn && kind !== "cross") source.on = this.predicate(join.on as Json, scope, pointer + "/on");
      joined.push(source);
    });
    target.joins = joined;
    target.select = withSelect ? this.resolveFields(arr(body.select), scope, at) : [];
    target.where = body.where && typeof body.where === "object" ? this.predicate(body.where as Json, scope, at + "/where") : null;
    target.groupBy = arr(body.groupBy).map((g, i) => this.expression(g, scope, `${at}/groupBy/${i}`));
    target.having = body.having && typeof body.having === "object" ? this.predicate(body.having as Json, scope, at + "/having") : null;
    target.orderBy = withSelect
      ? arr(body.orderBy).map((o, i) => ({
          expression: this.expression((o.expression as Json | undefined) ?? {}, scope, `${at}/orderBy/${i}/expression`),
          direction: typeof o.direction === "string" ? o.direction : "asc",
          nulls: typeof o.nulls === "string" ? o.nulls : null,
        }))
      : [];
  }

  private addSource(scope: Scope, reference: string, alias: string | null, kind: string, pointer: string): RSource {
    const source: RSource = { alias: "", joinKind: kind, source: reference, tableKey: null, viewId: null, name: "", schema: null, on: null, optional: false };
    let found = this.sources.get(reference);
    if (!found) {
      const keyed = this.sources.get(`${reference}@${this.view.id}`);
      if (keyed?.table) found = keyed;
    }
    if (found?.table) {
      source.tableKey = found.table.key;
      source.name = found.table.name;
      source.schema = found.table.schema;
      this.use(found.table.key);
    } else if (found?.view) {
      source.viewId = found.view.id;
      source.name = found.view.name;
      source.schema = found.view.schema;
      this.use(found.view.id);
    } else if (!this.namedSource(scope, source, reference, pointer)) {
      this.add("MQ4021", `Query '${this.fileName}' reads '${reference}', which ${this.whatIs(reference)}.`, pointer + "/source");
      source.name = reference;
    }
    sourceTargets.set(source, found ?? (source.named ? { named: source.named } : {}));
    source.alias = alias ?? source.name;
    if (scope.aliases.has(source.alias))
      this.add(
        "MQ4022",
        `Query '${this.fileName}' declares alias '${source.alias}' twice; give each source its own alias.`,
        pointer + (alias === null ? "/source" : "/alias"),
      );
    else scope.aliases.set(source.alias, source);
    scope.sources.push(source);
    return source;
  }

  /** What a source reference that resolves to no table or view of the database is, for MQ4021. */
  private whatIs(reference: string): string {
    const element = this.docs.get(reference.split("@")[0]);
    const other = element && (element.kind === "table" || element.kind === "view") ? String(element.database ?? "") : null;
    if (other && other !== this.view.id)
      return `is ${String(element!.kind)} '${String(element!.name ?? "")}' of database '${String(this.docs.get(other)?.name ?? other)}', not of '${this.view.name}'`;
    if (element?.kind === "entity") return `is entity '${String(element.name ?? "")}', which has no table in database '${this.view.name}'`;
    if (element) return `is ${String(element.kind)} '${String(element.name ?? "")}', not a table or view`;
    return `is no table or view of database '${this.view.name}'${arr(this.file.with).length > 0 ? " and no named query of the query" : ""}`;
  }

  // ---------------------------------------------------------------- named queries (DatabaseRun.NamedQueries.cs)

  private readonly withList: RNamedQuery[] = [];
  /** How many named queries the sources being resolved may read. */
  private withVisible = 0;
  private withCurrent = -1;
  private inWith = false;
  private self: RNamedQuery | null = null;
  private selfPart: RQuery | null = null;
  private selfReads: { source: RSource; nested: boolean; pointer: string }[] = [];

  /** A part of a named query: a nested query of the top query, with its parameters. */
  private part(name: string): RQuery {
    return {
      ...this.query,
      name,
      parent: this.query,
      from: null as unknown as RSource,
      joins: [],
      select: [],
      where: null,
      groupBy: [],
      having: null,
      orderBy: [],
      distinct: false,
      paging: null,
      sql: "",
      uses: [],
      with: undefined,
    };
  }

  /** Resolves the file's `with` into `query.with`, before the query's own sources, which may read them. */
  private resolveWith(): void {
    const entries = arr(this.file.with);
    if (entries.length === 0) return;
    const names = new Map<string, string>();
    entries.forEach((entry, i) => {
      const at = `/with/${i}`;
      const name = String(entry.name ?? "");
      const first = names.get(name.toLowerCase());
      if (first === undefined) names.set(name.toLowerCase(), name);
      else
        this.add("MQ3001", `Named query name '${name}' is already used in query '${this.fileName}'${first === name ? "" : " (ignoring case)"}.`, at + "/name");
      [this.withCurrent, this.withVisible, this.inWith] = [i, i, true];
      const anchor = this.part(name);
      this.resolveBody(anchor, { query: anchor, outer: null, aliases: new Map(), sources: [] }, entry, at);
      anchor.distinct = entry.distinct === true;
      const named: RNamedQuery = {
        name,
        index: i,
        query: anchor,
        columns: anchor.select.map((f) => ({ ...f })),
        recursive: null,
        union: "all",
      };
      this.withList.push(named);
      if (entry.recursive && typeof entry.recursive === "object") this.resolveRecursive(named, entry.recursive as Json, `${at}/recursive`);
    });
    [this.withCurrent, this.withVisible, this.inWith] = [-1, this.withList.length, false];
    this.query.with = [...this.withList];
  }

  /** The repeating part of a recursive named query, its rules (MQ4099), and the columns' types and nullability merged. */
  private resolveRecursive(named: RNamedQuery, recursive: Json, at: string): void {
    const part = this.part(named.name);
    [this.self, this.selfPart] = [named, part];
    this.selfReads = [];
    this.resolveBody(part, { query: part, outer: null, aliases: new Map(), sources: [] }, { ...recursive, groupBy: [], having: null }, at);
    [this.self, this.selfPart] = [null, null];
    named.recursive = part;
    named.union = recursive.union === "distinct" ? "distinct" : "all";
    const what = `The repeating part of named query '${named.name}' of query '${this.fileName}'`;
    const dialect = { sqlserver: "SQL Server", oracle: "Oracle" }[this.view.dialect as "oracle"];
    if (named.union === "distinct" && dialect)
      this.add(
        "MQ4099",
        `${what} adds its rows with union distinct, which ${dialect} (database '${this.view.name}') does not take in a recursive query: use union all.`,
        at + "/union",
      );
    const anchor = named.query.select;
    if (part.select.length !== anchor.length || part.select.some((f, i) => f.name !== anchor[i].name))
      this.add(
        "MQ4099",
        `${what} selects (${part.select.map((f) => f.name).join(", ")}) where its anchor selects (${anchor.map((f) => f.name).join(", ")}): give it the same fields, with the same names in the same order.`,
        at + "/select",
      );
    else
      anchor.forEach((f, i) => {
        const [expected, actual] = [f.type, part.select[i].type];
        if (expected && actual && expected !== actual)
          this.add(
            "MQ4099",
            `Field '${f.name}' of the ${what.slice(4)} is of type '${actual}', and the anchor's of type '${expected}': give both the same type (a cast).`,
            `${at}/select/${i}`,
          );
      });
    const reads = this.selfReads;
    const own = `reads '${named.name}'`;
    if (reads.length === 0)
      this.add("MQ4099", `${what} does not read '${named.name}': a repeating part reads its own name exactly once, in its from or a join.`, at + "/from");
    else if (reads.length > 1) this.add("MQ4099", `${what} ${own} ${reads.length} times: a repeating part reads its own name exactly once.`, reads[1].pointer);
    else if (reads[0].nested)
      this.add("MQ4099", `${what} ${own} in a nested query: a repeating part reads its own name in its own from or a join.`, reads[0].pointer);
    else if (reads[0].source.optional)
      this.add(
        "MQ4099",
        `${what} ${own} on the null-padded side of an outer join (the right side of a left join, the left side of a right join, either side of a full join), which the databases refuse: read it on the side that keeps its rows, or use an inner join.`,
        reads[0].pointer,
      );
    if (isGrouped(part)) this.add("MQ4099", `${what} calls an aggregate function, which a repeating part may not.`, at + "/select");
    if (this.view.dialect === "sqlserver") {
      part.joins.forEach((j, i) => {
        if (["left", "right", "full"].includes(j.joinKind))
          this.add(
            "MQ4099",
            `${what} has an outer join, which SQL Server (database '${this.view.name}') does not take in a repeating part: use an inner join.`,
            `${at}/joins/${i}/kind`,
          );
      });
      if (hasNested(part))
        this.add("MQ4099", `${what} has a nested query (exists), which SQL Server (database '${this.view.name}') does not take in a repeating part.`, at);
    }
    // A column's type is the anchor's (the repeating part's when the anchor's is unknown); it may be null when either part's may.
    for (let i = 0; i < Math.min(anchor.length, part.select.length); i++) {
      const [column, field] = [named.columns[i], part.select[i]];
      column.nullable = column.nullable || field.nullable;
      if (column.type === null && field.type !== null) [column.type, column.nativeType, column.codeType] = [field.type, field.nativeType, field.codeType];
    }
  }

  /** A source that names a named query of the file's `with`, or the reason it may not read it (MQ4099); false when it names none. */
  private namedSource(scope: Scope, source: RSource, reference: string, pointer: string): boolean {
    const index = arr(this.file.with).findIndex((e) => e.name === reference);
    if (index < 0) return false;
    source.name = reference;
    if (index < this.withVisible) {
      source.named = this.withList[index];
      return true;
    }
    if (index === this.withCurrent && this.self) {
      source.named = this.self;
      this.selfReads.push({ source, nested: scope.query !== this.selfPart, pointer: pointer + "/source" });
      return true;
    }
    const reader = String(arr(this.file.with)[this.withCurrent]?.name ?? "");
    this.add(
      "MQ4099",
      index === this.withCurrent
        ? `Named query '${reader}' of query '${this.fileName}' reads itself outside recursive: only its repeating part may read its own name.`
        : `Named query '${reader}' of query '${this.fileName}' reads named query '${reference}', which is listed after it: move '${reference}' before it.`,
      pointer + "/source",
    );
    return true;
  }

  /** MQ4099: a named query with the name of a table or view the query reads, which the SQL would then not read. */
  private checkWithNames(): void {
    for (const named of this.withList) {
      let clash: [string, string] | null = null;
      for (const id of this.uses) {
        const found = this.sources.get(id);
        const hit: [string, string] | null = found?.table ? ["table", found.table.name] : found?.view ? ["view", found.view.name] : null;
        if (hit && hit[1].toLowerCase() === named.name.toLowerCase()) {
          clash = hit;
          break;
        }
      }
      if (clash)
        this.add(
          "MQ4099",
          `Named query '${named.name}' of query '${this.fileName}' has the name of ${clash[0]} '${clash[1]}', which the query reads, so the SQL would read one for the other: rename the named query.`,
          `/with/${named.index}/name`,
        );
    }
  }

  private resolveFields(select: Json[], scope: Scope, at: string): RField[] {
    const fields: RField[] = [];
    const names = new Map<string, number>();
    const properties = new Map<string, string>();
    select.forEach((f, i) => {
      const pointer = `${at}/select/${i}`;
      const expression = this.expression((f.expression as Json | undefined) ?? {}, scope, pointer + "/expression");
      const declared = typeof f.type === "string" ? f.type : null;
      const field: RField = {
        name: typeof f.name === "string" ? f.name : "",
        expression,
        type: declared ?? expression.type,
        nativeType: declared ? this.keywordNativeType(declared) : expression.nativeType,
        codeType: declared ?? expression.codeType,
        nullable: typeof f.nullable === "boolean" ? f.nullable : expression.nullable,
      };
      const key = field.name.toLowerCase();
      const at2 = pointer + "/name";
      if (names.has(key))
        this.add("MQ3001", `Field name '${field.name}' is already used at index ${names.get(key)} of the select list of query '${this.fileName}'.`, at2);
      else {
        names.set(key, i);
        const property = enginePascal(field.name);
        if (isReservedName(field.name))
          this.add("MQ4040", `Field '${field.name}' of query '${this.fileName}' starts with mq_, which generated code reserves for its own names.`, at2);
        else if (!this.inWith && properties.has(property))
          // A row is a record whose properties are the fields' names Pascal-cased.
          this.add(
            "MQ4040",
            `Field '${field.name}' of query '${this.fileName}' becomes property '${property}', as field '${properties.get(property)}' does: give it another name.`,
            pointer + "/name",
          );
        else if (!this.inWith) properties.set(property, field.name);
      }
      fields.push(field);
    });
    return fields;
  }

  private predicate(p: Json, scope: Scope, pointer: string): RPredicate {
    if (Array.isArray(p.and)) return pred("and", { and: arr(p.and).map((x, i) => this.predicate(x, scope, `${pointer}/and/${i}`)) });
    if (Array.isArray(p.or)) return pred("or", { or: arr(p.or).map((x, i) => this.predicate(x, scope, `${pointer}/or/${i}`)) });
    if (p.not && typeof p.not === "object") return pred("not", { not: this.predicate(p.not as Json, scope, pointer + "/not") });
    if (p.exists && typeof p.exists === "object") {
      const query = this.query;
      const nested: RQuery = {
        ...query,
        name: "exists",
        parent: scope.query,
        from: null as unknown as RSource,
        joins: [],
        select: [],
        where: null,
        groupBy: [],
        having: null,
        orderBy: [],
        distinct: false,
        paging: null,
        sql: "",
        uses: [],
      };
      const inner: Scope = { query: nested, outer: scope, aliases: new Map(), sources: [] };
      this.resolveBody(nested, inner, p.exists as Json, pointer + "/exists", false);
      return pred("exists", { exists: nested });
    }
    const op = typeof p.op === "string" ? p.op : "eq";
    const result = pred("compare", {
      op,
      left: p.left && typeof p.left === "object" ? this.expression(p.left as Json, scope, pointer + "/left") : nullExpression(),
    });
    const raw = p.right;
    const right = raw === undefined || raw === null ? [] : Array.isArray(raw) ? arr(raw) : [raw as Json];
    // A list parameter is the whole right side of in or notIn, or nothing (MQ4039).
    const saved = this.listOperand;
    this.listOperand = (op === "in" || op === "notIn") && right.length === 1 ? right[0] : null;
    const values = right.map((x, i) => this.expression(x, scope, right.length === 1 && raw !== undefined ? pointer + "/right" : `${pointer}/right/${i}`));
    this.listOperand = saved;
    switch (op) {
      case "isNull":
      case "isNotNull":
        if (values.length > 0) this.add("MQ4031", `Comparison '${op}' in query '${this.fileName}' takes no right side.`, pointer + "/right");
        break;
      case "in":
      case "notIn":
        if (values.length === 1 && values[0].node === "param" && values[0].param?.collection) result.right = values[0];
        else if (values.length === 0)
          this.add("MQ4031", `Comparison '${op}' in query '${this.fileName}' needs a list of values or a list parameter on its right side.`, pointer);
        else result.values = values;
        break;
      case "between":
        if (values.length !== 2)
          this.add(
            "MQ4031",
            `Comparison 'between' in query '${this.fileName}' needs two values on its right side (the low and the high bound).`,
            values.length === 0 ? pointer : pointer + "/right",
          );
        else result.values = values;
        break;
      default:
        if (values.length !== 1)
          this.add(
            "MQ4031",
            `Comparison '${op}' in query '${this.fileName}' needs one value on its right side.`,
            values.length === 0 ? pointer : pointer + "/right",
          );
        else result.right = values[0];
        break;
    }
    return result;
  }

  private expression(e: Json, scope: Scope, pointer: string): RExpression {
    const listOperand = e === this.listOperand;
    this.listOperand = null; // only the operand itself, not what it holds
    if (typeof e.column === "string") return this.column(e.column, scope, pointer + "/column");
    if (typeof e.param === "string") {
      const parameter = this.parameters.get(e.param);
      if (!parameter) {
        this.add("MQ4024", `Query '${this.fileName}' uses parameter '${e.param}', which it does not declare.`, pointer + "/param");
        return expr("param", { param: { name: e.param } as RParameter });
      }
      if (parameter.collection && !listOperand)
        this.add(
          "MQ4039",
          `Query '${this.fileName}' uses list parameter '${e.param}' as one value: a list parameter is only the whole right side of in or notIn.`,
          pointer + "/param",
        );
      return expr("param", { param: parameter, type: parameter.type, nativeType: parameter.nativeType, codeType: parameter.codeType, nullable: false });
    }
    if (e.value !== undefined && e.value !== null) {
      if (typeof e.type === "string") return this.typedLiteral(e.value, e.type, pointer);
      const value = plain(e.value);
      if (typeof value === "number" && !Number.isFinite(value)) {
        this.add("MQ4043", `Query '${this.fileName}' has a number literal too large to hold.`, pointer + "/value");
        return nullExpression();
      }
      const keyword =
        typeof value === "string"
          ? "string"
          : typeof value === "boolean"
            ? "bool"
            : typeof value === "number"
              ? Number.isInteger(value)
                ? value >= -2147483648 && value <= 2147483647
                  ? "int32"
                  : "int64"
                : "decimal"
              : null;
      return expr("value", { value, type: keyword, codeType: keyword, nativeType: keyword ? this.keywordNativeType(keyword) : null, nullable: false });
    }
    if (e.null === true) return nullExpression();
    if (typeof e.op === "string") {
      const args = arr(e.args).map((a, i) => this.expression(a, scope, `${pointer}/args/${i}`));
      if (args.length < 2 && !(e.op === "-" && args.length === 1))
        this.add(
          "MQ4042",
          `Operation '${e.op}' in query '${this.fileName}' has ${args.length === 0 ? "no operands" : "one operand"}: it needs two or more${e.op === "-" ? " (or one, to negate it)" : ""}.`,
          pointer + "/args",
        );
      const type =
        e.op === "concat"
          ? "string"
          : args.some((a) => a.type === "decimal")
            ? "decimal"
            : args.some((a) => a.type === "double" || a.type === "float")
              ? "double"
              : (args.map((a) => a.type).find((t) => t !== null) ?? null);
      return expr("op", {
        op: e.op,
        args,
        type,
        codeType: type,
        nativeType: type ? this.keywordNativeType(type) : null,
        nullable: args.length === 0 || args.some((a) => a.nullable),
      });
    }
    if (typeof e.call === "string") return this.call(e.call, arr(e.args), scope, pointer);
    if (Array.isArray(e.case)) {
      const whens: RWhen[] = arr(e.case).map((b, i) => ({
        when: this.predicate((b.when as Json | undefined) ?? {}, scope, `${pointer}/case/${i}/when`),
        then: this.expression((b.then as Json | undefined) ?? {}, scope, `${pointer}/case/${i}/then`),
      }));
      const otherwise = e.else && typeof e.else === "object" ? this.expression(e.else as Json, scope, pointer + "/else") : undefined;
      const typed = [...whens.map((w) => w.then), otherwise].find((x) => x?.type);
      return expr("case", {
        case: whens,
        else: otherwise,
        type: typed?.type ?? null,
        nativeType: typed?.nativeType ?? null,
        codeType: typed?.codeType ?? null,
        nullable: !otherwise || otherwise.nullable || whens.some((w) => w.then.nullable),
      });
    }
    if (e.cast && typeof e.cast === "object") {
      const inner = this.expression(e.cast as Json, scope, pointer + "/cast");
      const keyword = typeof e.type === "string" ? e.type : "string";
      // MySQL casts to a short list of types only, which its CAST spells differently from its column types.
      const native = this.view.dialect === "mysql" ? mysqlCastType(keyword, this.keywordNativeType("decimal")) : this.keywordNativeType(keyword);
      return expr("cast", { cast: inner, type: keyword, codeType: keyword, nativeType: native, nullable: inner.nullable });
    }
    if (e.sql && typeof e.sql === "object") {
      const sql = Object.fromEntries(Object.entries(e.sql as Record<string, unknown>).map(([k, v]) => [k, String(v)]));
      const text = sqlFor(sql, this.view.dialect);
      if (text === null)
        this.add(
          "MQ4029",
          `Query '${this.fileName}' has an sql expression without a text for ${this.view.dialect} (database '${this.view.name}') and no "*" text.`,
          pointer + "/sql",
        );
      return expr("sql", { sql, sqlText: text, nullable: true });
    }
    return nullExpression();
  }

  private call(call: string, argumentList: Json[], scope: Scope, pointer: string): RExpression {
    const args = argumentList.map((a, i) => this.expression(a, scope, `${pointer}/args/${i}`));
    const result = expr("call", { call, args });
    const routine = this.routines.get(call);
    if (routine) {
      result.routine = { id: routine.id, name: routine.name, schema: routine.schema };
      this.use(routine.id);
      const returns = routine.returns;
      const dbType = returns?.dbTypeId ? this.types.get(returns.dbTypeId) : undefined;
      result.type = returns?.type ?? null;
      result.nativeType = returns && !returns.table && returns.nativeType ? returns.nativeType : null;
      result.codeType = returns?.type ?? (dbType?.typeKind === "domain" && dbType.base ? dbType.base : null);
      return result;
    }
    if (isId(call)) {
      this.add("MQ4021", `Query '${this.fileName}' calls '${call}', which ${this.whatIsRoutine(call)}.`, pointer + "/call");
      return result;
    }
    if (!FUNCTION_NAME.test(call)) {
      this.add(
        "MQ4034",
        `Query '${this.fileName}' calls '${call}', which is neither a function name (letters, digits and underscores, optionally schema.name) nor a routine id.`,
        pointer + "/call",
      );
      return result;
    }
    const first = args[0];
    const name = call.toLowerCase();
    const typing: Record<string, [string | null, boolean]> = {
      count: ["int64", false],
      sum: [first?.type === "int16" || first?.type === "int32" ? "int64" : (first?.type ?? null), true],
      avg: [first?.type === "double" || first?.type === "float" ? "double" : "decimal", true],
      min: [first?.type ?? null, true],
      max: [first?.type ?? null, true],
      lower: [first?.type ?? "string", first?.nullable ?? true],
      upper: [first?.type ?? "string", first?.nullable ?? true],
      length: ["int32", first?.nullable ?? true],
      coalesce: [args.map((a) => a.type).find((t) => t !== null) ?? null, args.length === 0 || args.every((a) => a.nullable)],
      now: ["datetime", false],
    };
    [result.type, result.nullable] = typing[name] ?? [null, true];
    const typed = ["min", "max", "lower", "upper", "coalesce"].includes(name) ? args.find((a) => a.type === result.type && a.nativeType !== null) : undefined;
    if (typed) {
      result.nativeType = typed.nativeType;
      result.codeType = typed.codeType;
    } else {
      result.nativeType = result.type ? this.keywordNativeType(result.type) : null;
      result.codeType = result.type;
    }
    return result;
  }

  /** A typed literal: a decimal number written as text ({ "value": "2.0", "type": "decimal" }); MQ4043 otherwise. */
  private typedLiteral(value: unknown, type: string, pointer: string): RExpression {
    const text = typeof value === "string" ? value.trim() : String(value);
    if (type !== "decimal" || !DECIMAL.test(text)) {
      this.add(
        "MQ4043",
        `Query '${this.fileName}' has the literal '${text}' typed '${type}': a typed literal is a decimal number (type decimal) written as text.`,
        pointer + (type !== "decimal" ? "/type" : "/value"),
      );
      return nullExpression();
    }
    return expr("value", {
      value: Number(text),
      literalText: decimalText(text),
      type: "decimal",
      codeType: "decimal",
      nativeType: this.keywordNativeType("decimal"),
      nullable: false,
    });
  }

  private whatIsRoutine(id: string): string {
    const element = this.docs.get(id);
    if (!element) return `is no routine of database '${this.view.name}'`;
    if (element.kind === "routine")
      return `is routine '${String(element.name ?? "")}' of database '${String(this.docs.get(String(element.database))?.name ?? element.database)}', not of '${this.view.name}'`;
    return `is ${String(element.kind)} '${String(element.name ?? "")}', not a routine`;
  }

  private column(text: string, scope: Scope, pointer: string): RExpression {
    const dot = text.indexOf(".");
    let source: RSource | null = null;
    let owner: Scope | null = null;
    let name = text;
    const aliased = dot > 0 ? find(scope, text.slice(0, dot)) : null;
    if (aliased) {
      [source, owner] = aliased;
      name = text.slice(dot + 1);
    } else if (dot > 0 && !isId(text.slice(0, dot)) && !text.slice(0, dot).includes("@")) {
      const alias = text.slice(0, dot);
      if (scope.outer === null)
        this.add("MQ4022", `Query '${this.fileName}' names column '${text}', but no source of the query has the alias '${alias}'.`, pointer);
      else
        this.add(
          "MQ4028",
          `A nested query of query '${this.fileName}' names column '${text}', but neither it nor an enclosing query has the alias '${alias}'.`,
          pointer,
        );
      return expr("column", { column: text, alias, columnName: text.slice(dot + 1) });
    }

    let found: { table?: ColumnView; view?: ViewColumn; named?: RField } = {};
    if (source) {
      found = findColumn(source, name);
      const target = sourceTargets.get(source);
      if (!found.table && !found.view && !found.named && source.name.length > 0 && (target?.table || target?.view || target?.named))
        this.add(
          "MQ4023",
          `Query '${this.fileName}' names column '${name}' of '${source.alias}' (${target.named ? "named query" : target.view ? "view" : "table"} '${source.name}'), which has no such column.`,
          pointer,
        );
    } else {
      // No alias: the one source of this query (or of the nearest enclosing one) that has the column.
      for (let s: Scope | null = scope; s && !source; s = s.outer) {
        const matches = s.sources.map((x) => ({ source: x, found: findColumn(x, text) })).filter((m) => m.found.table || m.found.view || m.found.named);
        if (matches.length > 1) {
          this.add(
            "MQ4023",
            `Query '${this.fileName}' names column '${text}' without an alias, and ${matches.map((m) => `'${m.source.alias}'`).join(" and ")} both have one: write alias.column.`,
            pointer,
          );
          return expr("column", { column: text, columnName: text });
        }
        if (matches.length === 1) {
          [source, owner] = [matches[0].source, s];
          found = matches[0].found;
        }
      }
      if (!source) {
        this.add("MQ4023", `Query '${this.fileName}' names column '${text}', which no source of the query has.`, pointer);
        return expr("column", { column: text, columnName: text });
      }
    }

    const result = expr("column", {
      column: text,
      alias: source.alias,
      columnName: found.table?.name ?? found.view?.name ?? found.named?.name ?? name,
      source,
      isOuter: owner !== scope,
    });
    if (found.table) {
      const column = found.table;
      const dbType = column.dbTypeId ? this.types.get(column.dbTypeId) : undefined;
      result.type = column.type;
      result.nativeType = column.nativeType;
      result.codeType = dbType?.typeKind === "domain" && dbType.base ? dbType.base : dbType?.typeKind === "enum" ? "string" : column.type;
      result.nullable = column.nullable || source.optional;
    } else if (found.view) {
      result.type = found.view.type;
      result.nativeType = found.view.nativeType;
      result.codeType = found.view.type;
      result.nullable = found.view.nullable || source.optional;
    } else if (found.named) {
      result.type = found.named.type;
      result.nativeType = found.named.nativeType;
      result.codeType = found.named.codeType;
      result.nullable = found.named.nullable || source.optional;
    }
    return result;
  }
}

/** Whether a repeating part has a nested query (an exists) in its joins' conditions, select list or where. */
function hasNested(part: RQuery): boolean {
  const inPredicate = (p: RPredicate): boolean =>
    !!p.exists ||
    p.and.some(inPredicate) ||
    p.or.some(inPredicate) ||
    (!!p.not && inPredicate(p.not)) ||
    [...p.values, p.left, p.right].some((e) => !!e && inExpression(e));
  const inExpression = (e: RExpression): boolean =>
    e.args.some(inExpression) ||
    (!!e.cast && inExpression(e.cast)) ||
    e.case.some((w) => inPredicate(w.when) || inExpression(w.then)) ||
    (!!e.else && inExpression(e.else));
  return (
    part.joins.some((j) => !!j.on && inPredicate(j.on)) || part.select.some((f) => inExpression(f.expression)) || (!!part.where && inPredicate(part.where))
  );
}

function find(scope: Scope, alias: string): [RSource, Scope] | null {
  for (let s: Scope | null = scope; s; s = s.outer) {
    const source = s.aliases.get(alias);
    if (source) return [source, s];
  }
  return null;
}

/**
 * A column of a source by physical name, column key or attribute id; else by name ignoring case, then ignoring case and
 * underscores, when only one column matches (so a change of the conventions' column case keeps a query valid).
 */
function findColumn(source: RSource, name: string): { table?: ColumnView; view?: ViewColumn; named?: RField } {
  const target = sourceTargets.get(source);
  if (target?.named) {
    const column = target.named.columns.find((c) => c.name === name) ?? loose(target.named.columns, (c) => c.name, name);
    return column ? { named: column } : {};
  }
  if (target?.table) {
    const columns = target.table.columns;
    const column = columns.find((c) => c.name === name) ?? columns.find((c) => c.key === name) ?? loose(columns, (c) => c.name, name);
    return column ? { table: column } : {};
  }
  if (target?.view) {
    const column = target.view.columns.find((c) => c.name === name) ?? loose(target.view.columns, (c) => c.name, name);
    return column ? { view: column } : {};
  }
  return {};
}

function loose<T>(columns: readonly T[], nameOf: (c: T) => string, name: string): T | undefined {
  const ignoringCase = columns.filter((c) => nameOf(c).toLowerCase() === name.toLowerCase());
  if (ignoringCase.length > 0) return ignoringCase.length === 1 ? ignoringCase[0] : undefined;
  const fold = (text: string) => text.replaceAll("_", "").toUpperCase();
  const matches = columns.filter((c) => fold(nameOf(c)) === fold(name));
  return matches.length === 1 ? matches[0] : undefined;
}
