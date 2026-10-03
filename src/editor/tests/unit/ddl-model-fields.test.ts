// The DDL fields of the model the editor edits beyond a column's type (schemas/v1 table.json and view.json): an identity's seed,
// increment and generated always; an index column that is an expression per dialect or takes a key prefix length; a unique
// constraint's nulls-not-distinct; a view's column list, WITH CHECK OPTION, materialized and dependsOn, with the views its body
// names. The pure edits, the dialect notes, and the mock's resolution, DDL and MQ4056 that the editor's mock flows read.
import { describe, expect, it } from "vitest";
import type { ColumnView } from "@/api/types";
import { columnFieldProblem, columnGeneration, identityNote, setColumnField } from "@/workspaces/database/columnEdits";
import {
  addIndexExpression,
  indexColumnNote,
  indexColumnText,
  indexLengthProblem,
  moveIndexColumn,
  nullsNotDistinctNote,
  removeIndexColumnAt,
  setIndexColumnLength,
  setIndexColumnOrder,
  setIndexExpression,
  toggleIndexColumn,
  withColumnIds,
} from "@/workspaces/database/tableParts";
import { inferredViewDependencies, setViewOption, sqlIdentifierTokens, viewOptionNote } from "@/editors/database/databaseDocs";
import { keysOfDocument } from "@/model/foreignKeyTarget";
import { planColumnRename } from "@/workspaces/database/renames";
import { MockBackend } from "@/mocks/backend";

type Json = Record<string, unknown>;
const NOTES = "01K6BND0000000000000000001";
const BODY = "01K6BND0000000000000000005";
const ID = "01K6BND0000000000000000002";
const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";
const OUTSTANDING = "01J92P0V1YZ4YP352KD1A50FXS";

const col = (key: string, type: string, name = "c") => ({ key, name, type }) as ColumnView;
const newId = () => "N1";

describe("an identity's seed, increment and generated always", () => {
  it("sets each into the column's identity object, in the schema's order, and makes the column an identity", () => {
    const doc: Json = { kind: "table", columns: [{ id: "c1", name: "id", type: "int64" }] };
    const c = col("c1", "int64", "id");
    const entry = () => (doc.columns as Json[])[0];
    expect(setColumnField(doc, c, "identityAlways", true, newId)).toBe(true);
    expect(setColumnField(doc, c, "identityIncrement", " 5 ", newId)).toBe(true);
    expect(setColumnField(doc, c, "identitySeed", "-10", newId)).toBe(true);
    expect(entry().generated).toBe("identity");
    expect(entry().identity).toEqual({ seed: -10, increment: 5, always: true });
    expect(Object.keys(entry().identity as Json)).toEqual(["seed", "increment", "always"]);
    expect(columnGeneration(entry(), c as never, "postgresql")).toMatchObject({ identitySeed: "-10", identityIncrement: "5", identityAlways: true });
    // Cleared one by one, the object goes with the last.
    setColumnField(doc, c, "identitySeed", "", newId);
    setColumnField(doc, c, "identityIncrement", "", newId);
    expect(entry().identity).toEqual({ always: true });
    setColumnField(doc, c, "identityAlways", false, newId);
    expect("identity" in entry()).toBe(false);
    expect(entry().generated).toBe("identity");
    // Another way to get values drops the identity's options.
    setColumnField(doc, c, "identitySeed", "100", newId);
    setColumnField(doc, c, "generated", "", newId);
    expect("identity" in entry() || "generated" in entry()).toBe(false);
    setColumnField(doc, c, "identitySeed", "100", newId);
    setColumnField(doc, c, "sequence", "SEQ1", newId);
    expect(entry()).toMatchObject({ generated: "sequence", sequence: "SEQ1" });
    expect("identity" in entry()).toBe(false);
  });

  it("refuses a seed or increment that is not a whole number, and an increment of 0, saying why", () => {
    expect(columnFieldProblem("identitySeed", "1.5")).toBe("The identity seed is a whole number, or empty.");
    expect(columnFieldProblem("identitySeed", "-3")).toBeNull();
    expect(columnFieldProblem("identityIncrement", "0")).toBe("The identity increment cannot be 0.");
    expect(columnFieldProblem("identityIncrement", "x")).toBe("The identity increment is a whole number, or empty.");
    expect(columnFieldProblem("identityIncrement", "")).toBeNull();
    const doc: Json = { kind: "table", columns: [{ id: "c1", name: "id", type: "int64", generated: "identity" }] };
    expect(setColumnField(doc, col("c1", "int64"), "identityIncrement", "0", newId)).toBe(false);
    expect((doc.columns as Json[])[0].identity).toBeUndefined();
  });

  it("says what each dialect does with them", () => {
    expect(identityNote("postgresql")).toBeNull();
    expect(identityNote("oracle")).toBeNull();
    expect(identityNote("sqlserver")).toContain("IDENTITY(seed, increment)");
    expect(identityNote("mysql")).toContain("AUTO_INCREMENT");
    expect(identityNote("sqlite")).toContain("MQ4056");
  });
});

