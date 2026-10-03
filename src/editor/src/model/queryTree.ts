// A query as data (schemas/v1/query.json, engine-design.md section 7, "Queries"): the types of its trees, constructors, short
// labels for the editor's cells, the checks the editor runs before it writes, and the edits that need the database view (the
// sources a query may read, the columns of an alias, a join's condition from a foreign key, the select list filled from same-named
// columns). Pure: the query editor (editors/database/Query*.tsx) renders these and writes the document through the draft.
import type { ColumnView, DatabaseView, TableView, ViewView } from "@/api/types";

export type DialectMap = Partial<Record<"postgresql" | "sqlserver" | "mysql" | "sqlite" | "oracle" | "*", string>>;

export const ARITHMETIC_OPS = ["+", "-", "*", "/", "%", "concat"] as const;
export type ArithmeticOp = (typeof ARITHMETIC_OPS)[number];

/** Exactly one of its forms (query.json $defs/expression). */
export interface Expression {
  column?: string;
  param?: string;
  value?: string | number | boolean;
  null?: true;
  op?: ArithmeticOp;
  call?: string;
  args?: Expression[];
  case?: CaseBranch[];
  else?: Expression;
  cast?: Expression;
  type?: string;
  sql?: DialectMap;
}

export interface CaseBranch {
  when: Predicate;
  then: Expression;
}

export const COMPARISON_OPS = ["eq", "ne", "lt", "le", "gt", "ge", "like", "ilike", "in", "notIn", "between", "isNull", "isNotNull"] as const;
export type ComparisonOp = (typeof COMPARISON_OPS)[number];

/** Exactly one of its forms (query.json $defs/predicate). */
export interface Predicate {
  and?: Predicate[];
  or?: Predicate[];
  not?: Predicate;
  op?: ComparisonOp;
  left?: Expression;
  right?: Expression | Expression[];
  exists?: Subquery;
}

export const JOIN_KINDS = ["inner", "left", "right", "full", "cross"] as const;
export type JoinKind = (typeof JOIN_KINDS)[number];

export interface QuerySource {
  source: string;
  alias?: string;
}

export interface Join extends QuerySource {
  kind?: JoinKind;
  on?: Predicate;
}

export interface Field {
  name?: string;
  attribute?: string;
  type?: string;
  nullable?: boolean;
  expression: Expression;
}

export interface Order {
  expression: Expression;
  direction?: "asc" | "desc";
  nulls?: "first" | "last";
}

export interface Paging {
  offset?: string | number;
  limit?: string | number;
}

/** A nested query (a collection's, an exists condition's), and the body of a query. */
export interface Subquery {
  from: QuerySource;
  joins?: Join[];
  select?: Field[];
  where?: Predicate;
  groupBy?: Expression[];
  having?: Predicate;
  orderBy?: Order[];
  distinct?: boolean;
}

export interface QueryParameter {
  name: string;
  type: string;
  length?: number;
  precision?: number;
  scale?: number;
  collection?: boolean;
  default?: string | number | boolean;
  description?: string;
}

export interface Collection {
  attribute: string;
  entity?: string;
  query: Subquery;
}

export interface QueryDoc extends Subquery {
  kind: "query";
  id: string;
  name: string;
  database: string;
  entity?: string;
  parameters?: QueryParameter[];
  select: Field[];
  paging?: Paging;
  collections?: Collection[];
  [member: string]: unknown;
}

// ------------------------------------------------------------------ forms

export type ExpressionKind = "column" | "param" | "value" | "null" | "op" | "call" | "case" | "cast" | "sql";
export const EXPRESSION_KINDS: readonly ExpressionKind[] = ["column", "param", "value", "null", "call", "op", "cast", "case", "sql"];
export const EXPRESSION_KIND_LABELS: Record<ExpressionKind, string> = {
  column: "Column",
  param: "Parameter",
  value: "Value",
  null: "Null",
  call: "Function",
  op: "Operation",
  cast: "Conversion",
  case: "Case",
  sql: "SQL per dialect",
};

/** The form of an expression (the first member present, in the schema's order), or null for none. */
export function expressionKind(e: Expression | undefined): ExpressionKind | null {
  if (!e) return null;
  if (e.column !== undefined) return "column";
  if (e.param !== undefined) return "param";
  if (e.value !== undefined) return "value";
  if (e.null !== undefined) return "null";
  if (e.op !== undefined) return "op";
  if (e.call !== undefined) return "call";
  if (e.case !== undefined) return "case";
  if (e.cast !== undefined) return "cast";
  if (e.sql !== undefined) return "sql";
  return null;
}

