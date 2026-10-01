// The statechart canvas's budgets (phase-3-design.md 4.5) on `?mock=chart400` (mocks/model/processDiagramSeed.ts: one
// process of 400 states nested three deep with parallel regions and 321 transitions, its diagram saved with a view that
// shows the whole chart): the first paint of the chart (from the Chart tab's first render to the first frame that
// shows the states at their places: the canvas draws no node before the diagram is loaded) at most 250 ms, and the
// layout of the 400 nested states in the worker at most 400 ms, each checked against its target plus 25 % for machine
// noise, as tests/e2e/scale.spec.ts does, here and in CI alike. It runs as the mock project's teardown
// (playwright.config.ts `mock-budgets`), after the other specs and alone. The budgets hold the first samples (the
// chart's first open, the first Layout); the chart is opened and laid out three times and every sample is recorded.
// The numbers are attached to the report as statechart-timings.json.
import type { Page, TestInfo } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const NOISE = 1.25;
// The section 4.5 budgets assume the design's 8-core machine; on a GitHub-hosted runner (editor.yml sets this from
// runner.environment, as gate3.yml does for the bench) a miss is recorded on the report and does not fail the run.
const ADVISORY = process.env.MQ_BUDGETS_ADVISORY === "1";

interface Measure {
  name: string;
  ms: number;
  targetMs: number;
  budgetMs: number;
}

function within(info: TestInfo, name: string, ms: number, targetMs: number): Measure {
  const measure = { name, ms, targetMs, budgetMs: Math.round(targetMs * NOISE) };
  info.annotations.push({ type: "timing", description: `${name}: ${ms} ms (target ${targetMs} ms, budget ${measure.budgetMs} ms)` });
  if (ADVISORY && ms > measure.budgetMs) {
    info.annotations.push({ type: "over budget (advisory on this runner)", description: `${name}: ${ms} ms, budget ${measure.budgetMs} ms` });
    return measure;
  }
  expect(ms, `${name} took ${ms} ms; budget ${measure.budgetMs} ms`).toBeLessThanOrEqual(measure.budgetMs);
  return measure;
}

const chart = (page: Page) => page.getByTestId("statechart-canvas");

test("a 400-state chart paints within 250 ms and lays out in the worker within 400 ms", async ({ page }, info) => {
  test.setTimeout(90_000);
  await page.setViewportSize({ width: 1600, height: 1200 });
  await openEditor(page, "/?mock=chart400");
  // The bottom panel hidden, so the canvas shows the whole chart.
  await page.keyboard.press("Alt+Shift+J");
  await workspace(page, "Processes");
  const side = page.getByRole("complementary", { name: "Explorer" });
  const billing = side.getByTestId("explorer-folder-Billing");
  await billing.click();
  await page.keyboard.press("ArrowRight");
  await side.getByTestId("explorer-row-Large chart").dblclick();
  await expect(chart(page)).toHaveAttribute("data-state-count", "400");
  const editor = page.getByTestId("process-editor");
  const paints: number[] = [];
  let drawn = 0;
  for (let run = 0; run < 3; run++) {
    if (run) {
      // Leaving the tab unmounts the canvas; coming back draws it again from its first render.
      await editor.getByRole("tab", { name: "States", exact: true }).click();
      await expect(chart(page)).toHaveCount(0);
      await editor.getByRole("tab", { name: "Chart", exact: true }).click();
    }
    await expect(chart(page)).toHaveAttribute("data-first-paint-ms", /\d+/);
    paints.push(Number(await chart(page).getAttribute("data-first-paint-ms")));
    drawn = await chart(page).locator(".react-flow__node").count();
    expect(drawn, "states drawn in the first view").toBeGreaterThan(300);
    // The states the first paint timed stand at their places, not stacked at the origin.
    const places = await chart(page)
      .locator(".react-flow__node-state")
      .evaluateAll(
        (els) =>
          new Set(
            els.map((e) => {
              const r = e.getBoundingClientRect();
              return `${Math.round(r.x)},${Math.round(r.y)}`;
            }),
          ).size,
      );
    expect(places, "distinct places of the states drawn").toBeGreaterThan(300);
  }

  const layouts: number[] = [];
  for (let run = 0; run < 3; run++) {
    const before = await chart(page).getAttribute("data-layout-ms");
    await page.getByTestId("chart-layout").click();
    await expect.poll(() => chart(page).getAttribute("data-layout-ms"), { timeout: 20_000 }).not.toBe(before);
    await expect(page.getByTestId("chart-layout")).toBeEnabled();
    layouts.push(Number(await chart(page).getAttribute("data-layout-ms")));
  }
  const measures = [
    within(info, "first paint, 400 states (first open)", paints[0], 250),
    within(info, "layout of 400 nested states in the worker (first Layout)", layouts[0], 400),
  ];
  info.annotations.push({ type: "timing", description: `samples: first paint ${paints.join(", ")} ms; layout ${layouts.join(", ")} ms` });
  await info.attach("statechart-timings.json", {
    body: JSON.stringify({ measures, drawn, paints, layouts }, null, 2),
    contentType: "application/json",
  });
});
