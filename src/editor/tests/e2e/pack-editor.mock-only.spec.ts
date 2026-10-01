// The Generate explorer and the pack editor (generation-ui.md 2 and 3, mock project): open a pack from the tree,
// read its units, edit a unit's output pattern (the example path and the MQ6020 collision follow) and save
// pack.json; create a pack from the explorer header and from the palette.
import AxeBuilder from "@axe-core/playwright";
import { expect, test, workspace } from "./fixtures";

test("open a pack, edit a unit's output pattern and save", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");
  const tree = page.getByRole("tree", { name: "Packs" });
  const pack = tree.getByTestId("pack-row-p:sql-ddl");
  await expect(pack).toContainText("7 units");

  // The tree reads the pack's units aloud before anything opens.
  await pack.getByText("sql-ddl", { exact: true }).click();
  await pack.press("ArrowRight");
  await expect(pack).toHaveAttribute("aria-expanded", "true");
  await tree.getByTestId("pack-row-p:sql-ddl/units").click();
  await expect(tree.getByTestId("pack-row-p:sql-ddl/u:table")).toContainText("table · each table → table.scriban → <database>/[<schema>/]tables/<table>.sql");
  await expect(tree.getByTestId("pack-row-p:sql-ddl/u:migration")).toContainText("Create only if missing");

  // The pack editor opens as a centre tab on Units.
  const editor = page.getByTestId("pack-editor");
  await expect(editor).toBeVisible();
  await expect(page.getByTestId("pack-tab-sql-ddl")).toHaveAttribute("aria-selected", "true");
  await expect(page.getByTestId("pack-editor-title")).toContainText("sql-ddl 1.0.0");
  const table = page.getByTestId("unit-row-table");
  await expect(table.getByTestId("unit-summary")).toHaveText("<database>/[<schema>/]tables/<table>.sql");
  await expect(table.getByTestId("unit-example")).toContainText(".sql");
  await expect(table.getByTestId("unit-count")).not.toHaveText("");

  // Scope help in plain words for the focused row.
  await page.getByTestId("unit-row-schema").getByLabel("Scope of unit schema").focus();
  await expect(page.getByTestId("scope-help")).toContainText("selector databases");

  // A constant output pattern: the summary follows, and every table claims the same path (MQ6020).
  const output = table.getByTestId("unit-output");
  await output.fill("all-tables.sql");
  await expect(table.getByTestId("unit-summary")).toHaveText("all-tables.sql");
  await expect(table.getByTestId("unit-example")).toContainText("all-tables.sql");
  await expect(table.getByTestId("unit-example")).toContainText("MQ6020");

  // Back to a per-table pattern, then save pack.json.
  await output.fill("{{ table.name }}.sql");
  await expect(table.getByTestId("unit-summary")).toHaveText("<table>.sql");
  await page.getByTestId("units-save").click();
  await expect(page.getByTestId("units-save")).toBeDisabled();
  const saved = await page.evaluate(() => fetch("/api/packs/sql-ddl").then((r) => r.json()));
  expect(saved.document.units[0].output).toBe("{{ table.name }}.sql");
  expect(saved.document.units[3].mode).toBe("regions");

  // The Parameters and Outputs tabs.
  await page.keyboard.press("Alt+2");
  await expect(page.getByTestId("parameters-tab")).toBeVisible();
  await expect(page.getByTestId("param-row-referenceStrategy")).toContainText("default");
  await page.keyboard.press("Alt+4");
  await expect(page.getByTestId("outputs-tab")).toBeVisible();

  const axe = await new AxeBuilder({ page }).include("[data-testid=pack-editor]").analyze();
  expect(axe.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);

  // The Plan tab is still there.
  await page.getByRole("tab", { name: "Plan" }).click();
  await expect(page.getByText("No plan yet")).toBeVisible();
});

