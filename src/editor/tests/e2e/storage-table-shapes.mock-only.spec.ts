// Table per type, child tables and a relationship's attributes on its foreign key, on the Storage tab (retire-projection-design.md
// sections 4 to 6), on the mock: the base foreign key of a derived binding and the key columns it joins, what the base stores left out
// of the field map, a list in a child table with its owner key, order column and members, the SQL preview's levels, and the column a
// relationship's attribute takes in the table that holds its foreign key. Each pick is one save.
import type { Locator, Page } from "@playwright/test";
import { expect, openEditor, test } from "./fixtures";

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const ulid = (n: number) => `01J92P0VYY${String(n).padStart(16, "0")}`;
const ID = {
  db: ulid(1),
  payment: ulid(2),
  card: ulid(3),
  quote: ulid(4),
  customer: ulid(5),
  invoice: ulid(6),
  note: ulid(7),
  rel: ulid(8),
  mapping: ulid(9),
  tPayments: ulid(10),
  tCards: ulid(11),
  tQuotes: ulid(12),
  tNotes: ulid(13),
  tLabels: ulid(14),
  tCustomers: ulid(15),
  tInvoices: ulid(16),
  fkCard: ulid(20),
  fkNotes: ulid(21),
  fkLabels: ulid(22),
  fkInvoice: ulid(23),
  col: (n: number) => ulid(100 + n),
  att: (n: number) => ulid(200 + n),
  end: (n: number) => ulid(300 + n),
  bind: (n: number) => ulid(400 + n),
};

const attribute = (n: number, name: string, type: unknown, extra: object = {}) => ({ id: ID.att(n), name, type, ...extra });
const column = (n: number, name: string, type: string, extra: object = {}) => ({ id: ID.col(n), name, type, ...extra });
const table = (id: string, name: string, columns: object[], extra: object = {}) => ({
  kind: "table",
  id,
  name,
  database: ID.db,
  columns,
  primaryKey: { columns: [(columns[0] as { id: string }).id] },
  ...extra,
});

