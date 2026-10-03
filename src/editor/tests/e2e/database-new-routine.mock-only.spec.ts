// New routine…: from the Database screen's New menu, a function is created with its result and a body prefilled for the
// database's dialect and opens in its editor; the Parameters tab adds and edits a parameter one save each, the Definition tab
// marks it deterministic; the Databases explorer lists it under Routines, the Database screen under Routines with its DDL, the
// inspector summarises it; undo takes a step back, and the inspector's delete removes it through the delete plan (nothing refers
// to it, so no choice is asked).
import type { Page } from "@playwright/test";
import { expect, test, workspace, keepDdlOpen } from "./fixtures";

// The DDL preview is hidden by default; these specs read it.
test.beforeEach(({ page }) => keepDdlOpen(page));

const editor = (page: Page) => page.getByTestId("element-editor");

/** The routine's document, read through the API (null once it is gone). */
const routineDoc = (page: Page, name: string) =>
  page.evaluate(async (wanted) => {
    const index = (await (await fetch("/api/model/index")).json()) as
      { elements?: { id: string; kind: string; name: string }[] } | { id: string; kind: string; name: string }[];
    const row = (Array.isArray(index) ? index : (index.elements ?? [])).find((r) => r.kind === "routine" && r.name === wanted);
    if (!row) return null;
    const read = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: [row.id] }),
    });
    const [doc] = ((await read.json()) as { elements: { json: Record<string, unknown> }[] }).elements;
    return doc?.json ?? null;
  }, name);

test("New routine… creates a function, Parameters and Definition edit it, it is listed and previewed, and the delete plan removes it", async ({ page }) => {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId("database-new-menu").click();
  await page.getByTestId("database-new-routine").click();

  const dialog = page.getByTestId("new-database-object-dialog");
  await expect(dialog).toHaveAttribute("data-kind", "routine");
  await expect(dialog.getByLabel("Routine kind")).toHaveValue("function");
  await expect(dialog.getByLabel("Returns")).toHaveValue("int32");
  await expect(dialog.getByLabel("Dialect")).toHaveValue("postgresql");
  await expect(dialog.getByLabel("Body")).toHaveValue("begin\n  return 0;\nend");
  await dialog.getByLabel("Name").fill("invoice_count");
  await dialog.getByLabel("Body").fill("begin\n  return (select count(*) from billing.invoices where customer_id = p_customer);\nend");
  await page.getByTestId("new-db-create").click();
  await expect(dialog).toHaveCount(0);

  await expect(editor(page)).toHaveAttribute("data-kind", "routine");
  await expect(page.getByTestId("editor-title")).toHaveText("invoice_count");

  // Parameters: Add parameter, then rename it and give it a type, one save each.
  await editor(page).getByRole("tab", { name: "Parameters" }).click();
  await page.getByTestId("routine-parameters-add").click();
  await expect(page.getByTestId("routine-parameters-row-param_1")).toBeVisible();
  await page.getByLabel("Name of parameter 1").fill("p_customer");
  await page.getByLabel("Name of parameter 1").press("Enter");
  await expect(page.getByTestId("routine-parameters-row-p_customer")).toBeVisible();
  await page.getByLabel("Type of parameter p_customer", { exact: true }).selectOption("uuid");
  await expect.poll(async () => (await routineDoc(page, "invoice_count"))?.parameters).toEqual([{ name: "p_customer", type: "uuid" }]);

  // Definition: deterministic.
  await editor(page).getByRole("tab", { name: "Definition" }).click();
  await editor(page).getByLabel("Deterministic (the same arguments always give the same result)").check();
  await expect.poll(async () => (await routineDoc(page, "invoice_count"))?.deterministic).toBe(true);

  // The Database screen: the Routines chip lists it, the preview renders the whole database (the routine unit writes its own
  // script only with objectScripts on), which creates it.
  await page.getByTestId("editor-tab-screen").click();
  await page.getByTestId("database-list-kind-routine").click();
  await expect(page.getByTestId("database-objects-count")).toHaveText("3 routines");
  await page.getByTestId("database-routine-invoice_count").click();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("sql-ddl/routine · invoice_count");
  await expect(page.getByTestId("ddl-preview")).toContainText("CREATE FUNCTION billing.invoice_count(p_customer uuid) RETURNS integer");
  await expect(page.getByTestId("ddl-preview")).toContainText("LANGUAGE plpgsql IMMUTABLE");
  await expect(page.getByTestId("inspector").getByTestId("routine-fields")).toContainText("1 parameter; returns int32; body for postgresql");

  // Undo takes the deterministic mark back.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(async () => (await routineDoc(page, "invoice_count"))?.deterministic).toBeUndefined();

  // The Databases explorer lists it under main › billing › Routines.
  await workspace(page, "Databases");
  const tree = page.getByRole("complementary", { name: "Explorer" }).getByTestId("explorer-tree");
  await expect(tree.getByTestId("explorer-folder-Routines")).toBeVisible();
  const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();
  if ((await tree.getByTestId("explorer-folder-Routines").getAttribute("aria-expanded")) !== "true")
    await chevron(tree.getByTestId("explorer-folder-Routines")).click();
  await expect(tree.getByTestId("explorer-row-invoice_count")).toBeVisible();

  // Delete through the plan: nothing refers to it, so the plan deletes it at once.
  await tree.getByTestId("explorer-row-invoice_count").click();
  await expect(page.getByTestId("inspector-title")).toHaveText("invoice_count");
  await page.getByRole("button", { name: "Delete invoice_count" }).click();
  await expect(page.getByTestId("delete-plan-dialog")).toHaveCount(0);
  await expect(tree.getByTestId("explorer-row-invoice_count")).toHaveCount(0);
  await expect.poll(() => routineDoc(page, "invoice_count")).toBeNull();
});
