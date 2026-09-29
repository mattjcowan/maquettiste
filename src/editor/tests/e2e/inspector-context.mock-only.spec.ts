// The inspector follows the ACTIVE context (the owner's 2026-09-29 test pass): each explorer keeps its own selection
// and an explorer without one says so; an editor tab shows its element; Generate shows the open pack; Settings has no
// inspector. The walk asserts the inspector's heading at every step.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

test("the inspector follows the rail, the explorer, the editor, Generate and Settings", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const title = page.getByTestId("inspector-title");
  await expect(title).toHaveText("Select an element in Domain model");

  // Explorer: the selection shows.
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").click();
  await expect(title).toHaveText("Invoice");

  // Rail: another explorer has its own (empty) selection; coming back restores Invoice.
  await page.getByTestId("rail-diagrams").click();
  await expect(title).toHaveText("Select an element in Diagrams");
  await page.getByTestId("rail-domain-model").click();
  await expect(title).toHaveText("Invoice");

  // Editor: the active tab's element, and the next one when the preview tab moves.
  await side.getByLabel("Search the model").fill("Payment");
  await side.getByTestId("explorer-row-Payment").dblclick();
  await expect(page.getByRole("region", { name: "Editor: Payment" })).toBeVisible();
  await expect(title).toHaveText("Payment");

  // Generate: nothing until a pack opens, then the pack.
  await page.getByTestId("rail-generate").click();
  await expect(title).toHaveText("Select a pack in Generate");
  await page.getByRole("tree", { name: "Packs" }).getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
  await expect(title).toHaveText("sql-ddl");

  // Settings: no inspector at all.
  await page.getByTestId("rail-settings").click();
  await expect(page.getByTestId("settings-workspace")).toBeVisible();
  await expect(page.getByRole("complementary", { name: "Inspector" })).toHaveCount(0);

  // Back to the domain model: its selection is still its own.
  await page.getByTestId("rail-domain-model").click();
  await expect(title).toHaveText("Payment");
});
