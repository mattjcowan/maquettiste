// The explorer close-out (explorer-redesign.md §6 steps 5, 8, 10, 13, 14): Promote to entity, the mark menu items,
// membership roll-up dots, the category filter over every category tree, the pinned explorer's own selection,
// the editable Inheritance and Mappings tabs, and the scoped Database screen.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { MockBackend } from "@/mocks/backend";
import { planPromotion, type PromoteSource, type PromoteUser } from "@/explorer/promote";
import { applyMark, markLabel } from "@/explorer/marks";
import { menuFor } from "@/explorer/menus";
import { canvasCounts, childKeys, forestOf, nodeOf, type Forest, type TablesInput } from "@/explorer/tree";
import { mergeCategoryTrees } from "@/model/vocabularies";
import { inspectorContext } from "@/inspector/context";
import { baseChoices, setDiscriminator, setInheritance, setRelationShape, wouldCycle } from "@/editors/inheritance";
import { CANVAS_CAP, scopeTables } from "@/workspaces/database/tableList";

const counter = () => {
  let n = 0;
  return () => `id${++n}`;
};

describe("Promote to entity", () => {
  const address: PromoteSource = {
    kind: "value-object",
    id: "vo",
    name: "Address",
    package: "sales",
    tags: ["core"],
    attributes: [{ id: "a1", name: "street", type: "string" }],
  };
  const customer: PromoteUser = {
    kind: "entity",
    id: "cust",
    name: "Customer",
    package: "crm",
    key: { attributes: ["k"] },
    attributes: [
      { id: "k", name: "id", type: "uuid", required: true },
      { id: "u1", name: "billingAddress", type: { ref: "vo" }, required: true },
      { id: "u2", name: "shipTo", type: { ref: "vo" }, collection: true },
    ],
  };

  it("makes an entity with a key and the value object's attributes, and rewrites each use into a relationship", () => {
    const plan = planPromotion(address, [customer], counter());
    expect(plan.blocked).toEqual([]);
    expect(plan.entity).toMatchObject({
      kind: "entity",
      id: "id1",
      name: "Address",
      package: "sales",
      tags: ["core"],
      key: { attributes: ["id2"], strategy: "uuid-v7" },
    });
    expect((plan.entity.attributes as { name: string }[]).map((a) => a.name)).toEqual(["id", "street"]);
    expect(plan.updates).toHaveLength(1);
    expect((plan.updates[0].json.attributes as { id: string }[]).map((a) => a.id)).toEqual(["k"]);
    expect(plan.relations.map((r) => r.name)).toEqual(["CustomerBillingAddress", "CustomerShipTo"]);
    const [single, many] = plan.relations as { package: string; ends: { entity: string; navigation: string; min: number; max: number | string }[] }[];
    expect(single.package).toBe("crm");
    expect(single.ends[0]).toMatchObject({ entity: "cust", navigation: "", min: 0, max: "*" });
    expect(single.ends[1]).toMatchObject({ entity: "id1", navigation: "billingAddress", min: 1, max: 1 });
    expect(many.ends[0]).toMatchObject({ min: 1, max: 1 });
    expect(many.ends[1]).toMatchObject({ navigation: "shipTo", min: 0, max: "*" });
    // The source is not mutated.
    expect(customer.attributes).toHaveLength(3);
  });

  it("turns a custom type into one value attribute of its base type", () => {
    const plan = planPromotion({ kind: "scalar-type", id: "st", name: "Email", base: "string", length: 320 }, [], counter());
    expect(plan.entity.attributes).toEqual([
      { id: "id3", name: "id", type: "uuid", required: true },
      { id: "id2", name: "value", type: "string", length: 320, required: true },
    ]);
    expect(plan.relations).toEqual([]);
  });

  it("names the key after the entity when an attribute is already called id", () => {
    const plan = planPromotion({ ...address, attributes: [{ id: "a", name: "id", type: "string" }] }, [], counter());
    expect((plan.entity.attributes as { name: string }[])[0].name).toBe("addressId");
  });

  it("blocks a use in a value object or in a key", () => {
    const holder: PromoteUser = { kind: "value-object", id: "h", name: "Contact", attributes: [{ id: "x", name: "home", type: { ref: "vo" } }] };
    const keyed: PromoteUser = { ...customer, key: { attributes: ["u1"] } };
    expect(planPromotion(address, [holder, keyed], counter()).blocked).toEqual([
      "Contact.home (a value object cannot hold a relationship)",
      "Customer.billingAddress (part of a key)",
    ]);
  });

  it("is offered on value objects and custom types only", () => {
    const ids = (kind: string) => menuFor([{ type: "element", kind, element: true }]).map((i) => i.id);
    expect(ids("value-object")).toContain("promote");
    expect(ids("scalar-type")).toContain("promote");
    expect(ids("entity")).not.toContain("promote");
    expect(ids("enum")).not.toContain("promote");
  });
});

