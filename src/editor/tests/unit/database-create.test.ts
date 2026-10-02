// Creating in a database: the New dialogs' checks and documents (databaseCreate.ts), the menus that offer them, the Database
// screen's list of views and sequences, and its DDL preview for a picked view or sequence.
import { describe, expect, it } from "vitest";
import {
  buildDatabaseObject,
  DATABASE_CREATE,
  DATABASE_CREATE_LABELS,
  databaseFolderCreate,
  databaseObjectProblems,
  viewBodyTemplate,
  type DatabaseObjectInput,
} from "@/explorer/databaseCreate";
import { menuFor, type MenuTarget } from "@/explorer/menus";
import { databaseTargetOf, ddlPreviewCaption, ddlPreviewTarget, emptyObjectUnitNote } from "@/workspaces/database/ddlPreview";
import { filterObjects } from "@/workspaces/database/tableList";
import { inspectorContext } from "@/inspector/context";
import { createEditorStore } from "@/state/store";

const ids = (() => {
  let n = 0;
  return () => `ID${++n}`;
})();

const base = (over: Partial<DatabaseObjectInput>): DatabaseObjectInput => ({ kind: "table", name: "audit_log", database: "db1", schema: null, ...over });
const taken = [
  { schema: "public", name: "invoices" },
  { schema: "billing", name: "open_invoices" },
];

describe("the New dialogs' checks", () => {
  it("needs a name that follows the identifier rule", () => {
    expect(databaseObjectProblems(base({ name: "" }), [], null, "public").name).toBe("Enter a name.");
    expect(databaseObjectProblems(base({ name: "2fast" }), [], null, "public").name).toBe("Use letters, digits and underscores, not starting with a digit.");
    expect(databaseObjectProblems(base({ name: "audit log" }), [], null, "public").name).toMatch(/^Use letters/);
    expect(databaseObjectProblems(base({ name: "audit_log" }), [], null, "public")).toEqual({});
  });

  it("refuses a name a table, view or sequence already has in the same schema, case-insensitively", () => {
    expect(databaseObjectProblems(base({ name: "Invoices" }), taken, null, "public").name).toBe("invoices is already a table, view or sequence in public.");
    // The same name in another schema is fine.
    expect(databaseObjectProblems(base({ name: "invoices" }), taken, "billing", "public")).toEqual({});
    expect(databaseObjectProblems(base({ kind: "view", name: "open_invoices", body: "select 1" }), taken, "billing", "public").name).toMatch(/already/);
  });

  it("needs a view body, and whole-number sequence values with a non-zero increment", () => {
    expect(databaseObjectProblems(base({ kind: "view", body: "  " }), [], null, null).body).toBe("Enter the view's SQL body.");
    expect(databaseObjectProblems(base({ kind: "view", body: "select 1" }), [], null, null)).toEqual({});
    expect(databaseObjectProblems(base({ kind: "sequence", start: "1.5", increment: "1" }), [], null, null).start).toBe("Start is a whole number.");
    expect(databaseObjectProblems(base({ kind: "sequence", start: "-10", increment: "0" }), [], null, null)).toEqual({ increment: "Increment cannot be 0." });
    expect(databaseObjectProblems(base({ kind: "sequence", start: "", increment: "x" }), [], null, null)).toEqual({
      increment: "Increment is a whole number.",
    });
    expect(databaseObjectProblems(base({ kind: "sequence", start: "1000", increment: "-1" }), [], null, null)).toEqual({});
  });
});

describe("the documents they create", () => {
  it("a designed table, with or without a starting id column as its primary key", () => {
    expect(buildDatabaseObject(base({ name: " audit_log ", schema: "S1" }), () => "T1")).toEqual({
      kind: "table",
      id: "T1",
      name: "audit_log",
      database: "db1",
      schema: "S1",
    });
    const next = ["T2", "C1"];
    expect(buildDatabaseObject(base({ withIdColumn: true }), () => next.shift()!)).toEqual({
      kind: "table",
      id: "T2",
      name: "audit_log",
      database: "db1",
      columns: [{ id: "C1", name: "id", type: "int64", nullable: false }],
      primaryKey: { columns: ["C1"] },
    });
  });

  it("a view with its body for one dialect (or every dialect), prefilled for the database's", () => {
    expect(viewBodyTemplate("postgresql")).toBe("select 1 as id");
    expect(viewBodyTemplate("oracle")).toBe("select 1 as id from dual");
    expect(buildDatabaseObject(base({ kind: "view", name: "v", dialect: "sqlserver", body: "select 1 as id" }), ids)).toMatchObject({
      kind: "view",
      body: { sqlserver: "select 1 as id" },
    });
    expect(buildDatabaseObject(base({ kind: "view", name: "v", dialect: "*", body: "select 2" }), ids).body).toEqual({ "*": "select 2" });
  });

  it("a sequence, leaving out the defaults (int64, start 1, increment 1)", () => {
    const plain = buildDatabaseObject(base({ kind: "sequence", name: "s", type: "int64", start: "1", increment: "1" }), () => "Q1");
    expect(plain).toEqual({ kind: "sequence", id: "Q1", name: "s", database: "db1" });
    expect(buildDatabaseObject(base({ kind: "sequence", name: "s", type: "int32", start: "1000", increment: "-5" }), ids)).toMatchObject({
      type: "int32",
      start: 1000,
      increment: -5,
    });
  });
});

