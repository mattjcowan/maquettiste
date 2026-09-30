// The Problems panel's quick fixes (phase-3-design.md 3): on the drifted model the MQ9203 diagnostic offers Sync enum,
// whose dry run is confirmed before it applies, and the panel re-validates to no problems (its undo step is covered
// by tests/unit/quick-fixes.test.ts). Mock-only: the drift scenario lives in mocks/model/processSeed.ts.
import { expect, openEditor, test } from "./fixtures";
import { scanIconControls, type IconControlScan } from "../icon-controls";

test("the MQ9203 quick fix syncs the enum after its dry run, as one change", async ({ page }) => {
  await openEditor(page, "/?mock=drift");
  await page.getByRole("tab", { name: /Problems/ }).click();
  const problems = page.getByTestId("problems-list");
  await expect(problems.getByTestId("problem-MQ9203")).toBeVisible();
  const fix = problems.getByTestId("problem-fix-MQ9203");
  await expect(fix).toHaveText("Sync enum");
  expect((await page.evaluate(scanIconControls as () => IconControlScan)).missing).toEqual([]);

  await fix.click();
  const plan = page.getByTestId("quick-fix-plan");
  await expect(plan).toContainText("Adds Disputed");
  await page.getByTestId("quick-fix-apply").click();
  await expect(plan).toHaveCount(0);
  await expect(page.getByRole("tab", { name: /Problems/ })).toContainText("0");
  await expect(page.getByTestId("problem-MQ9203")).toHaveCount(0);
});
