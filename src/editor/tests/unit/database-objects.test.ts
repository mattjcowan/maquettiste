// Routines, database types and SQL objects in the editor: the New dialogs' checks and documents (databaseCreate.ts), the
// editors' edits (databaseDocs.ts), the dependency picker's options, the labels and folders, the Database screen's list and DDL
// preview, and the mock's resolution and scripts.
import { describe, expect, it } from "vitest";
import {
  buildDatabaseObject,
  databaseObjectProblems,
  parseMembers,
  routineBodyTemplate,
  typeKindDefaults,
  type DatabaseObjectInput,
} from "@/explorer/databaseCreate";
import {
  addMember,
  definitionTemplate,
  addTypedRow,
  dialectKeys,
  facetProblem,
  memberProblem,
  moveRow,
  removeDialectText,
  removeRow,
  returnsShape,
  setDialectText,
  setFacet,
  setReturnsShape,
  setRoutineKind,
  setRowNativeType,
  setRowType,
  setTypeKind,
  toggleDependency,
} from "@/editors/database/databaseDocs";
import { dependencyOptions } from "@/editors/database/DependsOn";
import { typeSummary, typeText } from "@/editors/database/fields";
import { countOf, KIND_LABELS, kindFolder } from "@/model/labels";
import { KIND_ICONS } from "@/app/icons";
import { ddlPreviewCaption, ddlPreviewTarget } from "@/workspaces/database/ddlPreview";
import { LIST_KINDS, OBJECT_LIST_MEMBERS } from "@/workspaces/database/tableList";
import { unitScope } from "@/workspaces/generate/previewScope";
import { scopeHelp } from "@/workspaces/generate/unitsModel";
import { summaryFromDocument } from "@/model/model";
import { occupantsOf } from "@/model/databaseSchemas";
import { MockBackend } from "@/mocks/backend";
import { objectSql, routineSql, typeSql } from "@/mocks/model/render";
import { CLOSE_PERIOD, EMAIL_ADDRESS, INVOICE_STATE, INVOICE_TOTAL, REPORTING_READ } from "@/mocks/model/databaseObjectSeed";

type Json = Record<string, unknown>;
const ids = (() => {
  let n = 0;
  return () => `ID${++n}`;
})();
const base = (over: Partial<DatabaseObjectInput>): DatabaseObjectInput => ({ kind: "routine", name: "invoice_total", database: "db1", schema: null, ...over });

describe("kinds, labels and folders", () => {
  it("names the three kinds, with an icon each and folders after Sequences", () => {
    expect([KIND_LABELS.routine, KIND_LABELS["database-type"], KIND_LABELS["sql-object"]]).toEqual(["Routine", "Database type", "SQL object"]);
    expect(new Set([KIND_ICONS.routine, KIND_ICONS["database-type"], KIND_ICONS["sql-object"], KIND_ICONS.sequence]).size).toBe(4);
    const order = ["sequence", "routine", "database-type", "sql-object", "mapping"].map((k) => kindFolder(k)!.order);
    expect([...order].sort((a, b) => a - b)).toEqual(order);
    expect(["routine", "database-type", "sql-object"].map((k) => kindFolder(k)!.label)).toEqual(["Routines", "Types", "Objects"]);
    expect(["routine", "database-type", "sql-object"].every((k) => kindFolder(k)!.placement === "databases")).toBe(true);
    expect([countOf(1, "routine"), countOf(2, "database-type"), countOf(3, "sql-object")]).toEqual(["1 routine", "2 database types", "3 SQL objects"]);
  });

  it("keeps their database on index rows and counts them as occupants of a schema", () => {
    const row = summaryFromDocument({ json: { kind: "routine", id: "R1", name: "f", database: "db1" }, hash: "h", path: "p" } as never);
    expect(row.database).toBe("db1");
    const db = { id: "db1", kind: "database", schemas: [{ id: "S1", name: "billing" }] };
    const docs = [
      { kind: "routine", id: "R1", name: "f", database: "db1", schema: "S1" },
      { kind: "database-type", id: "T1", name: "t", database: "db1", schema: "S1" },
      { kind: "sql-object", id: "O1", name: "o", database: "db1", schema: "S1" },
    ];
    expect(occupantsOf(docs, db, "S1").map((o) => o.label)).toEqual(["routine f", "database-type t", "sql-object o"]);
  });
});

