// Panels and page state (the owner's 0.2.0 test pass): every panel hides from its header and comes back from the slim
// edge it leaves, the layout (per browser) and the page state (per project) survive a reload, the shortcuts toggle,
// Escape closes no panel, and Reset layout brings the defaults back.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });
const inspector = (page: Page) => page.getByRole("complementary", { name: "Inspector" });

async function open(page: Page, name: string) {
  const side = explorer(page);
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).dblclick();
  await expect(page.getByRole("region", { name: `Editor: ${name}` })).toBeVisible();
}

test("every panel hides and comes back; the layout and the page state survive a reload; Reset layout", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);

  // Page state: two editor tabs, a Generate pack choice, a Settings tab, an expanded domain, the second tab active.
  await open(page, "Invoice");
  await open(page, "Payment");
  await side.getByLabel("Search the model").fill("");
  const billing = side.getByTestId("explorer-domain-Billing");
  // ArrowRight expands and never collapses, whether or not revealing Payment has expanded it already.
  if ((await billing.getAttribute("aria-expanded")) !== "true") {
    await billing.focus();
    await page.keyboard.press("ArrowRight");
  }
  await expect(billing).toHaveAttribute("aria-expanded", "true");

  await page.getByTestId("rail-generate").click();
  await page.getByTestId("pack-picker").click();
  const pack = page.getByTestId("pack-picker-list").getByRole("checkbox", { name: "Pack sql-ddl" });
  const packBefore = await pack.getAttribute("aria-checked");
  await pack.click();
  const packAfter = packBefore === "true" ? "false" : "true";
  await expect(pack).toHaveAttribute("aria-checked", packAfter);
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("pack-picker-list")).toHaveCount(0);

  await page.getByTestId("rail-settings").click();
  await page.getByRole("tab", { name: "Locales" }).click();
  await expect(page).toHaveURL(/\/settings\/locales/);

  await page.getByTestId("rail-domain-model").click();
  await page.getByTestId("editor-tab").filter({ hasText: "Payment" }).getByRole("button").first().click();
  await expect(page.getByRole("region", { name: "Editor: Payment" })).toBeVisible();

  // Every panel hides from its header button and leaves a slim edge.
  await page.getByTestId("hide-explorer").click();
  await expect(side).toHaveCount(0);
  await expect(page.getByTestId("show-explorer")).toBeVisible();
  await page.getByTestId("hide-inspector").click();
  await expect(inspector(page)).toHaveCount(0);
  await expect(page.getByTestId("show-inspector")).toBeVisible();
  await page.getByTestId("hide-bottom").click();
  await expect(page.getByTestId("show-bottom")).toBeVisible();
  await expect(page.getByTestId("tab-problems")).toBeVisible(); // the bottom panel's edge is its tab row
  await page.getByTestId("hide-tabs").click();
  await expect(page.getByTestId("editor-tabs")).toHaveCount(0);
  await expect(page.getByTestId("show-tabs")).toBeVisible();
  await page.getByTestId("hide-topbar").click();
  await expect(page.getByRole("button", { name: "Undo" })).toHaveCount(0);
  await expect(page.getByTestId("show-topbar")).toBeVisible();

  // Escape closes nothing.
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("show-explorer")).toBeVisible();

  // Reload: the layout comes back as it was left.
  await page.reload();
  await expect(page.getByTestId("shell")).toBeVisible();
  for (const panel of ["explorer", "inspector", "bottom", "tabs", "topbar"]) await expect(page.getByTestId(`show-${panel}`)).toBeVisible();
  await expect(side).toHaveCount(0);
  await expect(inspector(page)).toHaveCount(0);
  await expect(page.getByRole("region", { name: "Editor: Payment" })).toBeVisible();

  // And the page state: the tabs with the active one, the expansion, the Settings tab and the pack choice.
  await page.getByTestId("show-tabs").click();
  await expect(page.getByTestId("editor-tab")).toHaveCount(2);
  await expect(page.getByTestId("editor-tab").filter({ hasText: "Payment" }).getByRole("button").first()).toHaveAttribute("aria-current", "true");
  await page.getByTestId("show-inspector").click();
  await expect(page.getByTestId("inspector-title")).toHaveText("Payment");
  await page.getByTestId("show-explorer").click();
  await expect(side.getByTestId("explorer-domain-Billing")).toHaveAttribute("aria-expanded", "true");
  await page.getByTestId("rail-settings").click();
  await expect(page).toHaveURL(/\/settings\/locales/);
  await expect(page.getByRole("tab", { name: "Locales" })).toHaveAttribute("aria-selected", "true");
  await page.getByTestId("rail-generate").click();
  await page.getByTestId("pack-picker").click();
  await expect(page.getByTestId("pack-picker-list").getByRole("checkbox", { name: "Pack sql-ddl" })).toHaveAttribute("aria-checked", packAfter);
  await page.keyboard.press("Escape");

  // The shortcuts: Alt+Shift+E hides and shows the explorer, Alt+Shift+H the top bar controls.
  await page.getByTestId("rail-domain-model").click();
  await page.getByTestId("show-topbar").click();
  await page.keyboard.press("Alt+Shift+KeyE");
  await expect(side).toHaveCount(0);
  await page.keyboard.press("Alt+Shift+KeyE");
  await expect(side).toBeVisible();
  await page.keyboard.press("Alt+Shift+KeyH");
  await expect(page.getByTestId("show-topbar")).toBeVisible();

  // Reset layout (palette): every panel open, and it stays so after a reload.
  await page.keyboard.press("Control+k");
  await page.getByRole("dialog").getByRole("combobox").fill(">Reset layout");
  await page.getByRole("option", { name: "Reset layout", exact: true }).click();
  for (const panel of ["explorer", "inspector", "bottom", "tabs", "topbar"]) await expect(page.getByTestId(`hide-${panel}`)).toBeVisible();
  await page.reload();
  await expect(page.getByTestId("shell")).toBeVisible();
  for (const panel of ["explorer", "inspector", "bottom", "tabs", "topbar"]) await expect(page.getByTestId(`hide-${panel}`)).toBeVisible();
  await expect(page.getByTestId("show-explorer")).toHaveCount(0);
  // Reset layout keeps the page state: the tabs are still open.
  await expect(page.getByTestId("editor-tab")).toHaveCount(2);

  // Reset layout and page state (palette) forgets it: no tabs, and so after a reload.
  await page.keyboard.press("Control+k");
  await page.getByRole("dialog").getByRole("combobox").fill(">Reset layout and page state");
  await page.getByRole("option", { name: "Reset layout and page state", exact: true }).click();
  await expect(page.getByTestId("editor-tab")).toHaveCount(0);
  await page.reload();
  await expect(page.getByTestId("shell")).toBeVisible();
  await expect(page.getByTestId("editor-tab")).toHaveCount(0);
});
