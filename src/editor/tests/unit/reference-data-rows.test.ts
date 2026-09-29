// The Rows grid's model (reference-types-seeds-localization.md 2.1 and 4.4): columns, cell parsing, the edits over a
// seed document (canonical trailing-null trim), tab-separated paste adding rows, and the keyboard map.
import { describe, expect, it } from "vitest";
import type { ReferenceTypeDoc, SeedDoc } from "@/api/types";
import {
  deleteRows,
  duplicateRow,
  formatCell,
  gridAction,
  gridColumns,
  gridRows,
  insertRow,
  labelCompleteness,
  localeColumns,
  moveRows,
  newSeedDocument,
  ONE_LOCALE_HINT,
  BUILTIN_COLUMNS,
  withTranslatedLabels,
  parseCell,
  parseDelimited,
  pasteMatrix,
  type GridColumn,
  setCells,
  shownLocales,
  toTsv,
} from "@/workspaces/reference-data/rowsModel";

const type = {
  kind: "reference-type",
  id: "T",
  name: "UnitOfMeasure",
  code: { id: "C" },
  label: { id: "L" },
  attributes: [
    { id: "F", name: "factor", type: "decimal", required: true },
    { id: "S", name: "symbol", type: "string" },
    { id: "A", name: "aliases", type: { ref: "T" }, collection: true },
  ],
} as unknown as ReferenceTypeDoc;

const seed = (): SeedDoc =>
  ({
    kind: "seed",
    id: "SEED",
    name: "UnitOfMeasure",
    target: "T",
    columns: ["code", "label", "F", "S"],
    rows: [
      { id: "r1", values: ["kg", "Kilogram", 1000, "kg"] },
      { id: "r2", values: ["g", "Gram", 1, "g"] },
      { id: "r3", values: ["pinch", "Pinch", 0.36] },
    ],
  }) as unknown as SeedDoc;

let n = 0;
const ids = () => `new${++n}`;

