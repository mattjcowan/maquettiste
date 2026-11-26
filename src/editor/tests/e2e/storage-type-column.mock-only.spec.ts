// The type column of a base and its subtypes in one table (the Storage tab): the base's binding names the column and its code; the
// derived entity's binding shows the column read-only, from the base, and sets its own code. Each edit is one save.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const NOTE = "01K7TYC0000000000000000001";
const URGENT = "01K7TYC0000000000000000010";
const explorer = (page: Page) => page.getByRole("complementary", { name: "Explorer" });

async function storage(page: Page, name: string): Promise<Locator> {
  const side = explorer(page);
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).dblclick();
  const editor = page.getByRole("region", { name: `Editor: ${name}` });
  await expect(editor).toBeVisible();
  await editor.getByRole("tab", { name: "Storage" }).click();
  return editor.getByTestId("storage-tab");
}

async function bindToNotes(tab: Locator, page: Page) {
  await tab.getByTestId("storage-add-binding").click();
  const add = page.getByTestId("add-binding-dialog");
  await add.getByLabel("Source").selectOption({ label: "billing.notes" });
  await add.getByTestId("add-binding-apply").click();
  await expect(tab.getByTestId("binding-card")).toBeVisible();
}

const bindingOf = (page: Page, id: string) =>
  page.evaluate(async (at) => {
    const doc = (await (await fetch(`/api/model/elements/${at}`)).json()) as { json: { bindings?: Record<string, unknown>[] } };
    return doc.json.bindings?.[0] ?? null;
  }, id);

test.beforeEach(async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
});

test("a base names the type column and its code; a derived entity reads the column from the base and sets its own code", async ({ page }) => {
  await openEditor(page);
  const outcomes = await page.evaluate(
    async ({ pkg, note, urgent }) => {
      const attr = (n: number, name: string, type: string) => ({ id: `01K7TYC00000000000000001${String(n).padStart(2, "0")}`, name, type, required: true });
      const post = async (body: unknown) =>
        (
          (await (
            await fetch("/api/model/elements", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })
          ).json()) as { outcome: string }
        ).outcome;
      return [
        await post({
          kind: "entity",
          id: note,
          name: "Note",
          package: pkg,
          key: { attributes: [attr(1, "id", "uuid").id], strategy: "uuid-v7" },
          attributes: [attr(1, "id", "uuid"), attr(2, "body", "text")],
        }),
        await post({ kind: "entity", id: urgent, name: "UrgentNote", package: pkg, base: note }),
      ];
    },
    { pkg: BILLING, note: NOTE, urgent: URGENT },
  );
  expect(outcomes).toEqual(["saved", "saved"]);

  // The base: the picker lists the source's columns; the column and the code are written on its binding.
  const base = await storage(page, "Note");
  await bindToNotes(base, page);
  const section = base.getByTestId("binding-type-column");
  await expect(section).toHaveAttribute("data-role", "base");
  await section.getByTestId("binding-type-column-select").selectOption({ label: "entity_type" });
  await section.getByTestId("binding-type-code").fill("note");
  await section.getByTestId("binding-type-code").blur();
  await expect(section.getByTestId("binding-type-codes")).toHaveText("Reads keep: note");
  await expect.poll(async () => (await bindingOf(page, NOTE))?.discriminatorValue).toBe("note");
  const saved = await bindingOf(page, NOTE);
  expect(saved?.discriminatorColumn).toBeTruthy();

  // The derived entity: the column is read-only, from Note; its code defaults to its name.
  const derived = await storage(page, "UrgentNote");
  await bindToNotes(derived, page);
  const line = derived.getByTestId("binding-type-column");
  await expect(line).toHaveAttribute("data-role", "derived");
  await expect(line.getByTestId("binding-type-column-inherited")).toHaveText("Type column entity_type, from Note");
  await expect(line.getByTestId("binding-type-column-select")).toHaveCount(0);
  await expect(line.getByTestId("binding-type-code")).toHaveAttribute("placeholder", "UrgentNote");
  await expect(line.getByTestId("binding-type-codes")).toHaveText("Reads keep: UrgentNote");
  await line.getByTestId("binding-type-code").fill("urgent");
  await line.getByTestId("binding-type-code").blur();
  await expect(line.getByTestId("binding-type-codes")).toHaveText("Reads keep: urgent");
  await expect.poll(async () => (await bindingOf(page, URGENT))?.discriminatorValue).toBe("urgent");
  expect((await bindingOf(page, URGENT))?.discriminatorColumn).toBeUndefined();

  // Back on the base: its reads keep its own code and the derived entity's.
  const again = await storage(page, "Note");
  await expect(again.getByTestId("binding-type-codes")).toHaveText("Reads keep: note, urgent");
});

test("an entity outside a hierarchy has no type column controls", async ({ page }) => {
  await openEditor(page);
  const tab = await storage(page, "InvoiceNote");
  await expect(tab.getByTestId("binding-card")).toBeVisible();
  await expect(tab.getByTestId("binding-type-column")).toHaveCount(0);
});
