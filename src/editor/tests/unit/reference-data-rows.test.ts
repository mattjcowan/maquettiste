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
  moveRows,
  parseCell,
  parseDelimited,
  pasteMatrix,
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
  it("lists code and label first, then the user fields", () => {
    const columns = gridColumns(type);
    expect(columns.map((c) => c.key)).toEqual(["code", "label", "F", "S", "A"]);
    expect(columns[0]).toMatchObject({ builtin: true, required: true });
    expect(columns[4]).toMatchObject({ type: "reference", collection: true });
  });

  it("reads rows over several seeds, values by column key", () => {
    const demo = { ...seed(), id: "DEMO", columns: ["label", "code"], rows: [{ id: "d1", values: ["Ounce", "oz"] }] } as unknown as SeedDoc;
    const rows = gridRows([seed(), demo]);
    expect(rows.map((r) => r.id)).toEqual(["r1", "r2", "r3", "d1"]);
    expect(rows[2].values).toEqual({ code: "pinch", label: "Pinch", F: 0.36 });
    expect(rows[3]).toMatchObject({ seed: "DEMO", index: 0, values: { code: "oz", label: "Ounce" } });
  });

  it("parses typed text as the column's literal and formats cells back", () => {
    const [code, , factor, , aliases] = gridColumns(type);
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
    const result = pasteMatrix(s, columns, { row: 1, col: 2 }, matrix, ids);
    expect(result.changed).toEqual(["r2", "r3"]);
    expect(result.added).toHaveLength(1);
    expect(s.rows![1]).toEqual({ id: "r2", values: ["g", "Gram", 2, "g"] });
    expect(s.rows![2]).toEqual({ id: "r3", values: ["pinch", "Pinch", 0.5, "lb"] });
    expect(s.rows![3]).toEqual({ id: result.added[0], values: [null, null, 0.25, "oz"] });
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
  it("reports the share of rows with a translated label", () => {
    const rows = [{ id: "a" }, { id: "b" }, { id: "c" }];
    expect(labelCompleteness(rows, new Map([["a", "A"]]))).toBe(33);
    expect(labelCompleteness(rows, undefined)).toBe(0);
    expect(labelCompleteness([], undefined)).toBe(100);
  });
});
