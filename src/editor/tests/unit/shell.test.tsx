// The shell renders over the mock API: rail, explorer (grouped, filterable), inspector, bottom panel.
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { App } from "@/app/App";
import { emptyFilter, explorerRows, matches } from "@/explorer/filter";
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

  it("groups rows by package and hides a collapsed group's rows", () => {
    const open = explorerRows(rows, emptyFilter, new Set());
    expect(open.filter((r) => r.type === "element").length).toBeGreaterThanOrEqual(2);
    const group = open.find((r) => r.type === "group")!;
    expect(group.label).toBe("Billing");
    const closed = explorerRows(rows, emptyFilter, new Set([group.key.replace(/^group:/, "")]));
    expect(closed.length).toBeLessThan(open.length);
  });
});

describe("shell", () => {
  const api = useMockApi();

  it("renders the regions and lists the model in the explorer", async () => {
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    const explorer = await screen.findByRole("complementary", { name: "Explorer" });
    await waitFor(() => expect(within(explorer).getByText("Invoice")).toBeTruthy(), { timeout: 5000 });
    expect(screen.getByRole("navigation", { name: "Workspaces" })).toBeTruthy();
    expect(screen.getByRole("complementary", { name: "Inspector" })).toBeTruthy();
    expect(screen.getByTestId("bottom-panel")).toBeTruthy();
    expect(document.querySelectorAll("[data-region]").length).toBeGreaterThanOrEqual(5);
  });

  it("selects an element from the explorer into the inspector and the URL", async () => {
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    const explorer = await screen.findByRole("complementary", { name: "Explorer" });
    const invoice = await within(explorer).findByText("Invoice", undefined, { timeout: 5000 });
    await act(async () => {
      await userEvent.click(invoice);
    });
    await waitFor(() => expect(api.services.store.getState().selection).toHaveLength(1));
    expect(window.location.search).toContain("sel=");
    const inspector = screen.getByRole("complementary", { name: "Inspector" });
    await waitFor(() => expect(within(inspector).getByDisplayValue("Invoice")).toBeTruthy(), { timeout: 5000 });
  });
});
