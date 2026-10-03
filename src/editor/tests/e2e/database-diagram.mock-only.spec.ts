// The database diagram designer (the owner: "seriously, how do I create a relationship/foreign key?"; "treat the database as
// its own thing, no entities on the database side"): a column's handle dragged onto a key column of another table opens the
// foreign key dialog; Create draws the edge with crow's foot ends, and undo removes it in one step; a click selects the edge
// (alone in the inspector), Edit changes its rules, Delete removes it after a confirmation; New table works from the canvas and lands
// where it was asked; the Display menu shows keys only (kept per browser); no entity name shows on the canvas.
import type { Locator, Page } from "@playwright/test";
import { expect, test } from "./fixtures";

const canvas = (page: Page) => page.getByTestId("database-canvas");
const tableCard = (page: Page, name: string) => canvas(page).getByTestId(`table-card-${name}`);
const row = (page: Page, table: string, column: string) => tableCard(page, table).getByTestId(`table-column-${column}`);

async function openDiagram(page: Page) {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  // The first layout (elkjs, in a worker) and its fit are done: the cards are where they stay.
  await expect(canvas(page)).toHaveAttribute("data-layout", "settled");
  await expect(tableCard(page, "notes")).toBeVisible();
  await expect(tableCard(page, "invoices")).toBeVisible();
  // Room to draw: the table list hidden too (the DDL preview starts hidden), the drawing fitted to the canvas.
  await expect(page.getByTestId("ddl-preview")).toHaveCount(0);
  await page.keyboard.press("Alt+Shift+L");
  await expect(page.getByTestId("database-tables")).toHaveCount(0);
  await canvas(page).locator(".react-flow__controls-fitview").click();
}

const nextFrame = (page: Page) => page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())));

/** An element's box once it has stopped moving: two reads one frame apart that agree. */
async function stableBox(page: Page, locator: Locator) {
  let box = await locator.boundingBox();
  for (let i = 0; i < 60; i++) {
    await nextFrame(page);
    const again = await locator.boundingBox();
    if (box && again && box.x === again.x && box.y === again.y && box.width === again.width && box.height === again.height) return again;
    box = again;
  }
  throw new Error("the element kept moving");
}

/**
 * Drags with the mouse from the centre of one element to the centre of another, in steps, as a person would: both measured once
 * they stand still, and the target measured again just before the button goes up.
 */
async function drag(page: Page, from: Locator, to: Locator) {
  const a = await stableBox(page, from);
  await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
  await page.mouse.down();
  await page.mouse.move(a.x + a.width / 2 + 30, a.y + a.height / 2 + 10, { steps: 4 });
  const b = await stableBox(page, to);
  await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 12 });
  const c = await stableBox(page, to);
  await page.mouse.move(c.x + c.width / 2, c.y + c.height / 2);
  await page.mouse.up();
}

/** The keyboard path to a new key (tests not about the drag): Enter on a handle, then the referenced table picked. */
async function newKeyByKeyboard(page: Page, handle: Locator, references: string) {
  await handle.focus();
  await page.keyboard.press("Enter");
  const dialog = page.getByTestId("fk-dialog");
  await expect(dialog).toBeVisible();
  await dialog.getByLabel("References table").selectOption({ label: references });
  return dialog;
}

test("a column dragged onto a key column creates a foreign key with crow's foot ends; undo takes it back", async ({ page }) => {
  await openDiagram(page);
  // The rows carry their markers: invoices.id is the key, invoices.number is unique and not null.
  await expect(row(page, "invoices", "id")).toHaveAttribute("data-markers", "pk nn");
  await expect(row(page, "invoices", "number")).toHaveAttribute("data-markers", "uq nn");
  await expect(row(page, "invoices", "customer_id")).toHaveAttribute("data-markers", "fk nn");
  await expect(row(page, "invoices", "id").getByRole("img", { name: "Primary key" })).toHaveAttribute("title", "Primary key");

  await row(page, "notes", "entity_id").hover();
  await drag(page, row(page, "notes", "entity_id").getByTestId("column-link-handle"), row(page, "invoices", "id"));
  const dialog = page.getByTestId("fk-dialog");
  await expect(dialog).toBeVisible();
  await expect(page.getByRole("dialog", { name: "New foreign key on notes" })).toBeVisible();
  await expect(dialog.getByLabel("References table")).toHaveValue(/@/);
  await expect(dialog.getByLabel("Column 1", { exact: true })).toHaveValue("01K6BND0000000000000000004");
  await expect(dialog.getByLabel("Referenced column 1", { exact: true })).toHaveValue("01J92P0V0Q9EK961M5HAQ3C5MY");
  await expect(dialog.getByLabel("Name")).toHaveValue("fk_notes_entity_id");
  await expect(page.getByTestId("fk-store-note")).toHaveCount(0);
  await dialog.getByLabel("On delete").selectOption("cascade");
  await expect(dialog).toBeVisible();
  await page.getByTestId("fk-save").click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByTestId("notice")).toContainText("Foreign key fk_notes_entity_id created.");

  // The edge: many notes rows to exactly one invoices row (entity_id is not null and not unique).
  const edge = canvas(page).getByTestId("fk-edge-fk_notes_entity_id");
  await expect(edge).toHaveAttribute("data-ends", "many:one");
  await expect(row(page, "notes", "entity_id")).toHaveAttribute("data-markers", "fk nn");

  await page.getByRole("button", { name: "Undo" }).click();
  await expect(edge).toHaveCount(0);
  await expect(row(page, "notes", "entity_id")).toHaveAttribute("data-markers", "nn");
});

