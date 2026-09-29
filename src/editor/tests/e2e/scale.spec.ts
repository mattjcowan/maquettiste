// The Playwright `scale` project (explorer-redesign.md 4.5 and 5): opens ?mock=large, the
// 5,000-entity model from scripts/gen-scale-model.mjs, and checks the targets today's explorer can
// be measured against: explorer open (cold and warm index), search keystroke to rows, and expand or
// collapse of a group with thousands of rows. Each budget is the target plus the mock's own recorded
// time for that measure (window.__mqPerf.mock), plus 25 % for machine noise. Every timing is
// attached to the report as scale-timings.json and as annotations. Targets that need parts of the
// redesign not built yet are listed as skipped tests that say what they wait for.
import type { Page, TestInfo } from "@playwright/test";
import { expect, test } from "./fixtures";

interface MockPerf {
  scenario: string;
  elements: number;
  seed: { fetchMs: number; parseMs: number; bytes: number } | null;
  backendMs: number;
  requests: { method: string; path: string; status: number; ms: number }[];
}

interface Measure {
  name: string;
  ms: number;
  mockMs: number;
  targetMs: number;
  budgetMs: number;
  detail?: Record<string, unknown>;
}

const NOISE = 1.25;
const measures: Measure[] = [];

function record(info: TestInfo, measure: Omit<Measure, "budgetMs">): Measure {
  const full = { ...measure, budgetMs: Math.round((measure.targetMs + measure.mockMs) * NOISE) };
  measures.push(full);
  info.annotations.push({
    type: "timing",
    description: `${full.name}: ${full.ms.toFixed(1)} ms (target ${full.targetMs} ms, mock ${full.mockMs.toFixed(0)} ms, budget ${full.budgetMs} ms)`,
  });
  return full;
}

/** MQ_SCALE_STRICT=1 turns the known misses below into failures too. */
const strict = process.env.MQ_SCALE_STRICT === "1";

/**
 * Checks a measure against its budget. A known miss (today's explorer, before the redesign) is
 * reported as an annotation instead of failing, unless MQ_SCALE_STRICT=1.
 */
function within(info: TestInfo, measure: Measure, knownMiss?: string): void {
  const over = measure.ms > measure.budgetMs;
  if (over && knownMiss && !strict) {
    info.annotations.push({
      type: "known miss",
      description: `${measure.name}: ${measure.ms.toFixed(1)} ms over the ${measure.budgetMs} ms budget. ${knownMiss}`,
    });
    return;
  }
  expect(measure.ms, `${measure.name} took ${measure.ms.toFixed(1)} ms; budget ${measure.budgetMs} ms`).toBeLessThanOrEqual(measure.budgetMs);
}

const TOGGLE_MISS =
  "Today's explorer rebuilds and re-sorts every row of the index (explorerRows) on each toggle; the tree model of section 4.3 patches one node.";

/** Marks the first moment the explorer shows an element row (a MutationObserver installed before any script runs). */
async function watchExplorer(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const w = window as unknown as { __mqScale?: { explorerReady?: number } };
    w.__mqScale = {};
    const check = () => {
      if (w.__mqScale!.explorerReady === undefined && document.querySelector('[data-testid="explorer-list"] [data-testid^="explorer-row-"]')) {
        w.__mqScale!.explorerReady = performance.now();
        observer.disconnect();
      }
    };
    const observer = new MutationObserver(check);
    document.addEventListener("DOMContentLoaded", () => observer.observe(document.body, { childList: true, subtree: true }));
  });
}

async function openLarge(page: Page, reload = false): Promise<{ readyMs: number; mock: MockPerf }> {
  if (reload) await page.reload();
  else await page.goto("/?mock=large");
  await expect(page.getByTestId("shell")).toBeVisible({ timeout: 60_000 });
  const missing = page.getByTestId("mock-large-missing");
  if (await missing.isVisible()) {
    // In CI a missing seed is a failure: skipping would let the step go green having measured nothing.
    if (process.env.CI) throw new Error("large.json.gz missing: run npm run gen:scale");
    test.skip(true, "The large mock model has not been generated: run `npm run gen:scale` in src/editor first.");
  }
  await page.waitForFunction(() => (window as unknown as { __mqScale?: { explorerReady?: number } }).__mqScale?.explorerReady !== undefined, null, {
    timeout: 60_000,
  });
  const readyMs = await page.evaluate(() => (window as unknown as { __mqScale: { explorerReady: number } }).__mqScale.explorerReady);
  const mock = await page.evaluate(() => (window as unknown as { __mqPerf: { mock: MockPerf } }).__mqPerf.mock);
  expect(mock.scenario).toBe("large");
  return { readyMs, mock };
}

