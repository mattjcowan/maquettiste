// The database designer's editing paths beyond one table's own fields: deleting a column shows what else goes (the keys and
// indexes that hold it, how many storage bindings follow, never whose) and changes it all in one undo step; renaming a column rewrites the table's
// SQL that names it; a column's values (identity, a sequence, a computed expression, a default per dialect) edit in the
// inspector with what does not fit said inline; and the explorer's table rows rename the table.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");
const inspector = (page: Page) => page.getByTestId("table-inspector");
const undo = (page: Page) => page.getByRole("button", { name: "Undo" }).click();

type Doc = { id: string; name?: string; kind?: string; [k: string]: unknown };

/** A document by id, as the server has it now. */
const docOf = (page: Page, id: string) =>
  page.evaluate(async (elementId) => ((await (await fetch(`/api/model/elements/${elementId}`)).json()) as { json: Doc }).json, id);

const NOTES = "01K6BND0000000000000000001";
const BODY = "01K6BND0000000000000000005";
const INVOICE_NOTE = "01K6BND0000000000000000010";

async function openNotes(page: Page) {
  await page.goto("/database");
  await page.getByTestId("database-table-notes").click();
  await expect(inspector(page).getByTestId("inspector-title")).toHaveText("notes");
  await inspector(page).getByTestId("table-inspector-edit").click();
  await expect(editor(page)).toHaveAttribute("data-kind", "table");
}

const fieldsReading = async (page: Page, column: string) =>
  ((await docOf(page, INVOICE_NOTE)).bindings as { fields: { column: string }[] }[])[0].fields.filter((f) => f.column === column).length;

test("deleting a column shows what else goes, changes it all in one step, and undo puts it all back", async ({ page }) => {
  await openNotes(page);
  await editor(page).getByRole("tab", { name: "Columns", exact: true }).click();
  await editor(page).getByTestId("column-grid").getByText("body", { exact: true }).click();
  await page.getByTestId("table-delete-column").click();
  // The bindings that read notes.body lose that field: the plan says so before anything is written.
  const plan = page.getByTestId("part-delete-plan");
  await expect(page.getByRole("dialog", { name: "Delete column body of notes?" })).toBeVisible();
  // Counted, never named: the database side knows no entities; Files lists the bindings' files and what each loses.
  await expect(plan.getByTestId("part-delete-storage")).toContainText("2 storage bindings follow.");
  await expect(plan).not.toContainText("InvoiceNote");
  await plan.getByText("Files").click();
  await expect(plan.getByTestId("part-delete-storage-files")).toContainText("a field no longer reads body");
  await expect(plan.getByTestId("part-delete-storage-files")).toContainText(".json");
  expect(await fieldsReading(page, BODY)).toBe(1);
  await page.getByTestId("part-delete-confirm").click();
  await expect(page.getByTestId("notice")).toContainText("Deleted column body of notes and 2 more things with it.");
  await expect.poll(async () => ((await docOf(page, NOTES)).columns as { name: string }[]).map((c) => c.name)).not.toContain("body");
  expect(await fieldsReading(page, BODY)).toBe(0);

  await undo(page);
  await expect.poll(async () => ((await docOf(page, NOTES)).columns as { name: string }[]).map((c) => c.name)).toContain("body");
  expect(await fieldsReading(page, BODY)).toBe(1);
});

