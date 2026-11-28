// The Database screen's column editor: one column's edit in its table file (columnEdits.ts), the resolved key of a table file,
// and the comments convention's control (CommentsConvention.tsx).
import { describe, expect, it } from "vitest";
import type { ColumnView, SettingsJson } from "@/api/types";
import {
  columnEntryOf,
  columnFieldProblem,
  columnText,
  logicalTypeText,
  parseColumnDefault,
  physicalHint,
  setColumnField,
  tableKeyOfDoc,
} from "@/workspaces/database/columnEdits";
import { columnAt } from "@/app/navigation";
import { mergeDatabaseView } from "@/api/queries";
import { MockBackend } from "@/mocks/backend";
import { foreignKeyMismatches, resolveDatabase } from "@/mocks/model/physical";
import { commentsAt } from "@/workspaces/settings/CommentsConvention";

const DB = "01J92P0V1QRN2181XM2ZWE02W4";
const ATTR = "01J92P0V0MS09YFZHX07JQ3KMN";

const column = (over: Partial<ColumnView>): ColumnView => ({ key: ATTR, name: "name", type: "string", nullable: false, default: null, ...over }) as ColumnView;
const table = (columns: Record<string, unknown>[]): Record<string, unknown> => ({ kind: "table", id: "T", name: "ledger", database: DB, columns });

describe("setColumnField", () => {
  it("edits a column by id, keeps its required members, and leaves a sidecar description alone", () => {
    const doc = table([
      { id: "C1", name: "id", type: "int64" },
      { id: "C2", name: "memo", type: "text", description: { file: "memo.md" } },
    ]);
    expect(setColumnField(doc, column({ key: "C1", name: "id" }), "comment", "The posting number.")).toBe(true);
    expect((doc.columns as object[])[0]).toEqual({ id: "C1", name: "id", type: "int64", comment: "The posting number." });
    expect(setColumnField(doc, column({ key: "C1", name: "id" }), "name", "")).toBe(false);
    expect(setColumnField(doc, column({ key: "C2", name: "memo" }), "description", "Inline now.")).toBe(false);
    expect(setColumnField(doc, column({ key: "missing" }), "comment", "x")).toBe(false);
  });

  it("types a default like its column and sets nullability explicitly", () => {
    const doc = table([{ id: "C1", name: "n", type: "int32" }]);
    const c = column({ key: "C1", name: "n", type: "int32" });
    setColumnField(doc, c, "default", "42");
    setColumnField(doc, c, "nullable", true);
    setColumnField(doc, c, "name", " full_name ");
    expect(doc.columns).toEqual([{ id: "C1", name: "full_name", type: "int32", default: 42, nullable: true }]);
    expect(parseColumnDefault("bool", "true")).toBe(true);
    expect(parseColumnDefault("string", "42")).toBe("42");
  });

  it("sets tags, stereotypes and the property bag in the column's own entry, and an empty list removes them", () => {
    const doc = table([{ id: "C1", name: "id", type: "int64", properties: { classification: "internal" } }]);
    const c = column({ key: "C1", name: "id" });
    expect(setColumnField(doc, c, "properties", { op: "set", key: "owner", value: "finance" })).toBe(true);
    expect(setColumnField(doc, c, "tags", ["billing"])).toBe(true);
    expect(setColumnField(doc, c, "stereotypes", ["audited"])).toBe(true);
    expect((doc.columns as object[])[0]).toEqual({
      id: "C1",
      name: "id",
      type: "int64",
      properties: { classification: "internal", owner: "finance" },
      tags: ["billing"],
      stereotypes: ["audited"],
    });
    setColumnField(doc, c, "tags", []);
    setColumnField(doc, c, "stereotypes", []);
    setColumnField(doc, c, "properties", { op: "set", key: "team", value: "finance", from: "owner" });
    expect(columnEntryOf(doc, c)?.properties).toEqual({ classification: "internal", team: "finance" });
    setColumnField(doc, c, "properties", { op: "remove", key: "team" });
    setColumnField(doc, c, "properties", { op: "remove", key: "classification" });
    expect(columnEntryOf(doc, c)).toEqual({ id: "C1", name: "id", type: "int64" });
    expect(columnEntryOf(doc, column({ key: "missing" }))).toBeUndefined();
  });
});

describe("the physical side is free (the attribute's length is validation, the column's is storage)", () => {
  it("a column takes type, length, precision, scale and native type, whatever an attribute bound to it says; empty clears", () => {
    const doc = table([{ id: "C1", name: "code", type: "string", length: 255 }]);
    const col = column({ key: "C1", name: "code", length: 255, precision: null, scale: null, nativeType: "varchar(255)" });
    expect(setColumnField(doc, col, "length", " 2056 ")).toBe(true);
    expect(setColumnField(doc, col, "nativeType", " citext ")).toBe(true);
    expect(setColumnField(doc, column({ key: "C1", name: "code", type: "decimal" }), "precision", "19")).toBe(true);
    expect(setColumnField(doc, column({ key: "C1", name: "code", type: "decimal" }), "scale", "0")).toBe(true);
    expect(doc.columns).toEqual([{ id: "C1", name: "code", type: "string", length: 2056, nativeType: "citext", precision: 19, scale: 0 }]);
    setColumnField(doc, col, "nativeType", "");
    setColumnField(doc, col, "length", "");
    expect((doc.columns as object[])[0]).toEqual({ id: "C1", name: "code", type: "string", precision: 19, scale: 0 });
  });

  it("refuses a facet that is not a whole number, and says why", () => {
    const doc = table([{ id: "C1", name: "code", type: "string" }]);
    expect(setColumnField(doc, column({ key: "C1" }), "length", "12.5")).toBe(false);
    expect(setColumnField(doc, column({ key: "C1" }), "precision", "0")).toBe(false);
    expect(doc.columns).toEqual([{ id: "C1", name: "code", type: "string" }]);
    expect(columnFieldProblem("length", "abc")).toBe("Length is a whole number from 1, or empty.");
    expect(columnFieldProblem("scale", "0")).toBeNull();
    expect(columnFieldProblem("length", "")).toBeNull();
    expect(columnFieldProblem("name", "x y")).toBeNull();
  });

  it("shows facets as text and says what a physical cell means", () => {
    expect(columnText(column({ length: 255 }), "length")).toBe("255");
    expect(columnText(column({ precision: null }), "precision")).toBe("");
    expect(columnText(column({ nativeType: "varchar(255)" }), "nativeType")).toBe("varchar(255)");
    expect(logicalTypeText("string", { length: 255 })).toBe("string(255)");
    expect(logicalTypeText("decimal", { precision: 18, scale: 2 })).toBe("decimal(18,2)");
    expect(physicalHint(column({ isForeignKey: false }))).toBe("Physical type of the column.");
    expect(physicalHint(column({ isForeignKey: true }))).toBe("Follows the referenced column; set it here and a different type is reported (MQ4005).");
  });
});

