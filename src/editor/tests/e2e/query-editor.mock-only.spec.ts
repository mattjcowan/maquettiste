// The query editor on the billing fixture's InvoicesByCustomer: the Database screen lists it under Queries with its SQL, the
// inspector's Show the SQL opens it on the SQL tab (the statement, the lines collection's statement), and the tabs show its
// sources, select list, filter, collection and paging; the Parameters tab edits the limit's default (one save); a column the
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
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId("database-list-kind-query").click();
  await expect(page.getByTestId("database-objects-count")).toHaveText("3 queries");
  await page.getByTestId("database-query-InvoicesByCustomer").click();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("query SQL · InvoicesByCustomer");
  await expect(page.getByTestId("ddl-preview").getByTestId("code-text-sql")).toContainText("LIMIT @limit OFFSET @offset");
  await expect(page.getByTestId("inspector").getByTestId("query-fields")).toContainText("Each row has 6 fields; 1 source; 4 parameters; 1 collection");

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
  await expect(editor(page).getByTestId("query-sql-collection-lines").getByTestId("code-text-sql")).toContainText(
    "SELECT l.id AS id, l.quantity AS quantity, l.description AS description, l.invoice_id AS mq_key0\nFROM billing.invoice_lines l\nWHERE l.invoice_id IN @mq_keys0\nORDER BY l.id ASC",
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
  await expect(editor(page).getByTestId("query-attribute-total")).toHaveCount(0);
  await expect(editor(page).getByTestId("query-attribute-updatedAt")).toContainText("Select updatedAt");
  // A value object's members and a to-one relationship's foreign key are rows too.
  await expect(editor(page).getByTestId("query-attribute-total.amount")).toContainText("Select total.amount");
  await expect(editor(page).getByTestId("query-attribute-customer (foreign key to Customer)")).toContainText("Select customer (foreign key to Customer)");
  await tab(page, "Filter");
  await expect(checked(page, "Where: group")).toHaveText("All of (and)");
  await expect(checked(page, "Where 1 left: column")).toHaveText("i.customer_id");
  await expect(editor(page).getByLabel("Where 1 right: parameter")).toHaveValue("customerId");
  await expect(editor(page).getByLabel("Where 2 operator")).toHaveValue("in");
  await expect(editor(page).getByLabel("Where 2 value 1: parameter")).toHaveValue("statuses");
  await expect(editor(page).getByLabel("Where 3 operator")).toHaveValue("isNull");
  await expect(checked(page, "Where 3 left: column")).toHaveText("i.deleted_at");
  await tab(page, "Collections");
  const lines = editor(page).getByTestId("query-collection-1");
  await expect(lines).toContainText("lines");
  await expect(checked(page, "Collection 1 fills")).toHaveText(/^lines/);
  await expect(checked(page, "Collection 1 from source")).toHaveText("billing.invoice_lines");
  await expect(checked(page, "Collection 1 where left: column")).toHaveText("l.invoice_id");
  await expect(checked(page, "Collection 1 where right: column")).toHaveText("i.id");
  await expect(lines.getByTestId("query-collection-keys")).toHaveText("Tied to the parent row by its id.");
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

/** Another session saves the query's select list. */
const setSelect = (page: Page, select: unknown[]) =>
  page.evaluate(
    async ({ id, select }) => {
      const doc = (await (await fetch(`/api/model/elements/${id}`)).json()) as { json: Record<string, unknown>; hash: string };
      const res = await fetch(`/api/model/elements/${id}`, {
        method: "PUT",
        headers: { "content-type": "application/json", "If-Match": `"${doc.hash}"` },
        body: JSON.stringify({ ...doc.json, select }),
      });
      return ((await res.json()) as { outcome: string }).outcome;
    },
    { id: QUERY, select },
  );

test("a field the entity does not have is listed with Remove, and another result entity keeps every field's value under a name", async ({ page }) => {
  await page.goto("/database");
  await page.getByTestId("database-list-kind-query").click();
  await page.getByTestId("database-query-InvoicesByCustomer").click();
  await page.getByTestId("query-open-sql").click();
  await expect(page.getByTestId("editor-title")).toHaveText("InvoicesByCustomer");
  const select = (await queryJson(page)).select as { attribute?: string; name?: string; expression: unknown }[];
  // An invoice line's attribute, which Invoice does not have (a hand edit, or another session's).
  expect(await setSelect(page, [...select, { attribute: "01J92P0V0X1EDKMF6X5RA8NZWG", expression: { column: "i.notes" } }])).toBe("saved");
  await tab(page, "Select");
  const unknown = editor(page).getByTestId("query-field-unknown");
  await expect(unknown).toHaveText("01J92P0V0X1EDKMF6X5RA8NZWG (not in the shape)");
  await editor(page).getByRole("button", { name: "Remove field 01J92P0V0X1EDKMF6X5RA8NZWG", exact: true }).click();
  await expect.poll(async () => ((await queryJson(page)).select as unknown[]).length).toBe(select.length);
  await expect(unknown).toHaveCount(0);

  // A query is the database's: General says the older result entity is set, and Remove turns every field that filled an
  // attribute into a field of its own name (its expression kept), and the lines collection into a collection named lines.
  await tab(page, "General");
  await expect(editor(page).getByLabel("Result entity")).toHaveCount(0);
  const older = editor(page).getByTestId("query-older-result-entity");
  await expect(older).toHaveText(/An older result entity is set; bind the entity to this query on its Storage tab instead\./);
  await older.getByRole("button", { name: "Remove" }).click();
  await expect
    .poll(async () => ((await queryJson(page)).select as { attribute?: string; name?: string }[]).map((f) => [f.attribute ?? null, f.name]))
    .toEqual([
      [null, "id"],
      [null, "number"],
      [null, "issuedOn"],
      [null, "status"],
      [null, "notes"],
      [null, "createdAt"],
    ]);
  const json = await queryJson(page);
  expect(json.entity).toBeUndefined();
  const collection = (json.collections as { attribute: string; entity?: string; query: { select: { attribute?: string; name?: string }[] } }[])[0];
  expect(collection.attribute).toBe("lines");
  expect(collection.entity).toBeUndefined();
  expect(collection.query.select.map((f) => [f.attribute ?? null, f.name])).toEqual([
    [null, "id"],
    [null, "quantity"],
    [null, "description"],
  ]);
  await expect(older).toHaveCount(0);
  // Nothing about entities is left in the editor; the select list is the row, and the SQL is the same statement.
  await tab(page, "Select");
  await expect(editor(page).getByTestId("query-field-number")).toBeVisible();
  await expect(editor(page).getByTestId("query-field-unknown")).toHaveCount(0);
  await tab(page, "Collections");
  await expect(editor(page).getByLabel("Collection 1 element entity")).toHaveCount(0);
  await expect(editor(page).getByLabel("Collection 1 name")).toHaveValue("lines");
  await expect(editor(page)).not.toContainText(/entit/i);
  await tab(page, "SQL");
  await expect(editor(page).getByTestId("query-sql-statement").getByTestId("code-text-sql")).toContainText(
    "SELECT i.id AS id, i.number AS number, i.issued_on AS issuedOn, i.status AS status, i.notes AS notes, i.created_at AS createdAt",
  );
  // One undo puts the result entity back.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(async () => (await queryJson(page)).entity).toBe(INVOICE);
});
