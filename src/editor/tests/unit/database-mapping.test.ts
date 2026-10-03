// Explicit mapping (engine-design.md D46, explorer-redesign.md 1.3): a database holds what its byConvention takes plus
// the entities a mapping names; a file without the member keeps "every entity" (or its packages'). A new database starts
// empty (no convention, no domain picks); domains and entities offer the Storage bulk actions, never Map to database…; the
// mock server's database view.
import { describe, expect, it } from "vitest";
import * as endpoints from "@/api/endpoints";
import type { ModelJson } from "@/api/types";
import { buildElement } from "@/explorer/create";
import { menuFor } from "@/explorer/menus";
import { conventionLabel, conventionOf, EMPTY_DATABASE_HINT, placesEntity, setConvention, takesPackage } from "@/model/databaseMapping";
import { newId } from "@/lib/ids";
import { useMockApi } from "./harness";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";

describe("database convention", () => {
  it("reads a file without the member as before: all domains, or its packages", () => {
    expect(conventionOf({})).toEqual({ mode: "all", explicit: false, packages: [], schemas: {} });
    expect(conventionOf({ packages: ["a"] })).toEqual({ mode: "packages", explicit: false, packages: ["a"], schemas: {} });
    expect(conventionOf({ byConvention: "none", packages: ["a"] })).toEqual({ mode: "none", explicit: true, packages: [], schemas: {} });
    expect(conventionLabel(conventionOf({}), (x) => x)).toBe("By convention: all domains (unspecified)");
    expect(conventionLabel(conventionOf({ byConvention: "packages" }), (x) => x)).toBe("By convention: nothing");
  });

  it("starts every new database empty: no convention and no domains", () => {
    const ids = () => "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    const json = buildElement("database", { name: "reporting", domain: null, schemas: [{ id: "S", name: "sales" }] }, ids);
    expect(json).toMatchObject({ byConvention: "none", defaultSchema: "sales" });
    expect(json).not.toHaveProperty("packages");
    // The empty database's hint is the database's own: no mapping, no Domain model.
    expect(EMPTY_DATABASE_HINT).not.toMatch(/map|domain|entit/i);
  });

  it("writes the convention explicitly", () => {
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

describe("storage on domain and entity menus", () => {
  it("offers the Storage bulk actions, never Map to database…", () => {
    const domain = { type: "domain" as const, kind: "package", element: true };
    const entity = { type: "element" as const, kind: "entity", element: true };
    for (const targets of [[domain], [entity], [entity, entity]]) {
      const labels = menuFor(targets).map((i) => i.label);
      expect(labels).not.toContain("Map to database…");
      expect(labels).toEqual(expect.arrayContaining(["Auto-map to existing tables…", "Create tables…", "Remove bindings…"]));
    }
  });
});

describe("mock database view", () => {
  useMockApi();

  it("a new database holds nothing until its convention takes a domain (an older project's)", async () => {
    const json = buildElement("database", { name: "archive", domain: null, dialect: "sqlite" }, newId);
    const created = await endpoints.createElement(json);
    expect(created.outcome).toBe("saved");
    const id = (json as unknown as { id: string }).id;
    expect((await endpoints.getDatabaseView(id)).view?.tables ?? []).toEqual([]);

    const current = await endpoints.getElement(id);
    const next = structuredClone(current.json) as unknown as Record<string, unknown>;
    setConvention(next, "packages", [BILLING]);
    expect((await endpoints.saveElement(id, next as unknown as ModelJson, current.hash)).outcome).toBe("saved");
    const names = ((await endpoints.getDatabaseView(id)).view?.tables ?? []).map((t) => t.name);
    expect(names).toContain("customers");
  });
});
