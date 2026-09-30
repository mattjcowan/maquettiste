// The reference-data close-out (reference-types-seeds-localization.md §5 steps 4, 7 and 8) in the mock: the Settings hint
// declares the standard storage strategies in one click, and a header field edited while French is the content
// locale writes the French translation with the default text as the placeholder.
import { expect, openEditor, test } from "./fixtures";

const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";

test("Settings declares the standard storage strategies in one click", async ({ page }) => {
  await page.goto("/settings/conventions");
  const hint = page.getByTestId("strategies-hint");
  await expect(hint).toBeVisible();
  await hint.getByRole("button", { name: "Declare the standard storage strategies" }).click();
  await expect(hint).toBeHidden();
  const strategies = await page.evaluate(async () => {
    const r = await fetch("/api/project/settings");
    const body = (await r.json()) as { json: { referenceData?: { strategies?: Record<string, unknown> } } };
    return Object.keys(body.json.referenceData?.strategies ?? {}).sort();
  });
  expect(strategies).toEqual(["check", "lookup-table", "native"]);
});

test("a header field edited in French writes the French translation", async ({ page }) => {
  await openEditor(page, "/?mock=locales");
  const switcher = page.getByTestId("locale-switcher");
  await switcher.click();
  await page.getByRole("menuitemradio", { name: /French \(fr\)/ }).click();
  await expect(switcher).toContainText("French (fr)");
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill("Invoice");
  await side
    .getByRole("treeitem", { name: /^Facture/ })
    .first()
    .click();

  const header = page.getByTestId("inspector").locator('input[data-locale="fr"]').first();
  await expect(header).toHaveValue("Facture");
  await header.fill("Facture émise");
  await header.press("Tab");
  await expect
    .poll(async () =>
      page.evaluate(async (id) => {
        const r = await fetch(`/api/localization/fr/entries?owner=${id}`);
        const body = (await r.json()) as { entries: { field: string; translation: string | null }[] };
        return body.entries.find((e) => e.field === "displayName")?.translation;
      }, INVOICE),
    )
    .toBe("Facture émise");
});
