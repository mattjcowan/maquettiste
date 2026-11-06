// The mock backend's actor types and membership (actors-design.md sections 3 to 6 and 9), as the engine does them: the
// index row, MQ9601, MQ9602 and MQ9605, the cascade on delete, and membership in the stepper (enabled actors, who may act,
// gate satisfaction and the actor of a trace entry).
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { Membership } from "@/mocks/model/membership";
import { simulate } from "@/mocks/model/stepper";

type Json = Record<string, unknown>;

// ULIDs, so the schema's id pattern accepts them.
const ID = {
  ann: "01J92P0V0JR8BE8253SKT29A01",
  bob: "01J92P0V0JR8BE8253SKT29A02",
  reviewer: "01J92P0V0JR8BE8253SKT29A03",
  night: "01J92P0V0JR8BE8253SKT29A04",
  plant: "01J92P0V0JR8BE8253SKT29A05",
  auto: "01J92P0V0JR8BE8253SKT29A06",
  auto2: "01J92P0V0JR8BE8253SKT29A07",
};

const actor = (id: string, name: string, type: string, memberOf?: string[]): Json => ({ kind: "actor", id, name, type, ...(memberOf ? { memberOf } : {}) });

/** The actors in the model. Created plain first (the mock refuses a file with errors), then given their memberships and types as an outside edit would. */
function backendWith(...actors: Json[]) {
  const backend = new MockBackend();
  for (const a of actors)
    expect(backend.model.create({ ...a, memberOf: undefined, type: a.type === "system" ? "external-system" : a.type }).outcome).toBe("saved");
  for (const a of actors) backend.model.externalEdit(String(a.id), (json) => void Object.assign(json, a));
  return backend;
}

const rules = (backend: MockBackend, rule: string) =>
  backend.model
    .validate()
    .diagnostics.filter((d) => d.rule === rule)
    .map((d) => `${d.elementId} ${d.jsonPointer}`)
    .sort();

describe("the mock's index row for actors", () => {
  it("takes the six types and lists memberOf only when it is not empty, in file order", () => {
    const backend = backendWith(
      actor(ID.plant, "Plant3", "organization"),
      actor(ID.night, "NightShift", "group", [ID.plant]),
      actor(ID.reviewer, "Reviewer", "role", []),
      actor(ID.ann, "Ann", "person", [ID.night, ID.reviewer]),
      actor(ID.auto, "Scheduler", "system"),
    );
    const rows = new Map(backend.model.index().map((r) => [r.id, r]));
    expect(rows.get(ID.plant)).toMatchObject({ actorType: "organization" });
    expect(rows.get(ID.plant)).not.toHaveProperty("memberOf");
    expect(rows.get(ID.reviewer)).not.toHaveProperty("memberOf");
    expect(rows.get(ID.night)).toMatchObject({ actorType: "group", memberOf: [ID.plant] });
    expect(rows.get(ID.ann)).toMatchObject({ actorType: "person", memberOf: [ID.night, ID.reviewer] });
    expect(rows.get(ID.auto)).toMatchObject({ actorType: "system" });
  });
});

describe("the mock's membership rules", () => {
  it("reports nothing for sound memberships and one system actor", () => {
    const backend = backendWith(actor(ID.reviewer, "Reviewer", "role"), actor(ID.ann, "Ann", "person", [ID.reviewer]), actor(ID.auto, "Scheduler", "system"));
    for (const rule of ["MQ9601", "MQ9602", "MQ9605"]) expect(rules(backend, rule)).toEqual([]);
  });

  it("MQ9601: an entry that names a person, the actor itself, or the same holder twice", () => {
    const backend = backendWith(
      actor(ID.bob, "Bob", "person"),
      actor(ID.reviewer, "Reviewer", "role"),
      actor(ID.ann, "Ann", "person", [ID.bob, ID.ann, ID.reviewer, ID.reviewer]),
    );
    const found = backend.model.validate().diagnostics.filter((d) => d.rule === "MQ9601");
    expect(found.map((d) => d.jsonPointer)).toEqual(["/memberOf/0", "/memberOf/1", "/memberOf/3"]);
    expect(found[0].message).toContain("which is a person");
    expect(found[1].message).toContain("names itself");
    expect(found[2].message).toContain("twice");
    expect(found.every((d) => d.severity === "error" && d.elementId === ID.ann)).toBe(true);
  });

  it("MQ9602: a cycle, reported once, naming the way round", () => {
    const backend = backendWith(actor(ID.reviewer, "Reviewer", "role", [ID.night]), actor(ID.night, "NightShift", "group", [ID.reviewer]));
    const found = backend.model.validate().diagnostics.filter((d) => d.rule === "MQ9602");
    expect(found.map((d) => d.elementId).sort()).toEqual([ID.night, ID.reviewer].sort());
    expect(found.find((d) => d.elementId === ID.reviewer)!.message).toContain("Reviewer -> NightShift -> Reviewer");
  });

  it("MQ9605: a second actor of type system, on each of them, naming the other", () => {
    const backend = backendWith(actor(ID.auto, "Scheduler", "system"), actor(ID.auto2, "Robot", "system"));
    const found = backend.model.validate().diagnostics.filter((d) => d.rule === "MQ9605");
    expect(found.map((d) => d.jsonPointer)).toEqual(["/type", "/type"]);
    expect(found.find((d) => d.elementId === ID.auto)!.message).toContain("'Robot'");
  });
});

