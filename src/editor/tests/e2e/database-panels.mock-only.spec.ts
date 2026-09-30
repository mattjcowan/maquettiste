// The Database screen's two side panels (the owner: "how do I toggle the DDL preview closed and open", "the filter
// tables panel needs a toggle too"): each hides from its header button or its shortcut (Alt+Shift+L, Alt+Shift+D),
// leaves a slim edge that brings it back, stays as it was after a reload, and Reset layout opens both again.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const tables = (page: Page) => page.getByTestId("database-tables");
const ddl = (page: Page) => page.getByTestId("ddl-preview");

async function openDatabaseScreen(page: Page) {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
}

test("the tables list and the DDL preview hide and show by button and shortcut, persist, and Reset layout restores them", async ({ page }) => {
  await openDatabaseScreen(page);
  await expect(tables(page)).toBeVisible();
  await expect(ddl(page)).toBeVisible();

  // By the header buttons, labelled with their shortcuts.
  const hideTables = page.getByTestId("hide-tables");
  await expect(hideTables).toHaveAttribute("title", "Hide tables list (Alt+Shift+L)");
  await expect(page.getByTestId("hide-ddl")).toHaveAttribute("title", "Hide DDL preview (Alt+Shift+D)");
  await hideTables.click();
  await expect(tables(page)).toBeHidden();
  await expect(page.getByTestId("show-tables")).toHaveAttribute("title", "Show tables list (Alt+Shift+L)");
  await page.getByTestId("hide-ddl").click();
  await expect(ddl(page)).toBeHidden();
  await expect(page.getByTestId("show-ddl")).toHaveAttribute("title", "Show DDL preview (Alt+Shift+D)");
  // The slim edges bring them back.
  await page.getByTestId("show-tables").click();
  await expect(tables(page)).toBeVisible();
  await page.getByTestId("show-ddl").click();
  await expect(ddl(page)).toBeVisible();

  // By the shortcuts (not while typing in the filter).
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await page.keyboard.press("Alt+Shift+KeyL");
  await expect(tables(page)).toBeHidden();
  await page.keyboard.press("Alt+Shift+KeyD");
  await expect(ddl(page)).toBeHidden();

  // Both stay hidden after a reload.
  await page.reload();
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await expect(page.getByTestId("show-tables")).toBeVisible();
  await expect(page.getByTestId("show-ddl")).toBeVisible();
  await expect(tables(page)).toBeHidden();
  await expect(ddl(page)).toBeHidden();

  // One back by shortcut, then Reset layout (palette) opens both, and so after a reload.
  await page.keyboard.press("Alt+Shift+KeyD");
  await expect(ddl(page)).toBeVisible();
  await page.keyboard.press("Control+k");
  await page.getByRole("dialog").getByRole("combobox").fill(">Reset layout");
  await page.getByRole("option", { name: "Reset layout", exact: true }).click();
  await expect(tables(page)).toBeVisible();
  await expect(ddl(page)).toBeVisible();
  await page.reload();
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await expect(page.getByTestId("hide-tables")).toBeVisible();
  await expect(page.getByTestId("hide-ddl")).toBeVisible();
});

test("the Database screen's shortcuts do nothing on another screen", async ({ page }) => {
  await openEditor(page);
  await page.keyboard.press("Alt+Shift+KeyD");
  await openDatabaseScreen(page);
  await expect(ddl(page)).toBeVisible();
});
