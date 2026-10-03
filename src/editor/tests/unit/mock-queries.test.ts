// The mock's queries (src/mocks/model/queries.ts and querySql.ts) mirror the engine's binding and SQL renderer
// (DatabaseRun.Queries.cs, Rendering/QuerySql.cs): the billing fixture's three queries render byte for byte as the engine's
// goldens (tests/fixtures/golden/csharp-dapper/billing/Queries/), against the mock's resolved view and the engine's recorded one;
// other dialects follow the engine's quoting, paging and spelling; every rule reports at the engine's pointer; and the model's
// validate, the database view and GET /api/model/queries/{id}/sql see them.
import { describe, expect, it } from "vitest";
import { setupServer } from "msw/node";
import { MockBackend } from "@/mocks/backend";
import { statefulHandlers } from "@/mocks/handlers";
import { queryClassName, resolveQueries } from "@/mocks/model/queries";
import { quoteIdentifier, sqlLiteral } from "@/mocks/model/querySql";
import { INVOICE_TOTAL } from "@/mocks/model/databaseObjectSeed";
import recorded from "@/mocks/recorded/database-view-main.json";
import type { DatabaseView } from "@/api/types";

type Json = Record<string, unknown>;

const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";
const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";
const INVOICES_TABLE = `${INVOICE}@${MAIN}`;
const INVOICE_NUMBER = "01J92P0V0R3VSP7D5238DTNZX1";
const CUSTOMER_EMAIL = "01J92P0V0NRX99014KNVAZGPEW";
const OUTSTANDING_INVOICES = "01J92P0V1YZ4YP352KD1A50FXS";
const BY_CUSTOMER = "01K6QRY0000000000000000001";
const REVENUE = "01K6QRY0000000000000000002";
const FIND_CUSTOMERS = "01K6QRY0000000000000000003";

// The engine's SQL for the billing queries (the csharp-dapper goldens' Sql and LinesSql constants).
const GOLDEN = {
  FindCustomersWithIssuedInvoices: [
    "SELECT c.id AS id, c.name AS name, c.email AS email, c.created_at AS createdAt",
    "FROM billing.customers c",
    "WHERE c.name ILIKE @namePattern AND EXISTS (SELECT 1",
    "    FROM billing.invoices i",
    "    WHERE i.customer_id = c.id AND i.status = 'I')",
    "ORDER BY LOWER(c.name) ASC",
  ].join("\n"),
  InvoicesByCustomer: [
    "SELECT i.id AS id, i.number AS number, i.issued_on AS issuedOn, i.status AS status, i.notes AS notes, i.created_at AS createdAt",
    "FROM billing.invoices i",
    "WHERE i.customer_id = @customerId AND i.status IN @statuses AND i.deleted_at IS NULL",
    "ORDER BY i.issued_on DESC, i.number ASC",
    "LIMIT @limit OFFSET @offset",
  ].join("\n"),
  lines: [
    "SELECT l.id AS id, l.quantity AS quantity, l.description AS description, l.invoice_id AS mq_key0",
    "FROM billing.invoice_lines l",
    "WHERE l.invoice_id IN @mq_keys0",
    "ORDER BY l.id ASC",
  ].join("\n"),
  RevenueByMonth: [
    "SELECT CAST(date_trunc('month', i.issued_on) AS date) AS month, COUNT(*) AS invoiceCount, COALESCE(SUM(i.total_amount), 0) AS revenue",
    "FROM billing.invoices i",
    "WHERE i.issued_on >= @from AND i.issued_on < @to AND i.status <> 'V'",
    "GROUP BY CAST(date_trunc('month', i.issued_on) AS date)",
    "ORDER BY CAST(date_trunc('month', i.issued_on) AS date) ASC",
  ].join("\n"),
};

const resolvedView = (backend: MockBackend) => backend.generation.databaseView(MAIN)!.view!;

function expectGolden(view: DatabaseView) {
  const queries = view.queries!;
  expect(queries.map((q) => q.name)).toEqual(["FindCustomersWithIssuedInvoices", "InvoicesByCustomer", "RevenueByMonth"]);
  for (const q of queries) expect(q.sql).toBe(GOLDEN[q.name as keyof typeof GOLDEN]);
  const byCustomer = queries.find((q) => q.name === "InvoicesByCustomer")!;
  expect(byCustomer.sqlParameters).toEqual(["customerId", "statuses", "offset", "limit"]);
  expect(byCustomer.collections).toHaveLength(1);
  expect(byCustomer.collections[0].sql).toBe(GOLDEN.lines);
  expect(byCustomer.collections[0].sqlParameters).toEqual(["mq_keys0"]);
}

/** Saves a query into the mock model; returns its id. */
function addQuery(backend: MockBackend, json: Json): string {
  const id = String(json.id);
  const result = backend.model.create({ kind: "query", database: MAIN, ...json });
  expect(result.diagnostics.filter((d) => d.severity === "error")).toEqual([]);
  expect(result.outcome).toBe("saved");
  return id;
}