export type PredicateKind = "and" | "or" | "not" | "compare" | "exists";

export function predicateKind(p: Predicate | undefined): PredicateKind | null {
  if (!p) return null;
  if (p.and) return "and";
  if (p.or) return "or";
  if (p.not) return "not";
  if (p.exists) return "exists";
  if (p.op !== undefined || p.left !== undefined) return "compare";
  return null;
}

export const COMPARISON_LABELS: Record<ComparisonOp, string> = {
  eq: "=",
  ne: "≠",
  lt: "<",
  le: "≤",
  gt: ">",
  ge: "≥",
  like: "like",
  ilike: "like (any case)",
  in: "in",
  notIn: "not in",
  between: "between",
  isNull: "is null",
  isNotNull: "is not null",
};

/** What a comparison takes on its right: nothing, one value, a list (or one list parameter), or the two bounds. */
export type RightShape = "none" | "one" | "list" | "two";

export function rightShape(op: ComparisonOp | undefined): RightShape {
  switch (op) {
    case "isNull":
    case "isNotNull":
      return "none";
    case "in":
    case "notIn":
      return "list";
    case "between":
      return "two";
    default:
      return "one";
  }
}

/** The right side as a list of expressions (one expression reads as a list of one). */
export function rightValues(p: Predicate): Expression[] {
  if (p.right === undefined) return [];
  return Array.isArray(p.right) ? p.right : [p.right];
}

// ------------------------------------------------------------------ constructors

export const column = (alias: string, key: string): Expression => ({ column: `${alias}.${key}` });
export const param = (name: string): Expression => ({ param: name });
export const literal = (value: string | number | boolean): Expression => ({ value });
export const nullValue = (): Expression => ({ null: true });
export const call = (name: string, ...args: Expression[]): Expression => (args.length ? { call: name, args } : { call: name });
export const operation = (op: ArithmeticOp, ...args: Expression[]): Expression => ({ op, args });
export const cast = (operand: Expression, type: string): Expression => ({ cast: operand, type });
export const sqlText = (texts: DialectMap): Expression => ({ sql: texts });
export const and = (...items: Predicate[]): Predicate => ({ and: items });
export const or = (...items: Predicate[]): Predicate => ({ or: items });
export const not = (p: Predicate): Predicate => ({ not: p });

/** A comparison; the right side as the operator takes it (a list of one written as the expression, as the canonical writer does). */
export function compare(op: ComparisonOp, left: Expression, right?: Expression | Expression[]): Predicate {
  const p: Predicate = { op, left };
  if (right !== undefined && rightShape(op) !== "none") p.right = Array.isArray(right) && right.length === 1 ? right[0] : right;
  return p;
}

/**
 * A comparison with another operator: the right side keeps what it can (the first value for one, the values for a list, two
 * bounds from what there is), and gets `fill` where it lacks one.
 */
export function withComparison(p: Predicate, op: ComparisonOp, fill: () => Expression): Predicate {
  const values = rightValues(p);
  const left = p.left ?? fill();
  switch (rightShape(op)) {
    case "none":
      return { op, left };
    case "one":
      return compare(op, left, values[0] ?? fill());
    case "list":
      return compare(op, left, values.length ? values : [fill()]);
    case "two":
      return { op, left, right: [values[0] ?? fill(), values[1] ?? fill()] };
  }
}

/**
 * An expression of another form, built from the current one where it can: a function or an operation wraps it, a conversion
 * converts it, a column or a parameter starts on `defaults`' first choice.
 */
export function withExpressionKind(
  current: Expression | undefined,
  kind: ExpressionKind,
  defaults: { column?: string; param?: string; dialect?: string },
): Expression {
  const inner = current && expressionKind(current) ? current : undefined;
  switch (kind) {
    case "column":
      return { column: defaults.column ?? (current?.column || "") };
    case "param":
      return { param: defaults.param ?? (current?.param || "") };
    case "value":
      return { value: current?.value ?? "" };
    case "null":
      return nullValue();
    case "call":
      return inner ? call("lower", inner) : call("count");
    case "op":
      return operation("+", inner ?? literal(0), literal(1));
    case "cast":
      return cast(inner ?? literal(""), "string");
    case "case":
      return { case: [{ when: compare("isNotNull", inner ?? nullValue()), then: inner ?? literal("") }], else: nullValue() };
    case "sql":
      // NULL until the SQL is written: a valid expression in every dialect, never a label of the tree it replaces.
      return sqlText({ [defaults.dialect ?? "*"]: "NULL" } as DialectMap);
  }
}

