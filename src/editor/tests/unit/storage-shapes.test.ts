// Table per type, child tables, field paths of any depth and relationship attributes on a foreign key, on the Storage tab
// (retire-projection-design.md sections 4 to 6): the pure modules (baseTable.ts, childTables.ts, bindingShape.ts, fieldMap.ts) and the
// mock's statements and findings for them.
import { describe, expect, it } from "vitest";
import { baseKeyChoices, baseProblems, boundBaseOf, joinedKeyColumns, setBaseForeignKey } from "@/editors/storage/baseTable";
import { bindingShape } from "@/editors/storage/bindingShape";
import {
  childTableComplete,
  newChildTable,
  upsertChildTable,
  childAttribute,
  childCandidates,
  childColumnRows,
  childProblems,
  itemAttributes,
  ownerKeyChoices,
  pickChildTable,
  setOrderColumn,
  setOwnerKey,
} from "@/editors/storage/childTables";
import {
  attributeRows,
  bindingProblems,
  columnRows,
  matchByName,
  storageAttributes,
  type Binding,
  type BindingSource,
  type ChildTable,
} from "@/editors/storage/fieldMap";
import { tableDocColumns, type TableDoc } from "@/editors/storage/tableDoc";
import { bindingFindings, bindingSql } from "@/mocks/model/bindings";
import { resolveDatabase } from "@/mocks/model/physical";

type Json = Record<string, unknown>;

const DB = "DB1";
const docs = (...entries: Json[]) => new Map<string, Json>(entries.map((d) => [String(d.id), d]));

// Payment (payments) with CardPayment (card_payments, joined by its key) and a value object nested in a value object.
const payments: TableDoc = {
  id: "T_PAY",
  name: "payments",
  columns: [
    { id: "pc_id", name: "id", type: "int32", identity: true },
    { id: "pc_amount", name: "amount", type: "decimal" },
  ],
  primaryKey: { columns: ["pc_id"] },
};
const cards: TableDoc = {
  id: "T_CARD",
  name: "card_payments",
  columns: [
    { id: "cc_pid", name: "payment_id", type: "int32" },
    { id: "cc_last4", name: "last4", type: "string" },
  ],
  primaryKey: { columns: ["cc_pid"] },
  foreignKeys: [
    { id: "FK_CARD_PAY", name: "fk_card_payment", columns: ["cc_pid"], referencesTable: "T_PAY", referencesColumns: ["pc_id"] },
    { id: "FK_CARD_SELF", name: "fk_other", columns: ["cc_last4"], referencesTable: "T_OTHER" },
  ],
};
const payment: Json = {
  kind: "entity",
  id: "E_PAY",
  name: "Payment",
  key: { attributes: ["a_id"] },
  attributes: [
    { id: "a_id", name: "id", type: "int32" },
    { id: "a_amount", name: "amount", type: "decimal" },
  ],
  bindings: [
    {
      id: "B_PAY",
      database: DB,
      source: "T_PAY",
      fields: [
        { attribute: "a_id", column: "pc_id" },
        { attribute: "a_amount", column: "pc_amount" },
      ],
    },
  ],
};
const card = (binding: Partial<Binding> = {}): Json => ({
  kind: "entity",
  id: "E_CARD",
  name: "CardPayment",
  base: "E_PAY",
  attributes: [{ id: "a_last4", name: "last4", type: "string" }],
  bindings: [
    { id: "B_CARD", database: DB, source: "T_CARD", baseForeignKey: "FK_CARD_PAY", fields: [{ attribute: "a_last4", column: "cc_last4" }], ...binding },
  ],
});