test("create a pack from the explorer and from the palette", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");
  await page.getByTestId("new-pack").click();
  const dialog = page.getByTestId("new-pack-dialog");
  await expect(dialog).toBeVisible();
  await dialog.getByLabel("Name").fill("sql-ddl");
  await expect(page.getByTestId("new-pack-create")).toBeDisabled();
  await dialog.getByLabel("Name").fill("api-docs");
  await expect(page.getByTestId("new-pack-writes")).toHaveText("Creates .maquettiste/templates/api-docs/ with 2 files.");
  await expect(dialog.getByLabel("Start from").locator("option:checked")).toHaveText("Empty pack");
  await expect(page.getByTestId("new-pack-from-text")).toHaveText("One each-entity unit and its template, ready to edit.");
  await page.getByTestId("new-pack-create").click();
  await expect(page.getByTestId("pack-tab-api-docs")).toHaveAttribute("aria-selected", "true");
  await expect(page.getByTestId("pack-editor-title")).toContainText("api-docs");
  await expect(page.getByRole("tree", { name: "Packs" }).getByTestId("pack-row-p:api-docs")).toBeVisible();

  // The palette: New pack…, copied from a pack of the project.
  await page.keyboard.press("Control+k");
  await page.keyboard.type("New pack");
  await page.keyboard.press("Enter");
  await expect(dialog).toBeVisible();
  await dialog.getByLabel("Name").fill("ddl-copy");
  await dialog.getByLabel("Start from").selectOption("sql-ddl");
  await expect(dialog.getByLabel("Start from").locator("option:checked")).toHaveText("Copy of sql-ddl");
  await expect(page.getByTestId("new-pack-from-text")).toHaveText(/^All \d+ files of sql-ddl, renamed to ddl-copy\.$/);
  await page.getByTestId("new-pack-create").click();
  await expect(page.getByTestId("pack-tab-ddl-copy")).toHaveAttribute("aria-selected", "true");
  await expect(page.getByTestId("unit-row-table")).toBeVisible();
});

test("an unedited Units grid follows pack.json on disk, and unsaved edits survive a tab switch", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");
  const tree = page.getByRole("tree", { name: "Packs" });
  await tree.getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  const table = page.getByTestId("unit-row-table");
  await expect(table.getByTestId("unit-output")).not.toHaveValue("");

  // The CLI or git rewrites pack.json behind the grid: the rows change and nothing is marked unsaved.
  await page.evaluate(async () => {
    const pack = await fetch("/api/packs/sql-ddl").then((r) => r.json());
    pack.document.units[0].output = "behind/{{ table.name }}.sql";
    const r = await fetch("/api/packs/sql-ddl", {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${pack.hash}"` },
      body: JSON.stringify(pack.document),
    });
    if (!r.ok) throw new Error(`PUT ${r.status}`);
  });
  await expect(table.getByTestId("unit-output")).toHaveValue("behind/{{ table.name }}.sql");
  await expect(page.getByTestId("units-save")).toBeDisabled();
  await expect(page.getByTestId("units-grid").locator("..").locator("..").getByText("unsaved", { exact: true })).toHaveCount(0);

  // An unsaved edit comes back after the Plan tab; closing the pack tab asks first.
  await table.getByTestId("unit-output").fill("kept/{{ table.name }}.sql");
  await page.getByRole("tab", { name: "Plan" }).click();
  await page.getByTestId("pack-tab-sql-ddl").click();
  await expect(page.getByTestId("unit-row-table").getByTestId("unit-output")).toHaveValue("kept/{{ table.name }}.sql");
  page.once("dialog", (d) => void d.dismiss());
  await page.getByRole("button", { name: "Close sql-ddl" }).click();
  await expect(page.getByTestId("pack-tab-sql-ddl")).toBeVisible();
  page.once("dialog", (d) => void d.accept());
  await page.getByRole("button", { name: "Close sql-ddl" }).click();
  await expect(page.getByTestId("pack-tab-sql-ddl")).toHaveCount(0);
});