/** A literal typed in a cell: true and false are booleans, a number is a number, a text in single quotes is that text. */
export function parseLiteral(text: string): string | number | boolean {
  const t = text.trim();
  if (t === "true") return true;
  if (t === "false") return false;
  if (/^-?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$/.test(t)) return Number(t);
  if (/^'.*'$/s.test(t)) return t.slice(1, -1);
  return text;
}

/** A number with a fractional point and digits on both sides (`2.0`, `-0.50`): its digits matter, so it is a typed literal. */
const DECIMAL_TEXT = /^-?\d+\.\d+$/;

/**
 * The value expression a literal typed in a cell writes. A number with a fractional point keeps the digits as typed, so `2.0`
 * stays `2.0` (a JSON number would read back as 2): `{ "value": "2.0", "type": "decimal" }`, the engine's typed decimal literal,
 * with leading zeros of the whole part dropped (`007.50` is `7.50`, as SQL writes it). Any other text is parseLiteral's.
 */
export function literalExpression(text: string): Expression {
  const t = text.trim();
  if (DECIMAL_TEXT.test(t)) {
    const negative = t.startsWith("-");
    const digits = (negative ? t.slice(1) : t).replace(/^0+(?=\d)/, "");
    return { value: (negative ? "-" : "") + digits, type: "decimal" };
  }
  return { value: parseLiteral(text) };
}

/** Whether a value expression is a typed decimal literal (`{ "value": "2.0", "type": "decimal" }`). */
export const isDecimalLiteral = (e: Expression | undefined): boolean => e?.type === "decimal" && typeof e.value === "string";

/** A literal as a cell shows it (a text that would read as a number or a boolean is quoted). */
export function literalText(value: string | number | boolean | undefined): string {
  if (value === undefined) return "";
  if (typeof value !== "string") return String(value);
  return typeof parseLiteral(value) === "string" && !/^'.*'$/s.test(value) ? value : `'${value}'`;
}

/** A value expression as its cell shows it: a typed decimal literal as its digits, any other literal as literalText does. */
export const valueText = (e: Expression | undefined): string => (isDecimalLiteral(e) ? String(e!.value) : literalText(e?.value));

// ------------------------------------------------------------------ labels

/** A column reference's alias and the rest (`i.number` → i, number); no dot: no alias. */
export function splitColumn(text: string): { alias: string | null; name: string } {
  const dot = text.indexOf(".");
  return dot > 0 ? { alias: text.slice(0, dot), name: text.slice(dot + 1) } : { alias: null, name: text };
}

const ARITH_SYMBOL: Record<ArithmeticOp, string> = { "+": "+", "-": "-", "*": "*", "/": "/", "%": "%", concat: "||" };

/**
 * A compact label: `i.number`, `@customerId`, `'I'`, `null`, `lower(c.name)`, `count(*)`, `(a + b)`, `cast(x as date)`,
 * `case…`, `sql (postgresql, sqlite)`. `columnName` turns a column reference into its physical name when it can.
 */
export function describe(e: Expression | undefined, columnName: (text: string) => string = (t) => t): string {
  switch (expressionKind(e)) {
    case "column":
      return columnName(e!.column!);
    case "param":
      return `@${e!.param}`;
    case "value":
      return typeof e!.value === "string" && !isDecimalLiteral(e) ? `'${e!.value}'` : String(e!.value);
    case "null":
      return "null";
    case "op": {
      const args = (e!.args ?? []).map((a) => describe(a, columnName));
      if (args.length === 1) return `(${e!.op === "-" ? "-" : ""}${args[0]})`;
      return `(${args.join(` ${ARITH_SYMBOL[e!.op!]} `)})`;
    }
    case "call": {
      const args = (e!.args ?? []).map((a) => describe(a, columnName));
      return `${e!.call}(${args.length || e!.call!.toLowerCase() !== "count" ? args.join(", ") : "*"})`;
    }
    case "case":
      return `case (${e!.case!.length} ${e!.case!.length === 1 ? "branch" : "branches"})`;
    case "cast":
      return `cast(${describe(e!.cast, columnName)} as ${e!.type ?? "string"})`;
    case "sql":
      return `sql (${Object.keys(e!.sql ?? {}).join(", ")})`;
    default:
      return "?";
  }
}