describe("a table file's table", () => {
  it("finds the resolved key from the document, or a pointer's column", () => {
    expect(tableKeyOfDoc({ kind: "table", id: "T", database: DB })).toEqual({ database: DB, key: "T" });
    expect(tableKeyOfDoc({ kind: "entity", id: "E" })).toBeNull();
    const doc = { json: { columns: [{ id: "E1" }, { id: "C2" }] } };
    expect(columnAt(doc, "/columns/0/length")).toBe("E1");
    expect(columnAt(doc, "/columns/1")).toBe("C2");
    expect(columnAt(doc, "/name")).toBeNull();
  });
});

describe("the database view while the model has errors", () => {
  it("keeps the last resolved tables, marked stale, with the new diagnostics", () => {
    const view = { id: DB, tables: [] } as never;
    const error = { rule: "MQ4005", severity: "error", message: "m", elementId: null, filePath: null, jsonPointer: null, line: null, column: null } as const;
    const first = mergeDatabaseView(undefined, { view, diagnostics: [] });
    expect(first).toEqual({ view, diagnostics: [], stale: false });
    expect(mergeDatabaseView(first, { view: null, diagnostics: [error] })).toEqual({ view, diagnostics: [error], stale: true });
    expect(mergeDatabaseView(undefined, { view: null, diagnostics: [error] })).toEqual({ view: null, diagnostics: [error], stale: false });
  });
});

describe("the mock resolver", () => {
  const resolve = (edit?: (docs: Map<string, Record<string, unknown>>) => void) => {
    const backend = new MockBackend();
    const docs = new Map(backend.model.docs());
    edit?.(docs);
    return { view: resolveDatabase({ docs, conventions: {}, databaseConventions: {} }, DB)!, docs };
  };

  it("lists every table file as a table, with its columns by id", () => {
    const { view, docs } = resolve();
    const files = [...docs.values()].filter((d) => d.kind === "table" && d.database === DB);
    expect(view.tables.map((t) => t.key).sort()).toEqual(files.map((f) => String(f.id)).sort());
    const invoices = view.tables.find((t) => t.name === "invoices")!;
    expect(invoices.columns.map((c) => c.key)).toEqual(((docs.get(invoices.key)!.columns as { id: string }[]) ?? []).map((c) => c.id));
  });

  it("a foreign key column pinned to another type is MQ4005 on the table file", () => {
    const plain = resolve();
    expect(foreignKeyMismatches(plain.view, plain.docs)).toEqual([]);
    const holder = plain.view.tables.find((t) => t.foreignKeys.length > 0)!;
    const fk = holder.foreignKeys[0];
    const fkColumn = holder.columns.find((c) => c.key === fk.columns[0])!;
    const target = plain.view.tables.find((t) => t.key === fk.referencedTable)!;
    const pk = target.columns.find((c) => c.key === fk.referencedColumns[0])!;
    const pinned = resolve((docs) => {
      const file = structuredClone(docs.get(holder.key)!);
      const entries = file.columns as Record<string, unknown>[];
      const at = entries.findIndex((c) => c.id === fkColumn.key);
      entries[at] = { ...entries[at], type: "string", length: 40 };
      delete entries[at].nativeType;
      docs.set(holder.key, file);
    });
    const [problem] = foreignKeyMismatches(pinned.view, pinned.docs);
    const at = (pinned.docs.get(holder.key)!.columns as { id: string }[]).findIndex((c) => c.id === fkColumn.key);
    expect(problem).toMatchObject({ rule: "MQ4005", severity: "error", elementId: holder.key, jsonPointer: `/columns/${at}` });
    expect(problem.message).toContain(
      `Foreign key column '${holder.name}.${fkColumn.name}' is string(40) (varchar(40)) but references '${target.name}.${pk.name}'`,
    );
  });
});

describe("commentsAt", () => {
  const json = (v: unknown) => v as SettingsJson;
  it("reads the scope's value and what an unset value inherits", () => {
    expect(commentsAt(json({}), "")).toEqual({ value: undefined, inherited: "descriptions (engine default)" });
    expect(commentsAt(json({ conventions: { comments: "none" } }), "")).toEqual({ value: "none", inherited: "descriptions (engine default)" });
    expect(commentsAt(json({ conventions: { comments: "none" } }), "main")).toEqual({ value: undefined, inherited: "none (project)" });
    expect(commentsAt(json({ conventions: { comments: "none" }, databases: { main: { comments: "descriptions" } } }), "main")).toEqual({
      value: "descriptions",
      inherited: "none (project)",
    });
  });
});
