// The explorer close-out (explorer-redesign.md §6): Promote to entity, Tag… and Set category… from the row menu on
// one row and on a multi-selection, the pinned second explorer's own selection, and the Database screen above
// 300 tables (the list-and-DDL form, then a table with its foreign-key neighbours).
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function menu(page: Page, row: string, item: string) {
  await explorer(page).getByTestId(`explorer-row-${row}`).first().click({ button: "right" });
  await page.getByRole("menuitem", { name: item }).click();
}

test("Promote to entity rewrites the uses, and refuses a use it cannot rewrite", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("Money");
  await menu(page, "Money", "Promote to entity");
  // Money is an attribute type of a relationship: that use cannot become a relationship.
  await expect(page.getByTestId("promote-blocked")).toBeVisible();
  await expect(page.getByTestId("promote-confirm")).toBeDisabled();
  await page.getByRole("button", { name: "Cancel" }).click();

  await side.getByLabel("Search the model").fill("EmailAddress");
  await menu(page, "EmailAddress", "Promote to entity");
  await expect(page.getByTestId("promote-uses")).toContainText("New relationship Customer");
  await page.getByTestId("promote-confirm").click();
  await expect(page.getByText(/Promoted EmailAddress to an entity; 1 use became a relationship/)).toBeVisible();
  await expect(page.getByTestId("inspector-title")).toHaveText("EmailAddress");
});

test("Tag… on a row and Set category… on a multi-selection save as one batch", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("Invoice");
  await menu(page, "Invoice", "Tag…");
  await page.locator("#mark-value").selectOption("pii");
  await page.getByTestId("mark-apply").click();
  await expect(page.getByText("Tag pii: saved.")).toBeVisible();

  await side.getByTestId("explorer-row-Invoice").first().click();
  await side
    .getByTestId("explorer-row-InvoiceLine")
    .first()
    .click({ modifiers: ["ControlOrMeta"] });
  await menu(page, "InvoiceLine", "Set category…");
  const receivables = await page.locator("#mark-value option", { hasText: "Receivables" }).getAttribute("value");
  await page.locator("#mark-value").selectOption(receivables!);
  await page.getByTestId("mark-apply").click();
  await expect(page.getByText("Set category Receivables: 2 elements saved in one batch.")).toBeVisible();
});

test("the pinned second explorer keeps its own selection, and the inspector follows it", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const title = page.getByTestId("inspector-title");
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").first().click();
  await expect(title).toHaveText("Invoice");

  await side.getByTestId("explorer-header-menu").click();
  await page.getByRole("menuitem", { name: "Databases" }).click();
  const pinned = side.getByRole("tree", { name: "Databases" });
  await pinned.getByTestId("explorer-folder-main").click();
  await expect(title).toHaveText("main");
  // The Domain model keeps Invoice selected; selecting there again shows it.
  await expect(side.getByTestId("explorer-row-Invoice").first()).toHaveAttribute("aria-selected", "true");
  await side.getByTestId("explorer-row-Invoice").first().click();
  await expect(title).toHaveText("Invoice");
  await expect(pinned.getByTestId("explorer-folder-main")).toHaveAttribute("aria-selected", "true");
});

test("above 300 tables the Database screen shows the list form, then a table with its neighbours", async ({ page }) => {
  await page.goto("/?mock=wide");
  await expect(page.getByTestId("shell")).toBeVisible();
  await workspace(page, "Databases");
  await explorer(page).getByTestId("explorer-folder-main").dblclick();
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await expect(page.getByTestId("database-list-form")).toBeVisible();
  await expect(page.getByTestId("database-canvas-count")).toContainText("pick one to draw it with its neighbours");
  await expect(page.locator(".react-flow__node")).toHaveCount(0);

  const first = page.getByRole("list", { name: "Table list" }).getByRole("button").first();
  await first.click();
  await expect(page.getByTestId("database-list-form")).toHaveCount(0);
  await expect(page.getByTestId("database-canvas-count")).toContainText("and its neighbours");
  const drawn = await page.locator(".react-flow__node").count();
  expect(drawn).toBeGreaterThan(0);
  expect(drawn).toBeLessThanOrEqual(300);
});
