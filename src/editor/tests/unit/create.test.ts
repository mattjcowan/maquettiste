// Creating elements from the explorer (explorer-redesign.md 1.8): the New actions of rows and explorers, the domain a
// New dialog starts on, and the documents it creates; the canvas's "All of X" view (1.6).
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { buildForest, childKeys, nodeOf } from "@/explorer/tree";
import {
  buildElement,
  CREATE_LABELS,
  currentDomain,
  DOMAIN_CREATE,
  domainOfKey,
  domainOfRow,
  EXPLORER_CREATE,
  FIRST_RUN_CREATE,
  folderCreate,
  nameProblem,
  startDomain,
} from "@/explorer/create";
import { menuFor } from "@/explorer/menus";
import { ALL_OF_CAP, parseView, viewElements } from "@/canvas/model";
import { isEmptyModel } from "@/workspaces/entities/EntitiesWorkspace";

function billing() {
  const rows = new MockBackend().model.index();
  const forest = buildForest({ rows });
  const entity = rows.find((r) => r.kind === "entity" && r.package)!;
  return { rows, forest, entity, domain: entity.package! };
}

let n = 0;
const ids = () => `id-${++n}`;

describe("New actions (1.8)", () => {
  it("offers a domain's New actions, a kind folder's own kind and each explorer's kinds", () => {
    const labels = menuFor([{ type: "domain", kind: "package", element: true }]).map((i) => i.label);
    for (const k of DOMAIN_CREATE) expect(labels).toContain(CREATE_LABELS[k]);
    expect(labels).toContain("New sub-domain");
    expect(menuFor([{ type: "folder", kind: "enum", element: false }])[0]).toMatchObject({ id: "new:enum", label: "New enum" });
    expect(menuFor([{ type: "folder", kind: "package", element: false }])[0].id).toBe("new:sub-package");
    expect(menuFor([{ type: "folder", kind: "seed", element: false }]).some((i) => i.id.startsWith("new:"))).toBe(false);
    expect(menuFor([{ type: "group", element: false, domainGroup: true }])[0].id).toBe("new:diagram");
    expect(menuFor([{ type: "group", element: false, explorer: "databases" }])[0].id).toBe("new:database");
    expect(menuFor([{ type: "element", kind: "diagram", element: true }]).map((i) => i.id)).toContain("duplicate");
    expect(folderCreate("scalar-type")).toBe("scalar-type");
    expect(EXPLORER_CREATE.databases).toEqual(["database"]);
    expect(EXPLORER_CREATE["reference-data"]).toEqual(["reference-type"]);
    expect(FIRST_RUN_CREATE).toEqual(expect.arrayContaining(["package", "entity", "reference-type", "diagram", "database"]));
  });

  it("starts the domain picker on the row's domain, the selection's, or the open diagram's home", () => {
    const { forest, entity, domain } = billing();
    expect(domainOfKey(forest, domain)).toBe(domain);
    expect(domainOfKey(forest, entity.id)).toBe(domain);
    const folder = childKeys(forest, domain).find((k) => nodeOf(forest, k)?.kind === "entity")!;
    expect(domainOfKey(forest, folder)).toBe(domain);
    expect(currentDomain(forest, { selection: [entity.id] })).toBe(domain);
    expect(currentDomain(forest, { selection: [], activeDiagram: `pkg:${domain}` })).toBe(domain);
    expect(currentDomain(forest, { selection: [] })).toBeNull();
    expect(currentDomain(null, { selection: [entity.id] })).toBeNull();
    // "New domain" with an entity selected makes a top-level domain; "New sub-domain" and other kinds keep the domain.
    const now = currentDomain(forest, { selection: [entity.id] });
    expect(startDomain("package", now)).toBeNull();
    expect(startDomain("sub-package", now)).toBe(domain);
    expect(startDomain("entity", now)).toBe(domain);
  });

  it("builds each kind with the fields its schema requires, in the chosen domain", () => {
    const input = { name: "Shipment", domain: "d1" };
    expect(buildElement("entity", input, ids)).toMatchObject({ kind: "entity", name: "Shipment", package: "d1", key: { strategy: "uuid-v7" } });
    expect(buildElement("sub-package", { name: "Invoicing", domain: "d1" }, ids)).toMatchObject({ kind: "package", parent: "d1" });
    expect(buildElement("package", { name: "Billing", domain: null }, ids)).not.toHaveProperty("parent");
    expect(buildElement("scalar-type", { ...input, base: "int32" }, ids)).toMatchObject({ kind: "scalar-type", base: "int32", package: "d1" });
    expect(buildElement("database", { name: "main", domain: "d1", dialect: "sqlite" }, ids)).toEqual({
      kind: "database",
      id: expect.any(String),
      name: "main",
      dialect: "sqlite",
      byConvention: "none",
    });
    expect(buildElement("diagram", { name: "Overview", domain: "d1" }, ids)).toMatchObject({ kind: "diagram", package: "d1" });
    const relation = buildElement(
      "relation",
      { name: "CustomerOrders", domain: "d1", source: "a", target: "b", sourceName: "Customer", targetName: "Order" },
      ids,
    ) as unknown as {
      ends: { entity: string; role: string }[];
    };
    expect(relation.ends.map((e) => [e.entity, e.role])).toEqual([
      ["a", "customer"],
      ["b", "orders"],
    ]);
    expect(nameProblem("entity", "")).toBe("Enter a name.");
    expect(nameProblem("entity", "2Fast")).not.toBeNull();
    expect(nameProblem("diagram", "Billing overview")).toBeNull();
  });
});

