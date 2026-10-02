// Many editor tabs (the owner: "a horizontal scrollbar on the tabs squishing the text"): the strip keeps its 28 px and
// shows no scrollbar; arrows at its ends scroll it, a vertical wheel scrolls it sideways, and the shown tab stays in
// view. A tab's menu closes others, to the right and all; a middle click closes a tab.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const NAMES = [
  "Invoice",
  "Payment",
  "Customer",
  "Product",
  "InvoiceLine",
  "InvoiceStatus",
  "Money",
  "EmailAddress",
  "contains",
  "places",
  "settles",
  "refers to",
];

const open = async (page: Page, name: string) => {
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).first().dblclick();
  await expect(page.getByRole("region", { name: `Editor: ${name}` })).toBeVisible();
};

const height = async (locator: Locator) => (await locator.boundingBox())!.height;

/** Whether a tab lies wholly inside the visible part of its strip. */
const inView = (tab: Locator) =>
  tab.evaluate((el) => {
    const a = el.getBoundingClientRect();
    const b = el.closest('[role="toolbar"]')!.getBoundingClientRect();
    return a.left >= b.left - 1 && a.right <= b.right + 1;
  });

test("many editor tabs scroll without a scrollbar, and the tab menu closes them", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.setViewportSize({ width: 1100, height: 800 });
  await openEditor(page);
  const bar = page.getByTestId("editor-tabs");
  const strip = bar.getByRole("toolbar", { name: "Open editors" });
  const tabs = bar.getByTestId("editor-tab");

  await open(page, NAMES[0]);
  const firstName = tabs.first().getByTestId("editor-tab-name");
  const textHeight = await height(firstName);
  expect(await height(bar)).toBe(28);
  await expect(page.getByTestId("tabs-scroll-left")).toHaveCount(0);
  await expect(page.getByTestId("tabs-scroll-right")).toHaveCount(0);

  for (const name of NAMES.slice(1)) await open(page, name);
  await expect(tabs).toHaveCount(NAMES.length);

  // No squish: the strip keeps its height, the text its height, and the strip has no scrollbar.
  expect(await height(bar)).toBe(28);
  expect(await height(firstName)).toBe(textHeight);
  expect(await strip.evaluate((el: HTMLElement) => el.offsetHeight - el.clientHeight)).toBe(0);
  expect(await strip.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);

  // The last opened tab shows and is in view, at the end: only the left arrow shows, as tall as the strip.
  await expect(tabs.last()).toContainText("refers to");
  expect(await inView(tabs.last())).toBe(true);
  const left = page.getByTestId("tabs-scroll-left");
  const right = page.getByTestId("tabs-scroll-right");
  await expect(left).toBeVisible();
  await expect(right).toHaveCount(0);
  await expect(left).toHaveAttribute("title", "Scroll tabs left");
  expect(await height(left)).toBeLessThanOrEqual(28);

  // A vertical wheel over the strip scrolls it sideways, back to the start: the right arrow shows, the left one goes.
  await strip.hover();
  await page.mouse.wheel(0, -5000);
  await expect.poll(() => strip.evaluate((el) => el.scrollLeft)).toBe(0);
  await expect(right).toBeVisible();
  await expect(left).toHaveCount(0);
  await expect(right).toHaveAttribute("title", "Scroll tabs right");

  // The right arrow scrolls about 60% of the visible width; the left arrow then shows.
  const visible = await strip.evaluate((el) => el.clientWidth);
  await right.click();
  await expect.poll(() => strip.evaluate((el) => el.scrollLeft)).toBeGreaterThan(visible * 0.4);
  await expect(left).toBeVisible();
  await left.click();
  await expect.poll(() => strip.evaluate((el) => el.scrollLeft)).toBe(0);

  // Showing a tab that is out of view scrolls it into view.
  const settles = tabs.filter({ hasText: "settles" });
  expect(await inView(settles)).toBe(false);
  await open(page, "settles");
  await expect.poll(() => inView(settles)).toBe(true);

  // A middle click closes a tab.
  await tabs.filter({ hasText: "refers to" }).click({ button: "middle" });
  await expect(tabs).toHaveCount(NAMES.length - 1);

  // The tab menu: Close to the right of the fourth tab leaves four.
  await tabs.nth(3).click({ button: "right" });
  const menu = page.getByTestId("tab-menu");
  await expect(menu).toBeVisible();
  await expect(menu.getByRole("menuitem")).toHaveText(["Close", "Close others", "Close to the right", "Close all", "Close saved", "Unpin"]);
  await menu.getByRole("menuitem", { name: "Close to the right" }).click();
  await expect(tabs).toHaveCount(4);
  await expect(menu).toHaveCount(0);

  // Close others, opened with the keyboard, leaves the one tab.
  await tabs.nth(1).getByRole("button").first().focus();
  await page.keyboard.press("Shift+F10");
  await expect(menu).toBeVisible();
  await menu.getByRole("menuitem", { name: "Close others" }).click();
  await expect(tabs).toHaveCount(1);
  await expect(tabs.first()).toContainText("Payment");
  await expect(page.getByTestId("tabs-scroll-left")).toHaveCount(0);
  await expect(page.getByTestId("tabs-scroll-right")).toHaveCount(0);

  // Close all: no tab left, and the strip goes.
  await open(page, "Customer");
  await expect(tabs).toHaveCount(2);
  await tabs.first().click({ button: "right" });
  await expect(menu.getByRole("menuitem", { name: "Close to the right" })).toBeEnabled();
  await menu.getByRole("menuitem", { name: "Close all" }).click();
  await expect(bar).toHaveCount(0);
});