describe("rows grid model", () => {
  it("lists code, label and description first, then the user fields", () => {
    const columns = gridColumns(type);
    expect(columns.map((c) => c.key)).toEqual(["code", "label", "description", "F", "S", "A"]);
    expect(columns[0]).toMatchObject({ builtin: true, required: true });
    expect(columns[2]).toMatchObject({ builtin: true, required: false, type: "text", multiline: true });
    expect(columns[5]).toMatchObject({ type: "reference", collection: true });
    expect(gridColumns({ ...type, code: { id: "C", type: "uuid" } } as ReferenceTypeDoc)[0].type).toBe("uuid");
  });

  it("adds the description column to a seed that lacks it on the first description edit; new seeds list it", () => {
    const s = seed();
    setCells(s, "r2", { description: "One thousandth of a kilogram.\nThe base unit of the fixture." });
    expect(s.columns).toEqual(["code", "label", "F", "S", "description"]);
    expect(s.rows![1]).toEqual({ id: "r2", values: ["g", "Gram", 1, "g", "One thousandth of a kilogram.\nThe base unit of the fixture."] });
    expect(gridRows([s])[1].values.description).toContain("\n");
    expect(BUILTIN_COLUMNS).toEqual(["code", "label", "description"]);
    expect(newSeedDocument("S2", { id: "T", name: "UnitOfMeasure" }, BUILTIN_COLUMNS).columns).toEqual(["code", "label", "description"]);
  });

  it("stores a uuid code in its canonical lowercase form", () => {
    const uuid = { type: "uuid", collection: false };
    expect(parseCell(" 0190A3B4-5C6D-7E8F-9A0B-1C2D3E4F5A6B ", uuid)).toBe("0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b");
    expect(parseCell("batch-3", uuid)).toBe("batch-3"); // left as typed: MQ7013 reports it
  });

  it("reads rows over several seeds, values by column key", () => {
    const demo = { ...seed(), id: "DEMO", columns: ["label", "code"], rows: [{ id: "d1", values: ["Ounce", "oz"] }] } as unknown as SeedDoc;
    const rows = gridRows([seed(), demo]);
    expect(rows.map((r) => r.id)).toEqual(["r1", "r2", "r3", "d1"]);
    expect(rows[2].values).toEqual({ code: "pinch", label: "Pinch", F: 0.36 });
    expect(rows[3]).toMatchObject({ seed: "DEMO", index: 0, values: { code: "oz", label: "Ounce" } });
  });

  it("parses typed text as the column's literal and formats cells back", () => {
    const [code, , , factor, , aliases] = gridColumns(type);
    expect(parseCell("1000", factor)).toBe(1000);
    expect(parseCell("0.360", factor)).toBe(0.36);
    expect(parseCell("", code)).toBeNull();
    expect(parseCell("kg; g", aliases)).toEqual(["kg", "g"]);
    expect(parseCell("007", code)).toBe("007");
    expect(formatCell(["kg", "g"])).toBe("kg;g");
    expect(formatCell({ amount: 5 })).toBe('{"amount":5}');
    expect(formatCell(null)).toBe("");
  });

  it("sets cells, adding a column the seed does not list and trimming trailing nulls", () => {
    const s = seed();
    setCells(s, "r3", { A: ["g"] });
    expect(s.columns).toEqual(["code", "label", "F", "S", "A"]);
    expect(s.rows![2]).toEqual({ id: "r3", values: ["pinch", "Pinch", 0.36, null, ["g"]] });
    setCells(s, "r3", { A: null, F: null });
    expect(s.rows![2]).toEqual({ id: "r3", values: ["pinch", "Pinch"] });
  });

  it("inserts, duplicates (with an empty code), moves and deletes rows", () => {
    const s = seed();
    insertRow(s, 0, "x");
    expect(s.rows!.map((r) => r.id)).toEqual(["r1", "x", "r2", "r3"]);
    expect(s.rows![1]).toEqual({ id: "x" });
    duplicateRow(s, 0, "dup");
    expect(s.rows![1]).toEqual({ id: "dup", values: [null, "Kilogram", 1000, "kg"] });
    expect(moveRows(s, 0, 1, -1)).toBe(false);
    expect(moveRows(s, 0, 1, 1)).toBe(true);
    expect(s.rows!.map((r) => r.id)).toEqual(["x", "r1", "dup", "r2", "r3"]);
    deleteRows(s, new Set(["x", "dup"]));
    expect(s.rows!.map((r) => r.id)).toEqual(["r1", "r2", "r3"]);
  });

  it("parses tab-separated clipboard text, quotes included, and writes it back", () => {
    expect(parseDelimited("kg\tKilogram\r\ng\tGram\r\n")).toEqual([
      ["kg", "Kilogram"],
      ["g", "Gram"],
    ]);
    expect(parseDelimited('a\t"two\nlines"\t"say ""hi"""\n')).toEqual([["a", "two\nlines", 'say "hi"']]);
    expect(parseDelimited("x\t\ty")).toEqual([["x", "", "y"]]);
    expect(
      toTsv([
        ["a", "b\tc"],
        ["d", ""],
      ]),
    ).toBe('a\t"b\tc"\nd\t');
  });

  it("pastes a range over existing rows and adds rows past the end", () => {
    const s = seed();
    const columns = gridColumns(type);
    const matrix = parseDelimited("2\tg\n0.5\tlb\n0.25\toz\n");
    const result = pasteMatrix(s, columns, { row: 1, col: 3 }, matrix, ids);
    expect(result.changed).toEqual(["r2", "r3"]);
    expect(result.added).toHaveLength(1);
    expect(s.rows![1]).toEqual({ id: "r2", values: ["g", "Gram", 2, "g"] });
    expect(s.rows![2]).toEqual({ id: "r3", values: ["pinch", "Pinch", 0.5, "lb"] });
    expect(s.rows![3]).toEqual({ id: result.added[0], values: [null, null, 0.25, "oz"] });
  });

  it("pastes an end column by row id or by the label the grid shows, and keeps a cell that names no row", () => {
    const s = {
      kind: "seed",
      id: "SEED",
      name: "Invoice",
      target: "I",
      columns: ["number", "E"],
      rows: [{ id: "i1", values: ["A-1", null] }],
    } as unknown as SeedDoc;
    const columns: GridColumn[] = [
      { key: "number", label: "number", builtin: false, type: "string", collection: false, required: false },
      { key: "E", label: "customer", builtin: false, type: "end", collection: false, required: false, end: "C" },
    ];
    const options = new Map([
      [
        "C",
        [
          { id: "c1", label: "Acme" },
          { id: "c2", label: "Globex" },
        ],
      ],
    ]);
    const result = pasteMatrix(s, columns, { row: 0, col: 1 }, parseDelimited("acme\nc2\nNobody\n"), ids, options);
    expect(result.unmatched).toBe(1);
    expect(s.rows![0].values).toEqual(["A-1", "c1"]);
    expect(s.rows![1].values).toEqual([null, "c2"]);
    expect(s.rows![2].values ?? []).not.toContain("Nobody");
  });

  it("maps the spreadsheet keys", () => {
    expect(gridAction({ key: "ArrowDown" }, false)).toEqual({ type: "move", dr: 1, dc: 0, extend: false });
    expect(gridAction({ key: "ArrowRight", shiftKey: true }, false)).toEqual({ type: "move", dr: 0, dc: 1, extend: true });
    expect(gridAction({ key: "Enter" }, false)).toEqual({ type: "edit" });
    expect(gridAction({ key: "F2" }, false)).toEqual({ type: "edit" });
    expect(gridAction({ key: "x" }, false)).toEqual({ type: "type", text: "x" });
    expect(gridAction({ key: "Enter" }, true)).toEqual({ type: "commit", move: "down" });
    expect(gridAction({ key: "Tab" }, true)).toEqual({ type: "commit", move: "right" });
    expect(gridAction({ key: "Tab", shiftKey: true }, true)).toEqual({ type: "commit", move: "left" });
    expect(gridAction({ key: "Escape" }, true)).toEqual({ type: "cancel" });
    expect(gridAction({ key: "a" }, true)).toBeNull();
    expect(gridAction({ key: "Enter", ctrlKey: true }, false)).toEqual({ type: "insert-below" });
    expect(gridAction({ key: "d", ctrlKey: true }, false)).toEqual({ type: "duplicate" });
    expect(gridAction({ key: "Delete" }, false)).toEqual({ type: "clear" });
    expect(gridAction({ key: "Delete", ctrlKey: true }, false)).toEqual({ type: "delete-rows" });
    expect(gridAction({ key: "ArrowUp", altKey: true }, false)).toEqual({ type: "move-rows", delta: -1 });
    expect(gridAction({ key: "c", metaKey: true }, false)).toEqual({ type: "copy" });
    expect(gridAction({ key: "f", ctrlKey: true }, false)).toEqual({ type: "find" });
    expect(gridAction({ key: "3", ctrlKey: true }, false)).toEqual({ type: "tab", index: 2 });
    expect(gridAction({ key: "/" }, false)).toEqual({ type: "focus-search" });
    // As in the Fields tab's attribute grid: Space edits, Tab leaves at the last column and Shift+Tab at the first.
    expect(gridAction({ key: " " }, false)).toEqual({ type: "edit" });
    expect(gridAction({ key: "Tab" }, false, { col: 1, cols: 3 })).toEqual({ type: "move", dr: 0, dc: 1, extend: false });
    expect(gridAction({ key: "Tab" }, false, { col: 2, cols: 3 })).toBeNull();
    expect(gridAction({ key: "Tab", shiftKey: true }, false, { col: 0, cols: 3 })).toBeNull();
    expect(gridAction({ key: "Tab", shiftKey: true }, false, { col: 1, cols: 3 })).toEqual({ type: "move", dr: 0, dc: -1, extend: false });
  });
});