describe("the New dialogs' checks and documents", () => {
  it("routines and SQL objects need a body; a SQL object its kind; an enum type a unique label", () => {
    expect(databaseObjectProblems(base({ body: " " }), [], null, null).body).toBe("Enter the routine's body.");
    expect(databaseObjectProblems(base({ kind: "sql-object", body: "grant x", objectKind: "" }), [], null, null)).toEqual({
      objectKind: "Say what the object is (trigger, grant, extension…).",
    });
    expect(databaseObjectProblems(base({ kind: "database-type", typeKind: "enum", members: " , " }), [], null, null).members).toBe("Enter at least one label.");
    expect(databaseObjectProblems(base({ kind: "database-type", typeKind: "enum", members: "a, b, a" }), [], null, null).members).toBe(
      "Each label appears once.",
    );
    expect(databaseObjectProblems(base({ kind: "database-type", typeKind: "domain" }), [], null, null)).toEqual({});
  });

  it("checks names within their group: tables, views, sequences and types share names; routines and SQL objects have their own", () => {
    const taken = [
      { schema: "public", name: "invoices", kind: "table" },
      { schema: "public", name: "email", kind: "database-type" },
      { schema: "public", name: "invoice_total", kind: "routine" },
    ];
    expect(databaseObjectProblems(base({ name: "Invoice_Total", body: "x" }), taken, null, "public").name).toBe(
      "invoice_total is already a routine in public.",
    );
    expect(databaseObjectProblems(base({ kind: "database-type", name: "invoices" }), taken, null, "public").name).toBe(
      "invoices is already a table, view or sequence in public.",
    );
    expect(databaseObjectProblems(base({ kind: "view", name: "email", body: "select 1" }), taken, null, "public").name).toBe(
      "email is already a database type in public.",
    );
    // A SQL object may share a table's name.
    expect(databaseObjectProblems(base({ kind: "sql-object", name: "invoices", objectKind: "grant", body: "x" }), taken, null, "public")).toEqual({});
  });

  it("builds a routine with its kind, result and body; a procedure returns nothing", () => {
    expect(routineBodyTemplate("postgresql", "function")).toContain("return 0;");
    expect(routineBodyTemplate("sqlserver", "procedure")).toContain("set nocount on;");
    expect(buildDatabaseObject(base({ routineKind: "function", returns: "decimal", dialect: "postgresql", body: "begin end" }), () => "R1")).toEqual({
      kind: "routine",
      id: "R1",
      name: "invoice_total",
      database: "db1",
      returns: { type: "decimal" },
      body: { postgresql: "begin end" },
    });
    expect(buildDatabaseObject(base({ routineKind: "procedure", returns: "int32", dialect: "*", body: "x" }), ids)).toMatchObject({
      routineKind: "procedure",
      body: { "*": "x" },
    });
    expect(buildDatabaseObject(base({ routineKind: "procedure", returns: "int32", dialect: "*", body: "x" }), ids).returns).toBeUndefined();
  });

  it("builds a database type with what its kind needs to be created", () => {
    expect(buildDatabaseObject(base({ kind: "database-type", name: "email", typeKind: "domain", base: "string" }), () => "T1")).toEqual({
      kind: "database-type",
      id: "T1",
      name: "email",
      database: "db1",
      typeKind: "domain",
      base: "string",
    });
    expect(buildDatabaseObject(base({ kind: "database-type", typeKind: "enum", members: "draft, paid" }), ids)).toMatchObject({ members: ["draft", "paid"] });
    expect(typeKindDefaults("range")).toEqual({ subtype: "int32" });
    expect(typeKindDefaults("composite")).toEqual({ fields: [{ name: "value", type: "string" }] });
    expect(typeKindDefaults("enum")).toEqual({ members: ["value_1"] });
    expect(parseMembers(" a ,, b ")).toEqual(["a", "b"]);
  });

  it("builds a SQL object with its kind, phase (after left out) and statements", () => {
    expect(
      buildDatabaseObject(base({ kind: "sql-object", name: "ext", objectKind: " extension ", phase: "before", dialect: "postgresql", body: "x" }), () => "O1"),
    ).toEqual({
      kind: "sql-object",
      id: "O1",
      name: "ext",
      database: "db1",
      objectKind: "extension",
      phase: "before",
      body: { postgresql: "x" },
    });
    expect(buildDatabaseObject(base({ kind: "sql-object", objectKind: "grant", phase: "after", body: "x" }), ids).phase).toBeUndefined();
  });
});

