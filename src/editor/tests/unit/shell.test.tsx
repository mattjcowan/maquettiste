// The shell renders over the mock API: rail, explorer (grouped, filterable), inspector, bottom panel.
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { App } from "@/app/App";
import { emptyFilter, isFiltering, matches, matchingIds } from "@/explorer/filter";
import type { ElementSummary } from "@/api/types";
import { useMockApi } from "./harness";

beforeAll(() => {
  // jsdom lacks layout; the virtualizer and splitters only need these to exist.
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

const row = (over: Partial<ElementSummary>): ElementSummary =>
  ({ id: "X", kind: "entity", name: "X", package: null, tags: [], category: null, stereotypes: [], path: "", hash: "", ...over }) as ElementSummary;

describe("explorer filter", () => {
  const rows = [
    row({ id: "P", kind: "package", name: "Billing" }),
    row({ id: "A", name: "Invoice", package: "P", tags: ["billing"], category: "C1", stereotypes: ["S1"] }),
    row({ id: "B", name: "Customer", package: "P", tags: ["pii"] }),
  ];

  it("filters by text, tag, category and stereotype", () => {
    expect(matches(rows[1], { ...emptyFilter, text: "inv" })).toBe(true);
    expect(matches(rows[2], { ...emptyFilter, text: "inv" })).toBe(false);
    expect(matches(rows[2], { ...emptyFilter, tags: ["pii"] })).toBe(true);
    expect(matches(rows[2], { ...emptyFilter, categories: ["C1"] })).toBe(false);
    expect(matches(rows[1], { ...emptyFilter, stereotypes: ["S1"] })).toBe(true);
  });

  it("says whether a filter is set and lists the matching ids", () => {
    expect(isFiltering(emptyFilter)).toBe(false);
    expect(isFiltering({ ...emptyFilter, text: "  " })).toBe(false);
    expect(isFiltering({ ...emptyFilter, tags: ["pii"] })).toBe(true);
    expect(matchingIds(rows, { ...emptyFilter, text: "i" })).toEqual(["P", "A"]);
  });
});

describe("shell", () => {
  const api = useMockApi();

  it("renders the regions and lists the model in the explorer", async () => {
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    const explorer = await screen.findByRole("complementary", { name: "Explorer" });
    // One explorer at a time, under a header that names it; domains start collapsed.
    expect(within(explorer).getByRole("heading", { name: "Domain model" })).toBeTruthy();
    await waitFor(() => expect(within(explorer).getByRole("tree", { name: "Domain model" })).toBeTruthy(), { timeout: 5000 });
    await act(async () => {
      await userEvent.type(within(explorer).getByLabelText("Search the model"), "Invoice");
    });
    await waitFor(() => expect(within(explorer).getByTestId("explorer-row-Invoice")).toBeTruthy(), { timeout: 5000 });
    expect(screen.getByRole("navigation", { name: "Explorers" })).toBeTruthy();
    expect(screen.getByRole("complementary", { name: "Inspector" })).toBeTruthy();
    expect(screen.getByTestId("bottom-panel")).toBeTruthy();
    expect(document.querySelectorAll("[data-region]").length).toBeGreaterThanOrEqual(5);
  });

  it("selects an element from the explorer into the inspector and the URL", async () => {
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    const explorer = await screen.findByRole("complementary", { name: "Explorer" });
    await within(explorer).findByRole("tree", { name: "Domain model" }, { timeout: 5000 });
    await act(async () => {
      await userEvent.type(within(explorer).getByLabelText("Search the model"), "Invoice");
    });
    const invoice = await within(explorer).findByTestId("explorer-row-Invoice", undefined, { timeout: 5000 });
    await act(async () => {
      await userEvent.click(invoice);
    });
    await waitFor(() => expect(api.services.store.getState().selection).toHaveLength(1));
    expect(window.location.search).toContain("sel=");
    const inspector = screen.getByRole("complementary", { name: "Inspector" });
    await waitFor(() => expect(within(inspector).getByDisplayValue("Invoice")).toBeTruthy(), { timeout: 5000 });
  });
});
