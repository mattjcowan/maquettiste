// The database diagram's foreign key edits (workspaces/database/fkEdits.ts): the draft a drag makes, default names from the
// naming convention, composite keys, the document a draft writes, the crow's foot ends, the card's display modes and edge
// handles. The write itself is the one way a table is written (storeTables.ts, tested in database-store-edits.test.ts).
import { describe, expect, it } from "vitest";
import type { ColumnView, TableView } from "@/api/types";
import {
  applyDraft,
  asAction,
  defaultFkName,
  draftFromDrop,
  draftForTable,
  draftFromForeignKey,
  fkEnds,
  fkEntryOf,
  fkNamePattern,
  fkProblems,
  legacyEntityOf,
  newColumnName,
  removeForeignKey,
} from "@/workspaces/database/fkEdits";
import { edgeHandles, keyColumns, shownColumns } from "@/canvas/tableColumns";
import { isKeyOf, keysOfDocument, referencedKeyProblem } from "@/model/foreignKeyTarget";
import { entryDiagnostics, globalsOf, type ModelEntry, type ValidationContext } from "@/mocks/model/validate";

const DB = "01DB0000000000000000000000";

const col = (key: string, name: string, over: Partial<ColumnView> = {}): ColumnView =>
  ({
    key,
    name,
    type: "uuid",
    nativeType: "uuid",
    length: null,
    precision: null,
    scale: null,
    nullable: true,
    isPrimaryKey: false,
    isForeignKey: false,
    ...over,
  }) as ColumnView;

const table = (key: string, name: string, columns: ColumnView[], over: Partial<TableView> = {}): TableView =>
  ({
    key,
    name,
    schema: "app",
    origin: "designed",
    entityId: null,
    relationId: null,
    columns,
    primaryKey: columns.some((c) => c.isPrimaryKey) ? { name: `pk_${name}`, columns: columns.filter((c) => c.isPrimaryKey).map((c) => c.key) } : null,
    uniques: [],
    foreignKeys: [],
    indexes: [],
    ...over,
  }) as TableView;

const invoices = table("T_INV", "invoices", [
  col("C_INV_ID", "id", { isPrimaryKey: true, nullable: false }),
  col("C_INV_NO", "number", { type: "string", nativeType: "varchar(32)" }),
]);
const lines = table("T_LINE", "invoice_lines", [
  col("C_LINE_ID", "id", { isPrimaryKey: true, nullable: false }),
  col("C_LINE_INV", "invoice_id", { nullable: false }),
  col("C_LINE_NOTE", "note", { type: "string", nativeType: "text" }),
]);
const periods = table("T_PER", "periods", [
  col("C_PER_Y", "year", { type: "int32", nativeType: "integer", isPrimaryKey: true, nullable: false }),
  col("C_PER_M", "month", { type: "int32", nativeType: "integer", isPrimaryKey: true, nullable: false }),
]);
const tables = [invoices, lines, periods];

const ids = () => {
  let n = 0;
  return () => `NEW${++n}`;
};

