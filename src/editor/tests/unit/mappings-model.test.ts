// The Mappings model: joining attributes to columns by attribute path, row states, and the edits
// that create or change the mapping element.
import { describe, expect, it } from "vitest";
import type { ColumnView, MappingDoc } from "@/api/types";
import {
  applyAttributeOverride,
  applyEntityOverride,
  columnOf,
  isEmptyPatch,
  mappingRows,
  otherColumns,
  storageOptions,
  PendingCreates,
} from "@/workspaces/mappings/model";

const column = (key: string, attributeId: string | null, attributePath: string | null) => ({ key, name: key, attributeId, attributePath }) as ColumnView;

const table = {
  columns: [
    column("id", "a-id", "a-id"),
    column("a-total", "a-total", "a-total"),
    column("a-addr.m-street", "m-street", "a-addr.m-street"),
    column("a-addr.m-city", "m-city", "a-addr.m-city"),
    column("end1.a-id", null, null),
    column("discriminator", null, null),
    column("legacy", "a-legacy", null),
  ],
};
const attributes = [{ id: "a-id" }, { id: "a-total" }, { id: "a-addr" }, { id: "a-legacy" }, { id: "a-gone" }];

describe("joining", () => {
  it("matches own columns, embedded value-object members by path prefix, and path-less columns by attributeId", () => {
    expect(columnOf("a-addr", table.columns[2])).toBe(true);
    expect(columnOf("a-add", table.columns[2])).toBe(false);
    expect(columnOf("m-street", table.columns[2])).toBe(false);
    expect(columnOf("a-legacy", table.columns[6])).toBe(true);
  });

  it("builds a row per attribute with its columns and the leftover columns", () => {
    const rows = mappingRows(attributes, table, null);
    expect(rows.map((r) => r.columns.map((c) => c.key))).toEqual([["id"], ["a-total"], ["a-addr.m-street", "a-addr.m-city"], ["legacy"], []]);
    expect(rows.every((r) => r.state === "convention" && r.override === null)).toBe(true);
    expect(otherColumns(table, rows).map((c) => c.key)).toEqual(["end1.a-id", "discriminator"]);
  });

  it("marks overridden and ignored rows", () => {
    const mapping = {
      attributes: [
        { attribute: "a-addr", prefix: "bill_" },
        { attribute: "a-total", ignore: true },
      ],
    } as MappingDoc;
    const rows = mappingRows(attributes, table, mapping);
    expect(rows.find((r) => r.attribute.id === "a-addr")).toMatchObject({ state: "override", override: { prefix: "bill_" } });
    expect(rows.find((r) => r.attribute.id === "a-total")?.state).toBe("ignored");
    expect(rows.find((r) => r.attribute.id === "a-id")?.state).toBe("convention");
  });

  it("has no columns without a table", () => {
    expect(mappingRows(attributes, null, null)[0].columns).toEqual([]);
    expect(otherColumns(null, [])).toEqual([]);
  });
});

describe("editing", () => {
  it("adds, merges and removes attribute overrides", () => {
    const m = {} as MappingDoc;
    applyAttributeOverride(m, "a-addr", { storage: "json" });
    expect(m.attributes).toEqual([{ attribute: "a-addr", storage: "json" }]);
    applyAttributeOverride(m, "a-addr", { prefix: "bill_" });
    expect(m.attributes).toEqual([{ attribute: "a-addr", storage: "json", prefix: "bill_" }]);
    applyAttributeOverride(m, "a-addr", { storage: undefined, prefix: "" });
    expect(m.attributes).toBeUndefined();
  });

  it("removes an override on a null patch and leaves others alone", () => {
    const m = {
      attributes: [
        { attribute: "a", ignore: true },
        { attribute: "b", prefix: "x" },
      ],
    } as MappingDoc;
    applyAttributeOverride(m, "a", null);
    expect(m.attributes).toEqual([{ attribute: "b", prefix: "x" }]);
    applyAttributeOverride(m, "c", { ignore: false });
    expect(m.attributes).toEqual([{ attribute: "b", prefix: "x" }]);
  });

  it("sets and clears entity-level fields", () => {
    const m = { kind: "mapping", id: "m", database: "d", entity: "e" } as MappingDoc;
    applyEntityOverride(m, { inheritance: "tpt", ignore: true });
    expect(m).toMatchObject({ inheritance: "tpt", ignore: true });
    applyEntityOverride(m, { inheritance: undefined, ignore: false, table: "" });
    expect(m).toEqual({ kind: "mapping", id: "m", database: "d", entity: "e" });
  });

  it("does not create a mapping for an empty patch", () => {
    expect(isEmptyPatch(null)).toBe(true);
    expect(isEmptyPatch({ ignore: false, prefix: "" })).toBe(true);
    expect(isEmptyPatch({ inheritance: "tph" })).toBe(false);
  });

  it("offers storage choices by referenced kind", () => {
    expect(storageOptions("enum")).toEqual(["int", "string"]);
    expect(storageOptions("value-object")).toEqual(["embedded", "table", "json"]);
    expect(storageOptions(undefined)).toEqual([]);
  });
});

describe("creating a mapping once", () => {
  it("applies edits made while the create is in flight to the created mapping", async () => {
    const pending = new PendingCreates();
    let creates = 0;
    let resolve!: (id: string) => void;
    const create = () => {
      creates++;
      return new Promise<string | null>((r) => (resolve = r));
    };
    const edited: string[] = [];
    const edit = (id: string) => {
      edited.push(id);
    };
    const first = pending.run("customer|main", create, edit);
    const second = pending.run("customer|main", create, edit);
    const third = pending.run("customer|main", create, edit);
    resolve("m1");
    await Promise.all([first, second, third]);
    expect(creates).toBe(1);
    expect(edited).toEqual(["m1", "m1"]);
    // Until the index shows it, a later edit still goes to the created mapping.
    await pending.run("customer|main", create, edit);
    expect(creates).toBe(1);
    pending.forget("customer|main");
    expect(pending.has("customer|main")).toBe(false);
  });

  it("allows a new create after a failed one", async () => {
    const pending = new PendingCreates();
    await pending.run(
      "k",
      () => Promise.resolve(null),
      () => undefined,
    );
    expect(pending.has("k")).toBe(false);
  });
});