describe("the mock's cascade on delete", () => {
  it("removes a deleted holder from its members' memberOf under remove-references, keeping the members", () => {
    const backend = backendWith(
      actor(ID.reviewer, "Reviewer", "role"),
      actor(ID.night, "NightShift", "group"),
      actor(ID.ann, "Ann", "person", [ID.night, ID.reviewer]),
      actor(ID.bob, "Bob", "person", [ID.reviewer]),
    );
    const model = backend.model;
    const plan = model.deletePlan([ID.reviewer], "remove-references");
    expect(plan.outcome).toBe("saved");
    expect(plan.refused).toEqual([]);
    expect(plan.clears.length + plan.removes.length).toBe(2);
    const done = model.delete(ID.reviewer, model.get(ID.reviewer)!.hash, "remove-references");
    expect(done.outcome).toBe("saved");
    expect(model.get(ID.reviewer)).toBeNull();
    expect((model.get(ID.ann)!.json as Json).memberOf).toEqual([ID.night]);
    expect(model.get(ID.bob)!.json).not.toHaveProperty("memberOf");
    expect(model.index().find((r) => r.id === ID.bob)).not.toHaveProperty("memberOf");
  });
});

describe("the mock's membership in the stepper", () => {
  const st = (id: string, extra: Json = {}): Json => ({ id, name: id, ...extra });
  const members = new Membership([
    { id: "Plant", json: { name: "Plant", type: "organization" } },
    { id: "Night", json: { name: "Night", type: "group", memberOf: ["Plant"] } },
    { id: "Ann", json: { name: "Ann", type: "person", memberOf: ["Night"] } },
    { id: "Zed", json: { name: "Zed", type: "person", memberOf: ["Plant"] } },
    { id: "Auto", json: { name: "Auto", type: "system" } },
  ]);
  const doc: Json = {
    kind: "process",
    id: "P",
    name: "P",
    events: [{ id: "go", name: "go", actors: ["Plant"] }],
    states: [st("A"), st("B"), st("C"), st("D", { type: "final" })],
    transitions: [
      { id: "t1", source: "A", event: "go", targets: ["B"] },
      { id: "t2", source: "B", event: "go", targets: ["C"], gate: { id: "g", name: "g", signers: ["Plant"], requiredActors: ["Night"] } },
      { id: "t3", source: "C", trigger: "after", after: "PT1H", targets: ["D"] },
    ],
  };

  it("reads the closures, depth and the system actor", () => {
    expect([...members.allMembers("Plant")].sort()).toEqual(["Ann", "Night", "Zed"]);
    expect([...members.allMemberOf("Ann")].sort()).toEqual(["Night", "Plant"]);
    expect(members.systemActor).toBe("Auto");
    expect(
      new Membership([...[{ id: "S2", json: { name: "S2", type: "system" } }], { id: "Auto", json: { name: "Auto", type: "system" } }]).systemActor,
    ).toBeNull();
    expect(members.accepts("Plant", "Ann")).toBe(true);
    expect(members.accepts("Night", "Zed")).toBe(false);
  });

  it("accepts a member of a listed actor for an event, and refuses anyone else", () => {
    const result = simulate(
      doc,
      null,
      [
        { event: "go", actor: "Zed" },
        { event: "go", actor: "Auto" },
      ],
      { membership: members },
    );
    expect(result.trace.slice(1).map((t) => [t.accepted, t.refusal])).toEqual([
      [true, null],
      [false, "actor"],
    ]);
  });

  it("offers each listed actor, then its members at any depth by name, in the Act-as list", () => {
    const result = simulate(doc, null, [], { membership: members });
    expect(result.enabled[0].actors).toEqual(["Plant", "Ann", "Night", "Zed"]);
    const without = simulate(doc, null, []);
    expect(without.enabled[0].actors).toEqual(["Plant"]);
  });

  it("counts a signature by a member of a required actor as that actor's signature", () => {
    const steps = [
      { event: "go", actor: "Zed" },
      { event: "go", actor: "Ann", signer: "ann" },
    ];
    const result = simulate(doc, null, steps, { membership: members });
    // Ann is a member of the required Night, so the one signature the gate needs completes it.
    expect(result.trace[2].configuration).toEqual(["C"]);
    const wrong = simulate(
      doc,
      null,
      [
        { event: "go", actor: "Zed" },
        { event: "go", actor: "Zed", signer: "zed" },
      ],
      { membership: members },
    );
    expect(wrong.trace[2].configuration).toEqual(["B"]);
    expect(wrong.gates[0]).toMatchObject({ satisfied: false });
    // Without memberships Ann is not Plant, so the event refuses her.
    expect(simulate(doc, null, steps).trace[2].refusal).toBe("actor");
  });

  it("names the system actor on the trace entries it moved: the initial entry and time steps, never an input's own actor", () => {
    const steps = [
      { event: "go", actor: "Zed" },
      { event: "go", actor: "Ann", signer: "ann" },
      { input: "time", after: "PT2H" },
    ];
    const result = simulate(doc, null, steps, { membership: members });
    expect(result.trace.map((t) => t.actor)).toEqual(["Auto", undefined, undefined, "Auto"]);
    // An event with no actor has none, and a project without exactly one system actor has none for the others.
    expect(simulate(doc, null, [{ event: "go" }], { membership: members }).trace[1].actor).toBeUndefined();
    expect(simulate(doc, null, [{ input: "time", after: "PT2H" }]).trace.map((t) => t.actor)).toEqual([undefined, undefined]);
  });
});
