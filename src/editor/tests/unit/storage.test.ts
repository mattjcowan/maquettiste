// The entity side's storage (erratum E43): the field map model (editors/storage/fieldMap.ts) with its statuses and Map by
// name, entities to tables by name (Auto-map), the relationship storage helpers, and the mock's materialize, binding SQL and
// binding rules (mocks/model/bindings.ts).
import { describe, expect, it } from "vitest";
import {
  attributeRows,
  autoMapRow,
  bindingProblems,
  columnRows,
  constantValue,
  matchByName,
  newBinding,
  retarget,
  setColumnStatus,
  setConstants,
  setField,
  setWrite,
  storageAttributes,
  tableForEntity,
  writes,
  type Binding,
  type BindingSource,
  type SourceColumn,
  type StorageAttribute,
} from "@/editors/storage/fieldMap";
import {
  boundTableOf,
  dependentEnd,
  foreignKeyChoices,
  reverseForeignKeys,
  setEndForeignKey,
  setJunctionTable,
  setRelationForeignKey,
} from "@/editors/storage/relationStorage";
import { MockBackend } from "@/mocks/backend";
import { bindingSql } from "@/mocks/model/bindings";

const col = (key: string, name: string, extra: Partial<SourceColumn> = {}): SourceColumn => ({
  key,
  name,
  type: "string",
  nullable: false,
  identity: false,
  computed: false,
  hasDefault: false,
  isPrimaryKey: false,
  ...extra,
});
const attr = (ref: string, name: string, extra: Partial<StorageAttribute> = {}): StorageAttribute => ({
  ref,
  name,
  origin: "own",
  type: "string",
  isKey: false,
  required: false,
  readOnly: false,
  expected: true,
  ...extra,
});

// A notes-like shared table: one table, told apart by entity_type.
const NOTES: BindingSource = {
  kind: "table",
  id: "T",
  name: "notes",
  schema: "billing",
  columns: [
    col("c-id", "id", { isPrimaryKey: true }),
    col("c-type", "entity_type"),
    col("c-entity", "entity_id"),
    col("c-body", "body"),
    col("c-created", "created_at", { hasDefault: true }),
    col("c-flag", "pinned"),
  ],
};
const ATTRS = [attr("a-id", "id", { isKey: true }), attr("a-product", "productId"), attr("a-body", "body"), attr("a-created", "createdAt", { readOnly: true })];