describe("the billing queries", () => {
  it("render byte for byte as the engine's goldens from the mock's resolved view", () => {
    const backend = new MockBackend();
    const view = resolvedView(backend);
    expectGolden(view);
    const byCustomer = view.queries!.find((q) => q.id === BY_CUSTOMER)!;
    // The projection follows QueryViews.cs: the trees as written, the resolved sources, fields and keys.
    expect([byCustomer.entityId, byCustomer.from.source, byCustomer.from.tableKey, byCustomer.from.kind]).toEqual([
      INVOICE,
      "01J92P0V1T0J6RH4MY9H81NYB4",
      INVOICES_TABLE,
      "from",
    ]);
    expect(Object.keys(byCustomer.where as Json)).toEqual(["and"]);
    expect((byCustomer.orderBy[0] as Json).direction).toBe("desc");
    expect(byCustomer.paging).toEqual({ offset: "offset", limit: "limit" });
    expect(byCustomer.select[0]).toMatchObject({ name: "id", type: "uuid", nativeType: "uuid", nullable: false });
    expect(byCustomer.parameters.map((p) => [p.name, p.nativeType, p.collection, p.default])).toEqual([
      ["customerId", "uuid", false, null],
      ["statuses", "varchar(1)", true, null],
      ["offset", "integer", false, 0],
      ["limit", "integer", false, 50],
    ]);
    const lines = byCustomer.collections[0];
    expect([lines.name, lines.attribute, lines.entityId]).toEqual(["lines", "01J92P0V1H4D2M1HCK82ASJEWT", "01J92P0V0GWFR78HZH0P8Z3GY7"]);
    expect(lines.keys).toEqual([
      {
        outer: "i.01J92P0V0Q9EK961M5HAQ3C5MY",
        parentField: "id",
        hidden: false,
        childField: "mq_key0",
        parameter: "mq_keys0",
        type: "uuid",
        nativeType: "uuid",
      },
    ]);
    expect(byCustomer.uses).toEqual([INVOICES_TABLE, `01J92P0V0GWFR78HZH0P8Z3GY7@${MAIN}`]);
    const revenue = view.queries!.find((q) => q.id === REVENUE)!;
    expect(revenue.entityId).toBeNull();
    expect(revenue.select.map((f) => [f.name, f.type, f.nullable])).toEqual([
      ["month", "date", false],
      ["invoiceCount", "int64", false],
      ["revenue", "decimal", false],
    ]);
    expect(backend.model.validate().diagnostics.filter((d) => /^MQ40(2|3)/.test(d.rule))).toEqual([]);
  });

  it("render the same against the engine's recorded view", () => {
    const backend = new MockBackend();
    // The recording carries the engine's own queries: the mock resolves them again over the recorded tables and agrees.
    const { queries: engine, ...rest } = (recorded as unknown as { view: DatabaseView }).view;
    const view = rest as DatabaseView;
    const result = resolveQueries(view, backend.model.docs());
    expect(result.diagnostics).toEqual([]);
    expectGolden({ ...view, queries: result.queries });
    expect(result.queries.map((q) => q.sql)).toEqual(engine!.map((q) => q.sql));
  });

  it("follow each dialect's quoting, paging, spelling and literals", () => {
    const backend = new MockBackend();
    const queries = resolveQueries(resolvedView(backend), backend.model.docs());
    const sql = (id: string, dialect: string, options = {}) => queries.preview(id, dialect, options)!.preview.sql;
    expect(sql(BY_CUSTOMER, "sqlserver")).toBe(
      [
        "SELECT i.id AS id, i.number AS number, i.issued_on AS issuedOn, i.status AS status, i.notes AS notes, i.created_at AS createdAt",
        "FROM billing.invoices i",
        "WHERE i.customer_id = @customerId AND i.status IN @statuses AND i.deleted_at IS NULL",
        "ORDER BY i.issued_on DESC, i.number ASC",
        "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY",
      ].join("\n"),
    );
    expect(sql(BY_CUSTOMER, "oracle", { placeholder: ":" })).toBe(
      [
        'SELECT i.id AS id, i."number" AS "number", i.issued_on AS issuedOn, i.status AS status, i.notes AS notes, i.created_at AS createdAt',
        "FROM billing.invoices i",
        "WHERE i.customer_id = :customerId AND i.status IN :statuses AND i.deleted_at IS NULL",
        'ORDER BY i.issued_on DESC, i."number" ASC',
        "OFFSET :offset ROWS",
        "FETCH NEXT :limit ROWS ONLY",
      ].join("\n"),
    );
    // SQLite has no schemas; MySQL pages as PostgreSQL does.
    expect(sql(BY_CUSTOMER, "sqlite")).toContain("FROM invoices i\n");
    expect(sql(BY_CUSTOMER, "mysql").endsWith("\nLIMIT @limit OFFSET @offset")).toBe(true);
    expect(sql(FIND_CUSTOMERS, "sqlserver")).toContain(
      "WHERE LOWER(c.name) LIKE LOWER(@namePattern) AND EXISTS (SELECT 1\n    FROM billing.invoices i\n    WHERE",
    );
    expect(sql(FIND_CUSTOMERS, "sqlserver")).toContain("i.status = N'I'");
    // Positional placeholders are numbered by first appearance; a list may be an array on PostgreSQL.
    const positional = queries.preview(BY_CUSTOMER, null, { placeholder: "$", lists: "any" })!.preview;
    expect(positional.parameters).toEqual(["customerId", "statuses", "offset", "limit"]);
    expect(positional.sql).toContain("i.customer_id = $1 AND i.status = ANY($2)");
    expect(positional.sql.endsWith("LIMIT $4 OFFSET $3")).toBe(true);
    expect(positional.collections[0].sql).toContain("WHERE l.invoice_id = ANY($1)");
    // RevenueByMonth has no text for mysql: NULL and MQ4029 with the renderer's message.
    const mysql = queries.preview(REVENUE, "mysql")!;
    expect(mysql.preview.sql.startsWith("SELECT NULL AS month")).toBe(true);
    expect(mysql.diagnostics.map((d) => [d.rule, d.message, d.elementId])).toEqual([
      ["MQ4029", `Query 'RevenueByMonth' has an sql expression without a text for mysql (and no "*" text), so it cannot be rendered there.`, REVENUE],
    ]);
    expect(() => queries.preview(BY_CUSTOMER, "cobol")).toThrow("'cobol' is not a dialect");
    expect(quoteIdentifier("order", "sqlserver", "reserved")).toBe("[order]");
    expect(quoteIdentifier("order", "mysql", "reserved")).toBe("`order`");
    expect(quoteIdentifier("Name", "postgresql", "always")).toBe('"Name"');
    expect(quoteIdentifier("my col", "postgresql", "never")).toBe("my col");
    expect(quoteIdentifier("_key", "oracle", "reserved")).toBe('"_key"');
    expect([
      sqlLiteral("it's", "mysql"),
      sqlLiteral("a\\b", "mysql"),
      sqlLiteral(true, "sqlserver"),
      sqlLiteral(false, "postgresql"),
      sqlLiteral(1.5, "oracle"),
    ]).toEqual(["'it''s'", "'a\\\\b'", "1", "false", "1.5"]);
  });

  it("render joins, functions, operations, case, cast, nulls, routines and hidden collection keys", () => {
    const backend = new MockBackend();
    const id = addQuery(backend, {
      id: "01K6QRY0000000000000000010",
      name: "Report",
      parameters: [{ name: "take", type: "int32" }],
      from: { source: CUSTOMER, alias: "c" },
      joins: [
        {
          source: INVOICES_TABLE,
          alias: "order",
          kind: "left",
          on: { op: "eq", left: { column: "order.customer_id" }, right: { column: "c.id" } },
        },
      ],
      select: [
        { name: "label", expression: { op: "concat", args: [{ column: "c.name" }, { value: " <" }, { column: "c.email" }, { value: ">" }] } },
        { name: "nameLength", expression: { call: "length", args: [{ column: "c.name" }] } },
        { name: "lastNumber", expression: { call: "max", args: [{ column: "order.number" }] } },
        { name: "due", expression: { call: INVOICE_TOTAL, args: [{ column: "order.id" }] } },
        { name: "flag", expression: { case: [{ when: { op: "isNull", left: { column: "order.notes" } }, then: { value: "none" } }], else: { value: "some" } } },
        { name: "asText", expression: { cast: { column: "c.id" }, type: "string" } },
        { name: "nothing", expression: { null: true } },
      ],
      groupBy: [{ column: "c.name" }, { column: "c.email" }, { column: "c.id" }, { column: "order.id" }, { column: "order.notes" }],
      orderBy: [{ expression: { column: "c.name" }, direction: "desc", nulls: "last" }],
      paging: { offset: 10, limit: "take" },
      collections: [
        {
          attribute: "invoiceNumbers",
          query: {
            from: { source: INVOICES_TABLE, alias: "i" },
            select: [{ name: "number", expression: { column: "i.number" } }],
            where: { op: "eq", left: { column: "i.customer_id" }, right: { column: "c.id" } },
          },
        },
      ],
    });
    expect(backend.model.validate().diagnostics.filter((d) => d.elementId === id)).toEqual([]);
    const queries = resolveQueries(resolvedView(backend), backend.model.docs());
    const query = queries.queries.find((q) => q.id === id)!;
    expect(query.sql).toBe(
      [
        "SELECT (c.name || ' <' || c.email || '>') AS label, LENGTH(c.name) AS nameLength, MAX(\"order\".number) AS lastNumber, billing.invoice_total(\"order\".id) AS due, CASE WHEN \"order\".notes IS NULL THEN 'none' ELSE 'some' END AS flag, CAST(c.id AS varchar(255)) AS asText, NULL AS nothing, c.id AS mq_key0_0",
        "FROM billing.customers c",
        'LEFT JOIN billing.invoices "order" ON "order".customer_id = c.id',
        'GROUP BY c.name, c.email, c.id, "order".id, "order".notes',
        "ORDER BY c.name DESC NULLS LAST",
        "LIMIT @take OFFSET 10",
      ].join("\n"),
    );
    expect(query.select.find((f) => f.name === "lastNumber")).toMatchObject({ type: "string", nullable: true });
    expect(query.select.find((f) => f.name === "due")).toMatchObject({ type: "decimal" });
    expect(query.uses).toContain(INVOICE_TOTAL);
    expect(query.collections[0].keys[0]).toMatchObject({ parentField: "mq_key0_0", hidden: true, childField: "mq_key0" });
    expect(query.collections[0].sql).toBe(
      ["SELECT i.number AS number, i.customer_id AS mq_key0", "FROM billing.invoices i", "WHERE i.customer_id IN @mq_keys0"].join("\n"),
    );
    const sqlserver = queries.preview(id, "sqlserver")!.preview.sql;
    expect(sqlserver).toContain("CONCAT(c.name, N' <', c.email, N'>') AS label, LEN(c.name) AS nameLength, MAX([order].number)");
    expect(sqlserver).toContain("CAST(c.id AS nvarchar(255))");
    expect(sqlserver).toContain("ORDER BY CASE WHEN c.name IS NULL THEN 1 ELSE 0 END, c.name DESC\nOFFSET 10 ROWS FETCH NEXT @take ROWS ONLY");
  });
});

