// The mock's delete plan and cascade (src/mocks/model/cascade.ts) follow the engine's (engine-design.md 15.1):
// remove-references refuses a required reference with a readable MQ2001, delete-dependents deletes what cannot
// exist without the element and removes the parts that need it, all in one change, and a batch delete carries the
// resolution.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import type { MockModel } from "@/mocks/model/store";

const PRODUCT = "01J92P0V0JR8BE8253SKT29ZG7";
const REFERS_TO = "01J92P0V1CC98R0RRCAV73SGF4";
const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";
const MONEY = "01J92P0V0304M76KA84NE4D1TE";

const hash = (model: MockModel, id: string) => model.get(id)!.hash;

describe("mock delete plan", () => {
  it("plans an entity's delete both ways and deletes its relation with it", () => {
    const model = new MockBackend().model;
    const clear = model.deletePlan([PRODUCT], "remove-references");
    expect(clear.outcome).toBe("invalid");
    expect(clear.refused.map((r) => [r.name, r.pointer, r.rule])).toEqual([["refers to", "/ends/1/entity", "MQ2001"]]);

    const plan = model.deletePlan([PRODUCT], "delete-dependents");
    expect(plan.outcome).toBe("saved");
    expect(plan.deletes.map((d) => [d.id, d.because])).toEqual([[REFERS_TO, "needs entity Product"]]);
    expect(plan.removes.map((r) => r.what)).toEqual(["member Product"]);

    const refused = model.delete(PRODUCT, hash(model, PRODUCT), "remove-references");
    expect(refused.outcome).toBe("invalid");
    expect(refused.diagnostics[0].rule).toBe("MQ2001");
    expect(refused.diagnostics[0].message).toContain("delete-dependents");

    const deleted = model.delete(PRODUCT, hash(model, PRODUCT), "delete-dependents");
    expect(deleted.outcome).toBe("saved");
    expect(deleted.changes!.deleted.sort()).toEqual([PRODUCT, REFERS_TO].sort());
    expect(deleted.changes!.changed.map((c) => c.kind)).toEqual(["diagram"]);
  });

  it("deletes a database with its tables, views, sequences, routines, database types, SQL objects and mappings, and removes attributes a deleted type typed", () => {
    const model = new MockBackend().model;
    const plan = model.deletePlan([MAIN], "delete-dependents");
    expect(plan.deletes.map((d) => d.kind).sort()).toEqual([
      "database-type",
      "database-type",
      "mapping",
      "mapping",
      "routine",
      "routine",
      "sequence",
      "sql-object",
      "table",
      "view",
    ]);
    expect(model.delete(MAIN, hash(model, MAIN), "delete-dependents").outcome).toBe("saved");
    const physical = ["table", "view", "sequence", "routine", "database-type", "sql-object", "mapping"];
    expect([...model.entries.values()].filter((e) => physical.includes(String(e.json.kind)))).toEqual([]);

    const money = model.deletePlan([MONEY], "delete-dependents");
    expect(money.deletes).toEqual([]);
    expect(money.removes.map((r) => r.what)).toContain("attribute unitPrice");
  });

  it("takes the resolution on a batch delete and plans several elements at once", () => {
    const model = new MockBackend().model;
    const both = model.deletePlan([PRODUCT, REFERS_TO], "delete-dependents");
    expect(both.ids).toEqual([PRODUCT, REFERS_TO]);
    const result = model.batch({
      operations: [
        { op: "delete", id: PRODUCT, expectedHash: hash(model, PRODUCT), resolution: "delete-dependents" },
        { op: "delete", id: REFERS_TO, expectedHash: hash(model, REFERS_TO), resolution: "delete-dependents" },
      ],
    });
    expect(result.status).toBe(200);
    expect(model.get(PRODUCT)).toBeNull();
    expect(model.get(REFERS_TO)).toBeNull();
  });
});
