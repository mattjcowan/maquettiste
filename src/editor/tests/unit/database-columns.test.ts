// The Database screen's column editor: which file holds a table's columns, and one column's edit in it (columnEdits.ts); and
// the comments convention's control (CommentsConvention.tsx).
import { describe, expect, it } from "vitest";
import type { ColumnView, ElementSummary, SettingsJson } from "@/api/types";
import {
  columnDerivations,
  columnEntryOf,
  columnFieldProblem,
  columnText,
  emptyOverlay,
  isOverlayFor,
  logicalTypeText,
  newOverlay,
  overlayCandidates,
  overlayChangesSomething,
  parseColumnDefault,
  physicalHint,
  setColumnField,
  tableFileTarget,
  tableKeyOfDoc,
} from "@/workspaces/database/columnEdits";
import { tableKeyOfFile } from "@/workspaces/database/tableList";
import { columnAt } from "@/app/navigation";
import { mergeDatabaseView } from "@/api/queries";
import { MockBackend } from "@/mocks/backend";
import { foreignKeyMismatches, resolveDatabase } from "@/mocks/model/physical";
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

describe("a column entry's marks: tags, stereotypes and the property bag", () => {
  it("sets tags and stereotypes on the overlay entry, and an empty list removes them (and the entry with them)", () => {
    const doc: Record<string, unknown> = { kind: "table", id: "A", database: DB, origin: "synthesized", entity: ENTITY };
    expect(setColumnField(doc, column({}), "tags", ["pii"], ids)).toBe(true);
    expect(setColumnField(doc, column({}), "stereotypes", ["audited"], ids)).toBe(true);
    expect(doc.columns).toEqual([{ id: expect.any(String), attribute: ATTR, tags: ["pii"], stereotypes: ["audited"] }]);
    setColumnField(doc, column({}), "tags", [], ids);
    setColumnField(doc, column({}), "stereotypes", [], ids);
    expect(doc).not.toHaveProperty("columns");
  });

  it("applies a property edit to the entry's own properties: add, rename keeping the value, type change, remove", () => {
    const doc: Record<string, unknown> = { kind: "table", id: "A", database: DB, origin: "synthesized", entity: ENTITY };
    setColumnField(doc, column({}), "properties", { op: "set", key: "owner", value: "finance" }, ids);
    setColumnField(doc, column({}), "properties", { op: "set", key: "priority", value: "3" }, ids);
    expect(columnEntryOf(doc, column({}))).toEqual({ id: expect.any(String), attribute: ATTR, properties: { owner: "finance", priority: "3" } });
    setColumnField(doc, column({}), "properties", { op: "set", key: "team", value: "finance", from: "owner" }, ids);
    setColumnField(doc, column({}), "properties", { op: "set", key: "priority", value: 3 }, ids);
    expect(columnEntryOf(doc, column({}))?.properties).toEqual({ team: "finance", priority: 3 });
    setColumnField(doc, column({}), "properties", { op: "remove", key: "team" }, ids);
    setColumnField(doc, column({}), "properties", { op: "remove", key: "priority" }, ids);
    expect(doc).not.toHaveProperty("columns");
  });

  it("edits a designed column's marks in its own entry, keeping its other properties", () => {
    const doc: Record<string, unknown> = {
      kind: "table",
      id: "T",
      name: "ledger",
      database: DB,
      origin: "designed",
      columns: [{ id: "C1", name: "id", type: "int64", properties: { classification: "internal" } }],
    };
    expect(setColumnField(doc, column({ key: "C1", name: "id" }), "properties", { op: "set", key: "owner", value: "finance" }, ids)).toBe(true);
    expect(setColumnField(doc, column({ key: "C1", name: "id" }), "tags", ["billing"], ids)).toBe(true);
    expect((doc.columns as object[])[0]).toEqual({
      id: "C1",
      name: "id",
      type: "int64",
      properties: { classification: "internal", owner: "finance" },
      tags: ["billing"],
    });
    expect(columnEntryOf(doc, column({ key: "missing" }))).toBeUndefined();
  });

  it("creates an overlay for a projected table's first mark, and an empty overlay changes nothing", () => {
    expect(newOverlay({ kind: "overlay", entity: ENTITY }, DB, column({}), "properties", { op: "set", key: "owner", value: "" }, ids)?.columns).toEqual([
      { id: expect.any(String), attribute: ATTR, properties: { owner: "" } },
    ]);
    const empty = emptyOverlay({ kind: "overlay", entity: ENTITY, attribute: "VO" }, DB, "N");
    expect(empty).toEqual({ kind: "table", id: "N", database: DB, origin: "synthesized", entity: ENTITY, attribute: "VO" });
    expect(overlayChangesSomething(empty)).toBe(false);
    expect(overlayChangesSomething({ ...empty, comment: "c" })).toBe(true);
    expect(overlayChangesSomething({ ...empty, properties: { owner: "finance" } })).toBe(true);
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

describe("the physical side is free (the attribute's length is validation, the column's is storage)", () => {
  it("an overlay entry takes type, length, precision, scale and native type, whatever the attribute says; empty clears", () => {
    const doc: Record<string, unknown> = { kind: "table", id: "A", database: DB, origin: "synthesized", entity: ENTITY };
    const col = column({ length: 255, precision: null, scale: null, nativeType: "varchar(255)" });
    expect(setColumnField(doc, col, "type", "text", ids)).toBe(true);
    expect(setColumnField(doc, col, "length", " 2056 ", ids)).toBe(true);
    expect(setColumnField(doc, col, "nativeType", " citext ", ids)).toBe(true);
    expect(setColumnField(doc, column({ type: "decimal" }), "precision", "19", ids)).toBe(true);
    expect(setColumnField(doc, column({ type: "decimal" }), "scale", "0", ids)).toBe(true);
    expect(doc.columns).toEqual([{ id: expect.any(String), attribute: ATTR, type: "text", length: 2056, nativeType: "citext", precision: 19, scale: 0 }]);
    setColumnField(doc, col, "nativeType", "", ids);
    setColumnField(doc, col, "length", "", ids);
    expect((doc.columns as object[])[0]).toEqual({ id: expect.any(String), attribute: ATTR, type: "text", precision: 19, scale: 0 });
  });

  it("refuses a facet that is not a whole number, and says why", () => {
    const doc: Record<string, unknown> = { kind: "table", id: "A", database: DB, origin: "synthesized", entity: ENTITY };
    expect(setColumnField(doc, column({}), "length", "12.5", ids)).toBe(false);
    expect(setColumnField(doc, column({}), "precision", "0", ids)).toBe(false);
    expect(doc.columns).toBeUndefined();
    expect(columnFieldProblem("length", "abc")).toBe("Length is a whole number from 1, or empty.");
    expect(columnFieldProblem("scale", "0")).toBeNull();
    expect(columnFieldProblem("length", "")).toBeNull();
    expect(columnFieldProblem("name", "x y")).toBeNull();
  });

  it("edits a designed column's facets in its own entry", () => {
    const doc: Record<string, unknown> = {
      kind: "table",
      id: "T",
      database: DB,
      origin: "designed",
      columns: [{ id: "C1", name: "code", type: "string", length: 2056 }],
    };
    expect(setColumnField(doc, column({ key: "C1", name: "code", length: 2056 }), "length", "128", ids)).toBe(true);
    expect(setColumnField(doc, column({ key: "C1", name: "code" }), "nativeType", "char(128)", ids)).toBe(true);
    expect(doc.columns).toEqual([{ id: "C1", name: "code", type: "string", length: 128, nativeType: "char(128)" }]);
  });

  it("shows facets as text and says what a physical cell means", () => {
    expect(columnText(column({ length: 255 }), "length")).toBe("255");
    expect(columnText(column({ precision: null }), "precision")).toBe("");
    expect(columnText(column({ nativeType: "varchar(255)" }), "nativeType")).toBe("varchar(255)");
    expect(logicalTypeText("string", { length: 255 })).toBe("string(255)");
    expect(logicalTypeText("decimal", { precision: 18, scale: 2 })).toBe("decimal(18,2)");
    const derived = { entityId: ENTITY, label: "Customer.name", logical: "string(120)" };
    expect(physicalHint(column({ isForeignKey: false }), derived)).toBe(
      "Physical type of the column; the attribute keeps its own (string(120)) for validation.",
    );
    expect(physicalHint(column({ isForeignKey: true }), null)).toBe("Follows the referenced column; set it here and a different type is reported (MQ4005).");
  });

  it("names the attribute each column derives from, with its logical type", () => {
    const table = {
      entityId: ENTITY,
      columns: [column({ key: ATTR, attributeId: ATTR }), column({ key: "fk", attributeId: null }), column({ key: "x", attributeId: "INHERITED" })],
    };
    const entity = { name: "Customer", attributes: [{ id: ATTR, name: "name", type: "string", length: 120 }] };
    const d = columnDerivations(table, entity, () => undefined);
    expect(d.get(ATTR)).toEqual({ entityId: ENTITY, label: "Customer.name", logical: "string(120)" });
    expect(d.has("fk")).toBe(false);
    expect(d.get("x")).toEqual({ entityId: ENTITY, label: "Customer (attribute from a parent or a stereotype)", logical: null });
    const fromMore = columnDerivations(
      { entityId: ENTITY, columns: [column({ key: "b", attributeId: "BASE_ATTR" }), column({ key: "s", attributeId: "ST_ATTR" })] },
      { name: "Customer", base: "PARTY", attributes: [] },
      () => undefined,
      {
        bases: [{ id: "PARTY", name: "Party", stereotypes: ["audited"], attributes: [{ id: "BASE_ATTR", name: "code", type: "string", length: 20 }] }],
        stereotypes: [
          { key: "audited", attributes: [{ id: "ST_ATTR", name: "createdAt", type: "datetimeoffset" }] },
          { key: "unused", attributes: [{ id: "OTHER", name: "x", type: "int32" }] },
        ],
      },
    );
    expect(fromMore.get("b")).toEqual({ entityId: "PARTY", label: "Customer.code (from Party)", logical: "string(20)" });
    expect(fromMore.get("s")).toEqual({ entityId: ENTITY, label: "Customer.createdAt «audited»", logical: "datetimeoffset" });
    const refTyped = { name: "Customer", attributes: [{ id: ATTR, name: "email", type: { ref: "T1" } }] };
    expect(columnDerivations(table, refTyped, (r) => (r === "T1" ? "Email" : undefined)).get(ATTR)?.logical).toBe("Email");
  });
});

describe("a table file's table", () => {
  it("finds the resolved key from the document, the summaries, or a pointer's column", () => {
    expect(tableKeyOfDoc({ kind: "table", id: "T", database: DB, origin: "designed" })).toEqual({ database: DB, key: "T" });
    expect(tableKeyOfDoc({ kind: "table", id: "O", database: DB, origin: "synthesized", entity: ENTITY })).toEqual({ database: DB, key: `${ENTITY}@${DB}` });
    expect(tableKeyOfDoc({ kind: "table", id: "O", database: DB, origin: "synthesized", entity: ENTITY, attribute: ATTR })?.key).toBe(
      `${ENTITY}.${ATTR}@${DB}`,
    );
    expect(tableKeyOfDoc({ kind: "table", id: "J", database: DB, origin: "synthesized", relation: REL })?.key).toBe(`${REL}@${DB}`);
    expect(tableKeyOfDoc({ kind: "entity", id: "E" })).toBeNull();
    const summaries = [
      { key: "T", entityId: null, isJunction: false, origin: "designed" as const },
      { key: `${ENTITY}@${DB}`, entityId: ENTITY, isJunction: false, origin: "synthesized" as const },
    ];
    expect(tableKeyOfFile({ id: "T", database: DB }, summaries)).toBe("T");
    expect(tableKeyOfFile({ id: "O", database: DB, entity: ENTITY }, summaries)).toBe(`${ENTITY}@${DB}`);
    expect(tableKeyOfFile({ id: "O", database: DB, entity: "OTHER" }, summaries)).toBeNull();
    expect(tableKeyOfFile({ id: "O", database: DB, entity: ENTITY }, undefined)).toBeNull();
    const doc = { json: { columns: [{ id: "E1", attribute: ATTR }, { id: "C2" }] } };
    expect(columnAt(doc, "/columns/0/length")).toBe(ATTR);
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

describe("the mock resolver applies an overlay's physical fields as the engine does", () => {
  const CUSTOMER_ID = "01J92P0V0KGPC29TQQG8R57EBM";
  const resolve = (overlay: Record<string, unknown> | null) => {
    const backend = new MockBackend();
    const docs = new Map(backend.model.docs());
    if (overlay) docs.set(String(overlay.id), overlay);
    const view = resolveDatabase({ docs, conventions: {}, databaseConventions: {} }, DB)!;
    return { view, docs, customers: view.tables.find((t) => t.entityId === ENTITY)! };
  };
  const overlayOf = (columns: Record<string, unknown>[]) => ({ kind: "table", id: "OVL", database: DB, origin: "synthesized", entity: ENTITY, columns });

  it("a text type or a longer length under a string(120) attribute resolves, and the attribute keeps its own length", () => {
    const { view, docs, customers } = resolve(overlayOf([{ id: "E1", attribute: ATTR, type: "text" }]));
    expect(customers.columns.find((c) => c.key === ATTR)).toMatchObject({ type: "text" });
    expect(foreignKeyMismatches(view, docs)).toEqual([]);
    const longer = resolve(overlayOf([{ id: "E1", attribute: ATTR, length: 2056 }]));
    expect(longer.customers.columns.find((c) => c.key === ATTR)).toMatchObject({ type: "string", length: 2056, nativeType: "varchar(2056)" });
    expect(foreignKeyMismatches(longer.view, longer.docs)).toEqual([]);
    const attr = (longer.docs.get(ENTITY)!.attributes as { id: string; length?: number }[]).find((a) => a.id === ATTR);
    expect(attr?.length).toBe(120);
  });

  it("a foreign key follows the referenced key, and one pinned to another type is MQ4005 on the overlay entry", () => {
    const plain = resolve(null);
    const pk = plain.customers.columns.find((c) => c.key === CUSTOMER_ID)!;
    const holder = plain.view.tables.find((t) => t.foreignKeys.some((fk) => fk.referencedTable === plain.customers.key && t.entityId))!;
    const fk = holder.foreignKeys.find((f) => f.referencedTable === plain.customers.key)!;
    const fkColumn = holder.columns.find((c) => c.key === fk.columns[0])!;
    expect(fkColumn).toMatchObject({ type: pk.type, nativeType: pk.nativeType });
    expect(foreignKeyMismatches(plain.view, plain.docs)).toEqual([]);

    const backend = new MockBackend();
    const docs = new Map(backend.model.docs());
    // The holder's own overlay (the fixture's invoices table has one) takes the entry, after its existing ones.
    const existing = [...docs.values()].find((d) => d.kind === "table" && d.entity === holder.entityId && d.origin === "synthesized");
    const overlay = existing
      ? { ...existing, columns: [...((existing.columns as object[]) ?? []), { id: "E1", attribute: fkColumn.key, type: "string", length: 40 }] }
      : {
          kind: "table",
          id: "OVL",
          database: DB,
          origin: "synthesized",
          entity: holder.entityId,
          columns: [{ id: "E1", attribute: fkColumn.key, type: "string", length: 40 }],
        };
    docs.set(String(overlay.id), overlay);
    const pinned = resolveDatabase({ docs, conventions: {}, databaseConventions: {} }, DB)!;
    const [problem] = foreignKeyMismatches(pinned, docs);
    expect(problem).toMatchObject({
      rule: "MQ4005",
      severity: "error",
      elementId: String(overlay.id),
      jsonPointer: `/columns/${(overlay.columns as object[]).length - 1}`,
    });
    expect(problem.message).toBe(
      `Foreign key column '${holder.name}.${fkColumn.name}' is string(40) (varchar(40)) but references '${plain.customers.name}.${pk.name}', which is ${pk.type} (${pk.nativeType}) (database '${plain.view.name}').`,
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