describe("the field map", () => {
  it("gives every source column a status, as the engine's", () => {
    const b: Binding = {
      id: "B",
      database: "D",
      source: "T",
      constants: [{ column: "entity_type", value: "product" }],
      fields: [
        { attribute: "a-id", column: "c-id" },
        { attribute: "a-body", column: "body" },
      ],
      columns: [{ column: "c-flag", status: "ignored" }],
    };
    expect(columnRows(b, ATTRS, NOTES.columns).map((r) => [r.column.name, r.status, r.attribute?.name ?? null])).toEqual([
      ["id", "field", "id"],
      ["entity_type", "constant", null],
      ["entity_id", "unaccounted", null],
      ["body", "field", "body"],
      ["created_at", "default", null],
      ["pinned", "ignored", null],
    ]);
    expect(attributeRows(b, ATTRS, NOTES.columns).map((r) => [r.attribute.name, r.status])).toEqual([
      ["id", "mapped"],
      ["productId", "unmapped"],
      ["body", "mapped"],
      ["createdAt", "unmapped"],
    ]);
  });

  it("maps by name, ignoring case and underscores, each free column once", () => {
    const b: Binding = { id: "B", database: "D", source: "T", fields: [{ attribute: "a-id", column: "c-id" }] };
    const matches = matchByName(b, ATTRS, NOTES.columns);
    expect(matches.map((m) => [m.attribute.name, m.column.name])).toEqual([
      ["body", "body"],
      ["createdAt", "created_at"],
    ]);
    // A column a constant holds is not free.
    const taken = matchByName({ ...b, constants: [{ column: "body", value: "x" }] }, ATTRS, NOTES.columns);
    expect(taken.map((m) => m.column.name)).toEqual(["created_at"]);
  });

  it("starts a binding with the fields by name, and edits it gesture by gesture", () => {
    expect(newBinding("B", "D", NOTES, ATTRS)).toEqual({ id: "B", database: "D", source: "T" });
    const b = newBinding("B", "D", NOTES, ATTRS, true);
    expect(b.fields).toEqual([
      { attribute: "a-id", column: "c-id" },
      { attribute: "a-body", column: "c-body" },
      { attribute: "a-created", column: "c-created" },
    ]);
    setConstants(b, [{ column: "c-type", value: constantValue("product") }]);
    setField(b, "a-product", NOTES.columns[2], NOTES.columns);
    setColumnStatus(b, NOTES.columns[5], "ignored", NOTES.columns);
    expect(columnRows(b, ATTRS, NOTES.columns).filter((r) => r.status === "unaccounted")).toEqual([]);
    expect(bindingProblems(b, ATTRS, NOTES)).toEqual([]);
    // A column picked for another attribute moves to it; an ignored column picked for a field is no longer listed.
    setField(b, "a-body", NOTES.columns[5], NOTES.columns);
    expect(b.columns).toBeUndefined();
    expect(columnRows(b, ATTRS, NOTES.columns).find((r) => r.column.name === "body")?.status).toBe("unaccounted");
    // Write none: read-only, and the delete mode goes with it.
    b.delete = "key";
    setWrite(b, "none");
    expect([b.write, b.delete, writes(b, NOTES)]).toEqual(["none", undefined, false]);
    setWrite(b, "source");
    expect([b.write, writes(b, NOTES)]).toEqual([undefined, true]);
  });

  it("reports the binding rules it can see, and the proposed warning for an unmapped attribute", () => {
    const b: Binding = { id: "B", database: "D", source: "T", fields: [{ attribute: "a-body", column: "nope" }] };
    expect(bindingProblems(b, ATTRS, NOTES).map((p) => [p.rule, p.severity])).toEqual([
      ["MQ4045", "error"],
      ["MQ4046", "error"],
      ["MQ4047", "warning"],
      ["MQ4058", "warning"],
    ]);
    expect(bindingProblems({ ...b, source: "X" }, ATTRS, undefined).map((p) => p.rule)).toEqual(["MQ4044"]);
    expect(bindingProblems({ ...b, write: "none", constants: [{ column: "zz" }] }, ATTRS, NOTES).map((p) => p.rule)).toEqual([
      "MQ4045",
      "MQ4048",
      "MQ4047",
      "MQ4058",
    ]);
  });

  it("keeps on a new source only what it has", () => {
    const b: Binding = { id: "B", database: "D", source: "T", fields: [{ attribute: "a-id", column: "c-id" }], constants: [{ column: "c-type", value: "x" }] };
    const view: BindingSource = { kind: "view", id: "V", name: "v", schema: null, columns: [col("id", "id"), col("body", "body")] };
    retarget(b, view);
    expect(b).toEqual({ id: "B", database: "D", source: "V" });
  });

  it("reads constants as numbers and booleans when they are", () => {
    expect([constantValue("12"), constantValue("true"), constantValue("x"), constantValue(" ")]).toEqual([12, true, "x", null]);
  });
});