describe("the draft a drag makes", () => {
  it("pairs the dragged column with the column it was dropped on, named by the convention", () => {
    const draft = draftFromDrop(
      tables,
      { sourceTable: "T_LINE", sourceColumn: "C_LINE_INV", targetTable: "T_INV", targetColumn: "C_INV_ID" },
      "fk_{table}_{columns}",
    );
    expect(draft).toEqual({
      table: "T_LINE",
      editing: null,
      name: "fk_invoice_lines_invoice_id",
      referencedTable: "T_INV",
      pairs: [{ column: "C_LINE_INV", referenced: "C_INV_ID" }],
      onDelete: "no-action",
      onUpdate: "no-action",
      deferrable: "not-deferrable",
    });
  });

  it("references the target's primary key when dropped on its header, and guesses the column of a header drag by name", () => {
    expect(draftFromDrop(tables, { sourceTable: "T_LINE", sourceColumn: "C_LINE_NOTE", targetTable: "T_INV", targetColumn: null }, "x").pairs).toEqual([
      { column: "C_LINE_NOTE", referenced: "C_INV_ID" },
    ]);
    // From the header: invoice_id is <invoices singular>_<key>.
    expect(draftFromDrop(tables, { sourceTable: "T_LINE", sourceColumn: null, targetTable: "T_INV", targetColumn: null }, "x").pairs).toEqual([
      { column: "C_LINE_INV", referenced: "C_INV_ID" },
    ]);
  });

  it("makes one pair per column of a composite key, the dragged column first", () => {
    const draft = draftFromDrop(
      tables,
      { sourceTable: "T_LINE", sourceColumn: "C_LINE_NOTE", targetTable: "T_PER", targetColumn: null },
      "fk_{table}_{columns}",
    );
    expect(draft.pairs).toEqual([
      { column: "C_LINE_NOTE", referenced: "C_PER_Y" },
      { column: null, referenced: "C_PER_M" },
    ]);
    // Until every pair has a column the name is fk_<a>_<b>.
    expect(draft.name).toBe("fk_invoice_lines_periods");
    expect(fkProblems(tables, draft).errors).toContain("Choose the column of pair 2.");
    expect(fkProblems(tables, draft).warnings).toEqual(["note is text; periods.year is integer."]);
  });
});

describe("default names", () => {
  it("fills {table} and {columns}, and falls back to fk_<a>_<b> without columns", () => {
    expect(defaultFkName("fk_{table}_{columns}", "orders", ["customer_id", "region"])).toBe("fk_orders_customer_id_region");
    expect(defaultFkName("{table}_{columns}_fkey", "orders", ["customer_id"])).toBe("orders_customer_id_fkey");
    expect(defaultFkName("fk_{table}_{columns}", "orders", [], "customers")).toBe("fk_orders_customers");
  });

  it("takes the database's convention over the project's, else the built-in one", () => {
    const settings = { conventions: { foreignKeyName: "{table}_{columns}_fk" }, databases: { main: { foreignKeyName: "FK_{table}_{columns}" } } };
    expect(fkNamePattern(settings, "main")).toBe("FK_{table}_{columns}");
    expect(fkNamePattern(settings, "other")).toBe("{table}_{columns}_fk");
    expect(fkNamePattern({}, "main")).toBe("fk_{table}_{columns}");
  });

  it("names a new column after the referenced table's singular and its key column", () => {
    expect(newColumnName(invoices, { name: "id" })).toBe("invoice_id");
    expect(newColumnName(table("T", "categories", []), { name: "id" })).toBe("category_id");
    expect(newColumnName(invoices, { name: "id" }, ["invoice_id"])).toBe("invoice_id_2");
  });
});