describe("the menus that offer them", () => {
  const menu = (t: MenuTarget) => menuFor([t]).map((i) => `${i.id} ${i.label}`);

  it("a database row offers New schema…, New table…, New view…, New sequence…, New routine…, New database type… and New SQL object…", () => {
    expect(DATABASE_CREATE.map((k) => DATABASE_CREATE_LABELS[k])).toEqual([
      "New schema…",
      "New table…",
      "New view…",
      "New sequence…",
      "New routine…",
      "New database type…",
      "New SQL object…",
    ]);
    expect(menu({ type: "database", kind: "database", element: true })).toEqual(
      expect.arrayContaining([
        "new-db:schema New schema…",
        "new-db:table New table…",
        "new-db:view New view…",
        "new-db:sequence New sequence…",
        "new-db:routine New routine…",
        "new-db:database-type New database type…",
        "new-db:sql-object New SQL object…",
      ]),
    );
  });

  it("a schema row and a kind folder of a database offer the kind they hold", () => {
    expect(menu({ type: "schema", element: false })).toEqual([
      "new-db:table New table…",
      "new-db:view New view…",
      "new-db:sequence New sequence…",
      "new-db:routine New routine…",
      "new-db:database-type New database type…",
      "new-db:sql-object New SQL object…",
      "expand-all Expand all",
    ]);
    expect(databaseFolderCreate("routine")).toBe("routine");
    expect(databaseFolderCreate("database-type")).toBe("database-type");
    expect(menu({ type: "folder", kind: "sql-object", element: false, home: "databases" })[0]).toBe("new-db:sql-object New SQL object…");
    expect(databaseFolderCreate("view")).toBe("view");
    expect(databaseFolderCreate("mapping")).toBeNull();
    expect(menu({ type: "folder", kind: "sequence", element: false, home: "databases" })[0]).toBe("new-db:sequence New sequence…");
    expect(menu({ type: "group", kind: "table", element: false, home: "databases", explorer: "databases" })[0]).toBe("new-db:table New table…");
  });

  it("a table with a file of its own also offers Show in Database screen", () => {
    expect(menuFor([{ type: "table", kind: "table", element: true, designed: true }]).map((i) => i.id)).toEqual(["open", "show-in-database"]);
    expect(menuFor([{ type: "table", kind: "table", element: false, linked: true }]).map((i) => i.id)).toEqual(["open", "go-to-entity"]);
  });
});

describe("the Database screen", () => {
  it("lists views or sequences matching the filter, by name", () => {
    const views = [
      { id: "v2", name: "unpaid", schema: "billing" },
      { id: "v1", name: "open_invoices", schema: "billing" },
    ];
    expect(filterObjects(views, "").items.map((v) => v.id)).toEqual(["v1", "v2"]);
    expect(filterObjects(views, "billing open")).toEqual({ items: [views[1]], total: 1 });
  });

  it("previews a picked view or sequence through the pack's each-view or each-sequence unit", () => {
    const ddl = {
      name: "sql-ddl",
      units: [
        { id: "table", for: "each table" },
        { id: "schema", for: "select databases" },
        { id: "view", for: "each view" },
        { id: "sequence", for: "each sequence" },
      ],
    };
    const view = ddlPreviewTarget([ddl], "db1", "t1", { kind: "view", id: "v1" });
    expect(view).toEqual({ pack: "sql-ddl", unit: "view", elementId: "v1", scope: "view" });
    expect(ddlPreviewCaption(view!, "open_invoices")).toBe("sql-ddl/view · open_invoices");
    expect(ddlPreviewTarget([ddl], "db1", null, { kind: "sequence", id: "q1" })).toMatchObject({ unit: "sequence", scope: "sequence" });
    expect(emptyObjectUnitNote(view!, "open_invoices")).toBe(
      "sql-ddl/view writes no file for open_invoices with this project's parameters; the whole database below creates it.",
    );
    // A pack without the unit renders the whole database.
    const plain = { name: "ddl", units: [{ id: "schema", for: "select databases" }] };
    expect(ddlPreviewTarget([plain], "db1", null, { kind: "view", id: "v1" })).toEqual({ pack: "ddl", unit: "schema", elementId: "db1", scope: "database" });
    expect(databaseTargetOf([ddl], "db1")).toMatchObject({ unit: "schema", scope: "database" });
  });

  it("a view picked there shows in the inspector whichever explorer is shown", () => {
    const store = createEditorStore();
    store.getState().setSidebar("domain-model");
    store.getState().setWorkspace("database");
    store.getState().select(["view1"], undefined, "databases");
    expect(inspectorContext(store.getState())).toMatchObject({ mode: "element", ids: ["view1"] });
    // A rail switch afterwards shows that explorer's own selection again.
    store.getState().setSidebar("domain-model");
    expect(inspectorContext(store.getState()).mode).toBe("empty");
  });
});
