// The database side as a database manager (the owner: "completely remove the entity from the database portion and treat the
// database as its own thing"; "how are constraints, indexes and foreign keys managed? ... I'm expecting a full database
// manager"): the tables the model still lays out are stored as table files in one step; every table has the full table editor
// (General, Columns, Primary key, Unique constraints, Indexes, Foreign keys, Checks, DDL, References, JSON), each gesture one
// undo step; the explorer's table folders open the editor on their tab with the part picked, and offer New and Delete; the
// inspector shows one part at a time with the way back to the table; favorites and recents clear; and nothing on the database
// side names an entity.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");
const inspector = (page: Page) => page.getByTestId("table-inspector");
const undo = (page: Page) => page.getByRole("button", { name: "Undo" }).click();
const chevron = (row: Locator) => row.locator("span[aria-hidden]").first();
const tree = (page: Page) => page.getByRole("complementary", { name: "Explorer" }).getByTestId("explorer-tree");

type TableDoc = { id: string; name?: string; origin?: string; columns?: { name?: string }[]; [k: string]: unknown };

/** The table file named `name` (stored as a file), or undefined. */
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

/** Expands Databases › main › billing › Tables › `table` in the explorer. */
async function expandTable(page: Page, table: string): Promise<Locator> {
  await workspace(page, "Databases");
  for (const folder of ["main", "billing", "Tables"]) {
    const row = tree(page).getByTestId(`explorer-folder-${folder}`).first();
    if ((await row.getAttribute("aria-expanded")) !== "true") await chevron(row).click();
    await expect(row).toHaveAttribute("aria-expanded", "true");
  }
  const row = tree(page).locator(`[role="treeitem"][data-testid$="-${table}"]`).first();
  if ((await row.getAttribute("aria-expanded")) !== "true") await chevron(row).click();
  await expect(row).toHaveAttribute("aria-expanded", "true");
  return row;
}

/** Opens a table's editor from the Database screen (the inspector's Open the table editor). */
async function openTableEditor(page: Page, name: string) {
  await page.goto("/database");
  await page.getByTestId(`database-table-${name}`).click();
  await expect(inspector(page).getByTestId("inspector-title")).toHaveText(name);
  await inspector(page).getByTestId("table-inspector-edit").click();
  await expect(editor(page)).toHaveAttribute("data-kind", "table");
  await expect(editor(page).getByTestId("editor-title")).toHaveText(name);
}

test("Store as table files stores every laid-out table in one step, each file with every column, and undo lays them out again", async ({ page }) => {
  await page.goto("/database");
  const banner = page.getByTestId("store-tables-banner");
  await expect(banner).toHaveText(/5 tables are not stored as table files yet\. Store them as files to edit every part of them\./);
  await page.getByTestId("database-table-customers").click();
  const columns = await page.getByTestId("column-grid").locator("tbody tr").allTextContents();
  expect(await fileOf(page, "customers")).toBeUndefined();

  await page.getByTestId("store-tables").click();
  await expect(page.getByText("5 tables are now stored as table files.")).toBeVisible();
  await expect(banner).toHaveCount(0);
  const file = await fileOf(page, "customers");
  expect(file?.columns).toHaveLength(columns.length);
  for (const name of ["invoices", "invoice_lines", "payments", "products"]) expect(await fileOf(page, name)).toBeDefined();

  // The table's JSON tab is the whole file now.
  await page.getByTestId("database-table-customers").click();
  await inspector(page).getByRole("tab", { name: "JSON" }).click();
  for (const name of ["id", "name", "email", "customer_since"]) await expect(inspector(page)).toContainText(`"name": "${name}"`);
  await expect(inspector(page)).not.toContainText("synthesized");

  // One undo step puts every table back as it was.
  await undo(page);
  await expect(banner).toBeVisible();
  await expect.poll(() => fileOf(page, "customers")).toBeUndefined();
});