describe("the document a draft writes", () => {
  it("adds the key with the referenced columns, leaving out a conventional name and no-action rules", () => {
    const doc: Record<string, unknown> = { kind: "table", id: "T_LINE", columns: [{ id: "C_LINE_INV", name: "invoice_id", type: "uuid" }] };
    const draft = draftFromDrop(
      tables,
      { sourceTable: "T_LINE", sourceColumn: "C_LINE_INV", targetTable: "T_INV", targetColumn: "C_INV_ID" },
      "fk_{table}_{columns}",
    );
    expect(applyDraft(doc, { ...draft, onDelete: "cascade" }, { tables, pattern: "fk_{table}_{columns}", newId: ids() })).toBeNull();
    expect(doc.foreignKeys).toEqual([{ id: "NEW1", columns: ["C_LINE_INV"], referencesTable: "T_INV", referencesColumns: ["C_INV_ID"], onDelete: "cascade" }]);
  });

  it("writes a typed name, adds a new column typed like the referenced one, and keeps the id when editing", () => {
    const doc: Record<string, unknown> = { kind: "table", id: "T_LINE", columns: [] };
    const draft = {
      ...draftFromDrop(tables, { sourceTable: "T_LINE", sourceColumn: null, targetTable: "T_PER", targetColumn: null }, "x"),
      name: "fk_line_period",
    };
    draft.pairs = [
      { column: null, newColumn: "period_year", referenced: "C_PER_Y" },
      { column: null, newColumn: "period_month", referenced: "C_PER_M" },
    ];
    expect(fkProblems(tables, draft).errors).toEqual([]);
    expect(applyDraft(doc, draft, { tables, pattern: "fk_{table}_{columns}", newId: ids() })).toBeNull();
    expect(doc.columns).toEqual([
      { id: "NEW1", name: "period_year", type: "int32" },
      { id: "NEW2", name: "period_month", type: "int32" },
    ]);
    expect(doc.foreignKeys).toEqual([
      { id: "NEW3", name: "fk_line_period", columns: ["NEW1", "NEW2"], referencesTable: "T_PER", referencesColumns: ["C_PER_Y", "C_PER_M"] },
    ]);

    // Edit: the entry is found by its name and replaced under the same id.
    const view = { ...lines, columns: [...lines.columns, col("NEW1", "period_year"), col("NEW2", "period_month")] };
    const fk = {
      name: "fk_line_period",
      columns: ["NEW1", "NEW2"],
      referencedTable: "T_PER",
      referencedColumns: ["C_PER_Y", "C_PER_M"],
      onDelete: "no action",
      onUpdate: "no action",
      relationId: null,
      endId: null,
    };
    const edit = { ...draftFromForeignKey(view, fk, [view, periods]), onUpdate: "cascade" as const };
    expect(edit.editing?.name).toBe("fk_line_period");
    expect(applyDraft(doc, edit, { tables: [view, periods], pattern: "fk_{table}_{columns}", newId: ids() })).toBeNull();
    expect((doc.foreignKeys as unknown[]).length).toBe(1);
    expect(doc.foreignKeys).toEqual([
      {
        id: "NEW3",
        name: "fk_line_period",
        columns: ["NEW1", "NEW2"],
        referencesTable: "T_PER",
        referencesColumns: ["C_PER_Y", "C_PER_M"],
        onUpdate: "cascade",
      },
    ]);
  });

  it("finds an unnamed entry by its columns and table, and removes it", () => {
    const doc: Record<string, unknown> = { foreignKeys: [{ id: "K", columns: ["C_LINE_INV"], referencesTable: "T_INV" }] };
    const fk = { name: "fk_invoice_lines_invoice_id", columns: ["C_LINE_INV"], referencedTable: "T_INV" };
    expect(fkEntryOf(doc, fk)?.id).toBe("K");
    expect(removeForeignKey(doc, fk)).toBe(true);
    expect(doc.foreignKeys).toBeUndefined();
    expect(removeForeignKey(doc, fk)).toBe(false);
  });

  it("reads the view's rule words", () => {
    expect(asAction("no action")).toBe("no-action");
    expect(asAction("set null")).toBe("set-null");
    expect(asAction("CASCADE")).toBe("cascade");
  });
});

