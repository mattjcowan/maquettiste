// The mock backend's actors taken from the object (actors-design.md decisions 8 to 12), as the engine does them: the index row,
// MQ9603, MQ9604, MQ9606, the MQ9601 case, references and the cascade on delete.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { Membership } from "@/mocks/model/membership";
import { objectFields, pathProblem, relativeMeaning } from "@/model/objectFields";

type Json = Record<string, unknown>;

const ID = {
  order: "01J92P0V0JR8BE8253SKT29B01",
  project: "01J92P0V0JR8BE8253SKT29B02",
  manager: "01J92P0V0JR8BE8253SKT29B03",
  notes: "01J92P0V0JR8BE8253SKT29B04",
  lead: "01J92P0V0JR8BE8253SKT29B05",
  projectEnd: "01J92P0V0JR8BE8253SKT29B06",
  orderEnd: "01J92P0V0JR8BE8253SKT29B07",
  relation: "01J92P0V0JR8BE8253SKT29B08",
  owner: "01J92P0V0JR8BE8253SKT29B09",
  reviewer: "01J92P0V0JR8BE8253SKT29B0A",
  projectReviewer: "01J92P0V0JR8BE8253SKT29B0B",
  plain: "01J92P0V0JR8BE8253SKT29B0C",
  process: "01J92P0V0JR8BE8253SKT29B0D",
  entry: "01J92P0V0JR8BE8253SKT29B0E",
  event: "01J92P0V0JR8BE8253SKT29B0F",
  stateA: "01J92P0V0JR8BE8253SKT29B0G",
  stateB: "01J92P0V0JR8BE8253SKT29B0H",
  transition: "01J92P0V0JR8BE8253SKT29B0J",
  tag: "01J92P0V0JR8BE8253SKT29B0K",
  base: "01J92P0V0JR8BE8253SKT29B0M",
  orderKey: "01J92P0V0JR8BE8253SKT29B0N",
  projectKey: "01J92P0V0JR8BE8253SKT29B0P",
};

const entities: Json[] = [
  {
    kind: "entity",
    id: ID.order,
    name: "Order",
    key: { attributes: [ID.orderKey] },
    attributes: [
      { id: ID.orderKey, name: "id", type: "int64" },
      { id: ID.manager, name: "accountManager", type: "string" },
      { id: ID.notes, name: "notes", type: "string", collection: true },
    ],
  },
  {
    kind: "entity",
    id: ID.project,
    name: "Project",
    key: { attributes: [ID.projectKey] },
    attributes: [
      { id: ID.projectKey, name: "id", type: "int64" },
      { id: ID.lead, name: "lead", type: "string" },
    ],
  },
  {
    kind: "relation",
    id: ID.relation,
    name: "OrderProject",
    relationKind: "association",
    ends: [
      { id: ID.orderEnd, entity: ID.order, role: "order", min: 0, max: "*" },
      { id: ID.projectEnd, entity: ID.project, role: "project", min: 0, max: 1 },
    ],
  },
];

const actor = (id: string, name: string, type: string, extra: Json = {}): Json => ({ kind: "actor", id, name, type, ...extra });

const process = (extra: Json = {}): Json => ({
  kind: "process",
  id: ID.process,
  name: "Review",
  use: "orchestration",
  subjects: [{ id: ID.entry, entity: ID.order }],
  events: [{ id: ID.event, name: "go", actors: [ID.owner] }],
  states: [
    { id: ID.stateA, name: "A" },
    { id: ID.stateB, name: "B" },
  ],
  transitions: [{ id: ID.transition, source: ID.stateA, event: ID.event, targets: [ID.stateB] }],
  ...extra,
});

/** A model with the order, project and relation, then the given documents; each created plain and edited as an outside edit would. */
function backendWith(...docs: Json[]) {
  const backend = new MockBackend();
  for (const e of entities) {
    const made = backend.model.create(e);
    if (made.outcome !== "saved") throw new Error(JSON.stringify(made.diagnostics));
  }
  for (const d of docs) {
    const plain =
      d.kind === "actor"
        ? { ...d, relative: undefined, memberOf: undefined }
        : process({ events: undefined, transitions: undefined, subjects: undefined, use: "library" });
    expect(backend.model.create(d.kind === "actor" ? plain : { ...plain, id: d.id, name: d.name }).outcome).toBe("saved");
  }
  for (const d of docs) backend.model.externalEdit(String(d.id), (json) => void Object.assign(json, d));
  return backend;
}

