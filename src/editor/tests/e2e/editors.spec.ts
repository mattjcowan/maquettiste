// Element editors (explorer-redesign.md 3.6): a double click in the explorer opens the entity editor in a pinned tab
// beside the screen; a single click while an editor shows opens the preview tab; General mode follows the selection
// and keeps the sub-tab; the relationship and type editors share the frame.
import AxeBuilder from "@axe-core/playwright";
import { expect, openEditor, test, workspace } from "./fixtures";

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });

test("entity editor tabs: pinned, preview, General mode, close", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  const side = explorer(page);
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").dblclick();
  const editor = page.getByRole("region", { name: "Editor: Invoice" });
  await expect(editor).toBeVisible();
  const tabs = page.getByTestId("editor-tabs");
  await expect(tabs.getByTestId("editor-tab")).toHaveAttribute("data-pinned", "true");
  for (const name of ["Attributes", "Relationships", "Indexes", "Storage", "Seed data", "Code generation", "References"])
    await expect(editor.getByRole("tab", { name })).toBeVisible();
  await expect(editor.getByRole("grid", { name: "Attributes of Invoice" })).toBeVisible();
  // The inspector stays the compact property view.
  await expect(page.getByRole("region", { name: "Inspector: Invoice" })).toBeVisible();

  // The key's Edit… opens the key dialog.
  await editor.getByTestId("edit-key").click();
  await expect(page.getByTestId("key-dialog")).toBeVisible();
  await page.keyboard.press("Escape");

  // A single click while an editor shows opens the preview tab beside the pinned one.
  await side.getByLabel("Search the model").fill("Payment");
  await side.getByTestId("explorer-row-Payment").click();
  await expect(page.getByRole("region", { name: "Editor: Payment" })).toBeVisible();
  await expect(tabs.getByTestId("editor-tab")).toHaveCount(2);
  await expect(tabs.getByTestId("editor-tab").nth(1)).toHaveAttribute("data-pinned", "false");

  // General mode: Relationships stays the sub-tab while the selection moves.
  await page.getByRole("region", { name: "Editor: Payment" }).getByRole("tab", { name: "Relationships" }).click();
  await page.getByTestId("follow-selection").click();
  await expect(page.getByTestId("follow-selection")).toHaveAttribute("aria-pressed", "true");
  await side.getByLabel("Search the model").fill("Customer");
  await side.getByTestId("explorer-row-Customer").first().click();
  const customer = page.getByRole("region", { name: "Editor: Customer" });
  await expect(customer).toBeVisible();
  await expect(customer.getByRole("tab", { name: "Relationships" })).toHaveAttribute("aria-selected", "true");
  await expect(tabs.getByTestId("editor-tab")).toHaveCount(2);

  // A double click in General mode opens a separate pinned tab; the following tab moves on with the selection.
  await side.getByLabel("Search the model").fill("Payment");
  await side.getByTestId("explorer-row-Payment").dblclick();
  await expect(tabs.getByTestId("editor-tab")).toHaveCount(3);
  await expect(tabs.getByTestId("editor-tab").nth(2)).toHaveAttribute("data-pinned", "true");
  await expect(page.getByRole("region", { name: "Editor: Payment" })).toBeVisible();
  await expect(tabs.getByTestId("editor-tab").nth(1)).toHaveAttribute("data-follow", "true");

  // The screen tab shows the canvas again; a middle click closes a tab.
  await page.getByTestId("editor-tab-screen").click();
  await expect(page.getByTestId("editor-area")).toHaveCount(0);
  await tabs.getByTestId("editor-tab").first().click({ button: "middle" });
  await expect(tabs.getByTestId("editor-tab")).toHaveCount(2);
});

for (const theme of ["light", "dark"] as const) {
  test(`axe: the explorer and the entity editor in the ${theme} theme`, async ({ page }) => {
    await page.emulateMedia({ reducedMotion: "reduce" });
    await page.addInitScript((t) => localStorage.setItem("mq.theme", t), theme);
    await openEditor(page);
    const side = explorer(page);
    await side.getByLabel("Search the model").fill("Invoice");
    await side.getByTestId("explorer-row-Invoice").dblclick();
    const editor = page.getByRole("region", { name: "Editor: Invoice" });
    await expect(editor).toBeVisible();
    for (const tab of ["Attributes", "Code generation"]) {
      await editor.getByRole("tab", { name: tab }).click();
      await page.waitForLoadState("networkidle");
      const scan = await new AxeBuilder({ page }).exclude(".react-flow__minimap").analyze();
      const bad = scan.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => `${v.id} ${v.nodes[0]?.target.join(" ")}`);
      expect(bad, `${theme} ${tab}`).toEqual([]);
    }
  });
}

test("the explorers' folder labels use the product's words (no package, workspace, convention, scalar)", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  let seen = 0;
  const jargon = /\b(packages?|workspaces?|conventions?|scalars?)\b/i;
  for (const name of ["Domain model", "Reference data", "Databases", "Diagrams"]) {
    await workspace(page, name);
    await page.waitForLoadState("networkidle");
    // Folders, groups and containers are the product's words; element rows carry the user's names.
    const labels = await side.locator('[role="treeitem"][data-type="folder"], [role="treeitem"][data-type="group"]').allInnerTexts();
    seen += labels.length;
    const bad = labels.map((l) => l.trim()).filter((l) => jargon.test(l));
    expect(bad, name).toEqual([]);
  }
  expect(seen).toBeGreaterThan(0);
});