describe("entities to tables by name (Auto-map)", () => {
  const tables = ["invoices", "invoice_line", "Customer", "people"].map((name, i): BindingSource => ({
    kind: "table",
    id: `t${i}`,
    name,
    schema: null,
    columns: [],
  }));
  it("goes through the naming conventions: case ignored, the preferred plural first", () => {
    expect(tableForEntity("Invoice", tables)?.name).toBe("invoices");
    expect(tableForEntity("InvoiceLine", tables)?.name).toBe("invoice_line");
    expect(tableForEntity("Customer", tables, false)?.name).toBe("Customer");
    expect(tableForEntity("Person", tables)).toBeUndefined();
  });
  it("tells matches, partial matches and misses apart, and takes a picked table", () => {
    const source: BindingSource = { ...NOTES, name: "products", id: "P" };
    expect(autoMapRow({ id: "E", name: "Product" }, ATTRS, [source]).outcome).toBe("partial");
    expect(autoMapRow({ id: "E", name: "Product" }, [ATTRS[0], ATTRS[2]], [source]).outcome).toBe("match");
    expect(autoMapRow({ id: "E", name: "Widget" }, ATTRS, [source]).outcome).toBe("miss");
    expect(autoMapRow({ id: "E", name: "Widget" }, ATTRS, [source], { picked: "P" }).source?.name).toBe("products");
  });
});

describe("the bindable attributes", () => {
  it("lists own, inherited and virtual attributes, value object members and to-one relation keys", () => {
    const docs = new Map<string, Record<string, unknown>>([
      ["B", { kind: "entity", id: "B", name: "Base", attributes: [{ id: "b1", name: "createdBy", type: "string" }] }],
      [
        "E",
        {
          kind: "entity",
          id: "E",
          name: "Order",
          base: "B",
          stereotypes: ["audited"],
          key: { attributes: ["e1"] },
          attributes: [
            { id: "e1", name: "id", type: "uuid" },
            { id: "e2", name: "total", type: { ref: "M" } },
            { id: "e3", name: "lines", type: "string", collection: true },
            { id: "e4", name: "label", type: "string", derived: { expression: "x" } },
          ],
        },
      ],
      [
        "M",
        {
          kind: "value-object",
          id: "M",
          name: "Money",
          attributes: [
            { id: "m1", name: "amount", type: "decimal" },
            { id: "m2", name: "currency", type: "string" },
          ],
        },
      ],
      ["S", { kind: "stereotype", id: "S", key: "audited", attributes: [{ id: "s1", name: "updatedAt", type: "datetime" }] }],
      ["C", { kind: "entity", id: "C", name: "Customer" }],
      [
        "R",
        {
          kind: "relation",
          id: "R",
          name: "places",
          ends: [
            { id: "rc", entity: "C", role: "customer", navigation: "buyer", max: 1 },
            { id: "ro", entity: "E", role: "orders" },
          ],
        },
      ],
    ]);
    expect(storageAttributes("E", docs).map((a) => [a.ref, a.name, a.origin, a.isKey, a.expected])).toEqual([
      ["b1", "createdBy", "inherited", false, true],
      ["e1", "id", "own", true, true],
      ["e2.m1", "totalAmount", "member", false, true],
      ["e2.m2", "totalCurrency", "member", false, true],
      ["e4", "label", "own", false, false],
      ["s1", "updatedAt", "virtual", false, true],
      ["rc", "buyerId", "relation", false, true],
    ]);
  });
});

