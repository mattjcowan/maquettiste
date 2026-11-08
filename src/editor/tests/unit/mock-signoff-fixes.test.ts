// The mock stepper follows the engine after the sign-off review and the browser pass: a repeat on a complete signature is gate-repeat, an
// approval that needs nothing moves as a plain move (checksUnknown, no other response), a human task's end is an act, MQ9305 and MQ9704 on
// a step's response and signature, the MQ9311 sentence, and the typed preview of a signature's `when`.
import { describe, expect, it } from "vitest";
import { Membership } from "@/mocks/model/membership";
import { previewExpression } from "@/mocks/model/expressionPreview";
import { simulate } from "@/mocks/model/stepper";
import { verbUses } from "@/mocks/model/verbs";
import { failureSentence } from "@/editors/scenario/replayFailure";

type Json = Record<string, unknown>;
const st = (id: string): Json => ({ id, name: id });
const members = new Membership([
  { id: "FinRole", json: { name: "FinRole", type: "role" } },
  { id: "MgrRole", json: { name: "MgrRole", type: "role" } },
  { id: "Ann", json: { name: "Ann", type: "person", memberOf: ["FinRole"] } },
  { id: "Bob", json: { name: "Bob", type: "person", memberOf: ["MgrRole"] } },
]);
const sig = (id: string, signers: string[], extra: Json = {}): Json => ({ id, name: id, signers, ...extra });
const doc = (signatures: Json[] | null, extra: Json = {}): Json => ({
  kind: "process",
  use: "lifecycle",
  id: "P",
  name: "P",
  subjects: [{ id: "s", entity: "E" }],
  events: [
    { id: "submit", name: "submit", verb: "promote" },
    { id: "approve", name: "approve", verb: "approve" },
  ],
  states: [st("Draft"), st("Review"), st("Done")],
  transitions: [
    { id: "t1", source: "Draft", event: "submit", targets: ["Review"] },
    {
      id: "t2",
      source: "Review",
      event: "approve",
      targets: ["Done"],
      verb: "promote",
      ...(signatures ? { gate: { id: "g", name: "g", stages: [{ id: "stage", name: "stage", signatures }] } } : {}),
    },
  ],
  triggerPoints: [
    { id: "move", name: "move", type: "check", verbs: ["promote"] },
    { id: "signs", name: "signs", type: "check", verbs: ["sign"] },
  ],
  ...extra,
});
const options = { membership: members, refusedBy: true, attributesOf: () => [], subjectEntity: "E" };
const submit = { event: "submit", actor: "Ann", signer: "ann" };
const sign = (actor: string, extra: Json = {}) => ({ event: "approve", actor, signer: actor.toLowerCase(), ...extra });
const last = (r: ReturnType<typeof simulate>) => r.trace[r.trace.length - 1];

describe("signing again on a complete signature", () => {
  it("is gate-repeat, not gate-signer, when another signature is still open", () => {
    const d = doc([sig("fin", ["FinRole"]), sig("mgr", ["MgrRole"])]);
    const r = simulate(d, null, [submit, sign("Ann"), sign("Ann")], options);
    expect(last(r)).toMatchObject({ accepted: false, refusal: "gate-repeat" });
  });

  it("refuses an ignore gate-signer when the only signature the person meets is complete", () => {
    const d = doc([sig("fin", ["FinRole"]), sig("mgr", ["MgrRole"])]);
    const r = simulate(d, null, [submit, sign("Ann"), sign("Ann", { response: "ignore" })], options);
    expect(last(r)).toMatchObject({ accepted: false, refusal: "gate-signer" });
    // Another finance person who hasn't answered is refused the same way, not recorded as an ignore on the complete signature.
    const other = simulate(d, null, [submit, sign("Ann"), { event: "approve", actor: "FinRole", signer: "fay", response: "ignore" }], options);
    expect(last(other)).toMatchObject({ accepted: false, refusal: "gate-signer" });
  });
});

describe("an approval that needs nothing", () => {
  const none = doc([sig("fin", ["FinRole"], { when: { value: false } })]);

  it("lists only the move's checks, as a move without a gate", () => {
    const r = simulate(none, null, [submit], options);
    expect(r.enabled.find((e) => e.event === "approve")!.checksUnknown).toEqual(["move"]);
    // With something needed, sign's checks come first as before.
    const needed = simulate(doc([sig("fin", ["FinRole"])]), null, [submit], options);
    expect(needed.enabled.find((e) => e.event === "approve")!.checksUnknown).toEqual(["signs", "move"]);
  });

  it("is not open: a response other than sign has no transition", () => {
    const r = simulate(none, null, [submit, sign("Ann", { response: "reject" })], options);
    expect(last(r)).toMatchObject({ accepted: false, refusal: "no-transition" });
  });
});

