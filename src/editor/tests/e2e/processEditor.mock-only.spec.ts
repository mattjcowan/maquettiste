// The process editor (phase-3-design.md 6.2): a lifecycle opened from the Processes explorer (on its Chart tab) with
// every tab walked; a state and a transition added by keyboard (one undo step each); a guard expression that does not
// parse marked MQ9501; the drifted enum synced after its dry run; a scenario opened and replayed with each step's
// status; and the inspector following the selection inside the editor. Mock-only: the processes live in mocks/model/processSeed.ts.
import AxeBuilder from "@axe-core/playwright";
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";
import { scanIconControls, type IconControlScan } from "../icon-controls";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function expand(page: Page, row: Locator) {
  await row.click();
  await page.keyboard.press("ArrowRight");
  await expect(row).toHaveAttribute("aria-expanded", "true");
}

async function openProcess(page: Page, row: string, path = "/") {
  await openEditor(page, path);
  await workspace(page, "Processes");
  const side = explorer(page);
  await expand(page, side.getByTestId("explorer-folder-Billing"));
  await side.getByTestId(`explorer-row-${row}`).dblclick();
  const editor = page.getByTestId("process-editor");
  await expect(editor).toBeVisible();
  // The editor opens on its chart (round P4); these specs walk the grids from the States tab.
  await expect(tab(editor, "Chart")).toHaveAttribute("aria-selected", "true");
  await tab(editor, "States").click();
  return editor;
}

const tab = (editor: Locator, name: string) => editor.getByRole("tab", { name, exact: true });
const rowsOf = (editor: Locator, grid: string) => editor.getByTestId(`${grid}-row`);
const cell = (row: Locator, column: string) => row.locator(`[data-column="${column}"]`);

test("a lifecycle opens on its chart and every tab shows its part of the process", async ({ page }) => {
  const editor = await openProcess(page, "InvoiceLifecycle");
  await expect(tab(editor, "States")).toHaveAttribute("aria-selected", "true");
  const states = rowsOf(editor, "states-grid");
  await expect(states).toHaveCount(4);
  await expect(cell(states.first(), "bound")).toHaveText("Draft");
  // The details start folded on a process; the chevron shows the top controls.
  await expect(editor.getByLabel("Use")).toHaveCount(0);
  await editor.getByTestId("editor-details-toggle").click();
  await expect(editor.getByLabel("Use")).toHaveValue("lifecycle");
  await expect(editor.getByTestId("enum-drift")).toHaveCount(0);

  await tab(editor, "Transitions").click();
  await expect(rowsOf(editor, "transitions-grid")).toHaveCount(3);
  await expect(cell(rowsOf(editor, "transitions-grid").first(), "detail")).toHaveText("issue");
  await expect(editor.getByTestId("guards-grid")).toBeVisible();
  await expect(editor.getByTestId("actions-grid")).toBeVisible();

  await tab(editor, "Events").click();
  await expect(rowsOf(editor, "events-grid")).toHaveCount(3);
  await expect(editor.getByTestId("event-payload")).toContainText("Payload of issue");

  await tab(editor, "Gates").click();
  await expect(editor.getByText("No gates")).toBeVisible();
  await tab(editor, "Context").click();
  await expect(editor.getByTestId("attribute-grid")).toBeVisible();
  await tab(editor, "Scenarios").click();
  await expect(rowsOf(editor, "scenarios-grid")).toHaveCount(1);
  await expect(rowsOf(editor, "scenarios-grid").first().getByTestId("scenario-status")).toHaveAttribute("data-status", "not-run");

  // A gated orchestration shows one form per gate, N of M computed from the signers.
  await workspace(page, "Processes");
  await explorer(page).getByTestId("explorer-row-Purchase approval").dblclick();
  await tab(editor, "Gates").click();
  await expect(editor.getByTestId("gate-form")).toHaveCount(1);
  await expect(editor.getByTestId("gate-of")).toHaveText("2 of 1 signers");
  await expect(editor.getByTestId("meanings-grid-row")).toHaveCount(1);
  expect((await page.evaluate(scanIconControls as () => IconControlScan)).missing).toEqual([]);
});

