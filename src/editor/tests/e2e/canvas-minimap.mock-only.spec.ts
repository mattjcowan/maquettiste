// The Display menu's "Minimap" (workspaces/entities/EntitiesWorkspace.tsx): the minimap shows at the canvas's lower
// right corner by default, at 160 by 100 so it covers less of a short canvas; unticking it removes the map, the choice
// is kept per browser and diagram, and ticking it back brings the map at the same place.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const displayMenu = async (page: Page) => {
  await page.getByTestId("display-options").click();
  return page.getByRole("menu");
};

test("the minimap is on by default, small, and the Display menu hides and restores it", async ({ page }) => {
  await openEditor(page);
  const map = page.locator(".react-flow__minimap");
  await expect(map).toHaveCount(1);
  const canvas = (await page.getByTestId("canvas").boundingBox())!;
  const box = (await map.boundingBox())!;
  expect(box.width).toBeLessThanOrEqual(176);
  expect(box.height).toBeLessThanOrEqual(116);
  expect(box.x + box.width).toBeLessThanOrEqual(canvas.x + canvas.width);
  expect(box.y + box.height).toBeLessThanOrEqual(canvas.y + canvas.height);

  let menu = await displayMenu(page);
  const option = menu.getByRole("menuitemcheckbox", { name: "Minimap" });
  await expect(option).toHaveAttribute("aria-checked", "true");
  await option.click();
  await expect(map).toHaveCount(0);
  await page.keyboard.press("Escape");

  await page.reload();
  await expect(page.getByTestId("canvas")).toBeVisible();
  await expect(map).toHaveCount(0);

  menu = await displayMenu(page);
  await menu.getByRole("menuitemcheckbox", { name: "Minimap" }).click();
  await expect(map).toHaveCount(1);
});
