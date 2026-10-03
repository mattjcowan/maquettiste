// New sequence…: from the Database screen's New menu, a sequence is created with its type, start and increment and opens in its
// editor; the Definition tab edits it one field per save; the Database screen lists it under Sequences and the DDL preview
// renders its CREATE SEQUENCE; undo takes each step back, the create last.
import type { Page } from "@playwright/test";
import { expect, test, keepDdlOpen } from "./fixtures";

// The DDL preview is hidden by default; these specs read it.
test.beforeEach(({ page }) => keepDdlOpen(page));

const editor = (page: Page) => page.getByTestId("element-editor");

test("New sequence… from the Database screen creates a sequence, Definition edits it, the DDL preview follows, and undo takes it back", async ({ page }) => {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId("database-new-menu").click();
  await page.getByTestId("database-new-sequence").click();

  const dialog = page.getByTestId("new-database-object-dialog");
  await expect(dialog).toHaveAttribute("data-kind", "sequence");
  await expect(dialog.getByLabel("Type")).toHaveValue("int64");
  await dialog.getByLabel("Name").fill("ticket_seq");
  await dialog.getByLabel("Start").fill("1000");
  await dialog.getByLabel("Increment").fill("0");
  await expect(dialog.getByText("Increment cannot be 0.")).toBeVisible();
  await expect(page.getByTestId("new-db-create")).toBeDisabled();
  await dialog.getByLabel("Increment").fill("1");
  await page.getByTestId("new-db-create").click();
  await expect(dialog).toHaveCount(0);

  await expect(editor(page)).toHaveAttribute("data-kind", "sequence");
  await expect(page.getByTestId("editor-title")).toHaveText("ticket_seq");
  await editor(page).getByRole("tab", { name: "Definition" }).click();
  const definition = editor(page).getByTestId("sequence-fields");
  await expect(definition.getByLabel("Start", { exact: true })).toHaveValue("1000");
  await definition.getByLabel("Increment", { exact: true }).fill("5");
  await definition.getByLabel("Increment", { exact: true }).press("Enter");
  await definition.getByLabel("Cycle (start again after the last value)").click();

  // The Database screen lists it under Sequences; picked, the preview renders the database's script that creates it.
  await page.getByTestId("editor-tab-screen").click();
  await page.getByTestId("database-list-kind-sequence").click();
  await page.getByTestId("database-sequence-ticket_seq").click();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("sql-ddl/sequence · ticket_seq");
  const ddl = page.getByTestId("ddl-preview");
  await expect(ddl).toContainText("CREATE SEQUENCE billing.ticket_seq AS bigint START WITH 1000 INCREMENT BY 5 CYCLE;");
  // The inspector shows its definition too.
  await expect(page.getByTestId("inspector").getByTestId("sequence-fields")).toBeVisible();

  // Undo: cycle, increment, then the create.
  const undo = page.getByRole("button", { name: "Undo" });
  await undo.click();
  await expect(ddl).toContainText("START WITH 1000 INCREMENT BY 5 NO CYCLE;");
  await undo.click();
  await expect(ddl).toContainText("START WITH 1000 INCREMENT BY 1 NO CYCLE;");
  await undo.click();
  await expect(page.getByTestId("database-sequence-ticket_seq")).toHaveCount(0);
  await expect(page.getByTestId("database-objects-count")).toHaveText("1 sequence");
});