describe("table per type: the base binding and its key", () => {
  it("finds the nearest base bound to the database", () => {
    const d = docs(payment, card());
    expect(boundBaseOf("E_CARD", DB, d)).toMatchObject({ entityId: "E_PAY", name: "Payment" });
    expect(boundBaseOf("E_CARD", "OTHER", d)).toBeUndefined();
    expect(boundBaseOf("E_PAY", DB, d)).toBeUndefined();
  });

  it("offers only the foreign keys that reference the base's table and are the whole primary key", () => {
    expect(baseKeyChoices(cards, "T_PAY").map((k) => k.id)).toEqual(["FK_CARD_PAY"]);
    expect(baseKeyChoices(cards, "T_OTHER")).toEqual([]);
    // A key over a column that is not the primary key does not fit.
    const loose: TableDoc = { ...cards, primaryKey: { columns: ["cc_last4"] } };
    expect(baseKeyChoices(loose, "T_PAY")).toEqual([]);
  });

  it("shows the joined key columns with the base's key attribute they carry", () => {
    const d = docs(payment, card());
    const attrs = storageAttributes("E_PAY", d);
    const [column] = joinedKeyColumns(cards.foreignKeys![0], cards, payments, (payment.bindings as Binding[])[0], attrs);
    expect(column).toEqual({ column: "cc_pid", name: "payment_id", referenced: "id", attribute: "id" });
  });

  it("leaves what the base stores out of the derived binding's own map", () => {
    const d = docs(payment, card());
    const binding = (d.get("E_CARD")!.bindings as Binding[])[0];
    const shape = bindingShape("E_CARD", binding, d, (id) => ({ T_PAY: payments, T_CARD: cards })[id]);
    expect([...shape.extras.baseStored!].sort()).toEqual(["a_amount", "a_id"]);
    expect(shape.extras.baseKeyColumns!.get("cc_pid")).toBe("id");
    const attributes = storageAttributes("E_CARD", d);
    const offered = attributes.filter((a) => !shape.extras.baseStored!.has(a.ref));
    expect(offered.map((a) => a.name)).toEqual(["last4"]);
    // The key is the foreign key's: no MQ4046, no MQ4058 for the inherited attributes, and payment_id counts as mapped.
    const source: BindingSource = { kind: "table", id: "T_CARD", name: "card_payments", schema: null, columns: tableDocColumns(cards) };
    expect(bindingProblems(binding, attributes, source, undefined, undefined, shape.extras)).toEqual([]);
    expect(columnRows(binding, attributes, source.columns, undefined, shape.extras).map((r) => [r.column.name, r.status])).toEqual([
      ["payment_id", "base-key"],
      ["last4", "field"],
    ]);
  });

  it("says MQ4132 for a field of something the base maps", () => {
    const d = docs(payment, card({ fields: [{ attribute: "a_amount", column: "cc_last4" }] }));
    const binding = (d.get("E_CARD")!.bindings as Binding[])[0];
    const shape = bindingShape("E_CARD", binding, d, (id) => ({ T_PAY: payments, T_CARD: cards })[id]);
    const source: BindingSource = { kind: "table", id: "T_CARD", name: "card_payments", schema: null, columns: tableDocColumns(cards) };
    expect(bindingProblems(binding, storageAttributes("E_CARD", d), source, undefined, undefined, shape.extras).map((p) => p.rule)).toContain("MQ4132");
  });

  it("checks the key as the engine does: MQ4130, MQ4133, MQ4128, MQ4129, MQ4131", () => {
    const d = docs(payment, card());
    const base = boundBaseOf("E_CARD", DB, d);
    const b = (d.get("E_CARD")!.bindings as Binding[])[0];
    expect(baseProblems(b, undefined, cards).map((p) => p.rule)).toEqual(["MQ4130"]);
    expect(baseProblems({ ...b, source: "T_PAY" }, base, payments).map((p) => p.rule)).toEqual(["MQ4133"]);
    expect(baseProblems({ ...b, baseForeignKey: "FK_CARD_SELF" }, base, cards).map((p) => p.rule)).toEqual(["MQ4128"]);
    const notPk: TableDoc = { ...cards, primaryKey: { columns: ["cc_last4"] } };
    expect(baseProblems(b, base, notPk).map((p) => p.rule)).toEqual(["MQ4129"]);
    expect(baseProblems(b, base, cards)).toEqual([]);
    const readOnlyBase = { ...base!, binding: { ...base!.binding, write: "none" as const } };
    expect(baseProblems(b, readOnlyBase, cards).map((p) => p.rule)).toEqual(["MQ4131"]);
  });

  it("names the key and drops the fields the base maps; clearing removes the key", () => {
    const b: Binding = {
      id: "B",
      database: DB,
      source: "T_CARD",
      fields: [
        { attribute: "a_id", column: "cc_pid" },
        { attribute: "a_last4", column: "cc_last4" },
      ],
    };
    setBaseForeignKey(b, "FK_CARD_PAY", new Set(["a_id", "a_amount"]));
    expect(b.baseForeignKey).toBe("FK_CARD_PAY");
    expect(b.fields).toEqual([{ attribute: "a_last4", column: "cc_last4" }]);
    setBaseForeignKey(b, null, new Set());
    expect(b.baseForeignKey).toBeUndefined();
  });
});

