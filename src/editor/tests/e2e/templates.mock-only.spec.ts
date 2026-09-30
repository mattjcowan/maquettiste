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

test("complete from the template context, match template and output lines, and create, rename and delete files", async ({ page }) => {
  await page.goto("/generate");
  await workspace(page, "Generate");
  await page.getByRole("tree", { name: "Packs" }).getByTestId("pack-row-p:sql-ddl").getByText("sql-ddl", { exact: true }).click();
  await page.keyboard.press("Alt+3");
  const tab = page.getByTestId("templates-tab");
  const files = tab.getByRole("navigation", { name: "Pack files" });
  const code = page.getByTestId("code-scriban");
  await files.getByRole("button", { name: "table.scriban", exact: true }).click();
  await expect(page.getByTestId("preview-text").first()).not.toBeEmpty();

  // The cursor's template line highlights the output lines it matched (by text, and the pane says so).
  await code.locator(".view-lines").click();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n-- line map probe");
  const preview = page.getByTestId("template-preview");
  await expect(preview.locator("[data-match=true]").first()).toHaveText("-- line map probe");
  await expect(page.getByTestId("preview-match-note")).toContainText("approximate");
  // And back: an output line highlights the template lines that match it.
  await preview.locator("[data-line]", { hasText: "-- line map probe" }).first().click();
  await expect(code).toHaveAttribute("data-highlight", /^\d+$/);

  // Completion inside a code block: the unit's model members after `model.`, helpers and pipe functions after `|`.
  await code.locator(".view-lines").click();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\n{{ model.");
  const suggest = page.locator(".suggest-widget");
  await expect(suggest).toContainText("entities");
  await page.keyboard.press("Escape");
  await page.keyboard.type("entities | ");
  await page.keyboard.press("Control+Space");
  await expect(suggest).toContainText("array.add");
  await page.keyboard.press("Escape");

  // A file a unit names cannot be deleted: the refusal says who uses it.
  await page.getByTestId("template-delete").click();
  await page.getByTestId("template-file-action").getByRole("button", { name: "Delete" }).click();
  await expect(page.getByTestId("template-file-error")).toContainText("was not deleted");
  await expect(page.getByTestId("template-file-error")).toContainText("unit:table");
  await page.getByTestId("template-file-action").getByRole("button", { name: "Cancel" }).click();

  // New: an empty file, opened.
  await page.getByTestId("template-new").click();
  await page.getByRole("textbox", { name: "New file path" }).fill("partials/probe.scriban");
  await page.getByTestId("template-file-action").getByRole("button", { name: "Create" }).click();
  await expect(tab.getByTestId("template-path")).toHaveText("partials/probe.scriban");
  await expect(files.getByRole("button", { name: /^probe\.scriban/ })).toBeVisible();

  // Rename it, then delete it.
  await page.getByTestId("template-rename").click();
  await page.getByRole("textbox", { name: "New path" }).fill("partials/renamed.scriban");
  await page.getByTestId("template-file-action").getByRole("button", { name: "Rename" }).click();
  await expect(tab.getByTestId("template-path")).toHaveText("partials/renamed.scriban");
  await expect(files.getByRole("button", { name: /^probe\.scriban/ })).toHaveCount(0);
  await page.getByTestId("template-delete").click();
  await page.getByTestId("template-file-action").getByRole("button", { name: "Delete" }).click();
  await expect(files.getByRole("button", { name: /^renamed\.scriban/ })).toHaveCount(0);

  // Renaming a unit's template rewrites the unit in pack.json in the same change.
  await files.getByRole("button", { name: "schema.scriban", exact: true }).click();
  await expect(tab.getByTestId("template-path")).toHaveText("schema.scriban");
  await page.getByTestId("template-rename").click();
  await page.getByRole("textbox", { name: "New path" }).fill("database-schema.scriban");
  await page.getByTestId("template-file-action").getByRole("button", { name: "Rename" }).click();
  await expect(tab.getByTestId("template-path")).toHaveText("database-schema.scriban");
  const pack = await page.evaluate(() => fetch("/api/packs/sql-ddl").then((r) => r.json()));
  expect((pack.document.units as { id: string; template: string }[]).find((u) => u.id === "schema")?.template).toBe("database-schema.scriban");
});
