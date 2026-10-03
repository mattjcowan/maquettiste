// A table's parts on the database side (workspaces/database/tableParts.ts), storing laid-out tables as table files (the mock's
// materialize-tables and status, mocks/model/materialize.ts), and the store's part pick, favorites and recents.
import { describe, expect, it } from "vitest";
import type { ColumnView, ModelJson, SaveResult, TableView } from "@/api/types";
import {
  addPart,
  batchUndoEntry,
  columnIdMap,
  existingIds,
  findPart,
  incomingForeignKeys,
  isLaidOutKey,
  moveIndexColumn,
  partCrumb,
  partIdAt,
  partName,
  removePart,
  renamePart,
  replayOnFile,
  resolvedTableDoc,
  setIndexColumnOrder,
  storableTables,
  withColumnIds,
} from "@/workspaces/database/tableParts";
import { MockBackend } from "@/mocks/backend";
import { schemaValidator } from "@/mocks/contract";
import { createEditorStore } from "@/state/store";

type Json = Record<string, unknown>;
const DB = "01J92P0V1QRN2181XM2ZWE02W4";
const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";

const column = (over: Partial<ColumnView>): ColumnView =>
  ({
    key: "k",
    name: "n",
    type: "string",
    nativeType: "varchar(255)",
    length: 255,
    precision: null,
    scale: null,
    nullable: true,
    defaultSql: null,
    identity: false,
    computed: null,
    attributeId: null,
    attributePath: null,
    isPrimaryKey: false,
    isForeignKey: false,
    isDiscriminator: false,
    position: 1,
    default: null,
    comment: null,
    description: null,
    stereotypes: [],
    tags: [],
    properties: {},
    ...over,
  }) as ColumnView;

const table = (over: Partial<TableView> = {}): TableView =>
  ({
    key: `E1@${DB}`,
    name: "customers",
    schema: "billing",
    origin: "synthesized",
    entityId: "E1",
    relationId: null,
    isJunction: false,
    isLookup: false,
    comment: null,
    columns: [column({ key: "a.id", name: "id", type: "uuid", nullable: false, isPrimaryKey: true }), column({ key: "a.email", name: "email" })],
    primaryKey: { name: "pk_customers", columns: ["a.id"] },
    uniques: [{ name: "uq_customers_email", columns: ["a.email"] }],
    foreignKeys: [],
    indexes: [{ name: "ix_customers_email", columns: [{ column: "a.email", descending: false }], unique: false, where: null }],
    displayName: null,
    pluralName: null,
    description: null,
    stereotypes: [],
    tags: [],
    category: null,
    properties: {},
    generation: {},
    ...over,
  }) as TableView;

let n = 0;
const newId = () => `N${++n}`;

describe("a laid-out table as a document, and an edit replayed on its stored file", () => {
  it("shows the resolved table in the table document's shape, its columns by key and its constraints by name", () => {
    const doc = resolvedTableDoc(table(), DB);
    expect(isLaidOutKey(String(doc.id))).toBe(true);
    expect(doc.columns).toEqual([
      { id: "a.id", name: "id", type: "uuid", length: 255, nullable: false },
      { id: "a.email", name: "email", type: "string", length: 255 },
    ]);
    expect(doc.primaryKey).toEqual({ name: "pk_customers", columns: ["a.id"] });
    expect(doc.uniques).toEqual([{ id: "uq_customers_email", name: "uq_customers_email", columns: ["a.email"] }]);
    expect(doc.indexes).toEqual([{ id: "ix_customers_email", name: "ix_customers_email", columns: [{ column: "a.email" }] }]);
    expect(doc).not.toHaveProperty("foreignKeys");
  });

  it("renames column ids and every reference to them, both ways, by column name", () => {
    const file: Json = {
      kind: "table",
      id: "T",
      columns: [
        { id: "C1", name: "id" },
        { id: "C2", name: "email" },
      ],
      primaryKey: { columns: ["C1"] },
      uniques: [{ id: "U", columns: ["C2"] }],
      indexes: [{ id: "I", columns: [{ column: "C2" }], include: ["C1"] }],
      foreignKeys: [{ id: "F", columns: ["C2"], referencesTable: "T", referencesColumns: ["C1"] }],
    };
    const map = columnIdMap(table().columns, file);
    expect([...map]).toEqual([
      ["a.id", "C1"],
      ["a.email", "C2"],
    ]);
    const back = withColumnIds(structuredClone(file), new Map([...map].map(([a, b]) => [b, a])));
    expect(back.primaryKey).toEqual({ columns: ["a.id"] });
    expect((back.indexes as Json[])[0]).toEqual({ id: "I", columns: [{ column: "a.email" }], include: ["a.id"] });
    expect((back.foreignKeys as Json[])[0]).toMatchObject({ columns: ["a.email"], referencesColumns: ["a.id"] });
    // An edit written against the resolved document (old keys) lands on the file's own ids; new columns keep theirs.
    const edited = replayOnFile(file, table().columns, (doc) => {
      (doc.uniques as Json[])[0].columns = ["a.id", "a.email"];
      addPart(doc, "column", () => "NEW");
    });
    expect((edited!.uniques as Json[])[0].columns).toEqual(["C1", "C2"]);
    expect((edited!.columns as Json[]).map((c) => c.id)).toEqual(["C1", "C2", "NEW"]);
  });
});

