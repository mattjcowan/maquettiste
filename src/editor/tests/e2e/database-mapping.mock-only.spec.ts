// Explicit mapping on the entity side (engine-design.md D46, the owner, 2026-10-03: "treat the database as its own thing; do the
// mapping on the entity side completely"): a new database starts empty and its dialog asks nothing about domains; a domain's
// and an entity's menus offer the Storage bulk actions, never Map to database…; Settings > Conventions shows an older project's
// convention read-only, with Stop laying out by convention, which stores the laid-out tables as table files first.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });
const chevron = (row: import("@playwright/test").Locator) => row.locator("span[aria-hidden]").first();

/** The billing database's document, read through the API. */
const mainDoc = (page: Page) =>
  page.evaluate(async () => ((await (await fetch("/api/model/elements/01J92P0V1QRN2181XM2ZWE02W4")).json()) as { json: Record<string, unknown> }).json);

test("a new database starts empty, and domains and entities offer the Storage actions instead of Map to database…", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const tree = side.getByTestId("explorer-tree");

  // New database: a name, schemas and a dialect; nothing about domains, mapping or entities.
  await workspace(page, "Databases");
  await side.getByTestId("explorer-new").click();
  await page.getByRole("menuitem", { name: "New database", exact: true }).click();
  const dialog = page.getByTestId("new-element-dialog");
  await expect(dialog).toHaveAttribute("data-kind", "database");
  await expect(page.locator("#new-element-convention")).toHaveCount(0);
  await expect(dialog).not.toContainText(/domain|mapp|entit/i);
  await page.locator("#new-element-name").fill("archive");
  await page.getByTestId("new-element-create").click();
  const archive = tree.getByTestId("explorer-folder-archive");
  await expect(archive).toBeVisible();
  await chevron(archive).click();
  const hint = tree.getByText("Empty: New table…, New view… or New query… adds one here");
  await expect(hint).toBeVisible();
  await expect(tree.getByText("Nothing is mapped here yet", { exact: false })).toHaveCount(0);
  const json = await page.evaluate(async () => {
    const index = (await (await fetch("/api/model/index")).json()) as
      { id: string; kind: string; name: string }[] | { elements: { id: string; kind: string; name: string }[] };
    const row = (Array.isArray(index) ? index : index.elements).find((r) => r.kind === "database" && r.name === "archive")!;
    return ((await (await fetch(`/api/model/elements/${row.id}`)).json()) as { json: Record<string, unknown> }).json;
  });
  expect(json.byConvention).toBe("none");
  expect(json).not.toHaveProperty("packages");

  // A domain's and an entity's menus: the Storage bulk actions, no Map to database….
  await workspace(page, "Domain model");
  await chevron(side.getByTestId("explorer-domain-Billing")).click();
  await side.getByTestId("explorer-domain-Catalog").click({ button: "right" });
  const menu = page.getByTestId("row-menu");
  await expect(menu.getByRole("menuitem", { name: "Map to database…" })).toHaveCount(0);
  for (const name of ["Auto-map to existing tables…", "Create tables…", "Remove bindings…"]) await expect(menu.getByRole("menuitem", { name })).toBeVisible();
  await page.keyboard.press("Escape");
  await side.getByLabel("Search the model").fill("Product");
  await side.getByTestId("explorer-row-Product").click({ button: "right" });
  await expect(menu.getByRole("menuitem", { name: "Map to database…" })).toHaveCount(0);
  await expect(menu.getByRole("menuitem", { name: "Create tables…" })).toBeVisible();
  await page.keyboard.press("Escape");
});

test("Settings shows an older project's convention read-only; Stop stores its tables as files first", async ({ page }) => {
  await openEditor(page);
  await workspace(page, "Settings");
  await page.getByRole("tab", { name: "Conventions" }).click();
  const section = page.getByTestId("settings-tables-by-convention");
  await expect(section.getByText("Older projects: tables laid out by convention")).toBeVisible();
  // main was made before 0.3.0: its file says nothing, so it lays out every domain. Nothing here edits or adds a domain.
  const main = section.getByTestId("database-mapping").filter({ hasText: "main" });
  await expect(main.getByTestId("database-mapping-convention")).toContainText("By convention: all domains (unspecified)");
  await expect(section.getByRole("checkbox")).toHaveCount(0);
  await expect(section.getByRole("combobox")).toHaveCount(0);
  await expect(main.getByTestId("convention-domain")).toHaveText(["Every domainStop laying out by convention"]);

  await main.getByTestId("convention-stop").click();
  const dialog = page.getByTestId("convention-stop-dialog");
  await expect(dialog.getByTestId("convention-stop-summary")).toContainText("laid out by this convention");
  await dialog.getByTestId("convention-stop-store").click();
  await expect(dialog).toHaveCount(0);
  await expect.poll(async () => (await mainDoc(page)).byConvention).toBe("none");
  // The panel has nothing left to show, and the stored tables stay in the database as table files.
  await expect(section).toHaveCount(0);
  const tables = await page.evaluate(async () => {
    const view = (await (await fetch("/api/databases/01J92P0V1QRN2181XM2ZWE02W4/view")).json()) as {
      view: { tables: { name: string; origin: string; isJunction: boolean }[] };
    };
    // A junction table stays as the model lays it out while both its ends are stored here.
    return view.view.tables.filter((t) => !t.isJunction).map((t) => `${t.name}:${t.origin}`);
  });
  for (const name of ["customers", "invoices", "payments"]) expect(tables).toContain(`${name}:designed`);
  expect(tables.filter((t) => t.endsWith(":synthesized"))).toEqual([]);
});
