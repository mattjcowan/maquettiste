// Model snapshots in the editor (docs/engineering/snapshots.md section 10): the picker on the project name, taking a
// snapshot (packs off by default), comparing it with the working model (the element's fields and both documents), opening
// it read-only "as of" (the URL carries it, every write is off), restoring it after a confirmation and undoing that by
// restoring the safety snapshot, roles, export and import, and a restore refused while generation runs.
import { readFileSync } from "node:fs";
import type { Page } from "@playwright/test";
import { card, expect, openEditor, test } from "./fixtures";

const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";

/** An edit of the working model from outside this window (another window, the CLI or the disk). */
const renameInvoice = (page: Page, name: string) =>
  page.evaluate(
    ([id, value]) =>
      (
        window as unknown as { __mqMock: { model: { externalEdit(id: string, mutate: (json: Record<string, unknown>) => void): unknown } } }
      ).__mqMock.model.externalEdit(id, (json) => void (json.name = value)),
    [INVOICE, name] as const,
  );

const picker = async (page: Page) => {
  await page.getByTestId("snapshot-picker").click();
  const panel = page.getByTestId("snapshot-picker-panel");
  await expect(panel).toBeVisible();
  return panel;
};

async function takeSnapshot(page: Page, name: string): Promise<string> {
  const panel = await picker(page);
  await panel.getByTestId("snapshot-take").click();
  const dialog = page.getByRole("dialog", { name: "Take a snapshot" });
  // The packs are left out unless the box is ticked.
  await expect(dialog.getByRole("checkbox", { name: "Include the template packs" })).toHaveAttribute("aria-checked", "false");
  await dialog.getByLabel("Name").fill(name);
  await dialog.getByTestId("snapshot-take-submit").click();
  await expect(page.getByTestId("notice")).toContainText(`Snapshot ${name} taken`);
  const again = await picker(page);
  const row = again.locator('[data-testid^="snapshot-row-"]').filter({ hasText: name }).first();
  const id = ((await row.getAttribute("data-testid")) ?? "").replace("snapshot-row-", "");
  await page.keyboard.press("Escape");
  return id;
}

const rowActions = async (page: Page, id: string) => {
  const panel = await picker(page);
  await panel.getByTestId(`snapshot-row-${id}`).getByTestId("snapshot-actions").click();
  return page.getByRole("menu");
};

test("take a snapshot, edit the model, compare shows the change with its diff", async ({ page }) => {
  await openEditor(page);
  const id = await takeSnapshot(page, "Release 1");
  expect(id).toMatch(/^release-1-\d{8}-\d{6}$/);
  await renameInvoice(page, "Bill");
  await expect(card(page, "Bill")).toBeVisible();

  const menu = await rowActions(page, id);
  await menu.getByTestId("snapshot-compare-working").click();
  const view = page.getByTestId("compare-view");
  await expect(view).toBeVisible();
  await expect(view.getByTestId("compare-totals")).toContainText("~1 changed");
  await expect(view.getByTestId("compare-kind-entity")).toContainText("~1");
  const row = view.getByTestId("compare-element");
  await expect(row).toHaveCount(1);
  await expect(row).toContainText("Bill (renamed from Invoice)");
  // The filters: added shows nothing, renamed shows it.
  await view.getByTestId("compare-change").selectOption("added");
  await expect(row).toHaveCount(0);
  await view.getByTestId("compare-change").selectOption("renamed");
  await expect(row).toHaveCount(1);
  // The element is read when picked: its fields and both documents in the diff view.
  await row.click();
  const fields = view.getByTestId("compare-fields");
  await expect(fields).toContainText("/name");
  await expect(fields).toContainText('"Invoice"');
  await expect(fields).toContainText('"Bill"');
  await expect(view.getByTestId("compare-diff").locator(".monaco-diff-editor")).toBeVisible();
  await view.getByTestId("compare-close").click();
  await expect(view).toHaveCount(0);
});

