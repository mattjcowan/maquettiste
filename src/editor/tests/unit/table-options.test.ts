// A table's options beyond its columns and keys (workspaces/database/tableOptions.ts): storage parameters per dialect with a
// stereotype's profile, routine settings, partitioning, exclusion constraints; the index operator class; and the mock's MQ4056
// for what PostgreSQL alone writes.
import { describe, expect, it } from "vitest";
import {
  addExclusion,
  addExclusionElement,
  addPartition,
  editSettings,
  editStorage,
  exclusionText,
  inheritedStorage,
  partitionKeyProblem,
  postgresIndexParameters,
  postgresOnlyNote,
  removeExclusion,
  removeExclusionElement,
  removePartition,
  setExclusion,
  setExclusionElement,
  setPartition,
  setPartitionStrategy,
  storageKeyProblem,
  storageOf,
  togglePartitionColumn,
} from "@/workspaces/database/tableOptions";
import { indexColumnNote, setIndexColumnOperatorClass, temporalNote } from "@/workspaces/database/tableParts";
import { MockBackend } from "@/mocks/backend";

type Json = Record<string, unknown>;
const NOTES = "01K6BND0000000000000000001";
const ENTITY_TYPE = "01K6BND0000000000000000003";
const ID = "01K6BND0000000000000000002";
const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";

describe("storage parameters", () => {
  it("edits one dialect's parameters, drops what is left empty, and shows a stereotype's profile under the table's own", () => {
    const doc: Json = {};
    editStorage(doc, "postgresql", { op: "set", key: "fillfactor", value: 90 });
    editStorage(doc, "sqlserver", { op: "set", key: "data_compression", value: "PAGE" });
    expect(doc.storage).toEqual({ postgresql: { fillfactor: 90 }, sqlserver: { data_compression: "PAGE" } });
    editStorage(doc, "postgresql", { op: "set", key: "autovacuum_enabled", value: false, from: "fillfactor" });
    expect(storageOf(doc, "postgresql")).toEqual({ autovacuum_enabled: false });
    editStorage(doc, "postgresql", { op: "remove", key: "autovacuum_enabled" });
    editStorage(doc, "sqlserver", { op: "remove", key: "data_compression" });
    expect("storage" in doc).toBe(false);

    const inherited = inheritedStorage(
      [
        { key: "high-churn", storage: { postgresql: { fillfactor: 70, autovacuum_vacuum_scale_factor: 0.03 } } },
        { key: "tuned", storage: { postgresql: { fillfactor: 80 } } },
      ],
      "postgresql",
    );
    expect(inherited).toEqual({ fillfactor: { value: 80, from: "tuned" }, autovacuum_vacuum_scale_factor: { value: 0.03, from: "high-churn" } });
    expect(storageKeyProblem("toast.autovacuum_enabled")).toBeNull();
    expect(storageKeyProblem("fill factor")).toContain("letters");
    expect(postgresIndexParameters("hnsw")).toEqual(["m", "ef_construction"]);
    expect(postgresIndexParameters("default")).toContain("fillfactor");
  });

  it("keeps a routine's settings as text, and drops the map when it is empty", () => {
    const routine: Json = {};
    editSettings(routine, { op: "set", key: "search_path", value: "app, pg_temp" });
    expect(routine.settings).toEqual({ search_path: "app, pg_temp" });
    editSettings(routine, { op: "remove", key: "search_path" });
    expect("settings" in routine).toBe(false);
  });
});

describe("partitioning", () => {
  it("partitions by a strategy, keeps a column, adds named partitions and a default one, and unpartitions", () => {
    const doc: Json = { primaryKey: { columns: ["id"] } };
    setPartitionStrategy(doc, "range", "at");
    expect(doc.partitionBy).toEqual({ strategy: "range", columns: ["at"] });
    expect(togglePartitionColumn(doc, "at")).toBe(false);
    expect(partitionKeyProblem(doc, (id) => id)).toContain("lacks at");
    (doc.primaryKey as Json).columns = ["id", "at"];
    expect(partitionKeyProblem(doc, (id) => id)).toBeNull();
    addPartition(doc, "P1", "events");
    addPartition(doc, "P2", "events", true);
    expect(doc.partitions).toEqual([
      { id: "P1", name: "events_p1", bounds: "FROM () TO ()" },
      { id: "P2", name: "events_default", default: true },
    ]);
    expect(setPartition(doc, 0, "bounds", "FROM ('2026-01-01') TO ('2026-02-01')")).toBe(true);
    expect(setPartition(doc, 1, "bounds", "x")).toBe(false);
    expect(setPartition(doc, 1, "name", "events_p1")).toBe(false);
    setPartitionStrategy(doc, "hash", undefined);
    expect((doc.partitions as Json[])[1].default).toBeUndefined();
    removePartition(doc, 0);
    setPartitionStrategy(doc, undefined, undefined);
    expect("partitionBy" in doc || "partitions" in doc).toBe(false);
  });
});