const COMPARE_SQL: Partial<Record<ComparisonOp, string>> = { eq: "=", ne: "<>", lt: "<", le: "<=", gt: ">", ge: ">=", like: "like", ilike: "ilike" };

/** A compact label of a condition: `i.status = 'I'`, `x in (@ids)`, `all of (3)`, `not (…)`, `exists (invoices)`. */
export function describePredicate(p: Predicate | undefined, columnName?: (text: string) => string): string {
  switch (predicateKind(p)) {
    case "and":
      return `all of (${p!.and!.length})`;
    case "or":
      return `any of (${p!.or!.length})`;
    case "not":
      return `not (${describePredicate(p!.not, columnName)})`;
    case "exists":
      return `exists (${p!.exists!.from.alias ?? p!.exists!.from.source})`;
    case "compare": {
      const left = describe(p!.left, columnName);
      const values = rightValues(p!).map((v) => describe(v, columnName));
      switch (rightShape(p!.op)) {
        case "none":
          return `${left} ${p!.op === "isNull" ? "is null" : "is not null"}`;
        case "list":
          return `${left} ${p!.op === "notIn" ? "not in" : "in"} (${values.join(", ")})`;
        case "two":
          return `${left} between ${values[0] ?? "?"} and ${values[1] ?? "?"}`;
        default:
          return `${left} ${COMPARE_SQL[p!.op ?? "eq"] ?? p!.op} ${values[0] ?? "?"}`;
      }
    }
    default:
      return "?";
  }
}

// ------------------------------------------------------------------ checks

/** Why an expression cannot be written as it is, or null: exactly one form, with what that form needs (query.json). */
export function expressionProblem(e: Expression | undefined): string | null {
  if (!e) return "An expression is missing.";
  const forms = (["column", "param", "value", "null", "op", "call", "case", "cast", "sql"] as const).filter((k) => e[k] !== undefined);
  if (forms.length !== 1) return forms.length ? `An expression has one form, not ${forms.join(" and ")}.` : "An expression is missing.";
  switch (forms[0]) {
    case "column":
      return e.column ? null : "Pick a column.";
    case "param":
      return /^[A-Za-z_][A-Za-z0-9_]*$/.test(e.param ?? "") ? null : "Pick a parameter.";
    case "value":
      return e.type === undefined || (e.type === "decimal" && typeof e.value === "string" && DECIMAL_TEXT.test(e.value))
        ? null
        : "A typed value is a decimal number written as text.";
    case "op": {
      const args = e.args ?? [];
      if (args.length < 2 && !(e.op === "-" && args.length === 1))
        return e.op === "-" ? "A subtraction needs two operands, or one to negate." : "An operation needs at least two operands.";
      return args.map(expressionProblem).find((x) => x) ?? null;
    }
    case "call":
      if (!e.call) return "Name the function.";
      if (!/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/.test(e.call) && !/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$/.test(e.call))
        return "A function name is letters, digits and underscores (optionally schema.name).";
      return (e.args ?? []).map(expressionProblem).find((x) => x) ?? null;
    case "cast":
      return e.type ? expressionProblem(e.cast) : "A conversion needs a type.";
    case "sql":
      return Object.values(e.sql ?? {}).some((t) => typeof t === "string" && t.trim() !== "") ? null : "Write the SQL for at least one dialect (or * for any).";
    case "case":
      return (e.case ?? []).length ? null : "A case needs at least one branch.";
    default:
      return null;
  }
}

/** Why a condition cannot be written as it is, or null: a group has a condition, a comparison its right side's shape. */
export function predicateProblem(p: Predicate | undefined): string | null {
  switch (predicateKind(p)) {
    case "and":
    case "or": {
      const items = (p!.and ?? p!.or)!;
      return items.length ? (items.map(predicateProblem).find((x) => x) ?? null) : "A group needs at least one condition.";
    }
    case "not":
      return predicateProblem(p!.not);
    case "exists":
      return p!.exists!.from?.source ? null : "A nested query needs a source.";
    case "compare": {
      const left = expressionProblem(p!.left);
      if (left) return left;
      const values = rightValues(p!);
      switch (rightShape(p!.op)) {
        case "none":
          return values.length ? `'${p!.op}' takes no right side.` : null;
        case "two":
          if (values.length !== 2) return "between needs two values: the low and the high bound.";
          break;
        case "list":
          if (!values.length) return `'${p!.op}' needs a list of values or a list parameter.`;
          break;
        default:
          if (values.length !== 1) return `'${p!.op ?? "eq"}' needs one value on its right side.`;
      }
      return values.map(expressionProblem).find((x) => x) ?? null;
    }
    default:
      return "A condition is missing.";
  }
}

