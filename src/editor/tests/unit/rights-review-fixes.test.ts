// The fixes of the first rights review: the mock must answer as the engine does on link verbs, the grid's owner row, demoting a
// lifecycle, remove-verb and rename-verb, the delete cascade and gate signers held through holdings.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { simulate } from "@/mocks/model/stepper";
import { rightsGrid } from "@/mocks/model/rightsCheck";
import { Membership } from "@/mocks/model/membership";
import { verbOperation } from "@/mocks/model/verbs";
import { planDelete } from "@/mocks/model/cascade";
import { demoteLifecycle } from "@/editors/process/api";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const st = (id: string): Json => ({ id, name: id });
const life = (id: string, extra: Json = {}): Json => ({
  kind: "process",
  id,
  name: id,
  use: "lifecycle",
  subjects: [{ id: `${id}-s`, entity: "E" }],
  events: [{ id: "promote", name: "promote", verb: "promote" }],
  states: [st("A"), st("B")],
  transitions: [{ id: "t1", source: "A", event: "promote", targets: ["B"] }],
  ...extra,
});
const right = (id: string, access: string, verbs: string[], extra: Json = {}): Json => ({ id, name: id, access, verbs, ...extra });
const actors = [
  { id: "X", json: { name: "X", type: "person" } },
  { id: "Y", json: { name: "Y", type: "person" } },
];
const last = (doc: Json, steps: Json[], options: Json = {}) => {
  const result = simulate(doc, null, steps, { lifecycles: [doc], subjectEntity: "E", membership: new Membership(actors), ...options } as never);
  return result.trace[result.trace.length - 1];
};

describe("link verbs (review 5)", () => {
  it("a right with no ends speaks of the object only, so a verb step on a link end is not refused by it", () => {
    const doc = life("L", { rights: [right("r1", "deny", ["modify"])] });
    expect(last(doc, [{ input: "verb", verb: "modify", actor: "X" }]).accepted).toBe(false);
    expect(last(doc, [{ input: "verb", verb: "modify", actor: "X", end: "e1" }]).accepted).toBe(true);
  });

  it("a link verb with no end known: a deny with ends holds, an allow with ends does not grant", () => {
    const deny = life("L", { rights: [right("r1", "deny", ["connect"], { ends: ["e1"] })] });
    expect(last(deny, [{ input: "verb", verb: "connect", actor: "X" }]).refusedBy).toBe("r1");
    expect(last(deny, [{ input: "verb", verb: "connect", actor: "X", end: "e2" }]).accepted).toBe(true);
    const allow = life("L", { rightsDefault: "deny", rights: [right("a1", "allow", ["connect"], { ends: ["e1"] })] });
    expect(last(allow, [{ input: "verb", verb: "connect", actor: "X" }]).refusal).toBe("default");
    expect(last(allow, [{ input: "verb", verb: "connect", actor: "X", end: "e1" }]).accepted).toBe(true);
  });
});

describe("the grid row of an actor from the object (review 6)", () => {
  it("says allowed on the owner's own row", () => {
    const doc = life("L", { rightsDefault: "deny", rights: [right("a1", "allow", ["modify"], { actors: ["Owner"] })] });
    const world = {
      lifecycles: [doc],
      named: new Map<string, Json>(),
      membership: new Membership([...actors, { id: "Owner", json: { name: "Owner", type: "role", relative: { owner: true } } }]),
      ownerActors: new Set(["Owner"]),
      entitiesOf: () => ["E"],
    };
    const rows = rightsGrid(doc, world);
    const cell = (actor: string | null) => rows.find((r) => r.state === "A" && r.actor === actor && r.verb === "modify")!;
    expect(cell("Owner").answer).toBe("allowed");
    expect(cell("X")).toBeUndefined();
    expect(cell(null).answer).toBe("denied");
  });
});

describe("gate signers through holdings (review 12)", () => {
  it("count as an allow for the acting actor only", () => {
    const gated = life("L", {
      rightsDefault: "deny",
      transitions: [{ id: "t1", source: "A", event: "promote", targets: ["B"], gate: { id: "g1", name: "g1", signers: ["X"], requiredCount: 1 } }],
    });
    const participants = [{ id: "p1", name: "p1", actor: "Y", holds: [{ id: "h1", actor: "X" }] }];
    const refused = last(gated, [{ event: "promote", actor: "Y", participant: "p1", signer: "p1" }], { participants });
    expect([refused.accepted, refused.refusal]).toEqual([false, "default"]);
    expect(last(gated, [{ event: "promote", actor: "X", signer: "x" }]).accepted).toBe(true);
  });
});

