// Explorer filters and the pinned filter (explorer-redesign.md 3.2), and related-element highlighting that follows
// the selection across a rail switch, with the "n related" badge on collapsed folders and the preference (1.9).
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });

test("a kind chip narrows the tree, and a pinned filter survives a reload", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await expect(side.getByTestId("explorer-domain-Billing")).toBeVisible();

  await side.getByTestId("explorer-filters").click();
  await page.getByRole("menuitemcheckbox", { name: "Enum" }).click();
  await page.keyboard.press("Escape");
  await expect(side.getByTestId("explorer-filters")).toHaveAccessibleName("Filters (1 active)");
  const tree = side.getByTestId("explorer-tree");
  await expect(tree.getByTestId("explorer-row-InvoiceStatus")).toBeVisible();
  await expect(tree.getByTestId("explorer-row-Invoice")).toHaveCount(0);
  await expect(side.getByRole("button", { name: "Remove filter Enum" })).toBeVisible();

  // The search text combines with the chip.
  await side.getByLabel("Search the model").fill("zzz-nothing");
  await expect(side.getByTestId("explorer-empty")).toHaveAttribute("data-filter", "zzz-nothing");
  await side.getByLabel("Search the model").fill("");

  // Pinned: the filter comes back after a reload; unpinned: it does not.
  await side.getByTestId("explorer-pin-filter").click();
  await expect(side.getByTestId("explorer-pin-filter")).toHaveAttribute("aria-pressed", "true");
  await page.reload();
  await expect(side.getByRole("button", { name: "Remove filter Enum" })).toBeVisible();
  await expect(side.getByTestId("explorer-tree").getByTestId("explorer-row-InvoiceStatus")).toBeVisible();
  await expect(side.getByTestId("explorer-tree").getByTestId("explorer-row-Invoice")).toHaveCount(0);
  await side.getByTestId("explorer-pin-filter").click();
  await page.reload();
  await expect(side.getByTestId("explorer-domain-Billing")).toBeVisible();
  await expect(side.getByRole("button", { name: "Remove filter Enum" })).toHaveCount(0);
});

test("related rows stay highlighted across a rail switch, and the preference turns it off", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").click();
  await side.getByLabel("Search the model").fill("");
  // The selection is revealed and the rows related to it are tinted.
  await expect(side.getByTestId("explorer-row-Invoice")).toHaveAttribute("aria-selected", "true");
  await expect(side.locator('[data-related="true"]').first()).toBeVisible();

  // Databases: the collapsed database holding Invoice's table shows "n related" without expanding.
  await workspace(page, "Databases");
  const main = side.getByTestId("explorer-folder-main");
  await expect(main).toHaveAttribute("aria-expanded", "false");
  await expect(main.locator('[data-part="related"]')).toHaveText(/\d+ related/);
  await main.click();
  await expect(main).toHaveAttribute("aria-expanded", "true");
  await expect(main.locator('[data-part="related"]')).toHaveCount(0);

  // The preference, from the explorer's menu, turns the highlighting off everywhere.
  await side.getByTestId("explorer-header-menu").click();
  await page.getByRole("menuitemcheckbox", { name: "Highlight related elements" }).click();
  await page.keyboard.press("Escape");
  await expect(side.locator('[data-related="true"]')).toHaveCount(0);
  await expect(side.locator('[data-part="related"]')).toHaveCount(0);
  await workspace(page, "Domain model");
  await expect(side.locator('[data-related="true"]')).toHaveCount(0);
});