/** Why a parameter or an alias name cannot be used, or null: an identifier, not used twice. */
export function nameProblem(name: string, taken: readonly string[], noun: string): string | null {
  const n = name.trim();
  if (!n) return `${/^[aeiou]/.test(noun) ? "An" : "A"} ${noun} needs a name.`;
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(n)) return "Use letters, digits and underscores, not starting with a digit.";
  if (taken.some((t) => t === n)) return `${n} is already a ${noun} of this query.`;
  return null;
}

// ------------------------------------------------------------------ edits across the document

/** Visits every expression of a tree (predicates, nested queries, case branches), depth first. */
export function forEachExpression(node: unknown, visit: (e: Expression) => void): void {
  if (Array.isArray(node)) return node.forEach((x) => forEachExpression(x, visit));
  if (!node || typeof node !== "object") return;
  const rec = node as Record<string, unknown>;
  if (expressionKind(rec as Expression)) visit(rec as Expression);
  for (const value of Object.values(rec)) if (value && typeof value === "object") forEachExpression(value, visit);
}

/** Renames a parameter and every use of it (expressions, paging); false when the new name is taken. */
export function renameParameter(doc: QueryDoc, from: string, to: string): boolean {
  const parameters = doc.parameters ?? [];
  if (from === to) return true;
  if (parameters.some((p) => p.name === to)) return false;
  const target = parameters.find((p) => p.name === from);
  if (!target) return false;
  target.name = to;
  for (const member of ["select", "joins", "where", "groupBy", "having", "orderBy", "collections"] as const)
    forEachExpression(doc[member], (e) => {
      if (e.param === from) e.param = to;
    });
  if (doc.paging) for (const bound of ["offset", "limit"] as const) if (doc.paging[bound] === from) doc.paging[bound] = to;
  return true;
}

/**
 * Renames an alias and every column reference through it in a query body, and in its collections' nested queries that do not
 * declare the alias themselves (their correlation names the parent's aliases).
 */
export function renameAlias(body: Subquery & { collections?: Collection[] }, from: string, to: string): void {
  const rewrite = (node: unknown) =>
    forEachExpression(node, (e) => {
      if (e.column && splitColumn(e.column).alias === from) e.column = `${to}${e.column.slice(from.length)}`;
    });
  for (const member of ["select", "joins", "where", "groupBy", "having", "orderBy"] as const) rewrite(body[member]);
  for (const c of body.collections ?? []) if (![c.query.from, ...(c.query.joins ?? [])].some((s) => s.alias === from)) rewrite(c.query);
  if (body.from.alias === from) body.from.alias = to;
  for (const j of body.joins ?? []) if (j.alias === from) j.alias = to;
}

/**
 * Sets the result entity, or none (an ad hoc row). A field that names what the new entity does not have (any attribute, with
 * none) becomes a named field of the row: it keeps its expression, and takes its own name, else the old attribute's (numbered when
 * another field has it), so nothing is lost and nothing names an attribute the entity lacks. A field the new entity has keeps it.
 * `attributeName` names an attribute of the current entity; `has` tells whether the new entity has what a field's attribute names
 * (an attribute, a value object member, a foreign key's relation end).
 */
export function setResultEntity(
  doc: QueryDoc,
  entity: string | null,
  attributeName: (id: string) => string | undefined,
  has: (attribute: string) => boolean = () => false,
): void {
  if (entity) doc.entity = entity;
  else delete doc.entity;
  const taken = doc.select.map((f) => f.name ?? (f.attribute && (!entity || !has(f.attribute)) ? "" : (attributeName(f.attribute ?? "") ?? "")));
  doc.select = doc.select.map((f, i) => {
    if (!f.attribute || (entity && has(f.attribute))) return f;
    const { attribute, ...rest } = f;
    if (rest.name) return rest;
    const base = attributeName(attribute) ?? fieldName(attribute.replace(/\./g, "_"));
    const used = new Set(taken.filter((_, j) => j !== i).map((t) => t.toLowerCase()));
    let name = base;
    for (let n = 2; used.has(name.toLowerCase()); n++) name = `${base}${n}`;
    taken[i] = name;
    return { ...rest, name };
  });
}

