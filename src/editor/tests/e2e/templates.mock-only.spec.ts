// The pack editor's Templates tab (generation-ui.md 3.3, mock project): open a template, edit it and see the live
// preview follow the unsaved text, save it with Ctrl+S, then get the 409 bar when the file changes on disk underneath.
import AxeBuilder from "@axe-core/playwright";
import { expect, test, workspace } from "./fixtures";

test("edit a template, preview it, save it, and meet a concurrent change", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");
  await page.getByRole("tree", { name: "Packs" }).getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
  await page.keyboard.press("Alt+3");
  const tab = page.getByTestId("templates-tab");
  await expect(tab).toBeVisible();

  // The file tree over the pack folder; the table template opens with its unit in the preview.
  const files = tab.getByRole("navigation", { name: "Pack files" });
  await expect(
    files
      .getByRole("button", { name: "helpers.js" })
      .or(files.getByRole("button", { name: "_shared.scriban" }))
      .first(),
  ).toBeVisible();
  await files.getByRole("button", { name: "table.scriban", exact: true }).click();
  await expect(tab.getByTestId("template-path")).toHaveText("table.scriban");
  await expect(page.getByTestId("code-scriban")).toBeVisible();
  await expect(page.getByTestId("preview-unit")).toHaveValue("table");
  await expect(page.getByTestId("preview-path").first()).toContainText(".sql");
  await expect(page.getByTestId("preview-text").first()).not.toBeEmpty();

  // Edit: the file turns dirty and the preview renders the unsaved text.
  await page.getByTestId("code-scriban").locator(".view-lines").click();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n-- edited in the browser");
  await expect(tab.getByTestId("template-path")).toContainText("•");
  await expect(page.getByRole("tab", { name: /Templates/ })).toContainText("•");
  await expect(page.getByTestId("preview-text").first()).toContainText("-- edited in the browser");

  // Another element for the preview.
  const element = page.getByTestId("preview-element");
  const second = await element.locator("option").nth(2).getAttribute("value");
  if (second) {
    await element.selectOption(second);
    await expect(page.getByTestId("preview-text").first()).toContainText("-- edited in the browser");
  }

  // Ctrl+S saves the file on disk.
  await page.keyboard.press("Control+s");
  await expect(page.getByTestId("template-save")).toBeDisabled();
  await expect(tab.getByTestId("template-path")).toHaveText("table.scriban");
  const disk = await page.evaluate(() => fetch("/api/packs/sql-ddl/file?path=table.scriban").then((r) => r.json()));
  expect(disk.text).toContain("-- edited in the browser");

  // Someone else changes the file while it has unsaved edits: the conflict bar shows before any save.
  await page.getByTestId("code-scriban").locator(".view-lines").click();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n-- second edit");
  await page.evaluate(async (hash: string) => {
    const r = await fetch("/api/packs/sql-ddl/file?path=table.scriban", {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${hash}"` },
      body: JSON.stringify({ text: "-- changed elsewhere\n" }),
    });
    if (!r.ok) throw new Error(`PUT ${r.status}`);
  }, disk.hash as string);
  const bar = page.getByTestId("template-conflict");
  await expect(bar).toContainText("changed on disk");
  await bar.getByTestId("template-compare").click();
  await expect(page.getByTestId("template-compare-view")).toBeVisible();

  const axe = await new AxeBuilder({ page }).include("[data-testid=templates-tab]").analyze();
  expect(axe.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);

  // Take theirs: the editor shows the disk text and nothing is unsaved.
  await bar.getByRole("button", { name: "Take theirs" }).click();
  await expect(bar).toBeHidden();
  await expect(page.getByTestId("code-text-scriban")).toHaveText("-- changed elsewhere\n", { useInnerText: false });
  await expect(page.getByTestId("template-save")).toBeDisabled();

  // An unedited open file follows the disk.
  const now = await page.evaluate(() => fetch("/api/packs/sql-ddl/file?path=table.scriban").then((r) => r.json()));
  await page.evaluate(async (hash: string) => {
    await fetch("/api/packs/sql-ddl/file?path=table.scriban", {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${hash}"` },
      body: JSON.stringify({ text: "-- changed again\n" }),
    });
  }, now.hash as string);
  await expect(page.getByTestId("code-text-scriban")).toHaveText("-- changed again\n", { useInnerText: false });
  await expect(bar).toBeHidden();
});

test("the preview renders only elements of the unit's scope, and a partial through a unit that includes it", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");
  await page.getByRole("tree", { name: "Packs" }).getByTestId("pack-row-p:csharp-dapper").getByText("csharp-dapper", { exact: true }).click();
  await expect(page.getByTestId("pack-editor")).toBeVisible();
  await page.keyboard.press("Alt+3");
  const tab = page.getByTestId("templates-tab");
  const files = tab.getByRole("navigation", { name: "Pack files" });
  const element = page.getByTestId("preview-element");

  // An entity template: the picker lists entities only, the first chosen.
  await files.getByRole("button", { name: "entity.scriban", exact: true }).click();
  await expect(page.getByTestId("preview-unit")).toHaveValue("entity");
  await expect(element.locator("option", { hasText: /^Invoice$/ })).toHaveCount(1);
  await expect(element).not.toHaveValue("");
  await expect(page.getByTestId("preview-text").first()).not.toBeEmpty();

  // The reference type template never renders for the entity picked above: the fixture has no reference type, so
  // the preview says so instead of a raw MQ6006.
  await files.getByRole("button", { name: "reference-type.scriban", exact: true }).click();
  await expect(page.getByTestId("preview-unit")).toHaveValue("reference-type");
  await expect(element.locator("option", { hasText: /^Invoice$/ })).toHaveCount(0);
  await expect(page.getByTestId("preview-scope")).toHaveText("The model has no reference type to preview this template with.");
  await expect(page.getByTestId("template-preview")).not.toContainText("MQ6006");

  // Enums list enums only.
  await files.getByRole("button", { name: "enum.scriban", exact: true }).click();
  await expect(page.getByTestId("preview-unit")).toHaveValue("enum");
  await expect(element.locator("option", { hasText: /^Invoice$/ })).toHaveCount(0);

  // A partial previews through the first unit that includes it, and says so.
  await files.getByRole("button", { name: /^_shared\.scriban/ }).click();
  await expect(page.getByTestId("preview-note")).toContainText("is a partial: previewing unit");
});