describe("the query rules", () => {
  it("each reports its finding at the offending node, as the engine does", () => {
    const backend = new MockBackend();
    const other = "01K6QDB0000000000000000001";
    expect(backend.model.create({ kind: "database", id: other, name: "other", dialect: "postgresql" }).outcome).toBe("saved");
    const id = addQuery(backend, {
      id: "01K6QRY0000000000000000011",
      name: "Broken",
      entity: CUSTOMER,
      parameters: [
        { name: "word", type: "string" },
        { name: "word", type: "int32" },
        { name: "odd", type: CUSTOMER },
      ],
      from: { source: CUSTOMER, alias: "c" },
      joins: [
        { source: `${INVOICE}@${other}`, alias: "x" },
        { source: OUTSTANDING_INVOICES, alias: "c" },
        { source: "nowhere", alias: "n" },
      ],
      select: [
        { attribute: INVOICE_NUMBER, expression: { column: "c.nope" } },
        { name: "b", expression: { column: "z.id" } },
        { name: "c", expression: { param: "missing" } },
        { name: "d", expression: { sql: { sqlserver: "GETDATE()" } } },
        { name: "B", expression: { column: "c.name" } },
      ],
      where: { op: "between", left: { column: "c.name" }, right: { value: 1 } },
      paging: { offset: "gone", limit: "word" },
      collections: [
        {
          attribute: CUSTOMER_EMAIL,
          query: {
            from: { source: INVOICES_TABLE, alias: "i" },
            select: [{ name: "n", expression: { column: "i.number" } }],
            where: {
              or: [
                { op: "eq", left: { column: "i.customer_id" }, right: { column: "c.id" } },
                { op: "eq", left: { column: "q.id" }, right: { value: 1 } },
              ],
            },
          },
        },
      ],
    });
    const findings = backend.model
      .validate()
      .diagnostics.filter((d) => d.elementId === id)
      .map((d) => [d.rule, d.jsonPointer]);
    for (const expected of [
      ["MQ3001", "/parameters/1/name"],
      ["MQ4018", "/parameters/2/type"],
      ["MQ4021", "/joins/0/source"],
      ["MQ4022", "/joins/1/alias"],
      ["MQ4021", "/joins/2/source"],
      ["MQ4025", "/select/0/attribute"],
      ["MQ4023", "/select/0/expression/column"],
      ["MQ4022", "/select/1/expression/column"],
      ["MQ4024", "/select/2/expression/param"],
      ["MQ4029", "/select/3/expression/sql"],
      ["MQ3001", "/select/4/name"],
      ["MQ4026", "/select"],
      ["MQ4031", "/where/right"],
      ["MQ4024", "/paging/offset"],
      ["MQ4030", "/paging/limit"],
      ["MQ4027", "/collections/0/attribute"],
      ["MQ4028", "/collections/0/query/where/or/1/left/column"],
      ["MQ4028", "/collections/0/query/where/or/0/right/column"],
      ["MQ4028", "/collections/0/query"],
    ])
      expect(findings).toContainEqual(expected);
    const diagnostics = backend.model.validate().diagnostics.filter((d) => d.elementId === id);
    const message = (rule: string, pointer: string) => diagnostics.find((d) => d.rule === rule && d.jsonPointer === pointer)?.message;
    expect(message("MQ4021", "/joins/0/source")).toBe(
      `Query 'Broken' reads '${INVOICE}@${other}', which is entity 'Invoice', which has no table in database 'main'.`,
    );
    expect(message("MQ4021", "/joins/2/source")).toBe("Query 'Broken' reads 'nowhere', which is no table or view of database 'main'.");
    expect(message("MQ4023", "/select/0/expression/column")).toBe("Query 'Broken' names column 'nope' of 'c' (table 'customers'), which has no such column.");
    expect(message("MQ4030", "/paging/limit")).toBe(
      "Query 'Broken' pages by parameter 'word', whose type 'string' is not an integer type (int16, int32 or int64).",
    );
    expect(message("MQ3001", "/select/4/name")).toBe("Field name 'B' is already used at index 1 of the select list of query 'Broken'.");
    expect(message("MQ4027", "/collections/0/attribute")).toBe(
      "Collection 0 of query 'Broken' names attribute 'email' of entity 'Customer', which is not a collection.",
    );
    expect(diagnostics.find((d) => d.rule === "MQ4026")?.severity).toBe("warning");
    expect(diagnostics.find((d) => d.rule === "MQ4023")?.filePath).toBe(backend.model.get(id)!.path);

    // A model with a query error resolves no view, and the query has no SQL.
    expect(backend.generation.databaseView(MAIN)!.view).toBeNull();
    expect(backend.generation.querySql(BY_CUSTOMER, null, null).preview).toBeNull();
  });

  it("reports a removed column, and the view comes back once it is fixed", () => {
    const backend = new MockBackend();
    const entry = backend.model.get(BY_CUSTOMER)!;
    const json = structuredClone(entry.json) as Json;
    ((json.select as Json[])[1].expression as Json).column = "i.numbr_gone";
    expect(backend.model.save(BY_CUSTOMER, json, entry.hash).outcome).toBe("saved");
    const found = backend.model.validate().diagnostics.filter((d) => d.elementId === BY_CUSTOMER);
    expect(found.map((d) => [d.rule, d.severity, d.jsonPointer, d.message])).toEqual([
      [
        "MQ4023",
        "error",
        "/select/1/expression/column",
        "Query 'InvoicesByCustomer' names column 'numbr_gone' of 'i' (table 'invoices'), which has no such column.",
      ],
    ]);
    expect(backend.generation.databaseView(MAIN)!.view).toBeNull();
    // A name matches ignoring case and underscores when only one column does.
    ((json.select as Json[])[1].expression as Json).column = "i.NUMBER";
    expect(backend.model.save(BY_CUSTOMER, json, backend.model.get(BY_CUSTOMER)!.hash).outcome).toBe("saved");
    expect(backend.model.validate().diagnostics.filter((d) => d.elementId === BY_CUSTOMER)).toEqual([]);
    expect(resolvedView(backend).queries!.find((q) => q.id === BY_CUSTOMER)!.sql).toBe(GOLDEN.InvoicesByCustomer);
  });
});