describe("exclusion constraints", () => {
  it("adds one over a column, edits its elements, method, predicate and deferral, and writes it as the DDL does", () => {
    const doc: Json = {};
    addExclusion(doc, "X1", "room");
    expect(addExclusionElement(doc, 0, "during")).toBe(true);
    setExclusionElement(doc, 0, 1, "operator", "&&");
    expect(setExclusionElement(doc, 0, 1, "operator", " ")).toBe(false);
    setExclusion(doc, 0, "where", "NOT cancelled");
    setExclusion(doc, 0, "method", "gist");
    setExclusion(doc, 0, "deferrable", "initially-deferred");
    expect(exclusionText((doc.exclusions as Json[])[0], (id) => id)).toBe("EXCLUDE USING gist (room WITH =, during WITH &&) WHERE (NOT cancelled)");
    expect((doc.exclusions as Json[])[0]).toMatchObject({ deferrable: "initially-deferred" });
    expect("method" in (doc.exclusions as Json[])[0]).toBe(false);
    setExclusionElement(doc, 0, 0, "expression", "lower(room)");
    expect(((doc.exclusions as Json[])[0].elements as Json[])[0]).toEqual({ expression: "lower(room)", operator: "=" });
    expect(removeExclusionElement(doc, 0, 0)).toBe(true);
    expect(removeExclusionElement(doc, 0, 0)).toBe(false);
    removeExclusion(doc, 0);
    expect("exclusions" in doc).toBe(false);
  });
});

describe("index operator classes and the notes", () => {
  it("sets and clears an operator class, and says what other dialects leave out", () => {
    const entry: Json = { columns: [{ column: "title" }] };
    expect(setIndexColumnOperatorClass(entry, 0, " gin_trgm_ops ")).toBe(true);
    expect((entry.columns as Json[])[0]).toEqual({ column: "title", operatorClass: "gin_trgm_ops" });
    setIndexColumnOperatorClass(entry, 0, "");
    expect((entry.columns as Json[])[0]).toEqual({ column: "title" });
    expect(setIndexColumnOperatorClass(entry, 3, "x")).toBe(false);
    expect(indexColumnNote("mysql", { expression: false, length: false, operatorClass: true })).toContain("MQ4056");
    expect(indexColumnNote("postgresql", { expression: false, length: false, operatorClass: true })).toBeNull();
    expect(temporalNote("sqlserver")).toContain("MQ4056");
    expect(temporalNote("postgresql")).toContain("btree_gist");
    expect(postgresOnlyNote("postgresql", "x")).toBeNull();
  });

  it("is MQ4056 in the mock outside PostgreSQL for a temporal key, an exclusion, partitioning and an operator class", () => {
    const model = new MockBackend().model;
    const notes = model.get(NOTES)!;
    const json = structuredClone(notes.json) as unknown as Json;
    json.primaryKey = { columns: [ID, ENTITY_TYPE], withoutOverlaps: true };
    json.exclusions = [{ id: "01K6BND00000000000000000X1", elements: [{ column: ENTITY_TYPE, operator: "=" }] }];
    json.partitionBy = { strategy: "list", columns: [ENTITY_TYPE] };
    (((json.indexes as Json[])[0].columns as Json[])[0] as Json).operatorClass = "text_pattern_ops";
    model.save(NOTES, json, notes.hash);
    expect(model.validate().diagnostics.filter((d) => d.rule === "MQ4056" && d.elementId === NOTES)).toEqual([]);
    const db = model.get(MAIN)!;
    model.save(MAIN, { ...(db.json as unknown as Json), dialect: "sqlserver" }, db.hash);
    model.save(NOTES, json, model.get(NOTES)!.hash);
    const warned = model
      .validate()
      .diagnostics.filter((d) => d.rule === "MQ4056" && d.elementId === NOTES)
      .map((d) => d.jsonPointer)
      .sort();
    expect(warned).toEqual(["/exclusions/0", "/indexes/0/columns/0/operatorClass", "/partitionBy", "/primaryKey/withoutOverlaps"]);
  });
});