describe("relationship storage", () => {
  const orders = {
    id: "O",
    name: "orders",
    columns: [{ id: "oc", name: "customer_id" }],
    foreignKeys: [{ id: "fk1", name: "fk_orders_customer", columns: ["oc"], referencesTable: "C" }],
  };
  const customers = { id: "C", name: "customers" };
  it("finds the foreign keys between the bound tables, either way", () => {
    expect(foreignKeyChoices(customers, orders).map((c) => c.label)).toEqual(["orders.fk_orders_customer (customer_id → customers)"]);
    expect(boundTableOf({ bindings: [{ database: "D", source: "C" }] }, "D")).toBe("C");
    expect(boundTableOf({ bindings: [{ database: "D", source: "V", write: { table: "C" } }] }, "D")).toBe("C");
    expect(boundTableOf({}, "D")).toBeNull();
  });
  it("offers only the keys held by the dependent end's table, as the engine accepts them", () => {
    // Ends: [customer (one), order (many)]: the order end is dependent, so orders holds the key.
    const ends = [
      { id: "eC", max: 1 as const },
      { id: "eO", max: "*" as const },
    ];
    const dependent = dependentEnd(ends);
    expect(dependent).toEqual({ index: 1, tie: false });
    expect(foreignKeyChoices(customers, orders, dependent).map((c) => c.key.id)).toEqual(["fk1"]);
    // Bound the other way round (the customer end on orders' table): the key is reversed, not offered, and said.
    expect(foreignKeyChoices(orders, customers, dependent)).toEqual([]);
    expect(reverseForeignKeys(orders, customers, dependent)).toBe(1);
    expect(dependentEnd([{ id: "a" }, { id: "b" }])).toBeNull();
    // One to one: the optional end is dependent; a tie follows foreignKeyEnd (default the second end) and offers either way.
    expect(
      dependentEnd([
        { id: "a", min: 1, max: 1 },
        { id: "b", max: 1 },
      ]),
    ).toEqual({ index: 1, tie: false });
    expect(
      dependentEnd([
        { id: "a", min: 0, max: 1 },
        { id: "b", min: 1, max: 1 },
      ]),
    ).toEqual({ index: 0, tie: false });
    const tie = dependentEnd(
      [
        { id: "a", max: 1 },
        { id: "b", max: 1 },
      ],
      "a",
    );
    expect(tie).toEqual({ index: 0, tie: true });
    expect(foreignKeyChoices(orders, customers, tie).map((c) => [c.key.id, c.end])).toEqual([["fk1", 0]]);
    expect(foreignKeyChoices(customers, orders, tie).map((c) => [c.key.id, c.end])).toEqual([["fk1", 1]]);
    const json: Record<string, unknown> = {};
    setRelationForeignKey(json, "fk1", "b");
    expect(json).toEqual({ foreignKey: "fk1", foreignKeyEnd: "b" });
  });
  it("writes the relation mapping's foreignKey, junctionTable and ends", () => {
    const json: Record<string, unknown> = { shape: "foreign-key" };
    setRelationForeignKey(json, "fk1");
    expect(json).toEqual({ foreignKey: "fk1" });
    setJunctionTable(json, "J");
    setEndForeignKey(json, "e1", "fkA");
    expect(json).toEqual({ junctionTable: "J", shape: "junction", ends: [{ end: "e1", foreignKey: "fkA" }] });
    setJunctionTable(json, null);
    expect(json).toEqual({});
  });
});

// ------------------------------------------------------------------ the mock

const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";
const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";
const INVOICE_OVERLAY = "01J92P0V1T0J6RH4MY9H81NYB4";
const INVOICE_MAPPING = "01J92P0V200XW93JQYSRCT2SE3";
const PLACES = "01J92P0V1ACKN3G6TK3NJDTM82";
const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const INVOICE_NOTE = "01K6BND0000000000000000010";

