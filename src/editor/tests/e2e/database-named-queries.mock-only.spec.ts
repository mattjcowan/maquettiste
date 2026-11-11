// Named queries (WITH) on a built query: the Named queries (WITH) tab adds one, names it, switches Repeats on (a Repeating part
// panel with its own sources, select and filter, and a Union choice), and the query's own source picker lists it above the tables;
// the SQL tab shows WITH RECURSIVE for PostgreSQL and a plain WITH for SQL Server; a repeating part that does not read its own name
// shows its finding on its panel and the query has no SQL until it is fixed. On the Storage tab, a binding over a hand-written
// query that changes data says so in one line under the source picker.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");
const tab = (page: Page, name: string) => editor(page).getByRole("tab", { name, exact: true }).click();
const sqlText = (page: Page) => editor(page).getByTestId("query-sql-statement").getByTestId("code-text-sql");

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

const INVOICES_TABLE = "01J92P0V0FJ23CGSNKM7P1W5V7@01J92P0V1QRN2181XM2ZWE02W4";
const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";

test("a recursive named query: build it, see WITH RECURSIVE and WITH, a misplaced repeating part shows its finding", async ({ page }) => {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId("database-new-menu").click();
  await page.getByTestId("database-new-query").click();
  const dialog = page.getByTestId("new-database-object-dialog");
  await dialog.getByLabel("Name").fill("InvoiceChain");
  await dialog.getByLabel("From").selectOption(INVOICES_TABLE);
  await page.getByTestId("new-db-create").click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByTestId("editor-title")).toHaveText("InvoiceChain");

  // Named queries (WITH) sits right after Sources.
  const tabs = await editor(page).getByRole("tab").allTextContents();
  expect(tabs.indexOf("Named queries (WITH)")).toBe(tabs.indexOf("Sources") + 1);
  await tab(page, "Named queries (WITH)");
  await expect(editor(page).getByTestId("query-named")).toContainText("No named queries");
  await page.getByTestId("query-add-named").click();
  const card = page.getByTestId("query-named-1");
  await card.getByLabel("Named query 1 name").fill("chain");
  await card.getByLabel("Named query 1 name").press("Enter");
  await card.getByLabel("Named query 1 from source").selectOption(INVOICES_TABLE);
  await expect.poll(async () => ((await queryDoc(page, "InvoiceChain"))?.with as { name: string }[] | undefined)?.[0]?.name).toBe("chain");
  // A new named query starts on the table the query itself reads, so its anchor field already reads the invoices' id.
  await expect(card.getByLabel("Named query 1 from source").locator("option:checked")).toHaveText("billing.invoices");

  // Repeats: the repeating part starts out reading the named query itself, with the anchor's field names.
  await card.getByLabel("Named query 1 repeats").check();
  const part = page.getByTestId("query-named-1-repeating");
  await expect(part).toBeVisible();
  await expect(part.getByLabel("Named query 1 repeating from source").locator("option:checked")).toHaveText("chain (named query)");
  await expect(part.getByLabel("Named query 1 union")).toHaveValue("all");
  await expect(page.getByTestId("query-named-1-repeating-findings")).toHaveCount(0);

  // The query's own source picker lists Named queries above Tables and Views.
  await tab(page, "Sources");
  const from = editor(page).getByLabel("Query from source");
  await expect(from.locator("optgroup"))
    .toHaveText(/chain/, { useInnerText: true } as never)
    .catch(() => undefined);
  expect(await from.locator("optgroup").evaluateAll((groups) => groups.map((g) => g.getAttribute("label")))).toEqual(["Named queries", "Tables and views"]);
  await from.selectOption("chain");
  await tab(page, "Select");
  await editor(page).getByLabel("Field id: column").selectOption("i.id");
  await expect.poll(async () => (await queryDoc(page, "InvoiceChain"))?.from).toMatchObject({ source: "chain" });

  // SQL: WITH RECURSIVE for PostgreSQL, a plain WITH for SQL Server.
  await tab(page, "SQL");
  await expect(sqlText(page)).toContainText("WITH RECURSIVE chain (id) AS (");
  await expect(sqlText(page)).toContainText("UNION ALL");
  await expect(sqlText(page)).toContainText("FROM chain i");
  await editor(page).getByLabel("Dialect").selectOption("sqlserver");
  await expect(sqlText(page)).toContainText("WITH chain (id) AS (");
  await expect(sqlText(page)).not.toContainText("RECURSIVE");
  await editor(page).getByLabel("Dialect").selectOption("postgresql");

  // Union distinct writes UNION.
  await tab(page, "Named queries (WITH)");
  await part.getByLabel("Named query 1 union").selectOption("distinct");
  await tab(page, "SQL");
  await expect(sqlText(page)).toContainText("\n    UNION\n");

  // A repeating part that does not read its own name: its finding shows on the panel, and the SQL goes.
  await tab(page, "Named queries (WITH)");
  await part.getByLabel("Named query 1 repeating from source").selectOption(INVOICES_TABLE);
  await expect(page.getByTestId("query-named-1-repeating-findings")).toContainText("MQ4099");
  await expect(page.getByTestId("query-named-1-repeating-findings")).toContainText(
    "The repeating part of named query 'chain' of query 'InvoiceChain' does not read 'chain'",
  );
  await part.getByLabel("Named query 1 repeating from source").selectOption("chain");
  await expect(page.getByTestId("query-named-1-repeating-findings")).toHaveCount(0);

  // Renaming the named query rewrites the sources that read it.
  await card.getByLabel("Named query 1 name").fill("ancestors");
  await card.getByLabel("Named query 1 name").press("Enter");
  await expect.poll(async () => (await queryDoc(page, "InvoiceChain"))?.from).toMatchObject({ source: "ancestors" });
  await tab(page, "SQL");
  await expect(sqlText(page)).toContainText("WITH RECURSIVE ancestors (id) AS (");
  await expect(sqlText(page)).toContainText("FROM ancestors i");

  // Named queries move up and down (MQ4099 says to move one before the one that reads it).
  await tab(page, "Named queries (WITH)");
  await page.getByTestId("query-add-named").click();
  const names = async () => ((await queryDoc(page, "InvoiceChain"))?.with as { name: string }[] | undefined)?.map((e) => e.name);
  await expect.poll(names).toEqual(["ancestors", "named"]);
  await expect(page.getByRole("button", { name: "Move named query ancestors up" })).toBeDisabled();
  await page.getByRole("button", { name: "Move named query named up" }).click();
  await expect.poll(names).toEqual(["named", "ancestors"]);
  await page.getByRole("button", { name: "Move named query named down" }).click();
  await expect.poll(names).toEqual(["ancestors", "named"]);
});

