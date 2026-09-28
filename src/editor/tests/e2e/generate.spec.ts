// Generate workspace (mock project): plan summary counts, the changes table and its filters, the
// per-file diff in the bottom panel, apply by plan id, job progress in Output, and run history.
import { expect, test } from "./fixtures";

test("plan, read a diff, apply, output and history", async ({ page }) => {
  await page.goto("/generate");
  await expect(page.getByTestId("generate-workspace")).toBeVisible();
  await expect(page.getByText("No plan yet")).toBeVisible();
  await expect(page.getByTestId("apply")).toBeDisabled();

  await page.getByTestId("plan").click();
  const summary = page.getByTestId("plan-summary");
  await expect(summary).toContainText("sql-ddl:");
  await expect(summary).toContainText("csharp-dapper:");
  const rows = page.getByTestId("changes").getByRole("row");
  const all = await rows.count();
  expect(all).toBeGreaterThan(0);

  // The pack filter narrows the table to one pack.
  await page.locator("#filter-pack").selectOption("sql-ddl");
  await expect(rows.first()).toContainText("sql-ddl");
  expect(await rows.count()).toBeLessThan(all);
  await page.locator("#filter-pack").selectOption("");

  // Selecting a file shows its unified diff.
  const first = rows.first();
  const path = (await first.getByRole("cell").nth(1).innerText()).trim();
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
  await expect(page.getByTestId("plan-summary")).toContainText("unchanged");
  await expect(page.getByTestId("plan-summary")).not.toContainText("added");
  await expect(page.getByTestId("plan-summary")).not.toContainText(/: \d+ changed/);
  await expect(page.getByTestId("apply")).toBeDisabled();
});
