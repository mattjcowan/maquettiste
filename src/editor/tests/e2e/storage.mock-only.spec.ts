// The entity side's storage (erratum E43; the owner: "do the mapping on the entity side completely"): the Storage tab binds an
// entity to the shared notes table with a constant, Map by name, Ignore, a read-only write, each one undo step; Create a table
// for this entity; New entities from tables; a relationship's foreign key; Remove bindings and Auto-map in bulk; the domain
// only filter; and the Mappings screen is gone.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function openEntity(page: Page, name: string): Promise<Locator> {
  const side = explorer(page);
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).dblclick();
  const editor = page.getByRole("region", { name: `Editor: ${name}` });
  await expect(editor).toBeVisible();
  return editor;
}

async function storage(page: Page, name: string): Promise<Locator> {
  const editor = await openEntity(page, name);
  await editor.getByRole("tab", { name: "Storage" }).click();
  return editor.getByTestId("storage-tab");
}

const undo = (page: Page) => page.getByRole("button", { name: "Undo" }).click();

test.beforeEach(async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
});

test("binds an entity to the shared notes table: constant, Map by name, Ignore, read-only, each one undo step", async ({ page }) => {
  await openEditor(page);
  // A note entity of its own, saved elsewhere (the server tells the editor).
  const saved = await page.evaluate(async (pkg) => {
    const res = await fetch("/api/model/elements", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        kind: "entity",
        id: "01K7STR0000000000000000001",
        name: "ProductNote",
        package: pkg,
        key: { attributes: ["01K7STR0000000000000000002"], strategy: "uuid-v7" },
        attributes: [
          { id: "01K7STR0000000000000000002", name: "id", type: "uuid", required: true },
          { id: "01K7STR0000000000000000003", name: "entityId", type: "uuid", required: true },
          { id: "01K7STR0000000000000000004", name: "body", type: "text", required: true },
        ],
      }),
    });
    return ((await res.json()) as { outcome: string }).outcome;
  }, BILLING);
  expect(saved).toBe("saved");
  const tab = await storage(page, "ProductNote");
  await expect(tab.getByTestId("storage-domain-only")).toContainText("Domain only: not stored in any database.");

  // Add binding: main, the notes table.
  await tab.getByTestId("storage-add-binding").click();
  const add = page.getByTestId("add-binding-dialog");
  await add.getByLabel("Source").selectOption({ label: "billing.notes" });
  await add.getByTestId("add-binding-apply").click();
  const card = tab.getByTestId("binding-card");
  await expect(card).toHaveAttribute("data-database", "main");
  await expect(tab.getByTestId("storage-domain-only")).toHaveCount(0);
  await expect(card.getByTestId("field-row-id")).toHaveAttribute("data-status", "unmapped");
  await expect(card.getByTestId("binding-problems").locator('[data-rule="MQ4046"]')).toBeVisible();

  // Map by name: a review list, then one save.
  await card.getByTestId("binding-map-by-name").click();
  const review = page.getByTestId("map-by-name-dialog");
  await expect(review.getByText("entityId → entity_id")).toBeVisible();
  await review.getByTestId("map-by-name-apply").click();
  for (const name of ["id", "entityId", "body"]) await expect(card.getByTestId(`field-row-${name}`)).toHaveAttribute("data-status", "mapped");
  await expect(card.getByTestId("column-row-entity_type")).toHaveAttribute("data-status", "unaccounted");
  await expect(card.getByTestId("binding-problems").locator('[data-rule="MQ4047"]')).toBeVisible();

  // Ignore a column, then take it back.
  await card.getByTestId("column-row-entity_type").getByTestId("column-ignore").click();
  await expect(card.getByTestId("column-row-entity_type")).toHaveAttribute("data-status", "ignored");
  await expect(card.getByTestId("binding-problems")).toHaveCount(0);
  await undo(page);
  await expect(card.getByTestId("column-row-entity_type")).toHaveAttribute("data-status", "unaccounted");

  // A constant: entity_type = 'product'. Nothing is unaccounted then.
  await card.getByTestId("binding-add-constant").click();
  await expect(card.getByTestId("column-row-entity_type")).toHaveAttribute("data-status", "constant");
  await card.getByLabel("Value of constant 1").fill("product");
  await card.getByLabel("Value of constant 1").press("Enter");
  await expect(card.getByTestId("column-row-entity_type")).toContainText('constant "product"');
  await expect(card.getByTestId("binding-problems")).toHaveCount(0);

  // The statements.
  await card.getByTestId("binding-sql").locator("summary").click();
  await expect(card.getByTestId("binding-sql-select")).toContainText("FROM billing.notes t");
  await expect(card.getByTestId("binding-sql-select")).toContainText("t.entity_type = 'product'");

  // Read-only: write none; the delete mode goes with it.
  await card.getByTestId("binding-write").selectOption("none");
  await expect(card.getByTestId("binding-delete")).toBeDisabled();
  await expect(card.getByTestId("binding-sql-insert")).toContainText("None");

  // Undo each step back to domain only.
  await undo(page);
  await expect(card.getByTestId("binding-write")).toHaveValue("source");
  await undo(page);
  await expect(card.getByLabel("Value of constant 1")).toHaveValue("");
  await undo(page);
  await expect(card.getByTestId("constant-row")).toHaveCount(0);
  await undo(page);
  await expect(card.getByTestId("field-row-id")).toHaveAttribute("data-status", "unmapped");
  await undo(page);
  await expect(tab.getByTestId("storage-domain-only")).toBeVisible();
});

