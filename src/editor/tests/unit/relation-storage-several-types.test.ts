// The Held in and Linked by choices of a relation with an end of several types (several-types-design.md 19.10), without React: what is
// offered, the entries each pair writes, the pair read back from a mapping, and the pickers' filters.
import { describe, expect, it } from "vitest";
import {
  clearEntryWays,
  idColumnChoices,
  keysReferring,
  linkedByEnds,
  offeredHeldIn,
  offeredLinkedBy,
  primaryKeyColumn,
  readChoice,
  setAttributeColumn,
  setEntryColumns,
  setEntryKey,
  setEntryKeys,
  setHeldIn,
  setLinkTable,
  setMappingConstants,
  setRefersTo,
  setTypeKey,
  sharedTables,
  storageEndOf,
  typeColumnChoices,
  uniqueColumns,
  type StorageEnd,
  type StorageMapping,
  type TableDocWithKeys,
} from "@/editors/storage/relationStorage";

const comment = storageEndOf({ id: "e1", entity: "Comment", max: "*" });
const attachedOne = storageEndOf({ id: "e2", types: [{ entity: "Part" }, { entity: "Assembly" }], max: 1 });
const attachedMany = storageEndOf({ id: "e2", types: [{ entity: "Part" }, { entity: "Assembly" }], max: "*" });
const attachedAny = storageEndOf({ id: "e2", anyEntity: true, max: 1 });
const commentMany = storageEndOf({ id: "e1", types: [{ entity: "Comment" }, { entity: "Note" }], max: "*" });
const commentOne = storageEndOf({ id: "e1", entity: "Comment", max: 1 });

describe("storageEndOf", () => {
  it("reads a list of one entity as a one-type end", () => {
    expect(storageEndOf({ id: "x", types: [{ entity: "Part" }], max: 1 })).toMatchObject({ several: false, entities: ["Part"], toOne: true });
  });
  it("reads a stereotype entry and any entity as several types", () => {
    expect(storageEndOf({ id: "x", types: [{ stereotype: "approvable" }] }).several).toBe(true);
    expect(storageEndOf({ id: "x", anyEntity: true })).toMatchObject({ several: true, any: true });
  });
});

describe("what Held in offers", () => {
  it("offers the other end's table only when the end with several types is to-one", () => {
    expect(offeredHeldIn([comment, attachedOne])).toEqual(["other-table", "link-table", "table-per-type", "none"]);
  });
  it("offers each type's table when the one-type end is to-one", () => {
    expect(offeredHeldIn([commentOne, attachedMany])).toEqual(["type-tables", "link-table", "table-per-type", "none"]);
  });
  it("offers only a link table, a table per type and not here for a many-to-many", () => {
    expect(offeredHeldIn([comment, attachedMany])).toEqual(["link-table", "table-per-type", "none"]);
  });
  it("offers no table per type, and no type tables, with any entity", () => {
    expect(offeredHeldIn([commentOne, attachedAny])).toEqual(["other-table", "link-table", "none"]);
    expect(offeredHeldIn([commentOne, { ...attachedAny, toOne: false }])).toEqual(["link-table", "none"]);
  });
  it("offers only a link table, or none, with several types at both ends", () => {
    expect(offeredHeldIn([commentMany, attachedMany])).toEqual(["link-table", "none"]);
  });
  it("offers nothing for a one-type relation", () => {
    expect(offeredHeldIn([comment, storageEndOf({ id: "e2", entity: "Part" })])).toEqual([]);
  });
});

