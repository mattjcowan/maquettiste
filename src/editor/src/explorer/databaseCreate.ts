// Creating what lives in a database (explorer-redesign.md 1.8): the New actions a database offers (its explorer row's menu,
// the explorer's New menu while a database or anything inside it is selected, the Database screen's New button), their
// labels, the checks the New dialogs run and the documents they create. Pure: NewDatabaseObjectDialog.tsx renders the dialogs
// and saves the result through the create endpoint as one undo step.
//
// A schema is a database operation (add-schema, DatabaseSchemas.tsx); a table, a view and a sequence are element files of the
// database (schemas/v1/table.json, view.json, sequence.json). Routines, database types and SQL objects join this list when
// the engine has their kinds: each needs a kind here, a label, a check and a document builder, and the menus follow.
import { IDENTIFIER } from "@/model/model";

/** What a database's New menu creates, in menu order. */
export type DatabaseObjectKind = "schema" | "table" | "view" | "sequence";

/** The New actions of a database, in menu order. */
export const DATABASE_CREATE: readonly DatabaseObjectKind[] = ["schema", "table", "view", "sequence"];

/** The element kinds among them (a schema is an entry of the database file). */
export type DatabaseElementKind = Exclude<DatabaseObjectKind, "schema">;
export const DATABASE_ELEMENT_KINDS: readonly DatabaseElementKind[] = ["table", "view", "sequence"];

export const DATABASE_CREATE_LABELS: Record<DatabaseObjectKind, string> = {
  schema: "New schema…",
  table: "New table…",
  view: "New view…",
  sequence: "New sequence…",
};

/** The dialog titles (the labels without the ellipsis). */
export const DATABASE_CREATE_TITLES: Record<DatabaseObjectKind, string> = {
  schema: "New schema",
  table: "New table",
  view: "New view",
  sequence: "New sequence",
};

/** The kind folders of the Databases explorer whose rows offer a New action, by the kind they list. */
export function databaseFolderCreate(kind: string | undefined): DatabaseElementKind | null {
  return kind === "table" || kind === "view" || kind === "sequence" ? kind : null;
}

/** The dialect key a view body may use for every dialect. */
export const ANY_DIALECT = "*";

export const SEQUENCE_TYPES = ["int16", "int32", "int64"] as const;
export type SequenceType = (typeof SEQUENCE_TYPES)[number];

/** A starting body for a new view, valid SQL for the dialect. */
export function viewBodyTemplate(dialect: string): string {
  return dialect === "oracle" ? "select 1 as id from dual" : "select 1 as id";
}

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
}

/** A name already used in the database: a table, a view or a sequence, by schema name (null: the default schema). */
export interface TakenName {
  schema: string | null;
  name: string;
}

export type InputProblems = Partial<Record<"name" | "body" | "start" | "increment", string>>;

const WHOLE = /^-?\d+$/;

/**
 * Why the dialog cannot create the element yet, per field (empty when it can). The name follows the identifier rule and is
 * not already a table, view or sequence of the same schema (case-insensitively, as the databases compare names); `schemaName`
 * is the chosen schema's name, null for the default schema, and `defaultSchema` the default's name.
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
    const clash = taken.find((t) => t.name.toLowerCase() === name.toLowerCase() && (t.schema ?? defaultSchema ?? "").toLowerCase() === schema);
    if (clash) out.name = `${clash.name} is already a table, view or sequence in ${schemaName ?? defaultSchema ?? "this database"}.`;
  }
  if (input.kind === "view" && !(input.body ?? "").trim()) out.body = "Enter the view's SQL body.";
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
  }
}
