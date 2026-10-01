// The Database screen's column editor: which file holds a table's columns, and one column's edit in it (columnEdits.ts); and
// the comments convention's control (CommentsConvention.tsx).
import { describe, expect, it } from "vitest";
import type { ColumnView, ElementSummary, SettingsJson } from "@/api/types";
import { isOverlayFor, newOverlay, overlayCandidates, parseColumnDefault, setColumnField, tableFileTarget } from "@/workspaces/database/columnEdits";
import { commentsAt } from "@/workspaces/settings/CommentsConvention";

const DB = "01J92P0V1QRN2181XM2ZWE02W4";
const ENTITY = "01J92P0V0ETQKXXP951CMMNHH3";
const ATTR = "01J92P0V0MS09YFZHX07JQ3KMN";
const REL = "01J92P0V0ZZZZZZZZZZZZZZZZZ";

let n = 0;
const ids = () => `ID${++n}`;
const column = (over: Partial<ColumnView>): ColumnView => ({ key: ATTR, name: "name", type: "string", nullable: false, default: null, ...over }) as ColumnView;
const row = (over: Partial<ElementSummary>): ElementSummary =>
  ({
    id: "X",
    kind: "table",
    name: "",
    package: null,
    tags: [],
    category: null,
    stereotypes: [],
    hash: "h",
    path: "p",
    database: DB,
    ...over,
  }) as ElementSummary;

describe("tableFileTarget", () => {
  it("reads the file from the key: a file id, an entity, a child table, or a junction", () => {
    expect(tableFileTarget({ key: "01J9TABLE", entityId: null, relationId: null }, DB)).toEqual({ kind: "file", id: "01J9TABLE" });
    expect(tableFileTarget({ key: `${ENTITY}@${DB}`, entityId: ENTITY, relationId: null }, DB)).toEqual({ kind: "overlay", entity: ENTITY });
    expect(tableFileTarget({ key: `${ENTITY}.${ATTR}@${DB}`, entityId: ENTITY, relationId: null }, DB)).toEqual({
      kind: "overlay",
      entity: ENTITY,
      attribute: ATTR,
    });
    expect(tableFileTarget({ key: `${REL}@${DB}`, entityId: null, relationId: REL }, DB)).toEqual({ kind: "overlay", relation: REL });
  });
});

describe("overlay lookup", () => {
  it("lists the database's table rows on the entity, or for a junction the unnamed ones and the one named like the table", () => {
    const rows = [
      row({ id: "A", entity: ENTITY }),
      row({ id: "B", entity: ENTITY, database: "OTHER" }),
      row({ id: "C", name: "ledger" }),
      row({ id: "D" }),
      row({ id: "E", name: "customer_product" }),
      row({ id: "F", kind: "mapping", entity: ENTITY }),
    ];
    expect(overlayCandidates(rows, { name: "customers" }, { kind: "overlay", entity: ENTITY }, DB)).toEqual(["A"]);
    expect(overlayCandidates(rows, { name: "customer_product" }, { kind: "overlay", relation: REL }, DB)).toEqual(["D", "E"]);
    expect(overlayCandidates(rows, { name: "x" }, { kind: "file", id: "C" }, DB)).toEqual([]);
  });

  it("matches an overlay by what it overrides", () => {
    const entityTable = { kind: "table", id: "A", database: DB, origin: "synthesized", entity: ENTITY };
    expect(isOverlayFor(entityTable, { kind: "overlay", entity: ENTITY }, DB)).toBe(true);
    expect(isOverlayFor(entityTable, { kind: "overlay", entity: ENTITY, attribute: ATTR }, DB)).toBe(false);
    expect(isOverlayFor({ ...entityTable, attribute: ATTR }, { kind: "overlay", entity: ENTITY, attribute: ATTR }, DB)).toBe(true);
    expect(isOverlayFor({ ...entityTable, origin: "designed" }, { kind: "overlay", entity: ENTITY }, DB)).toBe(false);
    expect(isOverlayFor({ kind: "table", id: "J", database: DB, origin: "synthesized", relation: REL }, { kind: "overlay", relation: REL }, DB)).toBe(true);
  });
});

