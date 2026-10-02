// New query…: from the Database screen's New menu, a query whose rows are invoices is created over the invoices table (its key
// selected) and opens in its editor; Sources adds a join to customers and fills its condition from the foreign key, Select fills
// the attributes from the same-named columns, Parameters adds a parameter, Filter compares a column with it, Group and order
// adds an order, and the SQL tab renders each step and follows a change; undo takes them back one at a time, the explorer lists
// the query under Queries, and the inspector's delete removes it through the delete plan (nothing refers to it).
import type { Page } from "@playwright/test";
import { expect, test, workspace } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");
const tab = (page: Page, name: string) => editor(page).getByRole("tab", { name, exact: true }).click();
const sqlText = (page: Page) => editor(page).getByTestId("query-sql-statement").getByTestId("code-text-sql");

/** The query's document, read through the API (null once it is gone). */
const queryDoc = (page: Page, name: string) =>
  page.evaluate(async (wanted) => {
    const index = (await (await fetch("/api/model/index")).json()) as
      { elements?: { id: string; kind: string; name: string }[] } | { id: string; kind: string; name: string }[];
    const row = (Array.isArray(index) ? index : (index.elements ?? [])).find((r) => r.kind === "query" && r.name === wanted);
    if (!row) return null;
    const read = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: [row.id] }),
    });
    const [doc] = ((await read.json()) as { elements: { json: Record<string, unknown> }[] }).elements;
    return doc?.json ?? null;
  }, name);

const INVOICE_ID = "01J92P0V0Q9EK961M5HAQ3C5MY";
const CUSTOMER_KEY = "01J92P0V0KGPC29TQQG8R57EBM";
const INVOICE_TO_CUSTOMER = "01J92P0V1EHF7PB28CZJG9C5SN";

