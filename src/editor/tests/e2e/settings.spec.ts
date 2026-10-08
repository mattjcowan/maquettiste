// Settings (mock project): the vocabularies (tags, categories, stereotypes) and the conventions form,
// saved through PUT /api/project/settings.
import { expect, test } from "./fixtures";

const hex = (s: string) => `#${s}`;

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

test("Up and Down move through the stereotype list, picking each one, and so through the database's table list", async ({ page }) => {
  await page.goto("/settings/stereotypes");
  const list = page.getByRole("list", { name: "Stereotype list" });
  const items = list.getByRole("button");
  const current = list.locator('button[aria-current="true"]');
  await items.first().click();
  await expect(current).toHaveText(await items.first().innerText());
  await page.keyboard.press("ArrowDown");
  await expect(items.nth(1)).toBeFocused();
  await expect(current).toHaveText(await items.nth(1).innerText());
  await page.keyboard.press("End");
  await expect(items.last()).toBeFocused();
  await expect(current).toHaveText(await items.last().innerText());
  await page.keyboard.press("ArrowUp");
  await expect(current).toHaveText(await items.nth((await items.count()) - 2).innerText());
  await page.keyboard.press("Home");
  await expect(current).toHaveText(await items.first().innerText());

  await page.goto("/database");
  const tables = page.getByRole("list", { name: "Table list" }).getByRole("button");
  await tables.first().click();
  await page.keyboard.press("ArrowDown");
  await expect(tables.nth(1)).toBeFocused();
  await expect(tables.nth(1)).toHaveAttribute("aria-current", "true");
});

test("a long stereotype list scrolls on its own, under the New field, and the form beside it stays in view", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 520 });
  await page.goto("/settings/stereotypes");
  const list = page.getByRole("list", { name: "Stereotype list" });
  const key = page.getByRole("textbox", { name: "New stereotype key" });
  for (let i = 1; i <= 15; i++) {
    await key.fill(`zz-many-${String(i).padStart(2, "0")}`);
    await page.getByRole("button", { name: "Add stereotype" }).click();
    await expect(page.locator("#st-key")).toHaveValue(`zz-many-${String(i).padStart(2, "0")}`);
  }
  // The New field comes before the list; the list overflows and scrolls, the page does not.
  const fieldBox = (await key.boundingBox())!;
  const listBox = (await list.boundingBox())!;
  expect(fieldBox.y).toBeLessThan(listBox.y);
  expect(await list.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
  // The last one added is in view in the list, and so are the New field and the form.
  await expect(list.getByRole("button", { name: /zz-many-15/ })).toBeInViewport();
  await expect(key).toBeInViewport();
  await expect(page.locator("#st-key")).toBeInViewport();
  await list.getByRole("button").first().click();
  await expect(list.getByRole("button").first()).toBeInViewport();
  await expect(page.locator("#st-key")).toBeInViewport();
});

test("the stereotype list filters by the kinds a stereotype applies to, any of those checked", async ({ page }) => {
  await page.goto("/settings/stereotypes");
  // The open menu hides the page from the accessibility tree: the list is found by its label attribute.
  const list = page.locator('ul[aria-label="Stereotype list"]');
  const filter = page.getByTestId("stereotype-applies-filter");
  await expect(filter).toHaveText("Applies to: any kind");
  await expect(list.locator("li")).toHaveCount(4);

  await filter.click();
  // The menu offers the kinds some stereotype applies to, in the form's order; it stays open.
  await expect(page.getByRole("menuitemcheckbox")).toHaveText(["entity", "table", "column", "actor"]);
  await page.getByRole("menuitemcheckbox", { name: "table" }).click();
  await expect(list.locator("li")).toHaveCount(1);
  await expect(list).toContainText("audited");
  await page.getByRole("menuitemcheckbox", { name: "actor" }).click();
  await expect(list.locator("li")).toHaveCount(2);
  await expect(list).toContainText("persona");
  await page.keyboard.press("Escape");
  await expect(filter).toHaveText("Applies to: table, actor");
  await expect(page.getByTestId("stereotype-filter-count")).toHaveText("2 of 4");

  await filter.click();
  await page.getByRole("menuitem", { name: "Clear the filter" }).click();
  await expect(list.locator("li")).toHaveCount(4);
  await expect(page.getByTestId("stereotype-filter-count")).toHaveCount(0);
});

test("the stereotype form offers every kind a stereotype can apply to, grouped", async ({ page }) => {
  await page.goto("/settings/stereotypes");
  await page
    .getByRole("list", { name: "Stereotype list" })
    .getByRole("button", { name: /audited/ })
    .click();
  const databases = page.getByRole("group", { name: "Databases" });
  await expect(databases.getByRole("checkbox", { name: "column" })).toBeChecked();
  await expect(databases.getByRole("checkbox", { name: "table" })).toBeChecked();
  await expect(page.getByRole("group", { name: "Processes" }).getByRole("checkbox", { name: "transition" })).not.toBeChecked();
  await page.getByRole("group", { name: "Processes" }).getByRole("checkbox", { name: "transition" }).click();
  await page
    .getByRole("list", { name: "Stereotype list" })
    .getByRole("button", { name: /persona/ })
    .click();
  await expect(page.getByRole("group", { name: "Processes" }).getByRole("checkbox", { name: "actor" })).toBeChecked();
  await page
    .getByRole("list", { name: "Stereotype list" })
    .getByRole("button", { name: /audited/ })
    .click();
  await expect(page.getByRole("group", { name: "Processes" }).getByRole("checkbox", { name: "transition" })).toBeChecked();
  await expect(databases.getByRole("checkbox", { name: "column" })).toBeChecked();
});

test("a tag's color is picked with a color picker, and the field beside it shows the hex", async ({ page }) => {
  await page.goto("/settings/tags");
  const settings = page.getByTestId("settings-workspace");
  const key = await settings.getByRole("textbox", { name: "Key of tag 1" }).inputValue();
  const picker = settings.getByLabel(`Pick the color of ${key}`);
  await expect(picker).toHaveAttribute("type", "color");
  await picker.fill(hex("7a1fa2"));
  await picker.blur();
  await expect(settings.getByRole("textbox", { name: `Color of ${key}`, exact: true })).toHaveValue(hex("7a1fa2"));
  // A color typed by name stays as typed (the picker, which shows hex colors only, starts from the accent then).
  await settings.getByRole("textbox", { name: `Color of ${key}`, exact: true }).fill("teal");
  await expect(settings.getByRole("textbox", { name: `Color of ${key}`, exact: true })).toHaveValue("teal");
});