describe("setColumnField", () => {
  it("adds an overlay entry for the synthesized column, and removes it when it changes nothing any more", () => {
    const doc: Record<string, unknown> = {
      kind: "table",
      id: "A",
      database: DB,
      origin: "synthesized",
      entity: ENTITY,
      columns: [{ id: "K", attribute: "other", nativeType: "citext" }],
    };
    expect(setColumnField(doc, column({}), "description", "The legal name.", ids)).toBe(true);
    expect(doc.columns).toEqual([
      { id: "K", attribute: "other", nativeType: "citext" },
      { id: expect.any(String), attribute: ATTR, description: "The legal name." },
    ]);
    expect(setColumnField(doc, column({}), "comment", "Printed on invoices.", ids)).toBe(true);
    expect((doc.columns as object[])[1]).toMatchObject({ description: "The legal name.", comment: "Printed on invoices." });
    setColumnField(doc, column({}), "description", "  ", ids);
    setColumnField(doc, column({}), "comment", "", ids);
    expect(doc.columns).toEqual([{ id: "K", attribute: "other", nativeType: "citext" }]);
  });

  it("types a default like its column, sets nullability explicitly, and an empty name returns to the conventional one", () => {
    const doc: Record<string, unknown> = { kind: "table", id: "A", database: DB, origin: "synthesized", entity: ENTITY };
    setColumnField(doc, column({ type: "int32" }), "default", "42", ids);
    setColumnField(doc, column({ type: "int32" }), "nullable", true, ids);
    setColumnField(doc, column({ type: "int32" }), "name", " full_name ", ids);
    expect(doc.columns).toEqual([{ id: expect.any(String), attribute: ATTR, default: 42, nullable: true, name: "full_name" }]);
    setColumnField(doc, column({}), "name", "", ids);
    expect((doc.columns as object[])[0]).not.toHaveProperty("name");
    expect(parseColumnDefault("bool", "true")).toBe(true);
    expect(parseColumnDefault("string", "42")).toBe("42");
  });

  it("edits a designed column by id, keeps its required members, and leaves a sidecar description alone", () => {
    const doc: Record<string, unknown> = {
      kind: "table",
      id: "T",
      name: "ledger",
      database: DB,
      origin: "designed",
      columns: [
        { id: "C1", name: "id", type: "int64" },
        { id: "C2", name: "memo", type: "text", description: { file: "memo.md" } },
      ],
    };
    expect(setColumnField(doc, column({ key: "C1", name: "id" }), "comment", "The posting number.", ids)).toBe(true);
    expect((doc.columns as object[])[0]).toEqual({ id: "C1", name: "id", type: "int64", comment: "The posting number." });
    expect(setColumnField(doc, column({ key: "C1", name: "id" }), "name", "", ids)).toBe(false);
    expect(setColumnField(doc, column({ key: "C2", name: "memo" }), "description", "Inline now.", ids)).toBe(false);
    expect(setColumnField(doc, column({ key: "missing" }), "comment", "x", ids)).toBe(false);
  });
});

describe("newOverlay", () => {
  it("holds just the edited column, for an entity, a child table or a junction", () => {
    expect(newOverlay({ kind: "overlay", entity: ENTITY }, DB, column({}), "description", "The legal name.", ids)).toEqual({
      kind: "table",
      id: expect.any(String),
      database: DB,
      origin: "synthesized",
      entity: ENTITY,
      columns: [{ id: expect.any(String), attribute: ATTR, description: "The legal name." }],
    });
    expect(newOverlay({ kind: "overlay", entity: ENTITY, attribute: "VO" }, DB, column({}), "comment", "c", ids)).toMatchObject({
      entity: ENTITY,
      attribute: "VO",
    });
    expect(newOverlay({ kind: "overlay", relation: REL }, DB, column({}), "comment", "c", ids)).toMatchObject({ relation: REL });
    expect(newOverlay({ kind: "overlay", relation: REL }, DB, column({}), "comment", "", ids)).toBeNull();
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
