// Table detail in the Databases explorer (explorer-redesign.md 1.3, EX step 13): a table expands into Columns, keys
// and indexes from its detail (E5f); opening the table shows it focused in the Database screen, whose table section has a
// filter. A table names no entity, so a column highlights nothing in the Domain model.
import { expect, openEditor, test, workspace, keepDdlOpen } from "./fixtures";

// The DDL preview is hidden by default; these specs read it.
test.beforeEach(({ page }) => keepDdlOpen(page));

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });
const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();

test("a table expands into its columns and opens on the Database screen", async ({ page }) => {
  await openEditor(page, "/");
  const side = explorer(page);
  const tree = side.getByTestId("explorer-tree");

  // Databases › main › billing › Tables › invoices: expanding loads the detail.
  await workspace(page, "Databases");
  await chevron(tree.getByTestId("explorer-folder-main")).click();
  await chevron(tree.getByTestId("explorer-folder-billing")).click();
  await chevron(tree.getByTestId("explorer-folder-Tables").first()).click();
  const table = tree.locator('[data-testid$="-invoices"]').first();
  await chevron(table).click();
  await expect(table).toHaveAttribute("aria-expanded", "true");
  for (const folder of ["Columns", "Primary key"]) await expect(tree.getByTestId(`explorer-folder-${folder}`)).toBeVisible();
  await chevron(tree.getByTestId("explorer-folder-Columns")).click();
  const column = tree.getByTestId("explorer-item-number");
  await expect(column).toBeVisible();
  await column.click();
  await expect(column).toHaveAttribute("aria-selected", "true");

  // Opening the table shows it focused in the Database screen; the table section filters.
  // A double click opens the row (as Enter does).
  await table.dblclick();
  const screen = page.getByTestId("database-workspace");
  await expect(screen).toBeVisible();
  await expect(screen.getByTestId("database-table-invoices")).toHaveAttribute("aria-current", "true");
  await expect(page.getByTestId("ddl-preview")).toContainText("invoices");
  await screen.getByLabel("Filter tables").fill("zzz");
  await expect(screen.getByTestId("database-tables-count")).toHaveText("0 tables match");
  await screen.getByLabel("Filter tables").fill("invoice");
  await expect(screen.getByTestId("database-table-invoices")).toBeVisible();
});

test("Go to table on a bound entity opens the table its binding writes", async ({ page }) => {
  await openEditor(page, "/");
  const side = explorer(page);
  // From the Domain model explorer, before the Databases explorer has loaded any table: the entity's index row names its table.
  await workspace(page, "Domain model");
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Go to table" }).click();
  await expect(side.getByRole("region", { name: "Databases explorer" })).toBeVisible();
  await expect(page.getByRole("complementary", { name: "Inspector" }).getByRole("region", { name: "Inspector: table invoices" })).toBeVisible();
});