describe("table per type: what the engine reads the same way", () => {
  it("a subtype that shares a per-type table skips the upper level's attributes as unmapped, and may map the key", () => {
    const sharing: Json = {
      kind: "entity",
      id: "E_SH",
      name: "SharingCard",
      base: "E_CARD",
      attributes: [],
      bindings: [{ id: "B_SH", database: DB, source: "T_CARD", fields: [{ attribute: "a_last4", column: "cc_last4" }] }],
    };
    const d = docs(payment, card(), sharing);
    const binding = (sharing.bindings as Binding[])[0];
    const shape = bindingShape("E_SH", binding, d, (id) => ({ T_PAY: payments, T_CARD: cards })[id]);
    // The nearest upper level is the payment: its attributes are expected nowhere here, but a field for the key is no error.
    expect([...shape.extras.baseStored!].sort()).toEqual(["a_amount", "a_id"]);
    expect(shape.extras.baseFieldsRefused).toBe(false);
    const source: BindingSource = { kind: "table", id: "T_CARD", name: "card_payments", schema: null, columns: tableDocColumns(cards) };
    const attrs = storageAttributes("E_SH", d);
    const rules = bindingProblems(binding, attrs, source, undefined, undefined, shape.extras).map((p) => p.rule);
    expect(rules).not.toContain("MQ4058");
    expect(rules).not.toContain("MQ4132");
    const mapsKey = { ...binding, fields: [...binding.fields!, { attribute: "a_id", column: "cc_pid" }] };
    expect(bindingProblems(mapsKey, attrs, source, undefined, undefined, shape.extras).map((p) => p.rule)).not.toContain("MQ4132");
  });

  it("a base read from a view writes nothing: MQ4131 for a writing subtype, and no MQ4128 for a read-only one", () => {
    const view: TableDoc = { id: "T_PAY", kind: "view", name: "payments_view" };
    const viewBase = { entityId: "E_PAY", name: "Payment", binding: { id: "B_PAY", database: DB, source: "T_PAY" } as Binding };
    const keyElsewhere = { ...(card().bindings as Binding[])[0], baseForeignKey: "FK_CARD_SELF" };
    expect(baseProblems(keyElsewhere, viewBase, cards, view).map((p) => p.rule)).toEqual(["MQ4131"]);
    expect(baseProblems({ ...keyElsewhere, write: "none" }, viewBase, cards, view)).toEqual([]);
    // A base over a table file writes it; one not read yet says nothing.
    expect(baseProblems(keyElsewhere, viewBase, cards, payments).map((p) => p.rule)).toEqual(["MQ4128"]);
    expect(baseProblems(keyElsewhere, viewBase, cards, undefined).map((p) => p.rule)).toEqual(["MQ4128"]);
  });

  it("leaves MQ4047 to the engine when the relations were read only up to a limit", () => {
    const d = docs(payment, card());
    const binding = (d.get("E_CARD")!.bindings as Binding[])[0];
    const source: BindingSource = {
      kind: "table",
      id: "T_CARD",
      name: "card_payments",
      schema: null,
      columns: [
        ...tableDocColumns(cards),
        { key: "x", name: "terms", type: "string", nullable: true, identity: false, computed: false, hasDefault: false, isPrimaryKey: false },
      ],
    };
    const shape = bindingShape("E_CARD", binding, d, (id) => ({ T_PAY: payments, T_CARD: cards })[id]);
    const attrs = storageAttributes("E_CARD", d);
    expect(bindingProblems(binding, attrs, source, undefined, undefined, shape.extras).map((p) => p.rule)).toContain("MQ4047");
    expect(
      bindingProblems(binding, attrs, source, undefined, undefined, { ...shape.extras, relationColumnsIncomplete: true }).map((p) => p.rule),
    ).not.toContain("MQ4047");
  });
});

