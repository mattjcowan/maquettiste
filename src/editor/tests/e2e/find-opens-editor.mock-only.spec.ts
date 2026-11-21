// Finding an element opens its editor: the palette's Go to element, a click on a Problems row, and the inspector's Open editor; a
// table answers once in the palette; a translation finding opens the Locales screen on its queue; and the address follows a
// click on a table's finding.
import { expect, openEditor, test } from "./fixtures";

const PURCHASE_APPROVAL = "01JQPRC0000000000000000002";

test("the palette opens the editor of the element it goes to, and a table once", async ({ page }) => {
  await openEditor(page);
  await page.keyboard.press("Control+k");
  const box = page.getByRole("dialog").getByRole("combobox");
  await box.fill("PurchaseApproval");
  await page
    .getByRole("option", { name: /PurchaseApproval/ })
    .first()
    .click();
  await expect(page.getByTestId("element-editor")).toHaveAttribute("data-id", PURCHASE_APPROVAL);

  await page.keyboard.press("Control+k");
  await box.fill("invoices");
  const rows = page.getByRole("option", { name: /^invoices\b/i });
  await expect(rows.first()).toBeVisible();
  const names = await rows.allTextContents();
  expect(names.filter((n) => /table/i.test(n)).length, names.join(" | ")).toBe(1);
});

test("the inspector's Open editor opens the editor of a process", async ({ page }) => {
  await openEditor(page, `/?sel=${PURCHASE_APPROVAL}`);
  await expect(page.getByTestId("inspector-title")).toHaveText("PurchaseApproval");
  await page.getByTestId("inspector").getByTestId("inspector-open-editor").click();
  await expect(page.getByTestId("element-editor")).toHaveAttribute("data-id", PURCHASE_APPROVAL);
});

test("a click on a Problems row opens the owning element's editor", async ({ page }) => {
  await openEditor(page, "/?mock=lifecycle");
  await page.getByRole("tab", { name: /Problems/ }).click();
  const rows = page.getByTestId("problems-list").locator('[data-testid^="problem-MQ"]');
  await expect(rows.first()).toBeVisible();
  const n = await rows.count();
  let opened = false;
  for (let i = 0; i < n && !opened; i++) {
    const row = rows.nth(i);
    const owner = ((await row.locator("xpath=ancestor::li[2]").locator("span").first().textContent()) ?? "").replace(/\s*\(not saved\)$/, "").trim();
    await row.click();
    if ((await page.getByTestId("element-editor").count()) === 0) continue;
    opened = true;
    // The editor opened is the owning element's, not just any editor; a click opens a preview tab, so walking findings piles up no tabs.
    const title = (await page.getByTestId("element-editor").getByTestId("editor-title").textContent()) ?? "";
    expect(owner, `owner "${owner}" vs editor "${title}"`).toContain(title.trim());
    const tabs = page.getByTestId("editor-tab-name").filter({ hasText: title.trim() });
    await expect(tabs.first()).toBeVisible();
  }
  expect(opened).toBe(true);
});

test("a translation finding opens Settings, Locales, on its queue", async ({ page }) => {
  await openEditor(page, "/?mock=locales");
  await page.getByRole("tab", { name: /Problems/ }).click();
  const row = page.getByTestId("problems-list").locator('[data-testid="problem-MQ7204"]').first();
  await expect(row).toBeVisible();
  await row.click();
  await expect(page).toHaveURL(/\/settings\/locales/);
  await expect(page.getByTestId("completeness-matrix")).toBeVisible();
  await expect(page.getByTestId("translation-queue")).toBeVisible();
});

test("going to a table drops the previous selection from the address", async ({ page }) => {
  await openEditor(page, `/?sel=${PURCHASE_APPROVAL}`);
  await expect(page).toHaveURL(new RegExp(`sel=${PURCHASE_APPROVAL}`));
  await page.keyboard.press("Control+k");
  await page.getByRole("dialog").getByRole("combobox").fill("invoices");
  await page
    .getByRole("option", { name: /invoices.*table/i })
    .first()
    .click();
  await expect(page).toHaveURL(/\/database/);
  await expect(page).not.toHaveURL(new RegExp(PURCHASE_APPROVAL));
});
