// A table's seed and a relation's mapping lead to the editor that holds what they say: the seed is not listed under a domain
// (a table belongs to a schema) and its inspector opens the table's Data tab; the mapping's inspector opens the relation's
// Storage tab.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

type Row = { id: string; kind: string; name: string; target?: string };

async function index(page: Page): Promise<Row[]> {
  return page.evaluate(async () => (await (await fetch("/api/model/index")).json()) as Row[]);
}

async function create(page: Page, element: Record<string, unknown>) {
  const out = await page.evaluate(
    async (body) => {
      const res = await fetch("/api/model/batch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      return (await res.json()) as { outcome: string; diagnostics?: { message: string }[]; [k: string]: unknown };
    },
    { operations: [{ op: "create", element }] },
  );
  expect(out.outcome, JSON.stringify(out)).toBe("saved");
}

/** Selects an element through the URL without reloading (the mock's model lives in the page). */
const select = (page: Page, url: string) =>
  page.evaluate((to) => {
    window.history.pushState({}, "", to);
    window.dispatchEvent(new PopStateEvent("popstate"));
  }, url);

test("a table seed is not listed under a domain, and its inspector opens the table's Data tab", async ({ page }) => {
  await openEditor(page, "/entities");
  const rows = await index(page);
  const table = rows.find((r) => r.kind === "table" && r.name === "notes")!;
  const column = await page.evaluate(async (id) => {
    const res = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: [id] }),
    });
    return ((await res.json()) as { elements: { json: { columns: { id: string }[] } }[] }).elements[0].json.columns[0].id;
  }, table.id);
  await create(page, { kind: "seed", id: "01J92P0VZZ0000000000000501", name: "notes_seed", target: table.id, columns: [column], rows: [] });
  await select(page, "/entities?sel=01J92P0VZZ0000000000000501");
  await expect(page.getByTestId("inspector-edit-rows")).toBeVisible();
  await expect(page.getByText("Rows are edited in the table editor.")).toBeVisible();

  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill("notes_seed");
  await expect(side.getByTestId("explorer-row-notes_seed")).toHaveCount(0);

  await page.getByTestId("inspector-edit-rows").click();
  const editor = page.getByTestId("element-editor");
  await expect(editor.getByTestId("editor-title")).toHaveText("notes");
  await expect(editor.getByRole("tab", { name: "Data", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("button", { name: "Import CSV" })).toBeVisible();
});

test("a relation's mapping opens the relation's Storage tab", async ({ page }) => {
  await openEditor(page, "/entities");
  const rows = await index(page);
  const relation = rows.find((r) => r.kind === "relation")!;
  const database = rows.find((r) => r.kind === "database")!;
  await create(page, { kind: "mapping", id: "01J92P0VZZ0000000000000502", name: "link mapping", database: database.id, relation: relation.id });
  await select(page, "/entities?sel=01J92P0VZZ0000000000000502");
  await expect(page.getByText("The foreign key is edited on the relationship's Storage tab.")).toBeVisible();
  await page.getByTestId("inspector-open-relation-storage").click();
  const editor = page.getByTestId("element-editor");
  await expect(editor.getByTestId("editor-title")).toHaveText(relation.name);
  await expect(editor.getByRole("tab", { name: "Storage", exact: true })).toHaveAttribute("aria-selected", "true");
});