// A quote with notes (a list of single-field value objects) and a shipping address (a value object holding a geo point).
const geo: Json = {
  kind: "value-object",
  id: "VO_GEO",
  name: "GeoPoint",
  attributes: [
    { id: "g_lat", name: "latitude", type: "decimal" },
    { id: "g_lng", name: "longitude", type: "decimal" },
  ],
};
const address: Json = {
  kind: "value-object",
  id: "VO_ADDR",
  name: "PostalAddress",
  attributes: [
    { id: "ad_line", name: "line1", type: "string" },
    { id: "ad_geo", name: "geo", type: { ref: "VO_GEO" } },
    { id: "ad_tags", name: "tags", type: "string", collection: true },
  ],
};
const note: Json = {
  kind: "value-object",
  id: "VO_NOTE",
  name: "QuoteNote",
  attributes: [
    { id: "n_text", name: "text", type: "string" },
    { id: "n_by", name: "author", type: "string" },
  ],
};
const quote: Json = {
  kind: "entity",
  id: "E_Q",
  name: "Quote",
  key: { attributes: ["q_id"] },
  attributes: [
    { id: "q_id", name: "id", type: "int32" },
    { id: "q_notes", name: "notes", type: { ref: "VO_NOTE" }, collection: true },
    { id: "q_labels", name: "labels", type: "string", collection: true },
    { id: "q_ship", name: "shipTo", type: { ref: "VO_ADDR" } },
  ],
};
const quotes: TableDoc = {
  id: "T_Q",
  name: "quotes",
  columns: [{ id: "qc_id", name: "id", type: "int32" }],
  primaryKey: { columns: ["qc_id"] },
};
const quoteNotes: TableDoc = {
  id: "T_QN",
  name: "quote_notes",
  columns: [
    { id: "nc_q", name: "quote_id", type: "int32" },
    { id: "nc_pos", name: "position", type: "int32" },
    { id: "nc_text", name: "text", type: "string" },
    { id: "nc_by", name: "author", type: "string" },
    { id: "nc_note", name: "note_id", type: "int32", identity: true },
  ],
  primaryKey: { columns: ["nc_q", "nc_pos"] },
  foreignKeys: [
    { id: "FK_QN_Q", name: "fk_quote_notes_quote", columns: ["nc_q"], referencesTable: "T_Q", referencesColumns: ["qc_id"] },
    { id: "FK_QN_X", name: "fk_quote_notes_other", columns: ["nc_text"], referencesTable: "T_OTHER" },
  ],
};
const all = () => docs(quote, geo, address, note);