describe("the editors' edits", () => {
  it("sets and removes per-dialect texts; a required body keeps one, an optional definition goes when empty", () => {
    const doc: Json = { kind: "database-type" };
    setDialectText(doc, "definition", "postgresql", "AS ENUM ('a')");
    expect(dialectKeys(doc, "definition")).toEqual(["postgresql"]);
    expect(removeDialectText(doc, "definition", "postgresql", false)).toBe(true);
    expect(doc.definition).toBeUndefined();
    const routine: Json = { body: { postgresql: "x" } };
    expect(removeDialectText(routine, "body", "postgresql", true)).toBe(false);
  });

  it("edits typed rows: facets are whole numbers, a row keeps a type or a native type, rows add, move and go", () => {
    expect(facetProblem("length", "0")).toBe("At least 1.");
    expect(facetProblem("scale", "0")).toBeNull();
    expect(facetProblem("precision", "1.5")).toBe("A whole number, or empty.");
    const doc: Json = {};
    addTypedRow(doc, "parameters", "param", "int32");
    addTypedRow(doc, "parameters", "param", "int32");
    expect(doc.parameters).toEqual([
      { name: "param_1", type: "int32" },
      { name: "param_2", type: "int32" },
    ]);
    const row = (doc.parameters as Json[])[0];
    expect(setRowType(row, "")).toBe(false);
    expect(setRowNativeType(row, " citext ")).toBe(true);
    expect(setRowType(row, "")).toBe(true);
    expect(row).toEqual({ name: "param_1", nativeType: "citext" });
    expect(setRowNativeType(row, "")).toBe(false);
    expect(setFacet(row, "length", "40")).toBe(true);
    expect(row.length).toBe(40);
    expect(moveRow(doc, "parameters", 1, -1)).toBe(true);
    expect((doc.parameters as Json[]).map((p) => p.name)).toEqual(["param_2", "param_1"]);
    expect(moveRow(doc, "parameters", 0, -1)).toBe(false);
    removeRow(doc, "parameters", 0);
    removeRow(doc, "parameters", 0);
    expect(doc.parameters).toBeUndefined();
  });

  it("switches a routine's result and kind, and its dependencies", () => {
    const doc: Json = {};
    expect(returnsShape(doc)).toBe("none");
    setReturnsShape(doc, "table");
    expect(returnsShape(doc)).toBe("table");
    setReturnsShape(doc, "value");
    expect(doc.returns).toEqual({ type: "int32" });
    setRoutineKind(doc, "procedure");
    expect(doc.routineKind).toBe("procedure");
    setRoutineKind(doc, "function");
    expect(doc.routineKind).toBeUndefined();
    toggleDependency(doc, "V1");
    toggleDependency(doc, "V1");
    expect(doc.dependsOn).toEqual(["V1"]);
    toggleDependency(doc, "V1", true);
    expect(doc.dependsOn).toBeUndefined();
  });

  it("switches a database type's kind, keeping only what the new kind uses, and edits an enum's labels", () => {
    const doc: Json = { typeKind: "domain", base: "string", length: 320, check: "VALUE <> ''", nativeName: "email" };
    setTypeKind(doc, "enum", typeKindDefaults("enum"));
    expect(doc).toEqual({ typeKind: "enum", members: ["value_1"], nativeName: "email" });
    addMember(doc);
    expect(doc.members).toEqual(["value_1", "value_2"]);
    expect(memberProblem(doc.members as string[], 1, "value_1")).toBe("value_1 is already a label.");
    expect(memberProblem(doc.members as string[], 0, " ")).toBe("A label cannot be empty.");
    expect(memberProblem(doc.members as string[], 0, "value_1")).toBeNull();
  });

  it("offers the database's other objects as dependencies, by kind then name, each table by its table name", () => {
    const rows = [
      { id: "E1", kind: "entity", name: "Invoice" },
      { id: "T1", kind: "table", name: "", database: "db1", entity: "E1" },
      { id: "V1", kind: "view", name: "open", database: "db1" },
      { id: "R1", kind: "routine", name: "self", database: "db1" },
      { id: "R2", kind: "routine", name: "a_fn", database: "db1" },
      { id: "X1", kind: "view", name: "other", database: "db2" },
    ];
    // A file adjusting a laid-out table is named by the table, never by an entity.
    expect(dependencyOptions(rows, "db1", "R1", new Map([["E1@db1", "invoices"]]))).toEqual([
      { id: "T1", kind: "table", name: "invoices" },
      { id: "V1", kind: "view", name: "open" },
      { id: "R2", kind: "routine", name: "a_fn" },
    ]);
    expect(dependencyOptions(rows, "db1", "R1").find((o) => o.id === "T1")?.name).toBe("T1");
    expect(dependencyOptions(rows, "db1", "R1").some((o) => /Invoice/.test(o.name))).toBe(false);
  });

  it("says what a type slot and a database type are in one line", () => {
    expect(typeText({ type: "decimal", precision: 18, scale: 2 }, () => undefined)).toBe("decimal(18,2)");
    expect(typeText({ type: "T1" }, (id) => (id === "T1" ? "email" : undefined))).toBe("email");
    expect(typeText({ nativeType: "citext" }, () => undefined)).toBe("citext");
    expect(typeSummary({ typeKind: "domain", base: "string", length: 320, check: "x" })).toBe("Domain over string(320) with a check");
    expect(typeSummary({ typeKind: "enum", members: ["a", "b"] })).toBe("Enum: a, b");
    expect(typeSummary({ typeKind: "composite", fields: [{}] })).toBe("Composite of 1 field");
    expect(typeSummary({ typeKind: "range", subtype: "date" })).toBe("Range over date");
  });
});

