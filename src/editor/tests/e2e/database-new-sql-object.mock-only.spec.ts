// New SQL object…: from the Database screen's New menu, a trigger is created with its statements and opens in its editor; the
// Definition tab sets when it runs and what it depends on, one save each; the Database screen lists it under Objects with its
// DDL, the explorer under Objects and the inspector summarises it. Deleting what it depends on shows the delete plan, which
// clears the reference; undo brings both back.
import type { Page } from "@playwright/test";
import { expect, test, workspace } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");

const objectDoc = (page: Page, kind: string, name: string) =>
  page.evaluate(
    async ([wantedKind, wanted]) => {
      const index = (await (await fetch("/api/model/index")).json()) as
        { elements?: { id: string; kind: string; name: string }[] } | { id: string; kind: string; name: string }[];
      const row = (Array.isArray(index) ? index : (index.elements ?? [])).find((r) => r.kind === wantedKind && r.name === wanted);
      if (!row) return null;
      const read = await fetch("/api/model/elements/read", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ ids: [row.id] }),
      });
      const [doc] = ((await read.json()) as { elements: { json: Record<string, unknown> }[] }).elements;
      return doc?.json ?? null;
    },
    [kind, name] as const,
  );

test("New SQL object… creates a trigger, Definition sets its phase and dependency, it is listed and previewed, and deleting its dependency clears it", async ({
  page,
}) => {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await page.getByTestId("database-new-menu").click();
  await page.getByTestId("database-new-sql-object").click();

  const dialog = page.getByTestId("new-database-object-dialog");
  await expect(dialog).toHaveAttribute("data-kind", "sql-object");
  await expect(dialog.getByLabel("Object kind")).toHaveValue("trigger");
  await expect(dialog.getByLabel("Runs")).toHaveValue("after");
  await dialog.getByLabel("Name").fill("invoices_touch");
  await dialog.getByLabel("Object kind").fill("");
  await expect(dialog.getByText("Say what the object is (trigger, grant, extension…).")).toBeVisible();
  await expect(page.getByTestId("new-db-create")).toBeDisabled();
  await dialog.getByLabel("Object kind").fill("trigger");
  await dialog
    .getByLabel("Body")
    .fill("create trigger invoices_touch before update on billing.invoices for each row execute function billing.invoice_total();");
  await page.getByTestId("new-db-create").click();
  await expect(dialog).toHaveCount(0);

  await expect(editor(page)).toHaveAttribute("data-kind", "sql-object");
  await expect(page.getByTestId("editor-title")).toHaveText("invoices_touch");
  await editor(page).getByRole("tab", { name: "Body" }).click();
  await expect(page.getByTestId("sql-object-body-postgresql")).toBeVisible();

  // Definition: what it depends on (the invoice_total routine), one save.
  await editor(page).getByRole("tab", { name: "Definition" }).click();
  const definition = page.getByTestId("sql-object-definition");
  await definition.getByLabel("Depends on").selectOption({ label: "invoice_total" });
  await expect(definition.getByTestId("depends-on-invoice_total")).toBeVisible();
  await expect.poll(async () => ((await objectDoc(page, "sql-object", "invoices_touch"))?.dependsOn as string[] | undefined)?.length).toBe(1);

  // The Database screen: the Objects chip lists it with the seeded grant; the preview renders the whole database with it.
  await page.getByTestId("editor-tab-screen").click();
  await page.getByTestId("database-list-kind-sql-object").click();
  await expect(page.getByTestId("database-objects-count")).toHaveText("2 SQL objects");
  await page.getByTestId("database-sql-object-invoices_touch").click();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("sql-ddl/sql-object · invoices_touch");
  await expect(page.getByTestId("ddl-preview")).toContainText("create trigger invoices_touch before update on billing.invoices");
  const inspector = page.getByTestId("inspector");
  await expect(inspector.getByTestId("sql-object-fields")).toContainText("Statements for postgresql; depends on 1 object.");
  // The inspector sets the phase: before the tables.
  await inspector.getByLabel("Runs").selectOption("before");
  await expect.poll(async () => (await objectDoc(page, "sql-object", "invoices_touch"))?.phase).toBe("before");

  // The explorer lists it under Objects.
  await workspace(page, "Databases");
  const tree = page.getByRole("complementary", { name: "Explorer" }).getByTestId("explorer-tree");
  const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();
  const folder = (name: string) => tree.getByTestId(`explorer-folder-${name}`);
  if ((await folder("Objects").getAttribute("aria-expanded")) !== "true") await chevron(folder("Objects")).click();
  await expect(tree.getByTestId("explorer-row-invoices_touch")).toBeVisible();

  // Deleting the routine it depends on: the plan clears the reference.
  if ((await folder("Routines").getAttribute("aria-expanded")) !== "true") await chevron(folder("Routines")).click();
  await tree.getByTestId("explorer-row-invoice_total").click();
  await expect(page.getByTestId("inspector-title")).toHaveText("invoice_total");
  await page.getByRole("button", { name: "Delete invoice_total" }).click();
  const plan = page.getByTestId("delete-plan-dialog");
  await expect(plan.getByTestId("delete-plan-clears")).toContainText("invoices_touch");
  await plan.getByTestId("delete-clear-references").click();
  await expect(plan).toHaveCount(0);
  await expect(tree.getByTestId("explorer-row-invoice_total")).toHaveCount(0);
  await expect.poll(async () => (await objectDoc(page, "sql-object", "invoices_touch"))?.dependsOn ?? []).toEqual([]);

  // One undo brings the routine and the reference back.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(tree.getByTestId("explorer-row-invoice_total")).toBeVisible();
  await expect.poll(async () => ((await objectDoc(page, "sql-object", "invoices_touch"))?.dependsOn as string[] | undefined)?.length).toBe(1);
});
