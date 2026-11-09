// Relations with several types at an end, in the mock (several-types-design.md part 1): the index row of an end, MQ3041 to MQ3043,
// MQ4115 and MQ4116 with the design's texts, the references of an entry, and the cascade when an entity or stereotype is deleted.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";

type Json = Record<string, unknown>;

const ID = {
  part: "01J92P0V0JR8BE8253SKT29B01",
  purchased: "01J92P0V0JR8BE8253SKT29B02",
  assembly: "01J92P0V0JR8BE8253SKT29B03",
  contract: "01J92P0V0JR8BE8253SKT29B04",
  comment: "01J92P0V0JR8BE8253SKT29B05",
  approvable: "01J92P0V0JR8BE8253SKT29B06",
  rel: "01J92P0V0JR8BE8253SKT29B07",
  e1: "01J92P0V0JR8BE8253SKT29B08",
  e2: "01J92P0V0JR8BE8253SKT29B09",
  e3: "01J92P0V0JR8BE8253SKT29B0A",
  query: "01J92P0V0JR8BE8253SKT29B0B",
};

const entity = (id: string, name: string, extra: Json = {}): Json => ({ kind: "entity", id, name, abstract: true, ...extra });
const types = (...entries: Json[]) => entries;

/** The model with the entities and the relation. The relation is created plain, then edited into the form under test, as an outside edit would. */
function backendWith(ends: [Json, Json], relation: Json = {}, stereotypes: boolean = false) {
  const backend = new MockBackend();
  const make = (json: Json) => expect(backend.model.create(json).outcome).toBe("saved");
  make(entity(ID.part, "Part"));
  make(entity(ID.purchased, "PurchasedPart", { base: ID.part }));
  make(entity(ID.assembly, "Assembly"));
  if (stereotypes) {
    make({ kind: "stereotype", id: ID.approvable, key: "approvable", name: "Approvable", appliesTo: ["entity"] });
    make(entity(ID.contract, "Contract", { stereotypes: ["approvable"] }));
  }
  make(entity(ID.comment, "Comment"));
  make({
    kind: "relation",
    id: ID.rel,
    name: "AttachedTo",
    ends: [
      { id: ID.e1, entity: ID.comment, role: "comment", max: "*" },
      { id: ID.e2, entity: ID.part, role: "attachedTo", max: 1 },
    ],
  });
  backend.model.externalEdit(ID.rel, (json) => void Object.assign(json, relation, { ends: ends.map((e, i) => ({ id: [ID.e1, ID.e2][i], ...e })) }));
  return backend;
}

const near = { entity: ID.comment, role: "comment", max: "*" };
const far = (extra: Json) => ({ role: "attachedTo", max: 1, ...extra });
const found = (backend: MockBackend, rule: string) =>
  backend.model
    .validate()
    .diagnostics.filter((d) => d.rule === rule && [ID.rel, ID.e1, ID.e2, ID.e3].includes(d.elementId ?? ""))
    .map((d) => ({ pointer: d.jsonPointer, severity: d.severity, message: d.message }));

describe("the mock's index row for an end with several types", () => {
  it("keeps a one-type end exactly as it was", () => {
    const backend = backendWith([near, far({ entity: ID.part })]);
    expect(backend.model.index().find((r) => r.id === ID.rel)!.ends).toEqual([
      { entity: ID.comment, role: "comment" },
      { entity: ID.part, role: "attachedTo" },
    ]);
  });

  it("lists entities, stereotype ids and carriers for several types, and anyEntity", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }, { stereotype: "approvable" }, { entity: ID.assembly }) })], {}, true);
    expect(backend.model.index().find((r) => r.id === ID.rel)!.ends![1]).toEqual({
      role: "attachedTo",
      entities: [ID.part, ID.assembly],
      stereotypes: [ID.approvable],
      covers: [ID.contract],
    });
    const any = backendWith([near, far({ anyEntity: true })]);
    expect(any.model.index().find((r) => r.id === ID.rel)!.ends![1]).toEqual({ role: "attachedTo", anyEntity: true });
  });

  it("reads a list of one entity as a one-type end", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }) })]);
    expect(backend.model.index().find((r) => r.id === ID.rel)!.ends![1]).toEqual({ entity: ID.part, role: "attachedTo", entities: [ID.part] });
  });
});

