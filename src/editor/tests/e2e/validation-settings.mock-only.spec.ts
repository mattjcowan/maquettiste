// Settings › Validation over the mock's `locales` scenario (en, fr, fr-CA): the rule list filtered to one rule, a
// severity saved to maquettiste.json validation.rules, and the Problems panel following it (MQ7204 off drops it,
// MQ7205 given a severity reports it). The rows are walked by keyboard.
import { expect, openEditor, test } from "./fixtures";

test("a rule severity saved in Settings › Validation changes what Problems lists", async ({ page }) => {
  await openEditor(page, "/?mock=locales");
  await page.getByRole("tab", { name: /Problems/ }).click();
  const problems = page.getByTestId("problems-list");
  await expect(problems.getByTestId("problem-MQ7204").first()).toBeVisible();
  await expect(problems.getByTestId("problem-MQ7205")).toHaveCount(0);

  await page.getByTestId("rail-settings").click();
  await page.getByRole("tab", { name: "Validation" }).click();
  const tab = page.getByTestId("validation-settings");
  await expect(tab.getByTestId("validation-override-count")).toHaveText("0 overrides");
  // An MQ1 rule cannot be turned off.
  await expect(tab.getByLabel("Severity of MQ1001").locator("option")).toHaveText(["Default", "error", "warning", "info"]);

  await tab.getByLabel("Filter rules").fill("MQ7204");
  await expect(tab.locator("tr[data-rule]")).toHaveCount(1);
  await expect(tab.getByRole("columnheader", { name: /Localization/ })).toBeVisible();
  await tab.getByLabel("Severity of MQ7204").selectOption("off");
  await expect(tab.getByTestId("rule-row-MQ7204").getByTestId("rule-changed")).toBeVisible();
  await expect(tab.getByTestId("validation-override-count")).toHaveText("1 override");
  await tab.getByTestId("save-validation").click();
  await expect(tab.getByTestId("rule-changed")).toHaveCount(0);
  await expect(problems.getByTestId("problem-MQ7204")).toHaveCount(0);

  // Keyboard: the rows are focusable, arrows move between them and Enter moves into the picker.
  await tab.getByLabel("Filter rules").fill("MQ720");
  const first = tab.getByTestId("rule-row-MQ7201");
  await first.focus();
  await page.keyboard.press("ArrowDown");
  await expect(tab.getByTestId("rule-row-MQ7202")).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("ArrowDown");
  await expect(tab.getByTestId("rule-row-MQ7205")).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(tab.getByLabel("Severity of MQ7205")).toBeFocused();
  await tab.getByLabel("Severity of MQ7205").selectOption("warning");
  await tab.getByTestId("save-validation").click();
  await expect(tab.getByTestId("validation-override-count")).toHaveText("2 overrides");
  await expect(problems.getByTestId("problem-MQ7205").first()).toBeVisible();
  await expect(problems.getByTestId("problem-MQ7204")).toHaveCount(0);

  // Reset all, then Discard, leaves the saved overrides.
  await tab.getByRole("button", { name: "Reset all" }).click();
  await expect(tab.getByTestId("validation-override-count")).toHaveText("0 overrides");
  await tab.getByRole("button", { name: "Discard" }).click();
  await expect(tab.getByTestId("validation-override-count")).toHaveText("2 overrides");
});