test("each part tab adds, edits and deletes its parts, each one undo step, and a picked part is the inspector's only subject", async ({ page }) => {
  await openTableEditor(page, "notes");
  const tab = (name: string) => editor(page).getByRole("tab", { name, exact: true });
  for (const name of ["General", "Columns", "Primary key", "Unique constraints", "Indexes", "Foreign keys", "Checks", "DDL", "References", "JSON"])
    await expect(tab(name)).toBeVisible();

  // Unique constraints: add (picked at once, alone in the inspector), rename, delete; undo takes each back.
  await tab("Unique constraints").click();
  await page.getByTestId("table-add-unique").click();
  const unique = page.getByTestId("part-row-unique-uq_notes_id");
  await expect(unique).toHaveAttribute("aria-selected", "true");
  await expect(inspector(page).getByTestId("inspector-breadcrumb")).toHaveText("billing.notes›uq_notes_id");
  await expect(inspector(page).getByTestId("table-inspector-part")).toHaveAttribute("data-part", "unique");
  await expect(inspector(page).getByTestId("table-inspector-table")).toHaveCount(0);
  const name = unique.getByRole("textbox", { name: "Name of unique constraint uq_notes_id" });
  await name.fill("uq_notes_key");
  await name.press("Enter");
  await expect(page.getByTestId("part-row-unique-uq_notes_key")).toBeVisible();
  await expect.poll(async () => ((await fileOf(page, "notes"))?.uniques as { name?: string }[] | undefined)?.map((u) => u.name)).toEqual(["uq_notes_key"]);
  await page.getByRole("button", { name: "Delete unique constraint uq_notes_key" }).click();
  await expect(page.getByTestId("keys-uniques")).toHaveCount(0);
  await undo(page);
  await expect(page.getByTestId("part-row-unique-uq_notes_key")).toBeVisible();
  await undo(page);
  await expect(page.getByTestId("part-row-unique-uq_notes_id")).toBeVisible();
  await undo(page);
  await expect(page.getByTestId("keys-uniques")).toHaveCount(0);

  // Indexes: the existing one keeps its convention name; a new one sorts a column descending; delete and undo.
  await tab("Indexes").click();
  await expect(page.getByTestId("keys-indexes").locator("tbody tr")).toHaveCount(1);
  await page.getByTestId("table-add-index").click();
  const index = page.getByTestId("part-row-index-ix_notes_id");
  await expect(index).toHaveAttribute("aria-selected", "true");
  await index.getByRole("button", { name: "Ascending: sort id descending" }).click();
  await expect(index.getByRole("button", { name: "Descending: sort id ascending" })).toBeVisible();
  const indexOf = async () =>
    ((await fileOf(page, "notes"))?.indexes as { name?: string; columns: { descending?: boolean }[] }[]).find((i) => i.name === "ix_notes_id");
  await expect.poll(async () => (await indexOf())?.columns[0].descending).toBe(true);
  await index.getByLabel("Filter of index ix_notes_id").fill("body IS NOT NULL");
  await index.getByLabel("Filter of index ix_notes_id").press("Enter");
  await expect.poll(async () => ((await indexOf()) as { where?: string } | undefined)?.where).toBe("body IS NOT NULL");
  await page.getByRole("button", { name: "Delete index ix_notes_id" }).click();
  await expect(index).toHaveCount(0);
  await undo(page);
  await expect(index).toBeVisible();

  // Foreign keys: Add opens the foreign key dialog (nothing chosen yet); the key references another table's primary key;
  // On delete; delete and undo.
  await tab("Foreign keys").click();
  await page.getByTestId("table-add-foreign-key").click();
  const dialog = page.getByTestId("fk-dialog");
  await expect(page.getByRole("dialog", { name: "New foreign key on notes" })).toBeVisible();
  await expect(dialog.getByLabel("References table")).toHaveValue("");
  await expect(page.getByTestId("fk-save")).toBeDisabled();
  await dialog.getByLabel("References table").selectOption({ label: "billing.invoices" });
  await dialog.getByLabel("Column 1", { exact: true }).selectOption({ label: "entity_id (uuid)" });
  await expect(dialog.getByLabel("Name")).toHaveValue("fk_notes_entity_id");
  await page.getByTestId("fk-save").click();
  await expect(dialog).toHaveCount(0);
  const fk = page.getByTestId("part-row-foreign-key-fk_notes_entity_id");
  await expect(fk).toHaveAttribute("aria-selected", "true");
  await fk.getByLabel("On delete of fk_notes_entity_id").selectOption("cascade");
  await expect.poll(async () => ((await fileOf(page, "notes"))?.foreignKeys as { onDelete?: string }[] | undefined)?.[0]?.onDelete).toBe("cascade");
  await page.getByRole("button", { name: "Delete foreign key fk_notes_entity_id" }).click();
  await expect(fk).toHaveCount(0);
  await undo(page);
  await expect(fk).toBeVisible();

  // Checks: an expression for the dialect and one for any dialect; delete and undo.
  await tab("Checks").click();
  await page.getByTestId("table-add-check").click();
  const check = page.getByTestId("part-row-check-ck_notes_id");
  await expect(check.getByLabel("Expression of ck_notes_id for postgresql")).toHaveValue("id IS NOT NULL");
  await check.getByLabel("Expression of ck_notes_id for any dialect").fill("length(body) > 0");
  await check.getByLabel("Expression of ck_notes_id for any dialect").press("Enter");
  await expect
    .poll(async () => ((await fileOf(page, "notes"))?.checks as { expression?: Record<string, string> }[] | undefined)?.[0]?.expression)
    .toEqual({ postgresql: "id IS NOT NULL", "*": "length(body) > 0" });
  await page.getByRole("button", { name: "Delete check ck_notes_id" }).click();
  await expect(check).toHaveCount(0);
  await undo(page);
  await expect(check).toBeVisible();

  // Primary key: delete and undo.
  await tab("Primary key").click();
  await page.getByRole("button", { name: /^Delete primary key/ }).click();
  await expect(page.getByTestId("keys-primary")).toHaveCount(0);
  await undo(page);
  await expect(page.getByTestId("keys-primary")).toBeVisible();

  // The breadcrumb's table goes back to the table.
  await tab("Checks").click();
  await page.getByTestId("part-row-check-ck_notes_id").getByLabel("Name of check ck_notes_id").click();
  await expect(inspector(page).getByTestId("inspector-breadcrumb")).toHaveText("billing.notes›ck_notes_id");
  await inspector(page).getByTestId("inspector-back-to-table").click();
  await expect(inspector(page).getByTestId("table-inspector-table")).toBeVisible();

  // DDL: the table's create script; References: what points at it.
  await tab("DDL").click();
  await expect(page.getByTestId("table-ddl")).toContainText("CREATE TABLE");
  await tab("References").click();
  await expect(page.getByTestId("table-references")).toContainText("Referenced by");
});

