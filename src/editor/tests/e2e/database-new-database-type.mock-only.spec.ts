// New database type…: from the explorer's New menu with the database selected, an enum type is created with its labels and opens
// in its editor; the Definition tab adds a label and switches the kind (the section follows it), one save each, and the Dialects
// tab adds a definition for a dialect; the Database screen lists it under Types with its DDL, the inspector summarises it; undo
// takes the steps back, and the inspector's delete removes it through the delete plan.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");

const typeDoc = (page: Page, name: string) =>
  page.evaluate(async (wanted) => {
    const index = (await (await fetch("/api/model/index")).json()) as
      { elements?: { id: string; kind: string; name: string }[] } | { id: string; kind: string; name: string }[];
    const row = (Array.isArray(index) ? index : (index.elements ?? [])).find((r) => r.kind === "database-type" && r.name === wanted);
    if (!row) return null;
    const read = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: [row.id] }),
    });
    const [doc] = ((await read.json()) as { elements: { json: Record<string, unknown> }[] }).elements;
    return doc?.json ?? null;
  }, name);

test("New database type… creates an enum, its Definition edits labels and kind, it is listed and previewed, and the delete plan removes it", async ({
  page,
}) => {
  await openEditor(page);
  await workspace(page, "Databases");
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByTestId("explorer-tree").getByTestId("explorer-folder-main").click();
  await side.getByTestId("explorer-new").click();
  await page.getByTestId("explorer-new-database-type").click();

  const dialog = page.getByTestId("new-database-object-dialog");
  await expect(dialog).toHaveAttribute("data-kind", "database-type");
  await expect(dialog.getByLabel("Type kind")).toHaveValue("domain");
  await dialog.getByLabel("Type kind").selectOption("enum");
  await dialog.getByLabel("Name").fill("payment_method");
  await dialog.getByLabel("Members").fill("card, transfer, card");
  await expect(dialog.getByText("Each label appears once.")).toBeVisible();
  await expect(page.getByTestId("new-db-create")).toBeDisabled();
  await dialog.getByLabel("Members").fill("card, transfer");
  await page.getByTestId("new-db-create").click();
  await expect(dialog).toHaveCount(0);

  await expect(editor(page)).toHaveAttribute("data-kind", "database-type");
  await expect(page.getByTestId("editor-title")).toHaveText("payment_method");
  await editor(page).getByRole("tab", { name: "Definition" }).click();
  const definition = page.getByTestId("database-type-definition");
  await expect(definition).toHaveAttribute("data-type-kind", "enum");
  await expect(page.getByTestId("database-type-member-transfer")).toBeVisible();
  await page.getByTestId("database-type-add-member").click();
  await page.getByLabel("Label 3").fill("cash");
  await page.getByLabel("Label 3").press("Enter");
  await expect.poll(async () => (await typeDoc(page, "payment_method"))?.members).toEqual(["card", "transfer", "cash"]);

  // The Database screen: the Types chip lists it; the preview creates it in the whole database.
  await page.getByTestId("editor-tab-screen").click();
  await page.getByTestId("database-list-kind-database-type").click();
  await expect(page.getByTestId("database-objects-count")).toHaveText("3 database types");
  await page.getByTestId("database-database-type-payment_method").click();
  await expect(page.getByTestId("ddl-preview-unit")).toHaveText("sql-ddl/database-type · payment_method");
  await expect(page.getByTestId("ddl-preview")).toContainText("CREATE TYPE billing.payment_method AS ENUM ('card', 'transfer', 'cash');");
  await expect(page.getByTestId("inspector").getByTestId("database-type-fields")).toContainText("Enum: card, transfer, cash");

  // Switching the kind to a domain shows the domain's section, with a base type to start.
  await page.getByTestId("inspector").getByLabel("Type kind").selectOption("domain");
  await expect(page.getByTestId("inspector").getByTestId("database-type-fields")).toContainText("Domain over string");
  await expect.poll(async () => (await typeDoc(page, "payment_method"))?.members).toBeUndefined();
  // Undo brings the labels back.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect.poll(async () => (await typeDoc(page, "payment_method"))?.members).toEqual(["card", "transfer", "cash"]);

  // Delete through the plan: nothing refers to it, so it goes at once.
  await page.getByRole("button", { name: "Delete payment_method" }).click();
  await expect(page.getByTestId("delete-plan-dialog")).toHaveCount(0);
  await expect.poll(() => typeDoc(page, "payment_method")).toBeNull();
  await expect(page.getByTestId("database-database-type-payment_method")).toHaveCount(0);
});

test("a domain type's editor shows its base type and check, and the Dialects tab adds a definition", async ({ page }) => {
  await openEditor(page);
  await workspace(page, "Databases");
  const tree = page.getByRole("complementary", { name: "Explorer" }).getByTestId("explorer-tree");
  const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();
  const main = tree.getByTestId("explorer-folder-main");
  if ((await main.getAttribute("aria-expanded")) !== "true") await chevron(main).click();
  const billing = tree.getByTestId("explorer-folder-billing");
  if ((await billing.getAttribute("aria-expanded")) !== "true") await chevron(billing).click();
  await chevron(tree.getByTestId("explorer-folder-Types")).click();
  await tree.getByTestId("explorer-row-email_address").dblclick();
  await expect(editor(page)).toHaveAttribute("data-kind", "database-type");
  await editor(page).getByRole("tab", { name: "Definition" }).click();
  await expect(page.getByTestId("database-type-domain")).toBeVisible();
  await expect(editor(page).getByLabel("Base type", { exact: true })).toHaveValue("string");
  await expect(editor(page).getByLabel("Length", { exact: true })).toHaveValue("320");
  await expect(editor(page).getByLabel("Check", { exact: true })).toHaveValue("VALUE like '%@%'");

  await editor(page).getByRole("tab", { name: "Dialects" }).click();
  await page.getByTestId("database-type-add-dialect").click();
  await page.getByTestId("database-type-add-dialect-postgresql").click();
  await expect(page.getByTestId("database-type-body-postgresql")).toBeVisible();
  await expect.poll(async () => Object.keys((await typeDoc(page, "email_address"))?.definition ?? {})).toEqual(["postgresql"]);
});