async function seed(page: Page) {
  const operations = [
    { kind: "database", id: ID.db, name: "Shapes", dialect: "postgresql", byConvention: "none" },
    {
      kind: "value-object",
      id: ID.note,
      name: "QuoteNote",
      package: BILLING,
      attributes: [attribute(30, "text", "string"), attribute(31, "author", "string")],
    },
    table(ID.tPayments, "payments", [column(1, "id", "int32", { identity: { seed: 1, increment: 1 } }), column(2, "amount", "decimal")]),
    table(ID.tCards, "card_payments", [column(3, "payment_id", "int32"), column(4, "last4", "string", { length: 4 })], {
      foreignKeys: [{ id: ID.fkCard, name: "fk_card_payment", columns: [ID.col(3)], referencesTable: ID.tPayments, referencesColumns: [ID.col(1)] }],
    }),
    table(ID.tQuotes, "quotes", [column(5, "id", "int32")]),
    table(
      ID.tNotes,
      "quote_notes",
      [column(6, "quote_id", "int32"), column(7, "position", "int32"), column(8, "text", "string"), column(9, "author", "string")],
      {
        primaryKey: { columns: [ID.col(6), ID.col(7)] },
        foreignKeys: [{ id: ID.fkNotes, name: "fk_quote_notes_quote", columns: [ID.col(6)], referencesTable: ID.tQuotes, referencesColumns: [ID.col(5)] }],
      },
    ),
    table(ID.tLabels, "quote_labels", [column(10, "quote_id", "int32"), column(11, "label", "string")], {
      foreignKeys: [{ id: ID.fkLabels, name: "fk_quote_labels_quote", columns: [ID.col(10)], referencesTable: ID.tQuotes, referencesColumns: [ID.col(5)] }],
    }),
    table(ID.tCustomers, "customers", [column(12, "id", "int32")]),
    table(ID.tInvoices, "invoices", [column(13, "id", "int32"), column(14, "customer_id", "int32"), column(15, "terms_note", "string")], {
      foreignKeys: [{ id: ID.fkInvoice, name: "fk_invoices_customer", columns: [ID.col(14)], referencesTable: ID.tCustomers, referencesColumns: [ID.col(12)] }],
    }),
    {
      kind: "entity",
      id: ID.payment,
      name: "Remittance",
      package: BILLING,
      key: { attributes: [ID.att(1)], strategy: "database-identity" },
      attributes: [attribute(1, "id", "int32", { required: true }), attribute(2, "amount", "decimal")],
      bindings: [
        {
          id: ID.bind(1),
          database: ID.db,
          source: ID.tPayments,
          fields: [
            { attribute: ID.att(1), column: ID.col(1) },
            { attribute: ID.att(2), column: ID.col(2) },
          ],
        },
      ],
    },
    {
      kind: "entity",
      id: ID.card,
      name: "CardRemittance",
      package: BILLING,
      base: ID.payment,
      attributes: [attribute(3, "last4", "string")],
      bindings: [
        {
          id: ID.bind(2),
          database: ID.db,
          source: ID.tCards,
          fields: [
            { attribute: ID.att(1), column: ID.col(3) },
            { attribute: ID.att(3), column: ID.col(4) },
          ],
        },
      ],
    },
    {
      kind: "entity",
      id: ID.quote,
      name: "Quote",
      package: BILLING,
      key: { attributes: [ID.att(4)], strategy: "database-identity" },
      attributes: [
        attribute(4, "id", "int32", { required: true }),
        attribute(5, "notes", { ref: ID.note }, { collection: true }),
        attribute(6, "labels", "string", { collection: true }),
      ],
      bindings: [{ id: ID.bind(3), database: ID.db, source: ID.tQuotes, fields: [{ attribute: ID.att(4), column: ID.col(5) }] }],
    },
    {
      kind: "entity",
      id: ID.customer,
      name: "Client",
      package: BILLING,
      key: { attributes: [ID.att(7)], strategy: "database-identity" },
      attributes: [attribute(7, "id", "int32", { required: true })],
      bindings: [{ id: ID.bind(4), database: ID.db, source: ID.tCustomers, fields: [{ attribute: ID.att(7), column: ID.col(12) }] }],
    },
    {
      kind: "entity",
      id: ID.invoice,
      name: "Statement",
      package: BILLING,
      key: { attributes: [ID.att(8)], strategy: "database-identity" },
      attributes: [attribute(8, "id", "int32", { required: true })],
      bindings: [{ id: ID.bind(5), database: ID.db, source: ID.tInvoices, fields: [{ attribute: ID.att(8), column: ID.col(13) }] }],
    },
    {
      kind: "relation",
      id: ID.rel,
      name: "Bills",
      package: BILLING,
      ends: [
        { id: ID.end(1), entity: ID.customer, role: "customer", navigation: "invoices" },
        { id: ID.end(2), entity: ID.invoice, role: "invoice", navigation: "customer", max: 1 },
      ],
      attributes: [attribute(9, "termsNote", "string")],
    },
    { kind: "mapping", id: ID.mapping, name: "Bills in Shapes", database: ID.db, relation: ID.rel, foreignKey: ID.fkInvoice },
  ].map((element) => ({ op: "create", element }));
  const outcome = await page.evaluate(
    async (body) => {
      const res = await fetch("/api/model/batch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      return (await res.json()) as { outcome: string; items?: { outcome: string; id: string; diagnostics: { rule: string; message: string }[] }[] };
    },
    { operations },
  );
  expect(outcome.outcome, JSON.stringify((outcome.items ?? []).filter((i) => i.outcome !== "saved"))).toBe("saved");
}

const saved = (page: Page, id: string) =>
  page.evaluate(async (at) => ((await (await fetch(`/api/model/elements/${at}`)).json()) as { json: Record<string, unknown> }).json, id);

async function open(page: Page, name: string, tab: string): Promise<Locator> {
  const side = page.getByRole("complementary", { name: "Explorer" });
  await side.getByLabel("Search the model").fill(name);
  await side.getByTestId(`explorer-row-${name}`).dblclick();
  const editor = page.getByRole("region", { name: `Editor: ${name}` });
  await expect(editor).toBeVisible();
  await editor.getByRole("tab", { name: tab }).click();
  return editor;
}

test.beforeEach(async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openEditor(page);
  await seed(page);
});

test("a derived binding names the foreign key to its base's table; the base's attributes leave its field map", async ({ page }) => {
  const editor = await open(page, "CardRemittance", "Storage");
  const card = editor.getByTestId("binding-card");
  // Before the key: the inherited attributes are the derived binding's to map (or the engine warns about them).
  await expect(card.getByTestId("field-row-id")).toBeVisible();
  const section = card.getByTestId("binding-base-table");
  await expect(section).toBeVisible();
  const picker = section.getByTestId("binding-base-fk");
  await expect(picker.locator("option")).toHaveText(["None: this entity's own rows only", "fk_card_payment (payment_id → payments)"]);

  await picker.selectOption({ label: "fk_card_payment (payment_id → payments)" });
  await expect.poll(async () => ((await saved(page, ID.card)).bindings as { baseForeignKey?: string }[])[0].baseForeignKey).toBe(ID.fkCard);
  // The key columns, and what the base stores, are said; the field map offers only the entity's own.
  await expect(section.getByTestId("base-key-row")).toHaveText("payment_ididid, from Remittance");
  await expect(section.getByTestId("base-table-stored")).toContainText("Remittance's binding stores id, amount");
  await expect(card.getByTestId("binding-fields").getByTestId("field-row-last4")).toBeVisible();
  await expect(card.getByTestId("field-row-amount")).toHaveCount(0);
  await expect(card.getByTestId("field-map-base-stored")).toContainText("id, amount");
  await expect(card.getByTestId("column-row-payment_id")).toHaveAttribute("data-status", "base-key");
  await expect(card.getByTestId("binding-problems")).toHaveCount(0);

  // The preview: the probe and the table above, labelled by level.
  await card.getByTestId("binding-sql").locator("summary").click();
  await expect(card.getByTestId("binding-sql-exists")).toContainText("COUNT(*)");
  const base = card.getByTestId("binding-sql-base");
  await expect(base).toHaveAttribute("data-level", "1");
  await expect(base).toContainText("INSERT INTO payments");

  // Clearing removes the key.
  await picker.selectOption({ label: "None: this entity's own rows only" });
  await expect.poll(async () => ((await saved(page, ID.card)).bindings as { baseForeignKey?: string }[])[0].baseForeignKey).toBeUndefined();
  await expect(card.getByTestId("field-row-amount")).toHaveCount(1);
});

