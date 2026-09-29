// Domain tags and categories in the editor (explorer-redesign.md 1.11, EX step 16): a domain's own tag created on
// first use in the domain editor, offered to an element of the domain as "ledger · Billing", filtered on by a chip
// that names its domain, and MQ3021 in the Problems panel with a Go to that opens the domain's Tags tab. Mock-only:
// it writes a domain vocabulary file into the mock model.
import AxeBuilder from "@axe-core/playwright";
import { expect, openEditor, test } from "./fixtures";

const explorer = (page: import("@playwright/test").Page) => page.getByRole("complementary", { name: "Explorer" });

test("a domain's own tag, its picker entry and filter chip, and MQ3021 with Go to", async ({ page }) => {
  await openEditor(page);
  const side = explorer(page);
  const billing = side.getByTestId("explorer-domain-Billing");

  // The domain editor's Tags tab: none of its own, the global ones inherited; the first tag creates the vocabulary.
  await billing.click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Open" }).click();
  const editor = page.getByTestId("element-editor");
  await expect(editor).toHaveAttribute("data-kind", "package");
  await editor.getByRole("tab", { name: "Tags" }).click();
  await expect(editor.getByTestId("inherited-tag-vocabulary")).toContainText("pii");
  await editor.getByRole("button", { name: "Add tag" }).click();
  const key = editor.getByLabel("Key of tag 1");
  await expect(key).toHaveValue("tag1");
  await key.fill("ledger");
  await key.press("Tab");
  expect((await new AxeBuilder({ page }).include('[data-testid="element-editor"]').analyze()).violations).toEqual([]);

  // The element's tag picker offers the domain's tag, naming the domain.
  await side.getByLabel("Search the model").fill("Invoice");
  await side.getByTestId("explorer-row-Invoice").dblclick();
  await expect(editor).toHaveAttribute("data-kind", "entity");
  const add = editor.getByTestId("editor-chips").getByLabel("Add to Tags");
  await expect(add.locator("option", { hasText: "ledger · Billing" })).toHaveCount(1);
  await add.selectOption({ label: "ledger · Billing" });
  await expect(editor.getByTestId("editor-chips")).toContainText("ledger · Billing");
  await side.getByLabel("Search the model").fill("");

  // The filter chip names the domain and narrows the tree to the tagged element.
  await side.getByTestId("explorer-filters").click();
  await page.getByRole("menuitemcheckbox", { name: "ledger · Billing" }).click();
  await page.keyboard.press("Escape");
  await expect(side.getByRole("button", { name: "Remove filter #ledger · Billing" })).toBeVisible();
  const tree = side.getByTestId("explorer-tree");
  await expect(tree.getByTestId("explorer-row-Invoice")).toBeVisible();
  await expect(tree.getByTestId("explorer-row-Customer")).toHaveCount(0);
  await side.getByRole("button", { name: "Remove filter #ledger · Billing" }).click();

  // A domain tag key the global vocabulary declares is MQ3021; Go to opens the domain's Tags tab.
  await billing.click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Open" }).click();
  await expect(editor).toHaveAttribute("data-kind", "package");
  await editor.getByRole("tab", { name: "General" }).click();
  await editor.getByRole("tab", { name: "Tags" }).click();
  await editor.getByRole("button", { name: "Add tag" }).click();
  const second = editor.getByLabel("Key of tag 2");
  await second.fill("pii");
  await second.press("Tab");
  await editor.getByRole("tab", { name: "General" }).click();
  const goTo = page.getByTestId("problem-goto-MQ3021");
  await expect(goTo).toBeVisible({ timeout: 10_000 });
  await expect(goTo).toHaveAccessibleName("Go to tags of Billing");
  await goTo.click();
  await expect(editor).toHaveAttribute("data-kind", "package");
  await expect(editor.getByRole("tab", { name: "Tags" })).toHaveAttribute("aria-selected", "true");
  await expect(editor.getByLabel("Key of tag 2")).toHaveValue("pii");
});
