// The database designer's plans, pure: identifiers in hand-written SQL (sqlIdentifiers.ts), what deleting a table part takes
// with it (partDelete.ts), what a column rename rewrites (renames.ts), and how a column gets its values (columnEdits.ts).
import { describe, expect, it } from "vitest";
import type { ColumnView, TableView } from "@/api/types";
import { isKeyOf, keysOfDocument } from "@/model/foreignKeyTarget";
import { mentions, renameIdentifier, renameInDialectMap } from "@/workspaces/database/sqlIdentifiers";
import { planPartDelete, planReachesFurther } from "@/workspaces/database/partDelete";
import { planColumnRename, renameBindingColumns, renamedOutputColumns } from "@/workspaces/database/renames";
import { columnGeneration, columnGenerationProblems, setColumnField } from "@/workspaces/database/columnEdits";
import { joinUndo } from "@/workspaces/database/tableBatch";

type Json = Record<string, unknown>;

describe("identifiers in SQL text", () => {
  it("finds a name as a word or quoted, not in strings, comments or as a call", () => {
    expect(mentions("Email IS NOT NULL", "email")).toBe(true);
    expect(mentions(`"email" <> ''`, "email")).toBe(true);
    expect(mentions(`"Email" <> ''`, "email")).toBe(false);
    expect(mentions("[email] > 0 -- email", "email")).toBe(true);
    expect(mentions("'email' = x /* email */", "email")).toBe(false);
    expect(mentions("lower(name)", "lower")).toBe(false);
    expect(mentions("email_address IS NULL", "email")).toBe(false);
  });

  it("renames each form the way it was written and counts them", () => {
    expect(renameIdentifier(`email LIKE '%email%' AND "email" <> [email] AND \`email\` IS NOT NULL`, "email", "mail")).toEqual({
      text: `mail LIKE '%email%' AND "mail" <> [mail] AND \`mail\` IS NOT NULL`,
      count: 4,
      uncertain: false,
    });
    expect(renameIdentifier("t.email = s.email", "EMAIL", "mail").count).toBe(2);
    // A name that is also a keyword is never rewritten blindly.
    expect(renameIdentifier("CAST(x AS date) > date", "date", "day_of")).toEqual({ text: "CAST(x AS date) > date", count: 0, uncertain: true, why: "keyword" });
    expect(renameInDialectMap({ postgresql: "a > 0", "*": "a >= 1" }, "a", "b")).toEqual({
      map: { postgresql: "b > 0", "*": "b >= 1" },
      count: 2,
      uncertain: false,
    });
  });
});

const table = (): Json => ({
  kind: "table",
  id: "T",
  name: "customers",
  database: "DB",
  columns: [
    { id: "C_ID", name: "id", type: "int64", nullable: false },
    { id: "C_REGION", name: "region", type: "string" },
    { id: "C_EMAIL", name: "email", type: "string" },
    { id: "C_PARENT", name: "parent_id", type: "int64" },
    { id: "C_LOWER", name: "email_lower", type: "string", computed: "lower(email)" },
  ],
  primaryKey: { name: "pk_customers", columns: ["C_ID", "C_REGION"] },
  uniques: [{ id: "U", name: "uq_email", columns: ["C_EMAIL"] }],
  indexes: [
    { id: "I1", name: "ix_region", columns: [{ column: "C_REGION" }] },
    { id: "I2", name: "ix_set", columns: [{ column: "C_PARENT" }], where: "email IS NOT NULL" },
  ],
  foreignKeys: [{ id: "SELF", name: "fk_parent", columns: ["C_PARENT", "C_REGION"], referencesTable: "T", referencesColumns: ["C_ID", "C_REGION"] }],
  checks: [
    { id: "K1", name: "ck_email", expression: { "*": "email LIKE '%@%'" } },
    { id: "K2", name: "ck_region", column: "C_REGION", expression: { "*": "region <> ''" } },
  ],
});

