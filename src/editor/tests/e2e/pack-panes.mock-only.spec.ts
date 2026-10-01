// The pack editor's side panes (the owner: "how do I toggle the panels under Parameters and Templates when inside a
// pack?"): the Templates tab's pack files and preview and the Units tab's help each hide from a header button, the
// palette or a shortcut, leave a slim edge that brings them back, resize by their edge, and Reset layout shows them
// again. The Units tab's Example element picker names each element with its kind, its id in the tooltip.
import type { Page } from "@playwright/test";
import { expect, test, workspace } from "./fixtures";

async function openPack(page: Page, name: string) {
  await page.goto("/generate");
  await workspace(page, "Generate");
  await page.getByRole("tree", { name: "Packs" }).getByTestId(`pack-row-p:${name}`).getByText(name, { exact: true }).click();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
}

async function palette(page: Page, command: string) {
  await page.keyboard.press("Control+k");
  await page.getByRole("dialog").getByRole("combobox").fill(`>${command}`);
  // A command's option names its shortcut too ("Toggle pack files Alt+Shift+F").
  await page.getByRole("option", { name: new RegExp(`^${command}(\\s*Alt\\+|$)`) }).click();
}

test("the Templates tab's pack files and preview, and the Units tab's help, hide, come back, resize and reset", async ({ page }) => {
  await openPack(page, "sql-ddl");

  // Units: the help pane has a header with its hide button.
  const help = page.getByTestId("unit-help");
  await expect(help).toBeVisible();
  const hideHelp = page.getByTestId("hide-unitHelp");
  await expect(hideHelp).toHaveAttribute("title", "Hide unit help (Alt+Shift+U)");
  await hideHelp.click();
  await expect(help).toBeHidden();
  await expect(page.getByTestId("show-unitHelp")).toHaveAttribute("title", "Show unit help (Alt+Shift+U)");
  await page.getByTestId("show-unitHelp").click();
  await expect(help).toBeVisible();
  // Its edge resizes it.
  const helpSplitter = page.getByRole("separator", { name: "Resize the unit help" });
  const before = (await help.boundingBox())!.width;
  await helpSplitter.focus();
  await page.keyboard.press("ArrowLeft");
  await expect.poll(async () => (await help.boundingBox())!.width).toBeGreaterThan(before);

  // Templates: both side panes hide by button, palette and shortcut.
  await page.keyboard.press("Alt+3");
  const files = page.getByTestId("pack-files");
  const preview = page.getByTestId("template-preview");
  await expect(files).toBeVisible();
  await expect(preview).toBeVisible();
  await expect(page.getByTestId("hide-packFiles")).toHaveAttribute("title", "Hide pack files (Alt+Shift+F)");
  await page.getByTestId("hide-packFiles").click();
  await expect(files).toBeHidden();
  await page.getByTestId("show-packFiles").click();
  await expect(files).toBeVisible();
  await palette(page, "Toggle template preview");
  await expect(preview).toBeHidden();
  await expect(page.getByTestId("show-templatePreview")).toBeVisible();
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await page.keyboard.press("Alt+Shift+KeyF");
  await expect(files).toBeHidden();
  await palette(page, "Toggle unit help");

  // Reset layout shows every pane again.
  await palette(page, "Reset layout");
  await expect(files).toBeVisible();
  await expect(preview).toBeVisible();
  await page.keyboard.press("Alt+1");
  await expect(help).toBeVisible();
});

test("the Example element picker names the unit's elements with their kind", async ({ page }) => {
  await openPack(page, "csharp-dapper");
  const picker = page.getByTestId("example-element");
  await expect(picker).toBeEnabled();
  const options = picker.locator("option");
  await expect(options.filter({ hasText: "Invoice (entity)" })).toHaveCount(1);
  const labels = await options.allTextContents();
  expect(labels.length).toBeGreaterThan(1);
  for (const label of labels) expect(label).toMatch(/ \(entity\)$/);
  const invoice = options.filter({ hasText: "Invoice (entity)" });
  const id = await invoice.getAttribute("value");
  await expect(invoice).toHaveAttribute("title", id!);
  await picker.selectOption(id!);
  await expect(picker).toHaveAttribute("title", id!);
});

test("a table unit's Example element names each table with its database, never by its key", async ({ page }) => {
  await openPack(page, "sql-ddl");
  const picker = page.getByTestId("example-element");
  await expect(picker).toBeEnabled();
  const labels = await picker.locator("option").allTextContents();
  expect(labels.length).toBeGreaterThan(1);
  for (const label of labels) {
    expect(label).toMatch(/^\S.* \(\S+\) \(table\)$/);
    expect(label).not.toMatch(/[0-9A-Z]{26}/);
  }
});
