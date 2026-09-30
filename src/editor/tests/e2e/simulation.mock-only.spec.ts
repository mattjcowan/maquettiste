// The simulation panel (phase-3-design.md 6.4) on PurchaseApproval's Chart tab: the Enabled rows, submit raised as
// BudgetHolder with Enter and with the key 1, the Configuration after each input, time advanced to the reminder
// timer, the Last step, a trace input deleted with the ones after it, Record to scenario… listed on the Scenarios tab
// (one undo step removes it), Replay scenario… on QuickApproval stopping at its failing step with expected and actual
// side by side, and the explorer's entry points (Simulate on the row menu, Record from simulation in New scenario…).
// Every row of the panel is ROW_H high. Mock-only: the mock answers simulate with its reduced interpreter.
import AxeBuilder from "@axe-core/playwright";
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";
import { scanIconControls, type IconControlScan } from "../icon-controls";

const ROW_H = 24;
const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });
const menuItem = (page: Page, name: string) => page.getByTestId("row-menu").getByRole("menuitem", { name });

async function expand(page: Page, row: Locator) {
  await row.click();
  await page.keyboard.press("ArrowRight");
  await expect(row).toHaveAttribute("aria-expanded", "true");
}

async function processes(page: Page) {
  await openEditor(page);
  await workspace(page, "Processes");
  const side = explorer(page);
  await expand(page, side.getByTestId("explorer-folder-Billing"));
  return side;
}

/** Opens PurchaseApproval from the explorer; the editor opens on its Chart tab with the panel below the canvas. */
async function openPurchase(page: Page) {
  const side = await processes(page);
  await side.getByTestId("explorer-row-Purchase approval").dblclick();
  const editor = page.getByTestId("process-editor");
  await expect(editor.getByRole("tab", { name: "Chart", exact: true })).toHaveAttribute("aria-selected", "true");
  const panel = editor.getByTestId("simulation-panel");
  await expect(panel).toBeVisible();
  // The bottom panel and the inspector are hidden so the simulation panel has room.
  await page.keyboard.press("Alt+Shift+J");
  await page.keyboard.press("Alt+Shift+P");
  // The panel starts collapsed (the chart keeps its room) until it is expanded or asked for.
  const toggle = panel.getByRole("button", { name: /simulation panel/ }).first();
  if ((await toggle.getAttribute("aria-expanded")) !== "true") await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  return { side, editor, panel };
}

const rowsOf = (panel: Locator, testid: string) => panel.getByTestId(testid);
const enabled = (panel: Locator, label: string) => panel.locator(`[data-testid="enabled-row"][data-label="${label}"]`);
/** An enabled trigger with its field rows (payload, signer, meaning, reason, assumptions). */
const item = (panel: Locator, label: string) => panel.locator(`li[data-label="${label}"]`);
const paths = (panel: Locator) => rowsOf(panel, "configuration-row");

async function expectRowHeights(panel: Locator) {
  const heights = await panel
    .locator('[data-testid$="-row"]')
    .evaluateAll((els) => els.map((e) => ({ id: e.getAttribute("data-testid"), h: Math.round(e.getBoundingClientRect().height) })));
  expect(heights.length).toBeGreaterThan(5);
  expect(heights.filter((x) => x.h !== ROW_H)).toEqual([]);
}

