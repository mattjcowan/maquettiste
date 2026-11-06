// The mock backend's scenario participants (actors-design.md decisions 14 to 17), as the engine does them: the stepper's
// actsAs rule and the texts of MQ9301 and MQ9305, parts with references and the cascade, and simulate and record carrying them.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { Membership } from "@/mocks/model/membership";
import { PARTICIPANT_TEXTS, simulate, unsatisfiedLists } from "@/mocks/model/stepper";

type Json = Record<string, unknown>;
const st = (id: string, extra: Json = {}): Json => ({ id, name: id, ...extra });

const members = new Membership([
  { id: "Reviewer", json: { name: "Reviewer", type: "role" } },
  { id: "Ops", json: { name: "Ops", type: "group" } },
  { id: "Ann", json: { name: "Ann", type: "person", memberOf: ["Reviewer"] } },
  { id: "Bob", json: { name: "Bob", type: "person" } },
  { id: "Owner", json: { name: "Owner", type: "role", relative: { owner: true } } },
]);

const doc: Json = {
  kind: "process",
  id: "P",
  name: "P",
  events: [
    { id: "submit", name: "submit", actors: ["Bob", "Ann"] },
    { id: "approve", name: "approve" },
    { id: "free", name: "free" },
  ],
  states: [st("A"), st("B"), st("C"), st("D", { type: "final" })],
  transitions: [
    { id: "t1", source: "A", event: "submit", targets: ["B"] },
    { id: "t2", source: "B", event: "approve", targets: ["C"], gate: { id: "g", name: "g", signers: ["Reviewer"] } },
    { id: "t3", source: "C", event: "free", targets: ["D"] },
  ],
};

const people: Json[] = [
  { id: "pAnn", name: "ann", identity: "ann@x", actor: "Ann" },
  { id: "pBob", name: "bob", identity: 7, actor: "Bob" },
  { id: "pHolder", name: "holder", holds: [{ id: "h1", actor: "Reviewer" }] },
  { id: "pWithin", name: "within", holds: [{ id: "h2", actor: "Reviewer", within: "o1" }] },
  { id: "pNone", name: "nobody" },
];
const run = (steps: Json[], extra: Json = {}) => simulate(doc, null, steps, { membership: members, participants: people, ...extra });

describe("the stepper's participants", () => {
  it("acts as the first actor of the event's list the participant satisfies", () => {
    const t = run([{ event: "submit", participant: "pAnn" }]).trace[1];
    // submit lists [Bob, Ann]: Ann is the first one the participant satisfies.
    expect(t).toMatchObject({ accepted: true, actsAs: "Ann", input: { participant: "pAnn" } });
    // The echo is the filled-in step: the actor the engine worked out, and the signer defaulting to the identity.
    expect(t.input).toMatchObject({ actor: "Ann", signer: "ann@x" });
  });

  it("uses a gate's signers over the event's actors, and a signature's signer defaults to the identity", () => {
    const result = run([
      { event: "submit", participant: "pBob" },
      { event: "approve", participant: "pHolder" },
    ]);
    expect(result.trace[2]).toMatchObject({ accepted: true, actsAs: "Reviewer" });
    expect(result.trace[2].audit[0]).toMatchObject({ signer: "holder", actor: "Reviewer" });
    const gated = run([
      { event: "submit", participant: "pAnn" },
      { event: "approve", participant: "pAnn" },
    ]).trace[2];
    expect(gated.audit[0]).toMatchObject({ signer: "ann@x", actor: "Reviewer" });
  });

  it("falls back to the participant's actor when the list is empty, and a holding with a context does not count", () => {
    const free = run([
      { event: "submit", participant: "pBob" },
      { event: "approve", participant: "pHolder" },
      { event: "free", participant: "pBob" },
    ]);
    expect(free.trace[3]).toMatchObject({ accepted: true, actsAs: "Bob" });
    const within = run([
      { event: "submit", participant: "pBob" },
      { event: "approve", participant: "pWithin" },
    ]).trace[2];
    // Reviewer within o1 does not count for a plain Reviewer (decision 13).
    expect(within).toMatchObject({ accepted: false, refusal: "gate-signer" });
    expect(unsatisfiedLists.get(within)).toEqual(["Reviewer"]);
  });

  it("checks a given actor against the participant and an unknown participant (MQ9305)", () => {
    const wrong = run([{ event: "submit", participant: "pBob", actor: "Ann" }]).trace[1];
    expect(wrong.diagnostics[0]).toMatchObject({ rule: "MQ9305", message: PARTICIPANT_TEXTS.actorNotSatisfied("bob", "Ann") });
    // A role held only within a context, named where the step needs it held without one: say so plainly.
    const only = run([{ event: "submit", participant: "pWithin", actor: "Reviewer" }], { objects: [{ id: "o1", name: "request1", entity: "E" }] }).trace[1];
    expect(only.diagnostics[0]).toMatchObject({
      rule: "MQ9305",
      message:
        "participant 'within' holds Reviewer only within request1; this step needs Reviewer held without a context. Hold it without a context too, or name an actor taken from the object for the members of Reviewer within request1.",
    });
    const unknown = run([{ event: "submit", participant: "nope" }]).trace[1];
    expect(unknown.diagnostics[0]).toMatchObject({ rule: "MQ9305", message: PARTICIPANT_TEXTS.unknown("nope") });
    expect(unknown.incomplete).toBe(true);
  });

  it("satisfies an actor from the object that is the owner by the subject object's owner", () => {
    const owned = { ...doc, events: [{ id: "submit", name: "submit", actors: ["Owner"] }] };
    const objects = [{ id: "o1", name: "inv", entity: "E", owner: "pAnn" }];
    const base = { membership: members, participants: people, objects, subject: "o1", ownerActors: new Set(["Owner"]) };
    expect(simulate(owned, null, [{ event: "submit", participant: "pAnn" }], base).trace[1]).toMatchObject({ accepted: true, actsAs: "Owner" });
    expect(simulate(owned, null, [{ event: "submit", participant: "pBob" }], base).trace[1]).toMatchObject({ accepted: false });
  });

  it("leaves steps without a participant as they were", () => {
    const t = run([{ event: "submit", actor: "Bob" }]).trace[1];
    expect(t).not.toHaveProperty("actsAs");
    expect(t.input).not.toHaveProperty("participant");
  });

  it("words the MQ9301 refusal with the participant and the list", () => {
    expect(PARTICIPANT_TEXTS.refusedByActor(2, "within", ["Reviewer"])).toContain("participant 'within' satisfies none of Reviewer");
  });
});

