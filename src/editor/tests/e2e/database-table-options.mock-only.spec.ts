// The table editor's Exclusions and Storage tabs (the PostgreSQL DDL features): a storage parameter with a name offered as it is
// typed, partitioning with a partition, and an exclusion constraint with two elements, each gesture one save of the table file.
import type { Page } from "@playwright/test";
import { expect, test } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");
const inspector = (page: Page) => page.getByTestId("table-inspector");

type TableDoc = { id: string; name?: string; origin?: string; [k: string]: unknown };

/** The table file named `name`, or undefined. */
const fileOf = (page: Page, name: string) =>
  page.evaluate(async (tableName) => {
    const index = (await (await fetch("/api/model/index")).json()) as { elements?: { id: string; kind: string }[] } | { id: string; kind: string }[];
    const rows = (Array.isArray(index) ? index : (index.elements ?? [])).filter((r) => r.kind === "table");
    const read = await fetch("/api/model/elements/read", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ ids: rows.map((r) => r.id) }),
    });
    return ((await read.json()) as { elements: { json: TableDoc }[] }).elements
      .map((d) => d.json)
      .find((t) => t.name === tableName && t.origin !== "synthesized");
  }, name);

async function openTableEditor(page: Page, name: string) {
  await page.goto("/database");
  await page.getByTestId(`database-table-${name}`).click();
  await inspector(page).getByTestId("table-inspector-edit").click();
  await expect(editor(page).getByTestId("editor-title")).toHaveText(name);
}

test("the Storage tab sets a storage parameter and partitions the table; the Exclusions tab adds an exclusion constraint", async ({ page }) => {
  await openTableEditor(page, "notes");
  const tab = (name: string) => editor(page).getByRole("tab", { name, exact: true });

  await tab("Storage").click();
  const storage = page.getByTestId("table-storage");
  await storage.getByTestId("property-bag-add").click();
  await storage.getByLabel("Key of the new parameter").fill("fillfactor");
  await storage.getByRole("textbox", { name: "Value of the new parameter" }).fill("90");
  await storage.getByRole("textbox", { name: "Value of the new parameter" }).press("Enter");
  await expect.poll(async () => (await fileOf(page, "notes"))?.storage).toEqual({ postgresql: { fillfactor: 90 } });

  await storage.getByLabel("Partitioned by").selectOption("list");
  // Partitioned by its first column, id, which the primary key holds: nothing to say.
  await expect.poll(async () => (await fileOf(page, "notes"))?.partitionBy).toEqual({ strategy: "list", columns: ["01K6BND0000000000000000002"] });
  await expect(page.getByTestId("partition-key-problem")).toHaveCount(0);
  await page.getByTestId("partition-add").click();
  await expect.poll(async () => ((await fileOf(page, "notes"))?.partitions as { name: string }[] | undefined)?.map((p) => p.name)).toEqual(["notes_p1"]);
  const bounds = page.getByRole("textbox", { name: "Bounds of partition 1" });
  await bounds.fill("IN ('invoice')");
  await bounds.press("Enter");
  await expect.poll(async () => ((await fileOf(page, "notes"))?.partitions as { bounds?: string }[] | undefined)?.[0]?.bounds).toBe("IN ('invoice')");

  await tab("Exclusions").click();
  await page.getByTestId("exclusion-add").click();
  const exclusion = page.getByTestId("exclusion-0");
  await expect(exclusion).toBeVisible();
  await exclusion.getByRole("button", { name: "Add column" }).click();
  await exclusion.getByRole("textbox", { name: "Operator of element 2" }).fill("<>");
  await exclusion.getByRole("textbox", { name: "Operator of element 2" }).press("Enter");
  await expect(page.getByTestId("exclusion-0-sql")).toContainText("WITH <>");
  await expect
    .poll(async () =>
      (((await fileOf(page, "notes"))?.exclusions as { elements: { operator: string }[] }[] | undefined)?.[0]?.elements ?? []).map((e) => e.operator),
    )
    .toEqual(["=", "<>"]);
});