test("the explorer's table folders open the editor on their tab with the part picked, and offer New and Delete", async ({ page }) => {
  await openEditor(page);
  await expandTable(page, "customers");
  for (const folder of ["Columns", "Primary key", "Unique constraints", "Indexes", "Foreign keys", "Checks", "Referenced by"])
    await expect(tree(page).getByTestId(`explorer-folder-${folder}`).first()).toBeVisible();

  // A click on a part shows it alone in the inspector; Enter opens the table editor on its tab with it picked.
  const uniques = tree(page).getByTestId("explorer-folder-Unique constraints").first();
  await chevron(uniques).click();
  const item = tree(page).getByTestId("explorer-item-uq_customers_email");
  await item.click();
  await expect(inspector(page).getByTestId("inspector-breadcrumb")).toHaveText("billing.customers›uq_customers_email");
  await item.press("Enter");
  await expect(editor(page)).toHaveAttribute("data-kind", "table");
  await expect(editor(page).getByRole("tab", { name: "Unique constraints" })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByTestId("part-row-unique-uq_customers_email")).toHaveAttribute("aria-selected", "true");

  // A folder's Open shows its tab.
  const indexes = tree(page).getByTestId("explorer-folder-Indexes").first();
  await indexes.click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Open", exact: true }).click();
  await expect(editor(page).getByRole("tab", { name: "Indexes" })).toHaveAttribute("aria-selected", "true");

  // The table's New index stores the laid-out table as a file and opens the new index picked.
  const table = tree(page).locator('[role="treeitem"][data-testid$="-customers"]').first();
  await table.click({ button: "right" });
  const menu = page.getByTestId("row-menu");
  for (const action of ["New column", "New unique constraint", "New index", "New foreign key", "New check", "Rename"])
    await expect(menu.getByRole("menuitem", { name: action })).toBeVisible();
  // New foreign key asks for what it references: the foreign key dialog, nothing written yet.
  await menu.getByRole("menuitem", { name: "New foreign key" }).click();
  await expect(page.getByRole("dialog", { name: "New foreign key on customers" })).toBeVisible();
  await expect(page.getByTestId("fk-dialog").getByLabel("References table")).toHaveValue("");
  await page.keyboard.press("Escape");
  expect(await fileOf(page, "customers")).toBeUndefined();
  await table.click({ button: "right" });
  await menu.getByRole("menuitem", { name: "New index" }).click();
  await expect(page.getByText("customers is now stored as a table file.")).toBeVisible();
  await expect(editor(page).getByRole("tab", { name: "Indexes" })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByTestId("part-row-index-ix_customers_id")).toHaveAttribute("aria-selected", "true");
  await expect
    .poll(async () => ((await fileOf(page, "customers"))?.indexes as { name?: string }[] | undefined)?.map((i) => i.name))
    .toContain("ix_customers_id");

  // Delete on a part's row: one change of the file, one undo step.
  const stored = await expandTable(page, "customers");
  await expect(stored).toBeVisible();
  const storedUniques = tree(page).getByTestId("explorer-folder-Unique constraints").first();
  if ((await storedUniques.getAttribute("aria-expanded")) !== "true") await chevron(storedUniques).click();
  await tree(page).getByTestId("explorer-item-uq_customers_email").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Delete" }).click();
  await expect.poll(async () => (await fileOf(page, "customers"))?.uniques).toBeUndefined();
  await undo(page);
  await expect.poll(async () => ((await fileOf(page, "customers"))?.uniques as unknown[] | undefined)?.length).toBe(1);
});

