// The Database screen's column editor and the comments convention (the owner: "the ability to set a description for each table
// column, which may or may not be generated into DB metadata"): a description typed in the grid lands in the table's file,
// becomes the column's comment, and each edit is one undo step; Settings > Conventions turns the description fallback off. The Databases
// side inspects the TABLE and shows its columns as ordinary columns (it names no entity), one subject at a time, the physical
// fields are free, and a foreign key pinned to another type than its key is MQ4005.
import AxeBuilder from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace, keepDdlOpen } from "./fixtures";

// The DDL preview is hidden by default; these specs read it.
test.beforeEach(({ page }) => keepDdlOpen(page));

const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const NAME = "01J92P0V0MS09YFZHX07JQ3KMN";

type TableDoc = {
  id: string;
  name?: string;
  columns?: { id: string; name?: string; [k: string]: unknown }[];
};

/** The table files of the model, read through the API. */
const tableFiles = (page: Page) =>
  page.evaluate(async () => {
    const index = (await (await fetch("/api/model/index")).json()) as { elements?: { id: string; kind: string }[] } | { id: string; kind: string }[];
    const rows = (Array.isArray(index) ? index : (index.elements ?? [])).filter((r) => r.kind === "table");
    // The batch read lists a row deleted meanwhile (an undo while polling) as missing instead of answering 404.
    const read = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: rows.map((r) => r.id) }),
    });
    return ((await read.json()) as { elements: { json: TableDoc }[] }).elements.map((d) => d.json);
  });

/** The table file named `name`, or undefined. */
const fileOf = async (page: Page, name: string) => (await tableFiles(page)).find((t) => t.name === name);

const grid = (page: Page) => page.getByTestId("column-grid");
const cell = (page: Page, column: string, field: string) => grid(page).getByTestId(`column-row-${column}`).locator(`[data-column="${field}"]`);

async function openTable(page: Page, name: string, reload = true) {
  // The mock keeps its model in the page: a later step of a test switches screens without reloading.
  if (reload) await page.goto("/database");
  else await workspace(page, "Databases");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId(`database-table-${name}`).click();
  await expect(page.getByTestId("column-panel-table")).toContainText(name);
  await expect(grid(page)).toBeVisible();
}

test("a description typed in the grid lands in the table's file, becomes its comment, and undoes in one step", async ({ page }) => {
  await openTable(page, "customers");
  const columns = await grid(page).locator("tbody tr").count();
  expect(await fileOf(page, "customers")).toBeDefined();

  await cell(page, "name", "description").dblclick();
  await grid(page).getByRole("textbox", { name: "Description of name" }).fill("The legal name, as printed.");
  await page.keyboard.press("Enter");
  await expect(cell(page, "name", "description")).toHaveText("The legal name, as printed.");
  await expect(cell(page, "name", "comment")).toHaveText("The legal name, as printed.");
  await expect(page.getByTestId("ddl-preview")).toContainText("IS 'The legal name, as printed.'");
  // The file is the whole table: every column, the edited one with its description.
  const file = (await fileOf(page, "customers"))!;
  expect(file.columns).toHaveLength(columns);
  expect(file.columns?.find((c) => c.name === "name")).toMatchObject({ description: "The legal name, as printed." });

  // A second edit is one save of the same file; an explicit comment wins over the description.
  await cell(page, "email", "comment").click();
  await page.keyboard.press("Enter");
  await grid(page).getByRole("textbox", { name: "Comment of email" }).fill("Where invoices go.");
  await page.keyboard.press("Enter");
  await expect(cell(page, "email", "comment")).toHaveText("Where invoices go.");
  await expect.poll(async () => (await fileOf(page, "customers"))?.columns?.find((c) => c.name === "email")?.comment).toBe("Where invoices go.");

  // The grid passes an accessibility scan.
  const scan = await new AxeBuilder({ page }).include('[data-testid="column-panel"]').analyze();
  expect(scan.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);

  // Each edit is one undo step: the comment goes, then the description.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(cell(page, "email", "comment")).toHaveText("");
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(cell(page, "name", "description")).toHaveText("");
  await expect(cell(page, "name", "comment")).toHaveText("");
});