describe("the mock's materialize", () => {
  it("lists what is unbound, and the bindings of each table", () => {
    const model = new MockBackend().model;
    const status = model.materializeStatus(MAIN)!;
    expect(status.entities.find((e) => e.id === INVOICE)).toMatchObject({ projected: true });
    expect(status.entities.some((e) => e.id === INVOICE_NOTE)).toBe(false);
    expect(status.sources.find((s) => s.name === "notes")?.boundBy.map((b) => [b.entityName, b.constants])).toEqual([
      ["CustomerNote", [{ column: "01K6BND0000000000000000003", value: "customer" }]],
      ["InvoiceNote", [{ column: "01K6BND0000000000000000003", value: "invoice" }]],
    ]);
  });

  it("creates the tables of two related entities in one batch: bindings, the overlay kept, the foreign key named", () => {
    const model = new MockBackend().model;
    const plan = model.previewMaterialize(MAIN, { op: "materialize-tables", entities: [CUSTOMER, INVOICE] });
    expect(plan.valid).toBe(true);
    expect(plan.deletes.map((d) => d.id)).toEqual([INVOICE_MAPPING]);
    const result = model.batch({ operations: [{ op: "materialize-tables", database: MAIN, entities: [CUSTOMER, INVOICE] }] });
    expect(result.body).toMatchObject({ outcome: "saved" });
    const invoice = model.get(INVOICE)!.json as { bindings: Binding[] };
    expect(invoice.bindings[0]).toMatchObject({ database: MAIN, source: INVOICE_OVERLAY });
    const table = model.get(INVOICE_OVERLAY)!.json as { origin?: string; entity?: string; foreignKeys: { id: string; referencesTable: string }[] };
    expect([table.origin, table.entity]).toEqual([undefined, undefined]);
    const customerTable = (model.get(CUSTOMER)!.json as { bindings: Binding[] }).bindings[0].source;
    expect(table.foreignKeys.map((k) => k.referencesTable)).toEqual([customerTable]);
    const relationMapping = [...model.docs().values()].find((d) => d.kind === "mapping" && d.relation === PLACES);
    expect(relationMapping?.foreignKey).toBe(table.foreignKeys[0].id);
    expect(model.get(INVOICE_MAPPING)).toBeNull();
    // Every binding rule holds: no MQ404x finding.
    expect(model.validate().diagnostics.filter((d) => /^MQ40(4[4-9]|5\d)|MQ4011/.test(d.rule))).toEqual([]);
    // Refused a second time: already bound.
    expect(model.previewMaterialize(MAIN, { op: "materialize-tables", entities: [INVOICE] }).diagnostics.map((d) => d.rule)).toEqual(["MQ4055"]);
  });

  it("makes entities from a table and refuses a bound one", () => {
    const model = new MockBackend().model;
    const notes = "01K6BND0000000000000000001";
    expect(model.previewMaterialize(MAIN, { op: "materialize-entities", tables: [notes], package: BILLING }).diagnostics.map((d) => d.rule)).toEqual([
      "MQ4055",
    ]);
    const status = model.materializeStatus(MAIN)!;
    const view = status.sources.find((s) => s.kind === "view")!;
    const plan = model.previewMaterialize(MAIN, { op: "materialize-entities", tables: [view.id], package: BILLING });
    expect(plan.valid).toBe(true);
    const entity = plan.creates[0].element as { name: string; bindings: Binding[]; key?: unknown };
    expect(entity.bindings[0]).toMatchObject({ database: MAIN, source: view.id });
    expect(entity.key).toBeTruthy();
  });

  it("renders a binding's statements, constants as a filter and a value", () => {
    const model = new MockBackend().model;
    const settings = model.projectSettings();
    const input = {
      docs: model.docs(),
      conventions: settings.conventions as Record<string, unknown>,
      databaseConventions: settings.databases as Record<string, Record<string, unknown>>,
    };
    const sql = bindingSql(input, INVOICE_NOTE, "01K6BND0000000000000000015", null, "@")!;
    expect(sql.diagnostics).toEqual([]);
    expect(sql.preview!.select!.sql).toBe(
      'SELECT t.id AS id, t.entity_id AS "invoiceId", t.body AS body, t.created_at AS "createdAt"\nFROM billing.notes t\nWHERE t.entity_type = \'invoice\'',
    );
    expect(sql.preview!.insert!.sql).toBe("INSERT INTO billing.notes (id, entity_id, body, entity_type)\nVALUES (@id, @invoiceId, @body, 'invoice')");
    expect(sql.preview!.delete!.sql).toBe("DELETE FROM billing.notes\nWHERE id = @id AND entity_type = 'invoice'");
    // The fixture's bindings break no binding rule.
    expect(model.validate().diagnostics.filter((d) => /^MQ40(4[4-9]|5\d)$/.test(d.rule))).toEqual([]);
  });
});
