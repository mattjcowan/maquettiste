// Creating elements (explorer-redesign.md 1.8): the first-run panel of an empty model, New actions from a domain's
// context menu with the picker on that domain, and the New button of the Databases and Diagrams explorers.
// Mock-only: it starts from the empty mock model and writes new elements.
import AxeBuilder from "@axe-core/playwright";
import { expect, test, workspace } from "./fixtures";

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });

async function create(page: import("@playwright/test").Page, name: string, domain?: string) {
  const dialog = page.getByTestId("new-element-dialog");
  await expect(dialog).toBeVisible();
  if (domain !== undefined) await expect(dialog.locator("#new-element-domain option:checked")).toHaveText(domain);
  await dialog.getByLabel("Name").fill(name);
  await dialog.getByTestId("new-element-create").click();
  await expect(dialog).toHaveCount(0);
}

test("an empty model offers a first-run panel; a domain's menu creates in that domain", async ({ page }) => {
  await page.goto("/?mock=empty");
  const first = page.getByTestId("first-run");
  await expect(first).toBeVisible();
  await expect(page.getByText("No diagrams or domains yet")).toHaveCount(0);
  expect((await new AxeBuilder({ page }).include('[data-testid="first-run"]').analyze()).violations).toEqual([]);
  await expect(explorer(page).getByTestId("explorer-create-package")).toBeVisible();

  await first.getByTestId("first-run-create-package").click();
  await expect(page.getByTestId("new-element-dialog")).toHaveAttribute("data-kind", "package");
  expect((await new AxeBuilder({ page }).include('[data-testid="new-element-dialog"]').analyze()).violations).toEqual([]);
  await create(page, "Shipping");
  const side = explorer(page);
  const domain = side.getByTestId("explorer-domain-Shipping");
  await expect(domain).toBeVisible();
  await expect(page.getByTestId("element-editor")).toHaveAttribute("data-kind", "package");

  // New entity from the domain's menu: the picker starts on the domain; the entity lands in its Entities folder.
  await domain.click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "New entity" }).click();
  await create(page, "Parcel", "Shipping");
  await expect(side.getByTestId("explorer-row-Parcel")).toBeVisible();
  await expect(page.getByTestId("element-editor")).toHaveAttribute("data-kind", "entity");

  // New enum and New sub-domain from the same menu.
  await domain.click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "New enum" }).click();
  await create(page, "ParcelStatus", "Shipping");
  await expect(side.getByTestId("explorer-row-ParcelStatus")).toBeVisible();
  await domain.click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "New sub-domain" }).click();
  await create(page, "Tracking", "Shipping");
  await expect(side.getByTestId("explorer-domain-Tracking")).toBeVisible();

  // A relationship between two entities of the domain.
  await domain.click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "New relationship" }).click();
  await create(page, "ParcelParcels", "Shipping");
  await expect(page.getByTestId("element-editor")).toHaveAttribute("data-kind", "relation");
});

test("the Databases and Diagrams explorers create from their New button", async ({ page }) => {
  await page.goto("/?mock=empty");
  await expect(page.getByTestId("first-run")).toBeVisible();

  await workspace(page, "Databases");
  await expect(explorer(page).getByTestId("explorer-create-database")).toBeVisible();
  await explorer(page).getByTestId("explorer-new").click();
  await page.getByRole("menuitem", { name: "New database" }).click();
  await create(page, "reporting");
  await expect(explorer(page).getByText("reporting").first()).toBeVisible();
  // The Database screen shows the new database at once, not "No databases" until the project is read again.
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await expect(page.locator("#database-picker option:checked")).toHaveText("reporting");
  await expect(page.getByText("No databases")).toHaveCount(0);

  await workspace(page, "Diagrams");
  await explorer(page).getByTestId("explorer-create-diagram").click();
  await create(page, "Overview");
  await expect(explorer(page).getByTestId("explorer-row-Overview")).toBeVisible();
  await expect(page.locator("#diagram-picker option:checked")).toHaveText("Overview");
});
