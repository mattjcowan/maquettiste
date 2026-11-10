// Revisions (revisions-design.md section 12), in the mock: Invoice gets a revisions block from its Revisions tab, a move of InvoiceLifecycle is
// given the next revision from the inspector and says so on the chart, and a scenario with a revise step is replayed: its wrong expected
// revision fails with MQ9312, Update expectations records the right one, and the replay passes.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

test("adds a block, gives a move the next revision, runs a scenario and sees the revision", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openEditor(page);

  // 1. Invoice has no revisions: the tab says so, and Add revisions gives it one letters level, then a second level for the numbers.
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").dblclick();
  const entity = page.getByRole("region", { name: "Editor: Invoice" });
  await entity.getByRole("tab", { name: "Revisions", exact: true }).click();
  await expect(entity.getByTestId("revisions-empty")).toContainText("has no revisions");
  await entity.getByTestId("revisions-add").click();
  await expect(entity.getByTestId("revisions-preview-first")).toHaveText("A");
  await expect(entity.getByTestId("revisions-preview-level-1")).toHaveText("A, B, C, D, E …");
  await entity.getByTestId("revisions-add-level").click();
  await expect(entity.getByTestId("revisions-preview-level-2")).toHaveText("A.1, A.2, A.3 …");
  // A lifecycle governs Invoice, so the When revised table has a row for it, starting over by default.
  await expect(entity.getByTestId("revisions-lifecycles")).toContainText("InvoiceLifecycle");

  // 2. A scenario with a revise step at level 1 that expects the wrong revision.
  const ids = await page.evaluate(async () => {
    const get = async (url: string) => (await fetch(url)).json();
    const index = (await get("/api/model/index")) as { elements?: { id: string; kind: string; name: string }[] } | { id: string; kind: string; name: string }[];
    const rows = Array.isArray(index) ? index : (index.elements ?? []);
    const byName = (kind: string, name: string) => rows.find((r) => r.kind === kind && r.name === name)!.id;
    const process = byName("process", "InvoiceLifecycle");
    const invoice = byName("entity", "Invoice");
    const body = {
      kind: "scenario",
      id: "01JQSCN0000000000000000777",
      name: "RevisedInvoice",
      displayName: "Revised invoice",
      process,
      subject: "01JQ0BJ0000000000000000777",
      objects: [{ id: "01JQ0BJ0000000000000000777", name: "inv", entity: invoice }],
      steps: [{ id: "01JQSTP0000000000000000777", input: "verb", verb: "revise", revision: 1, expect: { revision: "C" } }],
    };
    const response = await fetch("/api/model/elements", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
    if (!response.ok) throw new Error(await response.text());
    return { process, invoice };
  });
  expect(ids.process).toBeTruthy();

  // 3. InvoiceLifecycle's pay move gives the next revision at level 1, and the chart says so.
  await workspace(page, "Processes");
  const processes = explorer(page);
  const billing = processes.getByTestId("explorer-folder-Billing");
  await billing.click();
  await page.keyboard.press("ArrowRight");
  const lifecycles = processes.locator('[data-key$="/use:lifecycle"]');
  await lifecycles.click();
  await page.keyboard.press("ArrowRight");
  await processes.getByTestId("explorer-row-InvoiceLifecycle").dblclick();
  const editor = page.getByTestId("process-editor");
  const chart = page.getByTestId("statechart-canvas");
  await expect(chart).toBeVisible();
  const pay = chart.getByTestId("transition-label").filter({ hasText: /^pay/ });
  await pay.click();
  await page.getByTestId("transition-next-revision").selectOption("1");
  await expect(pay.getByTestId("revision-label")).toHaveText("next level 1");

  // 4. The scenario fails at its expected revision; the record of the replay fixes it.
  await editor.getByRole("tab", { name: "Scenarios", exact: true }).click();
  const row = editor.getByTestId("scenarios-grid-row").filter({ hasText: "Revised invoice" });
  await row.getByTestId("scenario-replay").click();
  await expect(row.getByTestId("scenario-status")).toHaveAttribute("data-status", "failed");
  await row.getByTestId("scenario-open").click();
  const scenario = page.getByTestId("element-editor").and(page.locator('[data-kind="scenario"]'));
  await expect(scenario).toBeVisible();
  const step = scenario.getByTestId("steps-grid-row").first();
  await expect(step.locator('td[data-column="expectRevision"]')).toContainText("C");
  await scenario.getByTestId("scenario-editor-replay").click();
  await expect(scenario.getByTestId("replay-failure")).toContainText("MQ9312");
  await scenario.getByTestId("scenario-update-expectations").click();
  await expect(step.locator('td[data-column="expectRevision"]')).toContainText("B");
  await scenario.getByTestId("scenario-editor-replay").click();
  await expect(scenario.getByTestId("replay-failure")).toHaveCount(0);
  await expect(scenario.getByTestId("scenario-status")).toHaveAttribute("data-status", "passed");
});