test("a key from a table not stored as a file yet stores it first and lands in the new file; undo and redo take both", async ({ page }) => {
  await openDiagram(page);
  const banner = page.getByTestId("store-tables-banner");
  await expect(banner).toHaveText(/5 tables are not stored/);
  // From customers' header handle (a laid-out table), by the keyboard, to notes: the key references notes.id.
  const header = tableCard(page, "customers").locator("[data-table-header]");
  const dialog = await newKeyByKeyboard(page, header.getByTestId("table-link-handle"), "billing.notes");
  await expect(page.getByRole("dialog", { name: "New foreign key on customers" })).toBeVisible();
  await expect(page.getByTestId("fk-store-note")).toContainText("customers is not stored as a table file yet");
  await dialog.getByTestId("fk-add-column").first().click();
  await expect(dialog.getByTestId("fk-new-column")).toHaveValue("note_id");
  await dialog.getByLabel("Deferrable").selectOption("initially-deferred");
  await expect(dialog).toBeVisible();
  await page.getByTestId("fk-save").click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByTestId("notice")).toContainText("Foreign key fk_customers_note_id created.");
  const edge = canvas(page).getByTestId("fk-edge-fk_customers_note_id");
  await expect(edge).toHaveCount(1);
  await expect(banner).toHaveText(/4 tables are not stored/);
  await expect(row(page, "customers", "note_id")).toHaveAttribute("data-markers", "fk");
  // The key is in the customers file (its deferrable kept), as the inspector shows it.
  await edge.dispatchEvent("click");
  await expect(page.getByTestId("table-inspector-part").getByLabel("Deferrable of fk_customers_note_id")).toHaveValue("initially-deferred");

  // One undo puts customers back as it was laid out, without the key; redo stores it with the key again.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(edge).toHaveCount(0);
  await expect(banner).toHaveText(/5 tables are not stored/);
  await expect(row(page, "customers", "note_id")).toHaveCount(0);
  await page.getByRole("button", { name: "Redo" }).click();
  await expect(edge).toHaveCount(1);
  await expect(banner).toHaveText(/4 tables are not stored/);
});

test("a selected foreign key shows alone in the inspector, edits in the dialog, and Delete removes it after a confirmation", async ({ page }) => {
  await openDiagram(page);
  const made = await newKeyByKeyboard(page, row(page, "notes", "entity_id").getByTestId("column-link-handle"), "billing.invoices");
  await expect(made.getByLabel("Name")).toHaveValue("fk_notes_entity_id");
  await page.getByTestId("fk-save").click();
  await expect(made).toHaveCount(0);
  const edge = canvas(page).getByTestId("fk-edge-fk_notes_entity_id");
  await expect(edge).toHaveCount(1);

  // A click shows the key in the inspector, alone under its table's breadcrumb (no panel on the canvas).
  await edge.dispatchEvent("click");
  const part = page.getByTestId("table-inspector-part");
  await expect(part).toHaveAttribute("data-part", "foreign-key");
  await expect(part).toContainText("fk_notes_entity_id");
  await expect(page.getByTestId("inspector-breadcrumb")).toContainText("notes");
  await expect(page.getByTestId("fk-panel")).toHaveCount(0);
  await expect(canvas(page).getByTestId("fk-label-fk_notes_entity_id")).toBeVisible();

  // A double click edits it.
  await edge.dispatchEvent("dblclick");
  const dialog = page.getByTestId("fk-dialog");
  await expect(page.getByRole("dialog", { name: "Edit foreign key fk_notes_entity_id" })).toBeVisible();
  await dialog.getByLabel("On update").selectOption("cascade");
  await page.getByTestId("fk-save").click();
  await expect(dialog).toHaveCount(0);
  await expect(part.getByLabel("On update of fk_notes_entity_id")).toHaveValue("cascade");

  // Delete on the selected edge asks first; Delete in the confirmation removes the key; Undo brings it back.
  await canvas(page).focus();
  await page.keyboard.press("Delete");
  await expect(page.getByTestId("fk-delete-confirm")).toContainText("The key is removed from table notes");
  await page.getByTestId("fk-delete-confirm-button").click();
  await expect(edge).toHaveCount(0);
  await expect(part).toHaveCount(0);
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(edge).toHaveCount(1);
});

