// What follows a rename or a delete through hand-written SQL and queries, pure: SQL text read per dialect with a role for each
// identifier (a column is rewritten, a table, an alias or a qualifier of the same name never is, and an uncertain reading is
// left to check), a view's output names kept, a query's `alias.column` references resolved to their sources, routines and SQL
// objects listed to check, a table's rename listing what names its old name; and the small rules around them (a refused part
// edit, a foreign key dialog whose table was stored meanwhile).
import { describe, expect, it } from "vitest";
import type { TableView } from "@/api/types";
import { mentions, mentionsColumn, renameIdentifier, renameOutputColumn, selectsAll } from "@/workspaces/database/sqlIdentifiers";
import { planColumnRename, planTableRename, renameViewOutput } from "@/workspaces/database/renames";
import { queryReadsColumn, queryTable, renameInQuery } from "@/workspaces/database/queryColumns";
import { planPartDelete } from "@/workspaces/database/partDelete";
import { editPart } from "@/workspaces/database/tableParts";
import { followStoredDraft, type FkDraft } from "@/workspaces/database/fkEdits";
import { renameNotice } from "@/workspaces/database/columnRename";

type Json = Record<string, unknown>;

describe("a column renamed in SQL text", () => {
  it("rewrites a column, never a table, an alias or a qualifier named like it", () => {
    const sql = "SELECT o.id FROM orders o JOIN customer ON customer.id = o.customer";
    expect(renameIdentifier(sql, "customer", "customer_id")).toEqual({
      text: "SELECT o.id FROM orders o JOIN customer ON customer.id = o.customer_id",
      count: 1,
      uncertain: false,
    });
    // An alias (after AS, or after a table) and a schema-qualified table are not columns.
    expect(renameIdentifier("SELECT c.email AS customer FROM billing.customer c", "customer", "x").count).toBe(0);
    expect(renameIdentifier("SELECT o.total FROM orders customer WHERE customer.total > 0", "customer", "x").count).toBe(0);
    // A bare use while the text also names a table that way: which is meant is not certain, so nothing is rewritten.
    expect(renameIdentifier("SELECT customer FROM orders JOIN customer ON customer.id = orders.customer", "customer", "customer_id")).toMatchObject({
      count: 0,
      uncertain: true,
      why: "ambiguous",
    });
    // A call and a type are not columns either; a FROM inside a call names no table.
    expect(renameIdentifier("upper(upper) || x::upper", "upper", "u").text).toBe("upper(u) || x::upper");
    expect(renameIdentifier("EXTRACT(YEAR FROM created_at) > 2000", "created_at", "made_at").text).toBe("EXTRACT(YEAR FROM made_at) > 2000");
    expect(renameIdentifier("a IS DISTINCT FROM b", "b", "c").text).toBe("a IS DISTINCT FROM c");
  });

  it("reads each dialect's strings and quotes, and names in any script", () => {
    expect(renameIdentifier("prénom <> '' AND \"prénom\" IS NOT NULL", "prénom", "prenom")).toMatchObject({
      text: "prenom <> '' AND \"prenom\" IS NOT NULL",
      count: 2,
    });
    expect(mentions("名前 = 1", "名前")).toBe(true);
    // MySQL: "…" is a string, and a backslash escapes a quote inside one.
    expect(renameIdentifier(`email <> "email" AND note <> 'it\\'s email'`, "email", "mail", { dialect: "mysql" })).toMatchObject({
      text: `mail <> "email" AND note <> 'it\\'s email'`,
      count: 1,
    });
    expect(renameIdentifier(`email <> "email"`, "email", "mail", { dialect: "postgresql" }).count).toBe(2);
    // PostgreSQL: E'…' escapes with a backslash, $$…$$ is a string.
    expect(renameIdentifier(`email <> E'it\\'s email' AND $$ email $$ <> email`, "email", "mail", { dialect: "postgresql" }).text).toBe(
      `mail <> E'it\\'s email' AND $$ email $$ <> mail`,
    );
    // SQL Server: ]] inside brackets is one ], kept escaped when written.
    expect(renameIdentifier("[a]]b] > 0", "a]b", "c]d", { dialect: "sqlserver" }).text).toBe("[c]]d] > 0");
    expect(mentions("arr[1] > 0", "1", "postgresql")).toBe(false);
    // A variable or a bind parameter of the same name is not the column.
    expect(renameIdentifier("email = @email OR email = :email", "email", "mail", { dialect: "sqlserver" }).text).toBe("mail = @email OR mail = :email");
    expect(mentionsColumn("SELECT 1 FROM email", "email")).toBe(false);
  });

  it("keeps a view's output name: a bare item becomes `new AS old`; `*` is uncertain", () => {
    expect(renameIdentifier("SELECT email FROM customers", "email", "mail", { keepOutput: true }).text).toBe("SELECT mail AS email FROM customers");
    expect(renameIdentifier("SELECT DISTINCT c.email, lower(email) AS e FROM customers c WHERE email <> ''", "email", "mail", { keepOutput: true }).text).toBe(
      "SELECT DISTINCT c.mail AS email, lower(mail) AS e FROM customers c WHERE mail <> ''",
    );
    expect(renameIdentifier("SELECT *, email FROM customers", "email", "mail", { keepOutput: true })).toMatchObject({ uncertain: true, why: "star" });
    expect(selectsAll("SELECT c.* FROM customers c")).toBe(true);
    expect(selectsAll("SELECT count(*) FROM customers")).toBe(false);
  });

  it("renames a view's output column where its select list names it, else says so", () => {
    expect(renameOutputColumn("SELECT id, email AS mail FROM t", "mail", "contact").text).toBe("SELECT id, email AS contact FROM t");
    expect(renameOutputColumn("SELECT id, email FROM t", "email", "contact").text).toBe("SELECT id, email AS contact FROM t");
    expect(renameOutputColumn("SELECT id, count(*) n FROM t GROUP BY id", "n", "total").text).toBe("SELECT id, count(*) total FROM t GROUP BY id");
    expect(renameOutputColumn("SELECT * FROM t", "email", "contact")).toMatchObject({ uncertain: true });
    // With a column list, the body does not name the view's columns: nothing to do.
    expect(renameViewOutput({ body: { "*": "SELECT a FROM t" }, columnList: true, columns: [{ name: "x" }] }, "x", "y")).toEqual({ body: null, check: false });
    expect(renameViewOutput({ body: { "*": "SELECT a AS x FROM t" } }, "x", "y")).toEqual({ body: { "*": "SELECT a AS y FROM t" }, check: false });
    expect(renameViewOutput({ body: { "*": "SELECT * FROM t" } }, "x", "y")).toEqual({ body: null, check: true });
  });
});

