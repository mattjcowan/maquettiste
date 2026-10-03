// The property bag (the owner: "a properties bag (Dictionary<string,string>) for all the objects ... default functionality for
// every object in the inspector"): free keys with text values on every element, a number or true/false when chosen, declared
// keys left to their typed form; each commit one save and one undo step. A table laid out by convention edits from the inspector,
// its first edit storing it as a table file, and its columns carry tags and properties; every table has a JSON tab.
import type { Page } from "@playwright/test";
import { card, expect, openEditor, test } from "./fixtures";

const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";

type TableDoc = { id: string; name?: string; origin?: string; properties?: Record<string, unknown>; columns?: Record<string, unknown>[] };

/** One element's document, read through the API. */
const documentOf = (page: Page, id: string) =>
  page.evaluate(async (elementId) => {
    const read = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: [elementId] }),
    });
    return ((await read.json()) as { elements: { json: Record<string, unknown> }[] }).elements[0]?.json;
  }, id);

/** The table file named `name` (stored as a file), read through the API, or undefined. */
const fileOf = (page: Page, name: string) =>
  page.evaluate(async (tableName) => {
    const index = (await (await fetch("/api/model/index")).json()) as { elements?: { id: string; kind: string }[] } | { id: string; kind: string }[];
    const rows = (Array.isArray(index) ? index : (index.elements ?? [])).filter((r) => r.kind === "table");
    const read = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: rows.map((r) => r.id) }),
    });
    return ((await read.json()) as { elements: { json: TableDoc }[] }).elements
      .map((d) => d.json)
      .find((t) => t.name === tableName && t.origin !== "synthesized");
  }, name);

const propertiesOf = async (page: Page, id: string) => (await documentOf(page, id))?.properties;

async function openCustomersTable(page: Page) {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId("database-table-customers").click();
  await expect(page.getByTestId("column-grid")).toBeVisible();
  const inspector = page.getByTestId("table-inspector");
  await expect(inspector.getByTestId("inspector-title")).toHaveText("customers");
  return inspector;
}

test("an entity's property bag: text by default, a number when chosen, declared keys stay typed, each commit one undo step", async ({ page }) => {
  await openEditor(page);
  await card(page, "Customer").click();
  const inspector = page.getByRole("region", { name: "Inspector: Customer" });
  const bag = inspector.getByTestId("property-bag");
  // retentionDays is declared (the retention extension, through «audited»): typed above, never a row of the bag.
  await expect(inspector.getByTestId("custom-properties")).toContainText("retentionDays");
  await expect(bag.getByTestId("property-row-retentionDays")).toHaveCount(0);

  // Add property opens a row focused on its key; the key saves with an empty text, the value on Enter.
  await bag.getByRole("button", { name: "Add property" }).click();
  await expect(bag.getByLabel("Key of the new property")).toBeFocused();
  await page.keyboard.type("owner");
  await page.keyboard.press("Tab");
  await expect.poll(() => propertiesOf(page, CUSTOMER)).toEqual({ owner: "" });
  await page.keyboard.type("finance");
  await page.keyboard.press("Enter");
  await expect.poll(() => propertiesOf(page, CUSTOMER)).toEqual({ owner: "finance" });

  // A value typed as text, then its type changed to Number: the file holds the JSON number.
  await bag.getByRole("button", { name: "Add property" }).click();
  await page.keyboard.type("priority");
  await page.keyboard.press("Tab");
  await page.keyboard.type("3");
  await page.keyboard.press("Enter");
  await expect.poll(() => propertiesOf(page, CUSTOMER)).toEqual({ owner: "finance", priority: "3" });
  await bag.getByLabel("Type of priority").selectOption("number");
  await expect.poll(() => propertiesOf(page, CUSTOMER)).toEqual({ owner: "finance", priority: 3 });
  await expect(bag.getByLabel("Type of owner")).toHaveValue("text");

  // A duplicate key is said under its row, and nothing is saved.
  await bag.getByRole("button", { name: "Add property" }).click();
  await page.keyboard.type("owner");
  await page.keyboard.press("Enter");
  await expect(bag.getByTestId("property-row-new").getByRole("alert")).toHaveText("Another property is named owner.");
  await bag.getByTestId("property-row-new").getByRole("button", { name: "Remove property" }).click();
  await expect(bag.getByTestId("property-row-new")).toHaveCount(0);
  expect(await propertiesOf(page, CUSTOMER)).toEqual({ owner: "finance", priority: 3 });

  // Remove (its tooltip names it) is one save; Undo brings the property back.
  const remove = bag.getByTestId("property-row-owner").getByRole("button", { name: "Remove property" });
  await expect(remove).toHaveAttribute("title", "Remove property");
  await remove.click();
  await expect.poll(() => propertiesOf(page, CUSTOMER)).toEqual({ priority: 3 });
  await expect(bag.getByTestId("property-row-owner")).toHaveCount(0);
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(() => propertiesOf(page, CUSTOMER)).toEqual({ owner: "finance", priority: 3 });
  await expect(bag.getByLabel("Value of owner")).toHaveValue("finance");
});

