// The query editor's model (model/queryTree.ts): forms, constructors, labels and checks of the trees, the edits across a query
// (renames, the result entity), and what needs the database view (sources, columns, a join from a foreign key, the select list
// filled from same-named columns), over the billing fixture's resolved database. Also New query…'s checks and document
// (explorer/databaseCreate.ts), the parameters grid's defaults, and the Database screen's and the preview's query targets.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import {
  and,
  call,
  cast,
  column,
  columnLabel,
  compare,
  defaultAlias,
  describe as describeExpression,
  describePredicate,
  expressionKind,
  expressionProblem,
  fieldName,
  fillFromColumns,
  findSource,
  foreignKeyJoins,
  literal,
  literalText,
  nameProblem,
  not,
  nullValue,
  operation,
  param,
  literalExpression,
  parseLiteral,
  valueText,
  predicateKind,
  predicateProblem,
  renameAlias,
  renameParameter,
  resolveColumn,
  rightShape,
  scopeOf,
  setResultEntity,
  sourceOptions,
  sqlText,
  tidy,
  withAttributeField,
  withComparison,
  withExpressionKind,
  type QueryDoc,
} from "@/model/queryTree";
import {
  buildDatabaseObject,
  databaseObjectProblems,
  queryAlias,
  querySelect,
  SCHEMA_ELEMENT_KINDS,
  type DatabaseObjectInput,
} from "@/explorer/databaseCreate";
import { entityShapeOf, parameterDefault, tableFiles } from "@/editors/database/QueryParts";
import { indexLookup } from "@/model/index";
import { ddlPreviewCaption, ddlPreviewTarget } from "@/workspaces/database/ddlPreview";
import { LIST_KINDS, OBJECT_LIST_MEMBERS } from "@/workspaces/database/tableList";
import { unitScope } from "@/workspaces/generate/previewScope";
import { scopeHelp } from "@/workspaces/generate/unitsModel";
import { hasEditor } from "@/editors/tabs";
import { MockBackend } from "@/mocks/backend";

const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";
const INVOICE_LINE = "01J92P0V0GWFR78HZH0P8Z3GY7";
const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const INVOICES_OVERLAY = "01J92P0V1T0J6RH4MY9H81NYB4";
const INVOICE_ID = "01J92P0V0Q9EK961M5HAQ3C5MY";
const INVOICE_NUMBER = "01J92P0V0R3VSP7D5238DTNZX1";
const INVOICE_ISSUED_ON = "01J92P0V0SNXS6PZ42VDK42HP9";

const billing = () => {
  const backend = new MockBackend();
  const view = backend.generation.databaseView(MAIN)!.view!;
  const files = tableFiles(backend.model.index(), MAIN);
  return { backend, view, files };
};

