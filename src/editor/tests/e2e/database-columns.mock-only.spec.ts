// The Database screen's column editor and the comments convention (the owner: "the ability to set a description for each table
// column, which may or may not be generated into DB metadata"): a description typed in the grid lands in the table's file (a
// synthesized table without one gets an overlay holding just that column), becomes the column's comment, and each edit is one
// undo step; Settings > Conventions turns the description fallback off.
import AxeBuilder from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { expect, test, workspace } from "./fixtures";

const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const NAME = "01J92P0V0MS09YFZHX07JQ3KMN";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";

type TableDoc = { id: string; origin?: string; entity?: string; columns?: { id: string; attribute?: string; [k: string]: unknown }[] };

/** The table files on an entity, read through the API. */
const overlaysOf = (page: Page, entity: string) =>
  page.evaluate(async (entityId) => {
    const index = (await (await fetch("/api/model/index")).json()) as
      { elements?: { id: string; kind: string; entity?: string }[] } | { id: string; kind: string; entity?: string }[];
    const rows = (Array.isArray(index) ? index : (index.elements ?? [])).filter((r) => r.kind === "table" && r.entity === entityId);
    // The batch read lists a row deleted meanwhile (an undo while polling) as missing instead of answering 404.
    const read = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: rows.map((r) => r.id) }),
    });
    return ((await read.json()) as { elements: { json: TableDoc }[] }).elements.map((d) => d.json);
  }, entity);

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

test("a description typed in the grid creates the table's overlay with that column only, becomes its comment, and undoes in one step", async ({ page }) => {
  await openTable(page, "customers");
  expect(await overlaysOf(page, CUSTOMER)).toEqual([]);
  // The entity's description is the table's comment (the comments convention, on by default).
  await expect(page.getByTestId("ddl-preview")).toContainText("IS 'Someone we bill.'");

  await cell(page, "name", "description").dblclick();
  await grid(page).getByRole("textbox", { name: "Description of name" }).fill("The legal name, as printed.");
  await page.keyboard.press("Enter");
  await expect(cell(page, "name", "description")).toHaveText("The legal name, as printed.");
  await expect(cell(page, "name", "comment")).toHaveText("The legal name, as printed.");
  await expect(page.getByTestId("ddl-preview")).toContainText("IS 'The legal name, as printed.'");
  const [overlay] = await overlaysOf(page, CUSTOMER);
  expect(overlay).toMatchObject({ origin: "synthesized", entity: CUSTOMER });
  expect(overlay.columns).toEqual([{ id: expect.any(String), attribute: NAME, description: "The legal name, as printed." }]);

  // A second edit goes to the same overlay; an explicit comment wins over the description.
  await cell(page, "email", "comment").click();
  await page.keyboard.press("Enter");
  await grid(page).getByRole("textbox", { name: "Comment of email" }).fill("Where invoices go.");
  await page.keyboard.press("Enter");
  await expect(cell(page, "email", "comment")).toHaveText("Where invoices go.");
  await expect.poll(async () => (await overlaysOf(page, CUSTOMER)).map((t) => t.columns?.length)).toEqual([2]);

  // The grid passes an accessibility scan.
  const scan = await new AxeBuilder({ page }).include('[data-testid="column-panel"]').analyze();
  expect(scan.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);

  // Each edit is one undo step: the comment goes, then the overlay the first edit created.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(cell(page, "email", "comment")).toHaveText("");
  await expect.poll(async () => (await overlaysOf(page, CUSTOMER)).map((t) => t.columns?.length)).toEqual([1]);
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(() => overlaysOf(page, CUSTOMER)).toEqual([]);
  await expect(cell(page, "name", "description")).toHaveText("");
  await expect(cell(page, "name", "comment")).toHaveText("");
});

test("edits made in quick succession on a table without an overlay create one overlay, not two", async ({ page }) => {
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
  const overlays = await overlaysOf(page, CUSTOMER);
  expect(overlays).toHaveLength(1);
  expect(overlays[0].columns).toEqual([
    { id: expect.any(String), attribute: expect.any(String), comment: "Where invoices go.", description: "The address we send invoices to." },
  ]);
});

test("an existing overlay takes the edit: Space toggles Null, a rename shows at once, and the panel hides from its header", async ({ page }) => {
  await openTable(page, "invoices");
  const before = (await overlaysOf(page, INVOICE))[0];
  expect(before.columns).toHaveLength(1);

  const row = grid(page).getByTestId("column-row-issued_on");
  await row.locator('[data-column="nullable"]').click();
  const wasNullable = (await row.locator('[data-column="nullable"] span').getAttribute("aria-label")) === "nullable";
  await page.keyboard.press("Space");
  await expect(row.locator('[data-column="nullable"] span')).toHaveAttribute("aria-label", wasNullable ? "not null" : "nullable");
  await expect.poll(async () => (await overlaysOf(page, INVOICE))[0].columns?.length).toBe(2);
  expect((await overlaysOf(page, INVOICE))[0].columns?.find((c) => c.nullable !== undefined)?.nullable).toBe(!wasNullable);

  // Rename by typing over the cell; Escape would have cancelled.
  await row.locator('[data-column="name"]').click();
  await page.keyboard.press("F2");
  await grid(page).getByRole("textbox", { name: "Name of issued_on" }).fill("issue_date");
  await page.keyboard.press("Enter");
  await expect(grid(page).getByTestId("column-row-issue_date")).toBeVisible();
  await expect(grid(page).getByTestId("column-row-issued_on")).toHaveCount(0);

  const hide = page.getByTestId("toggle-columns");
  await expect(hide).toHaveAttribute("title", "Hide columns");
  await hide.click();
  await expect(grid(page)).toBeHidden();
  await expect(page.getByTestId("toggle-columns")).toHaveAttribute("title", "Show columns");
});

test("Settings > Conventions sets comments to none: descriptions stop filling comments", async ({ page }) => {
  await page.goto("/settings/conventions");
  const control = page.getByTestId("conv-comments");
  await expect(control.locator("option:checked")).toHaveText("inherit: descriptions (engine default)");
  await control.selectOption("none");
  await page.getByTestId("save-conventions").click();
  await expect(page.getByText("Conventions saved.")).toBeVisible();

  await openTable(page, "customers", false);
  await expect(page.getByTestId("ddl-preview")).toContainText("CREATE TABLE");
  await expect(page.getByTestId("ddl-preview")).not.toContainText("Someone we bill.");
});
