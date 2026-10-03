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

test("a file out of canonical form is rewritten from Problems, and the rest from Settings › Validation", async ({ page }) => {
  // `legacy`: maquettiste.json still sets the retired commit flag (MQ1010) and, with one entity file, is not canonical (MQ1003).
  await openEditor(page, "/?mock=legacy");
  await page.getByRole("tab", { name: /Problems/ }).click();
  const problems = page.getByTestId("problems-list");
  await expect(problems.getByTestId("problem-MQ1003")).toHaveCount(2);
  await expect(problems.getByTestId("problem-MQ1003").first()).toContainText("rewrite it from the Problems panel");
  await expect(problems.getByTestId("problem-MQ1010")).toHaveCount(1);
  const fix = problems.getByTestId("problem-MQ1010").locator("xpath=..").getByTestId("fix-canonical");
  await expect(fix).toHaveText("Rewrite in canonical form");
  expect((await page.evaluate(scanIconControls as () => IconControlScan)).missing).toEqual([]);

  await fix.click();
  await expect(page.getByText("Rewrote .maquettiste/maquettiste.json in canonical form.")).toBeVisible();
  await expect(problems.getByTestId("problem-MQ1010")).toHaveCount(0);
  await expect(problems.getByTestId("problem-MQ1003")).toHaveCount(1);

  await page.getByTestId("rail-settings").click();
  await page.getByRole("tab", { name: "Validation" }).click();
  const action = page.getByTestId("canonical-form-action");
  await expect(action.getByTestId("canonical-form-count")).toHaveText("1 model file is not in canonical form.");
  await action.getByTestId("format-all").click();
  await expect(action.getByTestId("canonical-form-count")).toHaveText("Every model file is in canonical form.");
  await expect(problems.getByTestId("problem-MQ1003")).toHaveCount(0);
});