describe("an index column that is an expression or takes a prefix length", () => {
  const index = (): Json => ({ id: "I1", name: "ix_t", columns: [{ column: "C1", length: 10 }, { column: "C2" }] });

  it("adds an expression, edits its text per dialect (keeping one), and keeps it through column toggles", () => {
    const entry = index();
    expect(addIndexExpression(entry, "postgresql", "lower(name)")).toBe(2);
    expect(setIndexExpression(entry, 2, "*", "lower(name)")).toBe(true);
    expect(setIndexExpression(entry, 2, "postgresql", "")).toBe(true);
    expect((entry.columns as Json[])[2]).toEqual({ expression: { "*": "lower(name)" } });
    expect(setIndexExpression(entry, 2, "*", "")).toBe(false); // the last text stays
    expect(setIndexExpression(entry, 0, "*", "x")).toBe(false); // a column is no expression
    // Unticking and ticking columns keeps the expression; the last entry cannot go.
    expect(toggleIndexColumn(entry, "C2")).toBe(true);
    expect(toggleIndexColumn(entry, "C1")).toBe(true);
    expect(entry.columns).toEqual([{ expression: { "*": "lower(name)" } }]);
    expect(removeIndexColumnAt(entry, 0)).toBe(false);
    expect(toggleIndexColumn(entry, "C3")).toBe(true);
    expect(entry.columns).toEqual([{ expression: { "*": "lower(name)" } }, { column: "C3" }]);
  });

  it("sorts, moves and sets a prefix length by place, keeping the column's other members", () => {
    const entry = index();
    addIndexExpression(entry, "*", "upper(a)");
    setIndexColumnOrder(entry, 2, true);
    setIndexColumnOrder(entry, "C1", true);
    expect(entry.columns).toEqual([{ column: "C1", descending: true, length: 10 }, { column: "C2" }, { expression: { "*": "upper(a)" }, descending: true }]);
    expect(moveIndexColumn(entry, 2, -1)).toBe(true);
    expect(setIndexColumnLength(entry, 0, undefined)).toBe(true);
    expect(setIndexColumnLength(entry, 2, 20)).toBe(true);
    expect(entry.columns).toEqual([
      { column: "C1", descending: true },
      { expression: { "*": "upper(a)" }, descending: true },
      { column: "C2", length: 20 },
    ]);
    expect(removeIndexColumnAt(entry, 1)).toBe(true);
    expect(indexLengthProblem("0")).toBe("A prefix length is a whole number from 1, or empty.");
    expect(indexLengthProblem(" 12 ")).toBeNull();
    expect(indexLengthProblem("")).toBeNull();
  });

  it("names an expression column by its dialect's text, renames columns around it, and is no foreign key target", () => {
    const nameOf = (id: string) => ({ C1: "name" })[id] ?? id;
    expect(indexColumnText({ column: "C1" }, "postgresql", nameOf)).toBe("name");
    expect(indexColumnText({ expression: { postgresql: "lower(name)", "*": "x" } }, "postgresql", nameOf)).toBe("(lower(name))");
    expect(indexColumnText({ expression: { "*": "x" } }, "mysql", nameOf)).toBe("(x)");
    expect(indexColumnText({ expression: { oracle: "x" } }, "mysql", nameOf)).toBe("(no expression for mysql)");
    const doc: Json = {
      id: "T",
      columns: [{ id: "a", name: "name" }],
      indexes: [{ id: "I", unique: true, columns: [{ expression: { "*": "lower(name)" } }, { column: "a" }] }],
    };
    withColumnIds(doc, new Map([["a", "A1"]]));
    expect((doc.indexes as Json[])[0].columns).toEqual([{ expression: { "*": "lower(name)" } }, { column: "A1" }]);
    expect(keysOfDocument(doc).uniqueIndexes).toEqual([]);
    expect(indexColumnNote("sqlserver", { expression: true, length: false })).toContain("MQ4056");
    expect(indexColumnNote("mysql", { expression: true, length: true })).toContain("8.0.13");
    expect(indexColumnNote("postgresql", { expression: true, length: true })).toContain("Only MySQL");
    expect(indexColumnNote("postgresql", { expression: true, length: false })).toBeNull();
  });

  it("a column rename rewrites the index expressions that name it", () => {
    const table: Json = {
      kind: "table",
      id: "T",
      name: "t",
      database: "D",
      columns: [{ id: "a", name: "name", type: "string" }],
      indexes: [{ id: "I", name: "ix_t_lower", columns: [{ expression: { "*": "lower(name)" } }] }],
    };
    const plan = planColumnRename({ table, column: "a", to: "title", docs: [], tables: [] });
    const edited = structuredClone(table);
    plan.edits.get("T")!(edited);
    expect(((edited.indexes as Json[])[0].columns as Json[])[0].expression).toEqual({ "*": "lower(title)" });
  });
});

