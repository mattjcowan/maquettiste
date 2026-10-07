// The DDL facets the table editor and the column inspector edit (unicode, fixedLength, defaultName on a column; deferrable on a
// foreign key and the columns its on-delete sets; a check's column), and the mock's MQ4057, MQ4060 and MQ4056 that show inline
// beside them.
import { describe, expect, it } from "vitest";
import type { ColumnView } from "@/api/types";
import { setColumnField, unicodeChoice } from "@/workspaces/database/columnEdits";
import { deleteTableColumn } from "@/editors/database/databaseDocs";
import { columnProblems } from "@/editors/database/ColumnFacets";
import { deferrableNote } from "@/editors/database/TablePartsTabs";
import { MockBackend } from "@/mocks/backend";
import { keepOnDeleteColumns, onDeleteColumnsNote, setForeignKeyAction, setsColumns, toggleOnDeleteColumn } from "@/workspaces/database/tableParts";

type Json = Record<string, unknown>;
const NOTES = "01K6BND0000000000000000001";
const ENTITY_TYPE = "01K6BND0000000000000000003";
const BODY = "01K6BND0000000000000000005";
const ID = "01K6BND0000000000000000002";
const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";

const col = (key: string, type: string, name = "c") => ({ key, name, type }) as ColumnView;
const newId = () => "N1";

describe("a column's DDL facets", () => {
  it("sets unicode three ways, fixedLength as a flag and defaultName as text; a type without the facet drops it", () => {
    const doc: Json = { kind: "table", columns: [{ id: "c1", name: "code", type: "string", length: 3 }] };
    const c = col("c1", "string", "code");
    const entry = () => (doc.columns as Json[])[0];
    expect(setColumnField(doc, c, "unicode", "false", newId)).toBe(true);
    expect(entry().unicode).toBe(false);
    expect(unicodeChoice(entry())).toBe("false");
    setColumnField(doc, c, "unicode", "", newId);
    expect("unicode" in entry()).toBe(false);
    expect(unicodeChoice(entry())).toBe("");
    setColumnField(doc, c, "unicode", "true", newId);
    setColumnField(doc, c, "fixedLength", true, newId);
    setColumnField(doc, c, "defaultName", "  df_code ", newId);
    expect(entry()).toMatchObject({ unicode: true, fixedLength: true, defaultName: "df_code" });
    setColumnField(doc, c, "fixedLength", false, newId);
    setColumnField(doc, c, "defaultName", "", newId);
    expect("fixedLength" in entry() || "defaultName" in entry()).toBe(false);
    // To binary: unicode goes (MQ4057 otherwise), a fixed length stays; to int32 both go.
    setColumnField(doc, c, "fixedLength", true, newId);
    setColumnField(doc, c, "type", "binary", newId);
    expect(entry()).toMatchObject({ type: "binary", fixedLength: true });
    expect("unicode" in entry()).toBe(false);
    setColumnField(doc, col("c1", "binary", "code"), "type", "int32", newId);
    expect("fixedLength" in entry()).toBe(false);
  });

  it("drops a column check with its column, and finds a column's problems by its place", () => {
    const doc: Json = {
      columns: [
        { id: "a", name: "a", type: "int32" },
        { id: "b", name: "b", type: "int32" },
      ],
      checks: [
        { id: "k1", name: "ck_b", column: "b", expression: { "*": "b > 0" } },
        { id: "k2", name: "ck_t", expression: { "*": "a < 10" } },
      ],
    };
    deleteTableColumn(doc, "b");
    expect((doc.checks as Json[]).map((k) => k.name)).toEqual(["ck_t"]);
    const problems = [
      { rule: "MQ4057", severity: "error", message: "x", elementId: "t", filePath: null, jsonPointer: "/columns/0/unicode", line: null, column: null },
      { rule: "MQ3001", severity: "error", message: "y", elementId: "t", filePath: null, jsonPointer: "/columns/10/name", line: null, column: null },
    ] as const;
    expect(columnProblems([...problems], doc, { key: "a" }).map((d) => d.rule)).toEqual(["MQ4057"]);
  });

  it("says when the dialect has no deferrable keys", () => {
    expect(deferrableNote("sqlserver")).toContain("SQL Server");
    expect(deferrableNote("mysql")).toContain("MQ4056");
    expect(deferrableNote("postgresql")).toBeUndefined();
  });
});