describe("a part's delete plan", () => {
  const others: Json[] = [
    {
      kind: "table",
      id: "O",
      name: "orders",
      foreignKeys: [
        { id: "FK_O", name: "fk_orders_customer", columns: ["X", "Y"], referencesTable: "T" },
        { id: "FK_O2", name: "fk_orders_email", columns: ["Z"], referencesTable: "T", referencesColumns: ["C_EMAIL"] },
      ],
    },
    { kind: "mapping", id: "M", name: "places in main", foreignKey: "FK_O" },
    {
      kind: "entity",
      id: "E",
      name: "Customer",
      attributes: [{ id: "A_EMAIL", name: "email" }],
      bindings: [
        {
          id: "B",
          database: "DB",
          source: "T",
          fields: [{ attribute: "A_EMAIL", column: "C_EMAIL" }],
          constants: [{ column: "region", value: "eu" }],
          delete: { soft: { column: "region", value: "gone" } },
        },
      ],
    },
    { kind: "query", id: "Q", name: "ByRegion", from: { source: "T", alias: "c" }, where: { op: "eq", left: { column: "c.region" }, right: { param: "r" } } },
    { kind: "view", id: "V", name: "customer_emails", body: { "*": "SELECT email FROM customers" } },
  ];

  it("takes a column's whole keys, indexes and checks, other tables' keys to them, and what binds it; a query blocks it", () => {
    const plan = planPartDelete({ table: table(), part: { kind: "column", id: "C_REGION" }, docs: others, dialect: "postgresql" });
    expect(plan.subject).toBe("column region");
    expect(plan.table).toEqual([
      "primary key pk_customers (region is in it)",
      "index ix_region (region is in it)",
      "foreign key fk_parent (region is in it)",
      "check ck_region (it checks region)",
    ]);
    // The orders' key referenced the primary key (no referenced columns): it goes, and the mapping naming it no longer does.
    expect(plan.others.map((o) => [o.id, o.lines])).toEqual([["O", ["foreign key fk_orders_customer goes (it references customers.region)"]]]);
    // The storage bindings that follow are counted, never named.
    expect(plan.storage.map((o) => [o.id, o.name, o.lines])).toEqual([
      ["M", "", ["no longer names foreign key fk_orders_customer"]],
      ["E", "", ["its constant on region goes", "its soft delete on region goes (it deletes by key)"]],
    ]);
    expect(JSON.stringify([plan.table, plan.others, plan.storage, plan.warnings, plan.blockers])).not.toContain("Customer");
    expect(plan.blockers).toEqual(["Query ByRegion reads customers.region: change it first (it would no longer resolve)."]);
    const doc = table();
    plan.edits.get("T")!(doc);
    expect((doc.columns as Json[]).map((c) => c.id)).toEqual(["C_ID", "C_EMAIL", "C_PARENT", "C_LOWER"]);
    expect(doc.primaryKey).toBeUndefined();
    expect(doc.foreignKeys).toBeUndefined();
    expect((doc.checks as Json[]).map((c) => c.id)).toEqual(["K1"]);
    const entity = structuredClone(others[2]);
    plan.edits.get("E")!(entity);
    expect((entity.bindings as Json[])[0]).toEqual({ id: "B", database: "DB", source: "T", fields: [{ attribute: "A_EMAIL", column: "C_EMAIL" }] });
  });

  it("takes checks and filters that read a column by name, and says what to check by hand", () => {
    const plan = planPartDelete({ table: table(), part: { kind: "column", id: "C_EMAIL" }, docs: others, dialect: "postgresql" });
    expect(plan.table).toEqual([
      "unique constraint uq_email (email is in it)",
      "index ix_set (its filter reads email)",
      "check ck_email (its expression reads email)",
    ]);
    expect(plan.others.map((o) => o.id)).toEqual(["O"]);
    expect(plan.storage.map((o) => [o.id, o.lines])).toEqual([["E", ["a field no longer reads email"]]]);
    expect(plan.warnings).toEqual([
      "Column email_lower of customers is computed from email: change its expression.",
      "View customer_emails names email in its body: check it.",
    ]);
  });

  it("takes the keys to a primary key that goes, a self key included; a unique key the same way", () => {
    const pk = planPartDelete({ table: table(), part: { kind: "primary-key", id: "primary-key" }, docs: others, dialect: "postgresql" });
    expect(pk.table).toEqual(["foreign key fk_parent (the key it references goes)"]);
    expect(pk.others.map((o) => o.id)).toEqual(["O"]);
    expect(pk.storage.map((o) => o.id)).toEqual(["M"]);
    const uq = planPartDelete({ table: table(), part: { kind: "unique", id: "uq_email" }, docs: others, dialect: "postgresql" });
    expect(uq.others.map((o) => [o.id, o.lines])).toEqual([["O", ["foreign key fk_orders_email goes (it references customers's unique constraint)"]]]);
    // An index nothing references goes alone: no plan to show.
    expect(planReachesFurther(planPartDelete({ table: table(), part: { kind: "index", id: "ix_region" }, docs: others, dialect: "postgresql" }))).toBe(false);
  });

  it("reads a table's keys for what a foreign key may reference", () => {
    const keys = keysOfDocument(table());
    expect(isKeyOf(keys, ["C_REGION", "C_ID"], "postgresql")).toBe(true);
    expect(isKeyOf(keys, ["C_ID"], "postgresql")).toBe(false);
    expect(isKeyOf(keys, ["C_EMAIL"], "oracle")).toBe(true);
  });
});

