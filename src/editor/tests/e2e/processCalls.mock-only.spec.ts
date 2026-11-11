// Processes linked to operations, editor part 2 (processes-operations-design.md section 12): the Calls dialog of an action, a typed
// value with its type, the column's cell, and the simulation panel's "recorded, not run". Mock-only: the operation is created on the
// mock server; PurchaseApproval's sendReminder action comes from mocks/model/processSeed.ts.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";
const OPERATION = "01JR5NE0000000000000000300";
const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function expand(page: Page, row: Locator) {
  await row.click();
  await page.keyboard.press("ArrowRight");
  await expect(row).toHaveAttribute("aria-expanded", "true");
}

async function createOperation(page: Page) {
  const outcome = await page.evaluate(
    async (element) => {
      const res = await fetch("/api/model/batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ operations: [{ op: "create", element }] }),
      });
      return (await res.json()) as { outcome: string };
    },
    {
      kind: "operation",
      id: OPERATION,
      name: "issue",
      package: BILLING,
      subject: INVOICE,
      effect: "change",
      input: [
        { id: "01JR5NE0000000000000000301", name: "dueDays", type: "int32", required: true },
        { id: "01JR5NE0000000000000000302", name: "attachment", file: true },
      ],
      outcomes: [{ id: "01JR5NE0000000000000000303", name: "issued" }],
    },
  );
  expect(outcome.outcome).toBe("saved");
}

test("an action's call is added with a typed value, shows in the Calls column, and the simulation records it without running it", async ({ page }) => {
  await openEditor(page);
  await createOperation(page);
  await workspace(page, "Processes");
  const side = explorer(page);
  await expand(page, side.getByTestId("explorer-folder-Billing"));
  await expand(page, side.locator('[data-key$="/use:orchestration"]'));
  await side.getByTestId("explorer-row-Purchase approval").dblclick();
  const editor = page.getByTestId("process-editor");
  await editor.getByRole("tab", { name: "Transitions", exact: true }).click();

  const grid = editor.getByTestId("actions-grid");
  await expect(grid.getByText("Calls", { exact: true })).toBeVisible();
  await grid.getByRole("button", { name: "Edit the call of sendReminder" }).click();
  const dialog = page.getByTestId("calls-dialog");
  await dialog.getByLabel("Operation", { exact: true }).selectOption({ label: "issue" });

  // The Inputs grid: Input, Type, Required, Value. A required input left empty says MQ9026; a file is supplied by the system.
  const due = dialog.locator('[data-testid="call-input-row"][data-member="dueDays"]');
  await expect(due).toContainText("int32");
  await expect(due.getByTestId("call-input-missing")).toContainText("MQ9026");
  await expect(dialog.locator('[data-testid="call-input-row"][data-member="attachment"]').getByTestId("call-input-supplied")).toHaveText(
    "Supplied by your system",
  );
  await due.getByLabel("Value of dueDays").fill("30");
  await expect(due.getByTestId("value-preview")).toContainText("Type int32");
  await expect(due.getByTestId("call-input-missing")).toHaveCount(0);
  await dialog.getByTestId("calls-apply").click();
  await expect(page.getByTestId("calls-dialog")).toHaveCount(0);
  await expect(grid.getByText("issue(dueDays = 30)")).toBeVisible();

  // Simulate: advance time to the reminder timer; the action called the operation, recorded and not run.
  await editor.getByRole("tab", { name: "Chart", exact: true }).click();
  const panel = editor.getByTestId("simulation-panel");
  await page.keyboard.press("Alt+Shift+J");
  await page.keyboard.press("Alt+Shift+P");
  const toggle = panel.getByRole("button", { name: /simulation panel/ }).first();
  if ((await toggle.getAttribute("aria-expanded")) !== "true") await toggle.click();
  await panel.getByTestId("enabled-row").filter({ hasText: "submit" }).getByTestId("enabled-raise").click();
  await expect(panel.getByTestId("trace-row")).toHaveCount(2);
  await panel.getByTestId("enabled-row").filter({ hasText: "checkBudget done" }).getByTestId("enabled-raise").click();
  await panel.getByTestId("pending-row").filter({ hasText: "complianceReview" }).getByRole("button", { name: "Done" }).click();
  await expect(panel.getByTestId("configuration-row")).toHaveText(["Approval"]);
  await panel.getByTestId("time-advance").click();
  // The engine also notes the call ("recorded, not run"), so the call's own line is the one asked for.
  await expect(panel.locator('[data-testid="last-step-row"][data-kind="call"]')).toContainText("called issue (dueDays = 30), recorded, not run");
});
