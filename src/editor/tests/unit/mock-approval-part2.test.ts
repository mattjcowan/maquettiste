// The mock stepper's part 2 of sign-off (signoff-design.md decisions 5, 6, 20, 21, 33 to 35): rounds and skipped stages, delegation and
// answering on behalf, notices on time steps, and expect.notices in replay and recording. A model without part 2 keys steps as before.
import { describe, expect, it } from "vitest";
import { Membership } from "@/mocks/model/membership";
import { replay, simulate, type SimulationJson } from "@/mocks/model/stepper";

type Json = Record<string, unknown>;
const st = (id: string) => ({ id, name: id });

const members = new Membership([
  { id: "FinRole", json: { name: "FinRole", type: "role" } },
  { id: "MgrRole", json: { name: "MgrRole", type: "role" } },
  { id: "Ann", json: { name: "Ann", type: "person", memberOf: ["FinRole"] } },
  { id: "Bob", json: { name: "Bob", type: "person", memberOf: ["MgrRole"] } },
  { id: "Deputy", json: { name: "Deputy", type: "role" } },
]);
const participants = [
  { id: "pann", name: "pann", identity: "ann", actor: "FinRole" },
  { id: "pbob", name: "pbob", identity: "bob", actor: "MgrRole" },
  { id: "pdan", name: "pdan", identity: "dan", actor: "Deputy" },
];

function doc(gate: Json): Json {
  return {
    kind: "process",
    use: "lifecycle",
    id: "P",
    name: "P",
    events: [
      { id: "submit", name: "submit", verb: "promote" },
      { id: "approve", name: "approve", verb: "approve" },
    ],
    states: [st("Draft"), st("Review"), st("Done")],
    transitions: [
      { id: "t1", source: "Draft", event: "submit", targets: ["Review"] },
      { id: "t2", source: "Review", event: "approve", targets: ["Done"], gate },
    ],
  };
}
const sig = (id: string, signers: string[], extra: Json = {}): Json => ({ id, name: id, signers, ...extra });
const stage = (id: string, signatures: Json[], extra: Json = {}): Json => ({ id, name: id, signatures, ...extra });
const gate = (stages: Json[]): Json => ({ id: "g", name: "g", stages });
const run = (d: Json, steps: Json[]) => simulate(d, null, steps, { membership: members, refusedBy: true, participants });
const submit = { event: "submit", actor: "Ann", signer: "ann" };
const answer = (who: string, response?: string, extra: Json = {}) => ({
  event: "approve",
  actor: who,
  signer: who.toLowerCase(),
  ...(response ? { response } : {}),
  ...extra,
});
const outcomes = (r: SimulationJson, step: number) => r.trace[step + 1].audit.map((a) => `${a.outcome}${a.signature ? `:${a.signature}` : ""}`);
const last = (r: SimulationJson) => r.trace[r.trace.length - 1];
const stages = (r: SimulationJson) => r.gates[0].stages!;

