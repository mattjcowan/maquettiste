// The mock stepper's approvals (signoff-design.md decisions 7 to 16, 19, 26 to 28): the responses, the pick, rows 7a to 7d, notBy
// with its window, the veto, ignore, withdraw, the gate state and a plain gate that behaves as before.
import { describe, expect, it } from "vitest";
import { Membership } from "@/mocks/model/membership";
import { replay, signatureCounts, simulate, type SimulationJson } from "@/mocks/model/stepper";

type Json = Record<string, unknown>;
const st = (id: string) => ({ id, name: id });

const members = new Membership([
  { id: "FinRole", json: { name: "FinRole", type: "role" } },
  { id: "MgrRole", json: { name: "MgrRole", type: "role" } },
  { id: "Ann", json: { name: "Ann", type: "person", memberOf: ["FinRole"] } },
  { id: "Bob", json: { name: "Bob", type: "person", memberOf: ["MgrRole"] } },
  { id: "Cat", json: { name: "Cat", type: "person", memberOf: ["MgrRole"] } },
  { id: "Dee", json: { name: "Dee", type: "person", memberOf: ["MgrRole", "FinRole"] } },
]);

function doc(gate: Json | null, extra: Json = {}): Json {
  return {
    kind: "process",
    use: "lifecycle",
    id: "P",
    name: "P",
    events: [
      { id: "submit", name: "submit", verb: "promote" },
      { id: "approve", name: "approve", verb: "approve" },
      { id: "back", name: "back", verb: "reject" },
    ],
    states: [st("Draft"), st("Review"), st("Done")],
    transitions: [
      { id: "t1", source: "Draft", event: "submit", targets: ["Review"] },
      { id: "t2", source: "Review", event: "approve", targets: ["Done"], ...(gate ? { gate } : {}) },
      { id: "t3", source: "Review", event: "back", targets: ["Draft"] },
    ],
    ...extra,
  };
}

const sig = (id: string, signers: string[], extra: Json = {}): Json => ({ id, name: id, signers, ...extra });
const approval = (signatures: Json[], extra: Json = {}): Json => ({ id: "g", name: "g", stages: [{ id: "stage", name: "stage", signatures }], ...extra });
const run = (d: Json, steps: Json[]) => simulate(d, null, steps, { membership: members, refusedBy: true });
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
const at = (r: SimulationJson, signature: string) => r.gates[0].stages![0].signatures.find((s) => s.signature === signature)!;

