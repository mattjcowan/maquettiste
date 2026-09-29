// Explicit mapping (engine-design.md D46, explorer-redesign.md 1.3): a new database holds nothing until something is
// mapped to it; mapping one domain from its menu fills the database with that domain's tables only.
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });
const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();

test("a second database starts empty and holds only the domain mapped to it", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const tree = side.getByTestId("explorer-tree");

  // New database: "Map domains by convention" starts on None.
  await workspace(page, "Databases");
  await side.getByTestId("explorer-new").click();
  await page.getByRole("menuitem", { name: "New database" }).click();
  await expect(page.locator("#new-element-convention")).toHaveValue("none");
  await page.locator("#new-element-name").fill("archive");
  await page.getByTestId("new-element-create").click();
  const archive = tree.getByTestId("explorer-folder-archive");
  await expect(archive).toBeVisible();
  await chevron(archive).click();
  await expect(tree.getByText("Nothing is mapped here yet", { exact: false })).toBeVisible();

  // Map the Catalog domain (a sub-domain of Billing) from its menu.
  await workspace(page, "Domain model");
  await chevron(side.getByTestId("explorer-domain-Billing")).click();
  await side.getByTestId("explorer-domain-Catalog").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Map to database…" }).click();
  const dialog = page.getByTestId("map-to-database-dialog");
  await dialog.getByLabel("Database").selectOption({ label: "archive" });
  await dialog.getByTestId("map-to-database-apply").click();
  await expect(dialog).toHaveCount(0);

  // The Databases explorer shows Catalog's table under archive, and none of Billing's own.
  await workspace(page, "Databases");
  await expect(archive).toContainText("1 table");
  await chevron(tree.getByTestId("explorer-folder-Default schema")).click();
  await chevron(tree.getByTestId("explorer-folder-Tables").first()).click();
  await expect(tree.locator('[data-testid$="-products"]').first()).toBeVisible();
  await expect(tree.getByText("Nothing is mapped here yet", { exact: false })).toHaveCount(0);
  await expect(archive).toContainText("1 table");
  for (const billing of ["invoices", "customers", "payments"]) await expect(tree.locator(`[data-testid$="-${billing}"]`)).toHaveCount(0);
});