const orders = (): Json => ({
  kind: "table",
  id: "T_ORD",
  name: "sales_orders",
  database: "DB",
  columns: [
    { id: "C_ID", name: "id", type: "uuid" },
    { id: "C_NUM", name: "order_number", type: "string" },
    { id: "C_CUST", name: "customer_id", type: "uuid" },
  ],
  primaryKey: { columns: ["C_ID"] },
});
const binder: Json = {
  kind: "entity",
  id: "E_ORD",
  name: "SalesOrder",
  bindings: [{ id: "B", database: "DB", source: "T_ORD", fields: [{ attribute: "A_NUM", column: "order_number" }] }],
};
// The reference app's form: the source is the entity bound to the table, references are alias.column_name.
const openOrders = (): Json => ({
  kind: "query",
  id: "Q_OPEN",
  name: "OpenSalesOrders",
  database: "DB",
  from: { source: "E_ORD", alias: "o" },
  select: [
    { attribute: "A_NUM", expression: { column: "o.order_number" } },
    { name: "n", expression: { sql: { "*": "upper(o.order_number)" } } },
  ],
  where: { op: "eq", left: { column: "o.customer_id" }, right: { param: "c" } },
  orderBy: [{ expression: { column: "O.ORDER_NUMBER" } }],
  collections: [
    {
      attribute: "L",
      query: {
        from: { source: "T_LINES", alias: "l" },
        select: [{ name: "x", expression: { column: "l.order_number" } }],
        where: { op: "eq", left: { column: "l.order_id" }, right: { column: "o.id" } },
      },
    },
  ],
});