test("Create a table for this entity binds it to a new table, and undo takes it back", async ({ page }) => {
  await openEditor(page);
  const tab = await storage(page, "Payment");
  await expect(tab.getByTestId("storage-domain-only")).toContainText("main still projects a table for it");
  await tab.getByTestId("storage-create-table").click();
  const dialog = page.getByTestId("create-tables-dialog");
  await expect(dialog.getByTestId("storage-plan")).toContainText("payments");
  await dialog.getByTestId("create-tables-apply").click();
  await expect(dialog).toHaveCount(0);
  const card = tab.getByTestId("binding-card");
  await expect(card).toHaveAttribute("data-database", "main");
  await expect(card.getByTestId("binding-source").locator("option:checked")).toHaveText("billing.payments");
  await expect(card.getByTestId("field-row-id")).toHaveAttribute("data-status", "mapped");
  await expect(card.getByTestId("binding-problems")).toHaveCount(0);
  await undo(page);
  await expect(tab.getByTestId("storage-domain-only")).toBeVisible();
});

test("New entities from tables makes an entity of a view, in one undo step", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await side.getByTestId("explorer-new").click();
  await page.getByTestId("explorer-new-entities-from-tables").click();
  const dialog = page.getByTestId("entities-from-tables-dialog");
  await dialog.getByLabel("Domain").selectOption({ label: "Billing" });
  await dialog.getByTestId("entities-from-tables-list").getByText("billing.outstanding_invoices").click();
  await expect(dialog.getByTestId("storage-plan")).toContainText("OutstandingInvoice");
  await dialog.getByTestId("entities-from-tables-apply").click();
  await expect(dialog).toHaveCount(0);
  const tab = await storage(page, "OutstandingInvoice");
  await expect(tab.getByTestId("binding-card").getByTestId("field-row-issuedOn")).toHaveAttribute("data-status", "mapped");
  await undo(page);
  await side.getByLabel("Search the model").fill("OutstandingInvoice");
  await expect(side.getByTestId("explorer-row-OutstandingInvoice")).toHaveCount(0);
});

test("a relationship whose ends are not both bound asks for that; an older shape shows read only", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("places");
  await side.getByTestId("explorer-row-places").dblclick();
  const editor = page.getByRole("region", { name: "Editor: places" });
  await editor.getByRole("tab", { name: "Storage" }).click();
  const main = editor.getByTestId("relation-storage-database").filter({ hasText: "main" });
  await expect(main.getByTestId("relation-storage-unbound")).toHaveText(/^Bind both ends to a table to name the foreign key\./);
  // Nothing to pick: no shape choice that would make a mapping of the older format.
  await expect(main.getByRole("combobox")).toHaveCount(0);
  await expect(main).not.toContainText("conventions store it");
});

