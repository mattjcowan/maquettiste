// The Problems panel's filter chips and Validate again (problems/ProblemsPanel.tsx): each severity shows its count,
// unticking one hides its findings and the tab's badge follows, the choice survives a reload, and Validate again
// runs the rules without an error.
import { expect, openEditor, test } from "./fixtures";

test("the severity chips filter the list, the badge follows, the choice is kept, and Validate again runs", async ({ page }) => {
  await openEditor(page, "/?mock=lifecycle");
  await page.getByRole("tab", { name: /Problems/ }).click();
  const header = page.getByTestId("problems-header");
  await expect(header).toBeVisible();
  const list = page.getByTestId("problems-list");
  const count = async () => list.locator('[data-testid^="problem-MQ"]').count();
  const badge = page.getByTestId("problem-count");
  const total = await count();
  expect(total).toBeGreaterThan(0);
  await expect(badge).toHaveText(String(total));

  // Hide the warnings: the list and the badge lose exactly the warnings, and the chip says how many are hidden.
  const warnings = header.getByTestId("problem-filter-warnings");
  const warningCount = Number((await warnings.textContent())!.replace(/\D/g, ""));
  await warnings.click();
  await expect(warnings).toHaveAttribute("aria-pressed", "false");
  await expect.poll(count).toBe(total - warningCount);
  await expect(badge).toHaveText(String(total - warningCount));
  if (warningCount) await expect(page.getByTestId("problems-hidden")).toHaveText(`${warningCount} hidden`);

  await page.reload();
  await page.getByRole("tab", { name: /Problems/ }).click();
  await expect(page.getByTestId("problem-filter-warnings")).toHaveAttribute("aria-pressed", "false");
  await page.getByTestId("problem-filter-warnings").click();
  await expect.poll(count).toBe(total);

  await page.getByTestId("problems-validate").click();
  await expect.poll(count).toBe(total);
});