describe("a table's parts", () => {
  const designed = (): Json => ({
    kind: "table",
    id: "T",
    name: "orders",
    columns: [
      { id: "C1", name: "id", type: "uuid" },
      { id: "C2", name: "code", type: "string" },
    ],
  });

  it("adds each part valid as added, named so it can be picked at once", () => {
    const doc = designed();
    expect(addPart(doc, "primary-key", newId)).toEqual({ kind: "primary-key", id: "primary-key" });
    expect(addPart(doc, "primary-key", newId)).toBeNull();
    expect(addPart(doc, "unique", newId)).toEqual({ kind: "unique", id: "uq_orders_id" });
    expect(addPart(doc, "unique", newId)).toEqual({ kind: "unique", id: "uq_orders_id_2" });
    expect(addPart(doc, "index", newId)).toEqual({ kind: "index", id: "ix_orders_id" });
    expect(addPart(doc, "foreign-key", newId)).toBeNull();
    expect(addPart(doc, "foreign-key", newId, { referencesTable: "T2" })).toEqual({ kind: "foreign-key", id: "fk_orders_id" });
    expect(addPart(doc, "check", newId, { dialect: "postgresql" })).toEqual({ kind: "check", id: "ck_orders_id" });
    expect(doc.primaryKey).toEqual({ name: "pk_orders", columns: ["C1"] });
    expect((doc.checks as Json[])[0]).toMatchObject({ name: "ck_orders_id", expression: { postgresql: "id IS NOT NULL" } });
    expect((doc.foreignKeys as Json[])[0]).toMatchObject({ columns: ["C1"], referencesTable: "T2" });
    expect(addPart({ kind: "table", name: "empty" }, "unique", newId)).toBeNull();
  });

  it("finds, renames and removes a part by its own name, its resolved name or its id", () => {
    const doc = designed();
    doc.uniques = [{ id: "U1", columns: ["C2"] }];
    const view = table({ key: "T", uniques: [{ name: "uq_orders_code", columns: ["C2"] }] });
    expect(partIdAt(doc, view, "unique", 0)).toBe("uq_orders_code");
    expect(findPart(doc, view, "unique", "uq_orders_code")).toBe(0);
    expect(findPart(doc, view, "unique", "U1")).toBe(0);
    expect(renamePart(doc, { kind: "unique", id: "uq_orders_code" }, "  uq_code ", view)).toEqual({ kind: "unique", id: "uq_code" });
    expect(renamePart(doc, { kind: "unique", id: "uq_code" }, " ", view)).toBeNull();
    expect(partName(doc, { kind: "unique", id: "uq_code" }, view)).toBe("uq_code");
    expect(renamePart(doc, { kind: "column", id: "C2" }, "reference")).toEqual({ kind: "column", id: "C2" });
    expect(partName(doc, { kind: "column", id: "C2" })).toBe("reference");
    expect(removePart(doc, { kind: "unique", id: "uq_code" }, view)).toBe(true);
    expect(doc).not.toHaveProperty("uniques");
    expect(removePart(doc, { kind: "unique", id: "uq_code" }, view)).toBe(false);
    expect(removePart(doc, { kind: "column", id: "C2" })).toBe(true);
    expect((doc.columns as Json[]).map((c) => c.id)).toEqual(["C1"]);
  });

  it("orders an index's columns and sets each one's direction", () => {
    const entry: Json = { columns: [{ column: "C1" }, { column: "C2" }] };
    setIndexColumnOrder(entry, "C2", true);
    expect(entry.columns).toEqual([{ column: "C1" }, { column: "C2", descending: true }]);
    expect(moveIndexColumn(entry, "C2", -1)).toBe(true);
    expect(entry.columns).toEqual([{ column: "C2", descending: true }, { column: "C1" }]);
    expect(moveIndexColumn(entry, "C2", -1)).toBe(false);
    setIndexColumnOrder(entry, "C2", false);
    expect(entry.columns).toEqual([{ column: "C2" }, { column: "C1" }]);
  });

  it("lists the foreign keys pointing at a table, and says the breadcrumb", () => {
    const target = table({ key: "T1", name: "customers" });
    const orders = table({
      key: "T2",
      name: "orders",
      columns: [column({ key: "c", name: "customer_id" })],
      foreignKeys: [
        {
          name: "fk_orders_customer",
          columns: ["c"],
          referencedTable: "T1",
          referencedColumns: [],
          onDelete: "no action",
          onUpdate: "no action",
          relationId: null,
          endId: null,
        },
      ],
    });
    expect(incomingForeignKeys([target, orders], "T1")).toEqual([{ table: orders, name: "fk_orders_customer", columns: ["customer_id"] }]);
    expect(partCrumb(target, "email")).toBe("billing.customers › email");
    expect(partCrumb({ schema: null, name: "x" }, null)).toBe("x");
  });
});