test("New query… creates a query, the editor joins, fills, filters and orders it with the SQL following, undo steps back, and the plan deletes it", async ({
  page,
}) => {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId("database-new-menu").click();
  await page.getByTestId("database-new-query").click();

  const dialog = page.getByTestId("new-database-object-dialog");
  await expect(dialog).toHaveAttribute("data-kind", "query");
  await expect(dialog.getByLabel("Schema")).toHaveCount(0);
  await dialog.getByLabel("Name").fill("CustomerInvoices");
  await dialog.getByLabel("Result entity").selectOption({ label: "Invoice" });
  // The source follows the entity to its table, the alias to the table's name.
  await expect(dialog.getByLabel("From")).toHaveValue(`01J92P0V0FJ23CGSNKM7P1W5V7@01J92P0V1QRN2181XM2ZWE02W4`);
  await expect(dialog.getByLabel("Alias")).toHaveValue("i");
  await page.getByTestId("new-db-create").click();
  await expect(dialog).toHaveCount(0);

  await expect(editor(page)).toHaveAttribute("data-kind", "query");
  await expect(page.getByTestId("editor-title")).toHaveText("CustomerInvoices");
  await expect
    .poll(async () => (await queryDoc(page, "CustomerInvoices"))?.select)
    .toEqual([{ attribute: INVOICE_ID, expression: { column: `i.${INVOICE_ID}` } }]);

  // Sources: a join to the related table, its condition from the foreign key.
  await tab(page, "Sources");
  await page.getByTestId("query-add-join").click();
  await expect(editor(page).getByLabel("Query join 1 alias")).toHaveValue("c");
  await editor(page).getByTestId("query-join-1").getByRole("button", { name: "Join by foreign key" }).click();
  await expect
    .poll(async () => ((await queryDoc(page, "CustomerInvoices"))?.joins as unknown[] | undefined)?.[0])
    .toEqual({
      source: "01J92P0V0ETQKXXP951CMMNHH3@01J92P0V1QRN2181XM2ZWE02W4",
      alias: "c",
      on: { op: "eq", left: { column: `i.${INVOICE_TO_CUSTOMER}.${CUSTOMER_KEY}` }, right: { column: `c.${CUSTOMER_KEY}` } },
    });
  await expect(editor(page).getByLabel("Query join 1 on left: column").locator("option:checked")).toHaveText("i.customer_id");

  // Select: the attributes from the invoices' same-named columns.
  await tab(page, "Select");
  await expect(editor(page).getByTestId("query-attribute-number")).toContainText("Select number");
  await editor(page).getByRole("button", { name: "Fill from columns by name" }).click();
  await page.getByRole("menuitem", { name: "i (invoices)" }).click();
  await expect(editor(page).getByLabel("Field number: column").locator("option:checked")).toHaveText("i.number");
  await expect(editor(page).getByLabel("Field issuedOn: column").locator("option:checked")).toHaveText("i.issued_on");
  await expect.poll(async () => ((await queryDoc(page, "CustomerInvoices"))?.select as unknown[]).length).toBeGreaterThan(5);

  // Parameters: one, renamed and typed.
  await tab(page, "Parameters");
  await page.getByTestId("query-add-parameter").click();
  await editor(page).getByLabel("Name of parameter 1").fill("customerId");
  await editor(page).getByLabel("Name of parameter 1").press("Enter");
  await editor(page).getByLabel("Type of parameter customerId").selectOption("uuid");
  await expect.poll(async () => (await queryDoc(page, "CustomerInvoices"))?.parameters).toEqual([{ name: "customerId", type: "uuid" }]);

  // Filter: the customer's key equals the parameter.
  await tab(page, "Filter");
  await editor(page).getByTestId("query-filter").getByRole("button", { name: "Add condition" }).click();
  await editor(page).getByLabel("Where left: column").selectOption({ label: "c.id" });
  await expect(editor(page).getByLabel("Where right: parameter")).toHaveValue("customerId");
  await expect
    .poll(async () => (await queryDoc(page, "CustomerInvoices"))?.where)
    .toEqual({ op: "eq", left: { column: `c.${CUSTOMER_KEY}` }, right: { param: "customerId" } });

  // Group and order: newest first.
  await tab(page, "Group and order");
  await page.getByTestId("query-add-order").click();
  await editor(page).getByLabel("Query order 1: column").selectOption({ label: "i.issued_on" });
  await editor(page).getByLabel("Query order 1 direction").selectOption("desc");
  await expect.poll(async () => ((await queryDoc(page, "CustomerInvoices"))?.orderBy as { direction?: string }[] | undefined)?.[0]?.direction).toBe("desc");

  // SQL: the statement as the engine renders it, then following a change.
  await tab(page, "SQL");
  await expect(sqlText(page)).toContainText("FROM billing.invoices i\nINNER JOIN billing.customers c ON i.customer_id = c.id\nWHERE c.id = @customerId");
  await expect(sqlText(page)).toContainText("ORDER BY i.issued_on DESC");
  await expect(editor(page).getByTestId("query-sql-parameters")).toHaveText("Parameters: @customerId");
  await editor(page).getByLabel("Dialect").selectOption("sqlserver");
  await expect(sqlText(page)).toContainText("ORDER BY i.issued_on DESC");
  await expect(sqlText(page)).toContainText("FROM billing.invoices i");
  await editor(page).getByLabel("Dialect").selectOption("postgresql");
  await tab(page, "Group and order");
  await editor(page).getByLabel("Query order 1 direction").selectOption("asc");
  await tab(page, "SQL");
  await expect(sqlText(page)).toContainText("ORDER BY i.issued_on ASC");

  // Undo, one step at a time: the direction, the order's column, the order, the condition.
  const undo = page.getByRole("button", { name: "Undo" });
  await undo.click();
  await expect.poll(async () => ((await queryDoc(page, "CustomerInvoices"))?.orderBy as { direction?: string }[] | undefined)?.[0]?.direction).toBe("desc");
  await undo.click();
  await expect.poll(async () => ((await queryDoc(page, "CustomerInvoices"))?.orderBy as { direction?: string }[] | undefined)?.[0]?.direction).toBeUndefined();
  await undo.click();
  await expect
    .poll(async () => ((await queryDoc(page, "CustomerInvoices"))?.orderBy as { expression: unknown }[] | undefined)?.[0]?.expression)
    .toEqual({ column: `i.${INVOICE_ID}` });
  await undo.click();
  await expect.poll(async () => (await queryDoc(page, "CustomerInvoices"))?.orderBy).toBeUndefined();
  await expect(sqlText(page)).not.toContainText("ORDER BY");
  await undo.click();
  await expect
    .poll(async () => (await queryDoc(page, "CustomerInvoices"))?.where)
    .toEqual({ op: "eq", left: { column: `i.${INVOICE_ID}` }, right: { param: "customerId" } });

  // The Databases explorer lists it under main › billing › Queries; the inspector summarises it and deletes it.
  await workspace(page, "Databases");
  const tree = page.getByRole("complementary", { name: "Explorer" }).getByTestId("explorer-tree");
  await expect(tree.getByTestId("explorer-folder-Queries")).toBeVisible();
  const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();
  if ((await tree.getByTestId("explorer-folder-Queries").getAttribute("aria-expanded")) !== "true")
    await chevron(tree.getByTestId("explorer-folder-Queries")).click();
  await tree.getByTestId("explorer-row-CustomerInvoices").click();
  await expect(page.getByTestId("inspector-title")).toHaveText("CustomerInvoices");
  await expect(page.getByTestId("inspector").getByTestId("query-fields")).toContainText("Each row is Invoice; 2 sources; 1 parameter; 0 collections");
  await page.getByRole("button", { name: "Delete CustomerInvoices" }).click();
  await expect(page.getByTestId("delete-plan-dialog")).toHaveCount(0);
  await expect(tree.getByTestId("explorer-row-CustomerInvoices")).toHaveCount(0);
  await expect.poll(() => queryDoc(page, "CustomerInvoices")).toBeNull();
});
