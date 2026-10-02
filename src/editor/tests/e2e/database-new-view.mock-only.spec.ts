// New view…: from the explorer's New menu with the database selected, a view is created with a body prefilled for the
// database's dialect and opens in its editor; its Body tab saves an edited body in one step when the editor loses focus, and
// Add dialect adds a body for another dialect; the Database screen lists it under Views, and the DDL preview renders it (the whole
// database while the pack's view unit writes nothing, the view's own script once objectScripts is on); undo takes the steps back.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");

/** The view's document, read through the API. */
const viewDoc = (page: Page, name: string) =>
  page.evaluate(async (wanted) => {
    const index = (await (await fetch("/api/model/index")).json()) as
      { elements?: { id: string; kind: string; name: string }[] } | { id: string; kind: string; name: string }[];
    const row = (Array.isArray(index) ? index : (index.elements ?? [])).find((r) => r.kind === "view" && r.name === wanted);
    if (!row) return null;
    // The batch read lists a view deleted meanwhile (an undo while polling) as missing instead of answering 404.
    const read = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: [row.id] }),
    });
    const [doc] = ((await read.json()) as { elements: { json: { body: Record<string, string> } }[] }).elements;
    return doc?.json ?? null;
  }, name);

test("New view… creates a view, its Body tab edits and adds dialect bodies, the DDL preview renders it, and undo takes each step back", async ({ page }) => {
  await openEditor(page);
  await workspace(page, "Databases");
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByTestId("explorer-tree").getByTestId("explorer-folder-main").click();
  await side.getByTestId("explorer-new").click();
  await page.getByTestId("explorer-new-view").click();

  const dialog = page.getByTestId("new-database-object-dialog");
  await expect(dialog).toHaveAttribute("data-kind", "view");
  await expect(dialog.getByLabel("Dialect")).toHaveValue("postgresql");
  await expect(dialog.getByLabel("Body")).toHaveValue("select 1 as id");
  await dialog.getByLabel("Name").fill("unpaid_invoices");
  await dialog.getByLabel("Body").fill("select id, number from billing.invoices where status = 1");
  await page.getByTestId("new-db-create").click();
  await expect(dialog).toHaveCount(0);

  await expect(editor(page)).toHaveAttribute("data-kind", "view");
  await expect(page.getByTestId("editor-title")).toHaveText("unpaid_invoices");
  await editor(page).getByRole("tab", { name: "Body" }).click();
  const body = page.getByTestId("view-body-postgresql");
  await expect(body).toBeVisible();
  await expect(body.getByTestId("code-text-sql")).toHaveText("select id, number from billing.invoices where status = 1");

  // Typing marks the body unsaved; leaving the editor saves it, once.
  await body.locator(".view-lines").click();
  await page.keyboard.press("Control+End");
  await page.keyboard.type(" and total > 0");
  await expect(body.getByText("Unsaved")).toBeVisible();
  await editor(page).getByRole("tab", { name: "Body" }).click();
  await expect(body.getByText("Unsaved")).toHaveCount(0);
  await expect
    .poll(async () => (await viewDoc(page, "unpaid_invoices"))?.body)
    .toEqual({
      postgresql: "select id, number from billing.invoices where status = 1 and total > 0",
    });

  // Add dialect: a body for every dialect, starting from the first one's text.
  await page.getByTestId("view-add-dialect").click();
  await page.getByTestId("view-add-dialect-*").click();
  await expect(page.getByTestId("view-body-*")).toBeVisible();
  await expect.poll(async () => Object.keys((await viewDoc(page, "unpaid_invoices"))?.body ?? {})).toEqual(["postgresql", "*"]);

  // The Database screen: the Views chip lists it; the preview renders the whole database (the pack's view unit writes the view's
  // own script only with objectScripts on), which creates the view.
  await page.getByTestId("editor-tab-screen").click();
  await page.getByTestId("database-list-kind-view").click();
  await page.getByTestId("database-view-unpaid_invoices").click();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("sql-ddl/view · unpaid_invoices");
  await expect(page.getByTestId("ddl-preview-fallback")).toContainText("writes no file for unpaid_invoices");
  await expect(page.getByTestId("ddl-preview")).toContainText("CREATE VIEW billing.unpaid_invoices AS");
  await expect(page.getByTestId("ddl-preview")).toContainText("and total > 0");
  // The inspector shows the view.
  await expect(page.getByTestId("view-fields")).toContainText("Body for postgresql, any dialect");

  // With objectScripts on, the view's own script.
  await page.evaluate(async () => {
    const settings = await fetch("/api/project/settings").then((r) => r.json());
    const section = settings.json.packs["sql-ddl"];
    await fetch("/api/project/settings/packs/sql-ddl", {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${settings.hash}"` },
      body: JSON.stringify({ ...section, parameters: { ...(section.parameters ?? {}), objectScripts: true } }),
    });
  });
  await expect(page.getByTestId("ddl-preview-fallback")).toHaveCount(0);
  await expect(page.getByTestId("ddl-preview")).toContainText("Generated by Maquettiste (sql-ddl/view)");

  // Undo: the added dialect, then the body edit, then the create.
  const undo = page.getByRole("button", { name: "Undo" });
  await undo.click();
  await expect.poll(async () => Object.keys((await viewDoc(page, "unpaid_invoices"))?.body ?? {})).toEqual(["postgresql"]);
  await undo.click();
  await expect.poll(async () => (await viewDoc(page, "unpaid_invoices"))?.body.postgresql).toBe("select id, number from billing.invoices where status = 1");
  await undo.click();
  await expect.poll(() => viewDoc(page, "unpaid_invoices")).toBeNull();
  await expect(page.getByTestId("database-view-unpaid_invoices")).toHaveCount(0);
});

test("a view's and a sequence's explorer rows open their editors", async ({ page }) => {
  await openEditor(page);
  await workspace(page, "Databases");
  const tree = page.getByRole("complementary", { name: "Explorer" }).getByTestId("explorer-tree");
  const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();
  const main = tree.getByTestId("explorer-folder-main");
  if ((await main.getAttribute("aria-expanded")) !== "true") await chevron(main).click();
  const billing = tree.getByTestId("explorer-folder-billing");
  if ((await billing.getAttribute("aria-expanded")) !== "true") await chevron(billing).click();
  await chevron(tree.getByTestId("explorer-folder-Views")).click();
  await tree.getByTestId("explorer-row-outstanding_invoices").dblclick();
  await expect(editor(page)).toHaveAttribute("data-kind", "view");
  await editor(page).getByRole("tab", { name: "Columns" }).click();
  await expect(page.getByTestId("view-column-issued_on")).toBeVisible();

  await chevron(tree.getByTestId("explorer-folder-Sequences")).click();
  await tree.getByTestId("explorer-row-invoice_number_seq").dblclick();
  await expect(editor(page)).toHaveAttribute("data-kind", "sequence");
  await editor(page).getByRole("tab", { name: "Definition" }).click();
  await expect(editor(page).getByLabel("Start", { exact: true })).toHaveValue("1000");
});