describe("Apply stereotype…, Tag… and Set category…", () => {
  it("adds a stereotype or a tag once, keeps the order, and sets or clears the category", () => {
    const json: Record<string, unknown> = { stereotypes: ["audited"], tags: ["core"] };
    applyMark(json, { kind: "stereotype", key: "audited" });
    applyMark(json, { kind: "stereotype", key: "cached" });
    applyMark(json, { kind: "tag", key: "pii" });
    applyMark(json, { kind: "tag", key: "core" });
    applyMark(json, { kind: "category", id: "c1" });
    expect(json).toEqual({ stereotypes: ["audited", "cached"], tags: ["core", "pii"], category: "c1" });
    applyMark(json, { kind: "category", id: null });
    expect(json.category).toBeUndefined();
    expect(markLabel({ kind: "tag", key: "pii" })).toBe("Tag pii");
    expect(markLabel({ kind: "category", id: "c1" }, "Finance")).toBe("Set category Finance");
  });

  it("are menu items on one row and on a multi-selection of one kind", () => {
    const entity = { type: "element" as const, kind: "entity", element: true };
    const multi = menuFor([entity, entity]).map((i) => i.id);
    for (const id of ["apply-stereotype", "tag", "set-category"] as const) {
      expect(menuFor([entity]).map((i) => i.id)).toContain(id);
      expect(multi).toContain(id);
    }
    expect(menuFor([{ type: "element", kind: "diagram", element: true }]).map((i) => i.id)).not.toContain("tag");
  });
});

describe("membership roll-up dots", () => {
  it("counts the active diagram's members under each ancestor row", () => {
    const backend = new MockBackend();
    const rows: ElementSummary[] = backend.model.index();
    const tables = new Map<string, TablesInput>();
    for (const db of rows.filter((r) => r.kind === "database")) tables.set(db.id, backend.generation.databaseTables(db.id)!);
    const forest: Forest = forestOf({ rows, tables });
    const byLabel = (key: string, label: string) => childKeys(forest, key).find((k) => nodeOf(forest, k)!.label === label)!;
    const billing = byLabel(forest.roots["domain-model"], "Billing");
    const entities = byLabel(billing, "Entities");
    childKeys(forest, entities);
    const members = new Set(rows.filter((r) => r.kind === "entity" && (r.name === "Invoice" || r.name === "Payment")).map((r) => r.id));
    const counts = canvasCounts(forest, members);
    expect(counts.get(entities)).toBe(2);
    expect(counts.get(billing)).toBe(2);
    expect(canvasCounts(forest, new Set(["not-there"])).size).toBe(0);
  });
});

describe("category filter over every category tree (§1.11)", () => {
  it("merges the global tree and the domains' trees, the global first", () => {
    const maps = mergeCategoryTrees([
      { kind: "category-tree", id: "g", name: "Global", categories: [{ id: "fin", name: "Finance" }] },
      undefined,
      {
        kind: "category-tree",
        id: "b",
        name: "Billing",
        package: "billing",
        categories: [
          { id: "ar", name: "Receivables", parent: "fin" },
          { id: "fin", name: "Duplicate" },
        ],
      },
    ] as never);
    expect(maps?.parents.get("ar")).toBe("fin");
    expect(maps?.names.get("fin")).toBe("Finance");
    expect(maps?.list.map((c) => c.id)).toEqual(["fin", "ar"]);
    expect(mergeCategoryTrees([undefined])).toBeUndefined();
  });
});