describe("a column's rename", () => {
  const view = (name: string, columns: string[]): TableView =>
    ({ key: name, name, columns: columns.map((c) => ({ key: c, name: c })) }) as unknown as TableView;

  it("rewrites the table's SQL texts, the views where it is certain and the bindings that name it, and lists the rest", () => {
    const doc = table();
    (doc.columns as Json[])[2].defaultSql = { postgresql: "'x@' || email" };
    const docs: Json[] = [
      { kind: "view", id: "V1", name: "emails", body: { "*": "SELECT c.email AS mail FROM customers c" } },
      { kind: "view", id: "V2", name: "joined", body: { "*": "SELECT email FROM customers JOIN suppliers ON true" } },
      { kind: "view", id: "V3", name: "plain", body: { "*": "SELECT email FROM customers" }, columns: [{ name: "email" }] },
      { kind: "view", id: "V4", name: "other", body: { "*": "SELECT email FROM suppliers" } },
      { kind: "entity", id: "E", name: "Customer", bindings: [{ id: "B", database: "DB", source: "T", fields: [{ attribute: "A", column: "email" }] }] },
    ];
    const plan = planColumnRename({ table: doc, column: "C_EMAIL", to: "mail", docs, tables: [view("T", ["email"]), view("suppliers", ["email"])] });
    expect(plan.rewritten).toEqual([
      "check ck_email",
      "the filter of index ix_set",
      "the default of column mail",
      "the expression of column email_lower",
      "view emails (1 place)",
      "view plain (1 place)",
    ]);
    expect(plan.bindings).toEqual(["E"]);
    expect(plan.check).toEqual(["view joined (it also reads suppliers, which has a column email)"]);
    plan.edits.get("T")!(doc);
    expect((doc.columns as Json[])[2]).toMatchObject({ name: "mail", defaultSql: { postgresql: "'x@' || mail" } });
    expect((doc.columns as Json[])[4].computed).toBe("lower(mail)");
    const v1 = structuredClone(docs[0]);
    plan.edits.get("V1")!(v1);
    expect(v1.body).toEqual({ "*": "SELECT c.mail AS mail FROM customers c" });
    // The view returns the column under its own name: it keeps that name.
    const v3 = structuredClone(docs[2]);
    plan.edits.get("V3")!(v3);
    expect(v3.body).toEqual({ "*": "SELECT mail AS email FROM customers" });
    expect([...plan.edits.keys()]).toEqual(["T", "V1", "V3", "E"]);
  });

  it("follows a view's or query's renamed column in the bindings that read it", () => {
    expect([
      ...renamedOutputColumns({ kind: "view", columns: [{ name: "a" }, { name: "b" }] }, { kind: "view", columns: [{ name: "a" }, { name: "c" }] }),
    ]).toEqual([["b", "c"]]);
    const before = { kind: "query", select: [{ name: "x", expression: { column: "t.a" } }] };
    expect([...renamedOutputColumns(before, { kind: "query", select: [{ name: "y", expression: { column: "t.a" } }] })]).toEqual([["x", "y"]]);
    expect(renamedOutputColumns(before, { kind: "query", select: [{ name: "y", expression: { column: "t.b" } }] }).size).toBe(0);
    const entity: Json = { name: "E", bindings: [{ source: "V", fields: [{ attribute: "A", column: "B" }], columns: [{ column: "b", status: "ignored" }] }] };
    expect(renameBindingColumns(entity, "V", new Map([["b", "c"]]))).toBe(2);
    expect((entity.bindings as Json[])[0]).toEqual({ source: "V", fields: [{ attribute: "A", column: "c" }], columns: [{ column: "c", status: "ignored" }] });
  });
});

