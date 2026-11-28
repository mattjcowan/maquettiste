import { describe, expect, it } from "vitest";
import { applySchemaOperation, defaultSchemaId, occupantsOf, schemasOf, type SchemaOp } from "@/model/databaseSchemas";

type Json = Record<string, unknown>;
const db = (): Json => ({
  kind: "database",
  id: "D",
  name: "main",
  dialect: "postgresql",
  defaultSchema: "sales",
  schemas: [
    { id: "S1", name: "sales" },
    { id: "S2", name: "billing" },
  ],
  byConvention: "none",
});
const docs = (...extra: Json[]) => new Map<string, Json>([db(), ...extra].map((d) => [String(d.id), d]));
let n = 0;
const newId = () => `N${++n}`;
const run = (map: Map<string, Json>, op: SchemaOp) => applySchemaOperation(map, op, newId);

describe("database schemas (erratum E26)", () => {
  it("reads the declared default schema", () => {
    expect(defaultSchemaId(db())).toBe("S1");
  });

  it("adds, renames (the default follows) and sets the default", () => {
    const added = run(docs(), { op: "add-schema", id: "D", name: "audit" });
    expect("changed" in added && schemasOf(added.changed.get("D")).map((s) => s.name)).toEqual(["sales", "billing", "audit"]);
    expect(run(docs(), { op: "add-schema", id: "D", name: "Sales" })).toHaveProperty("error");
    const renamed = run(docs(), { op: "rename-schema", id: "D", schema: "S1", name: "orders" });
    expect("changed" in renamed && renamed.changed.get("D")?.defaultSchema).toBe("orders");
    const set = run(docs(), { op: "set-default-schema", id: "D", schema: "S2" });
    expect("changed" in set && set.changed.get("D")?.defaultSchema).toBe("billing");
  });

  it("refuses to remove a schema that holds things unless a target is named, and the default unless a new one is", () => {
    const table = { kind: "table", id: "T", name: "invoices", database: "D", schema: "S2" };
    expect(occupantsOf([table], db(), "S2").map((o) => o.label)).toEqual(["table invoices"]);
    const refused = run(docs(table), { op: "remove-schema", id: "D", schema: "S2" });
    expect("error" in refused && refused.error).toContain("table invoices");
    const moved = run(docs(table), { op: "remove-schema", id: "D", schema: "S2", target: "S1" });
    if (!("changed" in moved)) throw new Error("refused");
    expect(moved.changed.get("T")?.schema).toBe("S1");
    expect(run(docs(), { op: "remove-schema", id: "D", schema: "S1" })).toHaveProperty("error");
    const swapped = run(docs(), { op: "remove-schema", id: "D", schema: "S1", default: "S2" });
    expect("changed" in swapped && swapped.changed.get("D")?.defaultSchema).toBe("billing");
  });
});