describe("forms and labels", () => {
  it("names each expression's and condition's form", () => {
    expect(expressionKind(column("i", "x"))).toBe("column");
    expect(expressionKind(param("p"))).toBe("param");
    expect(expressionKind(literal(0))).toBe("value");
    expect(expressionKind(nullValue())).toBe("null");
    expect(expressionKind(operation("+", literal(1), literal(2)))).toBe("op");
    expect(expressionKind(call("count"))).toBe("call");
    expect(expressionKind(cast(literal("1"), "int32"))).toBe("cast");
    expect(expressionKind(sqlText({ "*": "1" }))).toBe("sql");
    expect(expressionKind({})).toBeNull();
    expect(predicateKind(and(compare("isNull", column("i", "x"))))).toBe("and");
    expect(predicateKind(not(compare("eq", literal(1), literal(1))))).toBe("not");
    expect(predicateKind({ exists: { from: { source: "t" } } })).toBe("exists");
    expect(predicateKind(compare("eq", literal(1), literal(1)))).toBe("compare");
  });

  it("describes expressions and conditions compactly", () => {
    expect(describeExpression(call("lower", column("c", "name")))).toBe("lower(c.name)");
    expect(describeExpression(call("count"))).toBe("count(*)");
    expect(describeExpression(operation("concat", column("c", "a"), literal(" "), column("c", "b")))).toBe("(c.a || ' ' || c.b)");
    expect(describeExpression(operation("-", literal(1)))).toBe("(-1)");
    expect(describeExpression(cast(param("x"), "date"))).toBe("cast(@x as date)");
    expect(describeExpression(sqlText({ postgresql: "now()", sqlite: "date()" }))).toBe("sql (postgresql, sqlite)");
    expect(describePredicate(compare("eq", column("i", "status"), literal("I")))).toBe("i.status = 'I'");
    expect(describePredicate(compare("in", column("i", "status"), param("statuses")))).toBe("i.status in (@statuses)");
    expect(describePredicate(compare("between", column("i", "d"), [param("a"), param("b")]))).toBe("i.d between @a and @b");
    expect(describePredicate(compare("isNotNull", column("i", "d")))).toBe("i.d is not null");
    expect(describePredicate(and(compare("isNull", column("i", "d")), compare("isNull", column("i", "e"))))).toBe("all of (2)");
    expect(describePredicate(not({ exists: { from: { source: "t", alias: "x" } } }))).toBe("not (exists (x))");
    // A column label with its physical name.
    expect(describeExpression(column("i", INVOICE_NUMBER), (t) => (t.endsWith(INVOICE_NUMBER) ? "i.number" : t))).toBe("i.number");
  });

  it("reads typed literals, and quotes a text that reads as something else", () => {
    expect(parseLiteral("42")).toBe(42);
    expect(parseLiteral("-1.5")).toBe(-1.5);
    expect(parseLiteral("true")).toBe(true);
    expect(parseLiteral("'42'")).toBe("42");
    expect(parseLiteral("I")).toBe("I");
    expect(literalText("42")).toBe("'42'");
    expect(literalText("I")).toBe("I");
    expect(literalText(false)).toBe("false");
  });

  it("keeps the digits of a number with a fractional point as a typed decimal literal", () => {
    expect(literalExpression("2.0")).toEqual({ value: "2.0", type: "decimal" });
    expect(literalExpression(" -007.50 ")).toEqual({ value: "-7.50", type: "decimal" });
    expect(literalExpression("0.5")).toEqual({ value: "0.5", type: "decimal" });
    expect(literalExpression("42")).toEqual({ value: 42 });
    expect(literalExpression("1e3")).toEqual({ value: 1000 });
    expect(literalExpression("'2.0'")).toEqual({ value: "2.0" });
    expect(valueText({ value: "2.0", type: "decimal" })).toBe("2.0");
    expect(valueText({ value: "2.0" })).toBe("'2.0'");
    expect(describeExpression({ value: "2.0", type: "decimal" })).toBe("2.0");
  });
});

describe("constructors and form changes", () => {
  it("writes a comparison's right side as its operator takes it", () => {
    expect(rightShape("isNull")).toBe("none");
    expect(rightShape("in")).toBe("list");
    expect(rightShape("between")).toBe("two");
    expect(rightShape("eq")).toBe("one");
    // A list of one is written as the expression, as the canonical writer does.
    expect(compare("in", column("i", "s"), [param("ids")])).toEqual({ op: "in", left: { column: "i.s" }, right: { param: "ids" } });
    expect(compare("isNull", column("i", "s"), literal(1))).toEqual({ op: "isNull", left: { column: "i.s" } });
    const eq = compare("eq", column("i", "s"), param("p"));
    const fill = () => literal("");
    expect(withComparison(eq, "isNull", fill)).toEqual({ op: "isNull", left: { column: "i.s" } });
    expect(withComparison(eq, "between", fill)).toEqual({ op: "between", left: { column: "i.s" }, right: [{ param: "p" }, { value: "" }] });
    expect(withComparison(withComparison(eq, "isNull", fill), "ge", fill)).toEqual({ op: "ge", left: { column: "i.s" }, right: { value: "" } });
    expect(withComparison(eq, "notIn", fill)).toEqual({ op: "notIn", left: { column: "i.s" }, right: { param: "p" } });
  });

  it("builds another form from the current expression", () => {
    const c = column("i", "x");
    expect(withExpressionKind(c, "call", {})).toEqual({ call: "lower", args: [c] });
    expect(withExpressionKind(c, "cast", {})).toEqual({ cast: c, type: "string" });
    expect(withExpressionKind(c, "op", {})).toEqual({ op: "+", args: [c, { value: 1 }] });
    expect(withExpressionKind(c, "param", { param: "p" })).toEqual({ param: "p" });
    expect(withExpressionKind(param("p"), "column", { column: "i.y" })).toEqual({ column: "i.y" });
    expect(withExpressionKind(c, "null", {})).toEqual({ null: true });
    // SQL per dialect starts as NULL, valid everywhere, never as a label of the tree it replaces.
    expect(withExpressionKind(c, "sql", { dialect: "postgresql" })).toEqual({ sql: { postgresql: "NULL" } });
    expect(expressionKind(withExpressionKind(c, "case", {}))).toBe("case");
  });
});