describe("the mock stepper: rounds", () => {
  const rounds = doc(gate([stage("s1", [sig("fin", ["FinRole"])], { round: 1 }), stage("s2", [sig("mgr", ["MgrRole"])], { round: 2 })]));

  it("runs rounds one after another and moves only when the last round completes", () => {
    const r = run(rounds, [submit, answer("Ann"), answer("Bob")]);
    // Round 2 is not asked in round 1: Bob has no signature to give yet.
    expect(last(run(rounds, [submit, answer("Bob")]))).toMatchObject({ accepted: false, refusal: "gate-signer" });
    expect(outcomes(r, 1)).toEqual(["signed:fin"]);
    expect(r.trace[2].configuration).toContain("Review");
    expect(outcomes(r, 2)).toEqual(["signed:mgr", "completed"]);
    expect(r.configuration).toContain("Done");
  });

  it("shows each stage's round in the gate state, and the enabled trigger asks the current round's signers", () => {
    const start = run(rounds, [submit]);
    expect(stages(start).map((s) => [s.stage, s.round, s.complete])).toEqual([
      ["s1", 1, false],
      ["s2", 2, false],
    ]);
    expect(start.enabled.find((e) => e.event === "approve")!.actors).toEqual(["FinRole", "Ann"]);
    const next = run(rounds, [submit, answer("Ann")]);
    expect(stages(next).map((s) => [s.stage, s.complete])).toEqual([
      ["s1", true],
      ["s2", false],
    ]);
    expect(next.enabled.find((e) => e.event === "approve")!.gate).toEqual({ have: 0, need: 1 });
    expect(next.enabled.find((e) => e.event === "approve")!.actors).toEqual(expect.arrayContaining(["MgrRole"]));
  });

  it("skips a stage whose when is false, and a round with nothing needed completes at once", () => {
    const falsy = { kind: "literal", value: false };
    const d = doc(gate([stage("s1", [sig("fin", ["FinRole"])]), stage("s2", [sig("mgr", ["MgrRole"])], { round: 2, when: falsy })]));
    // Judged when its round starts: round 2 is not judged on entry.
    expect(stages(run(d, [submit])).find((s) => s.stage === "s2")).toMatchObject({ complete: false });
    // Ann completes round 1, round 2 finds nothing needed and completes at once: her signature completed the approval, so it moves (decision 33).
    const r = run(d, [submit, answer("Ann")]);
    expect(r.trace[2]).toMatchObject({ accepted: true });
    expect(outcomes(r, 1)).toEqual(["signed:fin", "completed"]);
    expect(r.configuration).toContain("Done");
    // The same when every signature of the later round is not needed.
    const unneeded = doc(gate([stage("s1", [sig("fin", ["FinRole"])]), stage("s2", [sig("mgr", ["MgrRole"], { when: falsy })], { round: 2 })]));
    expect(outcomes(run(unneeded, [submit, answer("Ann")]), 1)).toEqual(["signed:fin", "completed"]);
  });

  it("waits for the next occurrence when round 1 starts with nothing needed (decision 7)", () => {
    const falsy = { kind: "literal", value: false };
    const d = doc(gate([stage("s1", [sig("fin", ["FinRole"], { when: falsy })]), stage("s2", [sig("mgr", ["MgrRole"])], { round: 2, when: falsy })]));
    const entered = run(d, [submit]);
    expect(entered.gates[0].satisfied).toBe(true);
    expect(entered.configuration).toContain("Review");
    const moved = run(d, [submit, answer("Bob")]);
    expect(outcomes(moved, 1)).toEqual(["completed"]);
    expect(moved.configuration).toContain("Done");
  });

  it("a rejection clears every round and starts round 1 again; withdraw acts in the current round only", () => {
    const d = doc(gate([stage("s1", [sig("fin", ["FinRole"])]), stage("s2", [sig("mgr", ["MgrRole", "FinRole"])], { round: 2 })]));
    const withdrawn = run(d, [submit, answer("Ann"), answer("Ann", "withdraw")]);
    expect(last(withdrawn)).toMatchObject({ accepted: false, refusal: "gate-not-signed" });
    const rejected = run(d, [submit, answer("Ann"), answer("Bob", "reject")]);
    expect(outcomes(rejected, 2)).toEqual(["rejected:mgr"]);
    expect(stages(rejected).map((s) => s.complete)).toEqual([false, false]);
    expect(stages(rejected)[0].signatures[0].counted).toBe(0);
  });

  it("keeps a gate with one round as in part 1: no round or skipped in the gate state", () => {
    const one = doc(gate([stage("s1", [sig("fin", ["FinRole"])])]));
    const r = run(one, [submit]);
    expect(stages(r)[0]).not.toHaveProperty("round");
    expect(stages(r)[0]).not.toHaveProperty("skipped");
    expect(r.enabled.find((e) => e.event === "approve")!.responses).toEqual(["sign", "reject", "ignore", "withdraw"]);
  });
});