describe("MQ3041", () => {
  it("refuses several types on both ends", () => {
    const backend = backendWith([
      { role: "a", max: "*", types: types({ entity: ID.part }, { entity: ID.assembly }) },
      far({ types: types({ entity: ID.part }, { entity: ID.assembly }) }),
    ]);
    expect(found(backend, "MQ3041")).toEqual([
      { pointer: "/ends", severity: "error", message: "Relation 'AttachedTo' has several types on both ends; keep them on one end." },
    ]);
  });

  it("refuses several types on an n-ary relation", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }, { entity: ID.assembly }) })], { relationKind: "n-ary" });
    expect(found(backend, "MQ3041")).toEqual([
      {
        pointer: "/ends/1",
        severity: "error",
        message: "Relation 'AttachedTo' is n-ary, so its end 'attachedTo' can't have several types; give it one entity.",
      },
    ]);
  });

  it("refuses any entity on a composition's part end, and allows it on the owner end", () => {
    // The owner end has max 1; the other end is the part.
    const bad = backendWith(
      [
        { role: "item", max: "*", anyEntity: true },
        { entity: ID.comment, role: "holder", max: 1 },
      ],
      { relationKind: "composition" },
    );
    expect(found(bad, "MQ3041")).toEqual([
      {
        pointer: "/ends/0/anyEntity",
        severity: "error",
        message: "End 'item' is the part in composition 'AttachedTo', so it can't be any entity; list the types it may be.",
      },
    ]);
    const owner = backendWith([near, far({ anyEntity: true })], { relationKind: "composition" });
    expect(found(owner, "MQ3041")).toEqual([]);
  });

  it("says nothing about a one-type model", () => {
    expect(found(backendWith([near, far({ entity: ID.part })]), "MQ3041")).toEqual([]);
  });
});

describe("MQ3042 and MQ3043", () => {
  it("warns about a list of one entity, at the list", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }) })]);
    expect(found(backend, "MQ3042")).toEqual([
      { pointer: "/ends/1/types", severity: "warning", message: "End 'attachedTo' lists only entity 'Part' in types; write it as entity." },
    ]);
  });

  it("warns about an entity beside its base, at the entry", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }, { entity: ID.purchased }, { entity: ID.assembly }) })]);
    expect(found(backend, "MQ3043")).toEqual([
      {
        pointer: "/ends/1/types/1",
        severity: "warning",
        message: "End 'attachedTo' lists entity 'PurchasedPart', which entity 'Part' already covers, since it derives from it; remove the entry.",
      },
    ]);
  });

  it("warns about an entity beside a stereotype it carries", () => {
    const backend = backendWith([near, far({ types: types({ stereotype: "approvable" }, { entity: ID.contract }) })], {}, true);
    expect(found(backend, "MQ3043")).toEqual([
      {
        pointer: "/ends/1/types/1",
        severity: "warning",
        message: "End 'attachedTo' lists entity 'Contract', which carries stereotype 'approvable', also listed; remove the entry.",
      },
    ]);
  });

  it("is quiet for unrelated entries", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }, { entity: ID.assembly }) })]);
    expect(found(backend, "MQ3042")).toEqual([]);
    expect(found(backend, "MQ3043")).toEqual([]);
  });
});

describe("MQ4115", () => {
  it("is info for a database that places a type of every end, and silenced by a mapping that ignores the relation", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }, { entity: ID.assembly }) })]);
    const database = [...backend.model.entries.values()].find((e) => e.json.kind === "database")!;
    const messages = found(backend, "MQ4115");
    expect(messages).toHaveLength(1);
    expect(messages[0]).toMatchObject({
      severity: "info",
      message: `Relation 'AttachedTo' has an end with several types, so database '${String(database.json.name)}' stores nothing for it.`,
    });
    expect(
      backend.model.create({
        kind: "mapping",
        id: "01J92P0V0JR8BE8253SKT29B0C",
        name: "AttachedTo ignored",
        database: database.id,
        relation: ID.rel,
        ignore: true,
      }).outcome,
    ).toBe("saved");
    expect(found(backend, "MQ4115")).toEqual([]);
  });

  it("never fires for a one-type relation", () => {
    expect(found(backendWith([near, far({ entity: ID.part })]), "MQ4115")).toEqual([]);
  });
});