describe("checks", () => {
  it("finds what an expression lacks", () => {
    expect(expressionProblem(column("i", "x"))).toBeNull();
    expect(expressionProblem({ column: "" })).toBe("Pick a column.");
    expect(expressionProblem({ op: "+" })).toBe("An operation needs at least two operands.");
    expect(expressionProblem({ op: "*", args: [literal(2)] })).toBe("An operation needs at least two operands.");
    expect(expressionProblem({ op: "-", args: [literal(2)] })).toBeNull();
    expect(expressionProblem({ op: "-" })).toBe("A subtraction needs two operands, or one to negate.");
    expect(expressionProblem(call("lower); DROP TABLE t; --"))).toBe("A function name is letters, digits and underscores (optionally schema.name).");
    expect(expressionProblem(call("pg_catalog.lower", column("i", "x")))).toBeNull();
    expect(expressionProblem({ sql: { postgresql: "  " } })).toBe("Write the SQL for at least one dialect (or * for any).");
    expect(expressionProblem({ value: "2.0", type: "decimal" })).toBeNull();
    expect(expressionProblem({ value: "2.x", type: "decimal" })).toBe("A typed value is a decimal number written as text.");
    expect(expressionProblem({ cast: literal(1) })).toBe("A conversion needs a type.");
    expect(expressionProblem({ sql: {} })).toBe("Write the SQL for at least one dialect (or * for any).");
    expect(expressionProblem({ column: "i.x", param: "p" })).toBe("An expression has one form, not column and param.");
    expect(expressionProblem(call("lower", { column: "" }))).toBe("Pick a column.");
  });

  it("finds a condition's wrong shape", () => {
    expect(predicateProblem(compare("eq", column("i", "x"), param("p")))).toBeNull();
    expect(predicateProblem({ op: "between", left: column("i", "x"), right: [literal(1)] })).toBe("between needs two values: the low and the high bound.");
    expect(predicateProblem({ op: "isNull", left: column("i", "x"), right: literal(1) })).toBe("'isNull' takes no right side.");
    expect(predicateProblem({ op: "in", left: column("i", "x") })).toBe("'in' needs a list of values or a list parameter.");
    expect(predicateProblem({ op: "eq", left: column("i", "x") })).toBe("'eq' needs one value on its right side.");
    expect(predicateProblem({ and: [] })).toBe("A group needs at least one condition.");
    expect(predicateProblem({ exists: { from: { source: "" } } })).toBe("A nested query needs a source.");
    expect(nameProblem("1x", [], "parameter")).toBe("Use letters, digits and underscores, not starting with a digit.");
    expect(nameProblem("limit", ["limit"], "parameter")).toBe("limit is already a parameter of this query.");
    expect(nameProblem("", [], "alias")).toBe("An alias needs a name.");
  });
});

