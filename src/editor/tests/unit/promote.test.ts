import { describe, expect, it } from "vitest";
import { planPromotion, type PromoteSource, type PromoteUser } from "@/explorer/promote";

const ids = () => {
  let n = 0;
  return () => `NEW${String(++n).padStart(23, "0")}`;
};

const address: PromoteSource = {
  kind: "value-object",
  id: "VO",
  name: "Address",
  package: "PKG",
  attributes: [{ id: "VO-street", name: "street", type: "string" }],
};

const customer: PromoteUser = {
  kind: "entity",
  id: "CUST",
  name: "Customer",
  package: "PKG",
  key: { attributes: ["CUST-id"] },
  attributes: [
    { id: "CUST-id", name: "id", type: "uuid", required: true },
    { id: "CUST-ship", name: "shipTo", type: { ref: "VO" }, required: true },
    { id: "CUST-name", name: "name", type: "string" },
  ],
};

describe("planPromotion", () => {
  it("turns an attribute use into a many-to-one relationship and removes the attribute", () => {
    const plan = planPromotion(address, [customer], ids());
    expect(plan.blocked).toEqual([]);
    expect(plan.entity).toMatchObject({ kind: "entity", name: "Address", package: "PKG", key: { strategy: "uuid-v7" } });
    expect(plan.relations).toHaveLength(1);
    expect(plan.relations[0]).toMatchObject({
      name: "CustomerShipTo",
      ends: [
        { entity: "CUST", max: "*" },
        { entity: plan.entity.id, role: "shipTo", min: 1, max: 1 },
      ],
    });
    expect(plan.updates.map((u) => u.id)).toEqual(["CUST"]);
    expect((plan.updates[0].json.attributes as { id: string }[]).map((a) => a.id)).toEqual(["CUST-id", "CUST-name"]);
    expect(plan.rewritten).toEqual([]);
  });

  it("drops the owners' mapping rows and seed columns that point at a removed attribute", () => {
    const mapping: PromoteUser = {
      kind: "mapping",
      id: "MAP",
      name: "Customer in main",
      entity: "CUST",
      attributes: [
        { attribute: "CUST-ship", prefix: "ship_" },
        { attribute: "CUST-name", column: "COL" },
      ] as never,
    };
    const seed: PromoteUser = {
      kind: "seed",
      id: "SEED",
      name: "Customer seed",
      columns: ["CUST-name", "CUST-ship"],
      rows: [
        { id: "R1", values: ["Ada", { street: "Main" }] },
        { id: "R2", values: [null, { street: "Side" }] },
      ],
    };
    const plan = planPromotion(address, [customer], ids(), [seed, mapping]);
    expect(plan.blocked).toEqual([]);
    const byId = new Map(plan.updates.map((u) => [u.id, u.json]));
    expect(byId.get("MAP")!.attributes).toEqual([{ attribute: "CUST-name", column: "COL" }]);
    expect(byId.get("SEED")!.columns).toEqual(["CUST-name"]);
    expect(byId.get("SEED")!.rows).toEqual([
      { id: "R1", values: ["Ada"] },
      { id: "R2", values: [] },
    ]);
    expect(plan.rewritten).toEqual([
      "Mapping Customer in main drops its row for Customer.shipTo (the relationship maps by convention)",
      "Seed Customer seed drops its column for Customer.shipTo",
    ]);
  });

  it("leaves dependents that point at no removed attribute alone", () => {
    const mapping: PromoteUser = { kind: "mapping", id: "MAP", name: "m", attributes: [{ attribute: "CUST-name" }] as never };
    const plan = planPromotion(address, [customer], ids(), [mapping]);
    expect(plan.updates.map((u) => u.id)).toEqual(["CUST"]);
  });

  it("blocks a query that reads a removed attribute, and leaves one that does not alone", () => {
    const reads: PromoteUser = {
      kind: "query",
      id: "Q1",
      name: "CustomersByShipTo",
      from: { source: "CUST@DB", alias: "c" },
      select: [{ attribute: "CUST-ship", expression: { column: "c.CUST-ship" } }],
    };
    const other: PromoteUser = {
      kind: "query",
      id: "Q2",
      name: "CustomerNames",
      from: { source: "CUST@DB", alias: "c" },
      select: [{ attribute: "CUST-name", expression: { column: "c.CUST-name" } }],
    };
    const plan = planPromotion(address, [customer], ids(), [reads, other]);
    expect(plan.blocked).toEqual(["CustomersByShipTo (a query that reads Customer.shipTo; change the query first)"]);
    expect(plan.updates.map((u) => u.id)).toEqual(["CUST"]);
  });

  it("blocks a seed whose only columns are removed attributes", () => {
    const seed: PromoteUser = { kind: "seed", id: "SEED", name: "Only", columns: ["CUST-ship"], rows: [] };
    expect(planPromotion(address, [customer], ids(), [seed]).blocked).toEqual(["Only (a seed whose only columns are the promoted attributes)"]);
  });

  it("puts the new entity in a diagram's place and blocks other referrers without an attribute", () => {
    const diagram: PromoteUser = {
      kind: "diagram",
      id: "DIA",
      name: "Overview",
      members: [{ element: "VO", x: 10 }, { element: "CUST" }],
    };
    const other: PromoteUser = { kind: "mapping", id: "MAP2", name: "Odd mapping" };
    const plan = planPromotion(address, [diagram, other], ids());
    const dia = plan.updates.find((u) => u.id === "DIA")!.json;
    expect(dia.members).toEqual([{ element: plan.entity.id, x: 10 }, { element: "CUST" }]);
    expect(plan.rewritten).toEqual(["Diagram Overview shows the new entity in place of Address"]);
    expect(plan.blocked).toEqual(["Odd mapping (a mapping that refers to Address; remove the reference first)"]);
  });

  it("blocks uses inside a key, a value object or a relationship", () => {
    const keyed: PromoteUser = { ...customer, key: { attributes: ["CUST-ship"] } };
    const vo: PromoteUser = {
      kind: "value-object",
      id: "VO2",
      name: "Parcel",
      attributes: [{ id: "P-a", name: "to", type: { ref: "VO" } }],
    };
    const plan = planPromotion(address, [keyed, vo], ids());
    expect(plan.blocked).toEqual(["Customer.shipTo (part of a key)", "Parcel.to (a value object cannot hold a relationship)"]);
    expect(plan.updates).toEqual([]);
  });

  it("makes a custom type one required value attribute of its base type", () => {
    const plan = planPromotion({ kind: "scalar-type", id: "ST", name: "Sku", base: "string", length: 20 }, [], ids());
    expect((plan.entity.attributes as { name: string }[]).map((a) => a.name)).toEqual(["id", "value"]);
    expect((plan.entity.attributes as unknown[])[1]).toMatchObject({ type: "string", length: 20, required: true });
  });
});
