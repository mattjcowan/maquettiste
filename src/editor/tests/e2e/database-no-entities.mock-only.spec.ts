// The database side knows no entities (the owner, 2026-10-03: "completely remove the entity from the database portion and treat
// the database as its own thing"). A walk through the Databases workspace (the explorer with a table expanded, the Database
// screen and its diagram, the inspector and its Used tab, every tab of a table's editor, the New database and New query dialogs)
// reads what each shows and finds no entity's name and no entity or mapping wording. Column names such as entity_id are the
// database's own words and pass (the wording check is on whole words).
import type { Locator, Page } from "@playwright/test";
import { expect, test, workspace } from "./fixtures";

const WORDING = /\b(entit(y|ies)|mapp(ing|ings|ed)|map to database)\b/i;

/** The entities' names, from the index (Customer, Invoice, InvoiceLine, …). */
const entityNames = (page: Page) =>
  page.evaluate(async () => {
    const index = (await (await fetch("/api/model/index")).json()) as { kind: string; name: string }[] | { elements: { kind: string; name: string }[] };
    return (Array.isArray(index) ? index : index.elements).filter((r) => r.kind === "entity").map((r) => r.name);
  });

/** Asserts that `text` says nothing of entities. */
function expectNoEntitiesIn(text: string, names: readonly string[], label: string): void {
  expect(text.match(WORDING)?.[0] ?? null, `${label}: entity or mapping wording`).toBeNull();
  const named = names.filter((n) => new RegExp(`\\b${n}\\b`).test(text));
  expect(named, `${label}: entity names`).toEqual([]);
}

/** Asserts that `where` (its text and its tooltips) says nothing of entities. */
async function expectNoEntities(where: Locator, names: readonly string[], label: string): Promise<void> {
  await expect(where).toBeVisible();
  const text = await where.evaluate((el) =>
    [
      (el as HTMLElement).innerText,
      ...Array.from(el.querySelectorAll("[title],[aria-label]")).map((x) => `${x.getAttribute("title") ?? ""} ${x.getAttribute("aria-label") ?? ""}`),
    ].join("\n"),
  );
  expectNoEntitiesIn(text, names, label);
}

test("the Databases workspace shows no entity and no mapping anywhere", async ({ page }) => {
  await page.goto("/database");
  await expect(page.getByTestId("database-workspace")).toBeVisible();
  await workspace(page, "Databases");
  const names = await entityNames(page);
  expect(names).toEqual(expect.arrayContaining(["Customer", "Invoice"]));

  // The explorer, with the database, its schema, its tables and one table's parts expanded.
  const side = page.getByRole("complementary", { name: "Explorer" });
  const tree = side.getByTestId("explorer-tree");
  const expand = async (row: Locator) => {
    if ((await row.getAttribute("aria-expanded")) !== "true") await row.locator("span[aria-hidden]").first().click();
  };
  await expand(tree.getByTestId("explorer-folder-main"));
  await expand(tree.getByTestId("explorer-folder-billing"));
  await expand(tree.getByTestId("explorer-folder-Tables").first());
  const customersRow = tree.locator('[data-testid$="-customers"]').first();
  await expand(customersRow);
  await expect(tree.getByTestId("explorer-folder-Columns").first()).toBeVisible();
  await expectNoEntities(side, names, "explorer");

  // The Database screen: the diagram's cards, the tables list, the toolbar.
  await expect(page.locator(".react-flow__node").first()).toBeVisible();
  await expectNoEntities(page.getByTestId("database-workspace"), names, "Database screen");

  // A laid-out table (customers) and a table file two entities are bound to (notes): the inspector, its Used tab (only what of
  // the database uses the table; the bindings show on the entities' Storage tabs) and every tab of the table's editor.
  for (const table of ["customers", "notes"]) {
    await tree.locator(`[data-testid$="-${table}"]`).first().click();
    const inspector = page.getByTestId("table-inspector");
    await expect(inspector.getByTestId("inspector-title")).toHaveText(table);
    await expectNoEntities(inspector, names, `${table} inspector`);
    await inspector.getByRole("tab", { name: "Used", exact: true }).click();
    await expect(inspector.getByText(table === "notes" ? "Nothing references this element" : "Nothing uses this table's file")).toBeVisible();
    await expectNoEntities(inspector, names, `${table} inspector Used`);
    await inspector.getByRole("tab", { name: "Properties", exact: true }).click();
    await inspector.getByTestId("table-inspector-edit").click();
    const editor = page.getByTestId("element-editor");
    await expect(editor).toHaveAttribute("data-kind", "table");
    await expect(page.getByTestId("editor-title")).toHaveText(table);
    const tabs = await editor.getByRole("tab").allTextContents();
    expect(tabs.length).toBeGreaterThan(5);
    expect(tabs).toContain("DDL");
    for (const name of tabs) {
      await editor.getByRole("tab", { name, exact: true }).click();
      await expect(editor.getByRole("tab", { name, exact: true })).toHaveAttribute("data-state", "active");
      if (name === "References" && table === "notes") await expect(editor.getByText("Nothing references this element")).toBeVisible();
      await expectNoEntities(editor, names, `${table} editor, ${name}`);
      // The generated script whole (the code view paints only the lines in view): it names the table and its database only.
      if (name === "DDL") {
        const ddl = editor.getByTestId("table-ddl");
        await expect(ddl).toHaveAttribute("data-text", /CREATE TABLE/);
        expectNoEntitiesIn((await ddl.getAttribute("data-text")) ?? "", names, `${table} editor, DDL script`);
      }
    }
  }

  // New database and New query: nothing about domains, mapping or entities.
  await side.getByTestId("explorer-new").click();
  await page.getByRole("menuitem", { name: "New database", exact: true }).click();
  const newDatabase = page.getByRole("dialog");
  await expect(newDatabase.getByTestId("new-element-dialog")).toHaveAttribute("data-kind", "database");
  await expectNoEntities(newDatabase, names, "New database");
  await expect(newDatabase).not.toContainText(/domain/i);
  await page.keyboard.press("Escape");
  await expect(newDatabase).toHaveCount(0);

  await tree.getByTestId("explorer-folder-main").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "New query…" }).click();
  const newQuery = page.getByRole("dialog");
  await expect(newQuery.getByTestId("new-database-object-dialog")).toHaveAttribute("data-kind", "query");
  await expectNoEntities(newQuery, names, "New query");
  await page.keyboard.press("Escape");
  await expect(newQuery).toHaveCount(0);
});