test("favorites and recents clear from their headers, and each row removes itself", async ({ page }) => {
  await openEditor(page);
  const side = page.getByRole("complementary", { name: "Explorer" });
  await chevron(side.getByTestId("explorer-domain-Billing")).click();
  await chevron(tree(page).getByTestId("explorer-folder-Entities").first()).click();
  for (const name of ["Customer", "Invoice"]) {
    const row = tree(page).getByTestId(`explorer-row-${name}`);
    await row.click({ button: "right" });
    await page.getByTestId("row-menu").getByRole("menuitem", { name: "Add to favorites" }).click();
    // Opening makes it a recent one.
    await row.dblclick();
    await expect(editor(page).getByTestId("editor-title")).toHaveText(name);
  }
  const strip = side.getByTestId("explorer-strip");
  await expect(strip.getByTestId("explorer-favorite-Customer")).toBeVisible();
  await expect(strip.getByTestId("explorer-recent-Invoice")).toBeVisible();

  // A row's Remove (a tooltip names it).
  const remove = strip.getByTestId("explorer-recent-remove-Invoice");
  await expect(remove).toHaveAttribute("title", "Remove Invoice from the recent list");
  await remove.click();
  await expect(strip.getByTestId("explorer-recent-Invoice")).toHaveCount(0);
  await expect(strip.getByTestId("explorer-recent-Customer")).toBeVisible();

  // Clear the recents: no question asked.
  await expect(strip.getByTestId("explorer-recent-clear")).toHaveAttribute("title", "Clear the recent elements listed here");
  await strip.getByTestId("explorer-recent-clear").click();
  await expect(strip.getByTestId("explorer-recent-Customer")).toHaveCount(0);

  // Clear the favorites: asked first; dismissing keeps them.
  page.once("dialog", (d) => void d.dismiss());
  await strip.getByTestId("explorer-favorites-clear").click();
  await expect(strip.getByTestId("explorer-favorite-Customer")).toBeVisible();
  page.once("dialog", (d) => void d.accept());
  await strip.getByTestId("explorer-favorites-clear").click();
  await expect(side.getByTestId("explorer-strip")).toHaveCount(0);
});

test("a recent row opens its element, in another domain too, as a double click on its tree row does", async ({ page }) => {
  await openEditor(page);
  const side = page.getByRole("complementary", { name: "Explorer" });
  for (const domain of ["Billing", "Catalog"]) await chevron(side.getByTestId(`explorer-domain-${domain}`)).click();
  for (const folder of await tree(page).getByTestId("explorer-folder-Entities").all()) await chevron(folder).click();
  for (const name of ["Product", "Customer"]) {
    await tree(page).getByTestId(`explorer-row-${name}`).dblclick();
    await expect(editor(page).getByTestId("editor-title")).toHaveText(name);
  }
  // Customer's editor is pinned in front: a click on Product in the strip shows Product's editor, not only selects it.
  const strip = side.getByTestId("explorer-strip");
  await strip.getByTestId("explorer-recent-Product").click();
  await expect(editor(page).getByTestId("editor-title")).toHaveText("Product");
  await strip.getByTestId("explorer-recent-Customer").click();
  await expect(editor(page).getByTestId("editor-title")).toHaveText("Customer");
});

test("nothing on the database side names an entity, a projection or an overlay", async ({ page }) => {
  await page.goto("/database");
  await page.getByTestId("database-table-invoices").click();
  await page.getByTestId("column-grid").getByTestId("column-row-customer_id").locator('[data-column="name"]').click();
  await expect(inspector(page).getByTestId("table-inspector-column")).toBeVisible();
  await expandTable(page, "invoices");
  await chevron(tree(page).getByTestId("explorer-folder-Columns").first()).click();
  // The words, in any case; the entity names as the model spells them (a table's own comment may say "invoice").
  const words = /\b(entity|entities|projected|projection|overlay|mapped|mappings?|derived from)\b/i;
  const names = /\b(Customer|Invoice|InvoiceLine|Payment|Product|CustomerNote|InvoiceNote|RevenueMonth)\b(?! register)/;
  const named = (text: string) => (text.match(words) ?? text.match(names))?.[0] ?? null;
  // The DDL preview is the generated script (its comments are the model's descriptions), not the editor's words.
  const text = async (locator: Locator) =>
    locator.evaluate((el) => {
      const copy = el.cloneNode(true) as HTMLElement;
      copy.querySelectorAll('[data-testid="ddl-preview"]').forEach((n) => n.remove());
      return copy.innerText;
    });
  for (const region of [page.getByTestId("database-workspace"), inspector(page), page.getByRole("complementary", { name: "Explorer" })]) {
    const shown = await text(region);
    expect(named(shown)).toBeNull();
  }
  // The table's editor too.
  await inspector(page).getByTestId("table-inspector-edit").click();
  expect(named(await text(editor(page)))).toBeNull();
});