test("edits made in quick succession on one table land in its file once each", async ({ page }) => {
  await openTable(page, "customers");
  // Comment, then Tab straight into Description and type: the second edit is made while the first is still saving.
  await cell(page, "email", "comment").click();
  await page.keyboard.press("Enter");
  await page.keyboard.type("Where invoices go.");
  await page.keyboard.press("Tab");
  await page.keyboard.type("The address we send invoices to.");
  await page.keyboard.press("Enter");
  await expect(cell(page, "email", "description")).toHaveText("The address we send invoices to.");
  await expect(cell(page, "email", "comment")).toHaveText("Where invoices go.");
  const files = (await tableFiles(page)).filter((t) => t.name === "customers");
  expect(files).toHaveLength(1);
  expect(files[0].columns?.find((c) => c.name === "email")).toMatchObject({ comment: "Where invoices go.", description: "The address we send invoices to." });
});

test("Settings > Conventions sets comments to none: descriptions stop filling comments", async ({ page }) => {
  await openTable(page, "customers");
  await cell(page, "name", "description").dblclick();
  await grid(page).getByRole("textbox", { name: "Description of name" }).fill("The legal name, as printed.");
  await page.keyboard.press("Enter");
  await expect(page.getByTestId("ddl-preview")).toContainText("IS 'The legal name, as printed.'");
  await workspace(page, "Settings");
  await page.getByRole("tab", { name: "Conventions" }).click();
  const control = page.getByTestId("conv-comments");
  await expect(control.locator("option:checked")).toHaveText("inherit: descriptions (engine default)");
  await control.selectOption("none");
  await page.getByTestId("save-conventions").click();
  await expect(page.getByText("Conventions saved.")).toBeVisible();

  await openTable(page, "customers", false);
  await expect(page.getByTestId("ddl-preview")).toContainText("CREATE TABLE");
  await expect(page.getByTestId("ddl-preview")).not.toContainText("The legal name, as printed.");
});

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

test("the Databases side inspects the table, its columns are ordinary columns, and a column shows alone with the way back", async ({ page }) => {
  await openTable(page, "customers");
  const inspector = page.getByTestId("table-inspector");
  await expect(inspector.getByTestId("inspector-title")).toHaveText("customers");
  await expect(inspector.getByTestId("table-inspector-path")).toContainText("customers.json");
  await expect(inspector.getByTestId("table-inspector-table")).toBeVisible();

  // The grid shows the columns as columns: no Attribute column, a physical cell says what it is.
  await expect(grid(page).locator('[data-column="attribute"]')).toHaveCount(0);
  await expect(cell(page, "name", "length")).toHaveAttribute("title", "Physical type of the column.");
  await expect(cell(page, "name", "length")).toHaveText("120");

  // A column picked in the grid is the inspector's only subject, under a breadcrumb back to the table.
  await cell(page, "name", "length").click();
  await expect(inspector.getByTestId("inspector-breadcrumb")).toHaveText("billing.customers›name");
  await expect(inspector.getByTestId("table-inspector-column")).toContainText("Column name");
  await expect(inspector.getByTestId("table-inspector-table")).toHaveCount(0);
  const length = inspector.getByLabel("Length");
  await length.fill("2056");
  await length.press("Enter");
  await expect(cell(page, "name", "length")).toHaveText("2056");
  await expect(cell(page, "name", "nativeType")).toHaveText("varchar(2056)");
  await expect.poll(async () => (await fileOf(page, "customers"))?.columns?.find((c) => c.name === "name")?.length).toBe(2056);
  // The model's own rule for the value keeps its own length: the table's column is storage.
  const attribute = ((await documentOf(page, CUSTOMER)).attributes as { id: string; length?: number }[]).find((a) => a.id === NAME);
  expect(attribute?.length).toBe(120);

  // The grid's Type takes text: the same file.
  await cell(page, "name", "type").click();
  await page.keyboard.press("Enter");
  await grid(page).getByRole("combobox", { name: "Type of name" }).selectOption("text");
  await expect(cell(page, "name", "type")).toHaveText("text");
  await expect.poll(async () => (await fileOf(page, "customers"))?.columns?.find((c) => c.name === "name")?.type).toBe("text");
  await expect(page.getByTestId("database-stale")).toHaveCount(0);

  const scan = await new AxeBuilder({ page }).include('[data-testid="table-inspector"]').analyze();
  expect(scan.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);

  // Back to the table: the table's fields again, and its JSON tab is the whole file.
  const back = inspector.getByTestId("inspector-back-to-table");
  await expect(back).toHaveAttribute("title", "Back to the table");
  await back.click();
  await expect(inspector.getByTestId("inspector-breadcrumb")).toHaveCount(0);
  await expect(inspector.getByTestId("table-inspector-table")).toBeVisible();
  await inspector.getByRole("tab", { name: "JSON" }).click();
  await expect(inspector).toContainText('"name": "email"');
});