test("renaming a column rewrites the table's check that reads it, in one undo step", async ({ page }) => {
  await openNotes(page);
  await editor(page).getByRole("tab", { name: "Checks", exact: true }).click();
  await page.getByTestId("table-add-check").click();
  const check = page.getByTestId("part-row-check-ck_notes_id");
  await check.getByLabel("Expression of ck_notes_id for any dialect").fill("length(body) > 0");
  await check.getByLabel("Expression of ck_notes_id for any dialect").press("Enter");
  await expect
    .poll(async () => ((await docOf(page, NOTES)).checks as { expression: Record<string, string> }[] | undefined)?.[0]?.expression["*"])
    .toBe("length(body) > 0");

  await editor(page).getByRole("tab", { name: "Columns", exact: true }).click();
  await editor(page).getByTestId("column-grid").getByText("body", { exact: true }).click();
  const name = inspector(page).getByLabel("Name", { exact: true });
  await name.fill("content");
  await name.press("Enter");
  await expect(page.getByTestId("notice")).toContainText("Renamed column body to content. Also rewritten: check ck_notes_id.");
  await expect.poll(async () => ((await docOf(page, NOTES)).checks as { expression: Record<string, string> }[])[0].expression["*"]).toBe("length(content) > 0");

  await undo(page);
  await expect.poll(async () => ((await docOf(page, NOTES)).checks as { expression: Record<string, string> }[])[0].expression["*"]).toBe("length(body) > 0");
  await expect.poll(async () => ((await docOf(page, NOTES)).columns as { name: string }[]).map((c) => c.name)).toContain("body");
});

test("a column's values edit in the inspector: identity, a sequence, a default per dialect, a collation", async ({ page }) => {
  await page.goto("/database");
  await page.getByTestId("database-table-notes").click();
  await page.getByTestId("column-grid").getByText("entity_type", { exact: true }).click();
  const facets = inspector(page).getByTestId("column-facets");
  await expect(facets).toBeVisible();
  const entry = async () => ((await docOf(page, NOTES)).columns as Doc[]).find((c) => c.name === "entity_type")!;

  // Identity on a string column is said inline; a sequence asks for one.
  await facets.getByTestId("column-generated").selectOption("identity");
  await expect.poll(async () => (await entry()).generated).toBe("identity");
  await expect(facets.getByTestId("column-value-problem")).toHaveText("An identity column takes an integer type; this one is string.");
  await facets.getByTestId("column-generated").selectOption("sequence");
  await expect(facets.getByTestId("column-sequence")).toBeVisible();
  await expect(facets.getByTestId("column-value-problem")).toHaveText("Pick the sequence that supplies the values.");
  await facets.getByTestId("column-generated").selectOption("");
  await expect.poll(async () => (await entry()).generated).toBeUndefined();

  // A default per dialect, and a collation.
  const pg = facets.getByLabel("Default SQL for PostgreSQL");
  await pg.fill("'invoice'");
  await pg.press("Enter");
  await expect.poll(async () => (await entry()).defaultSql).toEqual({ postgresql: "'invoice'" });
  const collation = facets.getByLabel("Collation");
  await collation.fill("C");
  await collation.press("Enter");
  await expect.poll(async () => (await entry()).collation).toBe("C");
  await undo(page);
  await expect.poll(async () => (await entry()).collation).toBeUndefined();
});

test("a table row renames the table, and a table file's row offers Used, Favorite and Delete", async ({ page }) => {
  await page.goto("/");
  await workspace(page, "Databases");
  const tree = page.getByRole("complementary", { name: "Explorer" }).getByTestId("explorer-tree");
  for (const folder of ["main", "billing", "Tables"]) {
    const row = tree.getByTestId(`explorer-folder-${folder}`).first();
    if ((await row.getAttribute("aria-expanded")) !== "true") await row.locator("span[aria-hidden]").first().click();
    await expect(row).toHaveAttribute("aria-expanded", "true");
  }
  const notes = tree.locator('[role="treeitem"][data-testid$="-notes"]').first();
  await notes.click({ button: "right" });
  const menu = page.getByTestId("row-menu");
  for (const action of ["Used", "Rename", "Add to favorites", "Delete"]) await expect(menu.getByRole("menuitem", { name: action })).toBeVisible();
  await menu.getByRole("menuitem", { name: "Rename" }).click();
  const input = tree.getByRole("textbox");
  await input.fill("remarks");
  await input.press("Enter");
  await expect.poll(async () => (await docOf(page, NOTES)).name).toBe("remarks");
  await undo(page);
  await expect.poll(async () => (await docOf(page, NOTES)).name).toBe("notes");
});