describe("edits across the query", () => {
  const doc = (): QueryDoc => ({
    kind: "query",
    id: "Q",
    name: "Q",
    database: MAIN,
    entity: INVOICE,
    parameters: [
      { name: "take", type: "int32" },
      { name: "who", type: "uuid" },
    ],
    from: { source: INVOICES_OVERLAY, alias: "i" },
    select: [{ attribute: INVOICE_ID, expression: column("i", INVOICE_ID) }],
    where: and(compare("eq", column("i", "x"), param("who")), compare("gt", param("take"), literal(0))),
    paging: { limit: "take" },
    collections: [{ attribute: "E", query: { from: { source: "L", alias: "l" }, where: compare("eq", column("l", "inv"), column("i", INVOICE_ID)) } }],
  });

  it("renames a parameter everywhere it is used, and refuses a taken name", () => {
    const d = doc();
    expect(renameParameter(d, "take", "limit")).toBe(true);
    expect(d.parameters![0].name).toBe("limit");
    expect(d.paging).toEqual({ limit: "limit" });
    expect(d.where!.and![1].left).toEqual({ param: "limit" });
    expect(renameParameter(d, "who", "limit")).toBe(false);
  });

  it("renames an alias in the body and in the collections that correlate with it", () => {
    const d = doc();
    renameAlias(d, "i", "inv");
    expect(d.from.alias).toBe("inv");
    expect(d.select[0].expression).toEqual({ column: `inv.${INVOICE_ID}` });
    expect(d.where!.and![0].left).toEqual({ column: "inv.x" });
    expect(d.collections![0].query.where).toEqual(compare("eq", column("l", "inv"), column("inv", INVOICE_ID)));
  });

  it("turns the fields a new result entity lacks into named fields, keeping their expressions", () => {
    const d = doc();
    d.select = [
      { attribute: INVOICE_ID, expression: column("i", INVOICE_ID) },
      { attribute: "OLD", expression: column("i", "n") },
      { attribute: "OLD2", name: "kept", expression: column("i", "k") },
      { name: "number", expression: literal(1) },
      { attribute: "NUM", expression: column("i", "x") },
    ];
    const names: Record<string, string> = { [INVOICE_ID]: "id", OLD: "total", NUM: "number" };
    setResultEntity(
      d,
      CUSTOMER,
      (id) => names[id],
      (a) => a === INVOICE_ID,
    );
    expect(d.entity).toBe(CUSTOMER);
    expect(d.select).toEqual([
      { attribute: INVOICE_ID, expression: column("i", INVOICE_ID) },
      { name: "total", expression: column("i", "n") },
      { name: "kept", expression: column("i", "k") },
      { name: "number", expression: literal(1) },
      // Its attribute's name is taken by another field: numbered.
      { name: "number2", expression: column("i", "x") },
    ]);
  });

  it("drops the entity for an ad hoc row, keeping the attributes' names", () => {
    const d = doc();
    setResultEntity(d, null, (id) => (id === INVOICE_ID ? "id" : undefined));
    expect(d.entity).toBeUndefined();
    expect(d.select).toEqual([{ name: "id", expression: column("i", INVOICE_ID) }]);
    setResultEntity(d, CUSTOMER, () => undefined);
    expect(d.entity).toBe(CUSTOMER);
    const t: QueryDoc = { ...doc(), joins: [], groupBy: [], distinct: false };
    tidy(t);
    expect(["joins", "groupBy", "distinct"].filter((k) => k in t)).toEqual([]);
  });

  it("keeps the select list in the attributes' order", () => {
    const attributes = [
      { id: "a", name: "a" },
      { id: "b", name: "b" },
      { id: "c", name: "c" },
    ];
    const select = [
      { attribute: "a", expression: literal(1) },
      { attribute: "c", expression: literal(3) },
      { name: "extra", expression: literal(4) },
    ];
    expect(withAttributeField(select, attributes, { attribute: "b", expression: literal(2) }).map((f) => f.attribute ?? f.name)).toEqual([
      "a",
      "b",
      "c",
      "extra",
    ]);
    expect(withAttributeField(select, attributes, { attribute: "c", expression: literal(9) })[1].expression).toEqual(literal(9));
  });

  it("names aliases and fields", () => {
    expect(defaultAlias("invoices")).toBe("i");
    expect(defaultAlias("invoice_lines")).toBe("il");
    expect(defaultAlias("InvoiceLines")).toBe("il");
    expect(defaultAlias("invoices", ["i"])).toBe("i2");
    expect(defaultAlias("order_notes")).toBe("on2");
    expect(fieldName("issued_on")).toBe("issuedOn");
    expect(fieldName("id", ["id"])).toBe("id2");
  });
});