/** Whether a query still names an older result entity (its own, or a collection's elements'). */
export const hasResultEntity = (doc: Pick<QueryDoc, "entity" | "collections">): boolean => !!doc.entity || (doc.collections ?? []).some((c) => !!c.entity);

/**
 * Drops an older query's result entities (the query's `entity` and each collection's element entity): a query is the database's,
 * and the entity side binds an entity to it instead. Every field that filled an attribute becomes a named field with its
 * expression (setResultEntity), and a collection that filled a collection attribute or a navigation takes its name
 * (`collectionName`, made a unique field name). `attributeName` names the query's attributes; `elementName(i)` the attributes of
 * collection i's elements. Nothing is lost: each value keeps its expression under a name.
 */
export function removeResultEntities(
  doc: QueryDoc,
  attributeName: (id: string) => string | undefined,
  collectionName: (attribute: string) => string | undefined,
  elementName: (index: number) => (id: string) => string | undefined,
): void {
  setResultEntity(doc, null, attributeName);
  const taken: string[] = [];
  (doc.collections ?? []).forEach((c, i) => {
    const named = collectionName(c.attribute);
    if (named) c.attribute = fieldName(named, taken);
    taken.push(c.attribute);
    if (c.query.select) setResultEntity(c.query as QueryDoc, null, elementName(i));
    delete c.entity;
  });
}

/** Removes an empty optional list member (the canonical writer leaves out empty defaults). */
export function tidy(body: Subquery): void {
  for (const member of ["joins", "groupBy", "orderBy"] as const) if (body[member] && !body[member]!.length) delete body[member];
  if (body.distinct === false) delete body.distinct;
}

// ------------------------------------------------------------------ the database a query reads

/** A source a query may read: a table (by its key) or a view (by its id), with its columns. */
export interface SourceOption {
  /** What `source` is written as: a table's key (a designed table's file id, `<entity id>@<database id>`) or a view's id. */
  value: string;
  name: string;
  schema: string | null;
  kind: "table" | "view";
  table?: TableView;
  view?: ViewView;
}

/** The tables and views of a database a query may read, by name. */
export function sourceOptions(view: DatabaseView | null | undefined): SourceOption[] {
  if (!view) return [];
  const tables: SourceOption[] = view.tables.map((t) => ({ value: t.key, name: t.name, schema: t.schema, kind: "table", table: t }));
  const views: SourceOption[] = view.views.map((v) => ({ value: v.id, name: v.name, schema: v.schema, kind: "view", view: v }));
  return [...tables, ...views].sort((a, b) => a.name.localeCompare(b.name) || a.value.localeCompare(b.value));
}

/** The table files of a database the resolver reads a source id through: a synthesized table's overlay names its entity. */
export interface TableFileRow {
  id: string;
  entity?: string | null;
  relation?: string | null;
}

/**
 * The table or view a `source` names, as the resolver finds it: a table key, a table file's id (a designed table's id is its key,
 * an overlay's is its entity's or relation's table), a view id, or an entity id (its table in this database).
 */
export function findSource(view: DatabaseView | null | undefined, source: string, files: readonly TableFileRow[] = []): SourceOption | null {
  if (!view) return null;
  const options = sourceOptions(view);
  const direct = options.find((o) => o.value === source);
  if (direct) return direct;
  const file = files.find((f) => f.id === source);
  const owner = file?.entity ?? file?.relation;
  if (owner) return options.find((o) => o.value === `${owner}@${view.id}`) ?? null;
  return options.find((o) => o.kind === "table" && o.value === `${source}@${view.id}`) ?? null;
}

/** A column of a source as the editor offers it: the canonical text after the alias and the physical name. */
export interface ColumnOption {
  /** What follows `alias.` in a reference: the column's key (a table's), else its name (a view's). */
  ref: string;
  name: string;
  type: string | null;
  column?: ColumnView;
}

export function columnOptions(source: SourceOption | null): ColumnOption[] {
  if (!source) return [];
  if (source.table) return source.table.columns.map((c) => ({ ref: c.key, name: c.name, type: c.type, column: c }));
  return (source.view?.columns ?? []).map((c) => ({ ref: c.name, name: c.name, type: c.type ?? null }));
}

