import { describe, expect, it } from "vitest";
import { conventionOf, setConvention } from "@/model/databaseMapping";
import { stopConvention } from "@/workspaces/settings/DatabaseConvention";
import { applySchemaOperation, defaultSchemaId, occupantsOf, schemaForEntity, schemasOf, type SchemaOp } from "@/model/databaseSchemas";

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
  byConvention: "packages",
  packages: ["P1", { package: "P2", schema: "S2" }],
});
const docs = (...extra: Json[]) => new Map<string, Json>([db(), ...extra].map((d) => [String(d.id), d]));
let n = 0;
const newId = () => `N${++n}`;
const run = (map: Map<string, Json>, op: SchemaOp) => applySchemaOperation(map, op, newId);

describe("database schemas (erratum E26)", () => {
  it("reads convention entries that are ids or { package, schema }", () => {
    const c = conventionOf(db() as never);
    expect(c.packages).toEqual(["P1", "P2"]);
    expect(c.schemas).toEqual({ P2: "S2" });
  });

  it("writes an entry without a schema as the package id and keeps the schemas it had", () => {
    const json = db();
    setConvention(json, "packages", ["P3", "P2", "P1"]);
    expect(json.packages).toEqual(["P1", { package: "P2", schema: "S2" }, "P3"]);
    setConvention(json, "packages", ["P1", "P2"], { P1: "S2" });
    expect(json.packages).toEqual([{ package: "P1", schema: "S2" }, "P2"]);
    // Stopping one domain keeps the others with their schemas; stopping the last leaves none.
    const stopped = db();
    stopConvention(stopped, "P1");
    expect(stopped).toMatchObject({ byConvention: "packages", packages: [{ package: "P2", schema: "S2" }] });
    stopConvention(stopped, "P2");
    expect(stopped.byConvention).toBe("none");
    expect(stopped).not.toHaveProperty("packages");
  });

  it("places a table: the mapping, else the nearest convention entry up the package tree, else the default", () => {
    const parents: Record<string, string> = { P2child: "P2" };
    const parentOf = (id: string) => parents[id] ?? null;
    expect(schemaForEntity(db(), "P2child", null, parentOf)).toBe("S2");
    expect(schemaForEntity(db(), "P2child", { schema: "S1" }, parentOf)).toBe("S1");
    expect(schemaForEntity(db(), "P1", null, parentOf)).toBeNull();
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
    expect(occupantsOf([table], db(), "S2").map((o) => o.label)).toEqual(["table invoices", "convention entry P2"]);
    const refused = run(docs(table), { op: "remove-schema", id: "D", schema: "S2" });
    expect("error" in refused && refused.error).toContain("table invoices");
    const moved = run(docs(table), { op: "remove-schema", id: "D", schema: "S2", target: "S1" });
    if (!("changed" in moved)) throw new Error("refused");
    expect(moved.changed.get("T")?.schema).toBe("S1");
    expect(moved.changed.get("D")?.packages).toEqual(["P1", { package: "P2", schema: "S1" }]);
    expect(run(docs(), { op: "remove-schema", id: "D", schema: "S1" })).toHaveProperty("error");
    const swapped = run(docs(), { op: "remove-schema", id: "D", schema: "S1", default: "S2" });
    expect("changed" in swapped && swapped.changed.get("D")?.defaultSchema).toBe("billing");
  });
});