describe("the mock stepper: approvals", () => {
  const two = doc(approval([sig("fin", ["FinRole"]), sig("mgr", ["MgrRole"], { required: 2 })]));

  it("starts the round on entry, with needed, counted, required and the responses of the enabled trigger", () => {
    const r = run(two, [submit]);
    expect(r.gates[0].stages![0].signatures.map((s) => [s.signature, s.needed, s.counted, s.required, s.finished, s.complete])).toEqual([
      ["fin", true, 0, 1, [], false],
      ["mgr", true, 0, 2, [], false],
    ]);
    const enabled = r.enabled.find((e) => e.event === "approve")!;
    expect(enabled.responses).toEqual(["sign", "reject", "ignore", "withdraw"]);
    expect(enabled.gate).toEqual({ have: 0, need: 3 });
    expect(enabled.actors).toEqual(expect.arrayContaining(["FinRole", "MgrRole"]));
  });

  it("counts a signature on the picked signature, audits its name and stage, and moves when every needed signature is complete", () => {
    const r = run(two, [submit, answer("Ann"), answer("Bob"), answer("Cat")]);
    expect(outcomes(r, 1)).toEqual(["signed:fin"]);
    expect(r.trace[2].audit[0]).toMatchObject({ signature: "fin", stage: "stage", outcome: "signed" });
    expect(r.trace[3].accepted).toBe(true);
    expect(outcomes(r, 3)).toEqual(["signed:mgr", "completed"]);
    expect(r.configuration).toContain("Done");
    // One short of complete, it has not moved.
    expect(run(two, [submit, answer("Ann"), answer("Bob")]).configuration).toContain("Review");
  });

  it("leaves a plain gate exactly as before: no stages, no signature in the audit, no responses", () => {
    const plain = doc({ id: "g", name: "g", signers: ["FinRole"], required: 2 });
    const r = run(plain, [submit, answer("Ann")]);
    expect(r.trace[2].audit[0]).not.toHaveProperty("signature");
    expect(r.gates[0].stages).toBeUndefined();
    expect(r.enabled.find((e) => e.event === "approve")!.responses).toBeUndefined();
    expect(run(plain, [submit, answer("Ann"), answer("Dee")]).configuration).toContain("Done");
    // A response other than sign has nothing to answer on a plain gate.
    const refused = run(plain, [submit, answer("Ann", "reject")]);
    expect(last(refused)).toMatchObject({ accepted: false, refusal: "no-transition" });
  });

  it("picks, for sign, the first signature the person may fill, and prefers one whose required actor they are", () => {
    const d = doc(approval([sig("one", ["FinRole", "MgrRole"]), sig("two", ["MgrRole"], { requiredActors: ["Bob"] })]));
    expect(outcomes(run(d, [submit, answer("Ann")]), 1)).toEqual(["signed:one"]);
    expect(outcomes(run(d, [submit, answer("Bob")]), 1)).toEqual(["signed:two"]);
    // A step's own signature replaces the pick.
    expect(outcomes(run(d, [submit, answer("Cat", "sign", { signature: "two" })]), 1)).toEqual(["signed:two"]);
  });

  it("refuses with gate-signer when the person meets no signers, and names the gate", () => {
    const r = run(two, [submit, { event: "approve", actor: "Cat", signer: "x" }]);
    expect(last(r)).toMatchObject({ accepted: true });
    const none = run(two, [submit, { event: "approve", actor: "Nobody", signer: "n" }]);
    expect(last(none)).toMatchObject({ accepted: false, refusal: "gate-signer" });
  });

  it("row 7d: a person answers one signature once, unless the gate lets one person answer across signatures", () => {
    const d = doc(approval([sig("a", ["MgrRole"]), sig("b", ["MgrRole"])]));
    const again = run(d, [submit, answer("Bob"), answer("Bob")]);
    expect(last(again)).toMatchObject({ accepted: false, refusal: "gate-repeat", refusedBy: "g" });
    // The refusal is on the first signature the person meets, complete or not (review S1): a, which Bob already signed.
    expect(outcomes(again, 2)).toEqual(["refused:a"]);
    const across = run(doc(approval([sig("a", ["MgrRole"]), sig("b", ["MgrRole"])], { allowSignerAcrossSignatures: true })), [
      submit,
      answer("Bob"),
      answer("Bob"),
    ]);
    expect(outcomes(across, 2)).toEqual(["signed:b", "completed"]);
    // 7a: a person who signed can still reject (7d), a person who ignored cannot.
    const rejects = run(d, [submit, answer("Bob"), answer("Bob", "reject")]);
    expect(outcomes(rejects, 2)).toEqual(["rejected:a"]);
    const ignored = run(d, [submit, answer("Bob", "ignore"), answer("Bob", "reject")]);
    expect(last(ignored)).toMatchObject({ accepted: false, refusal: "gate-repeat" });
    // 7d on ignore: a person who signed or ignored may not ignore again.
    expect(last(run(d, [submit, answer("Bob", "ignore"), answer("Bob", "ignore")]))).toMatchObject({ refusal: "gate-repeat" });
    // allowRepeatSigner lets one person count twice on one signature.
    const repeat = doc(approval([sig("a", ["MgrRole"], { required: 2, allowRepeatSigner: true })]));
    expect(outcomes(run(repeat, [submit, answer("Bob"), answer("Bob")]), 2)).toEqual(["signed:a", "completed"]);
  });

  it("ignore finishes the person on the signature and leaves the count", () => {
    const r = run(two, [submit, answer("Bob", "ignore")]);
    expect(outcomes(r, 1)).toEqual(["ignored:mgr"]);
    expect(at(r, "mgr")).toMatchObject({ counted: 0, finished: ["bob"], complete: false });
    expect(last(r).accepted).toBe(true);
    expect(r.configuration).toEqual(["Review"]);
    // The ignore is the person's one response (7d): they cannot sign the same signature afterwards.
    expect(last(run(two, [submit, answer("Bob", "ignore"), answer("Bob")]))).toMatchObject({ accepted: false, refusal: "gate-repeat" });
  });

  it("withdraw removes the person's latest signature, or is refused with gate-not-signed", () => {
    const r = run(two, [submit, answer("Bob"), answer("Bob", "withdraw")]);
    expect(outcomes(r, 2)).toEqual(["withdrawn:mgr"]);
    expect(at(r, "mgr").counted).toBe(0);
    // They may sign again after a withdraw.
    expect(outcomes(run(two, [submit, answer("Bob"), answer("Bob", "withdraw"), answer("Bob")]), 3)).toEqual(["signed:mgr"]);
    const nothing = run(two, [submit, answer("Cat", "withdraw")]);
    expect(last(nothing)).toMatchObject({ accepted: false, refusal: "gate-not-signed" });
    expect(outcomes(nothing, 1)).toEqual(["refused"]);
  });

  it("a rejection is a veto: it clears the approval, moves nothing and starts the round again", () => {
    const r = run(two, [submit, answer("Ann"), answer("Bob"), answer("Dee", "reject", { reason: "no" })]);
    expect(outcomes(r, 3)).toEqual(["rejected:fin"]);
    expect(r.configuration).toEqual(["Review"]);
    expect(at(r, "fin").counted).toBe(0);
    expect(at(r, "mgr")).toMatchObject({ counted: 0, finished: [] });
    // The first needed signature whose signers the person meets is the one it names, even when complete.
    expect(outcomes(run(two, [submit, answer("Ann"), answer("Ann", "reject")]), 2)).toEqual(["rejected:fin"]);
    expect(last(run(two, [submit, answer("Nobody", "reject")]))).toMatchObject({ accepted: false, refusal: "gate-signer" });
  });

  it("the move on rejection is an ordinary move that discards the signatures, audited discarded", () => {
    const r = run(two, [submit, answer("Ann"), { event: "back", actor: "Ann", signer: "ann" }]);
    expect(r.configuration).toEqual(["Draft"]);
    expect(r.trace[3].audit.map((a) => a.outcome)).toEqual(["discarded"]);
    // Leaving with only an ignore on the approval audits discarded too.
    const ignored = run(two, [submit, answer("Bob", "ignore"), { event: "back", actor: "Ann", signer: "ann" }]);
    expect(ignored.trace[3].audit.map((a) => a.outcome)).toEqual(["discarded"]);
    // Entering again starts a new round.
    expect(at(run(two, [submit, answer("Ann"), { event: "back", actor: "Ann", signer: "ann" }, submit]), "fin").counted).toBe(0);
  });

  it("row 7c: notBy keeps off whoever did the verb since the source was entered, the entering move included", () => {
    const d = doc(approval([sig("mgr", ["MgrRole", "FinRole"], { notBy: [{ verb: "promote" }] })]));
    const same = run(d, [{ event: "submit", actor: "Bob", signer: "bob" }, answer("Bob")]);
    expect(last(same)).toMatchObject({ accepted: false, refusal: "gate-excluded", refusedBy: "g" });
    expect(outcomes(same, 1)).toEqual(["refused:mgr"]);
    expect(last(run(d, [{ event: "submit", actor: "Bob", signer: "bob" }, answer("Cat")])).accepted).toBe(true);
    // The window opens again with the move that enters the state: a person who submitted before the last entry is free.
    const again = run(d, [
      { event: "submit", actor: "Bob", signer: "bob" },
      { event: "back", actor: "Cat", signer: "cat" },
      { event: "submit", actor: "Cat", signer: "cat" },
      answer("Bob"),
    ]);
    expect(last(again).accepted).toBe(true);
    // A transition is named by its id, and a gated move is done by whoever signed it.
    const byMove = doc(approval([sig("mgr", ["MgrRole"], { notBy: [{ transition: "t1" }] })]));
    expect(last(run(byMove, [{ event: "submit", actor: "Cat", signer: "cat" }, answer("Cat")]))).toMatchObject({ refusal: "gate-excluded" });
  });

  it("with only the excluded person able to sign, the pick refuses with the first failing check of that signature", () => {
    const d = doc(approval([sig("a", ["MgrRole"], { notBy: [{ verb: "promote" }] })]));
    const r = run(d, [{ event: "submit", actor: "Bob", signer: "bob" }, answer("Bob", "ignore")]);
    expect(last(r)).toMatchObject({ refusal: "gate-excluded" });
  });

  it("judges when on entry: false is not needed, true and unknown are needed, and nothing needed completes at once", () => {
    const d = doc(
      approval([
        sig("a", ["FinRole"], { when: { value: false } }),
        sig("b", ["MgrRole"], { when: { value: true } }),
        sig("c", ["MgrRole"], { when: { text: "no such thing()" } }),
      ]),
    );
    const r = run(d, [submit]);
    expect(r.gates[0].stages![0].signatures.map((s) => s.needed)).toEqual([false, true, true]);
    expect(r.gates[0].stages![0].complete).toBe(false);
    expect(r.enabled.find((e) => e.event === "approve")!.gate).toEqual({ have: 0, need: 2 });
    // Without any needed signature the approval is satisfied and waits for the next occurrence, which moves.
    const none = doc(approval([sig("a", ["FinRole"], { when: { value: false } })]));
    const waiting = run(none, [submit]);
    expect(waiting.gates[0]).toMatchObject({ satisfied: true });
    expect(waiting.enabled.find((e) => e.event === "approve")!.responses).toEqual(["sign"]);
    const moved = run(none, [submit, { event: "approve", actor: "Ann", signer: "ann" }]);
    expect(moved.configuration).toContain("Done");
    expect(moved.trace[2].audit.map((a) => [a.outcome, a.signature])).toEqual([["completed", undefined]]);
    // A response other than sign has no open approval to act on.
    expect(last(run(none, [submit, answer("Ann", "reject")]))).toMatchObject({ accepted: false, refusal: "no-transition" });
  });

  it("starts the round again when a rejection clears it, judging when once more", () => {
    const d = doc(approval([sig("a", ["FinRole", "MgrRole"]), sig("b", ["MgrRole"], { when: { path: ["total"] } })]), {
      context: [{ id: "total", name: "total", type: "number", default: 0 }],
    });
    const r = run(d, [submit]);
    expect(r.gates[0].stages![0].signatures.map((s) => s.needed)).toEqual([true, true]);
  });

  it("records the signatures counted after a step that answers an approval, for expect.signatures", () => {
    const r = simulate(two, null, [submit, answer("Ann"), answer("Bob", "ignore")], { membership: members });
    expect(signatureCounts.get(r.trace[1])).toBeUndefined();
    expect(signatureCounts.get(r.trace[2])).toEqual({ fin: 1, mgr: 0 });
    expect(signatureCounts.get(r.trace[3])).toEqual({ fin: 1, mgr: 0 });
    const replayed = replay(two, null, [submit, answer("Ann")], { membership: members });
    expect(replayed.expects[1].signatures).toEqual({ fin: 1, mgr: 0 });
    expect(replayed.expects[0].signatures).toBeUndefined();
  });

  it("runs the trigger points of reject, ignore and withdraw where sign's run", () => {
    const withPoints = doc(approval([sig("mgr", ["MgrRole"], { required: 2 })]), {
      triggerPoints: [
        { id: "tp", name: "on response", verbs: ["reject", "ignore", "withdraw"], type: "check", condition: { value: false } },
        { id: "ts", name: "on sign", verbs: ["sign"], type: "check", condition: { value: true } },
      ],
    });
    expect(last(run(withPoints, [submit, answer("Bob")])).triggerPoints?.map((x) => [x.triggerPoint, x.verb, x.outcome])).toEqual([["ts", "sign", "passed"]]);
    for (const response of ["ignore", "reject"]) {
      const r = run(withPoints, [submit, answer("Bob", response)]);
      expect(last(r)).toMatchObject({ accepted: false, refusal: "check", refusedBy: "tp" });
      expect(last(r).triggerPoints?.[0]).toMatchObject({ triggerPoint: "tp", verb: response, outcome: "refused" });
    }
  });
});
