// The Reference data screen after the owner's first day of modelling (reference-types-seeds-localization.md 4.7), in
// the mock: the row editor (button, Shift+Enter, a double click on the handle; Save is one undo step), field display
// names and descriptions (Fields tab, the grid's headers, the row editor's help), Rename with the display name and
// the type's seed, the General tab, the tree without the type's own seed, the explorer's marks and seeds that do not
// move, Used by groups for reference types, the Storage tab's words and preview with the engine's explanation, the
// name hints and "Stored as" in plain words, and where a reference type, its fields and its rows are translated.
import AxeBuilder from "@axe-core/playwright";
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

/** ULID-shaped ids for the elements the tests create. */
const id = (n: number) => `01K0RD${String(n).padStart(20, "0")}`;
const CAR = id(1);
const CAR_SEED = id(5);
const COUNTRY = id(10);
const COUNTRY_SEED = id(13);
const CITY = id(20);
const DEMO_SEED = id(30);

const LONG = "A small car with a long description that does not fit in its cell, written over two lines.";

/** Creates car_models ("Car Models", as the owner named it), Country, and City with a field typed by Country. */
async function seedModel(page: Page): Promise<void> {
  const outcome = await page.evaluate(
    async ({ ids, long }) => {
      const res = await fetch("/api/model/batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          operations: [
            {
              op: "create",
              element: {
                kind: "reference-type",
                id: ids.car,
                name: "car_models",
                displayName: "Car Models",
                code: { id: ids.carCode, displayName: "Model code", description: "The maker's code for the model." },
                label: { id: ids.carLabel },
                attributes: [{ id: ids.maker, name: "maker", type: "string", displayName: "Maker", description: "Who builds it." }],
              },
            },
            {
              op: "create",
              element: {
                kind: "seed",
                id: ids.carSeed,
                name: "car_models",
                target: ids.car,
                columns: ["code", "label", "description", ids.maker],
                rows: [
                  { id: ids.row1, values: ["c3", "C3", long, "Citroën"] },
                  { id: ids.row2, values: ["2cv", "2CV", null, "Citroën"] },
                ],
              },
            },
            {
              op: "create",
              element: { kind: "reference-type", id: ids.country, name: "Country", code: { id: ids.countryCode }, label: { id: ids.countryLabel } },
            },
            {
              op: "create",
              element: {
                kind: "seed",
                id: ids.countrySeed,
                name: "Country",
                target: ids.country,
                columns: ["code", "label"],
                rows: [{ id: ids.row3, values: ["fr", "France"] }],
              },
            },
            {
              op: "create",
              element: {
                kind: "reference-type",
                id: ids.city,
                name: "City",
                code: { id: ids.cityCode },
                label: { id: ids.cityLabel },
                attributes: [{ id: ids.cityCountry, name: "country", type: { ref: ids.country } }],
              },
            },
          ],
        }),
      });
      return ((await res.json()) as { outcome: string }).outcome;
    },
    {
      long: LONG,
      ids: {
        car: CAR,
        carCode: id(2),
        carLabel: id(3),
        maker: id(4),
        carSeed: CAR_SEED,
        row1: id(6),
        row2: id(7),
        country: COUNTRY,
        countryCode: id(11),
        countryLabel: id(12),
        countrySeed: COUNTRY_SEED,
        row3: id(14),
        city: CITY,
        cityCode: id(21),
        cityLabel: id(22),
        cityCountry: id(23),
      },
    },
  );
  expect(outcome).toBe("saved");
}

async function element(page: Page, elementId: string): Promise<Record<string, unknown>> {
  return page.evaluate(async (x) => {
    const r = await fetch(`/api/model/elements/${x}`);
    return ((await r.json()) as { json: Record<string, unknown> }).json;
  }, elementId);
}

async function openType(page: Page, label: string): Promise<void> {
  await workspace(page, "Reference data");
  await page.getByTestId("explorer-reference-data").getByTestId(`explorer-row-${label}`).click();
  await expect(page.getByTestId("reference-type-title")).toHaveText(label);
}