const fold = (s: string) => s.replace(/_/g, "").toUpperCase();

/**
 * The column a reference's part after the alias names, as the resolver matches it: the physical name, the key, the attribute id,
 * then ignoring case, then ignoring case and underscores, when only one column matches.
 */
export function matchColumn(columns: readonly ColumnOption[], name: string): ColumnOption | null {
  const exact = columns.find((c) => c.name === name) ?? columns.find((c) => c.ref === name) ?? columns.find((c) => c.column?.attributeId === name);
  if (exact) return exact;
  const ignoringCase = columns.filter((c) => c.name.toLowerCase() === name.toLowerCase());
  if (ignoringCase.length) return ignoringCase.length === 1 ? ignoringCase[0] : null;
  const folded = columns.filter((c) => fold(c.name) === fold(name));
  return folded.length === 1 ? folded[0] : null;
}

/** One alias in scope of an expression: the alias, what it reads, and whether it belongs to a query around this one. */
export interface ScopeAlias {
  alias: string;
  source: SourceOption | null;
  columns: ColumnOption[];
  outer: boolean;
}

/** The aliases a query body declares (from, then its joins), followed by those of the queries around it (`outer`). */
export function scopeOf(
  body: Pick<Subquery, "from" | "joins">,
  view: DatabaseView | null | undefined,
  files: readonly TableFileRow[] = [],
  outer: readonly ScopeAlias[] = [],
): ScopeAlias[] {
  const own = [body.from, ...(body.joins ?? [])].map((s) => {
    const source = findSource(view, s.source, files);
    return { alias: s.alias ?? source?.name ?? s.source, source, columns: columnOptions(source), outer: false };
  });
  return [...own, ...outer.filter((o) => !own.some((x) => x.alias === o.alias)).map((o) => ({ ...o, outer: true }))];
}

/** The column a reference names in a scope (alias.part, or an unaliased name one alias has), with its alias. */
export function resolveColumn(scope: readonly ScopeAlias[], text: string): { alias: ScopeAlias; column: ColumnOption } | null {
  const { alias, name } = splitColumn(text);
  if (alias) {
    const a = scope.find((s) => s.alias === alias);
    const c = a ? matchColumn(a.columns, name) : null;
    return a && c ? { alias: a, column: c } : null;
  }
  const hits = scope.filter((s) => !s.outer).flatMap((s) => (matchColumn(s.columns, text) ? [{ alias: s, column: matchColumn(s.columns, text)! }] : []));
  return hits.length === 1 ? hits[0] : null;
}

/** A column reference as a label shows it: `alias.physical_name` when it resolves, else as written. */
export function columnLabel(scope: readonly ScopeAlias[], text: string): string {
  const hit = resolveColumn(scope, text);
  return hit ? `${hit.alias.alias}.${hit.column.name}` : text;
}

/** The canonical text of a column: `alias.<column key>` (a view's column by name). */
export const columnRef = (alias: string, c: ColumnOption): string => `${alias}.${c.ref}`;

const SHORT_KEYWORDS = new Set(["as", "at", "by", "do", "if", "in", "is", "of", "on", "or", "to", "and", "not", "for", "all", "any", "end", "asc"]);

/** An alias for a source: the first letter of each word of its name (`invoice_lines` → il), numbered when taken. */
export function defaultAlias(name: string, taken: readonly string[] = []): string {
  const words = name
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean);
  let base = words
    .map((w) => w[0])
    .join("")
    .toLowerCase();
  if (!/^[a-z_]/.test(base)) base = `t${base}`;
  if (!base) base = "t";
  const used = new Set(taken);
  if (!used.has(base) && !SHORT_KEYWORDS.has(base)) return base;
  for (let n = 2; ; n++) if (!used.has(`${base}${n}`)) return `${base}${n}`;
}

/** A join condition from a foreign key between two aliases: the label and the predicate. */
export interface ForeignKeyJoin {
  label: string;
  on: Predicate;
}

/**
 * The join conditions the foreign keys between a joined alias and the aliases before it give: the joined table's foreign keys
 * to an earlier one's table, and an earlier table's foreign keys to the joined one, in that order. Each column pair is one
 * equality (several make an `and`).
 */