describe("All of a domain (1.6)", () => {
  it("shows at most ALL_OF_CAP entities and only the relations whose ends are all shown", () => {
    const rows = [
      ...Array.from({ length: ALL_OF_CAP + 5 }, (_, i) => ({ id: `e${i}`, kind: "entity", package: "p" })),
      { id: "x", kind: "entity", package: "other" },
      { id: "r-in", kind: "relation", ends: [{ entity: "e0" }, { entity: "e1" }] },
      { id: "r-out", kind: "relation", ends: [{ entity: "e0" }, { entity: "x" }] },
      { id: "r-cut", kind: "relation", ends: [{ entity: "e0" }, { entity: `e${ALL_OF_CAP + 1}` }] },
    ];
    const view = viewElements(parseView("pkg:p"), rows, []);
    expect(view.entityIds).toHaveLength(ALL_OF_CAP);
    expect(view.relationIds).toEqual(["r-in"]);
  });

  it("calls a model with only settings documents empty", () => {
    expect(isEmptyModel([])).toBe(true);
    expect(isEmptyModel([{ kind: "tag-vocabulary" }, { kind: "stereotype" }])).toBe(true);
    expect(isEmptyModel([{ kind: "package" }])).toBe(false);
  });
});

describe("explorer state across a reload (3.3)", () => {
  it("reads no global expansion (page state is per project) and opens the New dialog through the store", async () => {
    const { createEditorStore } = await import("@/state/store");
    localStorage.setItem("mq.explorer.expanded.domain-model", JSON.stringify(["billing", "billing/entity"]));
    localStorage.setItem("mq.explorer.active", "diagrams");
    const store = createEditorStore();
    expect(store.getState().explorer.views["domain-model"].expanded.size).toBe(0);
    expect(store.getState().explorer.active).toBe("domain-model");
    expect(store.getState().explorer.views.databases.expanded.size).toBe(0);
    store.getState().requestNew({ kind: "enum", domain: "billing" });
    expect(store.getState().newElement).toEqual({ kind: "enum", domain: "billing" });
    store.getState().requestNew(null);
    expect(store.getState().newElement).toBeNull();
    localStorage.clear();
  });
});

describe("the canvas New entity domain (1.8)", () => {
  it("follows the explorer's rule: a seed's target, none for an element in no domain", () => {
    const byId = new Map(
      [
        { id: "d", kind: "package" },
        { id: "t", kind: "reference-type", package: "d" },
        { id: "s", kind: "seed", target: "t" },
        { id: "loose", kind: "entity", package: null },
      ].map((r) => [r.id, r]),
    );
    expect(domainOfRow(byId, "s")).toBe("d");
    expect(domainOfRow(byId, "loose")).toBeNull();
    expect(domainOfRow(byId, "d")).toBe("d");
  });
});