describe("the mock stepper: delegation", () => {
  const d = doc(gate([stage("s1", [sig("fin", ["FinRole"], { delegateTo: ["Deputy"] }), sig("mgr", ["MgrRole"])])]));
  const delegate = { ...answer("Ann", "delegate"), delegateTo: "pdan" };
  const onBehalf = (response?: string) => ({ event: "approve", participant: "pdan", ...(response ? { response } : {}), onBehalfOf: "pann" });

  it("offers delegate among the responses only where a signature has delegateTo", () => {
    expect(run(d, [submit]).enabled.find((e) => e.event === "approve")!.responses).toEqual(["sign", "reject", "ignore", "withdraw", "delegate"]);
  });

  it("delegates to a member of a delegateTo actor, audits delegated with the person it went to, and finishes the delegator", () => {
    const r = run(d, [submit, delegate]);
    expect(last(r)).toMatchObject({ accepted: true });
    expect(last(r).audit[0]).toMatchObject({ outcome: "delegated", signature: "fin", signer: "ann", delegateTo: "dan" });
    expect(r.gates[0].stages![0].signatures[0].finished).toEqual(["ann"]);
    // The delegator is finished on that signature and, by decision 16, has no other response either.
    const again = run(d, [submit, delegate, answer("Ann")]);
    expect(last(again)).toMatchObject({ accepted: false });
  });

  it("refuses a delegate to someone who is not a member of a delegateTo actor, as gate-signer", () => {
    const r = run(d, [submit, { ...answer("Ann", "delegate"), delegateTo: "pbob" }]);
    expect(last(r)).toMatchObject({ accepted: false, refusal: "gate-signer" });
  });

  it("lets the person delegated to answer on behalf, counted as the delegator's actor, with both people in the audit", () => {
    const one = run(d, [submit, delegate, onBehalf()]);
    expect(last(one).audit[0]).toMatchObject({ outcome: "signed", signer: "dan", onBehalfOf: "ann", signature: "fin" });
    expect(one.gates[0].signatures[0]).toMatchObject({ signer: "dan", actor: "Ann", signature: "fin" });
    expect(run(d, [submit, delegate, onBehalf(), answer("Bob")]).configuration).toContain("Done");
  });

  it("gives one answer per delegation: a second is refused, a withdraw reopens it, and nobody answers on behalf without one", () => {
    expect(last(run(d, [submit, delegate, onBehalf(), onBehalf()]))).toMatchObject({ accepted: false });
    const reopened = run(d, [submit, delegate, onBehalf(), onBehalf("withdraw"), onBehalf()]);
    expect(reopened.trace.slice(-3).map((t) => t.accepted)).toEqual([true, true, true]);
    expect(last(run(d, [submit, onBehalf()]))).toMatchObject({ accepted: false, refusal: "gate-signer" });
  });

  it("refuses a delegation passed on and ends delegations with a rejection", () => {
    const passed = run(d, [submit, delegate, { ...onBehalf("delegate"), delegateTo: "pbob" }]);
    expect(last(passed).diagnostics.map((x) => x.rule)).toContain("MQ9305");
    const rejected = run(d, [submit, delegate, answer("Bob", "reject"), onBehalf()]);
    expect(last(rejected)).toMatchObject({ accepted: false, refusal: "gate-signer" });
  });

  it("states MQ9305 for a step that delegates with no delegateTo, names one without delegate, or has no signature to delegate", () => {
    const rule = (steps: Json[], from = d) => last(run(from, [submit, ...steps])).diagnostics.map((x) => x.rule);
    expect(rule([answer("Ann", "delegate")])).toContain("MQ9305");
    expect(rule([{ ...answer("Ann"), delegateTo: "pdan" }])).toContain("MQ9305");
    const plain = doc(gate([stage("s1", [sig("fin", ["FinRole"])])]));
    expect(rule([delegate], plain)).toContain("MQ9305");
    expect(rule([{ ...delegate, signature: "mgr" }])).toContain("MQ9305");
  });
});

describe("the mock stepper: notices", () => {
  const notice = { remindAfter: "PT1H", escalateAfter: "PT4H", escalateTo: ["Deputy"] };
  const d = doc(gate([stage("s1", [sig("fin", ["FinRole"], notice), sig("mgr", ["MgrRole"], { remindAfter: "PT2H" })])]));
  const time = (after: string) => ({ input: "time", after });

  it("lists a reminder or an escalation that fell due on a time step, once per round start, with its recipients", () => {
    const r = run(d, [submit, time("PT90M"), time("PT90M"), time("PT3H")]);
    expect(r.trace[2].notices).toEqual([{ signature: "fin", kind: "remind", recipients: ["FinRole"] }]);
    expect(r.trace[3].notices).toEqual([{ signature: "mgr", kind: "remind", recipients: ["MgrRole"] }]);
    // Past four hours, the escalation falls due once, to escalateTo.
    expect(r.trace[4].notices).toEqual([{ signature: "fin", kind: "escalate", recipients: ["Deputy"] }]);
    // Nothing repeats.
    expect(run(d, [submit, time("PT90M"), time("PT10M")]).trace[3].notices).toBeUndefined();
  });

  it("lists nothing for a signature that is complete, and none for an approval without notices", () => {
    expect(run(d, [submit, answer("Ann"), time("PT5H")]).trace[3].notices).toEqual([{ signature: "mgr", kind: "remind", recipients: ["MgrRole"] }]);
    const plain = doc(gate([stage("s1", [sig("fin", ["FinRole"])])]));
    expect(run(plain, [submit, time("PT5H")]).trace[2]).not.toHaveProperty("notices");
  });

  it("starts the clock again with the next round", () => {
    const rounds = doc(gate([stage("s1", [sig("fin", ["FinRole"])]), stage("s2", [sig("mgr", ["MgrRole"], { remindAfter: "PT1H" })], { round: 2 })]));
    const r = run(rounds, [submit, time("PT3H"), answer("Ann"), time("PT30M"), time("PT45M")]);
    expect(r.trace[4].notices).toBeUndefined();
    expect(r.trace[5].notices).toEqual([{ signature: "mgr", kind: "remind", recipients: ["MgrRole"] }]);
  });

  it("compares expect.notices in replay as a set, and records them", () => {
    const steps = [submit, { ...time("PT90M"), expect: { notices: [{ signature: "fin", kind: "remind" }] } }];
    const done = replay(d, null, steps, { membership: members, participants });
    expect(done.expects[1].notices).toEqual([{ signature: "fin", kind: "remind" }]);
    // A step with no notices and no expectation writes no key.
    expect(done.expects[0]).not.toHaveProperty("notices");
    // A step where none fell due writes no key, even when it expected some, as the engine records it.
    const none = replay(d, null, [submit, { ...time("PT10M"), expect: { notices: [] } }], { membership: members, participants });
    expect(none.expects[1]).not.toHaveProperty("notices");
  });
});

