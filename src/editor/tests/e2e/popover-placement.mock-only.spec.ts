// Portal popovers stay inside the viewport (the shared placement in src/ui/usePopoverPlacement.ts): on a short
// window the type picker of a low attribute row opens with its whole panel on screen, every section is reachable by
// scrolling its list, and the keyboard model (arrows wrap, the highlight scrolls into view, Alt+M, Enter) is unchanged.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

test.use({ viewport: { width: 1440, height: 600 } });

async function inViewport(page: Page, locator: Locator): Promise<void> {
  const box = await locator.boundingBox();
  const size = page.viewportSize()!;
  expect(box).not.toBeNull();
  expect(box!.y).toBeGreaterThanOrEqual(0);
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.y + box!.height).toBeLessThanOrEqual(size.height);
  expect(box!.x + box!.width).toBeLessThanOrEqual(size.width);
}

/** The item lies inside the list's visible (scrolled) area. */
async function shownIn(list: Locator, item: Locator): Promise<void> {
  const [l, i] = [await list.boundingBox(), await item.boundingBox()];
  expect(i!.y).toBeGreaterThanOrEqual(l!.y - 1);
  expect(i!.y + i!.height).toBeLessThanOrEqual(l!.y + l!.height + 1);
}

test("the type picker of a low row stays on screen, scrolls to every section and keeps its keys", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  await workspace(page, "Domain model");
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").dblclick();
  const editor = page.getByRole("region", { name: "Editor: Invoice" });
  const grid = editor.getByRole("grid", { name: "Attributes of Invoice" });
  await expect(grid).toBeVisible();
  await editor.getByRole("button", { name: "Add attribute" }).click();
  await page.keyboard.type("lowRow");
  await page.keyboard.press("Tab");
  // The row at the bottom edge of the editor, low in the window: the list does not fit below it.
  await grid
    .locator('td[data-column="type"]')
    .last()
    .evaluate((el) => el.scrollIntoView({ block: "end" }));
  await page.keyboard.press("Enter");

  const input = page.getByTestId("type-picker-input");
  const picker = page.getByTestId("type-picker");
  const list = picker.getByRole("listbox", { name: "Types" });
  await expect(input).toBeFocused();
  const anchor = (await input.boundingBox())!;
  expect(anchor.y).toBeGreaterThan(300);
  await expect(picker).toHaveAttribute("data-side", "above");
  await inViewport(page, picker);
  // Pinned to its anchor: the panel's bottom edge sits just above the search box.
  expect(Math.abs((await picker.boundingBox())!.y + (await picker.boundingBox())!.height - anchor.y)).toBeLessThanOrEqual(4);

  // Scrolling the editor moves the anchor; the panel follows it (the capture-phase scroll listener).
  await input.evaluate((el) => {
    let p = el.parentElement;
    while (p && p.scrollHeight <= p.clientHeight) p = p.parentElement;
    if (p) p.scrollTop -= 40;
  });
  await expect.poll(async () => (await input.boundingBox())!.y).toBeGreaterThan(anchor.y + 20);
  const moved = (await input.boundingBox())!;
  await expect.poll(async () => Math.round((await picker.boundingBox())!.y + (await picker.boundingBox())!.height - moved.y)).toBeLessThanOrEqual(4);
  await inViewport(page, picker);

  // The last section is reachable: the list scrolls, and its last option can be brought into the visible area.
  const groups = picker.getByRole("group");
  const last = groups.last();
  const lastOption = last.getByRole("option").last();
  expect(await list.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
  await expect(last).toHaveAccessibleName(/Value objects|Reference data/);
  await list.evaluate((el) => (el.scrollTop = el.scrollHeight));
  await shownIn(list, lastOption);

  // Keys: Up from the first item wraps to the last one and scrolls it into view; Down wraps back to the first.
  await list.evaluate((el) => (el.scrollTop = 0));
  const options = picker.getByRole("option");
  const first = options.first();
  const selected = picker.locator('[role="option"][aria-selected="true"]');
  const startId = await selected.getAttribute("id");
  const count = await options.count();
  const startIndex = Number(startId!.split("-").pop());
  for (let i = startIndex; i >= 0; i--) await page.keyboard.press("ArrowUp");
  await expect(lastOption).toHaveAttribute("aria-selected", "true");
  await shownIn(list, lastOption);
  await page.keyboard.press("ArrowDown");
  await expect(first).toHaveAttribute("aria-selected", "true");
  await shownIn(list, first);
  expect(count).toBeGreaterThan(1);

  await page.keyboard.press("Alt+m");
  await expect(picker.getByRole("button", { name: "Many" })).toHaveAttribute("aria-pressed", "true");
  await page.keyboard.type("decimal");
  await expect(picker.getByRole("group")).toHaveCount(1);
  await inViewport(page, picker);
  await page.keyboard.press("Enter");
  await expect(picker).toBeHidden();
  await expect(grid.locator('td[data-column="type"]').last()).toContainText("decimal");
});

test("menus stay inside a short window: a low row's context menu and the explorer's filter menu", async ({ page }) => {
  await openEditor(page);
  await workspace(page, "Domain model");
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill("e");
  const rows = side.locator('[data-testid^="explorer-row-"]');
  await expect(rows.first()).toBeVisible();
  // The lowest row fully on screen, opened with the context-menu key's mouse equivalent.
  let low: Locator | undefined;
  for (let i = (await rows.count()) - 1; i >= 0 && !low; i--) {
    const box = await rows.nth(i).boundingBox();
    if (box && box.y > 380 && box.y + box.height < 590) low = rows.nth(i);
  }
  expect(low).toBeDefined();
  await low!.click({ button: "right" });
  const menu = page.getByTestId("row-menu");
  await expect(menu).toBeVisible();
  await inViewport(page, menu);
  await page.keyboard.press("Escape");
  await expect(menu).toBeHidden();

  // A window too short for the whole filter menu: it is clamped to the room and scrolls.
  await page.setViewportSize({ width: 1440, height: 260 });
  await side.getByTestId("explorer-filters").click();
  const filters = page.getByRole("menu");
  await expect(filters).toBeVisible();
  await inViewport(page, filters);
  expect(await filters.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
  await page.keyboard.press("End");
  await expect(filters.getByRole("menuitemcheckbox").last()).toBeFocused();
  await shownIn(filters, filters.getByRole("menuitemcheckbox").last());
});
