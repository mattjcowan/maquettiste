// The Database screen's DDL preview target: an enabled pack with a unit rendered per database, never a pack by name.
import { describe, expect, it } from "vitest";
import { ddlPreviewCaption, ddlPreviewTarget } from "@/workspaces/database/ddlPreview";

const ddl = {
  name: "ddl",
  units: [
    { id: "table", for: "each table" },
    { id: "migration", for: "select databases" },
    { id: "schema", for: "select databases" },
    { id: "seed", for: "select databases" },
  ],
};
const classes = { name: "classes", units: [{ id: "entity", for: "each entity" }] };

describe("DDL preview target", () => {
  it("prefers a database unit named schema or table, and the same pack's each-table unit for a selected table", () => {
    expect(ddlPreviewTarget([classes, ddl], "db1", null)).toEqual({ pack: "ddl", unit: "schema", elementId: "db1", scope: "database" });
    expect(ddlPreviewTarget([classes, ddl], "db1", "t1@db1")).toEqual({ pack: "ddl", unit: "table", elementId: "t1@db1", scope: "table" });
    expect(ddlPreviewCaption({ pack: "ddl", unit: "table", elementId: "t1", scope: "table" }, "invoices")).toBe("ddl/table · invoices");
    expect(ddlPreviewCaption({ pack: "ddl", unit: "schema", elementId: "db1", scope: "database" }, null)).toBe("ddl/schema · whole database");
  });

  it("falls back to the first database unit, and to it for a table when the pack has no each-table unit", () => {
    const scripts = { name: "scripts", units: [{ id: "all", for: "each database" }] };
    expect(ddlPreviewTarget([scripts], "db1", "t1")).toEqual({ pack: "scripts", unit: "all", elementId: "db1", scope: "database" });
  });

  it("answers null with no database, or no enabled pack rendering per database", () => {
    expect(ddlPreviewTarget([ddl], null, null)).toBeNull();
    expect(ddlPreviewTarget([classes], "db1", null)).toBeNull();
    expect(ddlPreviewTarget([{ ...ddl, enabled: false }], "db1", null)).toBeNull();
  });
});
