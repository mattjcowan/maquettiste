// Table detail in the Databases explorer (explorer-redesign.md 1.3, EX step 13): a table expands into Columns, keys
// and indexes from its detail (E5f); selecting a column highlights, in the Domain model, the entity and the attribute
// mapped onto it; opening the table shows it focused in the Database screen, whose table section has a filter.
import { expect, openEditor, test, workspace, keepDdlOpen } from "./fixtures";

// The DDL preview is hidden by default; these specs read it.
test.beforeEach(({ page }) => keepDdlOpen(page));

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });
const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();

test("a table expands into its columns, and a column highlights its attribute", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const tree = side.getByTestId("explorer-tree");

  // Show Invoice's attributes in the Domain model first: highlighting never expands a row.
  await chevron(side.getByTestId("explorer-domain-Billing")).click();
  const entities = tree.getByTestId("explorer-folder-Entities").first();
  await chevron(entities).click();
  const invoice = tree.getByTestId("explorer-row-Invoice");
  await chevron(invoice).click();
  await expect(invoice).toHaveAttribute("aria-expanded", "true");
  const attributes = tree.getByTestId("explorer-folder-Attributes").first();
  await chevron(attributes).click();
  await expect(tree.getByTestId("explorer-item-number")).toBeVisible();

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

  // Mapped by: Invoice and its number attribute are tinted in the Domain model.
  await workspace(page, "Domain model");
  await expect(tree.getByTestId("explorer-row-Invoice")).toHaveAttribute("data-related", "true");
  await expect(tree.getByTestId("explorer-item-number")).toHaveAttribute("data-related", "true");
  await expect(tree.getByTestId("explorer-item-total")).toHaveCount(1);
  await expect(tree.getByTestId("explorer-item-notes")).not.toHaveAttribute("data-related", "true");

  // Opening the table shows it focused in the Database screen; the table section filters.
  await workspace(page, "Databases");
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
