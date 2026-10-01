// The entity editor's Inheritance tab (disabled outside a hierarchy; base, derived and the strategy per database),
// its New relationship… button, and the reference type menu in the Reference data explorer (Duplicate, Rename,
// Delete with the usage check) (explorer-redesign.md 3.6, reference-types-seeds-localization.md 4.2).
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function openEntity(page: Page, name: string) {
  const side = explorer(page);
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).dblclick();
  const editor = page.getByRole("region", { name: `Editor: ${name}` });
  await expect(editor).toBeVisible();
  return editor;
}

test("the Inheritance tab follows the base entity, and New relationship… starts from the entity", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  const invoice = await openEntity(page, "Invoice");
  const tab = invoice.getByRole("tab", { name: "Inheritance" });
  await expect(tab).toBeDisabled();

  await invoice.getByLabel("Base entity").selectOption({ label: "Payment" });
  await expect(tab).toBeEnabled();
  await tab.click();
  const inheritance = invoice.getByTestId("editor-inheritance");
  await expect(inheritance.getByRole("button", { name: /Payment/ })).toBeVisible();
  await expect(inheritance.getByTestId("editor-inheritance-strategies").locator("tbody tr").first()).toBeVisible();

  // The base entity lists Invoice among its derived entities.
  const payment = await openEntity(page, "Payment");
  await payment.getByRole("tab", { name: "Inheritance" }).click();
  await expect(payment.getByTestId("editor-derived").getByRole("button", { name: /Invoice/ })).toBeVisible();

  // Relationships › New relationship… opens the creation dialog with this entity as the source.
  await payment.getByRole("tab", { name: "Relationships" }).click();
  await payment.getByTestId("editor-new-relationship").first().click();
  const dialog = page.getByRole("dialog");
  await expect(dialog.locator("#new-element-source option:checked")).toHaveText("Payment");
  await page.keyboard.press("Escape");
});

test("the reference type menu duplicates, renames and deletes a type", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  await workspace(page, "Reference data");
  await page.getByTestId("new-reference-type").click();
  const created = page.getByRole("dialog", { name: "New reference type" });
  await created.getByLabel("Name", { exact: true }).fill("Currency");
  await created.getByRole("button", { name: "Create" }).click();
  await expect(created).toBeHidden();
  const list = page.getByTestId("explorer-reference-data");
  await expect(list.getByTestId("explorer-row-Currency")).toBeVisible();

  await list.getByTestId("explorer-row-Currency").click({ button: "right" });
  await page.getByRole("menuitem", { name: "Duplicate" }).click();
  await expect(list.getByTestId("explorer-row-CurrencyCopy")).toBeVisible();

  await list.getByTestId("explorer-row-CurrencyCopy").click({ button: "right" });
  await page.getByRole("menuitem", { name: "Rename" }).click();
  const rename = page.getByRole("dialog", { name: "Rename CurrencyCopy" });
  await rename.getByLabel("Name", { exact: true }).fill("Money");
  await rename.getByRole("button", { name: "Rename" }).click();
  await expect(list.getByTestId("explorer-row-Money")).toBeVisible();

  await list.getByTestId("explorer-row-Money").click({ button: "right" });
  await page.getByRole("menuitem", { name: "Delete…" }).click();
  await page.getByTestId("confirm-delete-type").click();
  await expect(list.getByTestId("explorer-row-Money")).toHaveCount(0);
  await expect(list.getByTestId("explorer-row-Currency")).toBeVisible();
});
