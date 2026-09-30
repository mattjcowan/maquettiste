// Panels and page state (the owner's 0.2.0 test pass): every panel collapses and restores, the layout is kept per
// browser and "Reset layout" clears it; the page state is kept per project and restored before the shell draws.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createEditorStore, watchLayout } from "@/state/store";
import { DEFAULT_LAYOUT, LAYOUT_KEY, PANEL_KEYS, PANELS, PINNED_KEY, SCREEN_PANELS, panelForKey, readLayout } from "@/state/layout";
import { capturePage, forgetPage, pageChanged, pageKey, projectPageId, readPage, restorePage, watchPage, writePage } from "@/state/pageState";
import { openTab } from "@/editors/tabs";

const key = (code: string, mods: Partial<Record<"altKey" | "shiftKey" | "ctrlKey" | "metaKey", boolean>>) => ({
  code,
  altKey: false,
  shiftKey: false,
  ctrlKey: false,
  metaKey: false,
  ...mods,
});

describe("layout", () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.useRealTimers());

  it("opens every panel at the default sizes when nothing is saved", () => {
    expect(readLayout()).toEqual(DEFAULT_LAYOUT);
    const s = createEditorStore().getState();
    expect([s.explorerCollapsed, s.inspectorCollapsed, s.bottomCollapsed, s.tabsCollapsed, s.topbarCollapsed]).toEqual([false, false, false, false, false]);
    expect([s.explorerSize, s.inspectorSize, s.bottomSize]).toEqual([280, 360, 220]);
  });

  it("reads the sizes-only format of 0.2.0 and clamps bad values", () => {
    localStorage.setItem(LAYOUT_KEY, JSON.stringify({ explorer: 300, inspector: 9999, bottom: "tall", collapsed: ["bottom", "nonsense"] }));
    const layout = readLayout();
    expect(layout.explorerSize).toBe(300);
    expect(layout.inspectorSize).toBe(560);
    expect(layout.bottomSize).toBe(220);
    expect(layout.collapsed).toEqual({ explorer: false, inspector: false, bottom: true, tabs: false, topbar: false, tables: false, ddl: false });
    localStorage.setItem(LAYOUT_KEY, "{not json");
    expect(readLayout()).toEqual(DEFAULT_LAYOUT);
  });

  it("saves every panel's toggle at once and brings it back in a new store", () => {
    const store = createEditorStore();
    const stop = watchLayout(store);
    for (const panel of PANELS) store.getState().toggle(panel);
    store.getState().pinExplorer("databases");
    stop();
    expect(JSON.parse(localStorage.getItem(LAYOUT_KEY)!).collapsed).toEqual(["explorer", "inspector", "bottom", "tabs", "topbar", "tables", "ddl"]);
    const again = createEditorStore().getState();
    expect([again.explorerCollapsed, again.inspectorCollapsed, again.bottomCollapsed, again.tabsCollapsed, again.topbarCollapsed]).toEqual([
      true,
      true,
      true,
      true,
      true,
    ]);
    expect([again.tablesCollapsed, again.ddlCollapsed]).toEqual([true, true]);
    expect(again.explorer.pinned).toBe("databases");
    // A toggle with an explicit value is idempotent.
    again.toggle("explorer", true);
    expect(again.explorerCollapsed).toBe(true);
  });

  it("saves a resize after the last move", () => {
    vi.useFakeTimers();
    const store = createEditorStore();
    const stop = watchLayout(store, 300);
    store.setState({ explorerSize: 300 });
    store.setState({ explorerSize: 320 });
    expect(localStorage.getItem(LAYOUT_KEY)).toBeNull();
    vi.advanceTimersByTime(300);
    expect(JSON.parse(localStorage.getItem(LAYOUT_KEY)!).explorer).toBe(320);
    store.setState({ inspectorSize: 400 });
    stop(); // a pending resize is written on the way out
    expect(JSON.parse(localStorage.getItem(LAYOUT_KEY)!).inspector).toBe(400);
  });

  it("Reset layout opens every panel at the default sizes, unpins and clears what was saved", () => {
    const store = createEditorStore();
    const stop = watchLayout(store);
    store.getState().toggle("inspector");
    store.getState().toggle("topbar");
    store.getState().toggle("tables");
    store.getState().toggle("ddl");
    store.getState().pinExplorer("diagrams");
    store.setState({ explorerSize: 400 });
    stop();
    store.getState().resetLayout();
    const s = store.getState();
    expect([s.explorerCollapsed, s.inspectorCollapsed, s.bottomCollapsed, s.tabsCollapsed, s.topbarCollapsed]).toEqual([false, false, false, false, false]);
    expect([s.tablesCollapsed, s.ddlCollapsed]).toEqual([false, false]);
    expect(s.explorerSize).toBe(280);
    expect(s.explorer.pinned).toBeNull();
    expect(localStorage.getItem(LAYOUT_KEY)).toBeNull();
    expect(localStorage.getItem(PINNED_KEY)).toBeNull();
  });

  it("keeps the Database screen's two panels in the layout, keyed to that screen", () => {
    localStorage.setItem(LAYOUT_KEY, JSON.stringify({ collapsed: ["tables", "ddl"] }));
    expect(readLayout().collapsed).toMatchObject({ tables: true, ddl: true, explorer: false });
    expect(SCREEN_PANELS).toEqual({ tables: "database", ddl: "database" });
    expect(PANEL_KEYS.tables.label).toBe("Alt+Shift+L");
    expect(PANEL_KEYS.ddl.label).toBe("Alt+Shift+D");
    expect(new Set(PANELS.map((p) => PANEL_KEYS[p].code)).size).toBe(PANELS.length);
  });

  it("maps Alt+Shift and a physical letter to a panel, and nothing else", () => {
    expect(panelForKey(key("KeyE", { altKey: true, shiftKey: true }))).toBe("explorer");
    expect(panelForKey(key("KeyP", { altKey: true, shiftKey: true }))).toBe("inspector");
    expect(panelForKey(key("KeyJ", { altKey: true, shiftKey: true }))).toBe("bottom");
    expect(panelForKey(key("KeyO", { altKey: true, shiftKey: true }))).toBe("tabs");
    expect(panelForKey(key("KeyH", { altKey: true, shiftKey: true }))).toBe("topbar");
    expect(panelForKey(key("KeyL", { altKey: true, shiftKey: true }))).toBe("tables");
    expect(panelForKey(key("KeyD", { altKey: true, shiftKey: true }))).toBe("ddl");
    expect(panelForKey(key("KeyT", { altKey: true, shiftKey: true }))).toBeNull(); // the browser's toolbar
    expect(panelForKey(key("KeyE", { altKey: true }))).toBeNull();
    expect(panelForKey(key("KeyE", { altKey: true, shiftKey: true, ctrlKey: true }))).toBeNull();
    expect(panelForKey(key("Escape", {}))).toBeNull();
    expect(panelForKey(key("KeyI", { altKey: true, shiftKey: true }))).toBeNull(); // the browser's
  });
});