const found = (backend: MockBackend, rule: string) => backend.model.validate().diagnostics.filter((d) => d.rule === rule);

describe("the index row of an actor taken from the object", () => {
  it("gives owner, path or members-of, none for no form, and nothing for a plain actor", () => {
    const backend = backendWith(
      actor(ID.owner, "Owner", "person", { relative: { owner: true } }),
      actor(ID.reviewer, "Reviewer", "role"),
      actor(ID.projectReviewer, "ProjectReviewer", "role", { relative: { membersOf: ID.reviewer, within: [ID.projectEnd] } }),
      actor(ID.plain, "Lead", "person", { relative: { path: [ID.manager] } }),
      actor(ID.tag, "Empty", "person", { relative: {} }),
    );
    const rows = new Map(backend.model.index().map((r) => [r.id, r]));
    expect(rows.get(ID.owner)).toMatchObject({ relative: "owner" });
    expect(rows.get(ID.projectReviewer)).toMatchObject({ relative: "members-of" });
    expect(rows.get(ID.plain)).toMatchObject({ relative: "path" });
    expect(rows.get(ID.tag)).toMatchObject({ relative: "none" });
    expect(rows.get(ID.reviewer)).not.toHaveProperty("relative");
  });
});

describe("MQ9603: a malformed relative", () => {
  it("reports no form, two forms and within without membersOf, at the relative", () => {
    const backend = backendWith(
      actor(ID.owner, "Owner", "person", { relative: {} }),
      actor(ID.plain, "Both", "person", { relative: { owner: true, path: [ID.manager] } }),
      actor(ID.tag, "Within", "person", { relative: { path: [ID.manager], within: [ID.projectEnd] } }),
    );
    const list = found(backend, "MQ9603");
    expect(list.find((d) => d.elementId === ID.owner)).toMatchObject({ jsonPointer: "/relative", severity: "error" });
    expect(list.find((d) => d.elementId === ID.plain)!.message).toContain("2 forms");
    expect(list.find((d) => d.elementId === ID.tag)).toMatchObject({ jsonPointer: "/relative/within" });
  });

  it("reports membersOf naming a person or an actor from the object, and memberOf on the actor", () => {
    const backend = backendWith(
      actor(ID.reviewer, "Reviewer", "role"),
      actor(ID.plain, "Person", "person"),
      actor(ID.projectReviewer, "Relative", "role", { relative: { owner: true } }),
      actor(ID.owner, "BadHolder", "role", { relative: { membersOf: ID.plain } }),
      actor(ID.tag, "Chained", "role", { relative: { membersOf: ID.projectReviewer } }),
      actor(ID.base, "Member", "person", { relative: { owner: true }, memberOf: [ID.reviewer] }),
    );
    const list = found(backend, "MQ9603");
    expect(list.filter((d) => d.elementId === ID.owner).map((d) => d.jsonPointer)).toEqual(["/relative/membersOf"]);
    expect(list.filter((d) => d.elementId === ID.tag).map((d) => d.jsonPointer)).toEqual(["/relative/membersOf"]);
    expect(list.filter((d) => d.elementId === ID.base).map((d) => d.jsonPointer)).toEqual(["/memberOf"]);
  });

  it("is silent for the three good forms", () => {
    const backend = backendWith(
      actor(ID.reviewer, "Reviewer", "role"),
      actor(ID.owner, "Owner", "person", { relative: { owner: true } }),
      actor(ID.plain, "Manager", "person", { relative: { path: [ID.manager] } }),
      actor(ID.projectReviewer, "ProjectReviewer", "role", { relative: { membersOf: ID.reviewer, within: [ID.projectEnd] } }),
    );
    expect(found(backend, "MQ9603")).toEqual([]);
  });
});

describe("MQ9601 for an actor from the object", () => {
  it("refuses a memberOf entry that names one, and counts it as no holder", () => {
    const backend = backendWith(
      actor(ID.projectReviewer, "ProjectReviewer", "role", { relative: { owner: true } }),
      actor(ID.plain, "Ann", "person", { memberOf: [ID.projectReviewer] }),
    );
    const list = found(backend, "MQ9601");
    expect(list).toHaveLength(1);
    expect(list[0]).toMatchObject({ elementId: ID.plain, jsonPointer: "/memberOf/0" });
    expect(list[0].message).toContain("taken from the object");
    const members = new Membership([
      { id: ID.projectReviewer, json: { name: "R", type: "role", relative: { owner: true } } },
      { id: ID.plain, json: { name: "Ann", type: "person", memberOf: [ID.projectReviewer] } },
    ]);
    expect(members.any).toBe(false);
  });
});