test("New table from the canvas lands where the canvas was double-clicked; Delete on a table offers its delete plan", async ({ page }) => {
  await openDiagram(page);
  await page.getByTestId("database-canvas-new-table").click();
  const newDialog = page.getByTestId("new-database-object-dialog");
  await expect(newDialog).toHaveAttribute("data-kind", "table");
  await page.keyboard.press("Escape");
  await expect(newDialog).toHaveCount(0);

  // Room to click: the minimap off (Display).
  await page.getByTestId("database-display-options").click();
  await page.getByTestId("database-display-minimap").click();
  await expect(canvas(page).locator(".react-flow__minimap")).toHaveCount(0);
  await page.keyboard.press("Escape");
  const pane = canvas(page).locator(".react-flow__pane");
  const box = (await pane.boundingBox())!;
  const at = { x: 16, y: 16 };
  // The canvas then centres the new table, so the check is relative to another card, at the new zoom.
  const notesBefore = (await tableCard(page, "notes").boundingBox())!;
  await pane.dblclick({ position: at });
  await expect(newDialog).toHaveAttribute("data-kind", "table");
  await newDialog.getByLabel("Name").fill("audit_log");
  await page.getByTestId("new-db-create").click();
  await expect(newDialog).toHaveCount(0);
  // The new table opens in its editor in front of the Database screen; back on the screen, its card is where the click was.
  await page.getByTestId("editor-tab-screen").click();
  const card = tableCard(page, "audit_log");
  await expect(card).toBeVisible();
  const placed = (await card.boundingBox())!;
  const notesAfter = (await tableCard(page, "notes").boundingBox())!;
  const zoom = notesAfter.width / notesBefore.width;
  expect(Math.abs(placed.x - notesAfter.x - (box.x + at.x - notesBefore.x) * zoom)).toBeLessThan(6);
  expect(Math.abs(placed.y - notesAfter.y - (box.y + at.y - notesBefore.y) * zoom)).toBeLessThan(6);

  // Delete on the selected table: the delete plan dialog.
  await card.click();
  await page.keyboard.press("Delete");
  await expect(page.getByRole("dialog", { name: "Delete audit_log?" })).toBeVisible();
  await page.keyboard.press("Escape");
});

test("Keys only and Names only, kept per browser; the canvas names no entity", async ({ page }) => {
  await openDiagram(page);
  const text = (await canvas(page).innerText()).replace(/\s+/g, " ");
  for (const entity of ["Customer", "Invoice", "InvoiceLine", "Payment", "Product", "CustomerNote", "InvoiceNote"])
    expect(text, `entity ${entity} on the canvas`).not.toMatch(new RegExp(`\\b${entity}\\b`));
  expect(text).not.toMatch(/synthesized|projected/i);

  await page.getByTestId("database-display-options").click();
  await page.getByRole("menuitemradio", { name: "Keys only" }).click();
  await expect(tableCard(page, "invoices")).toHaveAttribute("data-display", "keys");
  await expect(tableCard(page, "invoices").locator("[data-column-key]")).toHaveCount(3);
  await expect(row(page, "invoices", "status")).toHaveCount(0);
  await expect(row(page, "invoices", "number")).toBeVisible();
  // The foreign key edges still join the shown key rows.
  await expect(canvas(page).getByTestId("fk-edge-fk_invoices_customer_id")).toHaveAttribute("data-ends", "many:one");

  await page.reload();
  await expect(tableCard(page, "invoices")).toHaveAttribute("data-display", "keys");
  await page.getByTestId("database-display-options").click();
  await page.getByRole("menuitemradio", { name: "Names only" }).click();
  await expect(tableCard(page, "invoices").locator("[data-column-key]")).toHaveCount(0);
  await page.getByTestId("database-display-options").click();
  await page.getByRole("menuitemradio", { name: "All columns" }).click();
  await expect(row(page, "invoices", "status")).toBeVisible();
});

test("the keyboard reaches a column's handle: Enter opens the dialog with that column, the referenced table to choose", async ({ page }) => {
  await openDiagram(page);
  const handle = row(page, "notes", "entity_id").getByTestId("column-link-handle");
  await expect(handle).toHaveAttribute("title", /New foreign key from notes\.entity_id/);
  await handle.focus();
  await page.keyboard.press("Enter");
  const dialog = page.getByTestId("fk-dialog");
  await expect(dialog.getByLabel("References table")).toHaveValue("");
  await expect(page.getByTestId("fk-save")).toBeDisabled();
  await dialog.getByLabel("References table").selectOption({ label: "billing.invoices" });
  await expect(dialog.getByLabel("Column 1", { exact: true })).toHaveValue("01K6BND0000000000000000004");
  await expect(dialog.getByLabel("Referenced column 1", { exact: true })).toHaveValue("01J92P0V0Q9EK961M5HAQ3C5MY");
  await expect(page.getByTestId("fk-save")).toBeEnabled();
  await page.keyboard.press("Escape");
  await expect(dialog).toHaveCount(0);
});