describe("GET /api/model/queries/{id}/sql", () => {
  it("answers the statements per dialect, the problems, and the errors of a bad request", async () => {
    const base = "http://queries.test";
    const backend = new MockBackend();
    const server = setupServer(...statefulHandlers(backend, base));
    server.listen({ onUnhandledRequest: "error" });
    try {
      const get = async (path: string) => {
        const response = await fetch(`${base}${path}`);
        return { status: response.status, body: (await response.json()) as Json };
      };
      const main = await get(`/api/model/queries/${BY_CUSTOMER}/sql`);
      expect(main.status).toBe(200);
      const preview = main.body.preview as Json;
      expect([preview.id, preview.name, preview.database, preview.dialect, preview.sql]).toEqual([
        BY_CUSTOMER,
        "InvoicesByCustomer",
        MAIN,
        "postgresql",
        GOLDEN.InvoicesByCustomer,
      ]);
      expect((preview.collections as Json[]).map((c) => [c.name, c.sql, c.parameters])).toEqual([["lines", GOLDEN.lines, ["mq_keys0"]]]);
      const sqlserver = await get(`/api/model/queries/${BY_CUSTOMER}/sql?dialect=mssql&placeholder=:`);
      expect((sqlserver.body.preview as Json).dialect).toBe("sqlserver");
      expect((sqlserver.body.preview as Json).sql).toContain("OFFSET :offset ROWS FETCH NEXT :limit ROWS ONLY");
      const mysql = await get(`/api/model/queries/${REVENUE}/sql?dialect=mysql`);
      expect((mysql.body.diagnostics as Json[]).map((d) => d.rule)).toEqual(["MQ4029"]);

      expect((await get(`/api/model/queries/${BY_CUSTOMER}/sql?dialect=cobol`)).status).toBe(400);
      expect((await get(`/api/model/queries/${BY_CUSTOMER}/sql?placeholder=?`)).status).toBe(400);
      expect((await get(`/api/model/queries/${BY_CUSTOMER}/sql?lists=all`)).status).toBe(400);
      const notFound = await get(`/api/model/queries/01K6QRY0000000000000000099/sql`);
      expect([notFound.status, notFound.body.code]).toEqual([404, "not-found"]);
      const notQuery = await get(`/api/model/queries/${CUSTOMER}/sql`);
      expect([notQuery.status, notQuery.body.code]).toEqual([404, "not-a-query"]);

      // While the pristine fixture replays the engine's recorded view, its queries are bound against it.
      const view = await get(`/api/databases/${MAIN}/view`);
      expect(((view.body.view as DatabaseView).queries ?? []).map((q) => q.sql)).toEqual([
        GOLDEN.FindCustomersWithIssuedInvoices,
        GOLDEN.InvoicesByCustomer,
        GOLDEN.RevenueByMonth,
      ]);
    } finally {
      server.close();
    }
  });
});

