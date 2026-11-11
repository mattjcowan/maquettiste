// A query written as SQL text: New query… asks "Written as" and opens the SQL text tab with a SELECT starter; typing a placeholder
// that names no parameter marks it live (the mock's own scanner, no server call) and the finding shows on the Parameters row it is
// about; columns are declared, Find in SQL lists the tables the text names, a table the text does not name shows its finding on
// its row, and the SQL tab renders the text with its placeholders as `@name`.
import type { Page } from "@playwright/test";
import { expect, test } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");
const tab = (page: Page, name: string) => editor(page).getByRole("tab", { name, exact: true }).click();

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

test("a query written as SQL text: mistype a placeholder and see the mark, declare columns and tables, see the SQL preview", async ({ page }) => {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId("database-new-menu").click();
  await page.getByTestId("database-new-query").click();

  const dialog = page.getByTestId("new-database-object-dialog");
  await dialog.getByLabel("Name").fill("TopInvoices");
  await dialog.getByLabel("Written as").selectOption("sql");
  await expect(dialog.getByLabel("From")).toHaveCount(0);
  await page.getByTestId("new-db-create").click();
  await expect(dialog).toHaveCount(0);

  // It opens on the SQL text tab, with a SELECT starter; the built-query tabs are not there.
  await expect(page.getByTestId("editor-title")).toHaveText("TopInvoices");
  const body = page.getByTestId("sqltext-body-postgresql");
  await expect(body.getByTestId("code-text-sql")).toHaveText("SELECT 1 AS value");
  for (const hidden of ["Sources", "Select", "Filter", "Group and order", "Collections"])
    await expect(editor(page).getByRole("tab", { name: hidden, exact: true })).toHaveCount(0);
  for (const shown of ["General", "SQL text", "Parameters", "Columns", "Tables", "SQL"])
    await expect(editor(page).getByRole("tab", { name: shown, exact: true })).toBeVisible();
  await expect.poll(async () => (await queryDoc(page, "TopInvoices"))?.sql).toEqual({ postgresql: "SELECT 1 AS value" });
  // The inspector describes it as SQL text: its declared columns and tables, not a select list and sources.
  await expect(page.getByTestId("inspector").getByTestId("query-fields")).toContainText(
    "Written as SQL text. Each row has 1 column; reads 0 tables; 0 parameters.",
  );

  // Type the SQL, with the second placeholder mistyped: both are marked, and neither names a parameter yet.
  await body.locator(".view-lines").click();
  await page.keyboard.press("Control+A");
  await page.keyboard.type("SELECT i.id, i.number FROM billing.invoices i WHERE i.customer_id = :customerId AND i.status = :statuss");
  await expect(body.locator(".mq-sql-param")).toHaveCount(2);
  await expect(body.getByTestId("sqltext-unknown-parameters")).toContainText(":customerId, :statuss");
  await tab(page, "Parameters");
  await expect
    .poll(async () => (await queryDoc(page, "TopInvoices"))?.sql)
    .toEqual({
      postgresql: "SELECT i.id, i.number FROM billing.invoices i WHERE i.customer_id = :customerId AND i.status = :statuss",
    });

  // Parameters: the two, the second spelled as it should be; its row says the text never uses it.
  for (const name of ["customerId", "status"]) {
    await page.getByTestId("query-add-parameter").click();
    const row = editor(page)
      .getByLabel(/^Name of parameter \d+$/)
      .last();
    await row.fill(name);
    await row.press("Enter");
  }
  await editor(page).getByLabel("Type of parameter customerId").selectOption("uuid");
  await expect(page.getByTestId("query-parameter-finding-status")).toContainText("MQ4096");

  // Back on the SQL text tab only the mistyped one is underlined; fixing it clears the finding.
  await tab(page, "SQL text");
  await expect(body.getByTestId("sqltext-unknown-parameters")).toContainText(":statuss");
  await expect(body.getByTestId("sqltext-unknown-parameters")).not.toContainText(":customerId");
  await body.locator(".view-lines").click();
  await page.keyboard.press("Control+End");
  await page.keyboard.press("Backspace");
  await expect(body.getByTestId("sqltext-unknown-parameters")).toHaveCount(0);
  await tab(page, "Parameters");
  await expect(page.getByTestId("query-parameter-finding-status")).toHaveCount(0);

  // Columns: the starter's column renamed, another added, each with a type; a column can be null unless unticked.
  await tab(page, "Columns");
  await expect(editor(page).getByLabel("Name of column 1")).toHaveValue("value");
  await page.getByTestId("query-add-column").click();
  await editor(page).getByLabel("Name of column 1").fill("id");
  await editor(page).getByLabel("Name of column 1").press("Enter");
  await expect.poll(async () => ((await queryDoc(page, "TopInvoices"))?.columns as { name: string }[] | undefined)?.[0]?.name).toBe("id");
  await editor(page).getByLabel("Name of column 2").fill("number");
  await editor(page).getByLabel("Name of column 2").press("Enter");
  await expect.poll(async () => ((await queryDoc(page, "TopInvoices"))?.columns as { name: string }[] | undefined)?.[1]?.name).toBe("number");
  await editor(page).getByLabel("Type of column id").selectOption("uuid");
  await editor(page).getByLabel("Column id can be null").uncheck();
  await expect
    .poll(async () => (await queryDoc(page, "TopInvoices"))?.columns)
    .toEqual([
      { name: "id", type: "uuid", nullable: false },
      { name: "number", type: "string" },
    ]);

  // Tables: Find in SQL lists what the text names, unticked; the customers table the text never names shows its finding.
  await tab(page, "Tables");
  await page.getByTestId("query-find-in-sql").click();
  const found = page.getByTestId("query-found-in-sql");
  await expect(found).toContainText("billing.invoices");
  await expect(found.getByRole("checkbox", { name: /invoices/ })).not.toBeChecked();
  await expect(found).not.toContainText("customers");
  await found.getByRole("checkbox").first().check();
  await page.getByTestId("query-found-add").click();
  await expect(page.getByTestId("query-reads-billing.invoices")).toBeVisible();
  await page.getByTestId("query-reads-add").selectOption({ label: "billing.customers" });
  await expect(page.getByTestId("query-reads-problem-billing.customers")).toContainText("MQ4098");
  await expect(page.getByTestId("query-writes-add").locator("option", { hasText: "(view)" })).toHaveCount(0);

  // SQL: what packs receive, the placeholders as @name.
  await tab(page, "SQL");
  await expect(page.getByTestId("query-sql-statement").getByTestId("code-text-sql")).toHaveText(
    "SELECT i.id, i.number FROM billing.invoices i WHERE i.customer_id = @customerId AND i.status = @status",
  );
  await expect(page.getByTestId("query-sql-parameters")).toContainText("@customerId, @status");
});
