// Table detail in the Databases explorer (explorer-redesign.md 1.3, EX step 13): a table's children from its detail
// (E5f), column-level "mapped by" into the Domain model, the database above a row, and the Database screen's filtered
// table list.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import type { TableSummary } from "@/api/types";
import { buildForest, childKeys, databaseOf, nodeOf, relatedKeys, tableChildren, tableOf, type TablesInput } from "@/explorer/tree";
import { filterTables } from "@/workspaces/database/tableList";

function billing() {
  const backend = new MockBackend();
  const rows = backend.model.index();
  const tables = new Map<string, TablesInput>();
  for (const db of rows.filter((r) => r.kind === "database")) tables.set(db.id, backend.generation.databaseTables(db.id)!);
  const forest = buildForest({ rows, tables });
  const invoice = rows.find((r) => r.kind === "entity" && r.name === "Invoice")!;
  const tableKey = forest.related.tablesOf.get(invoice.id)!.find((k) => !nodeOf(forest, k)!.table?.isJunction)!;
  return { backend, rows, forest, invoice, tableKey };
}

describe("table children", () => {
  it("adds Columns, the primary key, foreign keys and indexes from the table's detail", () => {
    const { backend, forest, tableKey } = billing();
    const node = nodeOf(forest, tableKey)!;
    expect(node.load).toBe("table");
    const database = databaseOf(forest, tableKey)!;
    expect(backend.model.index().find((r) => r.id === database)?.kind).toBe("database");
    const view = backend.generation.databaseTable(database, node.table!.key)!.table!;
    const folders = tableChildren(forest, tableKey, view);
    const labels = folders.map((k) => nodeOf(forest, k)!.label);
    expect(labels[0]).toBe("Columns");
    expect(labels).toContain("Primary key");
    const columns = childKeys(forest, folders[0]).map((k) => nodeOf(forest, k)!);
    expect(columns.map((c) => c.label)).toEqual(view.columns.map((c) => c.name));
    expect(columns.every((c) => tableOf(forest, c.key) === tableKey)).toBe(true);
    // The primary key column says so beside its type.
    const pk = view.columns.find((c) => c.isPrimaryKey)!;
    expect(columns.find((c) => c.label === pk.name)!.secondary).toContain("PK");
  });

  it("highlights the entity and the attribute mapped onto a column, and the entity for a table", () => {
    const { backend, forest, invoice, tableKey } = billing();
    const database = databaseOf(forest, tableKey)!;
    const view = backend.generation.databaseTable(database, nodeOf(forest, tableKey)!.table!.key)!.table!;
    const folders = tableChildren(forest, tableKey, view);
    const mapped = view.columns.find((c) => c.attributeId)!;
    const column = childKeys(forest, folders[0])
      .map((k) => nodeOf(forest, k)!)
      .find((c) => c.label === mapped.name)!;
    expect(column.attribute).toBe(mapped.attributeId);
    const entityKey = forest.place.get(invoice.id)!;
    const related = relatedKeys(forest, column.key);
    expect(related.has(entityKey)).toBe(true);
    expect(related.has(`${entityKey}/attributes/${mapped.attributeId}`)).toBe(true);
    expect(relatedKeys(forest, tableKey).has(entityKey)).toBe(true);
    // A column with no attribute (a key or discriminator column) highlights only the entity.
    const unmapped = view.columns.find((c) => !c.attributeId);
    if (unmapped) {
      const row = childKeys(forest, folders[0])
        .map((k) => nodeOf(forest, k)!)
        .find((c) => c.label === unmapped.name)!;
      expect([...relatedKeys(forest, row.key)]).toEqual([entityKey]);
    }
  });
});

describe("the Database screen's table list", () => {
  const t = (name: string, schema: string | null = "public"): TableSummary => ({
    key: name,
    name,
    schema,
    origin: "synthesized",
    entityId: null,
    relationId: null,
    isJunction: false,
    isLookup: false,
    columnCount: 3,
  });
  it("filters by every word, sorts by name and caps the list", () => {
    const tables = [t("invoices"), t("invoice_lines"), t("customers"), t("audit_log", "audit")];
    expect(filterTables(tables, "").tables.map((x) => x.name)).toEqual(["audit_log", "customers", "invoice_lines", "invoices"]);
    expect(filterTables(tables, "INVOICE").tables.map((x) => x.name)).toEqual(["invoice_lines", "invoices"]);
    expect(filterTables(tables, "audit. log").tables.map((x) => x.name)).toEqual(["audit_log"]);
    const capped = filterTables(tables, "", 2);
    expect(capped).toMatchObject({ total: 4, more: 2 });
    expect(capped.tables).toHaveLength(2);
  });
});
