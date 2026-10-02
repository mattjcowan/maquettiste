// Deleting with a plan (engine-design.md 15.1): the inspector's delete reads what the delete would do, lists it by
// outcome, and offers "Delete and clear references" and "Delete with N dependents"; the cascade is one change and
// one undo step. The explorer's delete shows the same plan for what it deletes.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function selectEntity(page: Page, name: string) {
  const side = explorer(page);
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).click();
  await expect(page.getByTestId("inspector-title")).toHaveText(name);
}

test("an entity with relations: the plan names the relation, clearing alone cannot work, deleting with dependents undoes in one step", async ({ page }) => {
  await openEditor(page);
  await selectEntity(page, "Product");
  await page.getByRole("button", { name: "Delete Product" }).click();

  const dialog = page.getByTestId("delete-plan-dialog");
  await expect(dialog).toBeVisible();
  await expect(dialog.getByTestId("delete-plan-deletes")).toContainText("Will be deleted (1)");
  await expect(dialog.getByTestId("delete-plan-deletes")).toContainText("refers to");
  await expect(dialog.getByTestId("delete-plan-deletes")).toContainText("needs entity Product");
  await expect(dialog.getByTestId("delete-plan-removes")).toContainText("member Product");

  // Clearing references alone: the relation's end needs Product, so that way is closed.
  await dialog.getByTestId("delete-plan-tab-clear").click();
  await expect(dialog.getByTestId("delete-plan-refused")).toContainText("Cannot be resolved (1)");
  await expect(dialog.getByTestId("delete-plan-refused")).toContainText("refers to");
  await expect(dialog.getByTestId("delete-clear-references")).toBeDisabled();

  await dialog.getByTestId("delete-with-dependents").click();
  await expect(dialog).toHaveCount(0);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("Product");
  await expect(side.getByTestId("explorer-row-Product")).toHaveCount(0);
  await side.getByLabel("Search the model").fill("refers");
  await expect(side.getByTestId("explorer-row-refers to")).toHaveCount(0);

  // One undo step brings back the entity and its relation.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(side.getByTestId("explorer-row-refers to")).toBeVisible();
  await side.getByLabel("Search the model").fill("Product");
  await expect(side.getByTestId("explorer-row-Product")).toBeVisible();
});

test("a database with tables: deleting with dependents takes its tables, views, sequences, routines, database types, SQL objects and mappings", async ({
  page,
}) => {
  await openEditor(page);
  await workspace(page, "Databases");
  const tree = explorer(page).getByTestId("explorer-tree");
  await tree.getByTestId("explorer-folder-main").click();
  await expect(page.getByTestId("inspector-title")).toHaveText("main");
  await page.getByRole("button", { name: "Delete main" }).click();

  const dialog = page.getByTestId("delete-plan-dialog");
  const deletes = dialog.getByTestId("delete-plan-deletes");
  await expect(deletes).toContainText("Will be deleted (10)");
  for (const name of [
    "Invoice register",
    "outstanding_invoices",
    "invoice_number_seq",
    "invoice_total",
    "close_period",
    "email_address",
    "invoice_state",
    "reporting_read",
    "Invoice in main",
    "settles in main",
  ])
    await expect(deletes).toContainText(name);
  await expect(deletes).toContainText("needs database main");
  await expect(dialog.getByTestId("delete-with-dependents")).toHaveText("Delete with 10 dependents");

  await dialog.getByTestId("delete-with-dependents").click();
  await expect(dialog).toHaveCount(0);
  await expect(tree.getByTestId("explorer-folder-main")).toHaveCount(0);
});

test("the explorer's delete shows the same plan", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("Product");
  await side.getByTestId("explorer-row-Product").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Delete" }).click();

  const dialog = page.getByTestId("delete-plan-dialog");
  await expect(dialog.getByTestId("delete-plan-deletes")).toContainText("refers to");
  await dialog.getByTestId("delete-with-dependents").click();
  await expect(dialog).toHaveCount(0);
  await expect(side.getByTestId("explorer-row-Product")).toHaveCount(0);
});
