// Relation mappings for relations with several types, in the mock (several-types-design.md part 2, 19.6 and 19.7): MQ4009's file cases,
// MQ4115's new skip, and the delete rule of an `ends` entry.
import { beforeAll, describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";

type Json = Record<string, unknown>;

const ID = {
  part: "01J92P0V0JR8BE8253SKT29C01",
  assembly: "01J92P0V0JR8BE8253SKT29C02",
  comment: "01J92P0V0JR8BE8253SKT29C03",
  rel: "01J92P0V0JR8BE8253SKT29C04",
  one: "01J92P0V0JR8BE8253SKT29C05",
  e1: "01J92P0V0JR8BE8253SKT29C06",
  e2: "01J92P0V0JR8BE8253SKT29C07",
  o1: "01J92P0V0JR8BE8253SKT29C08",
  o2: "01J92P0V0JR8BE8253SKT29C09",
  table: "01J92P0V0JR8BE8253SKT29C0A",
  cType: "01J92P0V0JR8BE8253SKT29C0B",
  cId: "01J92P0V0JR8BE8253SKT29C0C",
  cCode: "01J92P0V0JR8BE8253SKT29C0D",
  fk1: "01J92P0V0JR8BE8253SKT29C0E",
  fk2: "01J92P0V0JR8BE8253SKT29C0F",
  mapping: "01J92P0V0JR8BE8253SKT29C0G",
  oneMapping: "01J92P0V0JR8BE8253SKT29C0H",
};

const abstract = (id: string, name: string): Json => ({ kind: "entity", id, name, abstract: true });

function setup() {
  const backend = new MockBackend();
  const make = (json: Json) => expect(backend.model.create(json).outcome).toBe("saved");
  const database = backend.model.index().find((r) => r.kind === "database")!.id;
  make(abstract(ID.part, "Part"));
  make(abstract(ID.assembly, "Assembly"));
  make(abstract(ID.comment, "Comment"));
  make({
    kind: "relation",
    id: ID.rel,
    name: "AttachedTo",
    ends: [
      { id: ID.e1, entity: ID.comment, role: "comment", max: "*" },
      { id: ID.e2, types: [{ entity: ID.part }, { entity: ID.assembly }], role: "attachedTo", max: 1 },
    ],
  });
  make({
    kind: "relation",
    id: ID.one,
    name: "Owns",
    ends: [
      { id: ID.o1, entity: ID.comment, role: "owner", max: 1 },
      { id: ID.o2, entity: ID.part, role: "owned", max: "*" },
    ],
  });
  make({
    kind: "table",
    id: ID.table,
    name: "comment_links",
    database,
    columns: [
      { id: ID.cType, name: "attached_type", type: "string" },
      { id: ID.cId, name: "attached_id", type: "string" },
      { id: ID.cCode, name: "relation_code", type: "string" },
    ],
    foreignKeys: [
      { id: ID.fk1, name: "fk_a", columns: [ID.cId], referencesTable: ID.table },
      { id: ID.fk2, name: "fk_b", columns: [ID.cCode], referencesTable: ID.table },
    ],
  });
  return { backend, database };
}

let ctx: ReturnType<typeof setup>;
beforeAll(() => {
  ctx = setup();
});

/** The MQ4009 findings of a mapping saved with these keys. */
function findings(relation: string, mapping: Json): { pointer: string | null; message: string }[] {
  const id = relation === ID.rel ? ID.mapping : ID.oneMapping;
  const doc = { kind: "mapping", id, name: "m", database: ctx.database, relation, ...mapping };
  // Created plain, then edited into the form under test, as an outside edit would.
  if (!ctx.backend.model.index().some((r) => r.id === id))
    expect(ctx.backend.model.create({ kind: "mapping", id, name: "m", database: ctx.database, relation, ignore: true }).outcome).toBe("saved");
  ctx.backend.model.externalEdit(id, (json) => {
    for (const k of Object.keys(json)) delete json[k];
    Object.assign(json, doc);
  });
  return ctx.backend.model
    .validate()
    .diagnostics.filter((d) => d.rule === "MQ4009" && d.elementId === id)
    .map((d) => ({ pointer: d.jsonPointer, message: d.message }));
}

describe("MQ4009 on a mapping of a relation with several types", () => {
  it("accepts an entry with one way", () => {
    expect(findings(ID.rel, { junctionTable: ID.table, ends: [{ end: ID.e2, typeColumn: ID.cType, idColumn: ID.cId }] })).toEqual([]);
    expect(findings(ID.rel, { ends: [{ end: ID.e2, idColumn: ID.cId }] })).toEqual([]);
  });

  it("refuses an entry with no way, or with two", () => {
    expect(findings(ID.rel, { ends: [{ end: ID.e2 }] })).toEqual([
      {
        pointer: "/ends/0",
        message:
          "End entry 0 of relation 'AttachedTo' names no way to point at end 'attachedTo'; give it a foreignKey, foreignKeys, types with foreignKey items or an idColumn.",
      },
    ]);
    expect(findings(ID.rel, { ends: [{ end: ID.e2, foreignKey: ID.fk1, idColumn: ID.cId }] }).map((f) => f.message)).toEqual([
      "End entry 0 of relation 'AttachedTo' names more than one way to point at end 'attachedTo' (foreignKey, idColumn); keep one.",
    ]);
  });

  it("refuses a type column without an id column, and one column used twice", () => {
    expect(findings(ID.rel, { ends: [{ end: ID.e2, foreignKey: ID.fk1, typeColumn: ID.cType }] }).map((f) => f.pointer)).toEqual(["/ends/0/typeColumn"]);
    expect(findings(ID.rel, { ends: [{ end: ID.e2, idColumn: ID.cId, typeColumn: ID.cId }] }).map((f) => f.pointer)).toEqual(["/ends/0/typeColumn"]);
  });

  it("refuses types items of the wrong kind", () => {
    const mixed = [
      { entity: ID.part, foreignKey: ID.fk1 },
      { entity: ID.assembly, referencesColumn: ID.cId },
    ];
    expect(findings(ID.rel, { ends: [{ end: ID.e2, types: mixed }] }).map((f) => f.pointer)).toContain("/ends/0/types");
    const beside = [{ end: ID.e2, idColumn: ID.cId, types: [{ entity: ID.part, foreignKey: ID.fk1 }] }];
    expect(findings(ID.rel, { ends: beside }).map((f) => f.pointer)).toContain("/ends/0/types");
  });

  it("refuses two entries for one end, and foreignKeys beside a link table", () => {
    const twice = [
      { end: ID.e2, idColumn: ID.cId },
      { end: ID.e2, idColumn: ID.cId },
    ];
    expect(findings(ID.rel, { ends: twice }).map((f) => f.pointer)).toEqual(["/ends/1/end"]);
    expect(findings(ID.rel, { junctionTable: ID.table, ends: [{ end: ID.e1, foreignKeys: [ID.fk1] }] }).map((f) => f.pointer)).toEqual(["/ends/0/foreignKeys"]);
  });

  it("gives the engine's words for keys that are still refused, and refuses a shape that disagrees", () => {
    expect(findings(ID.rel, { shape: "junction" })).toEqual([
      {
        pointer: "/shape",
        message:
          "Relation 'AttachedTo' has an end with several types, so a mapping of it doesn't take 'shape'; its 'ends' entries say how it is stored. Remove it.",
      },
    ]);
    expect(findings(ID.rel, { foreignKeyEnd: ID.e1, ends: [{ end: ID.e2, idColumn: ID.cId }] }).map((f) => f.pointer)).toEqual(["/foreignKeyEnd"]);
    expect(findings(ID.rel, { shape: "junction", ends: [{ end: ID.e2, idColumn: ID.cId }] }).map((f) => f.pointer)).toEqual(["/shape"]);
  });

  it("checks constants against the link table", () => {
    const ends = [{ end: ID.e2, typeColumn: ID.cType, idColumn: ID.cId }];
    expect(findings(ID.rel, { ends, constants: [{ column: ID.cCode, value: "A" }] }).map((f) => f.pointer)).toEqual(["/constants/0"]);
    expect(findings(ID.rel, { junctionTable: ID.table, ends, constants: [{ column: ID.cCode, value: "A" }] })).toEqual([]);
    expect(findings(ID.rel, { junctionTable: ID.table, ends, constants: [{ column: ID.cId, value: "A" }] }).map((f) => f.pointer)).toEqual([
      "/constants/0/column",
    ]);
    expect(findings(ID.rel, { junctionTable: ID.table, ends, constants: [{ column: ID.part, value: "A" }] }).map((f) => f.pointer)).toEqual([
      "/constants/0/column",
    ]);
  });
});

describe("MQ4009 on a mapping of a one-type relation", () => {
  it("keeps the words of a missing key on a designed junction table", () => {
    expect(findings(ID.one, { junctionTable: ID.table, ends: [{ end: ID.o1 }] })).toEqual([
      {
        pointer: "/ends/0",
        message:
          "End entry 0 names no 'foreignKey'; junction table 'comment_links' is designed, so the entry needs the foreign key that references its end's table.",
      },
    ]);
  });

  it("refuses the keys of an end with several types, and takes constants against a designed link table", () => {
    const refused = findings(ID.one, { junctionTable: ID.table, ends: [{ end: ID.o1, foreignKey: ID.fk1, typeColumn: ID.cType }] });
    expect(refused.map((f) => f.message)).toEqual(["End entry 0 of relation 'Owns' sets 'typeColumn', which needs a relation with an end of several types."]);
    expect(findings(ID.one, { junctionTable: ID.table, ends: [{ end: ID.o1, foreignKey: ID.fk1 }], constants: [{ column: ID.cCode, value: "O" }] })).toEqual(
      [],
    );
  });
});

describe("MQ4009 on a one-type mapping, as the engine reads it now", () => {
  it("lets an empty foreignKeys list pass, as the engine counts items", () => {
    expect(findings(ID.one, { junctionTable: ID.table, ends: [{ end: ID.o1, foreignKey: ID.fk1, foreignKeys: [] }] })).toEqual([]);
  });

  it("checks the entries of an ignored mapping, naming the missing key only when its junction is designed", () => {
    expect(findings(ID.one, { ignore: true, junctionTable: ID.table, ends: [{ end: ID.o1 }] }).map((f) => f.pointer)).toEqual(["/ends/0"]);
    expect(findings(ID.one, { ignore: true, ends: [{ end: ID.o1 }] })).toEqual([]);
    expect(findings(ID.one, { ignore: true, ends: [{ end: ID.o1, idColumn: ID.cId }] }).map((f) => f.pointer)).toEqual(["/ends/0/idColumn"]);
  });

  it("gives one finding for constants with no storage on a relation with several types", () => {
    expect(findings(ID.rel, { constants: [{ column: ID.cCode, value: "A" }] }).map((f) => f.pointer)).toEqual(["/constants"]);
  });

  it("names two types items for one entity", () => {
    const types = [
      { entity: ID.part, foreignKey: ID.fk1 },
      { entity: ID.part, foreignKey: ID.fk2 },
    ];
    expect(findings(ID.rel, { ends: [{ end: ID.e2, types }] })).toEqual([
      { pointer: "/ends/0/types/1/entity", message: "End entry 0 of relation 'AttachedTo' lists entity 'Part' in two types items; keep one." },
    ]);
  });
});

describe("MQ4115 and a mapping that stores the relation", () => {
  const info = () => ctx.backend.model.validate().diagnostics.filter((d) => d.rule === "MQ4115" && d.elementId === ID.e2).length;
  it("goes quiet once an entry or a link table is named", () => {
    findings(ID.rel, { shape: "junction" });
    findings(ID.rel, { ends: [{ end: ID.e2, idColumn: ID.cId }] });
    expect(info()).toBe(0);
    findings(ID.rel, { junctionTable: ID.table });
    expect(info()).toBe(0);
  });
});

describe("the delete rule of an ends entry", () => {
  const withEntry = () =>
    findings(ID.rel, {
      junctionTable: ID.table,
      ends: [{ end: ID.e2, typeColumn: ID.cType, idColumn: ID.cId }],
      constants: [{ column: ID.cCode, value: "A" }],
    });

  it("removes the entry with delete-dependents and refuses with remove-references", () => {
    withEntry();
    const clear = ctx.backend.model.deletePlan([ID.table], "remove-references");
    expect(clear.outcome).toBe("invalid");
    expect(clear.refused.map((r) => r.id)).toContain(ID.mapping);
    const plan = ctx.backend.model.deletePlan([ID.table], "delete-dependents");
    expect(plan.outcome).toBe("saved");
    expect(
      plan.removes
        .filter((r) => r.id === ID.mapping)
        .map((r) => r.pointer)
        .sort(),
    ).toEqual(["/constants/0", "/ends/0"]);
    expect(plan.clears.map((c) => c.pointer)).toContain("/junctionTable");
  });

  it("lists the references of a column, a key and the table", () => {
    withEntry();
    const pointers = (id: string) => ctx.backend.model.references(id)!.map((r) => r.jsonPointer);
    expect(pointers(ID.cCode)).toContain("/constants/0/column");
    expect(pointers(ID.cType)).toContain("/ends/0/typeColumn");
    expect(pointers(ID.table)).toContain("/junctionTable");
  });

  it("removes the types item that names a deleted entity under either choice", () => {
    findings(ID.rel, {
      ends: [
        {
          end: ID.e2,
          types: [
            { entity: ID.part, foreignKey: ID.fk1 },
            { entity: ID.assembly, foreignKey: ID.fk2 },
          ],
        },
      ],
    });
    for (const resolution of ["remove-references", "delete-dependents"] as const) {
      const plan = ctx.backend.model.deletePlan([ID.assembly], resolution);
      expect(plan.clears.map((c) => c.pointer)).toContain("/ends/0/types/1/entity");
    }
  });
});