test("a click and an open on a table row in the explorer show the same table, and an open shows its editor", async ({ page }) => {
  await openEditor(page);
  await workspace(page, "Databases");
  const tree = page.getByRole("complementary", { name: "Explorer" }).getByTestId("explorer-tree");
  const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();
  await chevron(tree.getByTestId("explorer-folder-main")).click();
  await chevron(tree.getByTestId("explorer-folder-billing")).click();
  await chevron(tree.getByTestId("explorer-folder-Tables").first()).click();
  // invoices has an adjusting file, customers has none: both show as the table, and both have a table editor.
  for (const name of ["invoices", "customers"]) {
    const row = tree.locator(`[data-testid$="-${name}"]`).first();
    await row.click();
    await expect(page.getByTestId("table-inspector").getByTestId("inspector-title")).toHaveText(name);
    await expect(row).toHaveAttribute("aria-selected", "true");
    // Enter opens the row, as a double click and the menu's Open do: the table editor.
    await row.press("Enter");
    const editor = page.getByTestId("element-editor");
    await expect(editor).toHaveAttribute("data-kind", "table");
    await expect(editor.getByTestId("editor-title")).toHaveText(name);
    await expect(page.getByTestId("table-inspector").getByTestId("inspector-title")).toHaveText(name);
  }
  // The header's Open in the Database screen (a tooltip names it) shows the table on the Database screen.
  await expect(page.getByTestId("table-inspector-open")).toHaveAttribute("title", "Open in the Database screen");
  await page.getByTestId("table-inspector-open").click();
  await page.getByTestId("editor-tab-screen").click();
  await expect(page.getByTestId("database-table-customers")).toHaveAttribute("aria-current", "true");
});

test("a foreign key column follows its key, stays editable, and a different type pinned on it is MQ4005 until undone", async ({ page }) => {
  await openTable(page, "invoices");
  await expect(cell(page, "customer_id", "type")).toHaveAttribute(
    "title",
    "Follows the referenced column; set it here and a different type is reported (MQ4005).",
  );
  await cell(page, "customer_id", "name").click();
  await expect(page.getByTestId("table-inspector-references")).toHaveText(/Follows the referenced column customers\.id \(uuid\)/);

  await cell(page, "customer_id", "type").click();
  await page.keyboard.press("Enter");
  await grid(page).getByRole("combobox", { name: "Type of customer_id" }).selectOption("string");
  const stale = page.getByTestId("database-stale");
  await expect(stale).toContainText("MQ4005 Foreign key column 'invoices.customer_id' is string");
  await expect(stale).toContainText("but references 'customers.id', which is uuid (uuid)");
  // The screen keeps the tables as last resolved, so the grid and the inspector stay up to fix it.
  await expect(grid(page)).toBeVisible();
  await expect(page.getByTestId("table-inspector-problems")).toContainText("MQ4005");

  // Validation carries the resolver's finding (not only generation): the Problems panel lists it with its pointer, and its
  // row goes to the table, the inspector showing the pinned column.
  await page.getByRole("tab", { name: /Problems/ }).click();
  const problem = page.getByTestId("problem-MQ4005");
  await expect(problem).toContainText("Foreign key column 'invoices.customer_id' is string");
  await expect(problem).toContainText("/columns/");
  await page.getByTestId("database-table-customers").click();
  await expect(page.getByTestId("table-inspector").getByTestId("inspector-title")).toHaveText("customers");
  await problem.click();
  await expect(page.getByTestId("table-inspector").getByTestId("inspector-breadcrumb")).toContainText("invoices");
  await expect(page.getByTestId("database-table-invoices")).toHaveAttribute("aria-current", "true");
  await expect(page.getByTestId("table-inspector-column")).toContainText("Column customer_id");

  await page.getByRole("button", { name: "Undo" }).click();
  await expect(stale).toHaveCount(0);
  await expect(page.getByTestId("problem-MQ4005")).toHaveCount(0);
});
