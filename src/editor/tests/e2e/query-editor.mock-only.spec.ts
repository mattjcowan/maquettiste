// The query editor on the billing fixture's InvoicesByCustomer: the Database screen lists it under Queries with its SQL, the
// inspector's Show the SQL opens it on the SQL tab (the statement), and the tabs show its sources, select list, filter and
// paging (a query has no collections tab); the Parameters tab edits the limit's default (one save); a column the
// query reads goes away (the Invoice entity loses its soft-delete stereotype, so invoices.deleted_at is gone) and the problem
// shows in the SQL tab and in Problems, then the editor fixes it (the condition removed) and the SQL renders again.
import type { Page } from "@playwright/test";
import { expect, test, keepDdlOpen } from "./fixtures";

// The DDL preview is hidden by default; these specs read it.
test.beforeEach(({ page }) => keepDdlOpen(page));

const editor = (page: Page) => page.getByTestId("element-editor");
const tab = (page: Page, name: string) => editor(page).getByRole("tab", { name, exact: true }).click();
const checked = (page: Page, label: string) => editor(page).getByLabel(label, { exact: true }).locator("option:checked");

const QUERY = "01K6QRY0000000000000000001";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";

const queryJson = (page: Page) =>
  page.evaluate(async (id) => ((await (await fetch(`/api/model/elements/${id}`)).json()) as { json: Record<string, unknown> }).json, QUERY);

/** Another session (an agent, a teammate) edits the Invoice entity: its stereotypes become `stereotypes`. */
const setInvoiceStereotypes = (page: Page, stereotypes: string[]) =>
  page.evaluate(
    async ({ id, stereotypes }) => {
      const doc = (await (await fetch(`/api/model/elements/${id}`)).json()) as { json: Record<string, unknown>; hash: string };
      const res = await fetch(`/api/model/elements/${id}`, {
        method: "PUT",
        headers: { "content-type": "application/json", "If-Match": `"${doc.hash}"` },
        body: JSON.stringify({ ...doc.json, stereotypes }),
      });
      return ((await res.json()) as { outcome: string }).outcome;
    },
    { id: INVOICE, stereotypes },
  );

test("the billing InvoicesByCustomer: its tabs, its SQL, a default edited, and a removed column's problem fixed in the editor", async ({ page }) => {
  await page.goto("/database?mock=projected");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId("database-list-kind-query").click();
  await expect(page.getByTestId("database-objects-count")).toHaveText("3 queries");
  await page.getByTestId("database-query-InvoicesByCustomer").click();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("query SQL · InvoicesByCustomer");
  await expect(page.getByTestId("ddl-preview").getByTestId("code-text-sql")).toContainText("LIMIT @limit OFFSET @offset");
  await expect(page.getByTestId("inspector").getByTestId("query-fields")).toContainText("Each row has 6 fields; 1 source; 4 parameters.");

  // Show the SQL: the editor opens on its SQL tab.
  await page.getByTestId("query-open-sql").click();
  await expect(editor(page)).toHaveAttribute("data-kind", "query");
  await expect(page.getByTestId("editor-title")).toHaveText("InvoicesByCustomer");
  await expect(editor(page).getByRole("tab", { name: "SQL", exact: true })).toHaveAttribute("data-state", "active");
  const statement = editor(page).getByTestId("query-sql-statement").getByTestId("code-text-sql");
  await expect(statement).toContainText(
    [
      "SELECT i.id AS id, i.number AS number, i.issued_on AS issuedOn, i.status AS status, i.notes AS notes, i.created_at AS createdAt",
      "FROM billing.invoices i",
      "WHERE i.customer_id = @customerId AND i.status IN @statuses AND i.deleted_at IS NULL",
      "ORDER BY i.issued_on DESC, i.number ASC",
      "LIMIT @limit OFFSET @offset",
    ].join("\n"),
  );
  await expect(editor(page).getByTestId("query-sql-parameters")).toHaveText("Parameters: @customerId, @statuses, @offset, @limit");
  await editor(page).getByLabel("Dialect").selectOption("sqlserver");
  await expect(statement).toContainText("ORDER BY i.issued_on DESC, i.number ASC\nOFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY");

  // The other tabs show the query as data.
  await tab(page, "Sources");
  await expect(checked(page, "Query from source")).toHaveText("billing.invoices");
  await expect(editor(page).getByLabel("Query from alias")).toHaveValue("i");
  await tab(page, "Select");
  await expect(checked(page, "Field number: column")).toHaveText("i.number");
  await expect(checked(page, "Field createdAt: column")).toHaveText("i.created_at");
  await expect(editor(page).getByLabel("Name of field 1")).toHaveValue("id");
  await tab(page, "Filter");
  await expect(checked(page, "Where: group")).toHaveText("All of (and)");
  await expect(checked(page, "Where 1 left: column")).toHaveText("i.customer_id");
  await expect(editor(page).getByLabel("Where 1 right: parameter")).toHaveValue("customerId");
  await expect(editor(page).getByLabel("Where 2 operator")).toHaveValue("in");
  await expect(editor(page).getByLabel("Where 2 value 1: parameter")).toHaveValue("statuses");
  await expect(editor(page).getByLabel("Where 3 operator")).toHaveValue("isNull");
  await expect(checked(page, "Where 3 left: column")).toHaveText("i.deleted_at");
  await expect(editor(page).getByRole("tab", { name: "Collections", exact: true })).toHaveCount(0);
  await tab(page, "Group and order");
  await expect(editor(page).getByLabel("Paging limit", { exact: true })).toHaveValue("param");
  await expect(editor(page).getByLabel("Paging limit parameter")).toHaveValue("limit");
  await expect(editor(page).getByLabel("Query order 1 direction")).toHaveValue("desc");

  // Parameters: the limit's default, one save.
  await tab(page, "Parameters");
  await expect(editor(page).getByLabel("Default of parameter limit")).toHaveValue("50");
  await editor(page).getByLabel("Default of parameter limit").fill("25");
  await editor(page).getByLabel("Default of parameter limit").press("Enter");
  await expect
    .poll(async () => ((await queryJson(page)).parameters as { name: string; default?: unknown }[])[3])
    .toEqual({ name: "limit", type: "int32", default: 25 });
  await expect(page.getByTestId("editor-save-status")).toHaveText("Saved");

  // A column the query reads goes away: Invoice loses its soft-delete stereotype, and with it invoices.deleted_at.
  expect(await setInvoiceStereotypes(page, ["aggregate-root", "audited"])).toBe("saved");
  await tab(page, "SQL");
  const problems = editor(page).getByTestId("query-sql-problems");
  await expect(problems).toContainText("MQ4023");
  await expect(problems).toContainText("which has no such column");
  await expect(problems).toContainText("/where/and/2/left/column");
  await page.getByRole("tab", { name: /Problems/ }).click();
  await expect(page.getByTestId("problems-list").getByTestId("problem-MQ4023")).toBeVisible();

  // Fixed in the editor: the condition on the removed column goes, and the SQL renders again.
  await tab(page, "Filter");
  await editor(page).getByRole("button", { name: "Remove Where 3", exact: true }).click();
  await expect.poll(async () => ((await queryJson(page)).where as { and: unknown[] }).and.length).toBe(2);
  await tab(page, "SQL");
  await expect(problems).toHaveCount(0);
  await expect(statement).toContainText("WHERE i.customer_id = @customerId AND i.status IN @statuses\nORDER BY");
  await expect(page.getByTestId("problems-list").getByTestId("problem-MQ4023")).toHaveCount(0);
});