test("the row editor shows a row in full and saves it as one undo step", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  await seedModel(page);
  await openType(page, "Car Models");
  await page.getByRole("tab", { name: "Rows" }).click();
  const rows = page.getByTestId("rows-grid");

  // Headers show display names, with the field's description as the tooltip; a cell's tooltip is its whole text.
  await expect(rows.getByRole("columnheader")).toHaveText(["Row", "Model code", "label", "description", "Maker"]);
  await expect(rows.getByRole("columnheader", { name: "Model code" })).toHaveAttribute("title", "Model code (code), required\nThe maker's code for the model.");
  await expect(rows.locator('[data-cell="0:2"]')).toHaveAttribute("title", LONG);

  // Shift+Enter opens the row editor on the active row, one labelled control per column, each field's description
  // under its label.
  await rows.locator('[data-cell="0:0"]').click();
  await page.keyboard.press("Shift+Enter");
  const editor = page.getByTestId("row-editor");
  await expect(editor).toBeVisible();
  await expect(page.getByTestId("row-editor-title")).toHaveText("Row 1 of 2 · c3");
  await expect(editor.getByTestId("row-editor-field-code")).toContainText("The maker's code for the model.");
  await expect(editor.getByTestId("row-editor-field-maker")).toContainText("Who builds it.");
  const description = editor.getByTestId("row-editor-field-description").getByRole("textbox");
  await expect(description).toHaveValue(LONG);
  await description.fill("A small car.\nMade in Rennes.");
  await editor.getByTestId("row-editor-field-maker").getByRole("textbox").fill("Citroën SA");
  await expect(editor.getByText("Unsaved changes")).toBeVisible();
  await page.keyboard.press("Control+s");
  await expect(editor.getByText("No changes")).toBeVisible();
  await expect(rows.locator('[data-cell="0:3"]')).toHaveText("Citroën SA");
  await expect(rows.locator('[data-cell="0:2"]')).toHaveAttribute("title", "A small car.\nMade in Rennes.");

  // One undo step reverts both cells.
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(rows.locator('[data-cell="0:3"]')).toHaveText("Citroën");
  await expect(rows.locator('[data-cell="0:2"]')).toHaveAttribute("title", LONG);
  await expect(description).toHaveValue(LONG);

  // The screen with the row editor open passes an accessibility scan.
  const scan = await new AxeBuilder({ page }).include('[data-testid="reference-data"]').analyze();
  expect(scan.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);

  // Cancel closes it; a double click on a row's handle, or its button, opens it on that row; Esc closes it.
  await editor.getByRole("button", { name: "Cancel" }).click();
  await expect(editor).toBeHidden();
  await page.getByTestId("row-handle-2").dblclick();
  await expect(page.getByTestId("row-editor-title")).toHaveText("Row 2 of 2 · 2cv");
  await page.keyboard.press("Escape");
  await expect(editor).toBeHidden();
  await page.getByTestId("row-handle-1").hover();
  await page.getByRole("button", { name: "Open row 1 in the row editor" }).click();
  await expect(page.getByTestId("row-editor-title")).toHaveText("Row 1 of 2 · c3");
  // The next row button saves this row first, then shows the next one.
  await editor.getByTestId("row-editor-field-label").getByRole("textbox").fill("C3 Aircross");
  await editor.getByRole("button", { name: "Next row, saving this one" }).click();
  await expect(page.getByTestId("row-editor-title")).toHaveText("Row 2 of 2 · 2cv");
  await expect(rows.locator('[data-cell="0:1"]')).toHaveText("C3 Aircross");
});