describe("the Database screen and generation", () => {
  it("lists the kinds by chip, and previews each through the pack's each-<kind> unit", () => {
    expect(LIST_KINDS.map((k) => k.label)).toEqual(["Tables", "Views", "Sequences", "Routines", "Queries", "Types", "Objects"]);
    expect(OBJECT_LIST_MEMBERS["database-type"]).toBe("types");
    const ddl = {
      name: "sql-ddl",
      units: [
        { id: "schema", for: "select databases" },
        { id: "routine", for: "each routine" },
        { id: "database-type", for: "each database type" },
        { id: "sql-object", for: "each sql object" },
      ],
    };
    expect(ddlPreviewTarget([ddl], "db1", null, { kind: "routine", id: "R1" })).toEqual({
      pack: "sql-ddl",
      unit: "routine",
      elementId: "R1",
      scope: "routine",
    });
    const type = ddlPreviewTarget([ddl], "db1", null, { kind: "database-type", id: "T1" })!;
    expect(type).toMatchObject({ unit: "database-type", scope: "database-type" });
    expect(ddlPreviewCaption(type, "email")).toBe("sql-ddl/database-type · email");
    expect(ddlPreviewTarget([ddl], "db1", null, { kind: "sql-object", id: "O1" })).toMatchObject({ unit: "sql-object", scope: "sql-object" });
  });

  it("knows the each-routine, each-database-type and each-sql-object scopes", () => {
    expect(unitScope("each database type")).toMatchObject({ indexKind: "database-type", variable: "database_type", once: false });
    expect(unitScope("each sql object")).toMatchObject({ indexKind: "sql-object", variable: "sql_object" });
    expect(unitScope("each routine").indexKind).toBe("routine");
    expect(scopeHelp("each routine")).toBe("Runs once per routine, database type or SQL object of every database; one file each.");
  });
});