describe("the mock's MQ4057 and MQ4056", () => {
  it("reports a facet its type does not have, and a deferrable key or an index method the dialect lacks", () => {
    const model = new MockBackend().model;
    const notes = model.get(NOTES)!;
    const json = structuredClone(notes.json) as unknown as Json;
    const columns = json.columns as Json[];
    columns.find((c) => c.id === ID)!.unicode = true; // a uuid has no text encoding
    columns.find((c) => c.id === BODY)!.fixedLength = true; // text has no fixed length
    columns.find((c) => c.id === ENTITY_TYPE)!.unicode = false; // fine
    json.foreignKeys = [{ id: "01K6BND00000000000000000F1", name: "fk_notes_self", columns: [ID], referencesTable: NOTES, deferrable: "initially-deferred" }];
    (json.indexes as Json[])[0].method = "gin";
    const saved = model.save(NOTES, json, notes.hash);
    const rules = (saved.diagnostics ?? []).map((d) => `${d.rule} ${d.jsonPointer}`);
    expect(rules.filter((r) => r.startsWith("MQ4057")).sort()).toEqual(["MQ4057 /columns/0/unicode", "MQ4057 /columns/3/fixedLength"]);
    expect(saved.outcome).toBe("invalid");
    // Without the facet errors, PostgreSQL has deferrable keys and gin indexes: nothing to say.
    delete columns.find((c) => c.id === ID)!.unicode;
    delete columns.find((c) => c.id === BODY)!.fixedLength;
    const ok = model.save(NOTES, json, notes.hash);
    expect(ok.outcome).toBe("saved");
    expect(model.validate().diagnostics.filter((d) => d.rule === "MQ4056")).toEqual([]);
    // On SQL Server both are left out of the DDL, said as warnings on the table.
    const db = model.get(MAIN)!;
    model.save(MAIN, { ...(db.json as unknown as Json), dialect: "sqlserver" }, db.hash);
    const again = model.get(NOTES)!;
    model.save(NOTES, structuredClone(again.json) as unknown as Json, again.hash);
    const warnings = model
      .validate()
      .diagnostics.filter((d) => d.rule === "MQ4056" && d.elementId === NOTES)
      .map((d) => d.jsonPointer)
      .sort();
    expect(warnings).toEqual(["/foreignKeys/0/deferrable", "/indexes/0/method"]);
  });
});

describe("a foreign key's columns set on delete", () => {
  it("ticks columns in key order, drops them off set null or set default, and keeps only those still in the key", () => {
    const fk: Json = { id: "f1", columns: ["tenant", "folder"], referencesTable: "t", onDelete: "set-null" };
    toggleOnDeleteColumn(fk, "folder");
    toggleOnDeleteColumn(fk, "tenant");
    expect(fk.onDeleteColumns).toEqual(["tenant", "folder"]);
    toggleOnDeleteColumn(fk, "tenant");
    expect(fk.onDeleteColumns).toEqual(["folder"]);
    setForeignKeyAction(fk, "onDelete", "set-default");
    expect(fk.onDeleteColumns).toEqual(["folder"]);
    fk.columns = ["tenant"];
    keepOnDeleteColumns(fk);
    expect("onDeleteColumns" in fk).toBe(false);
    fk.onDeleteColumns = ["tenant"];
    setForeignKeyAction(fk, "onDelete", "cascade");
    expect(fk).toMatchObject({ onDelete: "cascade" });
    expect("onDeleteColumns" in fk).toBe(false);
    setForeignKeyAction(fk, "onDelete", "no-action");
    expect("onDelete" in fk).toBe(false);
    expect(setsColumns("set-null") && !setsColumns("restrict")).toBe(true);
    expect(onDeleteColumnsNote("postgresql")).toBeNull();
    expect(onDeleteColumnsNote("mysql")).toContain("MQ4056");
  });

  it("is MQ4060 off set null or off the key in the mock, and MQ4056 outside PostgreSQL", () => {
    const model = new MockBackend().model;
    const notes = model.get(NOTES)!;
    const json = structuredClone(notes.json) as unknown as Json;
    const fk: Json = {
      id: "01K6BND00000000000000000F1",
      name: "fk_notes_self",
      columns: [ID],
      referencesTable: NOTES,
      onDelete: "cascade",
      onDeleteColumns: [ID],
    };
    json.foreignKeys = [fk];
    const pointers = () => (model.save(NOTES, json, model.get(NOTES)!.hash).diagnostics ?? []).filter((d) => d.rule === "MQ4060").map((d) => d.jsonPointer);
    expect(pointers()).toEqual(["/foreignKeys/0/onDeleteColumns"]);
    fk.onDelete = "set-null";
    fk.onDeleteColumns = [ID, BODY, ID];
    expect(pointers()).toEqual(["/foreignKeys/0/onDeleteColumns/1", "/foreignKeys/0/onDeleteColumns/2"]);
    fk.onDeleteColumns = [ID];
    expect(pointers()).toEqual([]);
    expect(model.validate().diagnostics.filter((d) => d.rule === "MQ4056" && d.elementId === NOTES)).toEqual([]);
    const db = model.get(MAIN)!;
    model.save(MAIN, { ...(db.json as unknown as Json), dialect: "sqlserver" }, db.hash);
    model.save(NOTES, json, model.get(NOTES)!.hash);
    const warnings = model.validate().diagnostics.filter((d) => d.rule === "MQ4056" && d.elementId === NOTES);
    expect(warnings.map((d) => d.jsonPointer)).toContain("/foreignKeys/0/onDeleteColumns");
  });

  it("warns (MQ4061) when set null would null a column that is not nullable, unless the key lists only nullable ones", () => {
    const model = new MockBackend().model;
    const json = structuredClone(model.get(NOTES)!.json) as unknown as Json;
    const fk: Json = { id: "01K6BND00000000000000000F1", name: "fk_notes_self", columns: [ID, BODY], referencesTable: NOTES, onDelete: "set-null" };
    json.foreignKeys = [fk];
    const found = () => (model.save(NOTES, json, model.get(NOTES)!.hash).diagnostics ?? []).filter((d) => d.rule === "MQ4061");
    const [warning] = found();
    expect(warning).toMatchObject({ severity: "warning", jsonPointer: "/foreignKeys/0/onDelete" });
    expect(warning.message).toContain("onDeleteColumns");
    (json.columns as Json[]).find((c) => c.id === BODY)!.nullable = true;
    fk.onDeleteColumns = [BODY];
    expect(found()).toEqual([]);
  });
});
