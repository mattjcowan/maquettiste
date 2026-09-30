// Settings (mock project): the vocabularies (tags, categories, stereotypes) and the conventions form,
// saved through PUT /api/project/settings.
import { expect, test } from "./fixtures";

test("vocabularies and conventions", async ({ page }, testInfo) => {
  await page.goto("/settings");
  const settings = page.getByTestId("settings-workspace");
  await expect(settings.getByRole("tab", { name: "General" })).toHaveAttribute("aria-selected", "true");
  await settings.getByRole("tab", { name: "Tags" }).click();
  await expect(settings.getByRole("textbox", { name: "Key of tag 1" })).toHaveValue("billing");

  await settings.getByRole("tab", { name: "Categories" }).click();
  await expect(settings.getByRole("tree", { name: "Categories" }).getByRole("treeitem")).toHaveCount(2);

  await settings.getByRole("tab", { name: "Stereotypes" }).click();
  // The mock model adds the actors' persona stereotype (mocks/model/processSeed.ts) to the fixture's three.
  await expect(settings.getByRole("list", { name: "Stereotype list" }).getByRole("listitem")).toHaveCount(testInfo.project.name === "mock" ? 4 : 3);

  await settings.getByRole("tab", { name: "Conventions" }).click();
  const save = page.getByTestId("save-conventions");
  await expect(save).toBeDisabled();
  await settings.getByRole("combobox", { name: "tableCase" }).selectOption("pascal");
  await expect(save).toBeEnabled();
  await save.click();
  await expect(page.getByTestId("notice")).toContainText("Conventions saved.");
  await expect(save).toBeDisabled();
  await expect(settings.getByRole("combobox", { name: "tableCase" })).toHaveValue("pascal");

  // The saved convention reaches the database view.
  await page.getByRole("navigation", { name: "Explorers" }).getByRole("button", { name: "Databases", exact: true }).click();
  await expect(page.getByRole("group", { name: "Table Invoices", exact: true })).toBeVisible();
});