describe("a unique constraint's nulls-not-distinct and a view's DDL options", () => {
  it("says where nulls-not-distinct is written", () => {
    expect(nullsNotDistinctNote("postgresql")).toBeNull();
    expect(nullsNotDistinctNote("mysql")).toContain("MQ4056");
  });

  it("sets and clears a view's options, with what the dialect does with them", () => {
    const doc: Json = { kind: "view" };
    setViewOption(doc, "materialized", true);
    setViewOption(doc, "withCheckOption", true);
    setViewOption(doc, "columnList", true);
    expect(doc).toMatchObject({ materialized: true, withCheckOption: true, columnList: true });
    expect(viewOptionNote("postgresql", doc)).toContain("not written through");
    expect(viewOptionNote("postgresql", doc)).toContain("declare them there");
    expect(viewOptionNote("mysql", doc)).toContain("plain view (MQ4056)");
    setViewOption(doc, "materialized", false);
    expect(viewOptionNote("sqlite", doc)).toContain("SQLite has no WITH CHECK OPTION");
    setViewOption(doc, "withCheckOption", false);
    setViewOption(doc, "columnList", false);
    expect(doc).toEqual({ kind: "view" });
    expect(viewOptionNote("postgresql", doc)).toBeNull();
  });

  it("finds the other views a body names as identifiers, as the engine reads it", () => {
    expect(sqlIdentifierTokens("select a.x, 1abc, _y$ from billing.open_orders o")).toEqual(["select", "a", "x", "_y$", "from", "billing", "open_orders", "o"]);
    const views = [
      { id: "V1", name: "open_orders", schema: "billing" },
      { id: "V2", name: "late", schema: "billing" },
      { id: "V3", name: "summary", schema: null },
    ];
    expect(inferredViewDependencies("select * from billing.OPEN_ORDERS join late using (id)", views, "V3").map((v) => v.id)).toEqual(["V2", "V1"]);
    expect(inferredViewDependencies("select * from summary", views, "V3")).toEqual([]);
    expect(inferredViewDependencies("select * from open_orders", [views[0]], "X")).toEqual([]);
  });
});

