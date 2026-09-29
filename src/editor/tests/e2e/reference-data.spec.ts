// The Reference data screen (reference-types-seeds-localization.md section 4): create a reference type, add a
// field, enter rows with the keyboard, import a CSV through its preview, and use the type as an attribute's type
// through the entity editor's sectioned type picker.
import { readFile } from "node:fs/promises";
import AxeBuilder from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

async function createUnitOfMeasure(page: Page): Promise<void> {
  await workspace(page, "Reference data");
  await expect(page.getByTestId("workspace-reference-data")).toBeVisible();
  await page.getByTestId("new-reference-type").click();
  const dialog = page.getByRole("dialog", { name: "New reference type" });
  await dialog.getByLabel("Name", { exact: true }).fill("UnitOfMeasure");
  await dialog.getByLabel("Display name").fill("Unit of measure");
  // Stored as: the project declares no strategy here, so Template-defined is the one choice and the templates decide.
  await expect(dialog.getByLabel("Stored as")).toHaveValue("__template");
  await dialog.getByRole("button", { name: "Create" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId("reference-type-title")).toHaveText("Unit of measure");
  // One list of types on screen (EX 1.7): with the rail on Reference data, the explorer is the screen's list.
  const explorer = page.getByTestId("explorer-reference-data");
  await expect(explorer.getByTestId("explorer-row-Unit of measure")).toHaveAttribute("aria-selected", "true");
  await expect(page.locator('[data-testid="reference-type-list"], [data-testid="explorer-reference-data"]')).toHaveCount(1);
  // The A to Z option lists every type with no category groups; it is turned back off for the rest of the test.
  for (const on of [true, false]) {
    await explorer.getByTestId("explorer-header-menu").click();
    await page.getByTestId("reference-flat").click();
    if (await page.getByTestId("reference-flat").isVisible()) await page.keyboard.press("Escape");
    if (on) await expect(explorer.locator('[role="treeitem"][data-type="group"]')).toHaveCount(0);
    await expect(explorer.getByTestId("explorer-row-Unit of measure")).toBeVisible();
  }
}

test("reference data: create a type, add a field, enter rows, import and export CSV", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  await createUnitOfMeasure(page);

  // Fields: code, label and description are built-in rows; a user field goes through the attribute grid and the type picker.
  const fields = page.getByTestId("reference-fields");
  await expect(fields.getByTestId("builtin-code")).toBeVisible();
  await expect(fields.getByTestId("builtin-label")).toContainText("yes");
  await expect(fields.getByTestId("builtin-description")).toContainText("text");
  await expect(fields.getByTestId("builtin-description")).toContainText("yes");
  await expect(fields.getByLabel("Type of code").locator("option")).toHaveText(["string", "int16", "int32", "int64", "uuid"]);
  await fields.getByRole("button", { name: "Add attribute" }).click();
  const grid = fields.getByTestId("attribute-grid");
  const name = grid.locator('td[data-column="name"]').first().getByRole("textbox");
  await expect(name).toBeFocused();
  await page.keyboard.type("factor");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");
  const picker = page.getByTestId("type-picker");
  await expect(picker.getByRole("group", { name: "Built-in" })).toBeVisible();
  await page.keyboard.type("decimal");
  await page.keyboard.press("Enter");
  await expect(picker).toBeHidden();
  await expect(grid.locator('td[data-column="type"]').first()).toHaveText("decimal");

  // Rows: Add row opens the code cell; Tab commits and moves right; typing starts an edit; Enter commits. The
  // description sits third, a text area where Shift+Enter adds a line.
  await page.getByRole("tab", { name: "Rows" }).click();
  const rows = page.getByTestId("rows-grid");
  await expect(rows.getByRole("columnheader")).toHaveText(["code", "label", "description", "factor"]);
  await expect(page.getByTestId("rows-locale-hint")).toHaveText("Declare a second locale under Settings › Locales to translate labels and descriptions.");
  await page.getByRole("button", { name: "Add row" }).click();
  await expect(rows.getByRole("textbox", { name: "code of row 1" })).toBeFocused();
  await page.keyboard.type("kg");
  await page.keyboard.press("Tab");
  await page.keyboard.type("Kilogram");
  await page.keyboard.press("Tab");
  await page.keyboard.type("The base unit");
  await page.keyboard.press("Shift+Enter");
  await expect(rows.getByRole("textbox", { name: "description of row 1" })).toHaveValue("The base unit\n");
  await page.keyboard.type("of mass");
  await page.keyboard.press("Tab");
  await page.keyboard.type("1000");
  await page.keyboard.press("Enter");
  await expect(rows.locator('[data-cell="0:0"]')).toHaveText("kg");
  await expect(rows.locator('[data-cell="0:1"]')).toHaveText("Kilogram");
  await expect(rows.locator('[data-cell="0:2"]')).toHaveText("The base unit of mass");
  await expect(rows.locator('[data-cell="0:3"]')).toHaveText("1000");
  // Ctrl+Enter inserts a row below, Escape cancels its edit, Ctrl+Delete removes it again.
  await page.keyboard.press("ArrowUp");
  await page.keyboard.press("Control+Enter");
  await expect(rows.getByRole("textbox", { name: "code of row 2" })).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("rows-status")).toContainText("2 rows");
  await page.keyboard.press("Control+Delete");
  await expect(page.getByTestId("rows-status")).toContainText("1 row ·");

  // Import CSV: the preview counts what the file adds and changes; Apply writes it as one save.
  await page.getByRole("button", { name: "Import CSV" }).click();
  const dialog = page.getByRole("dialog", { name: /Import CSV/ });
  await dialog
    .getByLabel("Or paste the CSV text")
    .fill('@code,@label,@description,factor\nkg,Kilogram,"The base unit\nof mass",1000\ng,Gram,,1\nmg,Milligram,One thousandth of a gram,0.001\n');
  await dialog.getByRole("button", { name: "Preview" }).click();
  await expect(dialog.getByTestId("csv-preview")).toContainText("2 rows added, 0 rows changed, 0 rows removed");
  await dialog.getByRole("button", { name: "Apply" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId("rows-status")).toContainText("3 rows");
  await expect(rows.locator('[data-cell="2:1"]')).toHaveText("Milligram");
  await expect(rows.locator('[data-cell="2:2"]')).toHaveText("One thousandth of a gram");
  await expect(page.getByTestId("reference-type-facts")).toContainText("3 rows");

  // Export downloads the seed as CSV, the description column included.
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Export CSV" }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe("UnitOfMeasure.csv");
  expect((await readFile((await file.path())!, "utf8")).split(/\r?\n/)[0]).toContain("@description");

  // The screen passes an accessibility scan.
  const scan = await new AxeBuilder({ page }).include('[data-testid="reference-data"]').analyze();
  expect(scan.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
});

test("reference data: a reference type as an attribute type, with Many and Required", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  await createUnitOfMeasure(page);

  // The entity editor's type picker: sections, one search across them, the Required toggle.
  await workspace(page, "Domain model");
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").dblclick();
  const editor = page.getByRole("region", { name: "Editor: Invoice" });
  const grid = editor.getByRole("grid", { name: "Attributes of Invoice" });
  await expect(grid).toBeVisible();
  const names = grid.locator('td[data-column="name"]');
  const before = await names.count();
  await editor.getByRole("button", { name: "Add attribute" }).click();
  await expect(names).toHaveCount(before + 1);
  await page.keyboard.type("unit");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");
  const picker = page.getByTestId("type-picker");
  for (const section of ["Built-in", "Enums", "Reference data"]) await expect(picker.getByRole("group", { name: section })).toBeVisible();
  await page.keyboard.type("unit of");
  await expect(picker.getByRole("group")).toHaveCount(1);
  await expect(picker.getByRole("option", { name: /Unit of measure/ })).toHaveAttribute("aria-selected", "true");
  // The effective storage beside the name: no strategy is chosen here, so the templates decide.
  await expect(picker.getByRole("option", { name: /Unit of measure/ })).toContainText("· template");
  await page.keyboard.press("Alt+r");
  await expect(picker.getByRole("button", { name: "Required" })).toHaveAttribute("aria-pressed", "true");
  await page.keyboard.press("Enter");
  const typeCell = grid.locator('td[data-column="type"]').nth(before);
  await expect(typeCell).toHaveText("→ Unit of measure");
  await expect(grid.locator('td[data-column="required"]').nth(before).locator("span")).toHaveAttribute("aria-label", "yes");
  await expect(page.getByTestId("editor-save-status")).toHaveText("Saved");

  // The Reference data explorer's row reveals the type in the screen; Used by lists Invoice.unit.
  await workspace(page, "Reference data");
  await side.locator('[data-testid^="explorer-row-Unit"]').first().click();
  await expect(page.getByTestId("reference-type-title")).toHaveText("Unit of measure");
  await page.getByRole("tab", { name: "Used by" }).click();
  const usedBy = page.getByTestId("reference-used-by");
  await expect(usedBy.getByRole("button", { name: /Invoice\.unit/ })).toContainText("required");
  await expect(page.getByTestId("reference-type-facts")).toContainText("used by 1");
  await page.getByRole("tab", { name: "Storage" }).click();
  await expect(page.getByTestId("storage-All databases")).toContainText("Template-defined");
});
