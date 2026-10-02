// The explorer's keyboard model, context menus and filtered walk (explorer-redesign.md 1.8, 3.4, 4.3), without
// rendering: explorer/useTreeKeyboard.ts, explorer/menus.ts and the tree helpers the rendering uses.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { parentIndex, treeKeyAction, typeAhead, type KeyRow } from "@/explorer/useTreeKeyboard";
import { isMovable, menuFor, type MenuTarget } from "@/explorer/menus";
import { buildForest, childKeys, filteredRows, isExpandable, nodeOf, positionOf, visibleRows, expandAt, collapseAt } from "@/explorer/tree";
import { emptyFilter, matchingIds } from "@/explorer/filter";

const rows: KeyRow[] = [
  { key: "billing", depth: 0, label: "Billing", expandable: true, expanded: true },
  { key: "billing/entities", depth: 1, label: "Entities", expandable: true, expanded: true },
  { key: "invoice", depth: 2, label: "Invoice", kind: "entity", expandable: true, expanded: false },
  { key: "customer", depth: 2, label: "Customer", kind: "entity", expandable: true, expanded: false },
  { key: "catalog", depth: 0, label: "Catalog", expandable: true, expanded: false },
  { key: "cart", depth: 0, label: "Cart", expandable: false, expanded: false },
];

describe("keyboard model (3.4)", () => {
  it("moves with the arrows, Home, End and the page keys, clamped", () => {
    expect(treeKeyAction(rows, 0, { key: "ArrowDown" })).toEqual({ type: "move", index: 1, extend: false });
    expect(treeKeyAction(rows, 0, { key: "ArrowUp" })).toEqual({ type: "move", index: 0, extend: false });
    expect(treeKeyAction(rows, 2, { key: "End" })).toEqual({ type: "move", index: 5, extend: false });
    expect(treeKeyAction(rows, 4, { key: "Home" })).toEqual({ type: "move", index: 0, extend: false });
    expect(treeKeyAction(rows, 1, { key: "PageDown" }, 3)).toEqual({ type: "move", index: 4, extend: false });
    expect(treeKeyAction(rows, 1, { key: "PageUp" }, 3)).toEqual({ type: "move", index: 0, extend: false });
    expect(treeKeyAction(rows, 2, { key: "ArrowDown", shift: true })).toEqual({ type: "move", index: 3, extend: true });
  });

  it("expands, enters, collapses and climbs with Right and Left", () => {
    expect(treeKeyAction(rows, 4, { key: "ArrowRight" })).toEqual({ type: "expand", index: 4 });
    expect(treeKeyAction(rows, 0, { key: "ArrowRight" })).toEqual({ type: "move", index: 1, extend: false });
    expect(treeKeyAction(rows, 5, { key: "ArrowRight" })).toBeNull();
    expect(treeKeyAction(rows, 1, { key: "ArrowLeft" })).toEqual({ type: "collapse", index: 1 });
    expect(treeKeyAction(rows, 3, { key: "ArrowLeft" })).toEqual({ type: "move", index: 1, extend: false });
    expect(treeKeyAction(rows, 5, { key: "ArrowLeft" })).toBeNull();
    expect(parentIndex(rows, 3)).toBe(1);
    expect(parentIndex(rows, 4)).toBe(-1);
  });

  it("opens, selects, renames, deletes and opens the menu", () => {
    expect(treeKeyAction(rows, 2, { key: "Enter" })).toEqual({ type: "open", index: 2 });
    expect(treeKeyAction(rows, 2, { key: " " })).toEqual({ type: "toggle-select", index: 2 });
    expect(treeKeyAction(rows, 2, { key: "F2" })).toEqual({ type: "rename", index: 2 });
    expect(treeKeyAction(rows, 2, { key: "Delete" })).toEqual({ type: "delete" });
    expect(treeKeyAction(rows, 2, { key: "ContextMenu" })).toEqual({ type: "menu", index: 2 });
    expect(treeKeyAction(rows, 2, { key: "F10", shift: true })).toEqual({ type: "menu", index: 2 });
    expect(treeKeyAction(rows, 2, { key: "F10" })).toBeNull();
    expect(treeKeyAction(rows, 2, { key: "*" })).toEqual({ type: "expand-siblings", index: 2 });
    expect(treeKeyAction(rows, 2, { key: "F6" })).toBeNull();
    expect(treeKeyAction([], 0, { key: "ArrowDown" })).toBeNull();
  });

  it("type-ahead jumps to the next row starting with the letters, and a repeated letter cycles", () => {
    expect(typeAhead(rows, 0, "c")).toBe(3);
    expect(typeAhead(rows, 3, "c")).toBe(4);
    expect(typeAhead(rows, 4, "cc")).toBe(5);
    expect(typeAhead(rows, 5, "c")).toBe(3);
    expect(typeAhead(rows, 0, "cat")).toBe(4);
    expect(typeAhead(rows, 4, "cat")).toBe(4);
    expect(typeAhead(rows, 0, "zz")).toBe(-1);
  });
});