describe("the pinned second explorer's selection", () => {
  const base = {
    workspace: "entities" as const,
    editors: { tabs: [], active: null, preview: null, views: {} } as never,
    generation: { packTab: null, packFocus: null } as never,
    selectionBy: { "domain-model": ["a"], databases: ["t"] },
  };
  it("shows the pinned explorer's selection while it is pinned, else the active explorer's", () => {
    expect(inspectorContext({ ...base, explorer: { active: "domain-model", pinned: "databases" }, selectionFrom: "databases" })).toMatchObject({ ids: ["t"] });
    expect(inspectorContext({ ...base, explorer: { active: "domain-model", pinned: null }, selectionFrom: "databases" })).toMatchObject({ ids: ["a"] });
    expect(inspectorContext({ ...base, explorer: { active: "domain-model", pinned: "databases" }, selectionFrom: "domain-model" })).toMatchObject({
      ids: ["a"],
    });
  });
});

describe("the Inheritance and Mappings tabs, editable", () => {
  const entities = [
    { id: "animal", name: "Animal" },
    { id: "dog", name: "Dog", base: "animal" },
    { id: "puppy", name: "Puppy", base: "dog" },
    { id: "cat", name: "Cat", base: "animal" },
  ];
  const baseOf = (id: string) => entities.find((e) => e.id === id)?.base;
  it("refuses a base that derives from the entity", () => {
    expect(wouldCycle(baseOf, "animal", "puppy")).toBe(true);
    expect(wouldCycle(baseOf, "dog", "dog")).toBe(true);
    expect(wouldCycle(baseOf, "cat", "dog")).toBe(false);
    expect(baseChoices(entities, "dog").map((e) => e.id)).toEqual(["animal", "cat"]);
  });
  it("sets and clears the strategy, the discriminator and a relation's shape", () => {
    const m: Record<string, unknown> = {};
    setInheritance(m, "tpt");
    setDiscriminator(m, " D ");
    expect(m).toEqual({ inheritance: "tpt", discriminatorValue: "D" });
    setInheritance(m, null);
    setDiscriminator(m, "");
    expect(m).toEqual({});
    const r: Record<string, unknown> = {};
    setRelationShape(r, { shape: "junction", junctionTable: "tbl" });
    expect(r).toEqual({ shape: "junction", junctionTable: "tbl" });
    setRelationShape(r, { shape: "promoted", promotedName: " Link " });
    expect(r).toEqual({ shape: "promoted", promotedName: "Link" });
    setRelationShape(r, { shape: null });
    expect(r).toEqual({});
  });
});

describe("the Database screen above 300 tables", () => {
  const table = (key: string, refs: string[] = []) => ({ key, foreignKeys: refs.map((referencedTable) => ({ referencedTable })) });
  it("draws everything up to the cap, then the list form, then a table and its neighbours", () => {
    expect(scopeTables([table("a")], null)).toEqual({ mode: "all" });
    const many = Array.from({ length: CANVAS_CAP + 5 }, (_, i) => table(`t${String(i).padStart(3, "0")}`, i > 0 ? ["t000"] : []));
    expect(scopeTables(many, null)).toEqual({ mode: "list", total: CANVAS_CAP + 5 });
    const leaf = scopeTables(many, "t010");
    expect(leaf.mode === "scoped" && [...leaf.keys]).toEqual(["t010", "t000"]);
    const hub = scopeTables(many, "t000");
    expect(hub.mode === "scoped" && hub.keys.size).toBe(CANVAS_CAP);
    expect(hub.mode === "scoped" && hub.more).toBe(5);
  });
});
