// Relation attributes on the diagram (canvas/RelationEdge.tsx): the Display menu's "Relation attributes" draws each
// relation's attributes in a box hung off its label (the UML association class), at the cards' attribute detail, kept
// per browser; turning it on moves nothing, Auto-layout keeps the box off the cards, the box selects the relation, and
// the canvas passes the axe check with the boxes shown.
import AxeBuilder from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { card, expect, openEditor, test } from "./fixtures";

const displayMenu = async (page: Page) => {
  await page.getByTestId("display-options").click();
  return page.getByRole("menu");
};

test("Relation attributes draws the box with its rows, follows the detail, survives a reload and passes axe", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  const pill = page.getByTestId("relation-label-settles");
  const box = page.getByTestId("relation-box-settles");
  // Off by default: the pill's badge counts the attributes and no box is drawn.
  await expect(pill.getByLabel("1 relation attributes")).toBeVisible();
  await expect(box).toHaveCount(0);
  const before = await card(page, "Payment").boundingBox();

  let menu = await displayMenu(page);
  const option = menu.getByRole("menuitemcheckbox", { name: "Relation attributes" });
  await expect(option).toHaveAttribute("aria-checked", "false");
  await option.click();
  await expect(option).toHaveAttribute("aria-checked", "true");
  await page.keyboard.press("Escape");

  // The box: the relation's name, then one row per attribute with its type and the required marker; the pill drops the badge.
  await expect(box).toBeVisible();
  await expect(box).toContainText("settles");
  const rows = box.getByTestId("relation-attribute");
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText("allocated");
  await expect(rows.first()).toContainText("Money");
  await expect(rows.first()).toContainText("required");
  await expect(pill.getByLabel("1 relation attributes")).toHaveCount(0);
  await expect(page.getByTestId("relation-box-connector-settles")).toHaveCount(1);
  // A relation without attributes shows no box; and nothing moved.
  await expect(page.getByTestId("relation-box-places")).toHaveCount(0);
  expect(await card(page, "Payment").boundingBox()).toEqual(before);

  // Auto-layout makes room for the box: it lands clear of the pill and of every card.
  await page.getByTestId("auto-layout").click();
  await expect(page.getByTestId("auto-layout")).toHaveText(/Auto-layout/);
  await page.waitForTimeout(600);
  const b = (await box.boundingBox())!;
  const clear = (o: { x: number; y: number; width: number; height: number }) =>
    b.y >= o.y + o.height || b.y + b.height <= o.y || b.x >= o.x + o.width || b.x + b.width <= o.x;
  expect(clear((await pill.boundingBox())!), "box clear of the pill").toBe(true);
  for (const node of await page.locator(".react-flow__node").all()) expect(clear((await node.boundingBox())!), "box clear of the cards").toBe(true);

  // Clicking the box selects the relation.
  await box.click();
  await expect(page.getByRole("region", { name: /^Inspector: / })).toContainText("settles");

  // Kept per browser across a reload.
  await page.reload();
  await expect(page.locator(".react-flow__node")).toHaveCount(5);
  await expect(box).toBeVisible();
  await expect(rows).toHaveCount(1);

  // axe on the canvas with the box shown.
  await page.waitForLoadState("networkidle");
  const scan = await new AxeBuilder({ page }).exclude(".react-flow__minimap").analyze();
  const bad = scan.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => `${v.id} ${v.nodes[0]?.target.join(" ")}`);
  expect(bad).toEqual([]);

  // Names only: the rows without types. Keys only: a relation has no key of its own, so no box and the badge is back.
  menu = await displayMenu(page);
  await menu.getByRole("menuitemradio", { name: "Names only" }).click();
  await page.keyboard.press("Escape");
  await expect(rows.first()).toContainText("allocated");
  await expect(rows.first()).not.toContainText("Money");
  menu = await displayMenu(page);
  await menu.getByRole("menuitemradio", { name: "Keys only" }).click();
  await page.keyboard.press("Escape");
  await expect(box).toHaveCount(0);
  await expect(pill.getByLabel("1 relation attributes")).toBeVisible();
});
