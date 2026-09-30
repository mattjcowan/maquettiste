// The gate 3 walk (SPEC Section 21, phase-3-design.md section 8.3), project live only, against the image started over the
// processes fixture (tests/fixtures/models/processes/tools/gate3.sh walk, .github/workflows/gate3.yml). Open PurchaseApproval,
// simulate a path in the simulation panel (submit as BudgetHolder, checkBudget done, complianceReview done, approve twice,
// createPurchaseOrder done) and record it as the scenario RecordedInTheEditor; record a SalesOrderLifecycle path the same way (so
// each process has a scenario recorded through the panel, criterion 3); then plan every pack and apply. The script's verify
// and test steps check that both recorded scenarios pass in the engine and as generated tests.
// Skipped unless MAQUETTISTE_GATE3 is set: the mock and plain live runs serve the billing fixture, not this model.
import type { Locator, Page } from "@playwright/test";
import { expect, test, workspace } from "./fixtures";

test.skip(!process.env.MAQUETTISTE_GATE3, "gate 3 only: set MAQUETTISTE_GATE3 and serve the processes fixture");

const slow = { timeout: 120_000 };
test.use({ actionTimeout: 30_000 });
// No retry: the walk records scenarios, so a second attempt would start from a changed project (rerun gate3.sh from prepare).
test.describe.configure({ retries: 0 });

const PURCHASE_APPROVAL = "01JQPRC0000000000000000002";
const SALES_ORDER_LIFECYCLE = "01JQPRC0000000000000000001";

/** Same-origin fetch from the page: Node may not resolve *.localhost, the browser always does (so no page.request). */
async function api(page: Page, method: string, path: string, body?: unknown): Promise<{ status: number; json: unknown }> {
  return page.evaluate(
    async ([m, p, b]) => {
      const res = await fetch(p as string, {
        method: m as string,
        headers: b === undefined ? {} : { "Content-Type": "application/json" },
        body: b === undefined ? undefined : JSON.stringify(b),
      });
      const text = await res.text();
      return { status: res.status, json: text ? (JSON.parse(text) as unknown) : null };
    },
    [method, path, body] as const,
  );
}

/** Signs the browser in with the editor token (the container may see the browser as a remote peer, not a local one). */
async function signIn(page: Page): Promise<void> {
  const token = process.env.MAQUETTISTE_EDITOR_TOKEN;
  if (!token) return;
  await page.goto("/healthz");
  const res = await api(page, "POST", "/api/session", { token });
  expect(res.status, "POST /api/session").toBe(200);
}

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });
const enabled = (panel: Locator, label: string) => panel.locator(`[data-testid="enabled-row"][data-label="${label}"]`);
const item = (panel: Locator, label: string) => panel.locator(`li[data-label="${label}"]`);
const paths = (panel: Locator) => panel.getByTestId("configuration-row");

/** Opens a process from the Processes explorer on its Chart tab, with the simulation panel expanded. */
async function openProcess(page: Page, domain: string, row: string, id: string): Promise<Locator> {
  await workspace(page, "Processes");
  const side = explorer(page);
  const folder = side.getByTestId(`explorer-folder-${domain}`);
  if ((await folder.getAttribute("aria-expanded")) !== "true") {
    await folder.click();
    await page.keyboard.press("ArrowRight");
    await expect(folder).toHaveAttribute("aria-expanded", "true");
  }
  await side.getByTestId(`explorer-row-${row}`).dblclick();
  const editor = page.locator(`[data-testid="process-editor"][data-id="${id}"]`);
  await expect(editor.getByRole("tab", { name: "Chart", exact: true })).toHaveAttribute("aria-selected", "true", slow);
  const panel = editor.getByTestId("simulation-panel");
  await expect(panel).toBeVisible();
  const toggle = panel.getByRole("button", { name: /simulation panel/ }).first();
  if ((await toggle.getAttribute("aria-expanded")) !== "true") await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  return panel;
}

/** Raises an enabled row as an actor (by name), optionally signing as a person. */
async function raise(panel: Locator, label: string, actor?: string, signer?: string): Promise<void> {
  const row = enabled(panel, label);
  await expect(row).toBeVisible(slow);
  if (actor) await row.getByTestId("enabled-actor").selectOption({ label: actor });
  if (signer) await item(panel, label).getByTestId("enabled-signer").fill(signer);
  await row.getByTestId("enabled-raise").click();
}

/** Records the session as a scenario named name, with the outcome the replay gives. */
async function record(page: Page, panel: Locator, name: string, outcome: "final" | "active"): Promise<void> {
  await panel.getByTestId("simulation-record").click();
  const dialog = page.getByTestId("record-dialog");
  await expect(dialog.getByLabel("Outcome")).toHaveValue(outcome);
  await dialog.getByLabel("Name").fill(name);
  await dialog.getByTestId("record-save").click();
  await expect(dialog).toHaveCount(0, slow);
  await expect(page.getByText(`Recorded ${name}.`)).toBeVisible(slow);
}