describe("the mock carries them", () => {
  it("resolves an expression index as the engine does, renders the options, and validates against the schemas", () => {
    const backend = new MockBackend();
    const model = backend.model;
    const notes = model.get(NOTES)!;
    const json = structuredClone(notes.json) as unknown as Json;
    const columns = json.columns as Json[];
    columns.find((c) => c.id === ID)!.generated = "identity";
    columns.find((c) => c.id === ID)!.type = "int64";
    columns.find((c) => c.id === ID)!.identity = { seed: 100, increment: 2, always: true };
    json.uniques = [{ id: "01K6BND00000000000000000A1", name: "uq_notes_body", columns: [BODY], nullsNotDistinct: true }];
    json.indexes = [
      { id: "01K6BND00000000000000000X1", name: "ix_notes_lower", columns: [{ expression: { "*": "lower(body)" } }, { column: BODY, length: 20 }] },
      { id: "01K6BND00000000000000000X2", columns: [{ expression: { oracle: "upper(body)" } }] },
    ];
    // The document is valid against schemas/v1 (the mock's MQ1002 otherwise).
    const saved = model.save(NOTES, json, notes.hash);
    expect((saved.diagnostics ?? []).filter((d) => d.severity === "error")).toEqual([]);
    expect(saved.outcome).toBe("saved");
    const table = backend.generation.databaseView(MAIN)!.view!.tables.find((t) => t.key === NOTES)!;
    // The index without a text for the dialect is left out; the expression shows in parentheses.
    expect(table.indexes.map((i) => [i.name, i.columns.map((c) => c.column)])).toEqual([["ix_notes_lower", ["(lower(body))", BODY]]]);
    // PostgreSQL writes all of it; no MQ4056 but the prefix length (MySQL only).
    const pg = model
      .validate()
      .diagnostics.filter((d) => d.rule === "MQ4056" && d.elementId === NOTES)
      .map((d) => d.jsonPointer);
    expect(pg).toEqual(["/indexes/0/columns/1/length"]);

    const view = model.get(OUTSTANDING)!;
    const viewJson = { ...(view.json as unknown as Json), materialized: true, withCheckOption: true, columnList: true, dependsOn: [NOTES] };
    const viewSaved = model.save(OUTSTANDING, viewJson, view.hash);
    expect(viewSaved.outcome).toBe("saved");
    expect((viewSaved.diagnostics ?? []).filter((d) => d.severity === "error")).toEqual([]);
    expect(
      model
        .validate()
        .diagnostics.filter((d) => d.rule === "MQ4056" && d.elementId === OUTSTANDING)
        .map((d) => d.jsonPointer),
    ).toEqual(["/withCheckOption"]);
    const ddl = backend.generation.renderUnits(["sql-ddl"]);
    const tableDdl = ddl.find((u) => u.unit === "table" && u.elementId === NOTES)!.files[0].text;
    expect(tableDdl).toContain("GENERATED ALWAYS AS IDENTITY (START WITH 100 INCREMENT BY 2)");
    expect(tableDdl).toContain("UNIQUE NULLS NOT DISTINCT (body)");
    expect(tableDdl).toContain("ON billing.notes ((lower(body)), body)");
    const schema = ddl.find((u) => u.unit === "schema" && u.elementId === MAIN)!.files[0].text;
    expect(schema).toMatch(/CREATE MATERIALIZED VIEW billing\.outstanding_invoices \([a-z_, ]+\) AS/);
    expect(schema).not.toContain("WITH CHECK OPTION");

    // On MySQL: identity increment and always, nulls-not-distinct, materialized are left out; the prefix length is fine.
    const db = model.get(MAIN)!;
    model.save(MAIN, { ...(db.json as unknown as Json), dialect: "mysql" }, db.hash);
    const mysql = model
      .validate()
      .diagnostics.filter((d) => d.rule === "MQ4056" && (d.elementId === NOTES || d.elementId === OUTSTANDING))
      .map((d) => d.jsonPointer)
      .sort();
    expect(mysql).toEqual(["/columns/0/identity/always", "/columns/0/identity/increment", "/materialized", "/uniques/0/nullsNotDistinct"].sort());
    // On SQL Server the expression index is left out of the DDL.
    const again = model.get(MAIN)!;
    model.save(MAIN, { ...(again.json as unknown as Json), dialect: "sqlserver" }, again.hash);
    expect(
      model
        .validate()
        .diagnostics.filter((d) => d.rule === "MQ4056" && d.elementId === NOTES)
        .map((d) => d.jsonPointer),
    ).toContain("/indexes/0/columns/0/expression");
  });
});
