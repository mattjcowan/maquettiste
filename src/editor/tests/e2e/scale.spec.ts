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

/** window.__mqPerf.editor (src/lib/perf.ts). */
interface EditorEntry {
  name: string;
  at: number;
  ms: number;
  detail?: Record<string, unknown>;
}

const editorEntries = (page: Page) =>
  page.evaluate(() => (window as unknown as { __mqPerf: { editor?: { entries: EditorEntry[] } } }).__mqPerf.editor?.entries ?? []);

/** Waits for the search worker to take the index (the worker-ready measure is its own test). */
async function searchReady(page: Page, tables = false): Promise<void> {
  await expect.poll(async () => (await editorEntries(page)).some((e) => e.name === "search:ready"), { timeout: 30_000 }).toBe(true);
  if (tables)
    await expect
      .poll(() => page.evaluate(() => (window as unknown as { __mqPerf: { search?: { tablesPending: number } } }).__mqPerf.search?.tablesPending), {
        timeout: 60_000,
      })
      .toBe(0);
  // The forest rebuilt from the summaries has its folders built in idle time (explorer:prebuilt after the last build).
  if (tables)
    await expect
      .poll(async () => (await editorEntries(page)).filter((e) => e.name === "explorer:build" || e.name === "explorer:prebuilt").at(-1)?.name, {
        timeout: 30_000,
      })
      .toBe("explorer:prebuilt");
}

const BUILD_MISS = "The forest is built in one pass over every index row on the main thread (section 4.3); patching single changes is step 12.";
const MEMORY_MISS = "The mock backend and its seed live in the page; the worker split (section 5 item 4) moves them out.";

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

/** MQ_SCALE_STRICT=1 turns the known misses below, and the shared-runner misses, into failures too. */
const strict = process.env.MQ_SCALE_STRICT === "1";
/** A shared CI runner is too noisy to fail on a budget: there the numbers are recorded and an over-budget measure is
 * an annotation, never a failure (unless MQ_SCALE_STRICT=1). On a developer machine the budgets fail as before. */
const sharedRunner = !strict && Boolean(process.env.GITHUB_ACTIONS);

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
  if (over && sharedRunner) {
    info.annotations.push({
      type: "over budget on the shared runner",
      description: `${measure.name}: ${measure.ms.toFixed(1)} ms over the ${measure.budgetMs} ms budget on a shared CI runner; not a failure there.`,
    });
    return;
  }
  expect(measure.ms, `${measure.name} took ${measure.ms.toFixed(1)} ms; budget ${measure.budgetMs} ms`).toBeLessThanOrEqual(measure.budgetMs);
}

const CHANGE_MISS =
  "The index, tree and search patches take about 1 ms; the rest is the other panels (canvas, inspector, problems) re-rendering with the index in the same commit.";
const HIGHLIGHT_MISS = "The highlight set is computed and the rows re-render in the same commit as the selection's other panels.";
const REVEAL_MISS = "The reveal runs in an effect after the selection commit, then rebuilds the rows and scrolls in a second commit (section 3.5).";

