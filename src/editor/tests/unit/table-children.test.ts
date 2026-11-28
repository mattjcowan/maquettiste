// Table detail in the Databases explorer (explorer-redesign.md 1.3, EX step 13): a table's children from its detail
// (E5f), column-level "mapped by" into the Domain model, the database above a row, and the Database screen's filtered
// table list.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import type { TableSummary } from "@/api/types";
import { buildForest, childKeys, databaseOf, nodeOf, readAheadIdOf, tableChildren, tableOf, type TablesInput } from "@/explorer/tree";
import { filterTables } from "@/workspaces/database/tableList";
import { tableKeyOf } from "@/search/engine";

function billing() {
  const backend = new MockBackend();
  const rows = backend.model.index();
  const tables = new Map<string, TablesInput>();
  for (const db of rows.filter((r) => r.kind === "database")) tables.set(db.id, backend.generation.databaseTables(db.id)!);
  const forest = buildForest({ rows, tables });
  const database = rows.find((r) => r.kind === "database")!;
  const invoices = backend.generation.databaseTables(database.id)!.tables.find((t) => t.name === "invoices")!;
  const tableKey = tableKeyOf(database.id, invoices.key);
  return { backend, rows, forest, tableKey };
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

  it("reads ahead the table's own row, never a part under it (a part's id is a name, not a document)", () => {
    const { backend, forest, tableKey } = billing();
    const node = nodeOf(forest, tableKey)!;
    const view = backend.generation.databaseTable(databaseOf(forest, tableKey)!, node.table!.key)!.table!;
    const folders = tableChildren(forest, tableKey, view);
    const parts = folders.flatMap((f) => [f, ...childKeys(forest, f)]).map((k) => nodeOf(forest, k)!);
    // Keys and indexes are named rows: their id is the constraint's name.
    const names = [...view.uniques, ...view.indexes].map((k) => k.name);
    expect(parts.some((p) => p.type === "item" && !!p.id && names.includes(p.id))).toBe(true);
    expect(parts.map(readAheadIdOf).filter(Boolean)).toEqual([]);
    expect(readAheadIdOf(node)).toBe(node.id);
  });
});

describe("the Database screen's table list", () => {
  const t = (name: string, schema: string | null = "public"): TableSummary => ({
    key: name,
    name,
    schema,
    relationId: null,
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