test("every field has a display name and a description, shown in the grid and the row editor", async ({ page }) => {
  await openEditor(page);
  await seedModel(page);
  await openType(page, "Car Models");
  await page.getByRole("tab", { name: "Fields" }).click();
  const fields = page.getByTestId("reference-fields");
  await expect(fields.getByLabel("Display name of code")).toHaveValue("Model code");
  await fields.getByLabel("Display name of label").fill("Model name");
  await fields.getByLabel("Description of label").fill("What the dealer calls it.");
  await fields.getByLabel("Description of label").blur();

  // The user field's display name and description in the attribute grid.
  const grid = fields.getByTestId("attribute-grid");
  await expect(grid.locator('td[data-column="displayName"]').first()).toHaveText("Maker");
  await grid.locator('td[data-column="description"]').first().dblclick();
  const text = grid.getByRole("textbox", { name: "Description of maker" });
  await text.fill("Who builds it, as on the badge.");
  await text.press("Enter");
  await expect(grid.locator('td[data-column="description"]').first()).toHaveText("Who builds it, as on the badge.");
  await expect.poll(async () => ((await element(page, CAR)).label as { displayName?: string }).displayName).toBe("Model name");

  await page.getByRole("tab", { name: "Rows" }).click();
  const rows = page.getByTestId("rows-grid");
  await expect(rows.getByRole("columnheader", { name: "Model name" })).toHaveAttribute("title", "Model name (label), required\nWhat the dealer calls it.");
  await rows.locator('[data-cell="0:0"]').click();
  await page.keyboard.press("Shift+Enter");
  await expect(page.getByTestId("row-editor-field-maker")).toContainText("Who builds it, as on the badge.");
  await expect(page.getByTestId("row-editor-field-label")).toContainText("Model name");
});

test("Rename sets the name and the display name and renames the type's seed; General edits the common fields", async ({ page }) => {
  await openEditor(page);
  await seedModel(page);
  await openType(page, "Car Models");
  const explorer = page.getByTestId("explorer-reference-data");
  // The type's one seed, named after it, is not a child row: the type is the rows.
  await expect(explorer.getByTestId("explorer-row-Car Models")).not.toHaveAttribute("aria-expanded", /.*/);
  await expect(explorer.getByTestId("explorer-row-car_models")).toHaveCount(0);

  await explorer.getByTestId("explorer-row-Car Models").click({ button: "right" });
  await page.getByRole("menuitem", { name: "Rename" }).click();
  const rename = page.getByRole("dialog", { name: "Rename car_models" });
  await rename.getByLabel("Name", { exact: true }).fill("CarModel");
  await rename.getByLabel("Display name").fill("Car models");
  await rename.getByRole("button", { name: "Rename" }).click();
  await expect(rename).toBeHidden();
  await expect(explorer.getByTestId("explorer-row-Car models")).toBeVisible();
  await expect(page.getByTestId("reference-type-facts")).toContainText("CarModel · 2 rows");
  await expect.poll(async () => (await element(page, CAR_SEED)).name).toBe("CarModel");

  // General: the name commits like Rename (the seed follows), the other common fields save as you go.
  await page.getByRole("tab", { name: "General" }).click();
  const general = page.getByTestId("reference-general");
  const name = general.getByLabel("Name", { exact: true });
  await expect(name).toHaveValue("CarModel");
  await name.fill("Vehicle");
  await name.press("Enter");
  await expect(page.getByTestId("reference-type-facts")).toContainText("Vehicle · 2 rows");
  await expect.poll(async () => (await element(page, CAR_SEED)).name).toBe("Vehicle");
  await expect(general.getByLabel("Display name")).toHaveValue("Car models");
  await general.getByLabel("Plural name").fill("Vehicles");
  await general.getByLabel("Plural name").blur();
  await general.getByLabel("Category").selectOption({ label: "Catalog" });
  await general.getByLabel("Add to Tags").selectOption("billing");
  await expect
    .poll(async () => {
      const json = await element(page, CAR);
      return [json.pluralName, json.tags, typeof json.category];
    })
    .toEqual(["Vehicles", ["billing"], "string"]);
  // No stereotype of the billing project applies to reference types: the field says so.
  await expect(general).toContainText("No stereotype applies to a reference type yet; Settings › Stereotypes declares them.");
  // The category places the type in the explorer's Catalog group.
  await expect(explorer.getByRole("treeitem", { name: /^Catalog 1/ })).toBeVisible();
});

