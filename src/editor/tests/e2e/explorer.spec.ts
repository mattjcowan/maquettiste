// The rail of explorers and the tree (explorer-redesign.md 1.0, 1.1, 1.8, 3.4): the owner's rail order, one
// explorer at a time under a header naming it with its totals, expansion kept per explorer and selection kept
// across rail switches, the keyboard model, the row menus, a domain's Open, and the pinned second explorer.
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });

test("the rail shows one explorer at a time, under a header with its totals", async ({ page }) => {
  await openEditor(page);
  const rail = page.getByRole("navigation", { name: "Explorers" });
  const names = await rail.getByRole("button").evaluateAll((buttons) => buttons.map((b) => b.getAttribute("aria-label")));
  expect(names).toEqual(["Domain model", "Reference data", "Databases", "Diagrams", "Generate", "Settings", "Account"]);

  const side = explorer(page);
  await expect(side.getByRole("heading", { name: "Domain model" })).toBeVisible();
  await expect(side.getByTestId("explorer-totals")).toContainText("domain");
  await expect(side.getByRole("tree")).toHaveCount(1);
  const billing = side.getByTestId("explorer-domain-Billing");
  await expect(billing).toHaveAttribute("aria-expanded", "false");
  await billing.click();
  await expect(billing).toHaveAttribute("aria-expanded", "true");

  // Select Invoice, then switch: the Databases explorer replaces the Domain model one and the centre shows the
  // Database screen.
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").click();
  await expect(page.getByRole("region", { name: "Inspector: Invoice" })).toBeVisible();
  await side.getByLabel("Search the model").fill("");
  await workspace(page, "Databases");
  await expect(page.getByTestId("workspace-database")).toBeVisible();
  await expect(side.getByRole("heading", { name: "Databases" })).toBeVisible();
  await expect(side.getByRole("tree")).toHaveCount(1);
  await expect(side.getByRole("tree", { name: "Databases" }).getByTestId("explorer-folder-main")).toBeVisible();
  await expect(side.getByRole("tree", { name: "Domain model" })).toHaveCount(0);

  // Back to Domain model: its expansion was kept, and the selection outlived the switch.
  await workspace(page, "Domain model");
  await expect(side.getByTestId("explorer-domain-Billing")).toHaveAttribute("aria-expanded", "true");
  await expect(side.getByTestId("explorer-row-Invoice")).toHaveAttribute("aria-selected", "true");

  // Generate lists its packs in the sidebar; Settings keeps the sidebar and opens its screen.
  await workspace(page, "Generate");
  await expect(side.getByTestId("explorer-generate")).toBeVisible();
  await expect(side.getByRole("tree")).toHaveCount(0);
  await workspace(page, "Settings");
  await expect(page.getByTestId("workspace-settings")).toBeVisible();
  await expect(page.getByRole("button", { name: "Settings" })).toHaveAttribute("aria-current", "page");
});

test("keyboard, row menus and a domain's Open", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const tree = side.getByRole("tree", { name: "Domain model" });
  await tree.focus();
  await page.keyboard.press("Home");
  const billing = side.getByTestId("explorer-domain-Billing");
  await expect(tree).toHaveAttribute("aria-activedescendant", (await billing.getAttribute("id"))!);
  await page.keyboard.press("ArrowRight");
  await expect(billing).toHaveAttribute("aria-expanded", "true");
  // Type-ahead to the Entities folder, open it, go to its first entity and open it with Enter.
  await page.keyboard.type("ent");
  const entities = billing.locator("xpath=following-sibling::*").and(side.getByTestId("explorer-folder-Entities"));
  await expect(tree).toHaveAttribute("aria-activedescendant", (await entities.first().getAttribute("id"))!);
  await page.keyboard.press("ArrowRight");
  await expect(entities.first()).toHaveAttribute("aria-expanded", "true");
  await page.keyboard.press("ArrowRight");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("region", { name: /^Inspector: / })).toBeVisible();

  // Shift+F10 opens the row's menu; Escape closes it.
  await page.keyboard.press("Shift+F10");
  const menu = page.getByTestId("row-menu");
  await expect(menu.getByRole("menuitem", { name: "Open" })).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: "Delete" })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(menu).toHaveCount(0);

  // A domain's menu: Open shows the domain editor, General, Tags and Categories (1.11).
  await billing.click({ button: "right" });
  await menu.getByRole("menuitem", { name: "Open" }).click();
  const editor = page.getByTestId("element-editor");
  await expect(editor).toHaveAttribute("data-kind", "package");
  for (const tab of ["General", "Tags", "Categories"]) await expect(editor.getByRole("tab", { name: tab })).toBeVisible();
  await editor.getByRole("tab", { name: "Tags" }).click();
  await expect(editor.getByRole("tabpanel")).toContainText("This domain's tags");
  await expect(editor.getByTestId("inherited-tag-vocabulary")).toContainText("global");
});

test("a second explorer pinned beside the first, off by default", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await expect(side.getByRole("tree")).toHaveCount(1);
  await side.getByTestId("explorer-header-menu").click();
  await page.getByRole("menuitem", { name: "Databases" }).click();
  await expect(side.getByRole("tree")).toHaveCount(2);
  await expect(side.getByRole("tree", { name: "Databases" })).toBeVisible();
  await page.reload();
  await expect(side.getByRole("tree")).toHaveCount(2);
  await side.getByTestId("explorer-header-menu").first().click();
  await page.getByRole("menuitem", { name: "Unpin the second explorer" }).click();
  await expect(side.getByRole("tree")).toHaveCount(1);
});