describe("over the billing database", () => {
  it("lists the tables and views a query may read, and finds a source by key, file id, view id or entity id", () => {
    const { view, files } = billing();
    const options = sourceOptions(view);
    expect(options.map((o) => o.name)).toEqual(expect.arrayContaining(["customers", "invoices", "invoice_lines", "outstanding_invoices"]));
    expect(findSource(view, INVOICES_OVERLAY, files)?.name).toBe("invoices");
    expect(findSource(view, `${INVOICE_LINE}@${MAIN}`, files)?.name).toBe("invoice_lines");
    expect(findSource(view, CUSTOMER, files)?.name).toBe("customers");
    expect(findSource(view, "01J92P0V1YZ4YP352KD1A50FXS", files)?.kind).toBe("view");
    expect(findSource(view, "nope", files)).toBeNull();
  });

  it("resolves column references as the engine does, and labels them with the physical names", () => {
    const { view, files } = billing();
    const scope = scopeOf({ from: { source: INVOICES_OVERLAY, alias: "i" } }, view, files);
    expect(resolveColumn(scope, `i.${INVOICE_ISSUED_ON}`)?.column.name).toBe("issued_on");
    expect(resolveColumn(scope, "i.deleted_at")?.column.name).toBe("deleted_at");
    expect(resolveColumn(scope, "i.ISSUEDON")?.column.name).toBe("issued_on");
    expect(resolveColumn(scope, "issued_on")?.alias.alias).toBe("i");
    expect(resolveColumn(scope, "x.issued_on")).toBeNull();
    expect(columnLabel(scope, `i.${INVOICE_NUMBER}`)).toBe("i.number");
    expect(columnLabel(scope, "i.nope")).toBe("i.nope");
    // The aliases of the query around a nested one are in scope, marked outer.
    const nested = scopeOf({ from: { source: `${INVOICE_LINE}@${MAIN}`, alias: "l" } }, view, files, scope);
    expect(nested.map((s) => [s.alias, s.outer])).toEqual([
      ["l", false],
      ["i", true],
    ]);
  });

  it("joins by a foreign key in either direction", () => {
    const { view, files } = billing();
    const scope = scopeOf({ from: { source: INVOICES_OVERLAY, alias: "i" }, joins: [{ source: `${INVOICE_LINE}@${MAIN}`, alias: "il" }] }, view, files);
    const [invoices, lines] = scope;
    const fromLines = foreignKeyJoins(lines, [invoices]);
    expect(fromLines).toHaveLength(1);
    expect(fromLines[0].label).toMatch(/^il\.invoice_id = i\.id \(fk_/);
    expect(fromLines[0].on).toEqual(compare("eq", column("il", `01J92P0V1GMF7GJPA7981CH7YG.${INVOICE_ID}`), column("i", INVOICE_ID)));
    // The other way round: the invoice's foreign key to its customer.
    const customers = scopeOf({ from: { source: CUSTOMER, alias: "c" } }, view, files)[0];
    const toCustomer = foreignKeyJoins(customers, [invoices]);
    expect(toCustomer[0].label).toMatch(/^i\.customer_id = c\.id/);
    expect(foreignKeyJoins(invoices, [])).toEqual([]);
  });

  it("fills the select list from same-named columns, skipping attributes that have a field", () => {
    const { view, files } = billing();
    const alias = scopeOf({ from: { source: INVOICES_OVERLAY, alias: "i" } }, view, files)[0];
    const attributes = [
      { id: INVOICE_ID, name: "id" },
      { id: INVOICE_NUMBER, name: "number" },
      { id: INVOICE_ISSUED_ON, name: "issuedOn" },
      { id: "01X", name: "deletedAt" },
      { id: "02X", name: "nothingLikeIt" },
    ];
    const { select, filled } = fillFromColumns([{ attribute: INVOICE_ID, expression: literal(1) }], attributes, alias);
    expect(filled).toEqual(["number", "issuedOn", "deletedAt"]);
    expect(select.map((f) => f.expression)).toEqual([
      literal(1),
      column("i", INVOICE_NUMBER),
      column("i", INVOICE_ISSUED_ON),
      column("i", alias.columns.find((c) => c.name === "deleted_at")!.ref),
    ]);
    // A value object member and a foreign key fill from the column whose key they start.
    const total = alias.columns.find((c) => c.name === "total_amount")!.ref;
    const customer = alias.columns.find((c) => c.name === "customer_id")!.ref;
    const more = fillFromColumns(
      [],
      [
        { id: total, name: "total.amount" },
        { id: customer.split(".")[0], name: "customer (foreign key to Customer)" },
      ],
      alias,
    );
    expect(more.select.map((f) => [f.attribute, f.expression])).toEqual([
      [total, column("i", total)],
      [customer.split(".")[0], column("i", customer)],
    ]);
  });

  it("offers only named ends as collections and foreign keys, and a value object's members as fields", () => {
    const rows = [
      { id: "E", kind: "entity", name: "Order" },
      { id: "L", kind: "entity", name: "Line" },
      { id: "C", kind: "entity", name: "Client" },
      { id: "M", kind: "value-object", name: "Money" },
      {
        id: "R1",
        kind: "relation",
        name: "has",
        ends: [
          { entity: "E", role: "order" },
          { entity: "L", role: "lines" },
        ],
      },
      {
        id: "R2",
        kind: "relation",
        name: "notes",
        ends: [
          { entity: "E", role: "order" },
          { entity: "L", role: "notes" },
        ],
      },
      {
        id: "R3",
        kind: "relation",
        name: "placedBy",
        ends: [
          { entity: "E", role: "orders" },
          { entity: "C", role: "client" },
        ],
      },
    ] as unknown as ElementSummary[];
    const docs = new Map<string, { json: unknown }>([
      [
        "E",
        {
          json: {
            kind: "entity",
            id: "E",
            name: "Order",
            attributes: [
              { id: "A1", name: "id", type: "uuid" },
              { id: "A2", name: "total", type: { ref: "M" } },
            ],
          },
        },
      ],
      [
        "M",
        {
          json: {
            kind: "value-object",
            id: "M",
            name: "Money",
            attributes: [
              { id: "M1", name: "amount", type: "decimal" },
              { id: "M2", name: "currency", type: "string" },
            ],
          },
        },
      ],
      [
        "R1",
        {
          json: {
            kind: "relation",
            id: "R1",
            ends: [
              { id: "X1", entity: "E", role: "order", max: 1 },
              { id: "Y1", entity: "L", role: "lines", navigation: "lines" },
            ],
          },
        },
      ],
      // A to-many end without a navigation name is no navigation: the engine would report MQ4027 for it.
      [
        "R2",
        {
          json: {
            kind: "relation",
            id: "R2",
            ends: [
              { id: "X2", entity: "E", role: "order", max: 1 },
              { id: "Y2", entity: "L", role: "notes" },
            ],
          },
        },
      ],
      [
        "R3",
        {
          json: {
            kind: "relation",
            id: "R3",
            ends: [
              { id: "X3", entity: "E", role: "orders" },
              { id: "Y3", entity: "C", role: "client", navigation: "client", max: 1 },
            ],
          },
        },
      ],
    ]);
    const shape = entityShapeOf(["E"], ["R1", "R2", "R3"], docs, [], indexLookup(rows));
    expect(shape.collections.map((c) => [c.value, c.label])).toEqual([["Y1", "lines (Line)"]]);
    expect(shape.fills.map((f) => [f.id, f.name, f.fieldName])).toEqual([
      ["A2.M1", "total.amount", "totalAmount"],
      ["A2.M2", "total.currency", "totalCurrency"],
      ["Y3", "client (foreign key to Client)", "clientId"],
    ]);
  });
});

describe("New query…", () => {
  const input = (over: Partial<DatabaseObjectInput>): DatabaseObjectInput => ({
    kind: "query",
    name: "OpenInvoices",
    database: MAIN,
    schema: null,
    source: `${INVOICE}@${MAIN}`,
    alias: "i",
    ...over,
  });

  it("checks the name among the database's queries, the source and the alias", () => {
    const taken = [
      { schema: null, name: "InvoicesByCustomer", kind: "query" },
      { schema: "billing", name: "OpenInvoices", kind: "table" },
    ];
    expect(databaseObjectProblems(input({}), taken, null, "billing")).toEqual({});
    expect(databaseObjectProblems(input({ name: "invoicesbycustomer" }), taken, null, "billing").name).toBe(
      "InvoicesByCustomer is already a query of this database.",
    );
    expect(databaseObjectProblems(input({ source: "" }), taken, null, "billing").source).toBe("Pick the table or view the query reads.");
    expect(databaseObjectProblems(input({ alias: "1i" }), taken, null, "billing").alias).toBe(
      "Use letters, digits and underscores, not starting with a digit.",
    );
    expect(SCHEMA_ELEMENT_KINDS).not.toContain("query");
  });

  it("starts a valid query: the entity's key filled from its column, or the source's first column for an ad hoc row", () => {
    const { view, files } = billing();
    const invoices = findSource(view, INVOICE, files);
    expect(queryAlias(invoices)).toBe("i");
    const keyed = querySelect(invoices, "i", { id: INVOICE_ID, name: "id" });
    expect(keyed).toEqual([{ attribute: INVOICE_ID, expression: column("i", INVOICE_ID) }]);
    const adHoc = querySelect(invoices, "i", null);
    expect(adHoc).toEqual([{ name: "id", expression: column("i", invoices!.table!.columns[0].key) }]);
    expect(querySelect(null, "x", null)).toEqual([{ name: "value", expression: { value: 1 } }]);
    let n = 0;
    const json = buildDatabaseObject(input({ entity: INVOICE, select: keyed }), () => `ID${++n}`);
    expect(json).toEqual({
      kind: "query",
      id: "ID1",
      name: "OpenInvoices",
      database: MAIN,
      entity: INVOICE,
      from: { source: `${INVOICE}@${MAIN}`, alias: "i" },
      select: keyed,
    });
    expect(buildDatabaseObject(input({ entity: null, alias: "", select: adHoc }), () => "ID")).not.toHaveProperty("entity");
  });

  it("validates as a query document the mock accepts as one undo step", () => {
    const { backend, view, files } = billing();
    const invoices = findSource(view, INVOICE, files);
    const json = buildDatabaseObject(
      input({ entity: INVOICE, select: querySelect(invoices, "i", { id: INVOICE_ID, name: "id" }) }),
      () => "01K6QRY0000000000000000099",
    );
    const result = backend.model.create(json as never);
    expect(result.outcome).toBe("saved");
    expect(backend.model.get("01K6QRY0000000000000000099")?.path).toBe(".maquettiste/model/databases/main/queries/open-invoices.json");
  });
});

describe("the editor's other places", () => {
  it("opens a query in its editor, lists queries on the Database screen and previews their SQL", () => {
    expect(hasEditor("query")).toBe(true);
    expect(LIST_KINDS.find((k) => k.kind === "query")?.label).toBe("Queries");
    expect(OBJECT_LIST_MEMBERS.query).toBe("queries");
    const packs = [{ name: "sql-ddl", units: [{ id: "schema", for: "select databases" }] }];
    const target = ddlPreviewTarget(packs, MAIN, null, { kind: "query", id: "Q" });
    expect(target).toEqual({ pack: "", unit: "", elementId: "Q", scope: "query" });
    expect(ddlPreviewCaption(target!, "InvoicesByCustomer")).toBe("query SQL · InvoicesByCustomer");
    expect(unitScope("each query")).toMatchObject({ label: "query", indexKind: "query", variable: "query" });
    expect(unitScope("select queries").label).toBe("query");
    expect(scopeHelp("each query")).toMatch(/once per query/);
  });

  it("reads a parameter's default as its type takes it", () => {
    expect(parameterDefault("int32", "50")).toBe(50);
    expect(parameterDefault("decimal", "1.5")).toBe(1.5);
    expect(parameterDefault("bool", "true")).toBe(true);
    expect(parameterDefault("string", "%")).toBe("%");
    expect(parameterDefault("int32", "many")).toBe("many");
    expect(parameterDefault("int32", "  ")).toBeUndefined();
  });
});