describe("which tables can be stored as table files", () => {
  it("names a laid-out table of one element that the status lists, outside any hierarchy; never a child or a junction table", () => {
    const tables = [
      { key: `E1@${DB}`, origin: "synthesized" as const, entityId: "E1", isJunction: false },
      { key: `E1.attr@${DB}`, origin: "synthesized" as const, entityId: "E1", isJunction: false },
      { key: `R@${DB}`, origin: "synthesized" as const, entityId: null, isJunction: true },
      { key: `E2@${DB}`, origin: "synthesized" as const, entityId: "E2", isJunction: false },
      { key: `E3@${DB}`, origin: "synthesized" as const, entityId: "E3", isJunction: false },
      { key: "T", origin: "designed" as const, entityId: null, isJunction: false },
    ];
    const status = [
      { id: "E1", projected: true },
      { id: "E2", projected: true },
      { id: "E3", projected: false },
    ];
    expect([...storableTables(tables, DB, status, [{ id: "E1", base: undefined }])]).toEqual([
      [`E1@${DB}`, "E1"],
      [`E2@${DB}`, "E2"],
    ]);
    expect([...storableTables(tables, DB, status, [{ id: "E9", base: "E2" }])]).toEqual([[`E1@${DB}`, "E1"]]);
    expect(storableTables(tables, DB, undefined, []).size).toBe(0);
  });

  it("builds the undo step of a stored batch: a create undoes to nothing, a delete to its document", () => {
    const item = (id: string, json: Json | null): SaveResult =>
      ({
        outcome: "saved",
        id,
        hash: json ? `h-${id}` : null,
        current: json ? { json } : null,
        diagnostics: [],
        referrers: [],
        changes: null,
      }) as unknown as SaveResult;
    const priors = new Map<string, ModelJson>([
      ["E", { kind: "entity", id: "E" } as unknown as ModelJson],
      ["M", { kind: "mapping", id: "M" } as unknown as ModelJson],
    ]);
    const entry = batchUndoEntry("Store", priors, [
      item("T", { kind: "table", id: "T" }),
      item("E", { kind: "entity", id: "E", bindings: [] }),
      item("M", null),
    ]);
    // The created table last: undoing deletes it after the entity no longer refers to it.
    expect(entry.ids).toEqual(["E", "M", "T"]);
    expect(entry.before.map((b) => (b ? (b as unknown as Json).kind : null))).toEqual(["entity", "mapping", null]);
    expect(entry.after.map((a) => (a ? (a as unknown as Json).kind : null))).toEqual(["entity", null, "table"]);
    expect(entry.afterHashes).toEqual(["h-E", null, "h-T"]);
    expect(existingIds(["a", "b"], (x) => x === "a")).toEqual(["a"]);
    const same = ["a"];
    expect(existingIds(same, () => true)).toBe(same);
  });
});

