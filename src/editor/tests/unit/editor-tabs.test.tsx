// Element editor tabs (explorer-redesign.md 3.6): preview and pinned tabs, closing, General mode, and the entity
// editor rendered over the mock API.
import { act, fireEvent, render, renderHook, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { App } from "@/app/App";
import {
  activate,
  activeTab,
  closeElement,
  closeTab,
  emptyEditorTabs,
  followSelection,
  hasEditor,
  openTab,
  pinTab,
  setFollow,
  setView,
  type EditorTabsState,
} from "@/editors/tabs";
import { baseChain, relatedOf } from "@/editors/related";
import { applyIndexPatch } from "@/api/indexPatch";
import { QueryClientProvider } from "@tanstack/react-query";
import { createQueryClient, ELEMENT_GC_TIME, PREFETCH_DELAY_MS } from "@/api/queries";
import { useDelayedPrefetch } from "@/explorer/prefetch";
import { cardinalityOf, KEY_STRATEGIES, nextAlternateKeyName, toggleAlternateKeyField } from "@/editors/EntityEditor";
import { withGenerationHints } from "@/editors/EditorFrame";
import { createEditorStore } from "@/state/store";
import type { ElementSummary } from "@/api/types";
import { IDS, useMockApi } from "./harness";

const entity = (id: string) => ({ id, kind: "entity" as const });
const ids = (s: EditorTabsState) => s.tabs.map((t) => `${t.id}${t.pinned ? "" : "?"}${t.follow ? "*" : ""}`);

describe("editor tab state", () => {
  it("replaces the preview tab on the next preview open and pins on an open", () => {
    let s = openTab(emptyEditorTabs(), entity("A"), { pin: false });
    expect(ids(s)).toEqual(["A?"]);
    s = openTab(s, entity("B"), { pin: false });
    expect(ids(s)).toEqual(["B?"]);
    expect(activeTab(s)?.id).toBe("B");
    // Opening the preview's element pins it; the next preview adds a tab after the active one.
    s = openTab(s, entity("B"), { pin: true });
    expect(ids(s)).toEqual(["B"]);
    s = openTab(s, entity("C"), { pin: false });
    s = openTab(s, entity("D"), { pin: false });
    expect(ids(s)).toEqual(["B", "D?"]);
    s = pinTab(s, s.tabs[1].key);
    s = activate(s, s.tabs[0].key);
    s = openTab(s, entity("E"), { pin: true });
    expect(ids(s)).toEqual(["B", "E", "D"]);
    // An element already open is shown, not opened twice.
    const again = openTab(s, entity("D"), { pin: false });
    expect(ids(again)).toEqual(["B", "E", "D"]);
    expect(activeTab(again)?.id).toBe("D");
  });

  it("closes a tab and shows its right neighbour, else its left one, else the screen", () => {
    let s = emptyEditorTabs();
    for (const id of ["A", "B", "C"]) s = openTab(s, entity(id), { pin: true });
    s = activate(s, s.tabs[1].key);
    s = closeTab(s, s.tabs[1].key);
    expect(activeTab(s)?.id).toBe("C");
    s = closeTab(s, s.tabs[1].key);
    expect(activeTab(s)?.id).toBe("A");
    s = closeElement(s, "A");
    expect(s.tabs).toHaveLength(0);
    expect(s.active).toBeNull();
    expect(closeTab(s, "nope")).toBe(s);
  });

  it("General mode follows the selection, keeps the sub-tab, and leaves preview opens alone", () => {
    let s = openTab(emptyEditorTabs(), entity("A"), { pin: false });
    s = setView(s, "entity", "mapping");
    s = setFollow(s, s.tabs[0].key, true);
    expect(ids(s)).toEqual(["A*"]);
    const key = s.tabs[0].key;
    s = followSelection(s, entity("B"));
    expect(ids(s)).toEqual(["B*"]);
    expect(s.tabs[0].key).toBe(key);
    expect(s.view.entity).toBe("mapping");
    expect(followSelection(s, entity("B"))).toBe(s);
    // While the following tab shows, a preview open goes to it; a pinned open adds a tab.
    s = openTab(s, entity("Q"), { pin: false });
    expect(ids(s)).toEqual(["Q*"]);
    s = openTab(s, entity("B"), { pin: true });
    expect(ids(s)).toEqual(["Q*", "B"]);
    s = openTab(s, entity("C"), { pin: false });
    expect(ids(s)).toEqual(["Q*", "B", "C?"]);
    s = closeTab(s, s.tabs[1].key);
    s = followSelection(s, entity("B"));
    // One following tab at a time.
    s = setFollow(s, s.tabs[1].key, true);
    expect(ids(s)).toEqual(["B", "C*"]);
    // Without a following tab, a selection changes nothing.
    s = setFollow(s, s.tabs[1].key, false);
    expect(followSelection(s, entity("Z"))).toBe(s);
  });

  it("a click then a pinned open on the same element in General mode adds a pinned tab", () => {
    let s = openTab(emptyEditorTabs(), entity("A"), { pin: false });
    s = setFollow(s, s.tabs[0].key, true);
    // The double click's first click moves the following tab to B, then the open pins B in its own tab.
    s = openTab(s, entity("B"), { pin: false });
    expect(ids(s)).toEqual(["B*"]);
    s = openTab(s, entity("B"), { pin: true });
    expect(ids(s)).toEqual(["B*", "B"]);
    expect(s.active).toBe(s.tabs[1].key);
    // A second pinned open shows that tab; the selection moving leaves it in place.
    expect(openTab(s, entity("B"), { pin: true })).toBe(s);
    s = followSelection(s, entity("C"));
    expect(ids(s)).toEqual(["C*", "B"]);
  });

  it("keeps alternate keys valid, names them without clashes, and sets generation hints", () => {
    const keys = [
      { id: "k1", name: "AlternateKey1", attributes: ["a"] },
      { id: "k3", name: "AlternateKey3", attributes: ["a", "b"] },
    ];
    expect(nextAlternateKeyName(keys)).toBe("AlternateKey2");
    expect(nextAlternateKeyName([])).toBe("AlternateKey1");
    // Unticking the last field drops the key (entity.json: minItems 1); ticking adds once.
    expect(toggleAlternateKeyField(keys, "k1", "a", false).map((k) => k.id)).toEqual(["k3"]);
    expect(toggleAlternateKeyField(keys, "k3", "b", false)[1].attributes).toEqual(["a"]);
    expect(toggleAlternateKeyField(keys, "k1", "a", true)[0].attributes).toEqual(["a"]);
    expect(KEY_STRATEGIES).toContain("application");
    expect(cardinalityOf({})).toBe("0..*");
    expect(cardinalityOf({ min: 1, max: 1 })).toBe("1..1");
    const g = withGenerationHints(undefined, "*", { skip: true });
    expect(g).toEqual({ "*": { skip: true } });
    expect(withGenerationHints(g, "*", undefined)).toBeUndefined();
  });

  it("reads a row's document ahead after the pointer rests on it for 300 ms", async () => {
    vi.useFakeTimers();
    try {
      const qc = createQueryClient();
      const prefetch = vi.spyOn(qc, "prefetchQuery").mockResolvedValue(undefined);
      const { result, unmount } = renderHook(() => useDelayedPrefetch(), {
        wrapper: ({ children }) => <QueryClientProvider client={qc}>{children}</QueryClientProvider>,
      });
      result.current.start("A");
      vi.advanceTimersByTime(PREFETCH_DELAY_MS - 1);
      result.current.cancel();
      vi.advanceTimersByTime(PREFETCH_DELAY_MS);
      expect(prefetch).not.toHaveBeenCalled();
      result.current.start("B");
      vi.advanceTimersByTime(PREFETCH_DELAY_MS);
      expect(prefetch).toHaveBeenCalledTimes(1);
      expect(prefetch.mock.calls[0][0]).toMatchObject({ queryKey: ["element", "B"], gcTime: ELEMENT_GC_TIME });
      unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it("knows which kinds have an editor", () => {
    expect(["entity", "relation", "enum", "value-object", "scalar-type"].every(hasEditor)).toBe(true);
    expect(hasEditor("diagram")).toBe(false);
    expect(hasEditor(undefined)).toBe(false);
  });

  it("in the store: an edit pins the preview tab, and another screen shows in front of the tabs", () => {
    const store = createEditorStore();
    store.getState().updateEditors((s) => openTab(s, entity("A"), { pin: false }));
    expect(store.getState().editors.tabs[0].pinned).toBe(false);
    store.getState().setDraft({
      id: "A",
      channel: "element",
      baseHash: "h",
      baseJson: {} as never,
      json: {} as never,
      status: "dirty",
      diagnostics: [],
      conflict: null,
      error: null,
    });
    expect(store.getState().editors.tabs[0].pinned).toBe(true);
    expect(store.getState().editors.active).not.toBeNull();
    store.getState().setWorkspace("database");
    expect(store.getState().editors.active).toBeNull();
    expect(store.getState().editors.tabs).toHaveLength(1);
  });
});

describe("editor related rows", () => {
  const row = (over: Partial<ElementSummary> & Record<string, unknown>) =>
    ({ id: "X", kind: "entity", name: "X", package: null, tags: [], category: null, stereotypes: [], path: "", hash: "", ...over }) as ElementSummary;
  it("lists relations, mappings, tables, seeds and derived entities of an entity from the index", () => {
    const rows = [
      row({ id: "A", name: "Invoice" }),
      row({ id: "B", name: "Special", base: "A" }),
      row({
        id: "R",
        kind: "relation",
        name: "has",
        ends: [
          { entity: "A", role: "invoice" },
          { entity: "C", role: "c" },
        ],
      }),
      row({ id: "M", kind: "mapping", name: "m", entity: "A" }),
      row({ id: "T", kind: "table", name: "invoice", entity: "A" }),
      row({ id: "S", kind: "seed" as never, name: "seed", target: "A" }),
    ];
    const r = relatedOf(rows, "A");
    expect([r.relations, r.mappings, r.tables, r.seeds, r.derived].map((l) => l.map((x) => x.id))).toEqual([["R"], ["M"], ["T"], ["S"], ["B"]]);
    expect(relatedOf(rows, "A")).toBe(r);
    // A patched index carries the reverse index over: untouched answers are kept, touched ones recomputed.
    const c = relatedOf(rows, "C");
    const patched = applyIndexPatch(rows, [row({ id: "T2", kind: "table", name: "archive", entity: "A" })], ["M"]);
    expect(relatedOf(patched, "C")).toBe(c);
    const p = relatedOf(patched, "A");
    expect([p.mappings, p.tables].map((l) => l.map((x) => x.id))).toEqual([[], ["T2", "T"]]);
    const moved = applyIndexPatch(patched, [row({ id: "R", kind: "relation", name: "has", ends: [{ entity: "C", role: "c" }] })], []);
    expect(relatedOf(moved, "A").relations).toEqual([]);
    expect(relatedOf(moved, "C").relations.map((x) => x.id)).toEqual(["R"]);
    // The first array, asked again after its state moved on, rebuilds its own answer.
    expect(relatedOf(rows, "A").mappings.map((x) => x.id)).toEqual(["M"]);
    const byId = new Map(rows.map((x) => [x.id, x]));
    expect(baseChain(byId, "B")).toEqual(["B", "A"]);
    byId.set("A", row({ id: "A", base: "B" }));
    expect(baseChain(byId, "B")).toEqual(["B", "A"]);
  });
});

describe("entity editor", () => {
  const api = useMockApi();
  beforeAll(() => {
    globalThis.ResizeObserver ??= class {
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver;
    window.matchMedia ??= ((query: string) => ({
      matches: false,
      media: query,
      addEventListener() {},
      removeEventListener() {},
      addListener() {},
      removeListener() {},
      onchange: null,
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia;
    Element.prototype.scrollTo ??= () => undefined;
  });
  afterEach(() => localStorage.clear());

  it("opens in a preview tab, keeps its sub-tab in General mode, marks unsaved edits and closes on a middle click", async () => {
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    const store = api.services.store;
    await waitFor(() => expect(api.services.queryClient.getQueryData(["index"])).toBeTruthy(), { timeout: 5000 });
    act(() => store.getState().updateEditors((s) => openTab(s, entity(IDS.invoice), { pin: false })));
    const editor = await screen.findByRole("region", { name: "Editor: Invoice" }, { timeout: 5000 });
    const tabs = screen.getByTestId("editor-tabs");
    expect(within(tabs).getByTestId("editor-tab").dataset.pinned).toBe("false");
    for (const name of ["Attributes", "Relationships", "Indexes", "Mappings", "Seed data", "Code generation", "References"])
      expect(within(editor).getByRole("tab", { name })).toBeTruthy();
    expect(within(editor).getByRole("grid", { name: "Attributes of Invoice" })).toBeTruthy();

    await act(async () => {
      await userEvent.click(within(editor).getByRole("tab", { name: "Relationships" }));
    });
    await act(async () => {
      await userEvent.click(screen.getByTestId("follow-selection"));
    });
    expect(screen.getByTestId("follow-selection").getAttribute("aria-pressed")).toBe("true");
    act(() => store.getState().select([IDS.payment]));
    const payment = await screen.findByRole("region", { name: "Editor: Payment" }, { timeout: 5000 });
    expect(within(payment).getByRole("tab", { name: "Relationships" }).getAttribute("aria-selected")).toBe("true");

    // An edit marks the tab dirty.
    const name = within(payment).getByLabelText("Name");
    await act(async () => {
      await userEvent.type(name, "X");
    });
    await waitFor(() => expect(within(screen.getByTestId("editor-tabs")).getByTestId("editor-tab").dataset.dirty).toBe("true"));

    // A middle click closes the tab; the screen shows again.
    const tab = within(screen.getByTestId("editor-tabs")).getByTestId("editor-tab");
    act(() => {
      fireEvent(tab, new MouseEvent("auxclick", { bubbles: true, button: 1 }));
    });
    await waitFor(() => expect(screen.queryByTestId("editor-area")).toBeNull());
    expect(store.getState().editors.tabs).toHaveLength(0);
  });

  it("opens the relationship, enum, value object and custom type editors on the same frame", async () => {
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    const store = api.services.store;
    await waitFor(() => expect(api.services.queryClient.getQueryData(["index"])).toBeTruthy(), { timeout: 5000 });
    const rows = api.services.queryClient.getQueryData<ElementSummary[]>(["index"])!;
    const main: Record<string, string> = { relation: "Attributes", enum: "Members", "value-object": "Attributes", "scalar-type": "Definition" };
    const seen: string[] = [];
    for (const kind of ["relation", "enum", "value-object", "scalar-type"] as const) {
      const row = rows.find((r) => r.kind === kind && r.name);
      if (!row) continue;
      seen.push(kind);
      act(() => store.getState().updateEditors((s) => openTab(s, { id: row.id, kind }, { pin: true })));
      const editor = await screen.findByRole("region", { name: `Editor: ${row.name}` }, { timeout: 5000 });
      expect(editor.dataset.kind).toBe(kind);
      expect(within(editor).getByRole("tab", { name: main[kind] })).toBeTruthy();
      expect(within(editor).getByRole("tab", { name: "References" })).toBeTruthy();
      expect(within(editor).getByLabelText("Name")).toBeTruthy();
    }
    expect(seen).toEqual(expect.arrayContaining(["relation", "enum"]));
    expect(store.getState().editors.tabs).toHaveLength(seen.length);
  });
});
