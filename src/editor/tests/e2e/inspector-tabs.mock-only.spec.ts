// The inspector's tabs: Properties, Attributes, JSON, Used. A value object and a relationship edit their attribute
// grid on the Attributes tab (no longer inside Properties); an entity lists its attributes read-only with "Open editor", which
// opens the entity editor on its Attributes tab; an enum has no Attributes tab and a remembered one falls back.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const select = async (page: Page, name: string) => {
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).click();
  await expect(page.getByTestId("inspector-title")).toHaveText(name);
};

test("the inspector's Attributes tab per kind", async ({ page }) => {
  await openEditor(page);
  const inspector = page.getByTestId("inspector");
  const views = inspector.getByRole("tablist", { name: "Inspector views" });

  // Value object: the grid lives on the Attributes tab, not in Properties.
  await select(page, "Money");
  await expect(views.getByRole("tab")).toHaveText(["Properties", "Attributes", "JSON", "Used"]);
  await expect(views.getByRole("tab", { name: "Properties" })).toHaveAttribute("aria-selected", "true");
  await expect(inspector.getByTestId("attribute-grid")).toHaveCount(0);
  await views.getByRole("tab", { name: "Attributes" }).click();
  const grid = inspector.getByRole("grid", { name: "Attributes of Money" });
  await expect(grid).toBeVisible();
  await expect(grid.locator('td[data-column="name"]')).toHaveText(["amount", "currency"]);

  // The tabs are reachable with the arrow keys.
  await views.getByRole("tab", { name: "Attributes" }).focus();
  await page.keyboard.press("ArrowRight");
  await expect(views.getByRole("tab", { name: "JSON" })).toBeFocused();
  await page.keyboard.press("ArrowLeft");
  await expect(views.getByRole("tab", { name: "Attributes" })).toBeFocused();

  // Enum: no Attributes tab; the remembered Attributes tab falls back to Properties.
  await select(page, "InvoiceStatus");
  await expect(views.getByRole("tab")).toHaveText(["Properties", "JSON", "Used"]);
  await expect(views.getByRole("tab", { name: "Properties" })).toHaveAttribute("aria-selected", "true");

  // Relationship: its attributes are on its own Attributes tab too, edited there; Properties keeps the ends.
  await select(page, "settles");
  await expect(views.getByRole("tab")).toHaveText(["Properties", "Attributes", "JSON", "Used"]);
  await expect(views.getByRole("tab", { name: "Properties" })).toHaveAttribute("aria-selected", "true");
  await expect(inspector.getByTestId("attribute-grid")).toHaveCount(0);
  await expect(inspector.getByLabel("Inverse name")).toBeVisible();
  await views.getByRole("tab", { name: "Attributes" }).click();
  const relationGrid = inspector.getByRole("grid", { name: "Attributes of settles" });
  await expect(relationGrid).toBeVisible();
  await expect(relationGrid.locator('td[data-column="name"]')).toHaveText(["allocated"]);
  await expect(inspector.getByLabel("Inverse name")).toHaveCount(0);

  // Back on the value object, its kind remembers Attributes.
  await select(page, "Money");
  await expect(views.getByRole("tab", { name: "Attributes" })).toHaveAttribute("aria-selected", "true");

  // Entity: a read-only list, and "Open editor" opens the entity editor on its Attributes tab.
  await select(page, "Invoice");
  await expect(inspector.getByTestId("attribute-grid")).toHaveCount(0);
  await views.getByRole("tab", { name: "Attributes" }).click();
  const list = inspector.getByRole("list", { name: "Attributes of Invoice" });
  await expect(list.getByRole("listitem").first()).toBeVisible();
  await expect(list).toContainText("number");
  await expect(list.getByRole("textbox")).toHaveCount(0);
  await inspector.getByRole("button", { name: "Open editor" }).click();
  const editor = page.getByRole("region", { name: "Editor: Invoice" });
  await expect(editor).toBeVisible();
  await expect(editor.getByRole("tab", { name: "Attributes" })).toHaveAttribute("aria-selected", "true");
  await expect(editor.getByRole("grid", { name: "Attributes of Invoice" })).toBeVisible();
});
