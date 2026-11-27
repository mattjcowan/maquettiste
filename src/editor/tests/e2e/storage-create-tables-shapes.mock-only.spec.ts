// Create tables… asks how to store a hierarchy and a list (retire-projection-design.md section 7), on the mock: the choices with their
// defaults, a table per type picked for a hierarchy, and the tables and bindings that follow.
import type { Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const ulid = (n: number) => `01K9HQRS${String(n).padStart(18, "0")}`;

async function seed(page: Page) {
  const entity = (n: number, name: string, attributes: object[], extra: object = {}) => ({
    kind: "entity",
    id: ulid(n),
    name,
    package: BILLING,
    attributes,
    ...extra,
  });
  const docs = [
    entity(
      1,
      "Receipt",
      [
        { id: ulid(10), name: "id", type: "int32", required: true },
        { id: ulid(11), name: "amount", type: "decimal" },
      ],
      { key: { attributes: [ulid(10)], strategy: "database-identity" } },
    ),
    entity(2, "CardReceipt", [{ id: ulid(12), name: "last4", type: "string" }], { base: ulid(1) }),
    entity(3, "WireReceipt", [{ id: ulid(13), name: "iban", type: "string" }], { base: ulid(1) }),
  ];
  const outcomes = await page.evaluate(async (list) => {
    const out: string[] = [];
    for (const doc of list) {
      const res = await fetch("/api/model/elements", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(doc) });
      out.push(((await res.json()) as { outcome: string }).outcome);
    }
    return out;
  }, docs);
  expect(outcomes).toEqual(["saved", "saved", "saved"]);
}

test("a table per type for a hierarchy: pick it in Create tables and see the tables", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  await seed(page);
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill("Receipt");
  await side.getByTestId("explorer-row-Receipt").click();
  await side.getByTestId("explorer-row-CardReceipt").click({ modifiers: ["ControlOrMeta"] });
  await side.getByTestId("explorer-row-WireReceipt").click({ modifiers: ["ControlOrMeta"] });
  await side.getByTestId("explorer-row-CardReceipt").click({ button: "right" });
  await page.getByTestId("row-menu").getByRole("menuitem", { name: "Create tables…" }).click();
  const dialog = page.getByTestId("create-tables-dialog");
  const choice = dialog.getByTestId("create-tables-hierarchy-Receipt");
  await expect(choice).toContainText("Receipt and 2 derived entities");
  const shape = choice.getByRole("combobox");
  await expect(shape).toHaveValue("one-table");
  await expect(dialog.getByTestId("storage-plan")).toContainText("receipts");
  await expect(dialog.getByTestId("storage-plan")).not.toContainText("card_receipts");
  await shape.selectOption({ label: "A table per type (each derived table keyed by its base)" });
  await expect(dialog.getByTestId("storage-plan")).toContainText("card_receipts");
  await expect(dialog.getByTestId("storage-plan")).toContainText("wire_receipts");
  await dialog.getByTestId("create-tables-apply").click();
  await expect(dialog).toHaveCount(0);
  await side.getByTestId("explorer-row-CardReceipt").dblclick();
  const editor = page.getByRole("region", { name: "Editor: CardReceipt" });
  await editor.getByRole("tab", { name: "Storage" }).click();
  const card = editor.getByTestId("storage-tab").getByTestId("binding-card");
  await expect(card.getByTestId("binding-source").locator("option:checked")).toHaveText("billing.card_receipts");
  await expect(card.getByTestId("field-row-last4")).toHaveAttribute("data-status", "mapped");
});