describe("what Linked by offers", () => {
  it("offers four ways on an end with several types, in the other end's table", () => {
    expect(offeredLinkedBy("other-table", attachedOne)).toEqual(["shared-key", "column-per-type", "type-and-id", "id"]);
    expect(offeredLinkedBy("other-table", comment)).toEqual([]);
  });
  it("offers a key on a one-type end of a link table, and every way on the other", () => {
    expect(offeredLinkedBy("link-table", comment)).toEqual(["key"]);
    expect(offeredLinkedBy("link-table", attachedOne)).toContain("column-per-type");
  });
  it("never offers a column per type on an any-entity end", () => {
    expect(offeredLinkedBy("link-table", attachedAny)).toEqual(["shared-key", "type-and-id", "id"]);
    expect(offeredLinkedBy("other-table", attachedAny)).not.toContain("column-per-type");
  });
  it("fixes the choice for each type's table and a table per type", () => {
    for (const held of ["type-tables", "table-per-type", "none", null] as const) expect(offeredLinkedBy(held, attachedOne)).toEqual([]);
  });
  it("asks for each end under a link table, and the end with several types otherwise", () => {
    const ends: StorageEnd[] = [comment, attachedOne];
    expect(linkedByEnds("link-table", ends).map((e) => e.id)).toEqual(["e1", "e2"]);
    expect(linkedByEnds("other-table", ends).map((e) => e.id)).toEqual(["e2"]);
    expect(linkedByEnds("type-tables", ends)).toEqual([]);
  });
});