test("a base binding's entity shows no base table section", async ({ page }) => {
  const editor = await open(page, "Remittance", "Storage");
  await expect(editor.getByTestId("binding-card")).toBeVisible();
  await expect(editor.getByTestId("binding-base-table")).toHaveCount(0);
});

test("a list is stored in a child table of its own: owner key, order column and members", async ({ page }) => {
  const editor = await open(page, "Quote", "Storage");
  const section = editor.getByTestId("binding-child-tables");
  await section.getByTestId("child-table-attribute").selectOption({ label: "notes (list)" });
  await section.getByTestId("child-table-add").click();
  const entry = section.locator(`[data-testid="child-table-entry"][data-attribute="notes"]`);
  await entry.getByTestId("child-table-table").selectOption(ID.tNotes);

  // The one foreign key that fits is the owner key; the position column and the members are found by name.
  await expect(entry.getByTestId("child-table-owner")).toHaveValue(ID.fkNotes);
  await expect(entry.getByTestId("child-table-order")).toHaveValue(ID.col(7));
  await expect(entry.getByTestId("child-field-row-notesText")).toHaveAttribute("data-status", "mapped");
  await expect(entry.getByTestId("child-column-row-quote_id")).toHaveAttribute("data-status", "owner");
  await expect(entry.getByTestId("child-column-row-position")).toHaveAttribute("data-status", "order");
  await expect(entry.getByTestId("child-table-problems")).toHaveCount(0);
  await expect
    .poll(async () => ((await saved(page, ID.quote)).bindings as { childTables?: unknown[] }[])[0].childTables)
    .toEqual([
      {
        id: expect.any(String),
        attribute: ID.att(5),
        table: ID.tNotes,
        ownerForeignKey: ID.fkNotes,
        orderColumn: ID.col(7),
        fields: [
          { attribute: `${ID.att(5)}.${ID.att(30)}`, column: ID.col(8) },
          { attribute: `${ID.att(5)}.${ID.att(31)}`, column: ID.col(9) },
        ],
      },
    ]);

  // A list of single values maps the attribute itself to one value column.
  await section.getByTestId("child-table-attribute").selectOption({ label: "labels (list)" });
  await section.getByTestId("child-table-add").click();
  const labels = section.locator(`[data-testid="child-table-entry"][data-attribute="labels"]`);
  await labels.getByTestId("child-table-table").selectOption(ID.tLabels);
  await expect(labels.getByTestId("child-field-row-labels")).toHaveAttribute("data-status", "mapped");

  // Naming no owner key says so where it points; the rest of the entry stays.
  await entry.getByTestId("child-table-owner").selectOption({ label: "Pick a foreign key" });
  await expect(entry.getByTestId("child-table-problems")).toContainText("MQ4138");

  // The stored attributes are no longer the field map's, and the preview labels each entry.
  const card = editor.getByTestId("binding-card");
  await expect(card.getByTestId("field-map-child-stored")).toContainText("notes, labels");
  await entry.getByTestId("child-table-owner").selectOption({ label: "fk_quote_notes_quote (quote_id → quotes)" });
  await card.getByTestId("binding-sql").locator("summary").click();
  const blocks = card.getByTestId("binding-sql-child");
  await expect(blocks).toHaveCount(2);
  await expect(blocks.first()).toContainText("ORDER BY t.position");
  await expect(blocks.first()).toContainText("DELETE FROM quote_notes");

  // Removing an entry leaves the table.
  await labels.getByTestId("child-table-remove").click();
  await expect(section.getByTestId("child-table-entry")).toHaveCount(1);
});

test("a relationship's attribute takes a column of the table that holds its foreign key", async ({ page }) => {
  const editor = await open(page, "Bills", "Storage");
  const database = editor.getByTestId("relation-storage-database").filter({ hasText: "Shapes" });
  await expect(database.getByTestId("relation-foreign-key")).toHaveValue(ID.fkInvoice);
  const attributes = database.getByTestId("relation-storage-attribute-columns");
  const select = attributes.getByTestId("relation-attribute-column");
  // The key's own column is not offered.
  await expect(select.locator("option")).toHaveText(["(none named)", "id", "terms_note"]);
  await select.selectOption({ label: "terms_note" });
  await expect.poll(async () => (await saved(page, ID.mapping)).attributes).toEqual([{ attribute: ID.att(9), column: ID.col(15) }]);

  // The table's binding counts the column as the relationship's: read only, nothing left to account for.
  const statement = await open(page, "Statement", "Storage");
  const row = statement.getByTestId("column-row-terms_note");
  await expect(row).toHaveAttribute("data-status", "relation");
  await expect(statement.getByTestId("binding-problems")).not.toContainText("terms_note");
});
