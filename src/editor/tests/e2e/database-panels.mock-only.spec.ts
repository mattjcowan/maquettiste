// The Database screen's two side panels (the owner: "how do I toggle the DDL preview closed and open", "the filter
// tables panel needs a toggle too"; "the canvas is far too narrow"): the DDL preview starts hidden behind its edge and the
// list narrow, so the diagram has at least half the screen at 1440 px; each panel hides from its header button or its
// shortcut (Alt+Shift+L, Alt+Shift+D), leaves a slim edge that brings it back, resizes from its splitter, stays as it was
// after a reload, and Reset layout goes back to the defaults.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const tables = (page: Page) => page.getByTestId("database-tables");
const ddl = (page: Page) => page.getByTestId("ddl-preview");

async function openDatabaseScreen(page: Page) {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
}

test("the diagram has at least half the Database screen by default; the panels resize and keep their width", async ({ page }) => {
  await openDatabaseScreen(page);
  await expect(tables(page)).toBeVisible();
  await expect(ddl(page)).toHaveCount(0);
  const screen = (await page.getByTestId("database-workspace").boundingBox())!;
  const canvas = (await page.getByTestId("database-canvas").boundingBox())!;
  expect(canvas.width).toBeGreaterThanOrEqual(screen.width / 2);
  // The DDL preview opened from its edge, then the list widened from its splitter by the keyboard (kept after a reload).
  await page.getByTestId("show-ddl").click();
  await expect(ddl(page)).toBeVisible();
  const before = (await tables(page).boundingBox())!.width;
  const splitter = page.getByRole("separator", { name: "Resize the tables list" });
  await splitter.focus();
  await page.keyboard.press("Shift+ArrowRight");
  await expect.poll(async () => (await tables(page).boundingBox())!.width).toBe(before + 48);
  await expect(page.getByRole("separator", { name: "Resize the DDL preview" })).toBeVisible();
  // A resize is saved a moment after the last move.
  await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem("mq.layout") ?? "{}").tables)).toBe(before + 48);
  await page.reload();
  await expect(ddl(page)).toBeVisible();
  await expect.poll(async () => (await tables(page).boundingBox())!.width).toBe(before + 48);
});

test("the tables list and the DDL preview hide and show by button and shortcut, persist, and Reset layout restores the defaults", async ({ page }) => {
  await openDatabaseScreen(page);
  await expect(tables(page)).toBeVisible();
  await expect(ddl(page)).toBeHidden();
  await expect(page.getByTestId("show-ddl")).toHaveAttribute("title", "Show DDL preview (Alt+Shift+D)");
  await page.getByTestId("show-ddl").click();
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

  // One back by shortcut, then Reset layout (palette): the list shows, the DDL preview goes back behind its edge, and so
  // after a reload.
  await page.keyboard.press("Alt+Shift+KeyD");
  await expect(ddl(page)).toBeVisible();
  await page.keyboard.press("Control+k");
  await page.getByRole("dialog").getByRole("combobox").fill(">Reset layout");
  await page.getByRole("option", { name: "Reset layout", exact: true }).click();
  await expect(tables(page)).toBeVisible();
  await expect(ddl(page)).toBeHidden();
  await page.reload();
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await expect(page.getByTestId("hide-tables")).toBeVisible();
  await expect(page.getByTestId("show-ddl")).toBeVisible();
});

test("the Database screen's shortcuts do nothing on another screen", async ({ page }) => {
  await openEditor(page);
  await page.keyboard.press("Alt+Shift+KeyD");
  await openDatabaseScreen(page);
  await expect(ddl(page)).toBeHidden();
  await expect(page.getByTestId("show-ddl")).toBeVisible();
});