/** The process document with its uses of actors, written as an outside edit (the mock creates only valid, plain files). */
function withProcess(backend: MockBackend, extra: Json): MockBackend {
  backend.model.externalEdit(ID.process, (json) => void Object.assign(json, process(extra), { use: extra.use ?? "orchestration" }));
  return backend;
}

describe("MQ9604: an actor from the object used where it cannot be worked out", () => {
  it("reports a use in a process with no subjects, at the use", () => {
    const backend = backendWith(actor(ID.owner, "Owner", "person", { relative: { owner: true } }), process());
    withProcess(backend, {});
    backend.model.externalEdit(ID.process, (json) => void delete json.subjects);
    const list = found(backend, "MQ9604");
    expect(list).toHaveLength(1);
    expect(list[0]).toMatchObject({ elementId: ID.process, jsonPointer: "/events/0/actors/0", severity: "error" });
    expect(list[0].message).toContain("no subjects");
  });

  it("reports a many-valued field and a path through an attribute, and not a good one", () => {
    const backend = backendWith(
      actor(ID.owner, "Lead", "person", { relative: { path: [ID.projectEnd, ID.lead] } }),
      actor(ID.plain, "Notes", "person", { relative: { path: [ID.notes] } }),
      actor(ID.tag, "Through", "person", { relative: { path: [ID.manager, ID.lead] } }),
      process(),
    );
    withProcess(backend, { events: [{ id: ID.event, name: "go", actors: [ID.owner, ID.plain, ID.tag] }] });
    const list = found(backend, "MQ9604");
    expect(list.map((d) => d.jsonPointer)).toEqual(["/events/0/actors/1", "/events/0/actors/2"]);
    expect(list[0].message).toContain("holds many values");
    expect(list[1].message).toContain("is an attribute");
  });

  it("works a context out through to-one ends, and reports a step that is no end", () => {
    const backend = backendWith(
      actor(ID.reviewer, "Reviewer", "role"),
      actor(ID.projectReviewer, "ProjectReviewer", "role", { relative: { membersOf: ID.reviewer, within: [ID.projectEnd] } }),
      actor(ID.plain, "Elsewhere", "role", { relative: { membersOf: ID.reviewer, within: [ID.lead] } }),
      process(),
    );
    withProcess(backend, { events: [{ id: ID.event, name: "go", actors: [ID.projectReviewer, ID.plain] }] });
    expect(found(backend, "MQ9604").map((d) => d.jsonPointer)).toEqual(["/events/0/actors/1"]);
  });

  it("checks a gate's signers too, and leaves a library alone", () => {
    const backend = backendWith(actor(ID.plain, "Notes", "person", { relative: { path: [ID.notes] } }), process());
    const gate = { id: ID.base, name: "g", signers: [ID.plain], meanings: [{ id: ID.tag, name: "ok" }] };
    withProcess(backend, {
      events: [{ id: ID.event, name: "go" }],
      transitions: [{ id: ID.transition, source: ID.stateA, trigger: "event", event: ID.event, targets: [ID.stateB], gate }],
    });
    expect(found(backend, "MQ9604").map((d) => d.jsonPointer)).toContain("/transitions/0/gate/signers/0");
    withProcess(backend, { use: "library", subjects: undefined, events: [{ id: ID.event, name: "go", actors: [ID.plain] }], transitions: [] });
    backend.model.externalEdit(ID.process, (json) => void delete json.subjects);
    expect(found(backend, "MQ9604")).toEqual([]);
  });
});

describe("MQ9606: a subject's owner", () => {
  const owned = (owner: string) => withProcess(backendWith(process()), { subjects: [{ id: ID.entry, entity: ID.order, owner }] });

  it("accepts an attribute and a to-one end", () => {
    expect(found(owned(ID.manager), "MQ9606")).toEqual([]);
    expect(found(owned(ID.projectEnd), "MQ9606")).toEqual([]);
  });

  it("reports a many-valued field or an id the entity lacks, at the owner", () => {
    expect(found(owned(ID.notes), "MQ9606")[0]).toMatchObject({ jsonPointer: "/subjects/0/owner", severity: "error" });
    expect(found(owned(ID.lead), "MQ9606")[0].message).toContain("no attribute or end");
  });
});