test("the DDL fields edit in the UI and show in the JSON tab: identity options, index expressions and prefix lengths, nulls not distinct", async ({ page }) => {
  await openNotes(page);
  const json = () => editor(page).getByTestId("code-text-json");
  const columnsOf = async () => (await docOf(page, NOTES)).columns as Doc[];

  // Identity: seed, increment (0 is said inline and not saved) and generated always, under Generated by.
  await editor(page).getByRole("tab", { name: "Columns", exact: true }).click();
  await editor(page).getByTestId("column-grid").getByText("entity_id", { exact: true }).click();
  // The picked column's settings are edited in the inspector (the grid's own panel is gone: one place per subject).
  const facets = page.getByTestId("table-inspector-column");
  await facets.getByTestId("column-generated").selectOption("identity");
  await expect.poll(async () => (await columnsOf()).find((c) => c.name === "entity_id")?.generated).toBe("identity");
  await facets.getByLabel("Identity seed").fill("100");
  await facets.getByLabel("Identity seed").press("Enter");
  await expect.poll(async () => (await columnsOf()).find((c) => c.name === "entity_id")?.identity).toEqual({ seed: 100 });
  await facets.getByLabel("Identity increment").fill("0");
  await facets.getByLabel("Identity increment").press("Enter");
  await expect(facets.getByTestId("column-value-problem").filter({ hasText: "The identity increment cannot be 0." })).toBeVisible();
  await facets.getByLabel("Identity increment").fill("5");
  await facets.getByLabel("Identity increment").press("Enter");
  await facets.getByLabel("Generated always (an insert cannot give a value)").click();
  await expect.poll(async () => (await columnsOf()).find((c) => c.name === "entity_id")?.identity).toEqual({ seed: 100, increment: 5, always: true });
  await undo(page);
  await expect.poll(async () => (await columnsOf()).find((c) => c.name === "entity_id")?.identity).toEqual({ seed: 100, increment: 5 });
  await facets.getByLabel("Generated always (an insert cannot give a value)").click();

  // Unique constraints: nulls not distinct.
  await editor(page).getByRole("tab", { name: "Unique constraints", exact: true }).click();
  await page.getByTestId("table-add-unique").click();
  await page.getByTestId("part-row-unique-uq_notes_id").getByLabel("Nulls not distinct in uq_notes_id").click();
  await expect.poll(async () => ((await docOf(page, NOTES)).uniques as Doc[])[0].nullsNotDistinct).toBe(true);
  await expect(page.getByTestId("table-inspector-part")).toContainText("Nulls not distinct");

  // Indexes: a new index takes an expression (per dialect, like a check) and a prefix length per column; a bad length is said.
  await editor(page).getByRole("tab", { name: "Indexes", exact: true }).click();
  await page.getByTestId("table-add-index").click();
  const row = page.getByTestId("part-row-index-ix_notes_id");
  await expect(row).toHaveAttribute("aria-selected", "true");
  await row.getByRole("button", { name: "Add an expression to ix_notes_id" }).click();
  const details = page.getByTestId("keys-indexes").getByTestId("index-column-details");
  const anyDialect = details.getByLabel("Expression 2 of ix_notes_id for any dialect");
  await anyDialect.fill("lower(body)");
  await anyDialect.press("Enter");
  await details.getByLabel("Expression 2 of ix_notes_id for postgresql").fill("");
  await details.getByLabel("Expression 2 of ix_notes_id for postgresql").press("Enter");
  const indexOf = async () => ((await docOf(page, NOTES)).indexes as Doc[]).find((i) => i.name === "ix_notes_id");
  await expect.poll(async () => (await indexOf())?.columns).toEqual([{ column: "01K6BND0000000000000000002" }, { expression: { "*": "lower(body)" } }]);
  await expect(row.getByTestId("index-column-(lower(body))")).toBeVisible();
  const length = details.getByLabel("Prefix length of id in ix_notes_id");
  await length.fill("0");
  await length.press("Enter");
  await expect(details.getByTestId("index-length-problem")).toHaveText("A prefix length is a whole number from 1, or empty.");
  await length.fill("8");
  await length.press("Enter");
  await expect.poll(async () => ((await indexOf())?.columns as Doc[])[0]).toEqual({ column: "01K6BND0000000000000000002", length: 8 });
  // PostgreSQL takes no prefix length: said under the columns, and as MQ4056 on the row.
  await expect(details.getByTestId("index-column-note")).toContainText("Only MySQL");

  // The JSON tab shows each of them.
  await editor(page).getByRole("tab", { name: "JSON", exact: true }).click();
  for (const text of ['"seed": 100', '"increment": 5', '"always": true', '"nullsNotDistinct": true', '"*": "lower(body)"', '"length": 8'])
    await expect(json()).toContainText(text);
});