describe("a human task's end", () => {
  it("is accepted for a person of the task's actors", () => {
    const d = doc([sig("fin", ["FinRole"])]);
    d.states = [st("Draft"), { id: "Review", name: "Review", invoke: [{ id: "task", name: "task", type: "human-task", actors: ["FinRole"] }] }, st("Done")];
    d.transitions = [
      { id: "t1", source: "Draft", event: "submit", targets: ["Review"] },
      { id: "t2", source: "Review", invoke: "task", trigger: "invoke-done", targets: ["Done"], verb: "promote" },
    ];
    const r = simulate(d, null, [submit, { input: "invoke-done", invoke: "task", actor: "Ann", signer: "ann" }], options);
    expect(last(r).accepted).toBe(true);
  });
});

describe("MQ9305 on a step's response and signature", () => {
  const plain = doc(null);
  const gated = doc([sig("fin", ["FinRole"])]);
  const problems = (d: Json, step: Json) => last(simulate(d, null, [submit, step], options)).diagnostics.filter((x) => x.rule === "MQ9305");

  it("flags a response or a signature on an event no approval covers", () => {
    expect(problems(plain, sign("Ann", { response: "reject" }))[0].message).toMatch(/response 'reject'/);
    expect(problems(plain, sign("Ann", { signature: "fin" }))[0].message).toMatch(/names a signature/);
  });

  it("flags a signature that belongs to no approval of the event, and passes one that does", () => {
    expect(problems(gated, sign("Ann", { signature: "nope" }))[0].message).toMatch(/'nope' is not a named signature/);
    expect(problems(gated, sign("Ann", { signature: "fin" }))).toEqual([]);
  });
});

describe("MQ9704 on a step's response", () => {
  it("names a response the project hides, at the step's response", () => {
    const uses = verbUses({
      kind: "scenario",
      steps: [
        { id: "a", input: "event", event: "e", response: "ignore" },
        { id: "b", input: "event", event: "e" },
      ],
    });
    expect(uses).toEqual([{ pointer: "/steps/0/response", holder: "a", key: "ignore", owner: "step" }]);
  });
});

describe("MQ9311 in a sentence", () => {
  it("names the signatures and the counts, with the step once", () => {
    const names: Record<string, string> = { a: "Finance", b: "Manager" };
    const text = failureSentence(
      { step: 1, rule: "MQ9311", message: "Step 2: differ", expected: { a: 1, b: 0 }, actual: { a: 0, b: 0 } },
      undefined,
      (id) => names[id] ?? null,
    );
    expect(text).toBe("Step 2 was expected to leave Finance 1, Manager 0 signed, but in the replay it leaves Finance 0, Manager 0.");
  });
});

describe("the typed preview of a signature's when", () => {
  const entries = [{ id: "P", json: { kind: "process", name: "P" } }];
  it("takes site approval with the lifecycle as element", () => {
    const ok = previewExpression(entries, { element: "P", site: "approval", text: "subject.total > 1" });
    expect(ok.diagnostics).toEqual([]);
    expect(ok.preview?.text).toBe("subject.total > 1");
  });

  it("refuses an element that is no lifecycle and a text that is no condition", () => {
    expect(previewExpression(entries, { element: "X", site: "approval", text: "true" }).diagnostics[0].message).toMatch(/lifecycle/);
    expect(previewExpression(entries, { element: "P", site: "approval", text: "'x'" }).diagnostics[0].message).toMatch(/true or false/);
  });
});

describe("removing a move that a Not by names", () => {
  it("takes the entry out, and the key with the last entry", async () => {
    const { dropTriggerPointRefs } = await import("@/model/process");
    const process = {
      transitions: [
        {
          id: "t",
          gate: {
            stages: [
              {
                signatures: [
                  { id: "a", notBy: [{ transition: "gone" }, { verb: "submit" }] },
                  { id: "b", notBy: [{ transition: "gone" }] },
                ],
              },
            ],
          },
        },
      ],
    } as never as Parameters<typeof dropTriggerPointRefs>[0];
    dropTriggerPointRefs(process, { transitions: new Set(["gone"]) });
    const sigs = (process.transitions![0].gate!.stages![0] as { signatures: { notBy?: unknown[] }[] }).signatures;
    expect(sigs[0].notBy).toEqual([{ verb: "submit" }]);
    expect(sigs[1]).not.toHaveProperty("notBy");
  });
});