test("open as of shows the old value read-only, with writes off and the URL carrying it", async ({ page }) => {
  await openEditor(page);
  const id = await takeSnapshot(page, "Before rename");
  await renameInvoice(page, "Bill");
  await expect(card(page, "Bill")).toBeVisible();

  const panel = await picker(page);
  await panel.getByTestId(`snapshot-row-${id}`).getByTestId("snapshot-open-row").click();
  await expect(page).toHaveURL(new RegExp(`[?&]snapshot=${id}`));
  const banner = page.getByTestId("as-of-banner");
  await expect(banner).toContainText("Viewing snapshot Before rename");
  await expect(banner).toContainText("read-only");
  // The canvas and the inspector show the model as it was.
  await expect(card(page, "Invoice")).toBeVisible();
  await expect(card(page, "Bill")).toHaveCount(0);
  await card(page, "Invoice").click();
  const inspector = page.getByTestId("inspector");
  await expect(page.getByTestId("inspector-title")).toHaveText("Invoice");
  const name = inspector.getByRole("textbox", { name: "Name", exact: true });
  await expect(name).toHaveValue("Invoice");
  await expect(name).toHaveAttribute("readonly", "");
  // Writes are off: undo, the assistant, and the screens the API does not serve for a snapshot.
  await expect(page.getByRole("button", { name: "Undo", exact: true })).toBeDisabled();
  await expect(page.getByTestId("assistant-toggle")).toBeDisabled();
  await page.getByTestId("rail-generate").click();
  await expect(page.getByTestId("workspace-generate").getByTestId("not-for-snapshot")).toContainText("not available for a snapshot");
  await expect(page).toHaveURL(new RegExp(`[?&]snapshot=${id}`));
  // The Database and Reference data screens read the snapshot too.
  await page.getByTestId("rail-databases").click();
  await expect(page.getByTestId("workspace-database")).toBeVisible();
  await page.getByTestId("rail-reference-data").click();
  await expect(page.getByTestId("workspace-reference-data")).toBeVisible();
  await page.getByTestId("rail-domain-model").click();
  await expect(card(page, "Invoice")).toBeVisible();

  // Back to the working model: the edit is there again.
  await banner.getByTestId("as-of-back").click();
  await expect(page).not.toHaveURL(/snapshot=/);
  await expect(page.getByTestId("as-of-banner")).toHaveCount(0);
  await expect(card(page, "Bill")).toBeVisible();
});

test("restore after a confirmation brings it back, and the undo button restores the safety snapshot", async ({ page }) => {
  await openEditor(page);
  const id = await takeSnapshot(page, "Good state");
  await renameInvoice(page, "Bill");
  await expect(card(page, "Bill")).toBeVisible();

  const menu = await rowActions(page, id);
  await menu.getByTestId("snapshot-restore").click();
  const dialog = page.getByRole("dialog", { name: "Restore Good state?" });
  await expect(dialog.getByTestId("snapshot-restore-steps")).toContainText("before-restore-");
  await expect(dialog.getByTestId("snapshot-restore-steps")).toContainText("does not hold the template packs");
  await expect(dialog.getByRole("checkbox", { name: "Also restore the template packs" })).toBeDisabled();
  await dialog.getByTestId("snapshot-restore-confirm").click();

  const done = page.getByRole("dialog", { name: "Restored Good state" });
  await expect(done.getByTestId("snapshot-restored-summary")).toContainText("1 document written, 0 deleted");
  await expect(card(page, "Invoice")).toBeVisible();
  await expect(card(page, "Bill")).toHaveCount(0);
  const undo = done.getByTestId("snapshot-undo-restore");
  await expect(undo).toHaveText(/^Undo: restore before-restore-\d{8}-\d{6}/);
  await undo.click();
  await expect(card(page, "Bill")).toBeVisible();
  await expect(card(page, "Invoice")).toHaveCount(0);
  await page.getByRole("button", { name: "Done" }).click();

  // The safety snapshots are listed, marked as such.
  const panel = await picker(page);
  await expect(panel.locator('[data-testid^="snapshot-row-before-restore-"]').first()).toContainText("Safety");
});