describe("writing a choice and reading it back", () => {
  const ends = [comment, attachedOne];
  const roundTrip = (json: StorageMapping) => readChoice(json, ends);

  it("reads a mapping that states nothing as no choice, and ignore as not stored", () => {
    expect(readChoice(null, ends)).toEqual({ heldIn: null, linkedBy: {} });
    expect(roundTrip({})).toEqual({ heldIn: null, linkedBy: {} });
    expect(roundTrip({ ignore: true })).toEqual({ heldIn: "none", linkedBy: {} });
  });

  it("writes and reads the other end's table with each way", () => {
    const json: StorageMapping = {};
    setHeldIn(json, "other-table", ends);
    expect(json).toEqual({});
    setTypeKey(json, "e2", ["Part", "Assembly"], "Assembly", "fk_assembly");
    setTypeKey(json, "e2", ["Part", "Assembly"], "Part", "fk_part");
    expect(json.ends).toEqual([
      {
        end: "e2",
        types: [
          { entity: "Part", foreignKey: "fk_part" },
          { entity: "Assembly", foreignKey: "fk_assembly" },
        ],
      },
    ]);
    expect(roundTrip(json).linkedBy).toEqual({ e2: "column-per-type" });
    clearEntryWays(json, "e2");
    setEntryColumns(json, "e2", { idColumn: "c_id", typeColumn: "c_type" });
    expect(roundTrip(json).linkedBy).toEqual({ e2: "type-and-id" });
    setEntryColumns(json, "e2", { typeColumn: null });
    expect(roundTrip(json).linkedBy).toEqual({ e2: "id" });
    clearEntryWays(json, "e2");
    setEntryKey(json, "e2", "fk_item");
    expect(roundTrip(json).linkedBy).toEqual({ e2: "shared-key" });
  });

  it("writes each type's table and a table per type with no choice to make", () => {
    const type: StorageMapping = {};
    setHeldIn(type, "type-tables", [commentOne, attachedMany]);
    expect(type.ends).toBeUndefined();
    setEntryKeys(type, "e1", ["fk_a", "fk_b"]);
    expect(readChoice(type, [commentOne, attachedMany])).toEqual({ heldIn: "type-tables", linkedBy: {} });

    const per: StorageMapping = {};
    setHeldIn(per, "table-per-type", ends);
    expect(per.ends).toBeUndefined();
    setEntryKeys(per, "e1", ["fk_a", "fk_b"]);
    setTypeKey(per, "e2", ["Part", "Assembly"], "Part", "fk_part");
    expect(roundTrip(per)).toEqual({ heldIn: "table-per-type", linkedBy: {} });
  });

  it("writes a link table with a key on the one-type end and a way on the other", () => {
    const json: StorageMapping = {};
    setLinkTable(json, "links");
    expect(json).toEqual({ junctionTable: "links", shape: "junction" });
    expect(roundTrip(json)).toEqual({ heldIn: "link-table", linkedBy: { e1: "", e2: "" } });
    setEntryKey(json, "e1", "fk_comment");
    setEntryColumns(json, "e2", { idColumn: "to_id", typeColumn: "to_type" });
    expect(roundTrip(json)).toEqual({ heldIn: "link-table", linkedBy: { e1: "key", e2: "type-and-id" } });
  });

  it("reads several types at both ends in a link table", () => {
    const both = [commentMany, attachedMany];
    const json: StorageMapping = {};
    setLinkTable(json, "links");
    setEntryColumns(json, "e1", { idColumn: "from_id", typeColumn: "from_type" });
    setEntryColumns(json, "e2", { idColumn: "to_id" });
    expect(readChoice(json, both)).toEqual({ heldIn: "link-table", linkedBy: { e1: "type-and-id", e2: "id" } });
  });

  it("leaves a link table with its table, constants and attribute columns gone, and ignore set", () => {
    const json: StorageMapping = {
      attributes: [
        { attribute: "note", column: "c" },
        { attribute: "other", storage: "json" },
      ],
    };
    setLinkTable(json, "links");
    setMappingConstants(json, [{ column: "code", value: "A" }]);
    setHeldIn(json, "none", ends);
    expect(json).toEqual({ ignore: true, attributes: [{ attribute: "other", storage: "json" }] });
    setHeldIn(json, "other-table", ends);
    expect(json.ignore).toBeUndefined();
  });

  it("keeps the link table and its entries when A link table is chosen again", () => {
    const json: StorageMapping = { junctionTable: "links", shape: "junction", ends: [{ end: "e1", foreignKey: "fk" }] };
    setHeldIn(json, "link-table", ends);
    expect(json.junctionTable).toBe("links");
    expect(json.ends).toEqual([{ end: "e1", foreignKey: "fk" }]);
  });

  it("never writes an entry with no way", () => {
    const json: StorageMapping = {};
    setEntryKey(json, "e1", "fk");
    setEntryKey(json, "e1", null);
    expect(json.ends).toBeUndefined();
    setEntryColumns(json, "e2", { idColumn: "c" });
    setEntryColumns(json, "e2", { idColumn: null });
    expect(json.ends).toBeUndefined();
  });

  it("clears the entries and constants when the link table changes", () => {
    const json: StorageMapping = { junctionTable: "a", ends: [{ end: "e1", foreignKey: "fk" }], constants: [{ column: "c", value: 1 }] };
    setLinkTable(json, "b");
    expect(json.ends).toBeUndefined();
    expect(json.constants).toBeUndefined();
    setLinkTable(json, null);
    expect(json).toEqual({});
  });

  it("shows a mapping that matches no pair as written", () => {
    expect(roundTrip({ foreignKey: "fk" })).toMatchObject({ heldIn: "as-written" });
    expect(roundTrip({ shape: "promoted" })).toMatchObject({ heldIn: "as-written" });
    expect(
      roundTrip({
        ends: [
          { end: "e1", foreignKey: "x" },
          { end: "e2", idColumn: "y" },
        ],
      }),
    ).toMatchObject({ heldIn: "as-written" });
    expect(roundTrip({ ends: [{ end: "e2", foreignKey: "x", idColumn: "y" }] })).toMatchObject({ heldIn: "as-written" });
    expect(
      roundTrip({
        ends: [
          { end: "e2", idColumn: "y" },
          { end: "e2", idColumn: "z" },
        ],
      }),
    ).toMatchObject({ heldIn: "as-written" });
    // A type column on a one-type end of a link table is not offered: shown as written.
    expect(roundTrip({ junctionTable: "t", ends: [{ end: "e1", idColumn: "a", typeColumn: "b" }] })).toMatchObject({ heldIn: "as-written" });
  });

  it("writes the constants and an attribute column, and drops them when empty", () => {
    const json: StorageMapping = {};
    setMappingConstants(json, [
      { column: "code", value: "AttachedTo" },
      { column: "flag", value: null },
    ]);
    expect(json.constants).toEqual([{ column: "code", value: "AttachedTo" }, { column: "flag" }]);
    setMappingConstants(json, []);
    expect(json.constants).toBeUndefined();
    setAttributeColumn(json, "note", "c_note");
    expect(json.attributes).toEqual([{ attribute: "note", column: "c_note" }]);
    setAttributeColumn(json, "note", null);
    expect(json.attributes).toBeUndefined();
  });

  it("names what an id refers to per type, in type order", () => {
    const json: StorageMapping = {};
    setHeldIn(json, "other-table", ends);
    setEntryColumns(json, "e2", { idColumn: "c_id" });
    setRefersTo(json, "e2", ["Part", "Assembly"], "Assembly", "uid_a");
    setRefersTo(json, "e2", ["Part", "Assembly"], "Part", "uid_p");
    expect(json.ends![0].types).toEqual([
      { entity: "Part", referencesColumn: "uid_p" },
      { entity: "Assembly", referencesColumn: "uid_a" },
    ]);
    expect(roundTrip(json).linkedBy).toEqual({ e2: "id" });
    setRefersTo(json, "e2", ["Part", "Assembly"], "Part", null);
    expect(json.ends![0].types).toEqual([{ entity: "Assembly", referencesColumn: "uid_a" }]);
  });
});