describe("the mock stores laid-out tables as table files (materialize-tables)", () => {
  it("lists what can be stored, previews it and stores it in one batch; each file holds every column", () => {
    const backend = new MockBackend();
    const status = backend.model.materializeStatus(DB)!;
    expect(schemaValidator("MaterializeStatus")(status)).toBe(true);
    expect(status.entities.find((e) => e.id === CUSTOMER)).toMatchObject({ projected: true, table: "customers" });
    const plan = backend.model.previewMaterialize(DB, { op: "materialize-tables", entities: [CUSTOMER, INVOICE] });
    expect(schemaValidator("MaterializePlan")(plan)).toBe(true);
    expect(plan.valid).toBe(true);
    // The invoices table's adjusting file becomes its table file (same id); customers gets a new one; the mapping goes; the
    // relationship whose foreign key is now in a table file gets a mapping naming that key (the engine's plan, one for both flows).
    expect(plan.creates.filter((c) => c.kind === "table").map((c) => c.name)).toEqual(["customers"]);
    const relationMapping = plan.creates.find((c) => c.kind === "mapping");
    expect(relationMapping?.element?.foreignKey).toBeTruthy();
    expect(plan.creates.map((c) => c.kind).sort()).toEqual(["mapping", "table"]);
    expect(
      plan.updates
        .filter((c) => c.kind !== "query")
        .map((c) => c.kind)
        .sort(),
    ).toEqual(["entity", "entity", "table"]);
    // The queries over the stored tables read them by their files.
    expect(plan.updates.some((c) => c.kind === "query")).toBe(true);
    expect(plan.deletes.map((c) => c.kind)).toEqual(["mapping"]);
    // Every storable table at once (as Store as table files does): the foreign keys between them are written in the files.
    const all = status.entities.filter((e) => e.projected).map((e) => e.id);
    const { status: code, body } = backend.model.batch({ operations: [{ op: "materialize-tables", database: DB, entities: all }] });
    expect(code).toBe(200);
    expect("outcome" in body && body.outcome).toBe("saved");
    const view = backend.generation.databaseView(DB)!.view!;
    const customers = view.tables.find((t) => t.name === "customers")!;
    expect(customers.origin).toBe("designed");
    expect(isLaidOutKey(customers.key)).toBe(false);
    const file = backend.model.docs().get(customers.key)!;
    expect((file.columns as Json[]).length).toBe(customers.columns.length);
    expect((file.columns as Json[]).length).toBeGreaterThan(1);
    expect(view.tables.find((t) => t.name === "invoices")?.origin).toBe("designed");
    // The one materialize serves the database side's Store as table files as it does the entity side's Create tables: the
    // model stays valid (the many-to-many keeps the junction table the database lays out for it).
    expect(backend.model.validate().diagnostics.filter((d) => d.severity === "error")).toEqual([]);
    // Stored once: the next store of the same table is refused, and the status no longer lists it as laid out.
    expect(backend.model.materializeStatus(DB)!.entities.some((e) => e.id === CUSTOMER)).toBe(false);
    const again = backend.model.previewMaterialize(DB, { op: "materialize-tables", entities: [CUSTOMER] });
    expect(again.valid).toBe(false);
    expect(again.diagnostics[0].rule).toBe("MQ4055");
  });
});

describe("the store's table part, favorites and recents", () => {
  it("shows one subject at a time: a column or a part, back to the table", () => {
    const store = createEditorStore();
    store.getState().inspectTable({ database: DB, key: "T", column: "C1" });
    expect(store.getState().inspectedTable).toMatchObject({ column: "C1", part: null });
    store.getState().inspectPart({ kind: "index", id: "ix" });
    expect(store.getState().inspectedTable).toMatchObject({ column: null, part: { kind: "index", id: "ix" } });
    store.getState().inspectPart({ kind: "column", id: "C2" });
    expect(store.getState().inspectedTable).toMatchObject({ column: "C2", part: null });
    store.getState().inspectTable({ database: DB, key: "T", part: { kind: "check", id: "ck" } });
    expect(store.getState().inspectedTable).toMatchObject({ column: null, part: { kind: "check", id: "ck" } });
    store.getState().inspectPart(null);
    expect(store.getState().inspectedTable).toMatchObject({ key: "T", column: null, part: null });
  });

  it("clears favorites and recents, all or some, and drops deleted recents", () => {
    const store = createEditorStore();
    for (const id of ["A", "B", "C"]) {
      store.getState().toggleFavorite(id);
      store.getState().noteRecent(id);
    }
    store.getState().clearFavorites(["A"]);
    expect(store.getState().explorer.favorites).toEqual(["B", "C"]);
    store.getState().clearFavorites();
    expect(store.getState().explorer.favorites).toEqual([]);
    store.getState().pruneRecent((id) => id !== "B");
    expect(store.getState().recent).toEqual(["C", "A"]);
    store.getState().clearRecent(["C"]);
    expect(store.getState().recent).toEqual(["A"]);
    store.getState().clearRecent();
    expect(store.getState().recent).toEqual([]);
  });
});
