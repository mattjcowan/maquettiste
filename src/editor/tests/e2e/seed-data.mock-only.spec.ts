// Seed data for an entity (the entity editor's Seed data tab is the Reference data screen's Rows grid): New seed on
// request, two rows entered by keyboard, a CSV imported with its preview, the seed exported as CSV, and all seed data
// exported as one ZIP from the domain's menu.
import { expect, openEditor, test } from "./fixtures";

test("an entity's seed: create it, type two rows, import a CSV, export it", async ({ page }) => {
  await openEditor(page);
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill("Customer");
  await side.getByTestId("explorer-row-Customer").first().click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Edit seed data" }).click();

  // No seed yet: nothing is created until New seed.
  await page.getByTestId("new-seed").click();
  const grid = page.getByTestId("rows-grid");
  await expect(grid).toBeVisible();
  await expect(grid.getByRole("columnheader")).toHaveText(["Row", "id", "name", "email", "customerSince"]);
  await expect(page.getByRole("button", { name: "Import CSV" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Export CSV" })).toBeEnabled();

  // Two rows by keyboard: Ctrl+Enter inserts and edits, Tab moves, Enter edits and commits.
  await grid.focus();
  await page.keyboard.press("Control+Enter");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");
  await page.keyboard.type("Acme");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");
  await page.keyboard.type("hello@acme.test");
  await page.keyboard.press("Enter");
  await page.keyboard.press("Control+Enter");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");
  await page.keyboard.type("Globex");
  await page.keyboard.press("Enter");
  await expect(page.getByTestId("rows-status")).toContainText("2 rows");
  await expect(grid.locator('[data-cell="0:1"]')).toHaveText("Acme");
  await expect(grid.locator('[data-cell="0:2"]')).toHaveText("hello@acme.test");
  await expect(grid.locator('[data-cell="1:1"]')).toHaveText("Globex");

  // Import CSV: preview, then apply as one save.
  await page.getByRole("button", { name: "Import CSV" }).click();
  const dialog = page.getByRole("dialog", { name: /Import CSV/ });
  await dialog.getByLabel("Or paste the CSV text").fill("name,email\nInitech,info@initech.test\n");
  await dialog.getByRole("button", { name: "Preview" }).click();
  await expect(dialog.getByTestId("csv-preview")).toContainText("1 row added, 0 rows changed, 0 rows removed");
  await dialog.getByRole("button", { name: "Apply" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId("rows-status")).toContainText("3 rows");
  await expect(grid.locator('[data-cell="2:1"]')).toHaveText("Initech");

  // Export CSV downloads the seed, named after it.
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Export CSV" }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe("Customer.csv");

  // The domain's menu exports the seeds of that domain and its sub-domains as one ZIP.
  await side.getByLabel("Search the model").fill("");
  const zip = page.waitForEvent("download");
  await side.getByTestId("explorer-domain-Billing").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Export this domain's seed data" }).click();
  expect((await zip).suggestedFilename()).toBe("Billing seed data.zip");
});