describe("a query's references", () => {
  const target = () => queryTable(orders(), [binder]);

  it("resolve each alias to its source: a rename rewrites the table's by name, nested queries included", () => {
    const q = openOrders();
    expect(renameInQuery(q, target(), "order_number", "number", "postgresql")).toEqual({ count: 2, check: false });
    expect((q.select as Json[])[0].expression).toEqual({ column: "o.number" });
    // The SQL expression of a query that reads only the table follows too; the collection's own alias is another source.
    expect((q.select as Json[])[1].expression).toEqual({ sql: { "*": "upper(o.number)" } });
    expect((q.orderBy as Json[])[0].expression).toEqual({ column: "O.ORDER_NUMBER" });
    const nested = (q.collections as Json[])[0].query as Json;
    expect((nested.select as Json[])[0].expression).toEqual({ column: "l.order_number" });
  });

  it("default the alias to the table's name, and leave a bare name another source may hold to check", () => {
    const q: Json = {
      from: { source: "T_ORD" },
      joins: [{ source: "T_OTHER", alias: "x", on: { op: "eq", left: { column: "sales_orders.customer_id" }, right: { column: "x.id" } } }],
      select: [{ name: "a", expression: { column: "customer_id" } }],
    };
    expect(renameInQuery(q, target(), "customer_id", "client_id", "postgresql")).toEqual({ count: 1, check: true });
    expect(((q.joins as Json[])[0].on as Json).left).toEqual({ column: "sales_orders.client_id" });
  });

  it("find a column a delete removes by name, by id or by a bound attribute", () => {
    expect(queryReadsColumn(openOrders(), target(), "C_CUST", "customer_id", "postgresql")).toEqual({ reads: 1, maybe: false, sql: false });
    expect(
      queryReadsColumn(
        { from: { source: "T_ORD", alias: "t" }, select: [{ expression: { column: "t.C_NUM" } }] },
        target(),
        "C_NUM",
        "order_number",
        "postgresql",
      ).reads,
    ).toBe(1);
    expect(
      queryReadsColumn(
        { from: { source: "T_ORD", alias: "t" }, select: [{ expression: { column: "t.A_NUM" } }] },
        target(),
        "C_NUM",
        "order_number",
        "postgresql",
      ).reads,
    ).toBe(1);
    expect(
      queryReadsColumn(
        { from: { source: "T_ORD", alias: "t" }, select: [{ expression: { sql: { "*": "lower(order_number)" } } }] },
        target(),
        "C_NUM",
        "order_number",
        "postgresql",
      ),
    ).toEqual({
      reads: 0,
      maybe: false,
      sql: true,
    });
    expect(
      queryReadsColumn(
        { from: { source: "T_OTHER", alias: "t" }, select: [{ expression: { column: "t.order_number" } }] },
        target(),
        "C_NUM",
        "order_number",
        "postgresql",
      ).reads,
    ).toBe(0);
  });
});

