// The owner's rule over the live DOM: every button without visible text on the main screens (the shell and its
// explorers, an entity editor, reference data, settings, Generate and a pack editor, a dialog) shows a native tooltip
// (a title) and has an accessible name. The unit guard (tests/unit/icon-controls.test.tsx) covers the same in jsdom.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";
import { scanIconControls, type IconControlScan } from "../icon-controls";

test("every icon-only control on the main screens has a tooltip and an accessible name", async ({ page }) => {
  test.setTimeout(120_000);
  const missing: string[] = [];
  const checked = new Set<string>();
  const scan = async (where: string) => {
    // Let the screen settle (lazy panels, the virtualized rows) before reading it.
    await page.waitForTimeout(150);
    const result = await page.evaluate(scanIconControls as () => IconControlScan);
    for (const m of result.missing) missing.push(`${where}: ${m}`);
    checked.add(`${where}:${result.checked}`);
    return result.checked;
  };
  let total = 0;
  const tab = async (name: string, scope = page.locator("body")) => {
    await scope.getByRole("tab", { name, exact: true }).click();
    total += await scan(`tab ${name}`);
  };

  // The shell over the locales scenario (the locale switcher and the translation fields show).
  await openEditor(page, "/?mock=locales");
  total += await scan("shell");
  for (const name of ["Domain model", "Reference data", "Databases", "Diagrams", "Generate"]) {
    await workspace(page, name);
    total += await scan(`explorer ${name}`);
  }

  // An entity editor and its sub-tabs, with the inspector on it.
  await workspace(page, "Domain model");
  await open(page, "Invoice");
  const editor = page.getByRole("region", { name: "Editor: Invoice" });
  total += await scan("entity editor");
  for (const name of ["Relationships", "Indexes", "Storage", "Seed data", "Code generation", "References", "Attributes"]) await tab(name, editor);

  // Reference data: the New reference type dialog, then the type's fields and rows.
  await workspace(page, "Reference data");
  await expect(page.getByTestId("workspace-reference-data")).toBeVisible();
  await page.getByTestId("new-reference-type").click();
  const dialog = page.getByRole("dialog", { name: "New reference type" });
  total += await scan("new reference type dialog");
  await dialog.getByLabel("Name", { exact: true }).fill("UnitOfMeasure");
  await dialog.getByRole("button", { name: "Create" }).click();
  await expect(dialog).toBeHidden();
  total += await scan("reference data");
  await tab("Rows");
  await page.getByRole("button", { name: "Add row" }).click();
  await page.keyboard.press("Escape");
  total += await scan("reference data rows");

  // Settings, every tab.
  await page.getByTestId("rail-settings").click();
  for (const name of ["General", "Tags", "Categories", "Stereotypes", "Conventions", "Locales", "Validation", "Type maps, outputs, formatters", "Explorer"])
    await tab(name);

  // The bottom panel's tabs.
  for (const name of ["Output", "Diff", "References"]) await tab(name, page.getByTestId("bottom-panel"));

  // Generate: the plan screen, then a pack editor on each pane.
  await page.goto("/generate?mock=locales");
  await workspace(page, "Generate");
  total += await scan("generate");
  const pack = page.getByRole("tree", { name: "Packs" }).getByTestId("pack-row-p:sql-ddl");
  await pack.getByText("sql-ddl", { exact: true }).dblclick();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
  total += await scan("pack editor");
  for (const name of ["Templates", "Parameters"]) {
    const pane = page.getByTestId("pack-editor").getByRole("tab", { name });
    if (await pane.count()) {
      await pane.click();
      total += await scan(`pack editor ${name}`);
    }
  }

  console.log(`icon-only controls checked over the live DOM: ${total} across ${checked.size} screens`);
  expect(missing).toEqual([]);
  expect(total).toBeGreaterThan(50);
});

async function open(page: Page, name: string) {
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).dblclick();
  await expect(page.getByRole("region", { name: `Editor: ${name}` })).toBeVisible();
}
