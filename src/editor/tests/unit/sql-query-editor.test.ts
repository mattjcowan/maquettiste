// The editor's side of a query written as SQL text: Find in SQL, the placeholder marks, the lists and columns edits, a parameter's
// rename in the texts, the column rename that follows into bindings, and the New query dialog's SQL text document.
import { describe, expect, it } from "vitest";
import { placeholderMarks, tableChoices } from "@/editors/database/SqlQueryParts";
import { buildDatabaseObject, databaseObjectProblems, queryTextTemplate } from "@/explorer/databaseCreate";
import { renameParameter, type QueryDoc } from "@/model/queryTree";
import { addColumn, addToList, findTablesInSql, isSqlTextQuery, removeColumn, removeFromList, type TableChoice } from "@/model/sqlQuery";
import { renameBindingColumns, renamedOutputColumns } from "@/workspaces/database/renames";
import type { DatabaseView } from "@/api/types";

type Json = Record<string, unknown>;

const choices: TableChoice[] = [
  { id: "t-invoices", name: "invoices", schema: "billing", kind: "table" },
  { id: "t-customers", name: "customers", schema: "billing", kind: "table" },
  { id: "v-open", name: "open_invoices", schema: "billing", kind: "view" },
];

describe("Find in SQL", () => {
  it("lists the tables and views whose names appear in the text, ignoring case, and leaves out what is already read", () => {
    const texts = {
      postgresql: "SELECT * FROM billing.\"Invoices\" i JOIN Billing.CUSTOMERS c ON c.id = i.customer_id /* open_invoices */ WHERE x = 'open_invoices'",
    };
    expect(findTablesInSql(texts, "postgresql", choices, []).map((c) => c.id)).toEqual(["t-invoices", "t-customers"]);
    expect(findTablesInSql(texts, "postgresql", choices, ["t-invoices"]).map((c) => c.id)).toEqual(["t-customers"]);
  });

  it("reads the text the database runs: its own dialect's, else the * text, else nothing", () => {
    const texts = { "*": "SELECT 1 FROM customers", sqlserver: "SELECT 1 FROM invoices" };
    expect(findTablesInSql(texts, "sqlserver", choices, []).map((c) => c.id)).toEqual(["t-invoices"]);
    expect(findTablesInSql(texts, "postgresql", choices, []).map((c) => c.id)).toEqual(["t-customers"]);
    expect(findTablesInSql({ sqlserver: "SELECT 1 FROM invoices" }, "oracle", choices, [])).toEqual([]);
    expect(findTablesInSql(undefined, "oracle", choices, [])).toEqual([]);
  });

  it("lists tables then views of a database view, with their keys", () => {
    const view = {
      tables: [{ key: "k1", name: "b", schema: null }],
      views: [{ id: "v1", name: "a", schema: "s" }],
    } as unknown as DatabaseView;
    expect(tableChoices(view)).toEqual([
      { id: "k1", name: "b", schema: null, kind: "table" },
      { id: "v1", name: "a", schema: "s", kind: "view" },
    ]);
  });
});

describe("the SQL text's marks", () => {
  it("marks each placeholder and underlines one naming no parameter, with the finding's text as its message", () => {
    const text = "SELECT 1\nWHERE a = :id AND b = :idd AND c = ':nope' AND d::text = :id";
    const m = placeholderMarks("Q", "postgresql", text, ["id"], "postgresql");
    expect(m.highlights).toEqual([
      { line: 2, column: 11, length: 3 },
      { line: 2, column: 23, length: 4 },
      { line: 2, column: 58, length: 3 },
    ]);
    expect(m.markers).toEqual([
      { line: 2, column: 23, length: 4, severity: "error", message: "Query 'Q': ':idd' (line 2, column 23 of the postgresql text) names no parameter." },
    ]);
    expect(m.used).toEqual(["id", "idd"]);
    expect(m.unknown).toEqual(["idd"]);
  });

  it("reads a * text in the database's dialect", () => {
    const m = placeholderMarks("Q", "*", "SELECT $$ :a $$, :b", ["b"], "postgresql");
    expect(m.used).toEqual(["b"]);
    expect(m.unknown).toEqual([]);
  });
});