test("raises inputs by Enter and by number key, advances time, and deletes a trace input", async ({ page }) => {
  const { panel } = await openPurchase(page);
  const submit = enabled(panel, "submit");
  await expect(rowsOf(panel, "enabled-row")).toHaveCount(1);
  await expect(submit.getByTestId("enabled-actor")).toHaveValue("01JQACT0000000000000000008");
  await expect(submit.getByTestId("enabled-actor").locator("option:checked")).toHaveText("BudgetHolder");
  await expect(paths(panel)).toHaveText(["Drafting"]);

  // Enter on the row raises submit as BudgetHolder: the engine's answer moves the configuration into Review.
  await submit.focus();
  await page.keyboard.press("Enter");
  await expect(paths(panel)).toHaveText(["Review.Budget.Checking", "Review.Compliance.Pending"]);
  await expect(rowsOf(panel, "trace-row")).toHaveCount(2);
  await expect(rowsOf(panel, "trace-row").nth(1)).toContainText("submit as BudgetHolder");
  await expect(rowsOf(panel, "last-step-row").first()).toContainText("Took Drafting · submit → Review");
  // The canvas highlights the active states and flashes the edge just taken.
  const canvas = page.getByTestId("statechart-canvas");
  await expect(canvas.getByTestId("state-Checking")).toHaveAttribute("data-active", "true");
  await expect(canvas.getByTestId("state-Drafting")).not.toHaveAttribute("data-active", "true");
  await expect(canvas.locator('.react-flow__edge[data-id="01JQTRN0000000000000000015"]')).toHaveClass(/mq-flash-[ab]/);

  // Delete on a trace row removes that input (and every later one): back to Drafting.
  await rowsOf(panel, "trace-row").nth(1).click();
  await page.keyboard.press("Delete");
  await expect(rowsOf(panel, "trace-row")).toHaveCount(1);
  await expect(paths(panel)).toHaveText(["Drafting"]);

  // The key 1 raises the first enabled row with its current values.
  await expect(submit).toBeVisible();
  await submit.focus();
  await page.keyboard.press("1");
  await expect(paths(panel)).toHaveText(["Review.Budget.Checking", "Review.Compliance.Pending"]);
  await expect(enabled(panel, "checkBudget done")).toBeVisible();
  await expect(rowsOf(panel, "pending-row")).toHaveCount(2);

  // Both reviews complete: checkBudget from its row, complianceReview (a human task, as Approver) from Pending's Done.
  await enabled(panel, "checkBudget done").getByTestId("enabled-raise").click();
  await expect(paths(panel)).toHaveText(["Review.Budget.BudgetOk", "Review.Compliance.Pending"]);
  await rowsOf(panel, "pending-row").filter({ hasText: "complianceReview" }).getByRole("button", { name: "Done" }).click();
  await expect(paths(panel)).toHaveText(["Approval"]);
  await expect(enabled(panel, "approve").getByTestId("enabled-gate")).toHaveText("0 of 2 signed");

  // Time: the next timer is the reminder in five days; Advance fires it and the Last step shows its action.
  await expect(panel.getByTestId("time-after")).toHaveValue("P5D");
  await panel.getByTestId("time-advance").click();
  await expect(panel.getByTestId("simulation-clock")).toHaveText("Clock 2000-01-06T00:00:00Z");
  await expect(rowsOf(panel, "last-step-row")).toContainText([
    "Took Approval · P5D → (no target)",
    "sendReminder (expression): reminders=1",
    "Context: reminders=1",
  ]);
  await expect(rowsOf(panel, "trace-row").last()).toContainText("time +P5D");

  // A gated event signs once per raise; two signers complete the gate and the process moves on.
  await enabled(panel, "approve").getByTestId("enabled-raise").click();
  await expect(enabled(panel, "approve").getByTestId("enabled-gate")).toHaveText("1 of 2 signed");
  await expect(rowsOf(panel, "last-step-row")).toContainText(["Purchase approval: signed by signer1 as Approver (approved)"]);
  // The same signer again is refused with its reason, and the refusal is audited.
  await item(panel, "approve").getByTestId("enabled-signer").fill("signer1");
  await enabled(panel, "approve").getByTestId("enabled-raise").click();
  await expect(rowsOf(panel, "last-step-row").first()).toHaveText("Refused: this signer already signed the gate");
  await expect(rowsOf(panel, "trace-row").last()).toHaveAttribute("data-accepted", "false");
  await expect(item(panel, "approve").getByTestId("enabled-signer")).toHaveValue("signer2");
  await enabled(panel, "approve").getByTestId("enabled-raise").click();
  await expect(paths(panel)).toHaveText(["Ordering"]);

  // Selecting an earlier trace entry shows the state after it.
  await rowsOf(panel, "trace-row").nth(1).click();
  await expect(paths(panel)).toHaveText(["Review.Budget.Checking", "Review.Compliance.Pending"]);
  await expect(panel.getByTestId("enabled-past")).toBeVisible();

  await expectRowHeights(panel);
  expect((await page.evaluate(scanIconControls as () => IconControlScan)).missing).toEqual([]);
  expect((await new AxeBuilder({ page }).include('[data-testid="simulation-panel"]').analyze()).violations).toEqual([]);
});

test("Record to scenario… saves the inputs as a scenario the Scenarios tab lists, one undo step", async ({ page }) => {
  const { editor, panel } = await openPurchase(page);
  await enabled(panel, "submit").getByTestId("enabled-raise").click();
  await expect(paths(panel)).toHaveCount(2);
  await enabled(panel, "checkBudget error").getByTestId("enabled-raise").click();
  await expect(paths(panel)).toHaveText(["Review.Budget.BudgetRejected", "Review.Compliance.Pending"]);
  await enabled(panel, "requestChanges").getByTestId("enabled-raise").click();
  await expect(paths(panel)).toHaveText(["Drafting"]);

  await panel.getByTestId("simulation-record").click();
  const dialog = page.getByTestId("record-dialog");
  await expect(dialog.getByLabel("Outcome")).toHaveValue("active");
  await dialog.getByLabel("Name").fill("ChangesRequested");
  await dialog.getByTestId("record-save").click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByText("Recorded ChangesRequested.")).toBeVisible();

  await editor.getByRole("tab", { name: "Scenarios", exact: true }).click();
  const rows = editor.getByTestId("scenarios-grid-row");
  await expect(rows).toHaveCount(3);
  await expect(rows.filter({ hasText: "ChangesRequested" })).toContainText("3");

  // One undo step deletes the recorded scenario.
  await page.keyboard.press("ControlOrMeta+z");
  await expect(rows).toHaveCount(2);

  // Back on the Chart tab the session is where it was: the engine replays its inputs.
  await editor.getByRole("tab", { name: "Chart", exact: true }).click();
  await expect(rowsOf(editor.getByTestId("simulation-panel"), "trace-row")).toHaveCount(4);
  await expect(paths(editor.getByTestId("simulation-panel"))).toHaveText(["Drafting"]);
});

