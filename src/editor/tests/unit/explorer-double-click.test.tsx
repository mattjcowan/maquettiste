// A double click on an explorer row (explorer-redesign.md 3.6): the first click selects the row, the tree re-renders
// with the new selection, and the second click still opens the element pinned, as Enter does; a folder or a domain
// row toggles once. The test dispatches the two clicks with the selection's re-render in between, as a user does.
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeAll, describe, expect, it } from "vitest";
import { App } from "@/app/App";
import { DOUBLE_CLICK_MS } from "@/explorer/Explorer";
import { buildForest, revealPath } from "@/explorer/tree";
import { IDS, useMockApi } from "./harness";

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

describe("explorer double click", () => {
  const api = useMockApi();
  afterEach(() => localStorage.clear());

  async function tree() {
    window.history.replaceState(null, "", "/generate");
    // Invoice's domain expanded, Invoice not selected.
    const forest = buildForest({ rows: api.backend.model.index() });
    const expanded = api.services.store.getState().explorer.views["domain-model"].expanded;
    for (const key of [...revealPath(forest, IDS.invoice), ...revealPath(forest, IDS.payment)]) expanded.add(key);
    render(<App services={api.services} />);
    const explorer = await screen.findByRole("complementary", { name: "Explorer" });
    return within(explorer).findByRole("tree", { name: "Domain model" }, { timeout: 5000 });
  }

  /** Two clicks on the row with the given test id, the first one's re-render settled before the second. */
  async function doubleClick(scope: HTMLElement, testId: string, dblclick: boolean) {
    await act(async () => {
      fireEvent.click(within(scope).getByTestId(testId), { detail: 1 });
    });
    // The second click finds the row again: the first may have re-rendered or re-mounted it.
    await act(async () => {
      const row = within(scope).getByTestId(testId);
      fireEvent.click(row, { detail: 2 });
      if (dblclick) fireEvent.dblClick(row, { detail: 2 });
    });
  }

  const pinnedInvoice = () => {
    const { editors } = api.services.store.getState();
    const tab = editors.tabs.find((t) => t.id === IDS.invoice);
    return !!tab && tab.pinned && editors.active === tab.key;
  };

  it("opens an unselected entity in a pinned editor tab", async () => {
    const scope = await tree();
    await within(scope).findByTestId("explorer-row-Invoice", {}, { timeout: 5000 });
    expect(api.services.store.getState().selection).not.toContain(IDS.invoice);
    await doubleClick(scope, "explorer-row-Invoice", true);
    await waitFor(() => expect(pinnedInvoice()).toBe(true));
    expect(api.services.store.getState().selection).toEqual([IDS.invoice]);
    expect(api.services.store.getState().editors.tabs.filter((t) => t.id === IDS.invoice)).toHaveLength(1);
    expect(await screen.findByRole("region", { name: "Editor: Invoice" })).toBeTruthy();
  });

  it("opens it even when the browser's dblclick does not reach the row", async () => {
    const scope = await tree();
    await within(scope).findByTestId("explorer-row-Invoice", {}, { timeout: 5000 });
    await doubleClick(scope, "explorer-row-Invoice", false);
    await waitFor(() => expect(pinnedInvoice()).toBe(true));
  });

  it("opens the first row when the tree moved another row under the pointer between the clicks", async () => {
    const scope = await tree();
    await within(scope).findByTestId("explorer-row-Payment", {}, { timeout: 5000 });
    const at = { clientX: 40, clientY: 120 };
    await act(async () => {
      fireEvent.click(within(scope).getByTestId("explorer-row-Invoice"), { ...at, detail: 1 });
    });
    // The browser counts the second click as a double click at the same place; the row there is now Payment.
    await act(async () => {
      const moved = within(scope).getByTestId("explorer-row-Payment");
      fireEvent.click(moved, { ...at, detail: 2 });
      fireEvent.dblClick(moved, { ...at, detail: 2 });
    });
    await waitFor(() => expect(pinnedInvoice()).toBe(true));
    expect(api.services.store.getState().editors.tabs.some((t) => t.id === IDS.payment)).toBe(false);
  });

  it("opens the first row when something that is not a row moved under the pointer between the clicks", async () => {
    const scope = await tree();
    await within(scope).findByTestId("explorer-row-Invoice", {}, { timeout: 5000 });
    const at = { clientX: 40, clientY: 120 };
    await act(async () => {
      fireEvent.click(within(scope).getByTestId("explorer-row-Invoice"), { ...at, detail: 1 });
    });
    // The strip above the tree grew and pushed the tree down: the second click lands on a strip button, not a row.
    const explorer = scope.closest("section") as HTMLElement;
    const inTheWay = document.createElement("button");
    inTheWay.textContent = "Recent: Invoice";
    let reached = false;
    inTheWay.addEventListener("click", () => (reached = true));
    explorer.insertBefore(inTheWay, explorer.firstChild);
    await act(async () => {
      fireEvent.click(inTheWay, { ...at, detail: 2 });
    });
    await waitFor(() => expect(pinnedInvoice()).toBe(true));
    expect(await screen.findByRole("region", { name: "Editor: Invoice" })).toBeTruthy();
    // The strip's own click did not run: the explorer handled the second click.
    expect(reached).toBe(false);
    inTheWay.remove();
  });

  it("treats a later click on something else as a plain click", async () => {
    const scope = await tree();
    await within(scope).findByTestId("explorer-row-Invoice", {}, { timeout: 5000 });
    await act(async () => {
      fireEvent.click(within(scope).getByTestId("explorer-row-Invoice"), { clientX: 40, clientY: 120, detail: 1 });
    });
    await act(async () => {
      fireEvent.click(scope, { clientX: 40, clientY: 300, detail: 1 });
    });
    expect(api.services.store.getState().editors.tabs.some((t) => t.id === IDS.invoice && t.pinned)).toBe(false);
  });

  it("does not add a selected row to the recent list", async () => {
    const scope = await tree();
    await within(scope).findByTestId("explorer-row-Invoice", {}, { timeout: 5000 });
    // Past the double-click window of the previous test's clicks (module state), so this is a single click.
    await new Promise((r) => setTimeout(r, DOUBLE_CLICK_MS));
    const before = api.services.store.getState().recent;
    await act(async () => {
      fireEvent.click(within(scope).getByTestId("explorer-row-Invoice"), { detail: 1 });
    });
    expect(api.services.store.getState().selection).toEqual([IDS.invoice]);
    expect(api.services.store.getState().recent).toBe(before);
  });

  it("toggles a domain row once", async () => {
    const scope = await tree();
    const domain = await waitFor(() => {
      const row = scope.querySelector<HTMLElement>('[role="treeitem"][data-type="domain"]');
      expect(row).toBeTruthy();
      return row!;
    });
    const testId = domain.getAttribute("data-testid")!;
    const before = domain.getAttribute("aria-expanded");
    await doubleClick(scope, testId, true);
    await waitFor(() => expect(within(scope).getByTestId(testId).getAttribute("aria-expanded")).toBe(before === "true" ? "false" : "true"));
  });
});