describe("editing a query written as SQL text", () => {
  it("recognises the form", () => {
    expect(isSqlTextQuery({ kind: "query", sql: { "*": "x" } })).toBe(true);
    expect(isSqlTextQuery({ kind: "query", from: { source: "a" }, select: [] })).toBe(false);
  });

  it("adds and removes reads, writes and columns, dropping an empty member", () => {
    const doc: Json = {};
    addToList(doc, "reads", ["a", "b", "a"]);
    expect(doc.reads).toEqual(["a", "b"]);
    removeFromList(doc, "reads", 0);
    removeFromList(doc, "reads", 0);
    expect(doc.reads).toBeUndefined();
    addColumn(doc);
    addColumn(doc);
    expect((doc.columns as Json[]).map((c) => c.name)).toEqual(["column_1", "column_2"]);
    removeColumn(doc, 0);
    removeColumn(doc, 0);
    expect(doc.columns).toBeUndefined();
  });

  it("renames a parameter in the texts of every dialect, leaving strings, comments and casts alone", () => {
    const doc = {
      kind: "query",
      id: "q",
      name: "Q",
      database: "d",
      select: [],
      from: undefined,
      parameters: [{ name: "id", type: "int32" }],
      sql: { "*": "SELECT :id, ':id', a::int -- :id\nWHERE x = :id", sqlserver: "SELECT :id" },
    } as unknown as QueryDoc;
    expect(renameParameter(doc, "id", "key", "postgresql")).toBe(true);
    expect(doc.parameters![0].name).toBe("key");
    expect(doc.sql).toEqual({ "*": "SELECT :key, ':id', a::int -- :id\nWHERE x = :key", sqlserver: "SELECT :key" });
  });

  it("sees a declared column renamed in place as a rename of an output column", () => {
    const before = {
      kind: "query",
      sql: { "*": "x" },
      columns: [
        { name: "a", type: "string" },
        { name: "b", type: "string" },
      ],
    };
    const after = {
      ...before,
      columns: [
        { name: "a", type: "string" },
        { name: "c", type: "string" },
      ],
    };
    expect([...renamedOutputColumns(before, after)]).toEqual([["b", "c"]]);
  });
});

describe("renaming a declared column", () => {
  it("follows into the bindings that read the query by that name, and no other binding", () => {
    const entity: Json = {
      kind: "entity",
      id: "E1",
      name: "E",
      bindings: [
        {
          id: "b",
          database: "D",
          source: "Q1",
          fields: [
            { attribute: "a1", column: "total" },
            { attribute: "a2", column: "other" },
          ],
        },
        { id: "c", database: "D2", source: "Q2", fields: [{ attribute: "a3", column: "total" }] },
      ],
    };
    expect(renameBindingColumns(entity, "Q1", new Map([["total", "grand_total"]]))).toBe(1);
    const bindings = entity.bindings as Json[];
    expect((bindings[0].fields as Json[]).map((f) => f.column)).toEqual(["grand_total", "other"]);
    expect((bindings[1].fields as Json[]).map((f) => f.column)).toEqual(["total"]);
  });
});

describe("the New query dialog's SQL text form", () => {
  const input = {
    kind: "query" as const,
    name: "TopCustomers",
    database: "D",
    schema: null,
    form: "sql" as const,
    dialect: "oracle",
    body: queryTextTemplate("oracle"),
  };

  it("needs a name and some SQL, and no source", () => {
    expect(databaseObjectProblems(input, [], null, null)).toEqual({});
    expect(databaseObjectProblems({ ...input, body: " " }, [], null, null)).toEqual({ body: "Enter the query's SQL." });
    expect(databaseObjectProblems({ ...input, form: "parts" }, [], null, null).source).toBeDefined();
  });

  it("builds a document with one text for the dialect and the one column the starter selects", () => {
    const json = buildDatabaseObject(input, () => "ID1");
    expect(json).toEqual({
      kind: "query",
      id: "ID1",
      name: "TopCustomers",
      database: "D",
      sql: { oracle: "SELECT 1 AS value FROM dual" },
      columns: [{ name: "value", type: "int32" }],
    });
    expect(queryTextTemplate("postgresql")).toBe("SELECT 1 AS value");
  });
});