test("the explorer tags reference types; a seed moves to no domain and is renamed with its type", async ({ page }) => {
  await openEditor(page);
  await seedModel(page);
  await workspace(page, "Reference data");
  const explorer = page.getByTestId("explorer-reference-data");
  await explorer.getByTestId("explorer-row-Car Models").click({ button: "right" });
  const menu = page.getByTestId("row-menu");
  await expect(menu.getByRole("menuitem", { name: "Apply stereotype…" })).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: "Move to category…" })).toBeVisible();
  await menu.getByRole("menuitem", { name: "Tag…" }).click();
  const tag = page.getByTestId("mark-dialog-tag");
  await tag.getByLabel("Tag").selectOption("billing");
  await tag.getByRole("button", { name: "Add tag" }).click();
  await expect.poll(async () => (await element(page, CAR)).tags).toEqual(["billing"]);

  // A second seed: the type's seeds show, each with its row count, and neither moves to a domain nor renames alone.
  const outcome = await page.evaluate(
    async ({ seed, country }) => {
      const res = await fetch("/api/model/elements", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ kind: "seed", id: seed, name: "Demo", target: country, columns: ["code", "label"], rows: [] }),
      });
      return ((await res.json()) as { outcome: string }).outcome;
    },
    { seed: DEMO_SEED, country: COUNTRY },
  );
  expect(outcome).toBe("saved");
  const country = explorer.getByTestId("explorer-row-Country");
  await expect(country).toHaveAttribute("aria-expanded", /true|false/);
  if ((await country.getAttribute("aria-expanded")) === "false") {
    await country.click();
    await page.keyboard.press("ArrowRight");
  }
  await expect(explorer.getByTestId("explorer-row-Demo")).toBeVisible();
  await explorer.getByTestId("explorer-row-Demo").click({ button: "right" });
  await expect(menu.getByRole("menuitem", { name: "Delete" })).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: "Move to domain…" })).toHaveCount(0);
  await expect(menu.getByRole("menuitem", { name: "Rename" })).toHaveCount(0);
  await page.keyboard.press("Escape");
});

test("Move to category… says where categories come from when the project has none", async ({ page }) => {
  await page.goto("/?mock=empty");
  await expect(page.getByTestId("shell")).toBeVisible();
  await workspace(page, "Reference data");
  await page.getByTestId("new-reference-type").click();
  const created = page.getByRole("dialog", { name: "New reference type" });
  await created.getByLabel("Name", { exact: true }).fill("Currency");
  await created.getByRole("button", { name: "Create" }).click();
  await expect(created).toBeHidden();
  await page.getByTestId("explorer-reference-data").getByTestId("explorer-row-Currency").click({ button: "right" });
  await page.getByRole("menuitem", { name: "Move to category…" }).click();
  const move = page.getByRole("dialog", { name: "Move Currency to a category" });
  await expect(move).toContainText("No categories exist yet. Add them under Settings › Categories, then move the type here.");
  await move.getByRole("button", { name: "Open Settings › Categories" }).click();
  await expect(page).toHaveURL(/\/settings\/categories/);
});

