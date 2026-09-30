// Database schemas (erratum E26): New schema… on a database row, then the database inspector's Schemas section renames it
// and makes it the default; with two schemas the tree shows a row per schema and the entity tables follow the default.
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });
const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();

test("New schema, rename, set default: an entity's table shows under the default schema", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const tree = side.getByTestId("explorer-tree");
  await workspace(page, "Databases");
  const main = tree.getByTestId("explorer-folder-main");

  await main.click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "New schema…" }).click();
  const dialog = page.getByTestId("schema-name-dialog");
  await dialog.getByLabel("Name").fill("audit");
  await dialog.getByTestId("schema-name-save").click();
  await expect(dialog).toHaveCount(0);

  await chevron(main).click();
  await expect(tree.getByTestId("explorer-folder-audit")).toBeVisible();
  await expect(tree.getByTestId("explorer-folder-billing")).toContainText("default");

  // The inspector's Schemas section: rename audit to ledger, then make it the default.
  await main.click();
  const schemas = page.getByTestId("database-schemas");
  await schemas.getByTestId("schema-rename-audit").click();
  await dialog.getByLabel("Name").fill("ledger");
  await dialog.getByTestId("schema-name-save").click();
  await expect(dialog).toHaveCount(0);
  await schemas.getByTestId("schema-default-ledger").click();
  await expect(schemas.getByTestId("schema-row-ledger")).toContainText("Default schema");

  // Selecting the database row toggles it; open it again.
  if ((await main.getAttribute("aria-expanded")) !== "true") await chevron(main).click();
  const ledger = tree.getByTestId("explorer-folder-ledger");
  await expect(ledger).toContainText("default");
  await chevron(ledger).click();
  await chevron(tree.getByTestId("explorer-folder-Tables").first()).click();
  await expect(tree.locator('[data-testid$="-invoices"]').first()).toBeVisible();
});