describe("a column's rename and delete beyond views", () => {
  const docs = (): Json[] => [
    binder,
    openOrders(),
    { kind: "routine", id: "R", name: "close_order", body: { postgresql: "UPDATE sales_orders SET status = 'X' WHERE order_number = p" } },
    { kind: "sql-object", id: "S", name: "trg_orders", body: { "*": "CREATE TRIGGER t BEFORE UPDATE ON sales_orders FOR EACH ROW EXECUTE FUNCTION f()" } },
  ];

  it("rewrites queries, lists routines and SQL objects to check, and counts the bindings that follow without naming them", () => {
    const plan = planColumnRename({ table: orders(), column: "C_NUM", to: "number", docs: docs(), tables: [], dialect: "postgresql" });
    expect(plan.rewritten).toEqual(["query OpenSalesOrders (2 places)"]);
    expect(plan.check).toEqual(["routine close_order (its text names order_number)"]);
    expect(plan.bindings).toEqual(["E_ORD"]);
    const notice = renameNotice("Renamed column order_number to number.", plan.rewritten, plan.bindings.length, plan.check);
    expect(notice).toBe(
      "Renamed column order_number to number. Also rewritten: query OpenSalesOrders (2 places). 1 storage binding follows. Check by hand (the name may still be used there): routine close_order (its text names order_number).",
    );
    expect(notice).not.toContain("SalesOrder ");
    const q = openOrders();
    plan.edits.get("Q_OPEN")!(q);
    expect((q.select as Json[])[0].expression).toEqual({ column: "o.number" });
  });

  it("blocks a delete a query's reference resolves to, and lists routines and SQL objects to check", () => {
    const plan = planPartDelete({ table: orders(), part: { kind: "column", id: "C_NUM" }, docs: docs(), dialect: "postgresql" });
    expect(plan.blockers).toEqual(["Query OpenSalesOrders reads sales_orders.order_number: change it first (it would no longer resolve)."]);
    expect(plan.warnings).toEqual([
      "Query OpenSalesOrders names order_number in an SQL expression: check it.",
      "Routine close_order names order_number in its text: check it.",
    ]);
    expect(plan.storage.map((o) => [o.id, o.name, o.lines])).toEqual([["E_ORD", "", ["a field no longer reads order_number"]]]);
    // A view that returns * from the table loses the column too.
    const star = planPartDelete({
      table: orders(),
      part: { kind: "column", id: "C_CUST" },
      docs: [{ kind: "view", id: "V", name: "all_orders", body: { "*": "SELECT * FROM sales_orders" } }],
      dialect: "postgresql",
    });
    expect(star.warnings).toEqual(["View all_orders returns * from sales_orders: its column customer_id goes; check what reads it."]);
  });

  it("lists what still names a renamed table", () => {
    const list = planTableRename({
      table: { ...orders(), name: "orders" },
      from: "sales_orders",
      docs: [
        ...docs(),
        { kind: "view", id: "V", name: "open", body: { "*": "SELECT * FROM sales_orders" } },
        { kind: "view", id: "V2", name: "other", body: { "*": "SELECT * FROM invoices" } },
        { kind: "query", id: "Q2", name: "Unaliased", from: { source: "T_ORD" }, select: [{ name: "a", expression: { column: "sales_orders.id" } }] },
      ],
    });
    expect(list).toEqual(["routine close_order", "SQL object trg_orders", "view open", "query Unaliased"]);
  });
});

describe("the small rules", () => {
  it("refuses a part edit when the part is gone or nothing changes", () => {
    const doc: Json = { uniques: [{ id: "U", name: "uq", columns: ["A"] }] };
    const entry = (doc.uniques as Json[])[0];
    expect(editPart(doc, undefined, () => undefined)).toBe(false);
    expect(editPart(doc, entry, (e) => void (e.columns = ["A"]))).toBe(false);
    expect(editPart(doc, entry, (e) => void (e.columns = ["A", "B"]))).toBeUndefined();
  });

  it("follows a foreign key draft to the files its tables were stored as while the dialog was open", () => {
    const view = (key: string, name: string, cols: [string, string][]) =>
      ({ key, name, schema: null, columns: cols.map(([k, n]) => ({ key: k, name: n })), primaryKey: null, foreignKeys: [] }) as unknown as TableView;
    const before = new Map([
      [
        "c@DB",
        view("c@DB", "customers", [
          ["A_ID", "id"],
          ["A_NOTE", "note_id"],
        ]),
      ],
      ["n@DB", view("n@DB", "notes", [["N_ID", "id"]])],
    ]);
    const tables = [
      view("F_C", "customers", [
        ["C1", "id"],
        ["C2", "note_id"],
      ]),
      view("n@DB", "notes", [["N_ID", "id"]]),
    ];
    const draft: FkDraft = {
      table: "c@DB",
      editing: null,
      name: "fk",
      referencedTable: "n@DB",
      pairs: [{ column: "A_NOTE", referenced: "N_ID" }],
      onDelete: "no-action",
      onUpdate: "no-action",
      deferrable: "not-deferrable",
    };
    const files = new Map([["c@DB", "F_C"]]);
    const next = followStoredDraft(
      draft,
      tables,
      (k) => files.get(k),
      (k) => before.get(k),
    );
    expect(next).toMatchObject({ table: "F_C", referencedTable: "n@DB", pairs: [{ column: "C2", referenced: "N_ID" }] });
    // Nothing stored: nothing moves.
    expect(
      followStoredDraft(
        next!,
        tables,
        (k) => files.get(k),
        (k) => before.get(k),
      ),
    ).toBeNull();
  });
});