test("a binding over a hand-written query that changes data says so under the source picker", async ({ page }) => {
  await openEditor(page);
  const saved = await page.evaluate(
    async ({ pkg, table }) => {
      const post = async (body: unknown) =>
        (
          (await (
            await fetch("/api/model/elements", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })
          ).json()) as { outcome: string }
        ).outcome;
      const first = await post({
        kind: "query",
        id: "01K7NQY0000000000000000001",
        name: "VoidInvoices",
        database: "01J92P0V1QRN2181XM2ZWE02W4",
        sql: { "*": "UPDATE billing.invoices SET status = 'V' WHERE id = :id" },
        parameters: [{ name: "id", type: "uuid" }],
        writes: [table],
      });
      const second = await post({
        kind: "entity",
        id: "01K7NQY0000000000000000002",
        name: "VoidNote",
        package: pkg,
        key: { attributes: ["01K7NQY0000000000000000003"], strategy: "uuid-v7" },
        attributes: [{ id: "01K7NQY0000000000000000003", name: "id", type: "uuid", required: true }],
      });
      return [first, second];
    },
    { pkg: BILLING, table: INVOICES_TABLE },
  );
  expect(saved).toEqual(["saved", "saved"]);
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill("VoidNote");
  await side.getByTestId("explorer-row-VoidNote").dblclick();
  const entity = page.getByRole("region", { name: "Editor: VoidNote" });
  await entity.getByRole("tab", { name: "Storage" }).click();
  const storage = entity.getByTestId("storage-tab");
  await storage.getByTestId("storage-add-binding").click();
  const add = page.getByTestId("add-binding-dialog");
  await add.getByLabel("Source").selectOption({ label: "VoidInvoices" });
  await add.getByTestId("add-binding-apply").click();
  const card = storage.getByTestId("binding-card");
  await expect(card.getByTestId("binding-changes-data")).toHaveText("This query changes data; an entity can only read through a query that returns rows.");
  await expect(card.getByTestId("binding-problems").locator('[data-rule="MQ4044"]')).toContainText(
    "reads query 'VoidInvoices', which is written as SQL text and writes tables: an entity reads only through a query that returns rows and changes none.",
  );
});