export function foreignKeyJoins(joined: ScopeAlias, earlier: readonly ScopeAlias[]): ForeignKeyJoin[] {
  const out: ForeignKeyJoin[] = [];
  const table = joined.source?.table;
  if (!table) return out;
  const make = (from: ScopeAlias, fromTable: TableView, to: ScopeAlias, toTable: TableView, fk: TableView["foreignKeys"][number]) => {
    const pairs = fk.columns.map((c, i) => [c, fk.referencedColumns[i]] as const).filter(([a, b]) => a && b);
    if (!pairs.length) return;
    const name = (t: TableView, key: string) => t.columns.find((c) => c.key === key)?.name ?? key;
    const equalities = pairs.map(([a, b]) => compare("eq", column(from.alias, a), column(to.alias, b)));
    out.push({
      label: `${pairs.map(([a, b]) => `${from.alias}.${name(fromTable, a)} = ${to.alias}.${name(toTable, b)}`).join(" and ")} (${fk.name})`,
      on: equalities.length === 1 ? equalities[0] : and(...equalities),
    });
  };
  for (const other of earlier) {
    const otherTable = other.source?.table;
    if (!otherTable) continue;
    for (const fk of table.foreignKeys) if (fk.referencedTable === otherTable.key) make(joined, table, other, otherTable, fk);
  }
  for (const other of earlier) {
    const otherTable = other.source?.table;
    if (!otherTable) continue;
    for (const fk of otherTable.foreignKeys) if (fk.referencedTable === table.key) make(other, otherTable, joined, table, fk);
  }
  return out;
}

/** An attribute a select list may fill. */
export interface AttributeOption {
  id: string;
  name: string;
}

/**
 * Fills the select list from the columns of an alias: each attribute without a field gets the column that stores it (its
 * attribute id) or, failing that, the column whose name matches the attribute's ignoring case and underscores. Returns the
 * names of the attributes filled; the fields keep the attributes' order.
 */
export function fillFromColumns(select: Field[], attributes: readonly AttributeOption[], alias: ScopeAlias): { select: Field[]; filled: string[] } {
  const filled: string[] = [];
  const has = new Set(select.map((f) => f.attribute).filter(Boolean));
  let next = [...select];
  for (const a of attributes) {
    if (has.has(a.id)) continue;
    // An attribute by the column that stores it; a value object member or a foreign key by its column key; else by name.
    const keyed = alias.columns.filter((x) => x.ref.startsWith(a.id + "."));
    const c =
      alias.columns.find((x) => x.column?.attributeId === a.id || x.ref === a.id) ??
      (keyed.length === 1 ? keyed[0] : undefined) ??
      alias.columns.find((x) => fold(x.name) === fold(a.name));
    if (!c) continue;
    next = withAttributeField(next, attributes, { attribute: a.id, expression: { column: columnRef(alias.alias, c) } });
    filled.push(a.name);
  }
  return { select: next, filled };
}

/** Adds a field for an attribute in the attributes' order (before the first field of a later attribute), or replaces its field. */
export function withAttributeField(select: readonly Field[], attributes: readonly AttributeOption[], field: Field): Field[] {
  const at = select.findIndex((f) => f.attribute === field.attribute);
  if (at >= 0) return select.map((f, i) => (i === at ? field : f));
  const order = new Map(attributes.map((a, i) => [a.id, i]));
  const mine = order.get(field.attribute ?? "") ?? Number.MAX_SAFE_INTEGER;
  const before = select.findIndex((f) => f.attribute !== undefined && (order.get(f.attribute) ?? -1) > mine);
  const next = [...select];
  next.splice(before < 0 ? next.length : before, 0, field);
  return next;
}

/** A name for an ad hoc field from a column name (`issued_on` → issuedOn), not one of `taken`. */
export function fieldName(columnName: string, taken: readonly string[] = []): string {
  const words = columnName.split(/[^A-Za-z0-9]+/).filter(Boolean);
  let base = words.map((w, i) => (i === 0 ? w.charAt(0).toLowerCase() + w.slice(1) : w.charAt(0).toUpperCase() + w.slice(1))).join("") || "value";
  if (!/^[A-Za-z_]/.test(base)) base = `f${base}`;
  const used = new Set(taken.map((t) => t.toLowerCase()));
  if (!used.has(base.toLowerCase())) return base;
  for (let n = 2; ; n++) if (!used.has(`${base}${n}`.toLowerCase())) return `${base}${n}`;
}