describe("MQ4116", () => {
  it("refuses a query field and a collection that name an end with several types", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }, { entity: ID.assembly }) })]);
    const database = [...backend.model.entries.values()].find((e) => e.json.kind === "database")!;
    // Created with a field that names no end, then edited as an outside edit would: the query's own binding findings are not under test.
    const created = backend.model.create({
      kind: "query",
      id: ID.query,
      name: "Notes",
      database: database.id,
      entity: ID.comment,
      from: { source: ID.comment },
      select: [{ attribute: ID.comment, expression: { column: "x" } }],
    });
    expect(created.outcome).toBe("saved");
    backend.model.externalEdit(ID.query, (json) => {
      (json.select as Json[])[0].attribute = ID.e2;
      json.collections = [{ attribute: ID.e2, query: { from: { source: ID.comment } } }];
    });
    const messages = backend.model
      .validate()
      .diagnostics.filter((d) => d.rule === "MQ4116" && d.elementId === ID.query)
      .map((d) => [d.jsonPointer, d.message]);
    expect(messages).toEqual([
      ["/select/0/attribute", "Field 0 of query 'Notes' names end 'attachedTo', which leads to several types; a field can't map it."],
      ["/collections/0/attribute", "Collection 0 of query 'Notes' names end 'attachedTo', which leads to several types; a collection can't fill it."],
    ]);
  });

  it("says nothing for an end with one type", () => {
    const backend = backendWith([near, far({ entity: ID.part })]);
    const database = [...backend.model.entries.values()].find((e) => e.json.kind === "database")!;
    backend.model.create({
      kind: "query",
      id: ID.query,
      name: "Notes",
      database: database.id,
      entity: ID.comment,
      from: { source: ID.comment },
      select: [{ attribute: ID.comment, expression: { column: "x" } }],
    });
    backend.model.externalEdit(ID.query, (json) => void ((json.select as Json[])[0].attribute = ID.e2));
    expect(backend.model.validate().diagnostics.filter((d) => d.rule === "MQ4116")).toEqual([]);
  });
});

describe("references and the cascade", () => {
  it("lists an entry's reference at the types list and removes it under either delete choice", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }, { entity: ID.assembly }) })]);
    const refs = backend.model.references(ID.assembly)!;
    expect(refs.map((r) => r.jsonPointer)).toContain("/ends/1/types/1/entity");

    for (const resolution of ["remove-references", "delete-dependents"] as const) {
      const plan = backend.model.deletePlan([ID.assembly], resolution);
      expect(plan.outcome).toBe("saved");
      expect(plan.deletes).toEqual([]);
      expect(plan.clears.map((c) => c.pointer)).toEqual(["/ends/1/types/1/entity"]);
    }
    const refused = backend.model.deletePlan([ID.assembly], "refuse");
    expect(refused.outcome).toBe("referenced");
  });

  it("deletes the relation with delete-dependents, and refuses with remove-references, when an end would have no type left", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }, { entity: ID.assembly }) })]);
    const both = [ID.part, ID.assembly];
    const clear = backend.model.deletePlan(both, "remove-references");
    expect(clear.outcome).toBe("invalid");
    expect(clear.refused.map((r) => r.id)).toEqual([ID.rel]);
    const plan = backend.model.deletePlan(both, "delete-dependents");
    expect(plan.outcome).toBe("saved");
    expect(plan.deletes.map((d) => d.id)).toContain(ID.rel);
  });

  it("removes a stereotype entry when its stereotype is deleted", () => {
    const backend = backendWith([near, far({ types: types({ entity: ID.part }, { stereotype: "approvable" }) })], {}, true);
    const plan = backend.model.deletePlan([ID.approvable], "remove-references");
    expect(plan.clears.map((c) => c.pointer)).toEqual(["/ends/1/types/1/stereotype"]);
  });
});