describe("remove-verb and rename-verb (review 8)", () => {
  const docs = () =>
    new Map<string, Json>([
      ["V", { kind: "verb-vocabulary", id: "V", verbs: [{ id: "ov", key: "escalate" }] }],
      ["R", { kind: "right", id: "R", name: "R", access: "deny", verbs: ["escalate"] }],
      ["R2", { kind: "right", id: "R2", name: "R2", access: "deny", verbs: ["escalate", "read"] }],
      ["L", life("L", { rights: [{ id: "u1", right: "R" }, { id: "u2", right: "R2" }, right("i1", "deny", ["escalate"])] })],
      [
        "S",
        {
          kind: "scenario",
          id: "S",
          steps: [
            { id: "s1", input: "verb", verb: "escalate" },
            { id: "s2", input: "verb", verb: "read" },
          ],
        },
      ],
    ]);

  it("removing the last verb of a named right deletes the right and its uses, and removes verb steps", () => {
    const out = verbOperation(docs(), { op: "remove-verb", verb: "escalate" });
    if ("error" in out) throw new Error(out.error);
    expect(out.deleted).toEqual(["R"]);
    expect(out.changed.get("R2")!.verbs as string[]).toEqual(["read"]);
    expect((out.changed.get("L")!.rights as Json[]).map((r) => r.id)).toEqual(["u2"]);
    expect((out.changed.get("S")!.steps as Json[]).map((s) => s.id)).toEqual(["s2"]);
  });

  it("renaming rewrites verb steps", () => {
    const out = verbOperation(docs(), { op: "rename-verb", verb: "escalate", to: "raise" });
    if ("error" in out) throw new Error(out.error);
    expect((out.changed.get("S")!.steps as Json[]).map((s) => s.verb)).toEqual(["raise", "read"]);
    expect(out.deleted).toEqual([]);
  });

  it("the batch deletes the right through the model", () => {
    const backend = new MockBackend();
    const vocabulary = {
      kind: "verb-vocabulary",
      id: "01JRVW0000000000000000V001",
      name: "verbs",
      verbs: [{ id: "01JRVW0000000000000000V002", key: "escalate" }],
    };
    expect(backend.model.create(vocabulary).outcome).toBe("saved");
    expect(backend.model.create({ kind: "right", id: "01JRVW0000000000000000R001", name: "Esc", access: "deny", verbs: ["escalate"] }).outcome).toBe("saved");
    expect(backend.model.batch({ operations: [{ op: "remove-verb", verb: "escalate" }] }).body).toMatchObject({ outcome: "saved" });
    expect(backend.model.get("01JRVW0000000000000000R001")).toBeNull();
  });
});

describe("the delete cascade (review 9)", () => {
  const set = () =>
    new Map(
      Object.entries({
        actA: { kind: "actor", id: "actA", name: "A", type: "person" },
        actB: { kind: "actor", id: "actB", name: "B", type: "person" },
        R: { kind: "right", id: "R", name: "R", access: "deny", verbs: ["modify"], actors: ["actA"] },
        L: life("L", { rights: [{ id: "u1", right: "R" }, right("i1", "deny", ["delete"], { condition: { path: ["actB"] } })] }),
      }).map(([k, json]) => [k, { id: k, path: `${k}.json`, json: json as Json }] as const),
    );

  it("deleting the last actor of a named right, with remove-references, deletes the right and drops its uses", () => {
    const { plan, deleted, edited } = planDelete(set(), ["actA"], "remove-references");
    expect(plan.refused).toEqual([]);
    expect(deleted).toContain("R");
    expect((edited.get("L")!.rights as Json[]).map((r) => r.id)).toEqual(["i1"]);
  });

  it("deleting an id an inline right's condition names is refused with remove-references, and drops the right with delete-dependents", () => {
    const refused = planDelete(set(), ["actB"], "remove-references");
    expect(refused.plan.refused.length).toBeGreaterThan(0);
    const dropped = planDelete(set(), ["actB"], "delete-dependents");
    expect(dropped.plan.refused).toEqual([]);
    expect((dropped.edited.get("L")!.rights as Json[]).map((r) => r.id)).toEqual(["u1"]);
  });
});

describe("demoting a lifecycle that holds rights (review 7)", () => {
  const api = useMockApi({ scenarios: ["lifecycle"] });

  it("keeps rights and rightsDefault, and MQ9901 names them", async () => {
    const { queryClient, store, drafts } = api.services;
    const one = api.backend.model.validate().diagnostics.find((d) => d.rule === "MQ9209")!.elementId!;
    const doc = structuredClone(api.backend.model.get(one)!.json) as Json;
    doc.rightsDefault = "deny";
    expect(api.backend.model.save(one, doc, api.backend.model.get(one)!.hash).outcome).toBe("saved");
    const refused = await demoteLifecycle(queryClient, store, drafts, { process: one, label: "Make it an orchestration" });
    expect(refused).toMatch(/not a lifecycle|rightsDefault/);
    expect((api.backend.model.get(one)!.json as Json).rightsDefault).toBe("deny");
    expect((api.backend.model.get(one)!.json as Json).use).toBe("lifecycle");
  });
});
