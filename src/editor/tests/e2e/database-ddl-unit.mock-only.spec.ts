// The Database screen's DDL preview assumes no pack by name: it renders the enabled pack that has a unit rendered per
// database (a unit named schema or table first), or that pack's each-table unit for a selected table, and says which in
// its header. Renamed, the pack is still found; with no such pack enabled, the pane says so instead of hiding.
import { expect, test } from "./fixtures";

test("the DDL preview names the pack and unit it renders, follows a renamed pack, and says when none applies", async ({ page }) => {
  await page.goto("/database");
  const screen = page.getByTestId("database-workspace");
  await expect(screen).toBeVisible();
  const caption = page.getByTestId("ddl-preview-unit");
  await expect(caption).toHaveText("sql-ddl/schema · whole database");
  await expect(page.getByTestId("ddl-preview")).toContainText("CREATE SCHEMA");

  // A selected table renders through the same pack's each-table unit.
  await screen.getByTestId("database-table-invoices").click();
  await expect(caption).toHaveText("sql-ddl/table · invoices");

  // Renamed, the pack is found by its units, not its name.
  await page.evaluate(async () => {
    const pack = await fetch("/api/packs/sql-ddl").then((r) => r.json());
    await fetch("/api/packs/sql-ddl/rename", {
      method: "POST",
      headers: { "Content-Type": "application/json", "If-Match": `"${pack.hash}"` },
      body: JSON.stringify({ name: "ddl" }),
    });
  });
  await expect(caption).toHaveText("ddl/table · invoices");
  await expect(page.getByTestId("ddl-preview")).toContainText("invoices");

  // Disabled, no pack renders per database: the pane says so.
  await page.evaluate(async () => {
    const settings = await fetch("/api/project/settings").then((r) => r.json());
    await fetch("/api/project/settings/packs/ddl", {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${settings.hash}"` },
      body: JSON.stringify({ ...settings.json.packs.ddl, enabled: false }),
    });
  });
  await expect(page.getByTestId("ddl-preview-none")).toHaveText(
    "No enabled pack has a unit rendered per database (each database or select databases), so there is no DDL to preview.",
  );
  await expect(caption).toHaveText("no unit");
});
