// Creating what lives in a database (explorer-redesign.md 1.8): the New actions a database offers (its explorer row's menu,
// the explorer's New menu while a database or anything inside it is selected, the Database screen's New button), their
// labels, the checks the New dialogs run and the documents they create. Pure: NewDatabaseObjectDialog.tsx renders the dialogs
// and saves the result through the create endpoint as one undo step.
//
// A schema is a database operation (add-schema, DatabaseSchemas.tsx); a table, a view, a sequence, a routine, a database type
// a SQL object and a query are element files of the database (schemas/v1/table.json, view.json, sequence.json, routine.json,
// database-type.json, sql-object.json, query.json). Each has a kind here, a label, a check and a document builder, and the menus
// follow.
import { BUILTIN_TYPES, IDENTIFIER } from "@/model/model";
import type { BuiltinType } from "@/api/types";
import { columnOptions, columnRef, defaultAlias, fieldName, type Field, type SourceOption } from "@/model/queryTree";

/** What a database's New menu creates, in menu order. */
export type DatabaseObjectKind = "schema" | "table" | "view" | "sequence" | "routine" | "query" | "database-type" | "sql-object";

/** The New actions of a database, in menu order. */
export const DATABASE_CREATE: readonly DatabaseObjectKind[] = ["schema", "table", "view", "sequence", "routine", "query", "database-type", "sql-object"];

/** The element kinds among them (a schema is an entry of the database file). */
export type DatabaseElementKind = Exclude<DatabaseObjectKind, "schema">;
export const DATABASE_ELEMENT_KINDS: readonly DatabaseElementKind[] = ["table", "view", "sequence", "routine", "query", "database-type", "sql-object"];

/** What a schema row offers: everything but a query, which has no schema (it shows under the default one). */
export const SCHEMA_ELEMENT_KINDS: readonly DatabaseElementKind[] = DATABASE_ELEMENT_KINDS.filter((k) => k !== "query");

export const DATABASE_CREATE_LABELS: Record<DatabaseObjectKind, string> = {
  schema: "New schema…",
  table: "New table…",
  view: "New view…",
  sequence: "New sequence…",
  routine: "New routine…",
  query: "New query…",
  "database-type": "New database type…",
  "sql-object": "New SQL object…",
};

/** The dialog titles (the labels without the ellipsis). */
export const DATABASE_CREATE_TITLES: Record<DatabaseObjectKind, string> = {
  schema: "New schema",
  table: "New table",
  view: "New view",
  sequence: "New sequence",
  routine: "New routine",
  query: "New query",
  "database-type": "New database type",
  "sql-object": "New SQL object",
};

/** The kind folders of the Databases explorer whose rows offer a New action, by the kind they list. */
export function databaseFolderCreate(kind: string | undefined): DatabaseElementKind | null {
  return kind && (DATABASE_ELEMENT_KINDS as readonly string[]).includes(kind) ? (kind as DatabaseElementKind) : null;
}

/** The dialect key a view body may use for every dialect. */
export const ANY_DIALECT = "*";

export const SEQUENCE_TYPES = ["int16", "int32", "int64"] as const;
export type SequenceType = (typeof SEQUENCE_TYPES)[number];

/** A starting body for a new view, valid SQL for the dialect. */
export function viewBodyTemplate(dialect: string): string {
  return dialect === "oracle" ? "select 1 as id from dual" : "select 1 as id";
}

export const ROUTINE_KINDS = ["function", "procedure"] as const;
export type RoutineKind = (typeof ROUTINE_KINDS)[number];

/** A starting body for a new routine: what follows its signature (a function returns 0, a procedure does nothing). */
export function routineBodyTemplate(dialect: string, routineKind: RoutineKind): string {
  if (dialect === "sqlserver") return routineKind === "function" ? "begin\n  return 0;\nend" : "begin\n  set nocount on;\nend";
  return routineKind === "function" ? "begin\n  return 0;\nend" : "begin\n  null;\nend";
}

export const TYPE_KINDS = ["domain", "composite", "enum", "range"] as const;
export type TypeKind = (typeof TYPE_KINDS)[number];

/** What each kind of database type is, as the pickers explain it. */
export const TYPE_KIND_LABELS: Record<TypeKind, string> = {
  domain: "Domain (a built-in type with a constraint)",
  composite: "Composite (named fields)",
  enum: "Enum (a list of labels)",
  range: "Range (over a subtype)",
};

/** The kinds of SQL object the object kind field suggests; any text is allowed. */
export const OBJECT_KIND_SUGGESTIONS = ["trigger", "grant", "extension", "index", "policy", "function", "statement"] as const;

export const OBJECT_PHASES = ["before", "after"] as const;
export type ObjectPhase = (typeof OBJECT_PHASES)[number];
export const OBJECT_PHASE_LABELS: Record<ObjectPhase, string> = {
  before: "Before the types and tables",
  after: "After the routines and views",
};

/** A starting body for a new SQL object: a comment the user replaces with the statements. */
export const SQL_OBJECT_BODY_TEMPLATE = "-- The statements that create the object, run as written.";

