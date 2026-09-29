// Localization in the editor (reference-types-seeds-localization.md 3.10) over the mock's `locales` scenario (en, fr and
// fr-CA falling back to fr): nothing shows with one locale; with several, a translation edited in the inspector is
// written to the French shard and the labels follow the content locale; the translation queue is walked by keyboard;
// and the new screens pass axe.
import AxeBuilder from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";

async function axe(page: Page, selector: string) {
  const scan = await new AxeBuilder({ page }).include(selector).analyze();
  expect(scan.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
}

async function selectInvoice(page: Page) {
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").click();
  await expect(page.getByTestId("inspector-title")).toHaveText("Invoice");
}

test("with one locale nothing of localization shows", async ({ page }) => {
  await openEditor(page);
  await selectInvoice(page);
  await expect(page.getByTestId("inspector")).toBeVisible();
  await expect(page.getByTestId("locale-switcher")).toHaveCount(0);
  await expect(page.getByTestId("translations-section")).toHaveCount(0);
  await expect(page.getByTestId("explorer-locale-chip")).toHaveCount(0);
  await page.getByTestId("rail-settings").click();
  await page.getByRole("tab", { name: "Locales" }).click();
  await expect(page.getByRole("button", { name: "Declare locales" })).toBeVisible();
  await expect(page.getByTestId("completeness-matrix")).toHaveCount(0);
});

test("a translation edited in French is written to the shard and the labels follow the content locale", async ({ page }) => {
  await openEditor(page, "/?mock=locales");
  const switcher = page.getByTestId("locale-switcher");
  await expect(switcher).toContainText("English (en)");
  await selectInvoice(page);

  const section = page.getByTestId("inspector").getByTestId("translations-section");
  const toggle = section.getByRole("button", { name: /Translations/ });
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await toggle.click();
  const fr = section.getByLabel("Display name (fr)", { exact: true });
  await expect(fr).toHaveValue("Facture");
  // fr-CA has no translation: its fallback (the French text) is the placeholder.
  await expect(section.getByLabel("Display name (fr-CA)", { exact: true })).toHaveAttribute("placeholder", "Facture");
  await fr.fill("Facture client");
  await fr.press("Enter");
  await expect(section.getByLabel("Display name (fr-CA)", { exact: true })).toHaveAttribute("placeholder", "Facture client");

  // The mock wrote the French shard of the billing domain.
  const written = await page.evaluate(async (id) => {
    const r = await fetch(`/api/localization/fr/entries?owner=${id}`);
    const body = (await r.json()) as { entries: { field: string; translation: string | null; shard: string }[] };
    return body.entries.find((e) => e.field === "displayName");
  }, INVOICE);
  expect(written?.translation).toBe("Facture client");
  expect(written?.shard).toMatch(/\/locales\/fr\/billing\.json$/);

  // Content in French: the tree's labels come from the French chain; the explorer shows the chip.
  await switcher.click();
  await page.getByRole("menuitemradio", { name: /French \(fr\)/ }).click();
  await expect(switcher).toContainText("French (fr)");
  await expect(explorer(page).getByRole("treeitem", { name: /^Facture client/ })).toBeVisible();
  await expect(page.getByTestId("explorer-locale-chip")).toHaveText("fr");

  // A second edit arrives as model.changed `translations` and patches the label in place.
  await fr.fill("Facture fournisseur");
  await fr.press("Tab");
  await expect(explorer(page).getByRole("treeitem", { name: /^Facture fournisseur/ })).toBeVisible();
  await axe(page, '[data-testid="translations-section"]');

  // Removing the chip goes back to the default locale.
  await page.getByTestId("explorer-locale-chip").click();
  await expect(switcher).toContainText("English (en)");
  await expect(explorer(page).getByTestId("explorer-row-Invoice")).toBeVisible();
});

test("Settings › Locales: the completeness matrix and the translation queue walked by keyboard", async ({ page }) => {
  await openEditor(page, "/?mock=locales");
  await page.getByTestId("rail-settings").click();
  await page.getByRole("tab", { name: "Locales" }).click();
  await expect(page.getByRole("combobox", { name: "Default locale" })).toHaveValue("en");
  await expect(page.getByLabel("Fallbacks of fr-CA")).toHaveValue("fr");
  const matrix = page.getByTestId("completeness-matrix");
  const cell = matrix.getByRole("button", { name: /^billing in fr:/ });
  const before = await cell.getAttribute("aria-label");
  await cell.click();

  const queue = page.getByTestId("translation-queue");
  await expect(queue).toBeVisible();
  const entries = queue.getByTestId("queue-entry");
  // Focus starts on the first entry that needs work (the first one is translated: Facture is done).
  const focused = () => page.evaluate(() => document.activeElement?.getAttribute("aria-label") ?? "");
  await expect.poll(focused).toMatch(/\(fr\)$/);
  const first = await focused();
  await page.keyboard.type("Traduction un");
  await page.keyboard.press("Enter");
  await expect.poll(focused).not.toBe(first);
  const second = await focused();
  await page.keyboard.type("Traduction deux");
  await page.keyboard.press("Enter");
  await expect.poll(focused).not.toBe(second);
  // Arrow up goes back to the row above; its saved text is there.
  await page.keyboard.press("ArrowUp");
  await expect.poll(focused).toBe(second);
  await expect(queue.getByLabel(second, { exact: true })).toHaveValue("Traduction deux");
  await expect(entries.filter({ has: page.getByText("done") })).not.toHaveCount(0);
  await expect.poll(() => cell.getAttribute("aria-label")).not.toBe(before);

  await axe(page, '[data-testid="settings-workspace"]');

  // "Open element" goes to the owner.
  await queue
    .getByRole("button", { name: /^Open element / })
    .first()
    .click();
  await expect(page.getByTestId("inspector")).toBeVisible();
});

test("Settings › Locales: adding a locale saves maquettiste.json and shows it in the switcher", async ({ page }) => {
  await openEditor(page, "/?mock=locales");
  await page.getByTestId("rail-settings").click();
  await page.getByRole("tab", { name: "Locales" }).click();
  await page.getByLabel("New locale (BCP 47 tag)").fill("de");
  await page.getByRole("button", { name: "Add locale" }).click();
  await page.getByTestId("save-locales").click();
  await expect(page.getByTestId("notice")).toContainText("Locales saved.");
  await page.getByTestId("locale-switcher").click();
  await expect(page.getByRole("menuitemradio", { name: /German \(de\)/ })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("completeness-matrix").getByRole("columnheader", { name: "German (de)" })).toBeVisible();
});

test("the Rows grid shows the content locale's label column, every locale with All locales, written to the reference-data shard", async ({ page }) => {
  await openEditor(page, "/?mock=locales");
  await workspace(page, "Reference data");
  await page.getByTestId("new-reference-type").click();
  const dialog = page.getByRole("dialog", { name: "New reference type" });
  await dialog.getByLabel("Name", { exact: true }).fill("UnitOfMeasure");
  await dialog.getByRole("button", { name: "Create" }).click();
  await expect(dialog).toBeHidden();
  await page.getByRole("tab", { name: "Rows" }).click();
  const rows = page.getByTestId("rows-grid");
  // In the default locale the grid has no locale column; "All locales" shows one per translated locale.
  await expect(rows.getByRole("columnheader", { name: "label (fr)", exact: true })).toHaveCount(0);
  await page.getByTestId("rows-all-locales").check();
  await expect(rows.getByRole("columnheader", { name: "label (fr)", exact: true })).toBeVisible();
  await expect(rows.getByRole("columnheader", { name: "label (fr-CA)", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Add row" }).click();
  await page.keyboard.type("kg");
  await page.keyboard.press("Tab");
  await page.keyboard.type("Kilogram");
  await page.keyboard.press("Enter");
  // Once the seed is saved, the French cell shows the fallback (the default label) muted.
  await expect(rows.locator('[data-cell="0:2"]')).toHaveText("Kilogram");
  await rows.locator('[data-cell="0:2"]').click();
  await page.keyboard.type("Kilogramme");
  await page.keyboard.press("Enter");
  await expect(rows.locator('[data-cell="0:2"]')).toHaveText("Kilogramme");
  await expect(rows.locator('[data-cell="0:3"]')).toHaveText("Kilogramme");
  const written = await page.evaluate(async () => {
    const r = await fetch("/api/localization/fr/entries?missing=false");
    const body = (await r.json()) as { entries: { field: string; translation: string | null; shard: string }[] };
    return body.entries.find((e) => e.field === "label" && e.translation === "Kilogramme");
  });
  expect(written?.shard).toMatch(/\/locales\/fr\/_reference-data\.json$/);
  await axe(page, '[data-testid="reference-data"]');
});