describe("Rows grid locale columns (RT 4.4)", () => {
  it("shows the content locale's label column only, or every locale with All locales on", () => {
    expect(shownLocales(["de", "fr"], null, false)).toEqual([]);
    expect(shownLocales(["de", "fr"], "fr", false)).toEqual(["fr"]);
    expect(shownLocales(["de", "fr"], "es", false)).toEqual([]);
    expect(shownLocales(["de", "fr"], null, true)).toEqual(["de", "fr"]);
  });
  it("puts a translated description column beside each locale's label column", () => {
    expect(localeColumns(["fr"]).map((c) => [c.key, c.label, c.field, c.type, c.multiline ?? false])).toEqual([
      ["@label:fr", "label (fr)", "label", "string", false],
      ["@description:fr", "description (fr)", "description", "text", true],
    ]);
    const rows = withTranslatedLabels(
      gridRows([seed()]),
      new Map([["fr", new Map([["r1", "Kilogramme"]])]]),
      new Map([["fr", new Map([["r1", "Mille grammes"]])]]),
    );
    expect(rows[0].values).toMatchObject({ "@label:fr": "Kilogramme", "@description:fr": "Mille grammes" });
    expect(rows[1].values["@description:fr"]).toBeUndefined();
  });
  it("words the one-locale hint of the status bar", () => {
    expect(ONE_LOCALE_HINT).toBe("Declare a second locale under Settings › Locales to translate labels and descriptions.");
  });
  it("reports the share of rows with a translated label", () => {
    const rows = [{ id: "a" }, { id: "b" }, { id: "c" }];
    expect(labelCompleteness(rows, new Map([["a", "A"]]))).toBe(33);
    expect(labelCompleteness(rows, undefined)).toBe(0);
    expect(labelCompleteness([], undefined)).toBe(100);
  });
});
