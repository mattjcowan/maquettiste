// Search (explorer-redesign.md 3.1): the explorer's filter mode over the search worker (hidden non-matches, ancestors
// expanded, "3 of 41" counts, the operators and qualifier chips, the "also matches" footer that goes to the other
// explorers) and quick open's ranked list.
import { expect, openEditor, test } from "./fixtures";

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });

test("filter mode hides non-matches and counts the other explorers' matches", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const box = side.getByLabel("Search the model");
  const tree = side.getByRole("tree", { name: "Domain model" });

  await box.fill("invoice");
  // Ancestors of the matches open; the rows that do not match are hidden.
  await expect(tree.getByTestId("explorer-row-InvoiceLine")).toBeVisible();
  await expect(tree.getByTestId("explorer-row-InvoiceStatus")).toBeVisible();
  await expect(tree.getByTestId("explorer-row-Customer")).toHaveCount(0);
  await expect(tree.getByTestId("explorer-domain-Billing")).toHaveAttribute("aria-expanded", "true");
  await expect(tree.getByTestId("explorer-domain-Catalog")).toHaveCount(0);
  // A kind folder shows how many of its members match.
  await expect(tree.getByTestId("explorer-folder-Entities").first().locator('[data-part="count"]')).toHaveText(/^3 of \d+$/); // Invoice, InvoiceLine and InvoiceNote
  // The matched text is marked.
  await expect(tree.getByTestId("explorer-row-InvoiceLine").locator("mark")).toHaveText("Invoice");

  // Operators: equals keeps only Invoice.
  await box.fill("=Invoice");
  await expect(tree.getByTestId("explorer-row-Invoice")).toBeVisible();
  await expect(tree.getByTestId("explorer-row-InvoiceLine")).toHaveCount(0);
  await box.fill("~inv%line");
  await expect(tree.getByTestId("explorer-row-InvoiceLine")).toBeVisible();
  await expect(tree.getByTestId("explorer-row-Invoice")).toHaveCount(0);

  // A qualifier narrows and shows as a chip; removing the chip removes it from the box.
  await box.fill("kind:enum");
  await expect(tree.getByTestId("explorer-row-InvoiceStatus")).toBeVisible();
  await expect(tree.getByTestId("explorer-row-Invoice")).toHaveCount(0);
  await side.getByRole("button", { name: "Remove filter Kind: enum" }).click();
  await expect(box).toHaveValue("");

  // Kinds in other explorers still answer: the footer counts them and goes there with the same search.
  await box.fill("invoice");
  const also = side.getByTestId("explorer-also");
  await expect(also.getByTestId("explorer-also-databases")).toHaveText(/^\d+ in Databases$/);
  await also.getByTestId("explorer-also-databases").click();
  const databases = side.getByRole("tree", { name: "Databases" });
  await expect(databases).toBeVisible();
  await expect(side.getByLabel("Search the model")).toHaveValue("invoice");
  await expect(databases.getByTestId("explorer-row-outstanding_invoices")).toBeVisible();
  await expect(databases.locator('[data-type="table"]').first()).toBeVisible();
  await expect(side.getByTestId("explorer-also-domain-model")).toHaveText(/^\d+ in Domain model$/);

  // Esc clears the box and gives the tree back with the user's expansion.
  await side.getByLabel("Search the model").press("Escape");
  await expect(side.getByLabel("Search the model")).toHaveValue("");
  await expect(side.getByTestId("explorer-also")).toHaveCount(0);
});

test("quick open ranks across kinds and opens the choice", async ({ page }) => {
  await openEditor(page);
  await page.keyboard.press("Control+p");
  const dialog = page.getByTestId("quick-open");
  await expect(dialog).toBeVisible();
  await page.keyboard.type("invli");
  await expect(dialog.locator("[cmdk-item]").first()).toContainText("InvoiceLine");
  await page.keyboard.press("Enter");
  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Inspector: InvoiceLine" })).toBeVisible();

  // A table: typed by its physical name, it opens its database.
  await page.keyboard.press("Control+p");
  await page.keyboard.type("^outstanding");
  await expect(dialog.locator("[cmdk-item]").first()).toContainText("outstanding_invoices");
  await page.keyboard.press("Escape");
  await expect(dialog).toHaveCount(0);
});