describe("the mock stepper: review fixes for part 2", () => {
  const delegating = doc(gate([stage("s1", [sig("fin", ["FinRole"], { delegateTo: ["Deputy"], requiredActors: ["FinRole"] })])]));
  const two = doc(gate([stage("s1", [sig("fin", ["FinRole"], { delegateTo: ["Deputy"] }), sig("mgr", ["MgrRole"])])]));
  const delegate = { ...answer("Ann", "delegate"), delegateTo: "pdan" };
  const onBehalf = (response?: string) => ({ event: "approve", participant: "pdan", ...(response ? { response } : {}), onBehalfOf: "pann" });

  it("B1: an answer on behalf that completes the approval counts as the delegator's actor, so the move's check runs", () => {
    const checked = {
      ...delegating,
      subjects: [{ id: "s", entity: "E" }],
      triggerPoints: [{ id: "stop", name: "stop", type: "check", verbs: ["approve"], condition: { value: false } }],
    };
    const options = { membership: members, refusedBy: true, participants, attributesOf: () => [], subjectEntity: "E" };
    const r = simulate(checked, null, [submit, delegate, onBehalf()], options);
    expect(last(r)).toMatchObject({ accepted: false, refusal: "check", refusedBy: "stop" });
  });

  it("S1: a second answer on one delegation is refused gate-repeat, and a rejection on behalf after a signature on behalf is accepted", () => {
    expect(last(run(two, [submit, delegate, onBehalf(), onBehalf()]))).toMatchObject({ accepted: false, refusal: "gate-repeat" });
    expect(last(run(two, [submit, delegate, onBehalf("ignore"), onBehalf()]))).toMatchObject({ accepted: false, refusal: "gate-repeat" });
    const rejected = run(two, [submit, delegate, onBehalf(), onBehalf("reject")]);
    expect(last(rejected)).toMatchObject({ accepted: true });
    expect(last(rejected).audit[0]).toMatchObject({ outcome: "rejected", onBehalfOf: "ann" });
  });

  it("S2: a notice that would fall due after a timer moved the object out is not listed, and notices are sorted by signature then kind", () => {
    const notice = { remindAfter: "PT1H", escalateAfter: "PT4H", escalateTo: ["Deputy"] };
    const d = doc(gate([stage("s1", [sig("fin", ["FinRole"], notice)])]));
    (d.transitions as Json[]).push({ id: "t3", source: "Review", trigger: "after", targets: ["Draft"], after: "PT3H" });
    const r = run(d, [submit, { input: "time", after: "PT5H" }]);
    expect(last(r).configuration).toContain("Draft");
    expect(last(r).notices).toEqual([{ signature: "fin", kind: "remind", recipients: ["FinRole"] }]);
    const both = doc(gate([stage("s1", [sig("fin", ["FinRole"], notice), sig("mgr", ["MgrRole"], { remindAfter: "PT1H" })])]));
    expect(last(run(both, [submit, { input: "time", after: "PT5H" }])).notices!.map((n) => `${n.signature}:${n.kind}`)).toEqual([
      "fin:remind",
      "fin:escalate",
      "mgr:remind",
    ]);
  });

  it("M3: delegating to the delegator themselves is refused gate-signer with a reason, and a step naming its own participant is MQ9305", () => {
    const same = [...participants, { id: "pann2", name: "pann2", identity: "ann", actor: "Deputy" }];
    const self = simulate(delegating, null, [submit, { ...answer("Ann", "delegate"), delegateTo: "pann2" }], { membership: members, participants: same });
    expect(last(self)).toMatchObject({ accepted: false, refusal: "gate-signer" });
    expect(last(self).notes!.join(" ")).toContain("themselves");
    const own = run(delegating, [submit, { event: "approve", participant: "pdan", response: "delegate", delegateTo: "pdan" }]);
    expect(last(own).diagnostics.find((x) => x.rule === "MQ9305")?.message).toBe(
      "the step delegates to its own participant; a delegation goes to someone else.",
    );
  });
});