test("a view's DDL options, its dependencies and the views its body names edit in the view editor and show in its JSON", async ({ page }) => {
  await openEditor(page);
  // A second view reading the first, so the body names a view.
  const created = await page.evaluate(async (database) => {
    const r = await fetch("/api/model/elements", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        kind: "view",
        id: "01K6BND00000000000000000V1",
        name: "late_invoices",
        database,
        body: { "*": "select id from billing.outstanding_invoices where issued_on < current_date - 30" },
        columns: [{ name: "id", type: "uuid" }],
      }),
    });
    return r.status;
  }, "01J92P0V1QRN2181XM2ZWE02W4");
  expect(created).toBe(201);
  await workspace(page, "Databases");
  const tree = page.getByRole("complementary", { name: "Explorer" }).getByTestId("explorer-tree");
  for (const folder of ["main", "billing", "Views"]) {
    const row = tree.getByTestId(`explorer-folder-${folder}`).first();
    if ((await row.getAttribute("aria-expanded")) !== "true") await row.locator("span[aria-hidden]").first().click();
    await expect(row).toHaveAttribute("aria-expanded", "true");
  }
  await tree.getByTestId("explorer-row-late_invoices").dblclick();
  await expect(editor(page)).toHaveAttribute("data-kind", "view");
  const options = editor(page).getByTestId("view-ddl-options");
  await expect(options.getByTestId("view-inferred-outstanding_invoices")).toBeVisible();

  const VIEW = "01K6BND00000000000000000V1";
  await options.getByLabel("Column list (CREATE VIEW names the Columns tab's columns)").click();
  await options.getByLabel("With check option (an insert or update through the view must satisfy its WHERE)").click();
  await expect.poll(async () => (await docOf(page, VIEW)).withCheckOption).toBe(true);
  await options.getByLabel("Materialized (the view stores its rows: PostgreSQL and Oracle)").click();
  await expect.poll(async () => (await docOf(page, VIEW)).materialized).toBe(true);
  // A materialized view is not written through: the note and MQ4056 say so.
  await expect(options.getByTestId("view-ddl-note")).toContainText("not written through");
  await expect(options.getByTestId("view-ddl-problems")).toContainText("MQ4056");
  await options.getByLabel("Depends on").selectOption({ label: "notes" });
  await expect.poll(async () => (await docOf(page, VIEW)).dependsOn).toEqual(["01K6BND0000000000000000001"]);
  await expect(options.getByTestId("depends-on-notes")).toBeVisible();
  // One undo step each: the dependency goes first.
  await undo(page);
  await expect.poll(async () => (await docOf(page, VIEW)).dependsOn).toBeUndefined();
  await options.getByLabel("Depends on").selectOption({ label: "notes" });
  await expect.poll(async () => (await docOf(page, VIEW)).dependsOn).toEqual(["01K6BND0000000000000000001"]);

  // The view's JSON (the inspector's JSON tab) shows each of them.
  const side = page.getByTestId("inspector");
  await side.getByRole("tab", { name: "JSON" }).click();
  for (const text of ['"columnList": true', '"withCheckOption": true', '"materialized": true', '"dependsOn"', '"01K6BND0000000000000000001"'])
    await expect(side.getByTestId("code-text-json")).toContainText(text);
});