describe("page state", () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.useRealTimers());

  function worked() {
    const store = createEditorStore();
    const s = store.getState();
    s.setSidebar("databases");
    s.select(["db:main"]);
    s.setSidebar("domain-model");
    s.select(["e:billing.invoice"]);
    s.explorer.views["domain-model"].expanded.add("p:billing");
    s.updateEditors((e) =>
      openTab(openTab(e, { id: "e:billing.invoice", kind: "entity" }, { pin: true }), { id: "e:billing.customer", kind: "entity" }, { pin: true }),
    );
    s.setGeneration({ chosenPacks: ["sql-ddl"] });
    s.setSettingsTab("locales");
    return store;
  }

  it("round-trips per project and keeps projects apart", () => {
    const page = capturePage(worked().getState());
    writePage("billing", page);
    expect(readPage("billing")).toEqual(page);
    expect(readPage("other")).toBeNull();
    expect(localStorage.getItem(pageKey("billing"))).not.toBeNull();
  });

  it("restores the explorer, expanded rows, tabs, selections, packs and Settings tab; the address keeps the active selection", () => {
    writePage("billing", capturePage(worked().getState()));
    const store = createEditorStore();
    restorePage(store, readPage("billing")!, "entities");
    const s = store.getState();
    expect(s.explorer.active).toBe("domain-model");
    expect([...s.explorer.views["domain-model"].expanded]).toEqual(["p:billing"]);
    expect(s.editors.tabs.map((t) => t.id)).toEqual(["e:billing.invoice", "e:billing.customer"]);
    expect(s.editors.active).toBe(s.editors.tabs[1].key);
    expect(s.selectionBy).toEqual({ databases: ["db:main"] });
    expect(s.generation.chosenPacks).toEqual(["sql-ddl"]);
    expect(s.settingsTab).toBe("locales");
    expect(s.workspace).toBe("entities");
  });

  it("drops what it cannot trust", () => {
    localStorage.setItem(
      pageKey("billing"),
      JSON.stringify({
        explorer: "nowhere",
        expanded: { bogus: ["x"], diagrams: [1, "d:a"] },
        editors: { tabs: [{ key: "t1", id: "x", kind: "table" }, "junk"], active: "t1" },
      }),
    );
    const page = readPage("billing")!;
    expect(page.explorer).toBe("domain-model");
    expect(page.expanded).toEqual({ diagrams: ["d:a"] });
    expect(page.editors).toEqual({ tabs: [], active: null, view: {}, next: 1 });
    expect(page.chosenPacks).toBeNull();
  });

  it("saves after the last change, on an in-place expansion, and at once when the page is hidden", () => {
    vi.useFakeTimers();
    const store = createEditorStore();
    const stop = watchPage(store, "billing", 300);
    store.getState().setSettingsTab("tags");
    expect(readPage("billing")).toBeNull();
    vi.advanceTimersByTime(300);
    expect(readPage("billing")?.settingsTab).toBe("tags");
    store.getState().explorer.views.diagrams.expanded.add("d:folder");
    pageChanged();
    vi.advanceTimersByTime(300);
    expect(readPage("billing")?.expanded.diagrams).toEqual(["d:folder"]);
    store.getState().setGeneration({ chosenPacks: [] });
    window.dispatchEvent(new Event("pagehide"));
    expect(readPage("billing")?.chosenPacks).toEqual([]);
    stop();
    pageChanged(); // no watcher: nothing happens
  });

  it("is keyed by the project key, moves a name-keyed state once, and drops the 0.2.0 global explorer keys", () => {
    expect(projectPageId({ name: "billing", projectKey: "0123456789abcdef" })).toBe("0123456789abcdef");
    expect(projectPageId({ name: "billing" })).toBe("billing");
    localStorage.setItem("mq.explorer.active", "diagrams");
    localStorage.setItem("mq.explorer.expanded.diagrams", '["d:folder"]');
    writePage("billing", capturePage(createEditorStore().getState()));
    expect(localStorage.getItem("mq.explorer.active")).toBeNull();
    expect(localStorage.getItem("mq.explorer.expanded.diagrams")).toBeNull();
    expect(readPage("0123456789abcdef", "billing")).not.toBeNull();
    expect(localStorage.getItem(pageKey("billing"))).toBeNull();
    expect(localStorage.getItem(pageKey("0123456789abcdef"))).not.toBeNull();
    expect(readPage("fedcba9876543210", "other")).toBeNull();
  });

  it("forgets a project's page state and puts the explorer, tabs and selections back to their defaults", () => {
    const store = createEditorStore();
    store.getState().setSidebar("diagrams");
    store.getState().explorer.views.diagrams.expanded.add("d:folder");
    store.getState().setSettingsTab("tags");
    writePage("billing", capturePage(store.getState()));
    forgetPage(store, "billing");
    expect(localStorage.getItem(pageKey("billing"))).toBeNull();
    const s = store.getState();
    expect(s.explorer.active).toBe("domain-model");
    expect(s.explorer.views.diagrams.expanded.size).toBe(0);
    expect(s.editors.tabs).toEqual([]);
    expect(s.settingsTab).toBeNull();
    expect(s.selectionBy).toEqual({});
  });
});
