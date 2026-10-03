// Generate workspace (mock project): the plan summary lines, the changes grouped by unit and their filters, the
// per-file diff in the bottom panel, apply by plan id, job progress in Output, run history, and clearing it; the plan result
// line, and the Options popover (re-render every file, the hand-edit choice) sent with the plan request.
import { expect, test } from "./fixtures";

test("plan, read a diff, apply, output and history", async ({ page }) => {
  await page.goto("/generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  await expect(page.getByText("No plan yet")).toBeVisible();
  await expect(page.getByTestId("apply")).toBeDisabled();

  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-result")).toContainText(/Plan ready: \d+ files? to write \(\d+ added/);
  await expect(page.getByTestId("plan-result")).not.toContainText("hand edits");
  const summary = page.getByTestId("plan-summary");
  await expect(summary).toContainText("sql-ddl:");
  await expect(summary).toContainText("csharp-dapper:");
  const rows = page.getByTestId("changes").locator('[data-testid^="change-"]');
  const all = await rows.count();
  expect(all).toBeGreaterThan(0);

  // The pack filter narrows the table to one pack.
  await page.locator("#filter-pack").selectOption("sql-ddl");
  await expect(rows.first()).toContainText("sql-ddl");
  expect(await rows.count()).toBeLessThan(all);
  await page.locator("#filter-pack").selectOption("");

  // Selecting a file shows its unified diff.
  const first = rows.first();
  const path = (await first.getByRole("gridcell").nth(1).innerText()).trim();
  await first.click();
  const diff = page.getByTestId("diff-viewer");
  await expect(diff).toContainText(path);
  await expect(diff).toContainText("@@");

  await page.getByTestId("apply").click();
  await expect(page.getByTestId("apply-result")).toContainText("succeeded");

  await page.getByRole("tab", { name: "Output" }).click();
  const output = page.getByTestId("output-list");
  await expect(output).toContainText("Apply succeeded");
  await expect(output).toContainText("Apply: render");

  const history = page.getByTestId("history").getByRole("listitem");
  await expect(history).toHaveCount(2);
  await expect(history.first()).toContainText("apply");

  // A second plan after the apply has nothing left to write.
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-summary")).toContainText("nothing to write");
  await expect(page.getByTestId("plan-summary")).toContainText(/files? already match/);
  await expect(page.getByTestId("plan-summary")).not.toContainText("to add");
  await expect(page.getByTestId("plan-summary")).not.toContainText("to modify");
  await expect(page.getByTestId("apply")).toBeDisabled();
  await expect(page.getByTestId("plan-nothing-to-write")).toContainText("Apply has nothing to do");
  await expect(page.getByTestId("plan-result")).toHaveText("Plan ready: nothing to write, every file matches");
});

test("plan options: re-render every file and the hand-edit choice go with the plan request", async ({ page }) => {
  await page.goto("/generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  const options = page.getByTestId("generate-options");
  await expect(options).toHaveAttribute("title", "Plan options");
  await expect(page.getByTestId("generate-options-dot")).toHaveCount(0);

  await options.click();
  const panel = page.getByTestId("generate-options-panel");
  await expect(panel.getByTestId("plan-option-hand-edits").locator("option").first()).toHaveText(/^Project default \(.+\)$/);
  await panel.getByTestId("plan-option-force").click();
  await panel.getByTestId("plan-option-hand-edits").selectOption("overwrite");
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("generate-options-dot")).toBeVisible();

  const sent = page.waitForRequest((r) => r.method() === "POST" && new URL(r.url()).pathname === "/api/generate/plan");
  await page.getByTestId("plan").click();
  expect((await sent).postDataJSON()).toMatchObject({ force: true, handEdits: "overwrite" });
  await expect(page.getByTestId("plan-result")).toContainText("Plan ready");
  // Every unit renders again: the changes say why.
  const rows = page.getByTestId("changes").locator('[data-testid^="change-"]');
  await expect(rows.first()).toContainText("Forced: the run renders every unit");

  // Back to the defaults: neither is sent.
  await options.click();
  await panel.getByTestId("plan-option-force").click();
  await panel.getByTestId("plan-option-hand-edits").selectOption("");
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("generate-options-dot")).toHaveCount(0);
  const plain = page.waitForRequest((r) => r.method() === "POST" && new URL(r.url()).pathname === "/api/generate/plan");
  await page.getByTestId("plan").click();
  const body = (await plain).postDataJSON() as Record<string, unknown>;
  expect(body.force).toBeUndefined();
  expect(body.handEdits).toBeUndefined();
});

test("clear the run history after a plan", async ({ page }) => {
  await page.goto("/generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-summary")).toContainText("sql-ddl:");
  await expect(page.getByTestId("apply")).toBeEnabled();
  const clear = page.getByTestId("clear-run-history");
  await expect(clear).toBeEnabled();
  await expect(clear).toHaveAttribute("title", "Clear the run history and its stored plans");

  await clear.click();
  await expect(page.getByTestId("clear-run-history-dialog")).toContainText("a plan not yet applied must be made again");
  await page.getByTestId("clear-run-history-confirm").click();

  await expect(page.getByTestId("history")).toHaveText("No runs yet.");
  await expect(page.getByTestId("apply")).toBeDisabled();
  await expect(page.getByText("No plan yet")).toBeVisible();
  await expect(clear).toBeDisabled();
  await expect(page.getByText(/Run history cleared: \d+ runs? and \d+ stored plans? removed\./)).toBeVisible();
});