const indexRequest = (mock: MockPerf) => mock.requests.find((r) => r.path === "/api/model/index");

/** The mock's share of an open: seed fetch and parse, backend start and the index handler. */
const openMockMs = (mock: MockPerf) => (mock.seed ? mock.seed.fetchMs + mock.seed.parseMs : 0) + mock.backendMs + (indexRequest(mock)?.ms ?? 0);

/**
 * One interaction measured in the page: from just before `act` until its update has been rendered
 * and laid out (the next macrotask after React's flush, then a forced layout), with the long tasks
 * that overlapped it.
 */
async function interact(page: Page, act: string, arg: string, done: string): Promise<{ ms: number; longTasks: number }> {
  return page.evaluate(
    async ({ act, arg, done }) => {
      const longTasks: number[] = [];
      const observer = new PerformanceObserver((list) => list.getEntries().forEach((e) => longTasks.push(e.duration)));
      observer.observe({ type: "longtask", buffered: false });
      const tick = () =>
        new Promise<void>((resolve) => {
          const channel = new MessageChannel();
          channel.port1.onmessage = () => resolve();
          channel.port2.postMessage(null);
        });
      const run = new Function("arg", act) as (arg: string) => void;
      const finished = new Function("arg", done) as (arg: string) => boolean;
      const start = performance.now();
      run(arg);
      for (let i = 0; i < 50 && !finished(arg); i++) await tick();
      void document.body.offsetHeight;
      const ms = performance.now() - start;
      if (!finished(arg)) throw new Error(`The interaction did not finish: ${done}`);
      await new Promise((r) => setTimeout(r, 60));
      observer.disconnect();
      return { ms, longTasks: longTasks.length };
    },
    { act, arg, done },
  );
}

const TYPE = `
  const input = document.querySelector('input[aria-label="Filter elements by name"]');
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
  setter.call(input, arg);
  input.dispatchEvent(new Event("input", { bubbles: true }));
`;
const FILTERED = `
  const first = document.querySelector('[data-testid="explorer-list"] [data-testid^="explorer-row-"]');
  const input = document.querySelector('input[aria-label="Filter elements by name"]');
  return input.value === arg && (!first || first.textContent.toLowerCase().includes(arg.toLowerCase()));
`;
const groupRow = (label: string) =>
  `[...document.querySelectorAll('[data-testid="explorer-list"] [role="option"]:not([data-testid])')].find((r) => r.querySelector("span.flex-1")?.textContent === ${JSON.stringify(label)})`;
const TOGGLE = (label: string) => `${groupRow(label)}.click();`;
const STATE = (label: string, state: "collapsed" | "expanded") =>
  `return ${groupRow(label)}?.querySelector(".sr-only")?.textContent === ${JSON.stringify(state)};`;