test("a relationship between two bound entities names the foreign key that realizes it", async ({ page }) => {
  await openEditor(page);
  for (const name of ["Customer", "Invoice"]) {
    const tab = await storage(page, name);
    // Invoice still has an older mapping element for main; Create a table replaces it.
    if (name === "Invoice") await expect(tab.getByTestId("storage-legacy-mapping")).toContainText("older mapping element");
    await tab.getByTestId("storage-create-table").click();
    await page.getByTestId("create-tables-apply").click();
    await expect(tab.getByTestId("binding-card")).toBeVisible();
  }
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("places");
  await side.getByTestId("explorer-row-places").dblclick();
  const editor = page.getByRole("region", { name: "Editor: places" });
  await editor.getByRole("tab", { name: "Storage" }).click();
  const main = editor.getByTestId("relation-storage-database").filter({ hasText: "main" });
  const picker = main.getByTestId("relation-foreign-key");
  // Create tables named it already; take it away, then pick it.
  await expect(picker.locator("option:checked")).toContainText("invoices.fk_invoices");
  await picker.selectOption("");
  await expect(main.getByTestId("relation-storage-problems")).toContainText("MQ4011");
  await picker.selectOption({ index: 1 });
  await expect(picker.locator("option:checked")).toContainText("customer_id → customers");
  await expect(main.getByTestId("relation-storage-problems")).toHaveCount(0);
});

test("Remove bindings and Auto-map work in bulk from the Domain model, and the storage filter tells them apart", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("InvoiceNote");
  await side.getByTestId("explorer-row-InvoiceNote").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Remove bindings…" }).click();
  const remove = page.getByTestId("remove-bindings-dialog");
  await expect(remove.getByTestId("remove-bindings-list")).toContainText("InvoiceNote");
  await remove.getByTestId("remove-bindings-apply").click();
  await expect(remove).toHaveCount(0);

  // Domain only now: the filter keeps it; Bound to a database does not.
  await side.getByTestId("explorer-filters").click();
  await page.getByTestId("filter-storage-domain-only").click();
  await page.keyboard.press("Escape");
  await expect(side.getByTestId("explorer-row-InvoiceNote")).toBeVisible();
  await side.getByTestId("explorer-filters").click();
  await page.getByTestId("filter-storage-bound").click();
  await page.keyboard.press("Escape");
  await expect(side.getByTestId("explorer-row-InvoiceNote")).toHaveCount(0);
  await side.getByTestId("explorer-filters").click();
  await page.getByTestId("filter-storage-bound").click();
  await page.keyboard.press("Escape");

  // Auto-map: no table by its name, so pick notes; the attributes map by name.
  await side.getByTestId("explorer-row-InvoiceNote").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Auto-map to existing tables…" }).click();
  const dialog = page.getByTestId("auto-map-dialog");
  const row = dialog.getByTestId("auto-map-row-InvoiceNote");
  await expect(row).toHaveAttribute("data-outcome", "miss");
  await row.getByLabel("Source of InvoiceNote").selectOption({ label: "notes" });
  await expect(row).toHaveAttribute("data-outcome", "partial");
  await expect(row).toContainText("not found: invoiceId");
  await dialog.getByTestId("auto-map-apply").click();
  await expect(dialog).toHaveCount(0);
  const tab = await storage(page, "InvoiceNote");
  await expect(tab.getByTestId("binding-card").getByTestId("field-row-body")).toHaveAttribute("data-status", "mapped");
  await expect(tab.getByTestId("binding-card").getByTestId("field-row-invoiceId")).toHaveAttribute("data-status", "unmapped");
});

test("the Mappings screen is gone", async ({ page }) => {
  await openEditor(page, "/mappings");
  await expect(page).toHaveURL(/\/entities/);
  await page.keyboard.press("Control+k");
  await page.keyboard.type("screen Mappings");
  await expect(page.getByRole("option", { name: "Mappings", exact: true })).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("navigation", { name: "Explorers" }).getByRole("button", { name: "Mappings" })).toHaveCount(0);
});