test("Used by groups reference types by category, never by a domain they cannot have", async ({ page }) => {
  await openEditor(page);
  await seedModel(page);
  await openType(page, "Country");
  await page.getByRole("tab", { name: "Used by" }).click();
  const usedBy = page.getByTestId("reference-used-by");
  await expect(usedBy.getByRole("heading", { level: 3 })).toHaveText(["Reference types"]);
  await expect(usedBy).toContainText("City.country");
  await expect(usedBy).not.toContainText("Not in a domain");

  const explorer = page.getByTestId("explorer-reference-data");
  await explorer.getByTestId("explorer-row-City").click({ button: "right" });
  await page.getByRole("menuitem", { name: "Move to category…" }).click();
  const move = page.getByRole("dialog", { name: "Move City to a category" });
  await move.getByLabel("Category").selectOption({ label: "Catalog" });
  await move.getByRole("button", { name: "Move" }).click();
  await expect(move).toBeHidden();
  // Country now sits in the collapsed "No category" group: the search reveals it.
  await page.getByRole("complementary", { name: "Explorer" }).getByLabel("Search the model").fill("Country");
  await explorer.getByTestId("explorer-row-Country").click();
  await page.getByRole("tab", { name: "Used by" }).click();
  await expect(usedBy.getByRole("heading", { level: 3 })).toHaveText(["Reference type · Catalog"]);
});

test("Storage explains strategies in plain words and previews a pack's database unit, explaining a failure", async ({ page }) => {
  // The mock's model lives in the page: declare the strategies, then stay in the page.
  await page.goto("/settings/conventions");
  await page.getByTestId("strategies-hint").getByRole("button", { name: "Declare the standard storage strategies" }).click();
  await expect(page.getByTestId("strategies-hint")).toBeHidden();
  await seedModel(page);
  await openType(page, "Car Models");
  await page.getByRole("tab", { name: "Storage" }).click();
  const storage = page.getByTestId("reference-storage");
  await expect(page.getByTestId("storage-explanation")).toContainText("A storage strategy says how the packs store this type");
  await expect(storage.getByRole("columnheader")).toHaveText(["Database", "Strategy in use", "Set for this type"]);
  const all = storage.getByLabel("Storage for All databases");
  await expect(all.locator("option")).toHaveText(["Use the default (the packs decide)", "Let the packs decide", "lookup-table", "check", "native"]);
  await expect(storage.getByTestId("storage-main").getByTestId("strategy-in-use")).toHaveText("The packs decide, nothing is set");
  await all.selectOption("check");
  await expect(storage.getByTestId("strategy-help")).toHaveText("CHECK (col IN (...codes))");
  await expect(storage.getByTestId("storage-main").getByTestId("strategy-in-use")).toHaveText("check, from this type");
  await expect(storage.getByLabel("Storage for main").locator("option").first()).toHaveText("Use the default (check, as set for all databases)");

  // The preview lists the packs' database-scoped units, names the one it renders, and shows this type's statements.
  const unit = page.getByTestId("storage-preview-unit");
  await expect(unit.locator("option")).toHaveText(["schema (sql-ddl)", "migration (sql-ddl)", "seed (sql-ddl)", "seed-environments (sql-ddl)"]);
  await expect(unit.locator("option:checked")).toHaveText("seed (sql-ddl)");
  await expect(page.getByTestId("storage-preview-note")).toHaveText(
    "Renders the seed (sql-ddl) unit for main without writing it, and shows the statements for this type.",
  );
  // A check constraint holds no rows: the seed unit has no statement for the type; a lookup table gets its rows.
  await storage.getByRole("button", { name: "Preview output" }).click();
  await expect(page.getByTestId("storage-preview")).toHaveText("The seed unit of sql-ddl has no statement for this type in this database.");
  await all.selectOption("lookup-table");
  await expect(storage.getByTestId("storage-main").getByTestId("strategy-in-use")).toHaveText("lookup-table, from this type");
  await storage.getByRole("button", { name: "Preview output" }).click();
  await expect(page.getByTestId("storage-preview")).toContainText("'c3'");
  // A unit that does not render for the database: the engine's reason, the preview's message under Details.
  await unit.selectOption({ label: "migration (sql-ddl)" });
  await storage.getByRole("button", { name: "Preview output" }).click();
  const failure = page.getByTestId("storage-preview-error");
  await expect(failure).toContainText("The migration unit of sql-ddl does not render for main:");
  await expect(failure).toContainText("Unit 'migration' is 'select databases'");
  await expect(failure.getByText("Message from the preview")).toBeVisible();
});

