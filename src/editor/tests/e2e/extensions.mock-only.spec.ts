// The Extensions tab (Generate explorer › Extensions, mock project): the model's custom property schemas and script rules
// listed, a rule edited and saved so its findings reach the Problems panel, a syntax error shown on the file at once, a
// new script rule and a new property schema created from their templates, and a schema checked as it is typed.
import AxeBuilder from "@axe-core/playwright";
import { expect, test, workspace } from "./fixtures";

test("list, edit and save a script rule, see its findings and a syntax error, and create new extension files", async ({ page }) => {
  test.setTimeout(90_000);
  await page.goto("/generate");
  await workspace(page, "Generate");
  const tree = page.getByRole("tree", { name: "Packs" });

  // The Extensions node lists the schemas and the rule.
  const node = tree.getByTestId("pack-row-x:extensions");
  await expect(node).toContainText("Extensions");
  await expect(node).toContainText("2 schemas · 1 rule");
  await node.locator("[aria-hidden]").first().click();
  await expect(tree.getByTestId("pack-row-x:extensions/retention.json")).toContainText("custom properties");
  await tree.getByTestId("pack-row-x:extensions/rules/naming.js").click();

  // The Extensions tab opens on the rule, in the code editor, with the rule it registers.
  await expect(page.getByTestId("extensions-tab")).toHaveText("Extensions");
  const screen = page.getByTestId("extensions-screen");
  await expect(screen.getByTestId("extension-path")).toHaveText("rules/naming.js");
  await expect(screen).toContainText("x/entity-names (warning)");
  await expect(screen).toContainText("not undo steps");
  const code = page.getByTestId("code-javascript");
  await expect(code).toBeVisible();
  await expect(screen.getByTestId("extension-problems")).toContainText("none");

  // Tighten the rule and save: its findings come back from validation, in the strip and in the Problems panel.
  await code.locator(".view-line", { hasText: "> 30" }).locator("span", { hasText: /^30$/ }).dblclick();
  await page.keyboard.type("6");
  await expect(screen.getByTestId("extension-path")).toContainText("•");
  await page.keyboard.press("Control+s");
  await expect(screen.getByTestId("extension-save")).toBeDisabled();
  await expect(screen.getByTestId("extension-rule-count")).toContainText("this file's rules report");
  await expect(screen.getByTestId("extension-rule-finding").first()).toContainText("x/entity-names");
  const disk = await page.evaluate(() => fetch("/api/extensions/file?path=rules/naming.js").then((r) => r.json()));
  expect(disk.text).toContain("element.name.length > 6");
  await page.getByRole("tab", { name: /Problems/ }).click();
  await expect(page.getByTestId("problems-list").getByTestId("problem-x/entity-names").first()).toBeVisible();

  // A syntax error is shown on the file as soon as it is saved.
  await code.locator(".view-lines").click();
  await page.keyboard.press("Control+End");
  await page.keyboard.type("\nmaquettiste.rule({ id: ");
  await page.keyboard.press("Control+s");
  await expect(screen.getByTestId("extension-problem").first()).toContainText("MQ5002");
  await expect(page.getByTestId("problems-list").getByTestId("problem-MQ5002").first()).toBeVisible();

  const axe = await new AxeBuilder({ page }).include("[data-testid=extensions-screen]").analyze();
  expect(axe.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);

  // A new script rule from its template: valid, and reports nothing until edited.
  await screen.getByTestId("extension-new-rule").click();
  const form = screen.getByTestId("extension-file-action");
  await expect(form.getByRole("textbox", { name: "New script rule path" })).toHaveValue("rules/new-rule.js");
  await form.getByRole("button", { name: "Create" }).click();
  await expect(screen.getByTestId("extension-path")).toHaveText("rules/new-rule.js");
  await expect(page.getByTestId("code-text-javascript")).toContainText('id: "new-rule"', { useInnerText: false });
  await expect(screen).toContainText("x/new-rule (warning)");

  // A new property schema from the explorer row: valid as written, then checked against the extension schema as it is typed.
  await tree.getByTestId("new-extension-schema").click();
  await expect(form.getByRole("textbox", { name: "New property schema path" })).toHaveValue("new-properties.json");
  await form.getByRole("button", { name: "Create" }).click();
  await expect(screen.getByTestId("extension-path")).toHaveText("new-properties.json");
  const json = page.getByTestId("code-json");
  await expect(json).toBeVisible();
  // The kinds list is laid out one item per line: the line that holds only "entity".
  await json
    .locator(".view-line", { hasText: /^\s*"entity"\s*$/ })
    .locator("span", { hasText: /entity/ })
    .last()
    .dblclick();
  await page.keyboard.type("entities");
  await expect(screen.getByTestId("extension-live-problem").first()).toBeVisible();
  const files = await page.evaluate(() => fetch("/api/extensions/files").then((r) => r.json()));
  expect(files.files.map((f: { path: string }) => f.path)).toEqual([
    "new-properties.json",
    "persona.json",
    "retention.json",
    "rules/naming.js",
    "rules/new-rule.js",
  ]);
});