describe("the mock", () => {
  it("resolves the billing database's routines, types and SQL object into the database view", () => {
    const backend = new MockBackend();
    const db = backend.model.index().find((r) => r.kind === "database" && r.name === "main")!;
    const view = backend.generation.databaseView(db.id)!.view!;
    expect(view.routines!.map((r) => [r.id, r.routineKind])).toEqual([
      [CLOSE_PERIOD, "procedure"],
      [INVOICE_TOTAL, "function"],
    ]);
    const total = view.routines!.find((r) => r.id === INVOICE_TOTAL)!;
    expect(total).toMatchObject({
      schema: "billing",
      language: "plpgsql",
      hasBody: true,
      returns: { type: "decimal", nativeType: "numeric(18,2)", table: null },
    });
    expect(total.parameters[0]).toMatchObject({ name: "p_invoice", type: "uuid", nativeType: "uuid", mode: "in" });
    expect(view.types!.map((t) => [t.id, t.typeKind, t.nativeName])).toEqual([
      [EMAIL_ADDRESS, "domain", "billing.email_address"],
      [INVOICE_STATE, "enum", "billing.invoice_state"],
    ]);
    expect(view.objects!.map((o) => [o.id, o.phase, o.hasBody])).toEqual([[REPORTING_READ, "after", true]]);
    expect(typeSql(view.types![0], "postgresql")).toBe("CREATE DOMAIN billing.email_address AS varchar(320) CHECK (VALUE like '%@%');");
    expect(typeSql(view.types![1], "postgresql")).toBe("CREATE TYPE billing.invoice_state AS ENUM ('draft', 'issued', 'paid', 'void');");
    expect(routineSql(total, "postgresql")).toContain("CREATE FUNCTION billing.invoice_total(p_invoice uuid) RETURNS numeric(18,2)\nLANGUAGE plpgsql");
    expect(objectSql(view.objects![0])).toBe("grant select on billing.outstanding_invoices to reporting;");
  });

  it("writes new routines, types and objects under their database's folders", () => {
    const model = new MockBackend().model;
    const db = model.index().find((r) => r.kind === "database" && r.name === "main")!;
    for (const [kind, folder, extra] of [
      ["routine", "routines", { body: { "*": "x" } }],
      ["database-type", "types", { typeKind: "range", subtype: "date" }],
      ["sql-object", "objects", { objectKind: "grant", body: { "*": "x" } }],
    ] as const) {
      const id = `01JQZZZ00000000000000000${kind === "routine" ? "01" : kind === "database-type" ? "02" : "03"}`;
      const result = model.create({ kind, id, name: `new_${folder}`, database: db.id, ...extra } as never);
      expect(result.outcome).toBe("saved");
      expect(model.entries.get(id)?.path).toBe(`.maquettiste/model/databases/main/${folder}/new-${folder}.json`);
    }
  });
});

describe("a database type's definition per dialect", () => {
  it("starts from the structured form with the dialect's native types", () => {
    expect(definitionTemplate({ typeKind: "enum", members: ["a", "it's"] }, undefined)).toBe("AS ENUM ('a', 'it''s')");
    expect(definitionTemplate({ typeKind: "domain", base: "string", check: "VALUE <> ''" }, { baseNativeType: "varchar(320)" })).toBe(
      "AS varchar(320) CHECK (VALUE <> '')",
    );
    expect(definitionTemplate({ typeKind: "range", subtype: "date" }, { subtypeNativeType: "date" })).toBe("AS RANGE (SUBTYPE = date)");
    expect(definitionTemplate({ typeKind: "composite" }, { fields: [{ name: "x", nativeType: "integer" }] })).toBe("AS (x integer)");
  });
});
