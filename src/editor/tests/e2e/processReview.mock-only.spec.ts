// The process editor's review fixes (phase-3-design.md 6.2 and 3): every grid row is ROW_H high; the gate, Replay and
// Refresh row actions have keys (Ctrl+G, R, Shift+R); Use goes through set-lifecycle as one undo step; and on the
// `lifecycle` mock scenario each quick-fix kind applies: an operation with a picker (MQ9001), a plain update (MQ9205)
// and set-lifecycle (MQ9201). Mock-only: the processes live in mocks/model/processSeed.ts.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function openProcess(page: Page, row: string, path = "/") {
  await openEditor(page, path);
  await workspace(page, "Processes");
  const side = explorer(page);
  const billing = side.getByTestId("explorer-folder-Billing");
  await billing.click();
  await page.keyboard.press("ArrowRight");
  await expect(billing).toHaveAttribute("aria-expanded", "true");
  await side.getByTestId(`explorer-row-${row}`).dblclick();
  const editor = page.getByTestId("process-editor");
  await expect(editor).toBeVisible();
  // The editor opens on its chart (round P4); these specs start from the States tab.
  await tab(editor, "States").click();
  return editor;
}

const tab = (editor: Locator, name: string) => editor.getByRole("tab", { name, exact: true });

async function rowHeights(editor: Locator): Promise<number[]> {
  return editor.locator('[data-testid$="-grid-row"]').evaluateAll((rows) => rows.map((r) => Math.round(r.getBoundingClientRect().height)));
}

test("every process grid row is ROW_H high, and the row actions have keys", async ({ page }) => {
  const editor = await openProcess(page, "Purchase approval");
  const rowH = await page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--mq-row-h")));
  for (const name of ["States", "Transitions", "Events", "Scenarios"]) {
    await tab(editor, name).click();
    await expect(editor.locator('[data-testid$="-grid-row"]').first()).toBeVisible();
    const heights = await rowHeights(editor);
    expect(
      heights.every((h) => h === rowH),
      `${name}: ${heights.join(",")}`,
    ).toBe(true);
  }

  // Ctrl+G adds a gate to an event transition, and again removes it; each one undo step.
  await tab(editor, "Transitions").click();
  const transitions = editor.getByTestId("transitions-grid-row");
  const gated = editor.getByTestId("gate-badge");
  const before = await gated.count();
  const plain = transitions.filter({ hasNot: page.getByTestId("gate-badge") }).first();
  await plain.locator('[data-column="source"]').click();
  await page.keyboard.press("ControlOrMeta+g");
  await expect(gated).toHaveCount(before + 1);
  await page.keyboard.press("ControlOrMeta+z");
  await expect(gated).toHaveCount(before);

  // R replays a scenario from the keyboard.
  await tab(editor, "Scenarios").click();
  await expect(editor.getByText("R replays")).toBeVisible();
  const quick = editor.getByTestId("scenarios-grid-row").filter({ hasText: "Quick approval" });
  await quick.locator('[data-column="name"]').click();
  await page.keyboard.press("r");
  await expect(quick.getByTestId("scenario-status")).toHaveText("failed at step 2");
});

test("an invoke is added on the selected state, and F12 on a transition's source selects that state", async ({ page }) => {
  const editor = await openProcess(page, "InvoiceLifecycle");
  const states = editor.getByTestId("states-grid-row");
  await states.first().locator('[data-column="name"]').click();
  const invokes = editor.getByTestId("invokes-grid");
  await expect(invokes).toContainText("Draft invokes nothing.");
  await invokes.getByTestId("invokes-grid-add").click();
  await expect(editor.getByTestId("invokes-grid-row")).toHaveCount(1);
  await expect(states.first().locator('[data-column="invokes"]')).toHaveText("invoke");

  await tab(editor, "Transitions").click();
  const second = editor.getByTestId("transitions-grid-row").nth(1);
  await second.locator('[data-column="source"]').click();
  await page.keyboard.press("F12");
  await expect(tab(editor, "States")).toHaveAttribute("aria-selected", "true");
  await expect(states.nth(1).locator('[aria-selected="true"]')).toHaveCount(1);
});

test("Use turns a lifecycle into an orchestration with its entity, as one undo step", async ({ page }) => {
  const editor = await openProcess(page, "InvoiceLifecycle");
  const row = explorer(page).getByTestId("explorer-row-InvoiceLifecycle");
  await editor.getByTestId("editor-details-toggle").click();
  await editor.getByLabel("Use").selectOption("orchestration");
  await expect(row).toContainText("orchestration");
  await expect(page.getByRole("tab", { name: /Problems/ })).toContainText("0");
  await editor.getByTestId("states-grid").click();
  await page.keyboard.press("ControlOrMeta+z");
  await expect(row).toContainText("lifecycle · Invoice");
  await expect(editor.getByLabel("Use")).toHaveValue("lifecycle");
});

test("each quick-fix kind applies on the lifecycle scenario", async ({ page }) => {
  await openEditor(page, "/?mock=lifecycle");
  await page.getByRole("tab", { name: /Problems/ }).click();
  const problems = page.getByTestId("problems-list");

  // A plain update: the bound attribute's default.
  await expect(problems.getByTestId("problem-fix-MQ9205")).toHaveText("Set default to Draft");
  await problems.getByTestId("problem-fix-MQ9205").click();
  await expect(problems.getByTestId("problem-MQ9205")).toHaveCount(0);

  // The set-lifecycle operation: the subject names its lifecycle.
  await problems.getByTestId("problem-fix-MQ9201").click();
  await expect(problems.getByTestId("problem-MQ9201")).toHaveCount(0);

  // The set-initial operation, with a picker of the direct children.
  await problems.getByTestId("problem-fix-MQ9001").click();
  await expect(page.getByTestId("quick-fix-initial")).toBeVisible();
  await page.getByTestId("quick-fix-apply").click();
  await expect(problems.getByTestId("problem-MQ9001")).toHaveCount(0);
  await expect(page.getByRole("tab", { name: /Problems/ })).toContainText("0");
});