describe("how a column gets its values", () => {
  const column = {
    key: "C",
    name: "n",
    type: "int64",
    default: null,
    identity: false,
    sequenceId: null,
    computed: null,
    computedStored: false,
    collation: null,
    defaultSql: null,
  } as unknown as ColumnView;

  it("reads the file's entry, else the resolved column, and says what does not fit", () => {
    expect(columnGeneration({ generated: "sequence", sequence: "S", defaultSql: { "*": "1" } }, column, "postgresql")).toMatchObject({
      generated: "sequence",
      sequence: "S",
      defaultSql: { "*": "1" },
    });
    expect(columnGeneration(null, { ...column, identity: true, defaultSql: "now()" }, "postgresql")).toMatchObject({
      generated: "identity",
      defaultSql: { postgresql: "now()" },
    });
    const g = columnGeneration({ generated: "sequence", computed: "a + 1", defaultSql: { "*": "0" } }, column, "postgresql");
    expect(columnGenerationProblems(g, { type: "int64", default: null })).toEqual([
      "Pick the sequence that supplies the values.",
      "A computed column is neither an identity nor sequence-generated: choose one.",
      "A computed column has no default: its expression gives its value.",
      "A sequence-generated column takes no other default.",
    ]);
    expect(columnGenerationProblems(columnGeneration({ generated: "identity" }, column, "postgresql"), { type: "string", default: null })).toEqual([
      "An identity column takes an integer type; this one is string.",
    ]);
  });

  it("writes them: a sequence makes the column sequence-generated, a per-dialect default replaces the literal one", () => {
    const doc: Json = { kind: "table", id: "T", columns: [{ id: "C", name: "n", type: "int64", default: 5 }] };
    const entry = () => (doc.columns as Json[])[0];
    setColumnField(doc, column, "sequence", "SEQ", () => "x");
    expect(entry()).toMatchObject({ generated: "sequence", sequence: "SEQ" });
    setColumnField(doc, column, "generated", "identity", () => "x");
    expect(entry().sequence).toBeUndefined();
    expect(entry().generated).toBe("identity");
    setColumnField(doc, column, "defaultSql", { dialect: "postgresql", text: "now()" }, () => "x");
    expect(entry()).toMatchObject({ defaultSql: { postgresql: "now()" } });
    expect(entry().default).toBeUndefined();
    setColumnField(doc, column, "defaultSql", { dialect: "postgresql", text: "" }, () => "x");
    expect(entry().defaultSql).toBeUndefined();
    setColumnField(doc, column, "computed", "a + 1", () => "x");
    setColumnField(doc, column, "computedStored", true, () => "x");
    expect(entry()).toMatchObject({ computed: "a + 1", computedStored: true });
    setColumnField(doc, column, "computed", "", () => "x");
    expect(entry().computedStored).toBeUndefined();
    setColumnField(doc, column, "collation", "C", () => "x");
    expect(entry().collation).toBe("C");
  });
});

describe("one undo step for a gesture that stores a table first", () => {
  it("joins a batch's step to the store's, an element in both keeping its first before", () => {
    const top = {
      label: "Store",
      ids: ["T", "E"],
      before: [null, { id: "E" }],
      after: [
        { id: "T", v: 1 },
        { id: "E", b: 1 },
      ],
      afterHashes: ["h1", "h2"],
    };
    const next = {
      label: "Delete column x",
      ids: ["T", "O"],
      before: [{ id: "T", v: 1 }, { id: "O" }],
      after: [
        { id: "T", v: 2 },
        { id: "O", v: 2 },
      ],
      afterHashes: ["h3", "h4"],
    };
    expect(joinUndo(top as never, next as never)).toEqual({
      label: "Delete column x",
      ids: ["T", "E", "O"],
      before: [null, { id: "E" }, { id: "O" }],
      after: [
        { id: "T", v: 2 },
        { id: "E", b: 1 },
        { id: "O", v: 2 },
      ],
      afterHashes: ["h3", "h2", "h4"],
    });
  });
});
