// Explicit mapping (engine-design.md D46, explorer-redesign.md 1.3): a database holds what its byConvention takes plus
// the entities a mapping names; a file without the member keeps "every entity" (or its packages'). The New database
// dialog's document, the Map to database… menu items and the mock server's database view.
import { describe, expect, it } from "vitest";
import * as endpoints from "@/api/endpoints";
import type { ModelJson } from "@/api/types";
import { buildElement } from "@/explorer/create";
import { menuFor } from "@/explorer/menus";
import { conventionLabel, conventionOf, mapDomains, newDatabaseConvention, placesEntity, setConvention, takesPackage } from "@/model/databaseMapping";
import { newId } from "@/lib/ids";
import { useMockApi } from "./harness";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";

describe("database convention", () => {
  it("reads a file without the member as before: all domains, or its packages", () => {
    expect(conventionOf({})).toEqual({ mode: "all", explicit: false, packages: [] });
    expect(conventionOf({ packages: ["a"] })).toEqual({ mode: "packages", explicit: false, packages: ["a"] });
    expect(conventionOf({ byConvention: "none", packages: ["a"] })).toEqual({ mode: "none", explicit: true, packages: [] });
    expect(conventionLabel(conventionOf({}), (x) => x)).toBe("By convention: all domains (unspecified)");
    expect(conventionLabel(conventionOf({ byConvention: "packages" }), (x) => x)).toBe("By convention: nothing");
  });

  it("writes the member on every new database, none by default", () => {
    expect(newDatabaseConvention("none", ["a"])).toEqual({ byConvention: "none" });
    expect(newDatabaseConvention("pick", ["b", "a", "b"])).toEqual({ byConvention: "packages", packages: ["a", "b"] });
    expect(newDatabaseConvention("pick", [])).toEqual({ byConvention: "none" });
    expect(newDatabaseConvention("all", ["a"])).toEqual({ byConvention: "all" });
    const ids = () => "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    expect(buildElement("database", { name: "reporting", domain: null, convention: "pick", packages: ["d1"] }, ids)).toMatchObject({
      byConvention: "packages",
      packages: ["d1"],
    });
    expect(buildElement("database", { name: "reporting", domain: null }, ids)).toMatchObject({ byConvention: "none" });
  });

  it("maps domains into the list, makes an unspecified all explicit, and leaves an explicit all alone", () => {
    const none: Record<string, unknown> = { byConvention: "none" };
    expect(mapDomains(none, ["b", "a"])).toBe(true);
    expect(none).toMatchObject({ byConvention: "packages", packages: ["a", "b"] });
    expect(mapDomains(none, ["a"])).toBe(false);
    const legacy: Record<string, unknown> = {};
    expect(mapDomains(legacy, ["a"])).toBe(true);
    expect(legacy).toEqual({ byConvention: "all" });
    expect(mapDomains({ byConvention: "all" }, ["a"])).toBe(false);
    const json: Record<string, unknown> = { byConvention: "packages", packages: ["a"] };
    setConvention(json, "none");
    expect(json).toEqual({ byConvention: "none" });
  });

  it("places an entity by its domain chain or by a mapping, and ignore removes it", () => {
    const parents: Record<string, string> = { orders: "sales" };
    const parentOf = (id: string) => parents[id];
    const sales = conventionOf({ byConvention: "packages", packages: ["sales"] });
    expect(takesPackage(sales, "orders", parentOf)).toBe(true);
    expect(takesPackage(sales, "ops", parentOf)).toBe(false);
    expect(placesEntity(sales, "ops", {}, parentOf)).toBe(true);
    expect(placesEntity(sales, "orders", { ignore: true }, parentOf)).toBe(false);
    expect(placesEntity(conventionOf({ byConvention: "none" }), "orders", null, parentOf)).toBe(false);
  });
});

describe("Map to database… menu items", () => {
  it("is offered on a domain and an entity, for a multi-selection too", () => {
    const domain = { type: "domain" as const, kind: "package", element: true };
    const entity = { type: "element" as const, kind: "entity", element: true };
    expect(menuFor([domain]).map((i) => i.id)).toContain("map-to-database");
    expect(menuFor([domain, domain]).map((i) => i.id)).toContain("map-to-database");
    expect(menuFor([entity, entity]).map((i) => i.label)).toContain("Map to database…");
    expect(menuFor([{ type: "element", kind: "enum", element: true }]).map((i) => i.id)).not.toContain("map-to-database");
  });
});

describe("mock database view", () => {
  useMockApi();

  it("a new database holds nothing until a domain is mapped to it", async () => {
    const json = buildElement("database", { name: "archive", domain: null, dialect: "sqlite" }, newId);
    const created = await endpoints.createElement(json);
    expect(created.outcome).toBe("saved");
    const id = (json as unknown as { id: string }).id;
    expect((await endpoints.getDatabaseView(id)).view?.tables ?? []).toEqual([]);

    const current = await endpoints.getElement(id);
    const next = structuredClone(current.json) as unknown as Record<string, unknown>;
    mapDomains(next, [BILLING]);
    expect((await endpoints.saveElement(id, next as unknown as ModelJson, current.hash)).outcome).toBe("saved");
    const names = ((await endpoints.getDatabaseView(id)).view?.tables ?? []).map((t) => t.name);
    expect(names).toContain("customers");
  });
});