/** Marks the first moment the explorer shows an element row (a MutationObserver installed before any script runs). */
async function watchExplorer(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const w = window as unknown as { __mqScale?: { explorerReady?: number } };
    w.__mqScale = {};
    const check = () => {
      if (w.__mqScale!.explorerReady === undefined && document.querySelector('[data-testid="explorer-tree"] [role="treeitem"]')) {
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
 * and laid out (the commit that makes `done` true, then a forced layout), with the long tasks
 * that overlapped it.
 */
async function interact(page: Page, act: string, arg: string, done: string): Promise<{ ms: number; longTasks: number }> {
  return page.evaluate(
    async ({ act, arg, done }) => {
      const longTasks: number[] = [];
      const observer = new PerformanceObserver((list) => list.getEntries().forEach((e) => longTasks.push(e.duration)));
      observer.observe({ type: "longtask", buffered: false });
      const run = new Function("arg", act) as (arg: string) => void;
      const finished = new Function("arg", done) as (arg: string) => boolean;
      const start = performance.now();
      run(arg);
      // Wait on the DOM, not by spinning tasks: a busy loop of ticks would compete with the search worker's answer,
      // which arrives as a task of its own. The observer runs right after the commit that finishes it; then a forced
      // layout. An update React flushes within the event finishes at once.
      if (!finished(arg))
        await new Promise<void>((resolve) => {
          const observer = new MutationObserver(() => {
            if (!finished(arg)) return;
            observer.disconnect();
            resolve();
          });
          observer.observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
          setTimeout(() => {
            observer.disconnect();
            resolve();
          }, 2000);
        });
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
  const input = document.querySelector('input[aria-label="Search the model"]');
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
  setter.call(input, arg);
  input.dispatchEvent(new Event("input", { bubbles: true }));
`;
// Painted: the tree (or its empty state) shows the worker's answer for this text, or the unfiltered tree for "".
const FILTERED = `
  const input = document.querySelector('input[aria-label="Search the model"]');
  const shown = document.querySelector('[data-testid="explorer-tree"], [data-testid="explorer-empty"]');
  const answered = shown ? shown.getAttribute("data-filter") : null;
  if (input.value !== arg) return false;
  if (arg === "") return answered === null;
  const first = document.querySelector('[data-testid="explorer-tree"] [data-testid^="explorer-row-"]');
  return answered === arg && (!first || first.textContent.toLowerCase().includes(arg.toLowerCase()));
`;
const QUICK_TYPE = `
  const input = document.querySelector('[data-testid="quick-open"] input');
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
  setter.call(input, arg);
  input.dispatchEvent(new Event("input", { bubbles: true }));
`;
const QUICK_ANSWERED = `
  const list = document.querySelector('[data-testid="quick-open"] [cmdk-list]');
  return !!list && list.getAttribute("data-answered") === arg;
`;
const groupRow = (key: string) => `document.querySelector('[data-testid="explorer-tree"] [data-key=${JSON.stringify(key)}]')`;
const TOGGLE = (key: string) => `${groupRow(key)}.click();`;
const STATE = (key: string, state: "collapsed" | "expanded") =>
  `return ${groupRow(key)}?.getAttribute("aria-expanded") === ${JSON.stringify(String(state === "expanded"))};`;

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
    // Steady state: the search keystroke is measured once the worker has the index and the table names.
    await searchReady(page, true);
    const samples: number[] = [];
    let longTasks = 0;
    // Twice through, so the p95 of 26 samples is not simply the slowest one (each answer waits about a frame: the
    // browser renders after the input before it hands over the worker's message).
    const texts = ["l", "lo", "loo", "look", "looku", "lookup", "lookup1", "a", "ac", "acc", "", "order", ""];
    for (const text of [...texts, ...texts]) {
      const r = await interact(page, TYPE, text, FILTERED);
      samples.push(r.ms);
      longTasks += r.longTasks;
    }
    const sorted = [...samples].sort((a, b) => a - b);
    const p95 = sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * 0.95) - 1)];
    within(info, record(info, { name: "search keystroke p95", ms: p95, mockMs: 0, targetMs: 50, detail: { samples, longTasks } }));
  });

  test("search worker ready <= 500 ms after the index", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    await searchReady(page);
    const entries = await editorEntries(page);
    const parse = entries.find((e) => e.name === "index:parse");
    const ready = entries.find((e) => e.name === "search:ready")!;
    const handoff = entries.find((e) => e.name === "search:handoff")!;
    const indexAt = parse ? parse.at + parse.ms : ready.at;
    const ms = ready.at + ready.ms - indexAt;
    within(info, record(info, { name: "search worker ready after the index", ms, mockMs: 0, targetMs: 500, detail: { ready, handoff } }));
    within(
      info,
      // The index's JSON text goes over in slices of 1 MB, one per task: the measure is the longest main-thread task of the
      // handoff (the detail has the slice count and the total).
      record(info, { name: "search worker handoff (main thread, longest task)", ms: handoff.ms, mockMs: 0, targetMs: 5, detail: handoff.detail }),
      "each 1 MB slice of the index text is one structured-clone copy on the main thread; a transferable buffer would avoid it",
    );
    // The whole main-thread cost of the handoff, every slice summed, keeps the handoff's original 5 ms target: slicing moved
    // the cost off any one task but did not remove it (section 4.5: the worker fetches the index, or takes a transferable).
    const totalMs = Number(handoff.detail?.totalMs ?? handoff.ms);
    within(
      info,
      record(info, { name: "search worker handoff (main thread, all slices)", ms: totalMs, mockMs: 0, targetMs: 5, detail: handoff.detail }),
      "the slices together still copy the whole index text through structured clone; the worker fetching the index, or a transferable buffer, is not built",
    );
  });

  test("quick open keystroke -> ranked results painted", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    await searchReady(page, true);
    await page.keyboard.press("Control+p");
    await expect(page.getByTestId("quick-open")).toBeVisible();
    const samples: number[] = [];
    let longTasks = 0;
    const texts = ["i", "in", "inv", "invo", "invoi", "invoic", "invoice", "invli", "o", "or", "ord", "order", "^lookup1", "~%line%"];
    for (const text of [...texts, ...texts]) {
      const r = await interact(page, QUICK_TYPE, text, QUICK_ANSWERED);
      samples.push(r.ms);
      longTasks += r.longTasks;
    }
    const sorted = [...samples].sort((a, b) => a - b);
    const p95 = sorted[Math.min(sorted.length - 1, Math.ceil(sorted.length * 0.95) - 1)];
    within(info, record(info, { name: "quick open keystroke p95", ms: p95, mockMs: 0, targetMs: 50, detail: { samples, longTasks } }));
  });

  test("expand and collapse the largest group", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    // The Domain model explorer starts with every domain collapsed. Open the top-level domains, then time the
    // largest kind folder they show (a domain's entities or relationships: hundreds of rows).
    const tree = page.getByTestId("explorer-tree");
    await tree.evaluate((el) => {
      Array.from(el.querySelectorAll<HTMLElement>('[role="treeitem"][aria-level="1"][data-type="domain"]'))
        .slice(0, 12)
        .forEach((row) => row.click());
    });
    await page.waitForTimeout(200);
    const sizes = await tree.locator('[role="treeitem"][data-type="folder"][aria-expanded="false"]').evaluateAll((rows) =>
      rows.map((r) => ({
        key: r.getAttribute("data-key") ?? "",
        label: r.textContent ?? "",
        count: Number((r.querySelector('[data-part="count"]')?.textContent ?? "0").replace(/,/g, "")),
      })),
    );
    expect(sizes.length).toBeGreaterThan(0);
    const largest = sizes.reduce((a, b) => (b.count > a.count ? b : a));
    expect(largest.count).toBeGreaterThan(100);

    const expand = await interact(page, TOGGLE(largest.key), "", STATE(largest.key, "expanded"));
    const collapse = await interact(page, TOGGLE(largest.key), "", STATE(largest.key, "collapsed"));
    const detail = {
      group: largest.label,
      children: largest.count,
      groups: sizes.length,
      expandLongTasks: expand.longTasks,
      collapseLongTasks: collapse.longTasks,
    };
    const e = record(info, { name: `expand a ${largest.count}-row folder`, ms: expand.ms, mockMs: 0, targetMs: 16, detail });
    const c = record(info, { name: `collapse a ${largest.count}-row folder`, ms: collapse.ms, mockMs: 0, targetMs: 16, detail });
    within(info, e);
    within(info, c);
  });

  test("index arrives -> explorer first paint, per stage", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    await page.waitForFunction(() =>
      ((window as unknown as { __mqPerf: { editor?: { entries: { name: string }[] } } }).__mqPerf.editor?.entries ?? []).some(
        (e) => e.name === "explorer:first-rows",
      ),
    );
    const entries = await editorEntries(page);
    const parse = entries.find((e) => e.name === "index:parse");
    const build = entries.find((e) => e.name === "explorer:build" && Number(e.detail?.elements ?? 0) > 0);
    const rows = entries.find((e) => e.name === "explorer:first-rows");
    expect(parse && build && rows, "the editor records index:parse, explorer:build and explorer:first-rows").toBeTruthy();
    const p = record(info, { name: "index parse", ms: parse!.ms, mockMs: 0, targetMs: 90, detail: parse!.detail });
    const b = record(info, { name: "explorer build", ms: build!.ms, mockMs: 0, targetMs: 30, detail: build!.detail });
    const r = record(info, { name: "explorer first rows", ms: rows!.ms, mockMs: 0, targetMs: 20, detail: rows!.detail });
    const total = record(info, { name: "index arrives -> first paint", ms: rows!.at + rows!.ms - parse!.at, mockMs: 0, targetMs: 150 });
    within(info, p);
    within(info, b, BUILD_MISS);
    within(info, r, BUILD_MISS);
    // The total is the sum of the two measures above, so it carries their known miss until they are within budget.
    within(info, total, BUILD_MISS);
  });

  test("table summaries (E5c) off the critical path", async ({ page }, info) => {
    await watchExplorer(page);
    const { readyMs } = await openLarge(page);
    await page.waitForFunction(
      () =>
        ((window as unknown as { __mqPerf: { editor?: { entries: { name: string }[] } } }).__mqPerf.editor?.entries ?? []).some(
          (e) => e.name === "tables:fetch",
        ),
      null,
      { timeout: 90_000 },
    );
    // Every database is read one after the other at idle priority; let the rest arrive.
    await page.waitForTimeout(3000);
    const entries = (await editorEntries(page)).filter((e) => e.name === "tables:fetch");
    const mock = await page.evaluate(() => (window as unknown as { __mqPerf: { mock: MockPerf } }).__mqPerf.mock);
    const mockTables = mock.requests.filter((q) => /^\/api\/databases\/[^/]+\/tables$/.test(q.path));
    expect(entries.length).toBeGreaterThan(0);
    // Off the critical path: the first read starts after the explorer showed its rows.
    expect(entries[0].at).toBeGreaterThanOrEqual(readyMs);
    // Timed in the editor, so each includes the transfer and the parse of the answer. The mock
    // does not keep one resolve per snapshot the way the server does, so every database is held to
    // the as-built first-call target (section 4.5), plus the mock's own handler time.
    for (const [i, e] of entries.entries()) {
      within(info, record(info, { name: `table summaries, database ${i + 1}`, ms: e.ms, mockMs: mockTables[i]?.ms ?? 0, targetMs: 2500, detail: e.detail }));
    }
  });

  test("memory, page and workers (measureUserAgentSpecificMemory)", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    expect(await page.evaluate(() => crossOriginIsolated), "the preview sends COOP and COEP").toBe(true);
    const bytes = await page.evaluate(async () => {
      const measure = (performance as unknown as { measureUserAgentSpecificMemory?: () => Promise<{ bytes: number }> }).measureUserAgentSpecificMemory;
      if (!measure) return "the API is missing";
      try {
        const result = await Promise.race([measure.call(performance), new Promise<null>((r) => setTimeout(() => r(null), 40_000))]);
        return result?.bytes ?? "no answer within 40 s";
      } catch (error) {
        // Some Chromium builds (the headless shell) refuse it even on an isolated page.
        return String(error);
      }
    });
    if (typeof bytes === "string") {
      info.annotations.push({ type: "memory", description: `not measured: ${bytes}` });
      return;
    }
    const mb = bytes / 1024 / 1024;
    info.annotations.push({ type: "memory", description: `page and workers: ${mb.toFixed(0)} MB (target 150 MB)` });
    measures.push({ name: "memory (MB, not ms)", ms: mb, mockMs: 0, targetMs: 150, budgetMs: 150 });
    if (mb > 150 && !strict) info.annotations.push({ type: "known miss", description: `memory: ${mb.toFixed(0)} MB over 150 MB. ${MEMORY_MISS}` });
    else expect(mb).toBeLessThanOrEqual(150);
  });

  test("model.changed for one element -> tree and search updated", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    // Open the first domain and its Entities folder, so the element's row is on screen.
    const tree = page.getByTestId("explorer-tree");
    const domain = tree.locator('[role="treeitem"][aria-level="1"][data-type="domain"]').first();
    await domain.click();
    const domainKey = (await domain.getAttribute("data-key"))!;
    const folder = tree.locator(`[role="treeitem"][data-key="${domainKey}/entity"]`);
    await folder.click();
    const row = tree.locator(`[role="treeitem"][data-type="element"][aria-level="3"]`).first();
    await expect(row).toBeVisible();
    const id = (await row.getAttribute("data-key"))!;
    // Let the first build's idle work, the table summaries and validation settle: the measure is the change alone.
    await page.waitForTimeout(3000);

    const measure = async (name: string) =>
      page.evaluate(
        async ({ id, name }) => {
          type Holder = {
            __mqMock: { model: { externalEdit(id: string, mutate: (json: Record<string, unknown>) => void): unknown } };
            __mqPerf: {
              search?: { updates?: number; updatedAt?: number };
              mock: { requests: { path: string; ms: number }[] };
              editor?: { entries: { name: string; at: number; ms: number; detail?: Record<string, unknown> }[] };
            };
          };
          const w = window as unknown as Holder;
          const updates = w.__mqPerf.search?.updates ?? 0;
          const requests = w.__mqPerf.mock.requests.length;
          let treeAt = 0;
          const shown = () =>
            (document.querySelector(`[data-testid="explorer-tree"] [data-key="${id}"]`)?.textContent ?? "").includes(name) &&
            (w.__mqPerf.search?.updates ?? 0) > updates;
          // The mock's own work (the write, hashing, the index row) runs before the clock starts; the event is then
          // delivered on a later task, as the hub delivers it.
          const mockStart = performance.now();
          w.__mqMock.model.externalEdit(id, (json) => void (json.name = name));
          const start = performance.now();
          // Done when both have happened: the row shows the name (a DOM change) and the search client has sent the
          // worker its update (no DOM change of its own, so it is also checked on a 1 ms poll).
          await new Promise<void>((resolve) => {
            const finish = () => {
              observer.disconnect();
              clearInterval(poll);
              clearTimeout(limit);
              resolve();
            };
            // The row shows the name at the commit that renders it; the search update is timed where it is sent.
            const observer = new MutationObserver(() => {
              if (!treeAt && (document.querySelector(`[data-testid="explorer-tree"] [data-key="${id}"]`)?.textContent ?? "").includes(name))
                treeAt = performance.now();
              if (shown()) finish();
            });
            observer.observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
            const poll = setInterval(() => shown() && finish(), 1);
            const limit = setTimeout(finish, 3000);
          });
          if (!shown()) throw new Error("The renamed row was not shown");
          // Both done; later tasks (the mock answering the refetches the change triggers) do not count.
          // The tree's commit is its layout effect (`explorer:commit`): the DOM is updated then, even when the rest of
          // the task (other panels' effects) delays the observer's callback.
          const commit = (w.__mqPerf.editor?.entries ?? []).find((e) => e.name === "explorer:commit" && e.at >= start);
          const ms = Math.max(commit?.at ?? treeAt, w.__mqPerf.search?.updatedAt ?? 0) - start;
          const taskMs = (treeAt || performance.now()) - start;
          const mockRequests = w.__mqPerf.mock.requests.slice(requests).map((r) => `${r.path} ${Math.round(r.ms)}`);
          const builds = (w.__mqPerf.editor?.entries ?? []).filter((e) => e.name === "explorer:build" && e.at >= mockStart);
          const all = (w.__mqPerf.editor?.entries ?? [])
            .filter((e) => e.at >= mockStart)
            .map((e) => `${e.name}@${Math.round(e.at - start)}:${e.ms.toFixed(1)}`);
          return {
            all,
            mockRequests,
            taskMs,
            ms,
            mockMs: start - mockStart,
            patched: builds.length > 0 && builds.every((b) => b.detail?.patched === true),
            buildMs: builds.map((b) => b.ms),
          };
        },
        { id, name },
      );
    const runs = [];
    for (let i = 0; i < 5; i++) {
      runs.push(await measure(`Aaa renamed ${i}`));
      await page.waitForTimeout(400);
    }
    const sorted = runs.map((r) => r.ms).sort((a, b) => a - b);
    const median = sorted[Math.floor(sorted.length / 2)];
    expect(
      runs.every((r) => r.patched),
      "every change is patched into the tree, not rebuilt",
    ).toBe(true);
    const m = record(info, {
      name: "model.changed -> tree and search updated (median of 5)",
      ms: median,
      mockMs: 0,
      targetMs: 16,
      detail: {
        runs: runs.map((r) => Math.round(r.ms * 10) / 10),
        patchMs: runs.map((r) => r.buildMs),
        wholeTaskMs: runs.map((r) => Math.round(r.taskMs)),
        mockWriteMs: runs.map((r) => Math.round(r.mockMs)),
      },
    });
    within(info, m, CHANGE_MISS);
  });

  test("General-mode editor: next entity on the Mappings tab (walk 20)", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    const tree = page.getByTestId("explorer-tree");
    await tree.evaluate((el) => {
      Array.from(el.querySelectorAll<HTMLElement>('[role="treeitem"][aria-level="1"][data-type="domain"]'))
        .slice(0, 12)
        .forEach((row) => row.click());
    });
    await page.waitForTimeout(200);
    // The largest Entities folder the open domains show: the walk needs 21 entities in a row.
    const folders = await tree.locator('[role="treeitem"][data-type="folder"][aria-expanded="false"]').evaluateAll((rows) =>
      rows
        .filter((r) => (r.getAttribute("data-testid") ?? "").startsWith("explorer-folder-Entities"))
        .map((r) => ({
          key: r.getAttribute("data-key") ?? "",
          count: Number((r.querySelector('[data-part="count"]')?.textContent ?? "0").replace(/,/g, "")),
        })),
    );
    const folder = folders.reduce((a, b) => (b.count > a.count ? b : a), { key: "", count: 0 });
    test.skip(folder.count < 21, "No domain of the large model shows 21 entities in one folder.");
    const folderRow = tree.locator(`[data-key=${JSON.stringify(folder.key)}]`);
    await folderRow.scrollIntoViewIfNeeded();
    await folderRow.click();
    await expect(folderRow).toHaveAttribute("aria-expanded", "true");
    const first = await folderRow.evaluate((row) => {
      const next = row.nextElementSibling as HTMLElement | null;
      return next?.getAttribute("data-key") ?? "";
    });
    const firstRow = tree.locator(`[data-key=${JSON.stringify(first)}]`);
    await firstRow.click();
    await tree.focus();
    await page.keyboard.press("Enter");
    const editor = page.getByTestId("element-editor");
    await expect(editor).toHaveAttribute("data-id", first);
    await editor.getByRole("tab", { name: "Mappings" }).click();
    await page.getByTestId("follow-selection").click();
    await expect(page.getByTestId("follow-selection")).toHaveAttribute("aria-pressed", "true");
    await tree.focus();

    // Each step clicks the next entity row (a click selects; the following tab shows the selection).
    const CLICK = `document.querySelector('[data-testid="explorer-tree"] [data-key="' + arg + '"]').click();`;
    const SHOWN = `const e = document.querySelector('[data-testid="element-editor"]'); return !!e && e.getAttribute("data-id") === arg && !!e.querySelector('[role="tab"][aria-selected="true"]')?.textContent?.includes("Mappings");`;
    const times: number[] = [];
    let current = first;
    for (let i = 0; i < 20; i++) {
      const next = await tree.locator(`[data-key=${JSON.stringify(current)}]`).evaluate((row) => row.nextElementSibling?.getAttribute("data-key") ?? "");
      expect(next, `the row after ${current}`).not.toBe("");
      await tree.locator(`[data-key=${JSON.stringify(next)}]`).scrollIntoViewIfNeeded();
      // A user reading each editor rests on it: long enough for the read-ahead (EX 3.5) to land.
      await page.waitForTimeout(350);
      const step = await interact(page, CLICK, next, SHOWN);
      times.push(step.ms);
      current = next;
    }
    const sorted = [...times].sort((a, b) => a - b);
    const median = sorted[Math.floor(sorted.length / 2)];
    const m = record(info, {
      name: "General-mode editor, next entity on the Mappings tab (median of 20)",
      ms: median,
      mockMs: 0,
      targetMs: 100,
      detail: { runs: times.map((t) => Math.round(t)) },
    });
    within(
      info,
      m,
      "About 155 to 178 ms a step when first measured; profiled 2026-09-29: the vocabulary lookups and the filter bar's tag list are cached per index and the covered canvas no longer re-renders; what remains is the editor remounting per entity (its DOM rebuilt, one forced style recalculation in the tabs' presence check) and the explorer's selection commit.",
    );
  });

  test("selection -> related rows highlighted", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    const tree = page.getByTestId("explorer-tree");
    const domain = tree.locator('[role="treeitem"][aria-level="1"][data-type="domain"]').first();
    await domain.click();
    const domainKey = (await domain.getAttribute("data-key"))!;
    await tree.locator(`[role="treeitem"][data-key="${domainKey}/entity"]`).click();
    const rows = tree.locator('[role="treeitem"][data-type="element"][aria-level="3"]');
    await expect(rows.first()).toBeVisible();
    const keys = (await rows.evaluateAll((els) => els.map((e) => e.getAttribute("data-key") ?? ""))).filter(Boolean).slice(0, 8);
    expect(keys.length, "entity rows to select").toBeGreaterThan(1);
    await page.waitForTimeout(2000);
    // From the click to the layout effect of the commit that paints the new highlight (explorer:highlight).
    const times: number[] = [];
    for (const key of keys) {
      const ms = await page.evaluate(async (key) => {
        const w = window as unknown as { __mqPerf: { editor?: { entries: { name: string; at: number }[] } } };
        const mark = (start: number) => (w.__mqPerf.editor?.entries ?? []).find((e) => e.name === "explorer:highlight" && e.at >= start);
        const start = performance.now();
        (document.querySelector(`[data-testid="explorer-tree"] [data-key="${key}"]`) as HTMLElement).click();
        for (let i = 0; i < 200 && !mark(start); i++) await new Promise((r) => setTimeout(r, 5));
        const m = mark(start);
        return m ? m.at - start : -1;
      }, key);
      // An entity with no relations and no tables has nothing to highlight: no mark, not a sample.
      if (ms >= 0) times.push(ms);
      await page.waitForTimeout(300);
    }
    expect(times.length, "selections that highlight related rows").toBeGreaterThan(0);
    const sorted = [...times].sort((a, b) => a - b);
    const m = record(info, {
      name: "selection -> related rows highlighted (median)",
      ms: sorted[Math.floor(sorted.length / 2)],
      mockMs: 0,
      targetMs: 16,
      detail: { runs: times.map((t) => Math.round(t * 10) / 10) },
    });
    within(info, m, HIGHLIGHT_MISS);
  });

  test("select on canvas -> row revealed", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    const nodes = page.locator('.react-flow__node[aria-label^="Entity "]');
    await expect
      .poll(() => nodes.count(), { timeout: 30_000 })
      .toBeGreaterThan(0)
      .catch(() => undefined);
    test.skip((await nodes.count()) === 0, "The large model's first diagram drew no entity.");
    await page.waitForTimeout(2000);
    // A node whose row is not on screen yet: its domain and folder are collapsed.
    const ids = await nodes.evaluateAll((els) => els.map((e) => e.getAttribute("data-id") ?? ""));
    const hidden = await page.evaluate((ids) => ids.filter((id) => id && !document.querySelector(`[data-testid="explorer-tree"] [data-key="${id}"]`)), ids);
    test.skip(hidden.length === 0, "Every node's row is already on screen.");
    const id = hidden[0];
    await page.evaluate((id) => {
      const w = window as unknown as { __mqReveal?: { start: number; end: number } };
      const state = { start: 0, end: 0 };
      w.__mqReveal = state;
      const node = document.querySelector(`.react-flow__node[data-id="${id}"]`)!;
      node.addEventListener("pointerdown", () => (state.start = performance.now()), { capture: true, once: true });
      const shown = () => {
        const row = document.querySelector(`[data-testid="explorer-tree"] [data-key="${id}"]`);
        if (!row) return false;
        const box = row.getBoundingClientRect();
        const tree = document.querySelector('[data-testid="explorer-tree"]')!.getBoundingClientRect();
        return box.top >= tree.top - 1 && box.bottom <= tree.bottom + 1;
      };
      const observer = new MutationObserver(() => {
        if (state.start && shown()) {
          state.end = performance.now();
          observer.disconnect();
        }
      });
      observer.observe(document.body, { subtree: true, childList: true, attributes: true });
    }, id);
    await page.locator(`.react-flow__node[data-id="${id}"]`).click();
    await expect.poll(() => page.evaluate(() => (window as unknown as { __mqReveal: { end: number } }).__mqReveal.end), { timeout: 5_000 }).toBeGreaterThan(0);
    const { start, end } = await page.evaluate(() => (window as unknown as { __mqReveal: { start: number; end: number } }).__mqReveal);
    const m = record(info, { name: "select on canvas -> row revealed", ms: end - start, mockMs: 0, targetMs: 50, detail: { id } });
    within(info, m, REVEAL_MISS);
  });

  test("expand a table row", async ({ page }, info) => {
    await watchExplorer(page);
    await openLarge(page);
    await searchReady(page, true);
    await page.getByRole("navigation", { name: "Explorers" }).getByRole("button", { name: "Databases", exact: true }).click();
    const tree = page.getByTestId("explorer-tree");
    const table = tree.locator('[role="treeitem"][data-type="table"][aria-expanded="false"]').first();
    // Open the first database, schema and folder until a table row shows.
    for (let i = 0; i < 8 && (await table.count()) === 0; i++) {
      const closed = tree.locator('[role="treeitem"][aria-expanded="false"]:is([data-type="database"],[data-type="schema"],[data-type="folder"])').first();
      if ((await closed.count()) === 0) break;
      await closed.click();
      await page.waitForTimeout(200);
    }
    test.skip((await table.count()) === 0, "No table row in the Databases explorer.");
    const keys = await tree
      .locator('[role="treeitem"][data-type="table"][aria-expanded="false"]')
      .evaluateAll((els) => els.slice(0, 5).map((e) => e.getAttribute("data-key") ?? ""));
    await page.waitForTimeout(1000);
    const times: number[] = [];
    const mockMs: number[] = [];
    for (const key of keys) {
      const before = await page.evaluate(() => (window as unknown as { __mqPerf: { mock: MockPerf } }).__mqPerf.mock.requests.length);
      const step = await interact(
        page,
        `document.querySelector('[data-testid="explorer-tree"] [data-key="' + arg + '"] > span[aria-hidden]').click();`,
        key,
        `const row = document.querySelector('[data-testid="explorer-tree"] [data-key="' + arg + '"]'); const next = row && row.nextElementSibling; return !!row && row.getAttribute("aria-expanded") === "true" && !!next && Number(next.getAttribute("aria-level")) > Number(row.getAttribute("aria-level"));`,
      );
      times.push(step.ms);
      const requests = await page.evaluate((n) => (window as unknown as { __mqPerf: { mock: MockPerf } }).__mqPerf.mock.requests.slice(n), before);
      mockMs.push(requests.filter((r) => /^\/api\/databases\//.test(r.path)).reduce((a, r) => a + r.ms, 0));
      await page.waitForTimeout(200);
    }
    const order = times.map((t, i) => i).sort((a, b) => times[a] - times[b]);
    const mid = order[Math.floor(order.length / 2)];
    const m = record(info, {
      name: "expand a table row (median)",
      ms: times[mid],
      mockMs: mockMs[mid],
      targetMs: 200,
      detail: { runs: times.map((t) => Math.round(t)), mockMs: mockMs.map((t) => Math.round(t)) },
    });
    within(info, m);
  });

  // Targets of explorer-redesign.md 4.5 that wait for parts of the redesign.
  const pending: [string, string][] = [
    [
      "expand or collapse main's 10,005-table Tables folder (5,000+ children) <= 16 ms",
      "needs the Tables folder of a 10,005-table database measured after the summaries load (the kind folders exist; this measure is not written yet)",
    ],
    ["type-ahead in the 10,005-table Tables folder", "needs that folder expanded after the summaries load; the measure is not written yet"],
    [
      "expand an entity row, children prefetched <= 16 ms / not prefetched <= 150 ms",
      "the hover and focus read-ahead exists (section 3.5); the measure is not written yet",
    ],
    ["scroll the fully expanded tree at 60 fps", "the tree is virtualized (section 4.3); the scroll measure is not written yet"],
    ["300-node diagram open -> first paint <= 2 s", "the reads are batched (E5b); needs the diagram measurements of section 5 item 7"],
    ["300-node diagram pan and zoom, ELK auto-layout", "needs the diagram measurements of section 5 item 7"],
  ];
  for (const [name, reason] of pending) {
    test(name, () => {
      test.skip(true, `Not measured yet: ${reason}.`);
    });
  }
});