test("a state and a transition are added by keyboard, each one undo step, and the inspector follows", async ({ page }) => {
  const editor = await openProcess(page, "Purchase approval");
  const states = rowsOf(editor, "states-grid");
  await expect(states).toHaveCount(13);
  await cell(states.first(), "name").click();
  await page.keyboard.press("ControlOrMeta+Enter");
  await expect(states).toHaveCount(14);
  await expect(cell(states.nth(1), "name")).toHaveText("State");
  await page.keyboard.press("Enter");
  await page.keyboard.type("Submitted");
  await page.keyboard.press("Enter");
  await expect(cell(states.nth(1), "name")).toHaveText("Submitted");
  await expect(editor.getByTestId("editor-save-status")).toHaveText("Saved");
  // The inspector shows the selected state.
  await cell(states.nth(1), "name").click();
  await expect(page.getByTestId("inspector-state")).toContainText("Submitted");

  // A state a transition enters is not deleted.
  await cell(states.nth(2), "name").click();
  await page.keyboard.press("ControlOrMeta+Delete");
  await expect(editor.getByTestId("states-error")).toContainText("Transitions still enter Review");
  await expect(states).toHaveCount(14);

  // Undo takes back the rename, then the add.
  await page.keyboard.press("ControlOrMeta+z");
  await expect(cell(states.nth(1), "name")).toHaveText("State");
  await page.keyboard.press("ControlOrMeta+z");
  await expect(states).toHaveCount(13);

  await tab(editor, "Transitions").click();
  const transitions = rowsOf(editor, "transitions-grid");
  await expect(transitions).toHaveCount(10);
  await cell(transitions.first(), "source").click();
  await page.keyboard.press("ControlOrMeta+Enter");
  await expect(transitions).toHaveCount(11);
  await expect(cell(transitions.nth(1), "source")).toHaveText("Drafting");
  await expect(page.getByTestId("inspector-transition")).toBeVisible();
  await expect(page.getByTestId("transition-actors")).toHaveText("BudgetHolder");
  await expect(transitions.filter({ has: page.getByTestId("gate-badge") })).toHaveCount(1);
  expect((await new AxeBuilder({ page }).include('[data-testid="process-editor"]').analyze()).violations).toEqual([]);
});

test("a guard expression that does not parse carries the MQ9501 marker until it is fixed", async ({ page }) => {
  const editor = await openProcess(page, "Purchase approval");
  await tab(editor, "Transitions").click();
  await editor.getByTestId("guards-grid-add").click();
  const guard = rowsOf(editor, "guards-grid").first();
  await expect(cell(guard, "name")).toHaveText("guard");
  await cell(guard, "expression").click();
  await page.keyboard.press("Enter");
  await page.keyboard.type("context.amount >");
  await page.keyboard.press("Enter");
  await expect(guard.getByTestId("cell-marker")).toHaveAttribute("data-rule", "MQ9501");
  await expect(editor.getByTestId("editor-save-status")).toHaveText("Not saved: invalid");
  await cell(guard, "expression").click();
  await page.keyboard.press("Enter");
  await page.keyboard.press("End");
  await page.keyboard.type(" 1000");
  await page.keyboard.press("Enter");
  await expect(cell(guard, "expression")).toHaveText("context.amount > 1000");
  await expect(guard.getByTestId("cell-marker")).toHaveCount(0);
  await expect(editor.getByTestId("editor-save-status")).toHaveText("Saved");
});

test("the drifted enum is synced from the lifecycle after its dry run", async ({ page }) => {
  const editor = await openProcess(page, "PaymentLifecycle", "/?mock=drift");
  await editor.getByTestId("editor-details-toggle").click();
  await expect(editor.getByTestId("enum-drift")).toBeVisible();
  await expect(rowsOf(editor, "states-grid").locator('[data-testid="cell-marker"][data-rule="MQ9203"]')).not.toHaveCount(0);
  await editor.getByTestId("sync-enum").click();
  const plan = page.getByTestId("sync-enum-plan");
  await expect(plan).toContainText("Adds Disputed");
  await page.getByTestId("sync-enum-apply").click();
  await expect(plan).toHaveCount(0);
  await expect(editor.getByTestId("enum-drift")).toHaveCount(0);
  await expect(rowsOf(editor, "states-grid").locator('[data-testid="cell-marker"]')).toHaveCount(0);
});

test("a scenario opens from the Scenarios tab and replays with each step's status", async ({ page }) => {
  const editor = await openProcess(page, "Purchase approval");
  await tab(editor, "Scenarios").click();
  const scenarios = rowsOf(editor, "scenarios-grid");
  await expect(scenarios).toHaveCount(2);
  const quick = scenarios.filter({ hasText: "Quick approval" });
  await quick.getByTestId("scenario-replay").click();
  await expect(quick.getByTestId("scenario-status")).toHaveText("failed at step 2");
  await quick.getByTestId("scenario-open").click();
  const scenario = page.getByTestId("element-editor").and(page.locator('[data-kind="scenario"]'));
  await expect(scenario).toBeVisible();
  const steps = scenario.getByTestId("steps-grid-row");
  await expect(steps).toHaveCount(2);
  await expect(cell(steps.first(), "on")).toHaveText("submit");
  await expect(cell(steps.first(), "actor")).toHaveText("BudgetHolder");
  await expect(steps.nth(0).getByTestId("step-status")).toHaveAttribute("data-status", "passed");
  await expect(steps.nth(1).getByTestId("step-status")).toHaveAttribute("data-status", "failed");
  await scenario.getByTestId("scenario-editor-replay").click();
  await expect(scenario.getByTestId("replay-failure")).toContainText("Step 2: MQ9302");
  await expect(page.getByTestId("inspector-scenario")).toContainText("PurchaseApproval");
});