describe("crow's foot ends and the cards", () => {
  const fk = { columns: ["C_LINE_INV"] };
  it("is many to exactly one for a not null column, zero or one when nullable, one to one when unique", () => {
    expect(fkEnds(lines, fk)).toEqual({ source: "many", target: "one" });
    expect(fkEnds(lines, { columns: ["C_LINE_NOTE"] })).toEqual({ source: "many", target: "zero-or-one" });
    const unique = { ...lines, uniques: [{ name: "uq", columns: ["C_LINE_INV"] }] };
    expect(fkEnds(unique, fk)).toEqual({ source: "zero-or-one", target: "one" });
  });

  it("shows all columns, the key columns, or none, and anchors edges at shown rows on facing sides", () => {
    const withFk = { ...lines, columns: lines.columns.map((c) => (c.key === "C_LINE_INV" ? { ...c, isForeignKey: true } : c)) };
    expect(shownColumns(withFk, "all").map((c) => c.name)).toEqual(["id", "invoice_id", "note"]);
    expect(shownColumns(withFk, "keys").map((c) => c.name)).toEqual(["id", "invoice_id"]);
    expect(shownColumns(withFk, "names")).toEqual([]);
    expect([...keyColumns(invoices)]).toEqual(["C_INV_ID"]);
    const key = { columns: ["C_LINE_INV"], referencedColumns: ["C_INV_ID"] };
    const shown = { source: new Set(["C_LINE_INV"]), target: new Set(["C_INV_ID"]) };
    expect(edgeHandles(key, shown, { source: 0, target: 400 })).toEqual({ sourceHandle: "R:C_LINE_INV", targetHandle: "L:C_INV_ID" });
    expect(edgeHandles(key, shown, { source: 400, target: 0 })).toEqual({ sourceHandle: "L:C_LINE_INV", targetHandle: "R:C_INV_ID" });
    expect(edgeHandles(key, { source: new Set(), target: new Set() }, { source: 0, target: 400 })).toEqual({ sourceHandle: "R:", targetHandle: "L:" });
  });
});

