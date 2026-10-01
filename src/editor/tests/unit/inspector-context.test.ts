// The inspector follows the ACTIVE context: an editor tab's element, the active explorer's own selection (or an empty
// state naming the explorer), the Generate screen's pack or unit, and nothing on Settings and Reference data.
import { describe, expect, it } from "vitest";
import { emptyTitle, inspectorContext, showsElement } from "@/inspector/context";
import { openTab } from "@/editors/tabs";
import { createEditorStore } from "@/state/store";

const ctx = (store: ReturnType<typeof createEditorStore>) => inspectorContext(store.getState());

describe("inspectorContext", () => {
  it("keeps a selection per explorer and shows an empty state after a rail switch", () => {
    const store = createEditorStore();
    store.getState().setSidebar("domain-model");
    store.getState().select(["invoice"]);
    expect(ctx(store)).toEqual({ mode: "element", ids: ["invoice"], source: "explorer" });

    store.getState().setSidebar("reference-data");
    store.getState().setWorkspace("entities");
    const empty = ctx(store);
    expect(empty.mode).toBe("empty");
    if (empty.mode === "empty") expect(emptyTitle(empty)).toBe("Select an element in Reference data");

    store.getState().setSidebar("databases");
    store.getState().setWorkspace("database");
    store.getState().select(["billing-db"]);
    expect(ctx(store)).toMatchObject({ mode: "element", ids: ["billing-db"] });

    // Back to the domain model: its own selection is still there.
    store.getState().setSidebar("domain-model");
    store.getState().setWorkspace("entities");
    expect(ctx(store)).toMatchObject({ mode: "element", ids: ["invoice"] });
  });

  it("drops a deleted element from every explorer's selection, also after a rail switch back", () => {
    const store = createEditorStore();
    store.getState().setSidebar("domain-model");
    store.getState().select(["invoice", "payment"]);
    store.getState().setSidebar("databases");
    store.getState().setWorkspace("database");
    store.getState().select(["billing-db"]);

    // Deleted elsewhere (a realtime change or an undo of the create) while another explorer is active.
    const exists = (id: string) => id !== "invoice";
    expect(inspectorContext({ ...store.getState(), exists }).mode).toBe("element");
    store.getState().pruneSelection(exists);

    store.getState().setSidebar("domain-model");
    store.getState().setWorkspace("entities");
    expect(ctx(store)).toEqual({ mode: "element", ids: ["payment"], source: "explorer" });
    expect(inspectorContext({ ...store.getState(), exists: () => false }).mode).toBe("empty");
    store.getState().pruneSelection(() => false);
    expect(ctx(store).mode).toBe("empty");
    expect(store.getState().selection).toEqual([]);
  });

  it("follows the active editor tab while one shows", () => {
    const store = createEditorStore();
    store.getState().select(["invoice"]);
    store.getState().updateEditors((e) => openTab(e, { id: "payment", kind: "entity" }, { pin: true }));
    const c = ctx(store);
    expect(c).toEqual({ mode: "element", ids: ["payment"], source: "editor" });
    expect(showsElement(c, "payment")).toBe(true);
    expect(showsElement(c, "invoice")).toBe(false);
    // Another screen: the tab stays open behind it and the explorer's selection shows again.
    store.getState().setWorkspace("database");
    expect(ctx(store)).toMatchObject({ mode: "element", ids: ["invoice"], source: "explorer" });
  });

  it("shows the pack or unit on Generate and nothing on Settings and Reference data", () => {
    const store = createEditorStore();
    store.getState().select(["invoice"]);
    store.getState().setWorkspace("generate");
    const none = ctx(store);
    expect(none.mode).toBe("empty");
    if (none.mode === "empty") expect(emptyTitle(none)).toBe("Select a pack in Generate");
    store.getState().setGeneration({ packTabs: ["sql-ddl"], packTab: "sql-ddl", packFocus: { pack: "sql-ddl", unit: "table" } });
    expect(ctx(store)).toEqual({ mode: "pack", pack: "sql-ddl", unit: "table" });
    store.getState().setGeneration({ packFocus: { pack: "other", unit: "x" } });
    expect(ctx(store)).toEqual({ mode: "pack", pack: "sql-ddl", unit: null });

    store.getState().setWorkspace("settings");
    expect(ctx(store)).toEqual({ mode: "none" });
    store.getState().setWorkspace("reference-data");
    expect(ctx(store)).toEqual({ mode: "none" });
  });

  it("inspects the TABLE on the Databases side, by its key, never its entity; the Domain model keeps the entity", () => {
    const store = createEditorStore();
    store.getState().setSidebar("domain-model");
    store.getState().select(["invoice"]);
    store.getState().setSidebar("databases");
    store.getState().setWorkspace("database");
    store.getState().select(["billing-db"]);
    // A canvas pick, a Tables list row or an explorer row: the table by its resolved key (a projected table has no file).
    store.getState().inspectTable({ database: "billing-db", key: "invoice@billing-db" }, "billing-db/t:invoice@billing-db");
    expect(ctx(store)).toEqual({ mode: "table", database: "billing-db", key: "invoice@billing-db", column: null });
    expect(store.getState().selectionBy.databases).toEqual([]);
    expect(store.getState().explorerItem).toBe("billing-db/t:invoice@billing-db");
    // A column picked in the grid shows with it; another table starts with none.
    store.getState().inspectColumn("number");
    expect(ctx(store)).toMatchObject({ mode: "table", column: "number" });
    store.getState().inspectTable({ database: "billing-db", key: "invoice@billing-db" });
    expect(ctx(store)).toMatchObject({ column: "number" });
    store.getState().inspectTable({ database: "billing-db", key: "customer@billing-db" });
    expect(ctx(store)).toMatchObject({ key: "customer@billing-db", column: null });

    // The Domain model shows its own selection, the entity.
    store.getState().setSidebar("domain-model");
    store.getState().setWorkspace("entities");
    expect(ctx(store)).toEqual({ mode: "element", ids: ["invoice"], source: "explorer" });
    // Back on the Databases side the table shows again; selecting an element there replaces it.
    store.getState().setSidebar("databases");
    store.getState().setWorkspace("database");
    expect(ctx(store)).toMatchObject({ mode: "table", key: "customer@billing-db" });
    store.getState().select(["billing-db"]);
    expect(ctx(store)).toMatchObject({ mode: "element", ids: ["billing-db"] });
    expect(store.getState().inspectedTable).toBeNull();
    // An element editor tab still wins.
    store.getState().inspectTable({ database: "billing-db", key: "customer@billing-db" });
    store.getState().updateEditors((e) => openTab(e, { id: "payment", kind: "entity" }, { pin: true }));
    expect(ctx(store)).toMatchObject({ mode: "element", ids: ["payment"], source: "editor" });
  });
});