describe("the pickers' filters", () => {
  const comments: TableDocWithKeys = {
    id: "comments",
    columns: [
      { id: "c1", name: "id", type: "integer" },
      { id: "c2", name: "attached_type", type: "string" },
      { id: "c3", name: "attached_id", type: "integer" },
      { id: "c4", name: "note", type: "text" },
    ],
    foreignKeys: [
      { id: "fk1", name: "fk_part", columns: ["c3"], referencesTable: "parts" },
      { id: "fk2", name: "fk_assembly", columns: ["c3"], referencesTable: "assemblies" },
      { id: "fk3", name: "fk_item", columns: ["c3"], referencesTable: "items" },
    ],
  };
  const parts: TableDocWithKeys = {
    id: "parts",
    columns: [
      { id: "p1", name: "id", type: "integer" },
      { id: "p2", name: "uid", type: "string" },
      { id: "p3", name: "code", type: "string" },
    ],
    primaryKey: { columns: ["p1"] },
    uniques: [{ columns: ["uid"] }],
    indexes: [
      { columns: ["p3"], unique: true, where: "code is not null" },
      { columns: ["p2", "p3"], unique: true },
    ],
  };

  it("offers the keys of the holding table that refer to a type's table", () => {
    expect(keysReferring(comments, new Set(["parts"])).map((k) => k.id)).toEqual(["fk1"]);
    expect(keysReferring(comments, new Set(["parts", "items"])).map((k) => k.id)).toEqual(["fk1", "fk3"]);
    expect(keysReferring(undefined, new Set(["parts"]))).toEqual([]);
  });

  it("lists the columns that are unique alone, a filtered or two-column unique left out", () => {
    expect(uniqueColumns(parts).map((c) => c.name)).toEqual(["id", "uid"]);
    expect(uniqueColumns(undefined)).toEqual([]);
  });

  it("names the primary key column only when it is one column", () => {
    expect(primaryKeyColumn(parts)?.name).toBe("id");
    expect(primaryKeyColumn({ ...parts, primaryKey: { columns: ["p1", "p2"] } })).toBeNull();
    expect(primaryKeyColumn({ id: "t", columns: [] })).toBeNull();
  });

  it("filters the id column by the referenced column's type, and keeps the type column apart", () => {
    const refers = parts.columns![0];
    expect(idColumnChoices(comments, refers).map((c) => c.name)).toEqual(["id", "attached_id"]);
    expect(idColumnChoices(comments, refers, "c1").map((c) => c.name)).toEqual(["attached_id"]);
    expect(idColumnChoices(comments, null).length).toBe(4);
    expect(typeColumnChoices(comments, "c3").map((c) => c.name)).toEqual(["id", "attached_type", "note"]);
  });

  it("finds the tables every covered entity has, to offer a key to the shared base table", () => {
    expect(sharedTables([new Set(["parts", "items"]), new Set(["assemblies", "items"])])).toEqual(new Set(["items"]));
    expect(sharedTables([])).toEqual(new Set());
  });
});