describe("the rules the review added (MQ4032 to MQ4043), as the engine reports them", () => {
  const CUSTOMERS = `${CUSTOMER}@${MAIN}`;
  const CUSTOMER_ID = "01J92P0V0KGPC29TQQG8R57EBM";
  const CUSTOMER_NAME = "01J92P0V0MS09YFZHX07JQ3KMN";
  const CUSTOMER_END = "01J92P0V1EHF7PB28CZJG9C5SN";
  const INVOICE_ID = "01J92P0V0Q9EK961M5HAQ3C5MY";
  const TOTAL = "01J92P0V0T6EA0XM025XQX8GBW";
  const AMOUNT = "01J92P0V08P9BVJAPQ793XYNTQ";
  const CURRENCY = "01J92P0V093A7BE6Q8AS477H7D";
  const QUERY = "01K6QRY0000000000000000077";

  /** Binds one query (and any extra documents) against the mock's resolved view, with the view's dialect overridden. */
  function bind(json: Json, options: { dialect?: string; extra?: Json[] } = {}) {
    const backend = new MockBackend();
    const view = { ...resolvedView(backend), ...(options.dialect ? { dialect: options.dialect } : {}) } as DatabaseView;
    const docs = new Map(backend.model.docs());
    for (const d of options.extra ?? []) docs.set(String(d.id), d);
    docs.set(QUERY, { kind: "query", id: QUERY, database: MAIN, ...json });
    const result = resolveQueries(view, docs);
    const findings = result.diagnostics
      .filter((d) => d.elementId === QUERY)
      .map((d) => [d.rule, d.jsonPointer] as [string, string])
      .sort((a, b) => (a[0] + a[1] < b[0] + b[1] ? -1 : 1));
    return { result, findings, query: result.resolved.get(QUERY)!, diagnostics: result.diagnostics.filter((d) => d.elementId === QUERY) };
  }

  const keyed = (attribute: string, select: Json[], extra: Json = {}) => ({
    attribute,
    query: {
      from: { source: INVOICES_TABLE, alias: "i" },
      select,
      where: { op: "eq", left: { column: "i.customer_id" }, right: { column: "c.id" } },
      ...extra,
    },
  });

  it("groups a grouped collection by its keys and a grouped parent by its hidden keys, in every dialect", () => {
    const { findings, query, result } = bind({
      name: "CustomerTotals",
      from: { source: CUSTOMERS, alias: "c" },
      select: [
        { name: "name", expression: { column: "c.name" } },
        { name: "customers", expression: { call: "count" } },
      ],
      groupBy: [{ column: "c.name" }],
      collections: [
        keyed(
          "byNumber",
          [
            { name: "number", expression: { column: "i.number" } },
            { name: "total", expression: { call: "sum", args: [{ column: "i.total_amount" }] } },
          ],
          {
            groupBy: [{ column: "i.number" }],
          },
        ),
        keyed("invoiceCount", [{ name: "count", expression: { call: "COUNT" } }]),
      ],
    });
    expect(findings).toEqual([]);
    expect(query.sql).toBe(
      ["SELECT c.name AS name, COUNT(*) AS customers, c.id AS mq_key0_0, c.id AS mq_key1_0", "FROM billing.customers c", "GROUP BY c.name, c.id"].join("\n"),
    );
    const preview = result.preview(QUERY)!.preview;
    expect(preview.collections[0].sql).toBe(
      [
        "SELECT i.number AS number, SUM(i.total_amount) AS total, i.customer_id AS mq_key0",
        "FROM billing.invoices i",
        "WHERE i.customer_id IN @mq_keys0",
        "GROUP BY i.number, i.customer_id",
      ].join("\n"),
    );
    expect(preview.collections[1].sql.endsWith("\nGROUP BY i.customer_id")).toBe(true);
    for (const dialect of ["sqlserver", "mysql", "sqlite", "oracle"]) {
      const sql = result.preview(QUERY, dialect)!.preview;
      expect(sql.sql.replace(/["`[\]]/g, "").endsWith("GROUP BY c.name, c.id")).toBe(true);
      expect(sql.collections[0].sql.replace(/["`[\]]/g, "").endsWith("GROUP BY i.number, i.customer_id")).toBe(true);
    }
  });

  it("reports a distinct parent's hidden key (MQ4032) and a distinct order outside the select list (MQ4038)", () => {
    const hidden = bind({
      name: "Hidden",
      distinct: true,
      from: { source: CUSTOMERS, alias: "c" },
      select: [{ name: "name", expression: { column: "c.name" } }],
      collections: [keyed("numbers", [{ name: "number", expression: { column: "i.number" } }])],
    });
    expect(hidden.findings).toEqual([["MQ4032", "/collections/0/query/where"]]);
    expect(hidden.query.sql).toBe("");
    const order = bind({
      name: "Distinct",
      distinct: true,
      from: { source: CUSTOMERS, alias: "c" },
      select: [{ name: "name", expression: { column: "c.name" } }],
      orderBy: [{ expression: { column: "c.email" } }],
    });
    expect(order.findings).toEqual([["MQ4038", "/orderBy/0/expression"]]);
    const nulls = bind(
      {
        name: "Nulls",
        distinct: true,
        from: { source: CUSTOMERS, alias: "c" },
        select: [{ name: "name", expression: { column: "c.name" } }],
        orderBy: [{ expression: { column: "c.name" }, nulls: "last" }],
      },
      { dialect: "sqlserver" },
    );
    expect(nulls.findings).toEqual([["MQ4038", "/orderBy/0/nulls"]]);
  });

  it("casts on MySQL to the types its CAST accepts", () => {
    const { result, findings } = bind({
      name: "Casts",
      from: { source: INVOICES_TABLE, alias: "i" },
      select: [
        { name: "a", expression: { cast: { column: "i.number" }, type: "string" } },
        { name: "b", expression: { cast: { column: "i.number" }, type: "int64" } },
        { name: "c", expression: { cast: { column: "i.number" }, type: "decimal" } },
        { name: "d", expression: { cast: { column: "i.number" }, type: "datetimeOffset" } },
      ],
    });
    expect(findings).toEqual([]);
    expect(result.preview(QUERY, "mysql")!.preview.sql).toMatch(
      /^SELECT CAST\(i\.number AS CHAR\) AS a, CAST\(i\.number AS SIGNED\) AS b, CAST\(i\.number AS DECIMAL\(\d+,\d+\)\) AS c, CAST\(i\.number AS DATETIME\) AS d\n/,
    );
    const mysql = bind(
      { name: "Cast", from: { source: INVOICES_TABLE, alias: "i" }, select: [{ name: "a", expression: { cast: { column: "i.number" }, type: "int32" } }] },
      { dialect: "mysql" },
    );
    expect(mysql.query.select[0].nativeType).toBe("SIGNED");
  });

  it("types a field from its attribute (MQ4033) and fills a foreign key by its end and a value object by its members", () => {
    const typed = bind({
      name: "Typed",
      entity: CUSTOMER,
      from: { source: CUSTOMERS, alias: "c" },
      select: [
        { attribute: CUSTOMER_ID, expression: { column: "c.id" } },
        { attribute: CUSTOMER_NAME, expression: { call: "initcap", args: [{ column: "c.name" }] } },
        { attribute: CUSTOMER_EMAIL, expression: { call: "count" } },
      ],
    });
    expect(typed.findings.filter(([rule]) => rule !== "MQ4026")).toEqual([["MQ4033", "/select/2/expression"]]);
    expect(typed.diagnostics.find((d) => d.rule === "MQ4033")!.severity).toBe("warning");
    expect([typed.query.select[1].type, typed.query.select[1].codeType]).toEqual(["string", "string"]);
    expect(typed.query.sql).not.toBe("");

    const filled = bind({
      name: "Filled",
      entity: INVOICE,
      from: { source: INVOICES_TABLE, alias: "i" },
      select: [
        { attribute: INVOICE_ID, expression: { column: "i.id" } },
        { attribute: INVOICE_NUMBER, expression: { column: "i.number" } },
        { attribute: CUSTOMER_END, expression: { column: `i.${CUSTOMER_END}.${CUSTOMER_ID}` } },
        { attribute: `${TOTAL}.${AMOUNT}`, expression: { column: `i.${TOTAL}.${AMOUNT}` } },
        { attribute: `${TOTAL}.${CURRENCY}`, expression: { column: "i.total_currency" } },
        { attribute: `${INVOICE_NUMBER}.${AMOUNT}`, expression: { column: "i.number" } },
      ],
    });
    expect(filled.findings.filter(([rule]) => rule !== "MQ4026")).toEqual([["MQ4025", "/select/5/attribute"]]);
    expect(filled.query.select.map((f) => f.name)).toEqual(["id", "number", "customerId", "totalAmount", "totalCurrency", `${INVOICE_NUMBER}.${AMOUNT}`]);
    const view = filled.result.queries.find((q) => q.id === QUERY)!;
    expect(view.select.map((f) => f.attributeId)).toEqual([INVOICE_ID, INVOICE_NUMBER, CUSTOMER_END, `${TOTAL}.${AMOUNT}`, `${TOTAL}.${CURRENCY}`, null]);
  });

  it("refuses unsafe function names (MQ4034), joins that do not fit (MQ4035 to MQ4037) and misused list parameters (MQ4039)", () => {
    const calls = bind({
      name: "Calls",
      from: { source: CUSTOMERS, alias: "c" },
      select: [
        { name: "a", expression: { call: "lower(c.name)); DROP TABLE customers; --", args: [{ column: "c.name" }] } },
        { name: "b", expression: { call: "pg_catalog.lower", args: [{ column: "c.name" }] } },
      ],
    });
    expect(calls.findings).toEqual([["MQ4034", "/select/0/expression/call"]]);
    const on = { op: "eq", left: { column: "f.customer_id" }, right: { column: "c.id" } };
    const joins = (dialect: string) =>
      bind(
        {
          name: "Joins",
          from: { source: CUSTOMERS, alias: "c" },
          joins: [
            { source: INVOICES_TABLE, alias: "a" },
            { source: INVOICES_TABLE, alias: "b", kind: "cross", on },
            { source: INVOICES_TABLE, alias: "f", kind: "full", on },
          ],
          select: [{ name: "name", expression: { column: "c.name" } }],
        },
        { dialect },
      ).findings;
    expect(joins("postgresql")).toEqual([
      ["MQ4035", "/joins/0"],
      ["MQ4035", "/joins/1/on"],
    ]);
    expect(joins("mysql")).toContainEqual(["MQ4036", "/joins/2/kind"]);
    expect(joins("sqlite")).toContainEqual(["MQ4037", "/joins/2/kind"]);
    const lists = bind({
      name: "Lists",
      parameters: [
        { name: "ids", type: "uuid", collection: true },
        { name: "one", type: "uuid" },
      ],
      from: { source: CUSTOMERS, alias: "c" },
      select: [{ name: "name", expression: { column: "c.name" } }],
      where: {
        and: [
          { op: "in", left: { column: "c.id" }, right: { param: "ids" } },
          { op: "eq", left: { column: "c.id" }, right: { param: "ids" } },
          { op: "notIn", left: { column: "c.id" }, right: [{ param: "one" }, { param: "ids" }] },
        ],
      },
    });
    expect(lists.findings).toEqual([
      ["MQ4039", "/where/and/1/right/param"],
      ["MQ4039", "/where/and/2/right/1/param"],
    ]);
  });

  it("reports names that collide in generated code (MQ4040), abstract entities (MQ4041), operations (MQ4042) and literals (MQ4043)", () => {
    const names = bind({
      name: "Names",
      parameters: [
        { name: "word", type: "string" },
        { name: "Word", type: "string" },
        { name: "mq_keys0", type: "string" },
      ],
      from: { source: CUSTOMERS, alias: "c" },
      select: [
        { name: "issued_on", expression: { column: "c.name" } },
        { name: "issuedOn", expression: { column: "c.email" } },
      ],
      collections: [
        keyed("item", [{ name: "n", expression: { column: "i.number" } }]),
        keyed("lines", [{ name: "n", expression: { column: "i.number" } }]),
        keyed("Lines", [{ name: "n", expression: { column: "i.number" } }]),
      ],
    });
    expect(names.findings).toEqual([
      ["MQ3001", "/parameters/1/name"],
      ["MQ4040", "/collections/0/attribute"],
      ["MQ4040", "/collections/2/attribute"],
      ["MQ4040", "/parameters/2/name"],
      ["MQ4040", "/select/1/name"],
    ]);
    const party = {
      kind: "entity",
      id: "01K6QRY0000000000000000078",
      name: "Party",
      abstract: true,
      attributes: [{ id: "01K6QRY0000000000000000079", name: "id", type: "uuid" }],
    };
    const abstract = bind(
      {
        name: "Parties",
        entity: party.id,
        from: { source: CUSTOMERS, alias: "c" },
        select: [{ attribute: "01K6QRY0000000000000000079", expression: { column: "c.id" } }],
      },
      { extra: [party] },
    );
    expect(abstract.findings).toEqual([["MQ4041", "/entity"]]);
    const values = bind({
      name: "Values",
      parameters: [
        { name: "count", type: "int32", default: "ten" },
        { name: "rate", type: "decimal", default: 0.5 },
      ],
      from: { source: INVOICES_TABLE, alias: "i" },
      select: [
        { name: "a", expression: { op: "*", args: [{ value: 2 }] } },
        { name: "b", expression: { op: "-", args: [{ column: "i.total_amount" }] } },
        { name: "c", expression: { value: "2.x", type: "decimal" } },
        { name: "d", expression: { value: "2.0", type: "int32" } },
      ],
    });
    expect(values.findings).toEqual([
      ["MQ4042", "/select/0/expression/args"],
      ["MQ4043", "/parameters/0/default"],
      ["MQ4043", "/select/2/expression/value"],
      ["MQ4043", "/select/3/expression/type"],
    ]);
    expect(values.query.parameters[1].default).toBe(0.5);
  });

  it("writes a typed decimal literal as typed, a blank sql text as none, and an exists indented", () => {
    const literals = bind({
      name: "Literals",
      from: { source: INVOICES_TABLE, alias: "i" },
      select: [
        { name: "a", expression: { op: "*", args: [{ column: "i.total_amount" }, { value: "2.0", type: "decimal" }] } },
        { name: "b", expression: { value: "-007.50", type: "decimal" } },
        { name: "c", expression: { sql: { postgresql: "  ", "*": "1" } } },
      ],
      where: {
        exists: {
          from: { source: CUSTOMERS, alias: "c" },
          where: {
            and: [
              { op: "eq", left: { column: "c.id" }, right: { column: `i.${CUSTOMER_END}.${CUSTOMER_ID}` } },
              { op: "ne", left: { column: "c.name" }, right: { value: "two\nlines" } },
            ],
          },
        },
      },
    });
    expect(literals.findings).toEqual([]);
    expect(literals.query.sql).toBe(
      [
        "SELECT (i.total_amount * 2.0) AS a, -7.50 AS b, 1 AS c",
        "FROM billing.invoices i",
        "WHERE EXISTS (SELECT 1",
        "    FROM billing.customers c",
        "    WHERE c.id = i.customer_id AND c.name <> 'two\nlines')",
      ].join("\n"),
    );
    const blank = bind({ name: "Blank", from: { source: INVOICES_TABLE, alias: "i" }, select: [{ name: "a", expression: { sql: { postgresql: " " } } }] });
    expect(blank.findings).toEqual([["MQ4029", "/select/0/expression/sql"]]);
  });

  it("names a query's class as the engine does and reports two that collide", () => {
    expect(queryClassName("2024 report")).toBe("Q2024Report");
    expect(queryClassName("HTTPServer orders")).toBe("HttpServerOrders");
    const backend = new MockBackend();
    const docs = new Map(backend.model.docs());
    for (const [id, name] of [
      ["01K6QRY0000000000000000081", "open invoices"],
      ["01K6QRY0000000000000000082", "OpenInvoices"],
    ])
      docs.set(id, { kind: "query", id, name, database: MAIN, from: { source: INVOICES_TABLE }, select: [{ name: "n", expression: { value: 1 } }] });
    const found = resolveQueries(resolvedView(backend), docs).diagnostics.filter((d) => d.rule === "MQ4040");
    expect(found.map((d) => [d.elementId, d.jsonPointer])).toEqual([["01K6QRY0000000000000000081", "/name"]]);
  });
});