describe("field paths of any depth", () => {
  it("lists the members of nested value objects and a JSON row for each nested value object and list", () => {
    const rows = storageAttributes("E_Q", all()).map((a) => [a.ref, a.name, a.json ? "json" : (a.type ?? ""), a.expected]);
    expect(rows).toEqual([
      ["q_id", "id", "int32", true],
      ["q_notes", "notes", "json", false],
      ["q_labels", "labels", "json", false],
      ["q_ship.ad_line", "shipToLine1", "string", true],
      ["q_ship.ad_geo.g_lat", "shipToGeoLatitude", "decimal", true],
      ["q_ship.ad_geo.g_lng", "shipToGeoLongitude", "decimal", true],
      ["q_ship.ad_geo", "shipToGeo", "json", false],
      ["q_ship.ad_tags", "shipToTags", "json", false],
      ["q_ship", "shipTo", "json", false],
    ]);
  });

  it("reads a value object held whole as storing its members, and refuses mapping both", () => {
    const b: Binding = {
      id: "B",
      database: DB,
      source: "T_Q",
      fields: [
        { attribute: "q_ship.ad_geo", column: "j" },
        { attribute: "q_ship.ad_geo.g_lat", column: "k" },
      ],
    };
    const attrs = storageAttributes("E_Q", all());
    const status = (ref: string) => attributeRows(b, attrs, []).find((r) => r.attribute.ref === ref)!.status;
    expect(status("q_ship.ad_geo.g_lng")).toBe("whole");
    const source: BindingSource = {
      kind: "table",
      id: "T_Q",
      name: "quotes",
      schema: null,
      columns: [
        { key: "j", name: "j", type: "json", nullable: true, identity: false, computed: false, hasDefault: false, isPrimaryKey: false },
        { key: "k", name: "k", type: "decimal", nullable: true, identity: false, computed: false, hasDefault: false, isPrimaryKey: false },
      ],
    };
    const problems = bindingProblems(b, attrs, source);
    expect(problems.filter((p) => p.rule === "MQ4045").map((p) => p.pointer)).toEqual(["/fields/1/attribute"]);
    // No unmapped member is named for what the whole column stores; Map by name never proposes a JSON row or a member of a held-whole one.
    expect(problems.find((p) => p.rule === "MQ4058")?.message).not.toContain("shipToGeo");
    expect(matchByName({ fields: [{ attribute: "q_ship.ad_geo", column: "j" }] }, attrs, source.columns).map((m) => m.attribute.ref)).toEqual([]);
  });
});