test("a laid-out table edits from the inspector: its first property stores it as a file, a column takes a property and a tag", async ({ page }) => {
  const inspector = await openCustomersTable(page);
  expect(await fileOf(page, "customers")).toBeUndefined();
  const tableSection = inspector.getByTestId("table-inspector-table");
  await expect(inspector.getByTestId("table-inspector-path")).toHaveText("billing.customers · not stored as a table file yet");

  // The table's own property: the first commit stores the table as a file, with it.
  const tableBag = tableSection.getByTestId("property-bag");
  await tableBag.getByRole("button", { name: "Add property" }).click();
  await page.keyboard.type("tier");
  await page.keyboard.press("Tab");
  await page.keyboard.type("gold");
  await page.keyboard.press("Enter");
  await expect.poll(async () => (await fileOf(page, "customers"))?.properties).toEqual({ tier: "gold" });
  await expect(page.getByText("customers is now stored as a table file.")).toBeVisible();

  // A column's property and tag go to that column's entry in the file.
  await page.getByTestId("column-grid").getByTestId("column-row-name").locator('[data-column="name"]').click();
  const columnSection = inspector.getByTestId("table-inspector-column");
  await expect(columnSection).toContainText("Column name");
  const columnBag = columnSection.getByTestId("property-bag");
  await columnBag.getByRole("button", { name: "Add property" }).click();
  await page.keyboard.type("owner");
  await page.keyboard.press("Tab");
  await page.keyboard.type("finance");
  await page.keyboard.press("Enter");
  const nameColumn = async () => (await fileOf(page, "customers"))?.columns?.find((c) => c.name === "name");
  await expect.poll(async () => (await nameColumn())?.properties).toEqual({ owner: "finance" });
  await columnSection.getByLabel("Add to Tags").selectOption("pii");
  await expect.poll(async () => (await nameColumn())?.tags).toEqual(["pii"]);
  await expect(columnSection.getByRole("button", { name: "Remove pii from Tags" })).toBeVisible();

  // The JSON tab (back on the table) shows the file with both.
  await inspector.getByTestId("inspector-back-to-table").click();
  await inspector.getByRole("tab", { name: "JSON" }).click();
  await expect(inspector).toContainText('"tier": "gold"');
  await expect(inspector).toContainText('"owner": "finance"');
});

test("a laid-out table's JSON tab shows the table read-only, and Store as a table file makes the whole file", async ({ page }) => {
  const inspector = await openCustomersTable(page);
  await inspector.getByRole("tab", { name: "JSON" }).click();
  const resolved = inspector.getByTestId("table-inspector-resolved-json");
  await expect(resolved).toContainText("The table as resolved, read-only");
  await expect(resolved).toContainText('"name": "customers"');

  await resolved.getByTestId("table-inspector-store-file").click();
  await expect(page.getByText("customers is now stored as a table file.")).toBeVisible();
  await expect(inspector.getByTestId("table-inspector-resolved-json")).toHaveCount(0);
  const file = await fileOf(page, "customers");
  expect(file).toMatchObject({ kind: "table", name: "customers" });
  expect(file?.columns?.length).toBeGreaterThan(1);
  await inspector.getByRole("tab", { name: "JSON" }).click();
  await expect(inspector).toContainText('"name": "customer_since"');

  // Storing is one undo step.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(() => fileOf(page, "customers")).toBeUndefined();
});
