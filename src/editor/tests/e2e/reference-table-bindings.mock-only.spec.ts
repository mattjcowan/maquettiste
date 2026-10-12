// "Stored in a table" (2026-10-11): on a reference type's Storage tab, a database's table file that holds the type's rows,
// its field map started from the columns named like the fields, and constant columns; the strategy column then names the
// table, and the table's Data tab names the type whose rows it holds. Each change is one save of the type.
import type { Page } from "@playwright/test";
import { expect, openEditor, test, workspace } from "./fixtures";

const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";
const id = (n: number) => `01K7RTE${String(n).padStart(19, "0")}`;
const TABLE = id(1);
const TYPE = id(10);

async function seedModel(page: Page): Promise<void> {
  const outcome = await page.evaluate(
    async ({ main, ids }) => {
      const res = await fetch("/api/model/batch", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          operations: [
            {
              op: "create",
              element: {
                kind: "table",
                id: ids.table,
                name: "payment_terms",
                database: main,
                columns: [
                  { id: ids.code, name: "code", type: "string", length: 10, nullable: false },
                  { id: ids.name, name: "name", type: "string", length: 40, nullable: false },
                  { id: ids.days, name: "due_days", type: "int32" },
                  { id: ids.kind, name: "kind", type: "string", length: 20 },
                ],
                primaryKey: { columns: [ids.code] },
              },
            },
            {
              op: "create",
              element: {
                kind: "reference-type",
                id: ids.type,
                name: "PaymentTerm",
                code: { id: ids.typeCode },
                label: { id: ids.typeLabel },
                attributes: [{ id: ids.typeDays, name: "dueDays", type: "int32" }],
              },
            },
          ],
        }),
      });
      return ((await res.json()) as { outcome: string }).outcome;
    },
    {
      main: MAIN,
      ids: { table: TABLE, code: id(2), name: id(3), days: id(4), kind: id(5), type: TYPE, typeCode: id(11), typeLabel: id(12), typeDays: id(13) },
    },
  );
  expect(outcome).toBe("saved");
}

const bindingsOf = (page: Page) =>
  page.evaluate(async (typeId) => {
    const r = await fetch(`/api/model/elements/${typeId}`);
    return ((await r.json()) as { json: { bindings?: unknown[] } }).json.bindings;
  }, TYPE);

test("a reference type names the table that holds its rows, with the column of each field and its constants", async ({ page }) => {
  await openEditor(page);
  await seedModel(page);
  await workspace(page, "Reference data");
  await page.getByTestId("explorer-reference-data").getByTestId("explorer-row-PaymentTerm").click();
  await page.getByRole("tab", { name: "Storage" }).click();
  const tables = page.getByTestId("reference-table-bindings");
  const main = tables.getByTestId("table-binding-main");
  await expect(main.getByLabel("Table for main")).toHaveValue("");

  // Choosing the table maps each field to the column named like it.
  await main.getByLabel("Table for main").selectOption({ label: "payment_terms" });
  await expect(main.getByLabel("Column for Code")).toHaveValue(id(2));
  await expect(main.getByLabel("Column for Label")).toHaveValue(id(3));
  await expect(main.getByLabel("Column for dueDays")).toHaveValue(id(4));
  await expect(main.getByLabel("Column for Description")).toHaveValue("");
  await expect(page.getByTestId("storage-main").getByTestId("strategy-in-use")).toHaveText("Table payment_terms");
  await expect
    .poll(() => bindingsOf(page))
    .toEqual([
      {
        id: expect.any(String),
        database: MAIN,
        table: TABLE,
        fields: [
          { attribute: "code", column: id(2) },
          { attribute: "label", column: id(3) },
          { attribute: id(13), column: id(4) },
        ],
      },
    ]);

  // A constant column, then none again.
  await main.getByRole("button", { name: "Add constant" }).click();
  await main.getByLabel("Constant column 1").selectOption({ label: "kind" });
  await main.getByLabel("Constant value 1").fill("payment-term");
  await main.getByLabel("Constant value 1").blur();
  await expect.poll(async () => ((await bindingsOf(page)) as { constants?: unknown[] }[])[0].constants).toEqual([{ column: id(5), value: "payment-term" }]);
  // A second constant; removing the first leaves the second's value in its row, and a blur there keeps it.
  await main.getByRole("button", { name: "Add constant" }).click();
  await main.getByLabel("Constant column 2").selectOption({ label: "due_days" });
  await main.getByLabel("Constant value 2").fill("30");
  await main.getByLabel("Constant value 2").blur();
  await expect
    .poll(async () => ((await bindingsOf(page)) as { constants?: unknown[] }[])[0].constants)
    .toEqual([
      { column: id(5), value: "payment-term" },
      { column: id(4), value: 30 },
    ]);
  await main.getByRole("button", { name: "Remove constant kind" }).click();
  await expect(main.getByLabel("Constant value 1")).toHaveValue("30");
  await main.getByLabel("Constant value 1").focus();
  await main.getByLabel("Constant value 1").blur();
  await expect.poll(async () => ((await bindingsOf(page)) as { constants?: unknown[] }[])[0].constants).toEqual([{ column: id(4), value: 30 }]);
  await main.getByRole("button", { name: "Remove constant due_days" }).click();
  await expect.poll(async () => ((await bindingsOf(page)) as { constants?: unknown[] }[])[0].constants).toBeUndefined();

  // No table again: the binding goes, the strategy applies.
  await main.getByLabel("Table for main").selectOption("");
  await expect.poll(() => bindingsOf(page)).toBeUndefined();
  await expect(page.getByTestId("storage-main").getByTestId("strategy-in-use")).not.toHaveText("Table payment_terms");
});

test("a table's Data tab names the reference type whose rows it holds", async ({ page }) => {
  await openEditor(page);
  await seedModel(page);
  await workspace(page, "Reference data");
  await page.getByTestId("explorer-reference-data").getByTestId("explorer-row-PaymentTerm").click();
  await page.getByRole("tab", { name: "Storage" }).click();
  await page.getByTestId("table-binding-main").getByLabel("Table for main").selectOption({ label: "payment_terms" });
  await expect.poll(() => bindingsOf(page)).toHaveLength(1);

  await workspace(page, "Databases");
  await page.getByTestId("database-table-payment_terms").click();
  await page.getByTestId("table-inspector").getByTestId("table-inspector-edit").click();
  await page.getByTestId("element-editor").getByRole("tab", { name: "Data", exact: true }).click();
  const note = page.getByTestId("bound-types-note");
  await expect(note).toContainText("This table also holds the rows of PaymentTerm (a reference type bound to it)");
  await note.getByRole("button", { name: "PaymentTerm" }).click();
  await expect(page.getByTestId("reference-type-title")).toHaveText("PaymentTerm");
});
