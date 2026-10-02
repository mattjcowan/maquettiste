// New table… (the owner: "where do i create a new table, view, function, procedure in the UI?"): from a database's row menu in
// the Databases explorer, a designed table is created in one step and opens in its editor; its Columns tab adds and edits
// columns in the column grid, its Keys tab adds a unique constraint; the table shows in the Database screen's list and DDL
// preview; each gesture is one undo step, and undoing the create removes the table.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });
const editor = (page: Page) => page.getByTestId("element-editor");

test("New table… creates a designed table, its editor edits columns and keys, and undo takes each step back", async ({ page }) => {
  await openEditor(page);
  await workspace(page, "Databases");
  const main = explorer(page).getByTestId("explorer-tree").getByTestId("explorer-folder-main");
  await main.click({ button: "right" });
  const menu = page.getByTestId("row-menu");
  await expect(menu.getByRole("menuitem", { name: "New schema…" })).toBeVisible();
  await menu.getByRole("menuitem", { name: "New table…" }).click();

  const dialog = page.getByTestId("new-database-object-dialog");
  await expect(dialog).toHaveAttribute("data-kind", "table");
  await expect(dialog.getByLabel("Schema")).toHaveValue("");
  await expect(dialog.getByRole("option", { name: "billing (default)" })).toHaveCount(1);
  await expect(dialog.getByText("Designed table (its own columns)")).toBeVisible();
  // The identifier rule is checked as typed.
  await dialog.getByLabel("Name").fill("audit log");
  await expect(page.getByTestId("new-db-name-problem")).toHaveText("Use letters, digits and underscores, not starting with a digit.");
  await expect(page.getByTestId("new-db-create")).toBeDisabled();
  await dialog.getByLabel("Name").fill("invoices");
  await expect(page.getByTestId("new-db-name-problem")).toHaveText("invoices is already a table, view or sequence in billing.");
  await dialog.getByLabel("Name").fill("audit_log");
  await page.getByTestId("new-db-create").click();
  await expect(dialog).toHaveCount(0);

  // The editor opens in front of the Database screen, on the new table with its id column.
  await expect(editor(page)).toHaveAttribute("data-kind", "table");
  await expect(page.getByTestId("editor-title")).toHaveText("audit_log");
  await editor(page).getByRole("tab", { name: "Columns" }).click();
  // The Database screen stays mounted behind the editor, with its own column grid.
  const grid = editor(page).getByTestId("column-grid");
  await expect(grid.getByTestId("column-row-id")).toBeVisible();
  await expect(grid.getByTestId("column-row-id").locator('[data-column="nativeType"]')).toHaveText("bigint");

  // Add column, then rename it in the grid: two saves, two undo steps.
  await page.getByTestId("table-add-column").click();
  await expect(grid.getByTestId("column-row-column_1")).toBeVisible();
  await grid.getByTestId("column-row-column_1").locator('[data-column="name"]').dblclick();
  await grid.getByRole("textbox", { name: "Name of column_1" }).fill("created_at");
  await page.keyboard.press("Enter");
  await expect(grid.getByTestId("column-row-created_at")).toBeVisible();

  // Keys: the primary key is there; Add unique starts on the first column.
  await editor(page).getByRole("tab", { name: "Keys" }).click();
  await expect(page.getByTestId("keys-primary")).toContainText("id");
  await page.getByTestId("keys-add-uniques").click();
  await expect(page.getByTestId("keys-uniques")).toBeVisible();

  // A column picked in the grid shows in the inspector; Delete column removes it, and Undo brings it back.
  await editor(page).getByRole("tab", { name: "Columns" }).click();
  await grid.getByTestId("column-row-created_at").locator('[data-column="name"]').click();
  await expect(page.getByTestId("table-inspector-column")).toContainText("Column created_at");
  await page.getByTestId("table-delete-column").click();
  await expect(grid.getByTestId("column-row-created_at")).toHaveCount(0);
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(grid.getByTestId("column-row-created_at")).toBeVisible();

  // The table's explorer row opens its editor (Enter or a double click), in front of the Database screen.
  await page.getByTestId("editor-tab-screen").click();
  await expect(editor(page)).toHaveCount(0);
  const tree = explorer(page).getByTestId("explorer-tree");
  for (const folder of ["main", "billing", "Tables"]) {
    const row = tree.getByTestId(`explorer-folder-${folder}`).first();
    if ((await row.getAttribute("aria-expanded")) !== "true") await row.locator("span[aria-hidden]").first().click();
    await expect(row).toHaveAttribute("aria-expanded", "true");
  }
  const row = tree.getByRole("treeitem", { name: /^audit_log\b/ });
  await row.dblclick();
  await expect(editor(page)).toHaveAttribute("data-kind", "table");

  // The Database screen lists the table and its DDL preview renders it.
  await page.getByTestId("editor-tab-screen").click();
  await page.getByTestId("database-table-audit_log").click();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("sql-ddl/table · audit_log");
  await expect(page.getByTestId("ddl-preview")).toContainText("CREATE TABLE billing.audit_log");
  await expect(page.getByTestId("ddl-preview")).toContainText("created_at");

  // Undo: the unique, the rename, the added column, then the create.
  const undo = page.getByRole("button", { name: "Undo" });
  await undo.click();
  await undo.click();
  await expect(page.getByTestId("ddl-preview")).toContainText("column_1");
  await undo.click();
  await expect(page.getByTestId("ddl-preview")).not.toContainText("column_1");
  await undo.click();
  await expect(page.getByTestId("database-table-audit_log")).toHaveCount(0);
});

test("the explorer's New menu offers the database's New actions, and New table… points to the Mappings tab for a projected table", async ({ page }) => {
  await openEditor(page);
  await workspace(page, "Databases");
  const tree = explorer(page).getByTestId("explorer-tree");
  await tree.getByTestId("explorer-folder-main").click();
  await explorer(page).getByTestId("explorer-new").click();
  for (const kind of ["schema", "table", "view", "sequence"]) await expect(page.getByTestId(`explorer-new-${kind}`)).toBeVisible();
  await page.getByTestId("explorer-new-table").click();
  await expect(page.getByTestId("new-database-object-dialog")).toHaveAttribute("data-kind", "table");
  // A projected table comes from a mapping: the dialog's link opens the Mappings tab.
  await page.getByTestId("new-db-open-mappings").click();
  await expect(page.getByTestId("new-database-object-dialog")).toHaveCount(0);
  await expect(page).toHaveURL(/\/mappings/);
});
