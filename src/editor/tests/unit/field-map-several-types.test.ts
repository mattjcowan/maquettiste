// The field map of a binding offers the forms of a link to an end with several types (several-types-design.md decision 46, 19.10), and
// Map by name matches them.
import { describe, expect, it } from "vitest";
import { matchByName, storageAttributes, type Binding, type SourceColumn } from "@/editors/storage/fieldMap";

type Json = Record<string, unknown>;

const docs = (relation: Json, entityExtra: Json = {}): Map<string, Json> =>
  new Map<string, Json>([
    ["Comment", { kind: "entity", id: "Comment", name: "Comment", attributes: [], ...entityExtra }],
    ["Part", { kind: "entity", id: "Part", name: "Part" }],
    ["Assembly", { kind: "entity", id: "Assembly", name: "Assembly" }],
    ["rel", relation],
  ]);

const attachedTo = (extra: Json = {}): Json => ({
  kind: "relation",
  id: "rel",
  name: "AttachedTo",
  ends: [
    { id: "e1", entity: "Comment", role: "comment", max: "*" },
    { id: "e2", types: [{ entity: "Part" }, { entity: "Assembly" }], role: "attachedTo", max: 1, ...extra },
  ],
});

const relationRows = (id: string, d: Map<string, Json>) =>
  storageAttributes(id, d)
    .filter((a) => a.origin === "relation")
    .map((a) => ({ ref: a.ref, name: a.name, from: a.from }));

describe("the field map's forms for an end with several types", () => {
  it("offers a type, an id and a key per type to the entity the other end covers", () => {
    expect(relationRows("Comment", docs(attachedTo()))).toEqual([
      { ref: "e2.type", name: "attachedToType", from: "AttachedTo → Part, Assembly" },
      { ref: "e2", name: "attachedToId", from: "AttachedTo → Part, Assembly" },
      { ref: "e2.Part", name: "attachedToPartId", from: "AttachedTo → Part, Assembly" },
      { ref: "e2.Assembly", name: "attachedToAssemblyId", from: "AttachedTo → Part, Assembly" },
    ]);
  });

  it("offers nothing for a to-many end, or for an end the entity does not hold", () => {
    expect(relationRows("Part", docs(attachedTo()))).toEqual([]);
    expect(relationRows("Comment", docs(attachedTo({ max: "*" })))).toEqual([]);
  });

  it("offers an end the entity holds through a type of the other end (way 1)", () => {
    const d = docs({
      kind: "relation",
      id: "rel",
      name: "Filed",
      ends: [
        { id: "items", types: [{ entity: "Part" }, { entity: "Assembly" }], role: "items", max: "*" },
        { id: "folder", entity: "Comment", role: "folder", max: 1 },
      ],
    });
    expect(relationRows("Part", d)).toEqual([{ ref: "folder", name: "folderId", from: "Filed → Comment" }]);
  });

  it("offers the forms of an any-entity end with a type and an id only", () => {
    const rows = relationRows("Comment", docs(attachedTo({ types: undefined, anyEntity: true })));
    expect(rows.map((r) => r.ref)).toEqual(["e2.type", "e2"]);
    expect(rows[0].from).toBe("AttachedTo → any entity");
  });

  it("reads a stereotype entry as covered by an entity that carries it", () => {
    const d = docs(
      {
        kind: "relation",
        id: "rel",
        name: "Tagged",
        ends: [
          { id: "a", types: [{ stereotype: "approvable" }, { entity: "Part" }], role: "tagged", max: "*" },
          { id: "b", entity: "Assembly", role: "owner", max: 1 },
        ],
      },
      { stereotypes: ["approvable"] },
    );
    expect(relationRows("Comment", d).map((r) => r.ref)).toEqual(["b"]);
  });

  it("matches the forms to columns by name", () => {
    const attributes = storageAttributes("Comment", docs(attachedTo()));
    const column = (name: string): SourceColumn => ({
      key: name,
      name,
      type: null,
      nullable: true,
      identity: false,
      computed: false,
      hasDefault: false,
      isPrimaryKey: false,
    });
    const columns = ["attached_to_type", "attached_to_id", "attached_to_part_id", "other"].map(column);
    const binding: Binding = { id: "b", database: "d", source: "s" };
    const matches = matchByName(binding, attributes, columns).map((m) => [m.attribute.ref, m.column.name]);
    expect(matches).toEqual([
      ["e2.type", "attached_to_type"],
      ["e2", "attached_to_id"],
      ["e2.Part", "attached_to_part_id"],
    ]);
  });
});