test("Replay scenario… steps through QuickApproval and stops at the failing step with expected and actual", async ({ page }) => {
  const { panel } = await openPurchase(page);
  await panel.getByTestId("simulation-replay").click();
  const dialog = page.getByTestId("replay-dialog");
  await dialog.getByLabel("Scenario").selectOption({ label: "QuickApproval" });
  await dialog.getByTestId("replay-run").click();
  await expect(dialog).toHaveCount(0);
  const replay = panel.getByTestId("replay-panel");
  await expect(replay).toHaveAttribute("data-passed", "false");
  await expect(replay.getByTestId("replay-row")).toHaveCount(2);
  await expect(replay.getByTestId("replay-verdict")).toHaveText(["passed", "failed"]);
  await expect(replay.getByTestId("replay-summary")).toContainText("Step 2 failed (MQ9302)");
  expect((await new AxeBuilder({ page }).include('[data-testid="simulation-panel"]').analyze()).violations).toEqual([]);
  await expect(replay.getByTestId("replay-expected-row")).toHaveText("Approval");
  await expect(replay.getByTestId("replay-actual-row")).toHaveText(["Review.Budget.BudgetOk", "Review.Compliance.Pending"]);
  // Configuration shows the failing step's states, from the engine's simulation of the scenario; selecting the first
  // step shows the states after it.
  await expect(paths(panel)).toHaveText(["Review.Budget.BudgetOk", "Review.Compliance.Pending"]);
  await replay.getByTestId("replay-row").first().click();
  await expect(paths(panel)).toHaveText(["Review.Budget.Checking", "Review.Compliance.Pending"]);
  await expectRowHeights(panel);

  // A passing scenario: every step passed; closing the replay brings the trace back.
  await panel.getByTestId("replay-close").click();
  await expect(rowsOf(panel, "trace-row")).toHaveCount(1);
  await panel.getByTestId("simulation-replay").click();
  await page.getByTestId("replay-dialog").getByLabel("Scenario").selectOption({ label: "BudgetRejected" });
  await page.getByTestId("replay-run").click();
  await expect(panel.getByTestId("replay-verdict")).toHaveText(["passed", "passed", "passed"]);
  await expect(panel.getByTestId("replay-row").last()).toContainText("Review.Budget.BudgetRejected, Review.Compliance.Cleared");

  // From scenario… loads a scenario's start and steps as the session's inputs.
  await panel.getByTestId("replay-close").click();
  await panel.getByTestId("simulation-from-scenario").selectOption({ label: "BudgetRejected" });
  await expect(rowsOf(panel, "trace-row")).toHaveCount(4);
  await expect(panel.locator('[data-testid="start-row"][data-attribute="amount"] input')).toHaveValue("2500");
  await expect(paths(panel)).toHaveText(["Review.Budget.BudgetRejected", "Review.Compliance.Cleared"]);
});

test("Simulate on the explorer's row menu opens the Chart tab with the panel", async ({ page }) => {
  const side = await processes(page);
  await side.getByTestId("explorer-row-Purchase approval").click({ button: "right" });
  await menuItem(page, "Simulate").click();
  const editor = page.getByTestId("process-editor");
  await expect(editor).toHaveAttribute("data-id", "01JQPRC0000000000000000002");
  await expect(editor.getByRole("tab", { name: "Chart", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(editor.getByTestId("enabled-row")).toHaveCount(1);
});

test("Record from simulation in New scenario… opens the panel recording under the name given", async ({ page }) => {
  const side = await processes(page);
  await expand(page, side.getByTestId("explorer-row-Purchase approval"));
  await expand(page, side.getByTestId("explorer-folder-Scenarios").first());
  await side.getByTestId("explorer-folder-Scenarios").first().click({ button: "right" });
  await menuItem(page, "New scenario…").click();
  const dialog = page.getByTestId("new-scenario-dialog");
  await dialog.getByLabel("Name").fill("Recorded");
  await dialog.getByTestId("new-scenario-record").check();
  await dialog.getByTestId("new-scenario-create").click();
  await expect(dialog).toHaveCount(0);
  // Nothing was created yet: the scenario exists once the panel records it.
  await expect(side.getByTestId("explorer-row-Recorded")).toHaveCount(0);
  const editor = page.getByTestId("process-editor");
  await expect(editor.getByRole("tab", { name: "Chart", exact: true })).toHaveAttribute("aria-selected", "true");
  const panel = editor.getByTestId("simulation-panel");
  await expect(panel.getByTestId("simulation-recording")).toHaveText("Recording Recorded");
  await enabled(panel, "submit").getByTestId("enabled-raise").click();
  await expect(paths(panel)).toHaveCount(2);
  await panel.getByTestId("simulation-record").click();
  await expect(page.getByTestId("record-dialog").getByLabel("Name")).toHaveValue("Recorded");
  await page.getByTestId("record-save").click();
  await expect(panel.getByTestId("simulation-recording")).toHaveCount(0);
  await expect(side.getByTestId("explorer-row-Recorded")).toContainText("1 step");
});
