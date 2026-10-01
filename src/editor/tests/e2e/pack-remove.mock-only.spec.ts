// Remove pack (the owner: "How do I delete a pack?"): the pack editor's header offers Remove pack…, whose confirmation
// names what goes (the folder, the packs.<pack> settings entry) and what stays (the files the pack generated, counted
// from its manifest, under their output roots). Removing closes the pack's tab and the pack leaves the list; the files
// it generated stay on disk, untracked, so the next plan does not delete them.
import AxeBuilder from "@axe-core/playwright";
import { expect, test, workspace } from "./fixtures";

test("remove a pack from its editor, with a confirmation that counts the files left on disk", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");

  // Generate first, so the pack's manifest records files.
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-summary")).toContainText("sql-ddl:");
  await page.getByTestId("apply").click();
  await expect(page.getByTestId("apply-result")).toContainText("succeeded");
  const outputs = await page.evaluate(() => fetch("/api/packs/sql-ddl/outputs").then((r) => r.json()));
  const written = (outputs.outputs as { path: string }[]).length;
  expect(written).toBeGreaterThan(0);

  const tree = page.getByRole("tree", { name: "Packs" });
  await tree.getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
  const remove = page.getByTestId("pack-remove");
  await expect(remove).toHaveAttribute("title", /generated files stay on disk/);

  // Cancel leaves everything as it was.
  await remove.click();
  const dialog = page.getByTestId("remove-pack-dialog");
  await expect(dialog).toContainText(".maquettiste/templates/sql-ddl/");
  await expect(dialog).toContainText("packs.sql-ddl");
  await expect(page.getByTestId("remove-pack-untracked")).toHaveText(
    `The ${written} files it generated under db stay on disk and are no longer tracked; delete them yourself if you do not want them.`,
  );
  const axe = await new AxeBuilder({ page }).include("[data-testid=remove-pack-dialog]").analyze();
  expect(axe.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toBeHidden();
  expect(await page.evaluate(() => fetch("/api/packs/sql-ddl").then((r) => r.status))).toBe(200);

  // Remove: the tab closes, the pack leaves the list and the settings, the files stay recorded nowhere.
  await remove.click();
  await page.getByTestId("remove-pack-confirm").click();
  await expect(page.getByTestId("pack-tab-sql-ddl")).toHaveCount(0);
  await expect(page.getByTestId("pack-editor")).toHaveCount(0);
  await expect(tree.getByTestId("pack-row-p:sql-ddl")).toHaveCount(0);
  await expect(tree.getByTestId("pack-row-p:csharp-dapper")).toBeVisible();
  await expect(page.getByTestId("notice")).toContainText(`Removed .maquettiste/templates/sql-ddl/. ${written} generated files stay on disk, untracked.`);
  const packs = await page.evaluate(() => fetch("/api/packs").then((r) => r.json()));
  expect((packs.packs as { name: string }[]).map((p) => p.name)).toEqual(["csharp-dapper"]);
  const settings = await page.evaluate(() => fetch("/api/project/settings").then((r) => r.json()));
  expect(settings.json.packs?.["sql-ddl"]).toBeUndefined();

  // The next plan has nothing of the removed pack to delete.
  await page.getByTestId("generate-tabs").getByRole("tab").first().click();
  await page.getByTestId("plan").click();
  await expect(page.getByTestId("plan-summary")).toContainText("csharp-dapper:");
  await expect(page.getByTestId("plan-summary")).not.toContainText("sql-ddl");
  await expect(page.getByTestId("plan-summary")).not.toContainText("to delete");
});

test("a stale pack.json hash removes nothing and says why", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");
  const tree = page.getByRole("tree", { name: "Packs" });
  await tree.getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
  await page.getByTestId("pack-remove").click();
  await expect(page.getByTestId("remove-pack-untracked")).toHaveText("It has generated no files.");

  // pack.json changes behind the editor's back (another window, the CLI).
  await page.evaluate(async () => {
    const pack = await fetch("/api/packs/sql-ddl").then((r) => r.json());
    await fetch("/api/packs/sql-ddl", {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${pack.hash}"` },
      body: JSON.stringify({ ...pack.document, description: "Changed elsewhere." }),
    });
  });
  await page.getByTestId("remove-pack-confirm").click();
  await expect(page.getByTestId("remove-pack-dialog").getByRole("alert")).toContainText("pack.json changed since this editor read it; nothing was removed.");
  expect(await page.evaluate(() => fetch("/api/packs/sql-ddl").then((r) => r.status))).toBe(200);
  await expect(page.getByTestId("pack-tab-sql-ddl")).toBeVisible();
});
