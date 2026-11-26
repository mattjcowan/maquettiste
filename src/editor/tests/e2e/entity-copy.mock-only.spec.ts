// What a copy carries over (the entity editor's Copy tab): fields get reset or cleared, the parts of a record are copied all, one or none,
// and one is chosen by a condition or a state and the latest by an attribute. Each choice is one save of the entity's `copy` block.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const INVOICE = "01J92P0V0FJ23CGSNKM7P1W5V7";
const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function copyTab(page: Page, name: string): Promise<Locator> {
  const side = explorer(page);
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).dblclick();
  const editor = page.getByRole("region", { name: `Editor: ${name}` });
  await expect(editor).toBeVisible();
  await editor.getByRole("tab", { name: "Copy", exact: true }).click();
  return editor.getByTestId("editor-copy");
}

const copyOf = (page: Page, id: string) =>
  page.evaluate(async (at) => {
    const doc = (await (await fetch(`/api/model/elements/${at}`)).json()) as { json: { copy?: Record<string, unknown> } };
    return doc.json.copy ?? null;
  }, id);

test.beforeEach(async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
});

test("fields reset or clear, a part kind is chosen one by latest, and only what differs from the default is written", async ({ page }) => {
  await openEditor(page);
  const tab = await copyTab(page, "Invoice");
  // The key is never listed; every other attribute is, copied by default.
  await expect(tab.getByTestId("copy-field-row")).toHaveCount(5);
  await expect(tab.getByTestId("copy-field-row").filter({ hasText: /^id/ })).toHaveCount(0);
  expect(await copyOf(page, INVOICE)).toBeNull();

  const status = tab.getByRole("combobox", { name: "status when copied" });
  // status is required with a default: it can be reset, not cleared.
  await expect(status.locator("option")).toHaveText(["Copied", "Reset"]);
  await status.selectOption("reset");
  await tab.getByRole("combobox", { name: "notes when copied" }).selectOption("clear");
  await expect
    .poll(() => copyOf(page, INVOICE))
    .toEqual({
      attributes: [
        { attribute: "01J92P0V0VB1Z49SWERMAGR4TV", onCopy: "reset" },
        { attribute: "01J92P0V0WKRGKH7YBKA2V30NC", onCopy: "clear" },
      ],
    });

  // Back to the default removes the entry.
  await tab.getByRole("combobox", { name: "notes when copied" }).selectOption("copy");
  await expect.poll(async () => ((await copyOf(page, INVOICE))?.attributes as unknown[]).length).toBe(1);

  // The parts: the lines, copied all by default; one needs a choice.
  const lines = tab.locator('[data-testid="copy-part-row"][data-name="lines"]');
  await expect(lines).toBeVisible();
  await lines.getByRole("combobox", { name: "lines when copied" }).selectOption("one");
  const choice = tab.getByTestId("copy-part-choice");
  // `one` is not written until it says how the one part is chosen.
  await expect(choice.getByTestId("copy-choice-hint")).toHaveText("Pick how the one part is chosen.");
  expect((await copyOf(page, INVOICE))?.parts).toBeUndefined();
  await choice.getByRole("combobox", { name: "Latest lines by" }).selectOption({ label: "quantity" });
  await expect
    .poll(async () => (await copyOf(page, INVOICE))?.parts)
    .toEqual([{ end: "01J92P0V1H4D2M1HCK82ASJEWT", onCopy: "one", choose: { latestBy: "01J92P0V0Y049452AH0K8CDC9N" } }]);

  // A condition on the part's own fields.
  await choice.getByRole("combobox", { name: "How lines is chosen" }).selectOption("condition");
  const condition = choice.getByLabel("Condition for lines");
  await condition.fill("quantity > 1");
  await condition.blur();
  await expect.poll(async () => ((await copyOf(page, INVOICE))?.parts as { choose?: { condition?: unknown } }[])[0].choose?.condition).toBeTruthy();

  // None writes `none` and drops the choice; All removes the entry.
  await lines.getByRole("combobox", { name: "lines when copied" }).selectOption("none");
  await expect.poll(async () => (await copyOf(page, INVOICE))?.parts).toEqual([{ end: "01J92P0V1H4D2M1HCK82ASJEWT", onCopy: "none" }]);
  await lines.getByRole("combobox", { name: "lines when copied" }).selectOption("all");
  await expect.poll(async () => (await copyOf(page, INVOICE))?.parts).toBeUndefined();
});

test("a part chosen by a state of a lifecycle that governs it, and an entity with no parts still has fields and links", async ({ page }) => {
  await openEditor(page);
  // Customer owns its invoices here: Invoice is governed by InvoiceLifecycle, and owns its lines.
  const made = await page.evaluate(
    async ({ pkg, customer, invoice }) => {
      const res = await fetch("/api/model/elements", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          kind: "relation",
          id: "01K7CPY0000000000000000001",
          name: "ownsInvoices",
          package: pkg,
          relationKind: "composition",
          ends: [
            { id: "01K7CPY0000000000000000002", entity: customer, role: "owner", max: 1 },
            { id: "01K7CPY0000000000000000003", entity: invoice, role: "ownedInvoices" },
          ],
        }),
      });
      return ((await res.json()) as { outcome: string }).outcome;
    },
    { pkg: BILLING, customer: CUSTOMER, invoice: INVOICE },
  );
  expect(made).toBe("saved");
  const tab = await copyTab(page, "Customer");
  const owned = tab.locator('[data-testid="copy-part-row"][data-name="ownedInvoices"]');
  await expect(owned).toBeVisible();
  // The lines of each owned invoice are listed one level down.
  await expect(tab.getByTestId("copy-part-row")).toHaveCount(2);
  await owned.getByRole("combobox", { name: "ownedInvoices when copied" }).selectOption("one");
  const choice = tab.getByTestId("copy-part-choice");
  await choice.getByRole("combobox", { name: "How ownedInvoices is chosen" }).selectOption("state");
  await choice.getByRole("combobox", { name: "State for ownedInvoices" }).selectOption({ label: "Paid" });
  await expect.poll(async () => ((await copyOf(page, CUSTOMER))?.parts as { choose?: { state?: string } }[])?.[0]?.choose?.state).toBeTruthy();
  await choice.getByRole("combobox", { name: "Latest ownedInvoices by" }).selectOption({ label: "issuedOn" });
  await expect
    .poll(async () => ((await copyOf(page, CUSTOMER))?.parts as { choose?: { latestBy?: string } }[])?.[0]?.choose?.latestBy)
    .toBe("01J92P0V0SNXS6PZ42VDK42HP9");

  // An entity that owns no parts says so, and still lists its fields.
  const none = await copyTab(page, "Product");
  await expect(none.getByTestId("copy-no-parts")).toContainText("owns no parts");
  await expect(none.getByTestId("copy-field-row")).not.toHaveCount(0);
});
