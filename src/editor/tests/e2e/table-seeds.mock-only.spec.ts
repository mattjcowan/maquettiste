// Table seeds (2026-10-07): a table file's own rows on the table editor's Data tab (the seed grid over the table's columns),
// and each seed's settings: its environments, insert once or keep as the model has it, deleting rows it does not hold, the row
// key, and rows kept in a CSV file, each one save of the seed.
import type { Page } from "@playwright/test";
import { expect, test } from "./fixtures";

const editor = (page: Page) => page.getByTestId("element-editor");

type SeedJson = { id: string; target: string; columns: string[]; rows?: { id: string; values: unknown[] }[]; [k: string]: unknown };

/** The seeds of the table file named `name`. */
const seedsOf = (page: Page, name: string) =>
  page.evaluate(async (tableName) => {
    const index = (await (await fetch("/api/model/index")).json()) as { id: string; kind: string; name: string; target?: string }[];
    const table = index.find((r) => r.kind === "table" && r.name === tableName);
    const ids = index.filter((r) => r.kind === "seed" && r.target === table?.id).map((r) => r.id);
    if (!ids.length) return [];
    const read = await fetch("/api/model/elements/read", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ ids }) });
    return ((await read.json()) as { elements: { json: SeedJson }[] }).elements.map((d) => d.json);
  }, name);

test("a table's Data tab seeds the table in column terms, and the seed's settings say how its rows are applied", async ({ page }) => {
  await page.goto("/database");
  await page.getByTestId("database-table-notes").click();
  await page.getByTestId("table-inspector").getByTestId("table-inspector-edit").click();
  await expect(editor(page).getByTestId("editor-title")).toHaveText("notes");
  await editor(page).getByRole("tab", { name: "Data", exact: true }).click();

  // No seed yet: New seed creates one listing the columns the seed may give (all of them here; none is an identity).
  await page.getByTestId("new-seed").click();
  const grid = page.getByTestId("rows-grid");
  await expect(grid.getByRole("columnheader")).toHaveText(["Row", "id", "entity_type", "entity_id", "body", "created_at"]);
  await grid.focus();
  await page.keyboard.press("Control+Enter");
  await page.keyboard.press("Enter");
  await page.keyboard.type("0193a3c2-0000-7000-8000-000000000001");
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");
  await page.keyboard.type("invoice");
  await page.keyboard.press("Enter");
  await expect(page.getByTestId("rows-status")).toContainText("1 row");
  await expect.poll(async () => (await seedsOf(page, "notes"))[0]?.rows?.[0]?.values?.slice(0, 2)).toEqual(["0193a3c2-0000-7000-8000-000000000001", "invoice"]);

  // The seed's settings, under the grid.
  const settings = page.getByTestId("seed-settings");
  await settings.getByLabel("Environments of notes").fill("dev, test");
  await settings.getByLabel("Environments of notes").blur();
  await settings.getByLabel("How the rows of notes are applied").selectOption("converge");
  await settings.getByLabel("Delete rows the seed does not hold").check();
  await settings.getByLabel("Rows in a CSV file").check();
  await expect(settings.getByText("notes.csv")).toBeVisible();
  await expect
    .poll(async () => {
      const seed = (await seedsOf(page, "notes"))[0];
      return [seed?.environments, seed?.apply, seed?.delete, seed?.rowsFrom];
    })
    .toEqual([["dev", "test"], "converge", true, { file: "notes.csv" }]);
  await expect(settings.getByLabel("Row key of notes")).toHaveValue("");

  // Back to insert once: the delete flag goes with converge.
  await settings.getByLabel("How the rows of notes are applied").selectOption("once");
  await expect(settings.getByLabel("Delete rows the seed does not hold")).toHaveCount(0);
  await expect
    .poll(async () => {
      const seed = (await seedsOf(page, "notes"))[0];
      return [seed?.apply, seed?.delete];
    })
    .toEqual([undefined, undefined]);
});