/** The members of an enum database type typed as one line: comma-separated labels, trimmed, empty ones left out. */
export function parseMembers(text: string): string[] {
  return text
    .split(",")
    .map((m) => m.trim())
    .filter(Boolean);
}

export const isBuiltinType = (value: unknown): value is BuiltinType => typeof value === "string" && (BUILTIN_TYPES as readonly string[]).includes(value);

/** What a New dialog collects. Text fields stay text until the document is built. */
export interface DatabaseObjectInput {
  kind: DatabaseElementKind;
  name: string;
  database: string;
  /** The schema's id, or null for the database's default schema. */
  schema: string | null;
  /** Table: start with an `id` column (int64, not null) as the primary key. */
  withIdColumn?: boolean;
  /** View: the dialect the body is written for (a dialect name, or "*" for every dialect). */
  dialect?: string;
  body?: string;
  /** Sequence. */
  type?: SequenceType;
  start?: string;
  increment?: string;
  /** Routine: function or procedure, and a function's result type ("" returns nothing). The body is `dialect` and `body`. */
  routineKind?: RoutineKind;
  returns?: BuiltinType | "";
  /** Database type: its kind, a domain's base, a range's subtype, an enum's members (comma-separated). */
  typeKind?: TypeKind;
  base?: BuiltinType;
  subtype?: BuiltinType;
  members?: string;
  /** SQL object: what it is (free text) and when it runs. The statements are `dialect` and `body`. */
  objectKind?: string;
  phase?: ObjectPhase;
  /** Query: the result entity (none: an ad hoc row), the from source (a table key or a view id), its alias and the first fields
   * (querySelect). A query has no schema. */
  entity?: string | null;
  source?: string;
  alias?: string;
  select?: Field[];
}

/** A name already used in the database, by schema name (null: the default schema) and, when known, the element's kind. */
export interface TakenName {
  schema: string | null;
  name: string;
  kind?: string;
}

export type InputProblems = Partial<Record<"name" | "body" | "start" | "increment" | "members" | "objectKind" | "source" | "alias", string>>;

/** Which names a new element must not repeat: tables, views, sequences and database types share one set of names (relations
 * and types are named alike in a schema); routines, SQL objects and queries each have their own (a query's across the whole
 * database, since it has no schema). */
const nameGroup = (kind: string | undefined): string =>
  kind === "routine" ? "routine" : kind === "sql-object" ? "sql-object" : kind === "query" ? "query" : "relation";

const CLASH_WORDS: Record<string, string> = {
  relation: "a table, view or sequence",
  "database-type": "a database type",
  routine: "a routine",
  "sql-object": "a SQL object",
  query: "a query",
};

const WHOLE = /^-?\d+$/;

/**
 * Why the dialog cannot create the element yet, per field (empty when it can). The name follows the identifier rule and is
 * not already used in the same schema by an element of the same name group (case-insensitively, as the databases compare
 * names); `schemaName` is the chosen schema's name, null for the default schema, and `defaultSchema` the default's name.
 */
export function databaseObjectProblems(
  input: DatabaseObjectInput,
  taken: readonly TakenName[],
  schemaName: string | null,
  defaultSchema: string | null,
): InputProblems {
  const out: InputProblems = {};
  const name = input.name.trim();
  if (!name) out.name = "Enter a name.";
  else if (!IDENTIFIER.test(name)) out.name = "Use letters, digits and underscores, not starting with a digit.";
  else {
    const schema = (schemaName ?? defaultSchema ?? "").toLowerCase();
    const group = nameGroup(input.kind);
    const clash = taken.find(
      (t) =>
        nameGroup(t.kind) === group &&
        t.name.toLowerCase() === name.toLowerCase() &&
        (group === "query" || (t.schema ?? defaultSchema ?? "").toLowerCase() === schema),
    );
    if (clash)
      out.name =
        group === "query"
          ? `${clash.name} is already a query of this database.`
          : `${clash.name} is already ${CLASH_WORDS[clash.kind === "database-type" ? "database-type" : group]} in ${schemaName ?? defaultSchema ?? "this database"}.`;
  }
  if (input.kind === "query") {
    if (!(input.source ?? "").trim()) out.source = "Pick the table or view the query reads.";
    const alias = (input.alias ?? "").trim();
    if (alias && !IDENTIFIER.test(alias)) out.alias = "Use letters, digits and underscores, not starting with a digit.";
  }
  if (input.kind === "view" && !(input.body ?? "").trim()) out.body = "Enter the view's SQL body.";
  if (input.kind === "routine" && !(input.body ?? "").trim()) out.body = "Enter the routine's body.";
  if (input.kind === "sql-object") {
    if (!(input.body ?? "").trim()) out.body = "Enter the object's SQL statements.";
    if (!(input.objectKind ?? "").trim()) out.objectKind = "Say what the object is (trigger, grant, extension…).";
  }
  if (input.kind === "database-type" && (input.typeKind ?? "domain") === "enum" && !parseMembers(input.members ?? "").length)
    out.members = "Enter at least one label.";
  else if (input.kind === "database-type" && (input.typeKind ?? "domain") === "enum") {
    const members = parseMembers(input.members ?? "");
    if (new Set(members).size !== members.length) out.members = "Each label appears once.";
  }
  if (input.kind === "sequence") {
    const start = (input.start ?? "").trim();
    const increment = (input.increment ?? "").trim();
    if (start !== "" && !WHOLE.test(start)) out.start = "Start is a whole number.";
    if (increment !== "" && !WHOLE.test(increment)) out.increment = "Increment is a whole number.";
    else if (increment !== "" && Number(increment) === 0) out.increment = "Increment cannot be 0.";
  }
  return out;
}