describe("context menus (1.8)", () => {
  const entity: MenuTarget = { type: "element", kind: "entity", element: true, linked: true };
  const ids = (targets: MenuTarget[]) => menuFor(targets).map((i) => i.id);

  it("offers each kind its own actions", () => {
    expect(ids([entity])).toEqual([
      "open",
      "add-to-diagram",
      "add-with-related",
      "show-on-canvas",
      "where-used",
      "map-to-database",
      "edit-seed-data",
      "import-seed-csv",
      "go-to-table",
      "apply-stereotype",
      "tag",
      "set-category",
      "move",
      "rename",
      "favorite",
      "delete",
    ]);
    expect(ids([{ type: "element", kind: "relation", element: true }])).toEqual([
      "open",
      "add-to-diagram",
      "go-to-ends",
      "where-used",
      "apply-stereotype",
      "tag",
      "set-category",
      "move",
      "rename",
      "favorite",
      "delete",
    ]);
    expect(ids([{ type: "element", kind: "enum", element: true }])).toContain("where-used");
    expect(ids([{ type: "domain", kind: "package", element: true }])).toContain("new:entity");
    expect(ids([{ type: "domain", kind: "package", element: true }])).toContain("search-in-domain");
    expect(ids([{ type: "domain", kind: "package", element: true }])).toEqual(expect.arrayContaining(["export-seeds", "import-seeds"]));
    expect(menuFor([entity]).find((i) => i.id === "favorite")?.label).toBe("Add to favorites");
    expect(menuFor([{ ...entity, favorite: true }]).find((i) => i.id === "favorite")?.label).toBe("Remove from favorites");
    expect(ids([{ type: "folder", kind: "entity", element: false }])).toEqual(["new:entity", "select-all", "expand-all"]);
    expect(ids([{ type: "database", kind: "database", element: true }])).toEqual([
      "open-database",
      "open-mappings",
      "new-db:schema",
      "new-db:table",
      "new-db:view",
      "new-db:sequence",
      "new-db:routine",
      "new-db:query",
      "new-db:database-type",
      "new-db:sql-object",
      "expand-all",
    ]);
    expect(ids([{ type: "table", kind: "table", element: false, linked: true }])).toEqual(["open", "go-to-entity"]);
    expect(menuFor([entity]).find((i) => i.id === "delete")?.danger).toBe(true);
  });

  it("keeps only the multi-selection actions valid for every row, and nothing across kinds", () => {
    expect(ids([entity, { ...entity, linked: false }])).toEqual([
      "add-to-diagram",
      "add-with-related",
      "map-to-database",
      "apply-stereotype",
      "tag",
      "set-category",
      "move",
      "delete",
    ]);
    expect(ids([entity, { type: "element", kind: "relation", element: true }])).toEqual([]);
    expect(ids([])).toEqual([]);
  });

  it("moves only the kinds that live in a domain", () => {
    expect(isMovable("entity")).toBe(true);
    expect(isMovable("package")).toBe(true);
    expect(isMovable("diagram")).toBe(true);
    expect(isMovable("table")).toBe(false);
    expect(isMovable(undefined)).toBe(false);
  });
});

describe("tree helpers the rendering uses (4.3)", () => {
  const backend = new MockBackend();
  const index = backend.model.index();
  const forest = buildForest({ rows: index });

  it("starts collapsed, splices on expand and collapse, and gives every row its position", () => {
    const expanded = new Set<string>();
    const list = visibleRows(forest, "domain-model", expanded);
    expect(list.length).toBeGreaterThan(0);
    expect(list.every((r) => r.depth === 0)).toBe(true);
    const i = list.findIndex((r) => isExpandable(forest, r.key));
    const before = list.length;
    expanded.add(list[i].key);
    expandAt(forest, list, i, expanded);
    expect(list.length).toBe(before + childKeys(forest, list[i].key).length);
    const { pos, size } = positionOf(forest, list[i + 1].key);
    expect(pos).toBe(1);
    expect(size).toBe(childKeys(forest, list[i].key).length);
    collapseAt(list, i);
    expect(list.length).toBe(before);
  });

  it("filters to the matches and their ancestors, ancestors open", () => {
    const invoice = index.find((r) => r.name === "Invoice" && r.kind === "entity")!;
    const { rows: list, open } = filteredRows(forest, "domain-model", matchingIds(index, { ...emptyFilter, text: "Invoice" }));
    const keys = list.map((r) => r.key);
    expect(keys).toContain(forest.place.get(invoice.id));
    for (const k of keys) if (open.has(k)) expect(isExpandable(forest, k)).toBe(true);
    expect(list.every((r) => nodeOf(forest, r.key)!.explorer === "domain-model")).toBe(true);
    expect(filteredRows(forest, "domain-model", []).rows).toEqual([]);
  });
});