describe("child tables", () => {
  const binding = (extra: Partial<Binding> = {}): Binding => ({
    id: "B_Q",
    database: DB,
    source: "T_Q",
    fields: [{ attribute: "q_id", column: "qc_id" }],
    ...extra,
  });

  it("offers the lists and value objects the entity has, until one is stored", () => {
    expect(childCandidates("E_Q", binding(), all()).map((c) => [c.name, c.isList])).toEqual([
      ["notes", true],
      ["labels", true],
      ["shipTo", false],
    ]);
    const b = binding({ childTables: [{ id: "C1", attribute: "q_notes", table: "T_QN", ownerForeignKey: "FK_QN_Q" }] });
    expect(childCandidates("E_Q", b, all()).map((c) => c.name)).toEqual(["labels", "shipTo"]);
  });

  it("offers nothing a base level above stores, by a child table or by fields, along the whole chain", () => {
    const rootBinding: Binding = {
      id: "BR",
      database: DB,
      source: "T_Q",
      fields: [{ attribute: "q_ship.ad_line", column: "x" }],
      childTables: [{ id: "C0", attribute: "q_notes", table: "T_QN", ownerForeignKey: "FK_QN_Q" }],
    };
    const mid = { kind: "entity", id: "E_M", name: "Mid", base: "E_Q", bindings: [{ id: "BM", database: DB, source: "T_M", baseForeignKey: "FK_M" }] };
    const leaf = { kind: "entity", id: "E_L", name: "Leaf", base: "E_M", bindings: [{ id: "BL", database: DB, source: "T_L", baseForeignKey: "FK_L" }] };
    const chain = docs({ ...quote, bindings: [rootBinding] }, mid, leaf, geo, address, note);
    // Two levels up from the leaf: the root's child table and fields still count.
    expect(childCandidates("E_L", (leaf.bindings as Binding[])[0], chain).map((c) => c.name)).toEqual(["labels"]);
    // Without a key and in another table, the binding joins nothing and every attribute is its own.
    expect(childCandidates("E_L", { id: "BL", database: DB, source: "T_L" }, chain).map((c) => c.name)).toEqual(["notes", "labels", "shipTo"]);
    // A subtype that shares a per-type table has the same levels above it.
    const sharing = { kind: "entity", id: "E_S", name: "Sharing", base: "E_M", bindings: [{ id: "BS", database: DB, source: "T_M" }] };
    const shared = docs({ ...quote, bindings: [rootBinding] }, mid, sharing, geo, address, note);
    expect(childCandidates("E_S", (sharing.bindings as Binding[])[0], shared).map((c) => c.name)).toEqual(["labels"]);
  });

  it("holds an entry back until it names its table and owner key, then saves it once", () => {
    const entry = newChildTable("C1", "q_notes");
    expect(childTableComplete(entry)).toBe(false);
    expect(childTableComplete({ ...entry, table: "T_QN" })).toBe(false);
    const b = binding({
      fields: [
        { attribute: "q_id", column: "qc_id" },
        { attribute: "q_notes.n_text", column: "x" },
      ],
    });
    expect(b.childTables).toBeUndefined();
    const done = { ...entry, table: "T_QN", ownerForeignKey: "FK_QN_Q" };
    expect(childTableComplete(done)).toBe(true);
    upsertChildTable(b, done);
    expect(b.childTables).toEqual([done]);
    expect(b.fields).toEqual([{ attribute: "q_id", column: "qc_id" }]);
    upsertChildTable(b, { ...done, orderColumn: "nc_pos" });
    expect(b.childTables).toHaveLength(1);
  });

  it("maps a list of value objects by member path, and a list of single values by the attribute itself", () => {
    const notes = itemAttributes(childAttribute("E_Q", "q_notes", all()), all());
    expect(notes.map((a) => a.ref)).toEqual(["q_notes.n_text", "q_notes.n_by"]);
    const labels = itemAttributes(childAttribute("E_Q", "q_labels", all()), all());
    expect(labels.map((a) => [a.ref, a.name])).toEqual([["q_labels", "labels"]]);
    const ship = itemAttributes(childAttribute("E_Q", "q_ship", all()), all());
    expect(ship.map((a) => a.ref)).toContain("q_ship.ad_geo.g_lat");
  });

  it("offers only the foreign keys that reference the owning table on its primary or a unique key", () => {
    expect(ownerKeyChoices(quoteNotes, "T_Q", quotes).map((k) => k.id)).toEqual(["FK_QN_Q"]);
    const nonKey: TableDoc = { ...quotes, columns: [...quotes.columns!, { id: "qc_no", name: "no", type: "string" }] };
    const toNonKey: TableDoc = { ...quoteNotes, foreignKeys: [{ id: "FK_NK", columns: ["nc_q"], referencesTable: "T_Q", referencesColumns: ["qc_no"] }] };
    expect(ownerKeyChoices(toNonKey, "T_Q", nonKey)).toEqual([]);
    expect(ownerKeyChoices(toNonKey, "T_Q", { ...nonKey, uniques: [{ columns: ["qc_no"] }] }).map((k) => k.id)).toEqual(["FK_NK"]);
  });

  it("starts an entry from a table: the owner key, the members by name and the position column", () => {
    const b = binding();
    const entry = newChildTable("C1", "q_notes");
    upsertChildTable(b, entry);
    const items = itemAttributes(childAttribute("E_Q", "q_notes", all()), all());
    pickChildTable(entry, quoteNotes, tableDocColumns(quoteNotes), ownerKeyChoices(quoteNotes, "T_Q", quotes), items, true, "notes");
    expect(entry).toMatchObject({ table: "T_QN", ownerForeignKey: "FK_QN_Q", orderColumn: "nc_pos" });
    expect(entry.fields).toEqual([
      { attribute: "q_notes.n_text", column: "nc_text" },
      { attribute: "q_notes.n_by", column: "nc_by" },
    ]);
    // The row's own generated id is the last column nothing maps: the column rows say so, and the owner and position are read only.
    const rows = childColumnRows(entry, items, tableDocColumns(quoteNotes), quoteNotes);
    expect(rows.map((r) => [r.column.name, r.status])).toEqual([
      ["quote_id", "owner"],
      ["position", "order"],
      ["text", "field"],
      ["author", "field"],
      ["note_id", "identity"],
    ]);
  });

  it("drops a binding field that mapped the attribute when the entry takes it over", () => {
    const b = binding({
      fields: [
        { attribute: "q_id", column: "qc_id" },
        { attribute: "q_ship.ad_line", column: "x" },
        { attribute: "q_ship", column: "y" },
      ],
    });
    upsertChildTable(b, newChildTable("C1", "q_ship"));
    expect(b.fields).toEqual([{ attribute: "q_id", column: "qc_id" }]);
    expect(b.childTables).toHaveLength(1);
  });

  it("changes the owner key and the order column, freeing what they used", () => {
    const columns = tableDocColumns(quoteNotes);
    const entry: ChildTable = {
      id: "C1",
      attribute: "q_notes",
      table: "T_QN",
      ownerForeignKey: "FK_QN_X",
      fields: [
        { attribute: "q_notes.n_text", column: "nc_text" },
        { attribute: "q_notes.n_by", column: "nc_q" },
      ],
    };
    setOwnerKey(entry, quoteNotes.foreignKeys![0], quoteNotes, columns);
    expect(entry.ownerForeignKey).toBe("FK_QN_Q");
    expect(entry.fields).toEqual([{ attribute: "q_notes.n_text", column: "nc_text" }]);
    setOrderColumn(entry, columns[2], columns);
    expect(entry.orderColumn).toBe("nc_text");
    expect(entry.fields).toBeUndefined();
    setOrderColumn(entry, null, columns);
    expect(entry.orderColumn).toBeUndefined();
  });

  it("checks an entry: MQ4138, MQ4139, MQ4140, MQ4141, MQ4047, MQ4058", () => {
    const d = all();
    const b = binding();
    const attribute = childAttribute("E_Q", "q_notes", d);
    const items = itemAttributes(attribute, d);
    const columns = tableDocColumns(quoteNotes);
    const names = { entity: "Quote", table: "quote_notes" };
    const check = (entry: ChildTable, binding: Binding = b) =>
      childProblems(entry, attribute, items, columns, quoteNotes, quotes, binding, true, names).map((p) => [p.rule, p.pointer]);
    const good: ChildTable = {
      id: "C1",
      attribute: "q_notes",
      table: "T_QN",
      ownerForeignKey: "FK_QN_Q",
      orderColumn: "nc_pos",
      fields: [
        { attribute: "q_notes.n_text", column: "nc_text" },
        { attribute: "q_notes.n_by", column: "nc_by" },
      ],
      columns: [{ column: "nc_note", status: "database" }],
    };
    expect(check(good)).toEqual([]);
    expect(check({ ...good, ownerForeignKey: "" }).map((p) => p[0])).toContain("MQ4138");
    expect(check({ ...good, ownerForeignKey: "FK_QN_X" }).map((p) => p[0])).toContain("MQ4138");
    expect(check(good, binding({ fields: [] }))).toEqual([["MQ4139", "/ownerForeignKey"]]);
    expect(check({ ...good, fields: [...good.fields!, { attribute: "q_notes.nope", column: "nc_by" }] })).toContainEqual(["MQ4140", "/fields/2/attribute"]);
    expect(check({ ...good, fields: [good.fields![0], { attribute: "q_notes.n_by", column: "nc_text" }] })).toContainEqual(["MQ4140", "/fields/1/column"]);
    expect(check({ ...good, orderColumn: "nc_text" })).toContainEqual(["MQ4141", "/orderColumn"]);
    expect(check({ ...good, fields: [good.fields![0]] })).toContainEqual(["MQ4047", ""]);
    expect(
      check({
        ...good,
        fields: [good.fields![0]],
        columns: [
          { column: "nc_by", status: "ignored" },
          { column: "nc_note", status: "database" },
        ],
      }),
    ).toEqual([["MQ4058", "/fields"]]);
  });
});