test("a restore from the as-of banner is refused while generation runs", async ({ page }) => {
  await openEditor(page);
  const id = await takeSnapshot(page, "Locked");
  await (await picker(page)).getByTestId(`snapshot-row-${id}`).getByTestId("snapshot-open-row").click();
  await page.evaluate(() => ((window as unknown as { __mqMock: { runLocked: boolean } }).__mqMock.runLocked = true));
  await page.getByTestId("as-of-restore").click();
  const dialog = page.getByRole("dialog", { name: "Restore Locked?" });
  await dialog.getByTestId("snapshot-restore-confirm").click();
  await expect(dialog.getByTestId("snapshot-restore-error")).toHaveText(
    "A generation run is writing files right now. Nothing changed: restore the snapshot when the run ends.",
  );
});

test("roles disable the actions they do not allow, saying why", async ({ page }) => {
  await openEditor(page, "/?mock=role-editor");
  const id = await takeSnapshot(page, "Editor's");
  const menu = await rowActions(page, id);
  await expect(menu.getByTestId("snapshot-open")).not.toHaveAttribute("data-disabled");
  await expect(menu.getByTestId("snapshot-compare-working")).not.toHaveAttribute("data-disabled");
  await expect(menu.getByTestId("snapshot-export")).not.toHaveAttribute("data-disabled");
  await expect(menu.getByTestId("snapshot-restore")).toHaveAttribute("data-disabled", "");
  await expect(menu.getByTestId("snapshot-restore")).toHaveAttribute("title", "Needs the maintainer role (you are editor).");
  await expect(menu.getByTestId("snapshot-delete")).toHaveAttribute("data-disabled", "");
  await page.keyboard.press("Escape");
  await page.keyboard.press("Escape");

  await openEditor(page, "/?mock=role-viewer");
  const panel = await picker(page);
  await expect(panel.getByTestId("snapshot-take")).toBeDisabled();
  await expect(panel.locator('[data-denied="Needs the editor role (you are viewer)."]')).toHaveCount(1);
  await expect(panel.getByTestId("snapshot-import")).toBeDisabled();
});

test("export a snapshot, then import the archive", async ({ page }) => {
  await openEditor(page);
  const id = await takeSnapshot(page, "Shared");
  const menu = await rowActions(page, id);
  const [download] = await Promise.all([page.waitForEvent("download"), menu.getByTestId("snapshot-export").click()]);
  expect(download.suggestedFilename()).toBe(`${id}.zip`);
  const bytes = readFileSync(await download.path());
  expect(bytes.subarray(0, 2).toString()).toBe("PK");

  await renameInvoice(page, "Bill");
  const panel = await picker(page);
  await panel.getByTestId("snapshot-import-file").setInputFiles({ name: `${id}.zip`, mimeType: "application/zip", buffer: bytes });
  await expect(page.getByTestId("notice")).toContainText("Imported Shared as shared-");
  const list = (await picker(page)).locator('[data-testid^="snapshot-row-shared-"]');
  await expect(list).toHaveCount(2);
  // The imported copy (a new id) holds the model as it was exported.
  const ids = (await list.evaluateAll((rows) => rows.map((r) => r.getAttribute("data-testid") ?? ""))).map((t) => t.replace("snapshot-row-", ""));
  const imported = ids.find((other) => other !== id)!;
  expect(imported).toMatch(/^shared-\d{8}-\d{6}/);
  await page.getByTestId(`snapshot-row-${imported}`).getByTestId("snapshot-open-row").click();
  await expect(page).toHaveURL(new RegExp(`[?&]snapshot=${imported}`));
  await expect(card(page, "Invoice")).toBeVisible();
});