describe("what a key may reference, and the dialog's starting points", () => {
  const pattern = "fk_{table}_{columns}";
  it("expands a drop on one column of a composite key to the whole key, the dragged column in the dropped one's place", () => {
    const draft = draftFromDrop(tables, { sourceTable: "T_LINE", sourceColumn: "C_LINE_NOTE", targetTable: "T_PER", targetColumn: "C_PER_M" }, pattern);
    expect(draft.pairs).toEqual([
      { column: null, referenced: "C_PER_Y" },
      { column: "C_LINE_NOTE", referenced: "C_PER_M" },
    ]);
    // A drop on a column of a composite unique constraint takes that constraint.
    const coded = table("T_CODE", "codes", [col("C_A", "a"), col("C_B", "b"), col("C_ID", "id", { isPrimaryKey: true })], {
      uniques: [{ name: "uq_codes", columns: ["C_A", "C_B"] }],
    });
    const withCodes = [...tables, coded];
    expect(draftFromDrop(withCodes, { sourceTable: "T_LINE", sourceColumn: "C_LINE_NOTE", targetTable: "T_CODE", targetColumn: "C_B" }, pattern).pairs).toEqual(
      [
        { column: null, referenced: "C_A" },
        { column: "C_LINE_NOTE", referenced: "C_B" },
      ],
    );
  });

  it("refuses referenced columns that are neither the primary key nor a unique key (MQ4059)", () => {
    const draft = draftFromDrop(tables, { sourceTable: "T_LINE", sourceColumn: "C_LINE_NOTE", targetTable: "T_INV", targetColumn: "C_INV_NO" }, pattern);
    expect(fkProblems(tables, draft).errors.join(" ")).toContain("MQ4059");
    const unique = { ...invoices, uniques: [{ name: "uq_number", columns: ["C_INV_NO"] }] };
    expect(fkProblems([unique, lines, periods], { ...draft }).errors.join(" ")).not.toContain("MQ4059");
    // A unique index counts, except on Oracle (a constraint only); a filtered one never.
    const indexed = { ...invoices, indexes: [{ name: "ix_number", unique: true, where: null, columns: [{ column: "C_INV_NO", descending: false }] }] };
    expect(fkProblems([indexed, lines, periods], draft, "postgresql").errors.join(" ")).not.toContain("MQ4059");
    expect(fkProblems([indexed, lines, periods], draft, "oracle").errors.join(" ")).toContain("MQ4059");
    // Half of a composite key is not a key.
    const half = { ...draftFromDrop(tables, { sourceTable: "T_LINE", sourceColumn: "C_LINE_NOTE", targetTable: "T_PER", targetColumn: null }, pattern) };
    half.pairs = [{ column: "C_LINE_NOTE", referenced: "C_PER_Y" }];
    expect(fkProblems(tables, half).errors.join(" ")).toContain("MQ4059");
  });

  it("refuses a referenced column named twice, and on MySQL an order no index of the referenced table starts with (MQ4059)", () => {
    const twice = { ...draftFromDrop(tables, { sourceTable: "T_LINE", sourceColumn: "C_LINE_INV", targetTable: "T_PER", targetColumn: null }, pattern) };
    twice.pairs = [
      { column: "C_LINE_INV", referenced: "C_PER_Y" },
      { column: "C_LINE_NOTE", referenced: "C_PER_Y" },
    ];
    expect(fkProblems(tables, twice).errors.join(" ")).toContain("periods.year is referenced twice");
    // The key in the other order: fine elsewhere; MySQL needs an index that starts with (month, year).
    const reversed = {
      ...twice,
      pairs: [
        { column: "C_LINE_INV", referenced: "C_PER_M" },
        { column: "C_LINE_NOTE", referenced: "C_PER_Y" },
      ],
    };
    expect(fkProblems(tables, reversed, "postgresql").errors.join(" ")).not.toContain("MQ4059");
    expect(fkProblems(tables, reversed, "mysql").errors.join(" ")).toContain("MySQL needs an index of periods whose first columns are (month, year)");
    expect(fkProblems(tables, { ...reversed, pairs: [...reversed.pairs].reverse() }, "mysql").errors.join(" ")).not.toContain("MQ4059");
    // Any index that starts with them will do, unique or not, longer or not.
    const indexed = {
      ...periods,
      indexes: [
        {
          name: "ix_my",
          unique: false,
          where: null,
          columns: [
            { column: "C_PER_M", descending: false },
            { column: "C_PER_Y", descending: false },
          ],
        },
      ],
    };
    expect(fkProblems([invoices, lines, indexed], reversed, "mysql").errors.join(" ")).not.toContain("MQ4059");
  });

  it("mirrors the engine's MQ4059 in the shared rule and the mock's validation", () => {
    const keys = keysOfDocument({ primaryKey: { columns: ["Y", "M"] }, indexes: [{ columns: [{ expression: { "*": "lower(x)" } }, { column: "M" }] }] });
    expect(referencedKeyProblem(keys, ["Y", "Y"], "postgresql")).toEqual({ kind: "twice", column: "Y" });
    expect(referencedKeyProblem(keys, ["Y"], "postgresql")).toEqual({ kind: "not-key" });
    expect(isKeyOf(keys, ["Y", "M", "M"], "postgresql")).toBe(false);
    expect(referencedKeyProblem(keys, ["M", "Y"], "postgresql")).toBeNull();
    expect(referencedKeyProblem(keys, ["M", "Y"], "mysql")).toEqual({ kind: "order" });
    expect(referencedKeyProblem(keys, ["Y", "M"], "mysql")).toBeNull();

    const db = {
      id: "01J92P0V0000000000000000F1",
      path: "db.json",
      json: { kind: "database", id: "01J92P0V0000000000000000F1", name: "main", dialect: "mysql" },
    };
    const target = {
      id: "01J92P0V0000000000000000F2",
      path: "periods.json",
      json: {
        kind: "table",
        id: "01J92P0V0000000000000000F2",
        name: "periods",
        database: db.id,
        columns: [
          { id: "01J92P0V0000000000000000F3", name: "year", type: "int32" },
          { id: "01J92P0V0000000000000000F4", name: "month", type: "int32" },
        ],
        primaryKey: { columns: ["01J92P0V0000000000000000F3", "01J92P0V0000000000000000F4"] },
      },
    };
    const holder = (refs: string[]) => ({
      id: "01J92P0V0000000000000000F5",
      path: "sales.json",
      json: {
        kind: "table",
        id: "01J92P0V0000000000000000F5",
        name: "sales",
        database: db.id,
        columns: [
          { id: "01J92P0V0000000000000000F6", name: "a", type: "int32" },
          { id: "01J92P0V0000000000000000F7", name: "b", type: "int32" },
        ],
        foreignKeys: [
          {
            id: "01J92P0V0000000000000000F8",
            name: "fk_sales_period",
            columns: ["01J92P0V0000000000000000F6", "01J92P0V0000000000000000F7"],
            referencesTable: target.id,
            referencesColumns: refs,
          },
        ],
      },
    });
    const run = (entry: ModelEntry) => {
      const all = [db, target, entry];
      const ctx: ValidationContext = {
        ...globalsOf(all),
        hasId: (id) => all.some((e) => e.id === id),
        extensions: [],
        firstInScope: () => undefined,
        lookup: (id) => all.find((e) => e.id === id),
      };
      return entryDiagnostics(entry, ctx)
        .filter((d) => d.rule === "MQ4059")
        .map((d) => d.message);
    };
    const [y, m] = ["01J92P0V0000000000000000F3", "01J92P0V0000000000000000F4"];
    expect(run(holder([y, m]))).toEqual([]);
    expect(run(holder([m, y]))).toEqual([
      "Foreign key 'fk_sales_period' references (month, year) of table 'periods' in an order no index of it starts with; MySQL needs an index whose first columns are the referenced ones in the same order: reference them in the key's order, or add such an index.",
    ]);
    expect(run(holder([y, y]))).toEqual([
      "Foreign key 'fk_sales_period' references (year, year) of table 'periods', naming column 'year' more than once; reference each column of the key once.",
    ]);
  });

  it("edits an entry from what it held: deferrable, and members the dialog does not show, survive", () => {
    const doc: Record<string, unknown> = {
      kind: "table",
      id: "T_LINE",
      columns: [{ id: "C_LINE_INV", name: "invoice_id", type: "uuid" }],
      foreignKeys: [
        {
          id: "K",
          name: "fk_line_invoice",
          columns: ["C_LINE_INV"],
          referencesTable: "T_INV",
          deferrable: "initially-deferred",
          comment: "keep me",
          onUpdate: "cascade",
        },
      ],
    };
    const fk = {
      name: "fk_line_invoice",
      columns: ["C_LINE_INV"],
      referencedTable: "T_INV",
      referencedColumns: ["C_INV_ID"],
      onDelete: "no action",
      onUpdate: "cascade",
      relationId: null,
      endId: null,
    };
    const draft = draftFromForeignKey(lines, fk, tables, (doc.foreignKeys as Record<string, unknown>[])[0]);
    expect(draft.deferrable).toBe("initially-deferred");
    expect(applyDraft(doc, { ...draft, onDelete: "cascade" }, { tables, pattern, newId: ids() })).toBeNull();
    expect(doc.foreignKeys).toEqual([
      {
        id: "K",
        name: "fk_line_invoice",
        columns: ["C_LINE_INV"],
        referencesTable: "T_INV",
        referencesColumns: ["C_INV_ID"],
        deferrable: "initially-deferred",
        comment: "keep me",
        onUpdate: "cascade",
        onDelete: "cascade",
      },
    ]);
    // Not deferrable again: left out of the file (the default).
    expect(applyDraft(doc, { ...draft, deferrable: "not-deferrable" }, { tables, pattern, newId: ids() })).toBeNull();
    expect((doc.foreignKeys as Record<string, unknown>[])[0].deferrable).toBeUndefined();
  });

  it("starts a new key from the explorer with nothing chosen, and names a legacy table's entity", () => {
    const draft = draftForTable(tables, "T_LINE", pattern);
    expect(draft).toMatchObject({ table: "T_LINE", referencedTable: "", editing: null, deferrable: "not-deferrable" });
    expect(legacyEntityOf({ key: `ENT@${DB}`, entityId: "ENT", relationId: null, origin: "synthesized" }, DB)).toBe("ENT");
    expect(legacyEntityOf({ key: "T_LINE", entityId: null, relationId: null, origin: "designed" }, DB)).toBeNull();
  });
});
