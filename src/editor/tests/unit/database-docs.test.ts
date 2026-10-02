// The table, view and sequence editors' edits to their documents (editors/database/databaseDocs.ts).
import { describe, expect, it } from "vitest";
import {
  addKeyEntry,
  addTableColumn,
  addViewColumn,
  deleteTableColumn,
  nextColumnName,
  removeKeyEntry,
  removeViewColumn,
  removeViewDialect,
  sequenceNumberProblem,
  setSequenceNumber,
  setViewBody,
  toggleColumn,
  viewDialects,
} from "@/editors/database/databaseDocs";

type Json = Record<string, unknown>;
const counter = () => {
  let n = 0;
  return () => `N${++n}`;
};

const table = (): Json => ({
  kind: "table",
  id: "T",
  name: "audit_log",
  database: "D",
  columns: [
    { id: "C1", name: "id", type: "int64", nullable: false },
    { id: "C2", name: "at", type: "datetime" },
  ],
  primaryKey: { columns: ["C1"] },
  uniques: [{ id: "U1", columns: ["C2"] }],
  indexes: [{ id: "I1", columns: [{ column: "C2", descending: true }], include: ["C1"] }],
  foreignKeys: [{ id: "F1", columns: ["C2", "C1"], referencesTable: "X", referencesColumns: ["a", "b"] }],
});

describe("a designed table's columns", () => {
  it("adds a nullable string column with a free name", () => {
    expect(nextColumnName(["column_1", "Column_2"])).toBe("column_3");
    const doc = table();
    expect(addTableColumn(doc, counter())).toBe("N1");
    expect((doc.columns as Json[])[2]).toEqual({ id: "N1", name: "column_1", type: "string" });
  });

  it("deletes a column and takes it out of every key, dropping a key left without columns", () => {
    const doc = table();
    expect(deleteTableColumn(doc, "C2")).toBe(true);
    expect(doc.columns).toEqual([{ id: "C1", name: "id", type: "int64", nullable: false }]);
    expect(doc.uniques).toBeUndefined();
    expect(doc.indexes).toBeUndefined();
    expect(doc.foreignKeys).toEqual([{ id: "F1", columns: ["C1"], referencesTable: "X", referencesColumns: ["a"] }]);
    expect(doc.primaryKey).toEqual({ columns: ["C1"] });
    expect(deleteTableColumn(doc, "C1")).toBe(true);
    expect(doc.columns).toBeUndefined();
    expect(doc.primaryKey).toBeUndefined();
    expect(doc.foreignKeys).toBeUndefined();
    expect(deleteTableColumn(doc, "nope")).toBe(false);
  });
});

describe("a designed table's keys", () => {
  it("adds each kind of key valid as added, and none without a column", () => {
    const doc = table();
    delete doc.primaryKey;
    const id = counter();
    expect(addKeyEntry(doc, "primaryKey", id)).toBe(true);
    expect(doc.primaryKey).toEqual({ columns: ["C1"] });
    expect(addKeyEntry(doc, "primaryKey", id)).toBe(false);
    expect(addKeyEntry(doc, "uniques", id)).toBe(true);
    expect((doc.uniques as Json[])[1]).toEqual({ id: "N1", columns: ["C1"] });
    expect(addKeyEntry(doc, "indexes", id)).toBe(true);
    expect((doc.indexes as Json[])[1]).toEqual({ id: "N2", columns: [{ column: "C1" }] });
    expect(addKeyEntry(doc, "foreignKeys", id)).toBe(false);
    expect(addKeyEntry(doc, "foreignKeys", id, "OTHER")).toBe(true);
    expect((doc.foreignKeys as Json[])[1]).toEqual({ id: "N3", columns: ["C1"], referencesTable: "OTHER" });
    const empty: Json = { kind: "table", id: "E", name: "e", database: "D" };
    expect(addKeyEntry(empty, "uniques", id)).toBe(false);
  });

  it("removes an entry, and keeps at least one column in a key", () => {
    const doc = table();
    removeKeyEntry(doc, "uniques", 0);
    expect(doc.uniques).toBeUndefined();
    removeKeyEntry(doc, "primaryKey");
    expect(doc.primaryKey).toBeUndefined();
    expect(toggleColumn(["C1"], "C2")).toEqual(["C1", "C2"]);
    expect(toggleColumn(["C1", "C2"], "C1")).toEqual(["C2"]);
    expect(toggleColumn(["C1"], "C1")).toBeNull();
  });
});

describe("a view's body and columns", () => {
  it("adds and removes dialect bodies, keeping one", () => {
    const doc: Json = { kind: "view", body: { postgresql: "select 1" } };
    setViewBody(doc, "*", "select 2");
    expect(viewDialects(doc)).toEqual(["postgresql", "*"]);
    expect(removeViewDialect(doc, "postgresql")).toBe(true);
    expect(doc.body).toEqual({ "*": "select 2" });
    expect(removeViewDialect(doc, "*")).toBe(false);
  });

  it("adds and removes declared columns", () => {
    const doc: Json = { kind: "view" };
    addViewColumn(doc);
    addViewColumn(doc);
    expect(doc.columns).toEqual([{ name: "column_1" }, { name: "column_2" }]);
    removeViewColumn(doc, 0);
    removeViewColumn(doc, 0);
    expect(doc.columns).toBeUndefined();
  });
});

describe("a sequence's definition", () => {
  it("takes whole numbers, a non-zero increment and a cache of 1 or more; empty or the default 1 removes start and increment", () => {
    expect(sequenceNumberProblem("start", "x")).toBe("A whole number, or empty.");
    expect(sequenceNumberProblem("increment", "0")).toBe("The increment cannot be 0.");
    expect(sequenceNumberProblem("cache", "0")).toBe("The cache is 1 or more.");
    expect(sequenceNumberProblem("min", "-5")).toBeNull();
    const doc: Json = { kind: "sequence", start: 1000 };
    expect(setSequenceNumber(doc, "start", "1")).toBe(true);
    expect(doc.start).toBeUndefined();
    expect(setSequenceNumber(doc, "max", " 99 ")).toBe(true);
    expect(doc.max).toBe(99);
    expect(setSequenceNumber(doc, "max", "")).toBe(true);
    expect(doc.max).toBeUndefined();
    expect(setSequenceNumber(doc, "increment", "0")).toBe(false);
  });
});