type Json = Record<string, unknown>;

/** The new element's document: what its schema requires, plus the dialog's choices (defaults are left out). */
export function buildDatabaseObject(input: DatabaseObjectInput, newId: () => string): Json {
  const id = newId();
  if (input.kind === "query") {
    const alias = (input.alias ?? "").trim();
    return {
      kind: "query",
      id,
      name: input.name.trim(),
      database: input.database,
      ...(input.entity ? { entity: input.entity } : {}),
      from: { source: input.source ?? "", ...(alias ? { alias } : {}) },
      select: input.select ?? [],
    };
  }
  const head: Json = { kind: input.kind, id, name: input.name.trim(), database: input.database, ...(input.schema ? { schema: input.schema } : {}) };
  switch (input.kind) {
    case "table": {
      // A designed table (the schema's default origin): its own columns.
      if (!input.withIdColumn) return head;
      const column = newId();
      return {
        ...head,
        columns: [{ id: column, name: "id", type: "int64", nullable: false }],
        primaryKey: { columns: [column] },
      };
    }
    case "view":
      return { ...head, body: { [input.dialect || ANY_DIALECT]: input.body ?? "" } };
    case "sequence": {
      const start = (input.start ?? "").trim();
      const increment = (input.increment ?? "").trim();
      return {
        ...head,
        ...(input.type && input.type !== "int64" ? { type: input.type } : {}),
        ...(start !== "" && Number(start) !== 1 ? { start: Number(start) } : {}),
        ...(increment !== "" && Number(increment) !== 1 ? { increment: Number(increment) } : {}),
      };
    }
    case "routine": {
      const routineKind = input.routineKind ?? "function";
      return {
        ...head,
        ...(routineKind === "procedure" ? { routineKind } : {}),
        ...(routineKind === "function" && input.returns ? { returns: { type: input.returns } } : {}),
        body: { [input.dialect || ANY_DIALECT]: input.body ?? "" },
      };
    }
    case "database-type": {
      const typeKind = input.typeKind ?? "domain";
      return { ...head, typeKind, ...typeKindDefaults(typeKind, input) };
    }
    case "sql-object":
      return {
        ...head,
        objectKind: (input.objectKind ?? "").trim(),
        ...(input.phase === "before" ? { phase: "before" } : {}),
        body: { [input.dialect || ANY_DIALECT]: input.body ?? "" },
      };
  }
}

/**
 * The fields a new query starts with, so it is valid as created (a select list has at least one field): with a result entity,
 * its key attribute filled from the column that stores it (by attribute id, else by name); without one, the source's first
 * column as a field named after it; a view without declared columns, the value 1 as `value`.
 */
export function querySelect(source: SourceOption | null, alias: string, key: { id: string; name: string } | null): Field[] {
  const columns = columnOptions(source);
  const a = alias || source?.name || "";
  if (key) {
    const fold = (s: string) => s.replace(/_/g, "").toLowerCase();
    const c = columns.find((x) => x.column?.attributeId === key.id) ?? columns.find((x) => fold(x.name) === fold(key.name)) ?? columns[0];
    return [{ attribute: key.id, expression: c ? { column: columnRef(a, c) } : { null: true } }];
  }
  const first = columns[0];
  return first ? [{ name: fieldName(first.name), expression: { column: columnRef(a, first) } }] : [{ name: "value", expression: { value: 1 } }];
}

/** The alias a new query's source starts with: the first letter of each word of its name. */
export const queryAlias = (source: SourceOption | null): string => (source ? defaultAlias(source.name) : "");

/** The members a database type of a kind starts with, so there is something to create: a domain's base (string), a range's
 * subtype (int32), an enum's members, a composite's first field. */
export function typeKindDefaults(typeKind: TypeKind, input: Pick<DatabaseObjectInput, "base" | "subtype" | "members"> = {}): Json {
  switch (typeKind) {
    case "domain":
      return { base: input.base ?? "string" };
    case "range":
      return { subtype: input.subtype ?? "int32" };
    case "enum": {
      const members = parseMembers(input.members ?? "");
      return { members: members.length ? members : ["value_1"] };
    }
    case "composite":
      return { fields: [{ name: "value", type: "string" }] };
  }
}