// ULIDs, so the schema's id pattern accepts them.
const ID = {
  process: "01J92P0V0JR8BE8253SKT29B01",
  stateA: "01J92P0V0JR8BE8253SKT29B02",
  event: "01J92P0V0JR8BE8253SKT29B03",
  ann: "01J92P0V0JR8BE8253SKT29B04",
  reviewer: "01J92P0V0JR8BE8253SKT29B05",
  scenario: "01J92P0V0JR8BE8253SKT29B06",
  p1: "01J92P0V0JR8BE8253SKT29B07",
  h1: "01J92P0V0JR8BE8253SKT29B08",
  step: "01J92P0V0JR8BE8253SKT29B09",
};

function backend() {
  const b = new MockBackend();
  const create = (json: Json) => expect(b.model.create(json).outcome).toBe("saved");
  create({ kind: "actor", id: ID.ann, name: "Ann", type: "person" });
  create({ kind: "actor", id: ID.reviewer, name: "Reviewer", type: "role" });
  create({ kind: "process", id: ID.process, name: "Flow", events: [{ id: ID.event, name: "go" }], states: [{ id: ID.stateA, name: "A" }] });
  create({
    kind: "scenario",
    id: ID.scenario,
    name: "Happy",
    process: ID.process,
    participants: [
      { id: ID.p1, name: "ann", actor: ID.ann, holds: [{ id: ID.h1, actor: ID.reviewer }] },
      { name: "second", holds: [{ actor: ID.reviewer }] },
    ],
    steps: [{ id: ID.step, event: ID.event, participant: ID.p1 }],
  });
  return b;
}

describe("participants as parts", () => {
  it("mints the ids of participants and holdings that have none", () => {
    const json = backend().model.get(ID.scenario)!.json as Json;
    const second = (json.participants as Json[])[1];
    expect(typeof second.id).toBe("string");
    expect(typeof ((second.holds as Json[])[0] as Json).id).toBe("string");
  });

  it("lists the actor and each holding's actor as references from the participant and holding ids", () => {
    const b = backend();
    expect(b.model.references(ID.ann)).toContainEqual(
      expect.objectContaining({ fromElementId: ID.scenario, fromId: ID.p1, jsonPointer: "/participants/0/actor" }),
    );
    expect(b.model.references(ID.reviewer)).toContainEqual(expect.objectContaining({ fromId: ID.h1, jsonPointer: "/participants/0/holds/0/actor" }));
    // A participant is a part: it resolves to its scenario and the step's reference to it stays inside the file.
    expect(b.model.references(ID.p1)).toEqual([]);
  });

  it("clears a participant's actor and drops a holding under remove-references", () => {
    const b = backend();
    expect(b.model.delete(ID.ann, b.model.get(ID.ann)!.hash, "remove-references").outcome).toBe("saved");
    expect(((b.model.get(ID.scenario)!.json as Json).participants as Json[])[0]).not.toHaveProperty("actor");
    expect(b.model.delete(ID.reviewer, b.model.get(ID.reviewer)!.hash, "remove-references").outcome).toBe("saved");
    const [first, second] = (b.model.get(ID.scenario)!.json as Json).participants as Json[];
    expect(first.holds).toEqual([]);
    expect(second.holds).toEqual([]);
  });
});