test("gate 3: simulate and record in the panel, plan and apply", async ({ page }) => {
  test.setTimeout(600_000);
  await signIn(page);
  await page.goto("/");
  await expect(page.getByTestId("shell")).toBeVisible(slow);
  // Room for the panel: the bottom panel and the inspector are hidden.
  await page.keyboard.press("Alt+Shift+J");
  await page.keyboard.press("Alt+Shift+P");

  // PurchaseApproval: the approval path through both reviews, the two-signature gate and the purchase order.
  const purchase = await openProcess(page, "Purchasing", "Purchase approval", PURCHASE_APPROVAL);
  await expect(paths(purchase)).toHaveText(["Drafting"], slow);
  await raise(purchase, "submit", "BudgetHolder");
  await expect(paths(purchase)).toHaveText(["Review.Budget.Checking", "Review.Compliance.Pending"], slow);
  await raise(purchase, "checkBudget done");
  await expect(paths(purchase)).toHaveText(["Review.Budget.BudgetOk", "Review.Compliance.Pending"], slow);
  await raise(purchase, "complianceReview done", "ComplianceOfficer");
  await expect(paths(purchase)).toHaveText(["Approval"], slow);
  await raise(purchase, "approve", "Approver", "erin");
  await expect(enabled(purchase, "approve").getByTestId("enabled-gate")).toHaveText("1 of 2 signed", slow);
  await expect(paths(purchase)).toHaveText(["Approval"]);
  await raise(purchase, "approve", "FinanceController", "frank");
  await expect(paths(purchase)).toHaveText(["Ordering"], slow);
  await raise(purchase, "createPurchaseOrder done");
  // A final state's row carries a "final" badge after its name.
  await expect(paths(purchase)).toHaveText([/^Ordered/], slow);
  await expect(purchase.getByTestId("enabled-final")).toBeVisible();
  await record(page, purchase, "RecordedInTheEditor", "final");

  // SalesOrderLifecycle: within the credit limit, picked, shipped and paid, so both regions end and the order completes.
  const sales = await openProcess(page, "Sales", "Sales order lifecycle", SALES_ORDER_LIFECYCLE);
  await expect(paths(sales)).toHaveText(["Draft"], slow);
  await raise(sales, "submit");
  await expect(paths(sales)).toHaveText(["Fulfilment.Processing.Payment.AwaitingPayment", "Fulfilment.Processing.Shipping.Picking"], slow);
  await raise(sales, "pick");
  await expect(paths(sales)).toHaveText(["Fulfilment.Processing.Payment.AwaitingPayment", "Fulfilment.Processing.Shipping.Packing"], slow);
  await raise(sales, "ship");
  await expect(paths(sales)).toHaveText(["Fulfilment.Processing.Payment.AwaitingPayment", "Fulfilment.Processing.Shipping.Shipped"], slow);
  await raise(sales, "paymentReceived");
  await expect(paths(sales)).toHaveText([/^Completed/], slow);
  await record(page, sales, "RecordedInTheEditor", "final");

  // Both recorded scenarios are saved beside their processes.
  for (const id of [PURCHASE_APPROVAL, SALES_ORDER_LIFECYCLE]) {
    const verified = await api(page, "POST", `/api/processes/${id}/verify`, {});
    expect(verified.status, `verify ${id}`).toBe(200);
    const results = (verified.json as { results: { name?: string; passed: boolean }[] }).results;
    expect(
      results.every((r) => r.passed),
      `every scenario of ${id} passes`,
    ).toBe(true);
    expect(results.length).toBeGreaterThanOrEqual(7);
  }

  // Generate: plan every pack, then apply by plan id; the job's result must be a succeeded apply.
  await workspace(page, "Generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-result")).toContainText("succeeded", slow);
  const summary = page.getByTestId("plan-summary");
  await expect(summary).toContainText("csharp-dapper:");
  await expect(summary).toContainText("sql-ddl:");
  const started = page.waitForResponse((r) => r.url().endsWith("/api/generate/apply") && r.request().method() === "POST");
  await page.getByTestId("apply").click();
  const response = await started;
  expect(response.status(), "POST /api/generate/apply").toBe(202);
  const job = (await response.json()) as { id: string };
  const result = page.getByTestId("apply-result");
  await expect(result).toBeVisible({ timeout: 300_000 });
  await expect(result.getByTestId("outcome")).toHaveText(/succeeded/);
  const final = await api(page, "GET", `/api/jobs/${job.id}`);
  expect(final.status).toBe(200);
  const info = final.json as { applyResult?: { outcome?: string; result?: { filesWritten?: number } } };
  expect(info.applyResult?.outcome, "applyResult.outcome").toBe("succeeded");
  expect(info.applyResult?.result?.filesWritten ?? 0).toBeGreaterThan(50);
});
