// The guard behind the owner's rule (tooltips on icon buttons): the shell, the explorers, the editors, the inspector,
// the settings tabs and the Generate screen render over the mock API, and every button without visible text must
// carry a title and an accessible name.
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { beforeAll, describe, expect, it } from "vitest";
import { App } from "@/app/App";
import { openTab } from "@/editors/tabs";
import type { ElementSummary } from "@/api/types";
import { scanIconControls } from "../icon-controls";
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

const settle = () => act(() => new Promise((r) => setTimeout(r, 150)));

describe("icon-only controls", () => {
  const api = useMockApi();

  async function renderAt(path: string) {
    window.history.replaceState(null, "", path);
    render(<App services={api.services} />);
    await waitFor(() => expect(api.services.queryClient.getQueryData(["index"])).toBeTruthy(), { timeout: 5000 });
    await settle();
  }

  // Every screen is scanned before the assertion so one run lists every unlabelled control.
  const missing = new Set<string>();
  const expectLabelled = (where: string) => {
    const scan = scanIconControls(document);
    for (const m of scan.missing) missing.add(`${where}: ${m}`);
    return scan.checked;
  };

  it("labels every icon button on the screens, the explorers and the settings tabs", async () => {
    let total = 0;
    await renderAt("/generate");
    const store = api.services.store;
    for (const view of ["domain-model", "reference-data", "databases", "diagrams", "generate"] as const) {
      act(() => store.getState().setSidebar(view));
      await settle();
      total += expectLabelled(`explorer ${view}`);
    }
    for (const path of ["/entities", "/reference-data", "/database", "/mappings", "/generate"]) {
      window.history.pushState(null, "", path);
      act(() => window.dispatchEvent(new PopStateEvent("popstate")));
      await settle();
      total += expectLabelled(path);
    }
    for (const tab of ["general", "tags", "categories", "stereotypes", "conventions", "locales", "validation", "project", "explorer"]) {
      window.history.pushState(null, "", `/settings/${tab}`);
      act(() => window.dispatchEvent(new PopStateEvent("popstate")));
      await settle();
      total += expectLabelled(`settings ${tab}`);
    }
    expect([...missing]).toEqual([]);
    console.log(`icon-only controls checked on the screens: ${total}`);
    expect(total).toBeGreaterThan(20);
  });

  it("labels every icon button on the editors and the inspector", async () => {
    await renderAt("/generate");
    const store = api.services.store;
    const rows = api.services.queryClient.getQueryData<ElementSummary[]>(["index"])!;
    act(() => store.getState().updateEditors((s) => openTab(s, { id: IDS.invoice, kind: "entity" }, { pin: true })));
    act(() => store.getState().select([IDS.invoice]));
    const editor = await screen.findByRole("region", { name: "Editor: Invoice" }, { timeout: 5000 });
    await settle();
    let total = expectLabelled("entity editor");
    for (const tab of ["Relationships", "Indexes", "Mappings", "Seed data", "Code generation", "References"]) {
      const el = within(editor).queryByRole("tab", { name: tab });
      if (!el) continue;
      act(() => el.click());
      await settle();
      total += expectLabelled(`entity editor ${tab}`);
    }
    for (const kind of ["relation", "enum", "value-object", "scalar-type", "package"] as const) {
      const row = rows.find((r) => r.kind === kind && r.name);
      if (!row) continue;
      act(() => store.getState().updateEditors((s) => openTab(s, { id: row.id, kind }, { pin: true })));
      act(() => store.getState().select([row.id]));
      await settle();
      total += expectLabelled(`${kind} editor`);
    }
    expect([...missing]).toEqual([]);
    console.log(`icon-only controls checked on the editors: ${total}`);
    expect(total).toBeGreaterThan(10);
  });
});