describe("references and the cascade", () => {
  it("lists the owner, path, membersOf and within ids as references", () => {
    const backend = backendWith(
      actor(ID.reviewer, "Reviewer", "role"),
      actor(ID.projectReviewer, "ProjectReviewer", "role", { relative: { membersOf: ID.reviewer, within: [ID.projectEnd] } }),
      actor(ID.plain, "Manager", "person", { relative: { path: [ID.manager] } }),
      process(),
    );
    withProcess(backend, { subjects: [{ id: ID.entry, entity: ID.order, owner: ID.notes }] });
    const holders = (id: string) => (backend.model.references(id) ?? []).map((r) => r.fromId);
    expect(holders(ID.reviewer)).toContain(ID.projectReviewer);
    expect(holders(ID.projectEnd)).toContain(ID.projectReviewer);
    expect(holders(ID.manager)).toContain(ID.plain);
    expect(holders(ID.notes)).toContain(ID.process);
  });

  it("clears a subject's owner when its end goes, and refuses a path through it unless the actor goes too", () => {
    const backend = backendWith(actor(ID.plain, "Lead", "person", { relative: { path: [ID.projectEnd, ID.lead] } }), process());
    withProcess(backend, { subjects: [{ id: ID.entry, entity: ID.order, owner: ID.projectEnd }] });
    const model = backend.model;
    expect(model.deletePlan([ID.project], "remove-references").refused.map((r) => r.id)).toContain(ID.plain);
    const dependents = model.deletePlan([ID.project], "delete-dependents");
    expect(dependents.outcome).toBe("saved");
    expect(dependents.deletes.map((d) => d.id)).toContain(ID.plain);
    expect(dependents.clears.map((c) => c.pointer)).toContain("/subjects/0/owner");
  });

  it("refuses deleting a role an actor from the object names, and deletes that actor under delete-dependents", () => {
    const backend = backendWith(
      actor(ID.reviewer, "Reviewer", "role"),
      actor(ID.projectReviewer, "ProjectReviewer", "role", { relative: { membersOf: ID.reviewer } }),
    );
    expect(backend.model.deletePlan([ID.reviewer], "remove-references").refused.map((r) => r.id)).toEqual([ID.projectReviewer]);
    expect(backend.model.deletePlan([ID.reviewer], "delete-dependents").deletes.map((d) => d.id)).toEqual([ID.projectReviewer]);
  });
});

describe("the shared field rules", () => {
  const src = {
    doc: (id: string) => entities.find((e) => e.id === id),
    relations: entities.filter((e) => e.kind === "relation"),
    stereotype: () => undefined,
    kindOf: (id: string) => entities.find((e) => e.id === id)?.kind as string | undefined,
  };
  const fieldsOf = (entity: string) => objectFields(entity, src);

  it("lists attributes then ends, named from their roles", () => {
    expect(fieldsOf(ID.order).map((f) => [f.name, f.kind, f.many])).toEqual([
      ["id", "attribute", false],
      ["accountManager", "attribute", false],
      ["notes", "attribute", true],
      ["project", "end", false],
    ]);
    expect(fieldsOf(ID.project).map((f) => [f.name, f.many])).toEqual([
      ["id", false],
      ["lead", false],
      ["order", true],
    ]);
  });

  it("works a path out through ends and says why one fails", () => {
    expect(pathProblem(ID.order, [ID.projectEnd, ID.lead], fieldsOf, "field")).toBeNull();
    expect(pathProblem(ID.order, [ID.projectEnd], fieldsOf, "end")).toBeNull();
    expect(pathProblem(ID.order, [ID.manager], fieldsOf, "end")).toContain("is an attribute");
    expect(pathProblem(ID.order, [ID.notes], fieldsOf, "field")).toContain("holds many");
    expect(pathProblem(ID.project, [ID.orderEnd, ID.manager], fieldsOf, "field")).toContain("leads to many");
    expect(pathProblem(ID.order, [ID.lead], fieldsOf, "field")).toContain("no attribute or end");
  });

  it("says in plain words what a relative means", () => {
    const names = { subject: "Order", field: (id: string) => (id === ID.manager ? "accountManager" : "project"), actor: () => "Reviewer" };
    expect(relativeMeaning({ owner: true }, names)).toBe("the order's owner");
    expect(relativeMeaning({ path: [ID.manager] }, names)).toBe("the order's account manager");
    expect(relativeMeaning({ membersOf: ID.reviewer, within: [ID.projectEnd] }, names)).toBe("the Reviewer members of the order's project");
    expect(relativeMeaning({ membersOf: ID.reviewer }, { ...names, subject: undefined })).toBe("the Reviewer members of the object itself");
  });
});