test.describe("explorer at scale (?mock=large)", () => {
  test.describe.configure({ mode: "serial", timeout: 180_000 });

  // eslint-disable-next-line no-empty-pattern -- Playwright needs the fixtures argument to reach testInfo
  test.afterAll(async ({}, info) => {
    if (measures.length === 0) return;
    await info.attach("scale-timings.json", { body: JSON.stringify(measures, null, 2), contentType: "application/json" });
    console.log(measures.map((m) => `${m.name}: ${m.ms.toFixed(1)} ms (budget ${m.budgetMs} ms)${m.ms > m.budgetMs ? " OVER BUDGET" : ""}`).join("\n"));
  });

  test("editor open, cold then warm index -> explorer usable", async ({ page }, info) => {
    await watchExplorer(page);
    const cold = await openLarge(page);
    expect(cold.mock.elements).toBeGreaterThan(20_000);
    const coldMeasure = record(info, {
      name: "open, cold index",
      ms: cold.readyMs,
      mockMs: openMockMs(cold.mock),
      targetMs: 3000,
      detail: { index: indexRequest(cold.mock), seed: cold.mock.seed, backendMs: cold.mock.backendMs, elements: cold.mock.elements },
    });

    const warm = await openLarge(page, true);
    const index = indexRequest(warm.mock);
    const warmMeasure = record(info, {
      name: index?.status === 304 ? "open, warm index (304)" : `open, reload (index answered ${index?.status ?? "nothing"}, not 304)`,
      ms: warm.readyMs,
      mockMs: openMockMs(warm.mock),
      targetMs: index?.status === 304 ? 1500 : 3000,
      detail: { index, seed: warm.mock.seed, backendMs: warm.mock.backendMs },
    });
    within(info, coldMeasure);
    within(info, warmMeasure);
  });

  test("search keystroke -> filtered rows painted", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    const samples: number[] = [];
    let longTasks = 0;
    for (const text of ["l", "lo", "loo", "look", "looku", "lookup", "lookup1", "a", "ac", "acc", "", "order", ""]) {
      const r = await interact(page, TYPE, text, FILTERED);
      samples.push(r.ms);
      longTasks += r.longTasks;
    }
    const sorted = [...samples].sort((a, b) => a - b);
    const p95 = sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * 0.95) - 1)];
    within(info, record(info, { name: "search keystroke p95", ms: p95, mockMs: 0, targetMs: 50, detail: { samples, longTasks } }));
  });

  test("expand and collapse the largest group", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    // Today's explorer groups rows by package, so its largest group holds a package's entities and
    // relations (hundreds of rows); the 5,000-child node of the target needs the redesign's kind
    // folders (listed below as pending). Collapse every group, then time the largest one.
    const list = page.getByTestId("explorer-list");
    const groups = list.locator('[role="option"]:not([data-testid])');
    for (let i = 0; i < 400; i++) {
      const open = groups.filter({ hasText: "expanded" }).first();
      if ((await open.count()) === 0) break;
      await open.click();
    }
    const sizes = await groups.evaluateAll((rows) =>
      rows.map((r) => ({ label: r.querySelector("span.flex-1")?.textContent ?? "", count: Number(r.querySelector("span.font-normal")?.textContent ?? "0") })),
    );
    const largest = sizes.reduce((a, b) => (b.count > a.count ? b : a));
    expect(largest.count).toBeGreaterThan(300);

    const expand = await interact(page, TOGGLE(largest.label), "", STATE(largest.label, "expanded"));
    const collapse = await interact(page, TOGGLE(largest.label), "", STATE(largest.label, "collapsed"));
    const detail = {
      group: largest.label,
      children: largest.count,
      groups: sizes.length,
      expandLongTasks: expand.longTasks,
      collapseLongTasks: collapse.longTasks,
    };
    const e = record(info, { name: `expand a ${largest.count}-row group`, ms: expand.ms, mockMs: 0, targetMs: 16, detail });
    const c = record(info, { name: `collapse a ${largest.count}-row group`, ms: collapse.ms, mockMs: 0, targetMs: 16, detail });
    within(info, e, TOGGLE_MISS);
    within(info, c, TOGGLE_MISS);
  });

  // Targets of explorer-redesign.md 4.5 that wait for parts of the redesign.
  const pending: [string, string][] = [
    [
      "expand or collapse a node with 5,000 children",
      "needs the kind folders of section 1.1 (today's explorer groups by package, so no group reaches 5,000 rows)",
    ],
    ["index arrives -> first paint, per stage", "needs the performance.mark stages and window.__mqPerf of section 5 item 7 (explorer worker, tree build)"],
    ["search worker ready <= 500 ms after the index", "needs the search worker (section 3.1)"],
    ["table summaries (E5c) off the critical path", "the explorer does not call GET /api/databases/{id}/tables yet"],
    ["scroll the fully expanded tree at 60 fps", "needs the virtualized tree of section 4.3"],
    ["select on canvas -> row revealed <= 50 ms", "needs the tree reveal of section 3.5"],
    ["model.changed -> tree and search updated <= 16 ms", "needs the E5d patching of section 4.4 in the editor"],
    ["300-node diagram open -> first paint <= 2 s", "needs the batched reads (E5b) in the diagram loader"],
    ["300-node diagram pan and zoom, ELK auto-layout", "needs the diagram measurements of section 5 item 7"],
    ["memory <= 150 MB (measureUserAgentSpecificMemory)", "needs a cross-origin isolated preview (COOP/COEP headers) and the worker split"],
  ];
  for (const [name, reason] of pending) {
    test(name, () => {
      test.skip(true, `Not measured yet: ${reason}.`);
    });
  }
});