// ------------------------------------------------------------------ the mock's statements and findings

const database: Json = { kind: "database", id: DB, name: "main", dialect: "postgresql" };
const withTable = (t: TableDoc): Json => ({ kind: "table", database: DB, origin: "designed", ...t }) as unknown as Json;
const input = (d: Map<string, Json>) => ({ docs: d, conventions: {}, databaseConventions: {} });

describe("the mock: statements and findings for the new shapes", () => {
  const boundQuote = (): Json => ({
    ...quote,
    bindings: [
      {
        id: "B_Q",
        database: DB,
        source: "T_Q",
        fields: [{ attribute: "q_id", column: "qc_id" }],
        childTables: [
          {
            id: "C1",
            attribute: "q_notes",
            table: "T_QN",
            ownerForeignKey: "FK_QN_Q",
            orderColumn: "nc_pos",
            fields: [
              { attribute: "q_notes.n_text", column: "nc_text" },
              { attribute: "q_notes.n_by", column: "nc_by" },
            ],
            columns: [{ column: "nc_note", status: "database" }],
          },
        ],
      },
    ],
  });

  it("renders the probe, the tables above and each child table's statements", () => {
    const d = docs(
      database,
      withTable(payments),
      withTable(cards),
      payment,
      card(),
      withTable(quotes),
      withTable(quoteNotes),
      boundQuote(),
      geo,
      address,
      note,
    );
    const derived = bindingSql(input(d), "E_CARD", "B_CARD", null, "@")!;
    expect(derived.diagnostics).toEqual([]);
    expect(derived.preview!.exists!.sql).toContain("COUNT(*)");
    expect(derived.preview!.bases).toHaveLength(1);
    expect(derived.preview!.bases![0]).toMatchObject({ level: 1, entityId: "E_PAY", bindingId: "B_PAY" });
    expect(derived.preview!.bases![0].insert!.sql).toContain("INSERT INTO payments");
    const owner = bindingSql(input(d), "E_Q", "B_Q", null, "@")!;
    const [child] = owner.preview!.childTables!;
    expect(child.attributeName).toBe("notes");
    expect(child.select!.sql).toBe("SELECT t.text AS text, t.author AS author\nFROM quote_notes t\nWHERE quote_id = @ownerId\nORDER BY t.position");
    expect(child.insert!.sql).toBe("INSERT INTO quote_notes (quote_id, position, text, author)\nVALUES (@ownerId, @position, @text, @author)");
    expect(child.delete!.sql).toBe("DELETE FROM quote_notes\nWHERE quote_id = @ownerId");
  });

  it("reports an entry's findings at the entry, and none for a sound shape", () => {
    const d = docs(
      database,
      withTable(payments),
      withTable(cards),
      payment,
      card(),
      withTable(quotes),
      withTable(quoteNotes),
      boundQuote(),
      geo,
      address,
      note,
    );
    const view = resolveDatabase(input(d), DB)!;
    const sound = bindingFindings(view, d, () => null).filter((f) => (f.jsonPointer ?? "").includes("childTables") || /^MQ41(2\d|3\d|4\d)$/.test(f.rule));
    expect(sound).toEqual([]);
    const broken = boundQuote();
    (broken.bindings as Binding[])[0].childTables![0].orderColumn = "nc_text";
    const bad = docs(database, withTable(payments), withTable(cards), payment, card(), withTable(quotes), withTable(quoteNotes), broken, geo, address, note);
    const found = bindingFindings(resolveDatabase(input(bad), DB)!, bad, () => null).find((f) => f.rule === "MQ4141")!;
    expect(found.jsonPointer).toBe("/bindings/0/childTables/0/orderColumn");
  });
});
