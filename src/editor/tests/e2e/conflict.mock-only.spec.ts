// Resolve a conflict (phase2-design.md §4.6, §4.10). With ?mock=conflict the next element save meets a
// disk edit first and answers 409; the conflict dialog opens with Keep mine, Take theirs and Save merge.
import type { Page } from "@playwright/test";
import { card, expect, openEditor, test } from "./fixtures";

async function renameInvoice(page: Page, to: string) {
  await card(page, "Invoice").click();
  const inspector = page.getByRole("region", { name: "Inspector: Invoice" });
  const name = inspector.getByRole("textbox", { name: "Name", exact: true });
  await name.fill(to);
  await name.press("Enter");
  const dialog = page.getByRole("dialog", { name: "Invoice2 changed on disk" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByTestId("conflict-diff")).toBeVisible();
  return dialog;
}

test("resolve a conflict with Keep mine", async ({ page }) => {
  await openEditor(page, "/?mock=conflict");
  const dialog = await renameInvoice(page, "Invoice2");
  await dialog.getByRole("button", { name: "Keep mine" }).click();
  await expect(dialog).toBeHidden();
  await expect(card(page, "Invoice2")).toBeVisible();
  await expect(page.getByTestId("save-status")).toHaveText("Saved");

  // The element is editable again: a later edit saves normally.
  const name = page.getByRole("region", { name: "Inspector: Invoice2" }).getByRole("textbox", { name: "Name", exact: true });
  await name.fill("Invoice3");
  await name.press("Enter");
  await expect(card(page, "Invoice3")).toBeVisible();
  await expect(page.getByTestId("save-status")).toHaveText("Saved");
});

test("resolve a conflict with Take theirs", async ({ page }) => {
  await openEditor(page, "/?mock=conflict");
  const dialog = await renameInvoice(page, "Invoice2");
  await dialog.getByRole("button", { name: "Take theirs" }).click();
  await expect(dialog).toBeHidden();
  await expect(card(page, "Invoice")).toBeVisible();
  await expect(page.getByRole("region", { name: "Inspector: Invoice" }).getByRole("textbox", { name: "Name", exact: true })).toHaveValue("Invoice");
});