test("names are identifiers in any case, and Stored as uses the Storage tab's words", async ({ page }) => {
  await page.goto("/settings/conventions");
  await page.getByTestId("strategies-hint").getByRole("button", { name: "Declare the standard storage strategies" }).click();
  await expect(page.getByTestId("strategies-hint")).toBeHidden();
  await workspace(page, "Reference data");
  await page.getByTestId("new-reference-type").click();
  const dialog = page.getByRole("dialog", { name: "New reference type" });
  await expect(dialog).toContainText("Letters, digits and underscores, not starting with a digit, such as UnitOfMeasure or car_models.");
  const storedAs = dialog.getByLabel("Stored as");
  await expect(storedAs.locator("option")).toHaveText(["Let the packs decide", "lookup-table", "check", "native"]);
  await expect(storedAs).toHaveValue("check");
  await expect(dialog.getByTestId("new-reference-type-storage-help")).toHaveText("CHECK (col IN (...codes))");
  await storedAs.selectOption({ label: "Let the packs decide" });
  await expect(dialog.getByTestId("new-reference-type-storage-help")).toHaveText("No strategy: the templates decide how the type is stored.");
  await dialog.getByLabel("Name", { exact: true }).fill("car_models");
  await dialog.getByRole("button", { name: "Create" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId("reference-type-title")).toHaveText("car_models");
});

test("one locale: the Rows tab says how to translate and opens Settings › Locales", async ({ page }) => {
  await openEditor(page);
  await seedModel(page);
  await openType(page, "Car Models");
  await page.getByRole("tab", { name: "Rows" }).click();
  await expect(page.getByTestId("rows-locale-hint")).toHaveText("Declare a second locale under Settings › Locales to translate labels and descriptions.");
  await expect(page.getByTestId("field-translations")).toHaveCount(0);
  await page.getByTestId("rows-open-locales").click();
  await expect(page).toHaveURL(/\/settings\/locales/);
});

test("several locales: the type's texts translate on General, its fields' on Fields", async ({ page }) => {
  await openEditor(page, "/?mock=locales");
  await seedModel(page);
  await openType(page, "Car Models");
  const entry = (locale: string, nodeId: string, field: string) =>
    page.evaluate(
      async ({ locale, owner, nodeId, field }) => {
        const r = await fetch(`/api/localization/${locale}/entries?owner=${owner}`);
        const body = (await r.json()) as { entries: { id: string; field: string; translation: string | null }[] };
        return body.entries.find((e) => e.id === nodeId && e.field === field)?.translation ?? null;
      },
      { locale, owner: CAR, nodeId, field },
    );

  // General: the inspector's Translations section, collapsed until opened.
  await page.getByRole("tab", { name: "General" }).click();
  const section = page.getByTestId("reference-general").getByTestId("translations-section");
  await section.getByRole("button", { name: /Translations/ }).click();
  const display = section.getByLabel("Display name (fr)", { exact: true });
  await expect(display).toHaveAttribute("placeholder", "Car Models");
  await display.fill("Modèles de voiture");
  await display.press("Enter");
  await expect.poll(() => entry("fr", CAR, "displayName")).toBe("Modèles de voiture");

  // Fields: a line per field with its display name and description in each locale.
  await page.getByRole("tab", { name: "Fields" }).click();
  const fields = page.getByTestId("field-translations");
  await fields.getByRole("button", { name: /Translations of the fields/ }).click();
  const maker = fields.getByTestId("field-translations-fr").getByLabel("Display name of maker (fr)");
  await expect(maker).toHaveAttribute("placeholder", "Maker");
  await maker.fill("Constructeur");
  await maker.press("Enter");
  await expect.poll(() => entry("fr", id(4), "displayName")).toBe("Constructeur");
  const code = fields.getByTestId("field-translations-fr").getByLabel("Description of code (fr)");
  await code.fill("Le code du constructeur.");
  await code.blur();
  await expect.poll(() => entry("fr", id(2), "description")).toBe("Le code du constructeur.");
});
