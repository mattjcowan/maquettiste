// The explorer tree's ARIA after a scroll (explorer-redesign.md 6): the virtualized rows keep aria-level,
// aria-setsize and aria-posinset of the whole tree, not of the rendered window, and the tree is multiselectable.
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeAll, describe, expect, it } from "vitest";
import { App } from "@/app/App";
import { buildForest, childKeys, visibleRows } from "@/explorer/tree";
import { useMockApi } from "./harness";

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
  Object.defineProperty(HTMLElement.prototype, "offsetHeight", { configurable: true, get: () => 600 });
  Object.defineProperty(HTMLElement.prototype, "offsetWidth", { configurable: true, get: () => 300 });
});

describe("explorer tree ARIA", () => {
  const api = useMockApi({ scenarios: ["medium"] });
  afterEach(() => localStorage.clear());

  it("keeps level, set size and position of the whole tree after a scroll", async () => {
    window.history.replaceState(null, "", "/generate");
    // Everything in the Domain model explorer expanded, so the tree is far taller than its window.
    const forest = buildForest({ rows: api.backend.model.index() });
    const expanded = api.services.store.getState().explorer.views["domain-model"].expanded;
    const open = (key: string) => {
      for (const child of childKeys(forest, key)) {
        if (childKeys(forest, child).length) {
          expanded.add(child);
          open(child);
        }
      }
    };
    open(forest.roots["domain-model"]);
    const all = visibleRows(forest, "domain-model", expanded);
    expect(all.length).toBeGreaterThan(200);

    render(<App services={api.services} />);
    const explorer = await screen.findByRole("complementary", { name: "Explorer" });
    const tree = await within(explorer).findByRole("tree", { name: "Domain model" }, { timeout: 5000 });
    expect(tree.getAttribute("aria-multiselectable")).toBe("true");
    await waitFor(() => expect(within(tree).getAllByRole("treeitem").length).toBeGreaterThan(0), { timeout: 5000 });

    // The tree element is the virtualizer's scroll container.
    const target = tree;
    await act(async () => {
      target.scrollTop = 150 * 28;
      fireEvent.scroll(target);
    });
    const items = await waitFor(() => {
      const shown = within(tree).getAllByRole("treeitem");
      expect(Number(shown[0].getAttribute("aria-posinset")) + Number(shown[0].getAttribute("aria-level"))).toBeGreaterThan(2);
      return shown;
    });

    const byKey = new Map(all.map((r, i) => [r.key, { ...r, at: i }]));
    for (const item of items) {
      const key = item.getAttribute("data-key")!;
      const row = byKey.get(key);
      expect(row, key).toBeTruthy();
      const level = Number(item.getAttribute("aria-level"));
      const pos = Number(item.getAttribute("aria-posinset"));
      const size = Number(item.getAttribute("aria-setsize"));
      expect(level).toBe(row!.depth + 1);
      // The siblings of the row in the whole tree, not in the rendered window.
      const parent = all
        .slice(0, row!.at)
        .reverse()
        .find((r) => r.depth === row!.depth - 1);
      const siblings = childKeys(forest, parent ? parent.key : forest.roots["domain-model"]);
      expect(size, key).toBe(siblings.length);
      expect(pos, key).toBe(siblings.indexOf(key) + 1);
    }
  }, 30_000);
});
