// Sign-off part 2, second browser pass: recording keeps a delegation, the refusal headlines of a delegation, the Approvals rows that wait
// for a round or name a delegation, the trace words, and the next notice of a time section.
import { describe, expect, it } from "vitest";
import { approvalRows, inputLabel, lastStepLines, namesOf, nextNoticeDue } from "@/editors/process/simulation/model";
import type { SimulationResult, StepTrace } from "@/editors/process/simulation/api";
import { APPROVER, BUDGET_HOLDER, INVOICE_LIFECYCLE } from "@/mocks/model/processSeed";
import { SIMULATION_LABELS as SL } from "@/model/labels";
import type { ProcessDoc } from "@/model/process";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

describe("record to scenario keeps a delegation", () => {
  const mock = useMockApi();
  it("keeps response, delegateTo and onBehalfOf on the recorded steps, under the recorded participants' ids, and replays green", async () => {
    const doc = mock.backend.model.get(INVOICE_LIFECYCLE)!.json as Json;
    const gated = (doc.transitions as Json[]).find((t) => t.gate)!;
    const event = String(gated.event);
    const stages = ((gated.gate as Json).stages as Json[]).flatMap((s) => s.signatures as Json[]);
    const manager = String(stages.find((s) => s.name === "Manager")!.id);
    const people = [
      { id: "01JQ0D00000000000000000001", name: "mark", identity: "mark", actor: BUDGET_HOLDER },
      { id: "01JQ0D00000000000000000002", name: "carla", identity: "carla", actor: APPROVER },
    ];
    const steps = [
      { event, participant: "mark", signature: manager, response: "delegate", delegateTo: "carla" },
      { event, participant: "carla", signature: manager, onBehalfOf: "mark" },
    ];
    const post = (path: string, body: unknown) =>
      fetch(mock.url(path), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const res = await post(`/api/processes/${INVOICE_LIFECYCLE}/scenarios`, { name: "DelegateFlow", participants: people, steps });
    expect(res.status).toBe(201);
    const saved = (await res.json()) as { element: { participants: { id: string }[]; steps: Json[] } };
    const [mark, carla] = saved.element.participants.map((p) => p.id);
    expect(saved.element.steps[0]).toMatchObject({ response: "delegate", delegateTo: carla, participant: mark, signature: manager });
    expect(saved.element.steps[1]).toMatchObject({ onBehalfOf: mark, participant: carla });
    const verify = await post(`/api/processes/${INVOICE_LIFECYCLE}/verify`, { scenarios: ["DelegateFlow"] });
    expect(verify.status).toBe(200);
    const body = (await verify.json()) as { passed: boolean; results?: { passed: boolean; failure?: unknown }[] };
    expect(JSON.stringify(body)).not.toContain('"passed":false');
  });
});

const names = namesOf({ states: [], transitions: [] } as unknown as ProcessDoc, new Map(), [
  { id: "pm", name: "mark" },
  { id: "pc", name: "carla" },
]);
const refused = (input: Json): StepTrace =>
  ({ index: 1, input, accepted: false, refusal: "gate-signer", audit: [], microsteps: [], guards: [], changed: {}, diagnostics: [] }) as unknown as StepTrace;

describe("the refusal headline of a delegation", () => {
  const headline = (input: Json) => lastStepLines(refused(input), names).find((l) => l.kind === "refusal")!.text;
  it("says the person it goes to is not among May be delegated to", () => {
    expect(headline({ event: "e", participant: "pm", response: "delegate", delegateTo: "pc" })).toBe(`Refused: ${SL.delegateTargetReason}`);
    expect(SL.delegateTargetReason).toBe("The person it goes to isn't one of the actors this signature may be delegated to.");
  });
  it("says a signer can't delegate to themselves", () => {
    expect(headline({ event: "e", participant: "pm", response: "delegate", delegateTo: "pm" })).toBe(`Refused: ${SL.delegateToSelfReason}`);
    expect(SL.delegateToSelfReason).toBe("A signer can't delegate a signature to themselves; delegate it to someone else.");
  });
  it("keeps the signer wording for other refusals", () => {
    expect(headline({ event: "e", participant: "pm" })).toBe(`Refused: ${SL.refusals["gate-signer"]}`);
  });
});

describe("the trace names a delegation", () => {
  it("says who a delegation went to and on whose behalf an answer is", () => {
    expect(inputLabel({ event: "e", participant: "pm", response: "delegate", delegateTo: "pc" } as never, names)).toContain("delegated to carla");
    expect(inputLabel({ event: "e", participant: "pc", onBehalfOf: "pm" } as never, names)).toContain("on behalf of mark");
  });
});

const process = {
  kind: "process",
  id: "P",
  name: "P",
  use: "lifecycle",
  events: [{ id: "ev", name: "approve" }],
  states: [{ id: "A", name: "A" }],
  transitions: [
    {
      id: "t",
      source: "A",
      event: "ev",
      gate: {
        id: "g",
        name: "Release",
        stages: [
          { id: "st1", name: "Review", round: 1, signatures: [{ id: "m", name: "Manager", signers: ["x"], delegateTo: ["y"], remindAfter: "PT24H" }] },
          { id: "st2", name: "Release", round: 2, signatures: [{ id: "c", name: "Controller", signers: ["x"] }] },
        ],
      },
    },
  ],
} as unknown as ProcessDoc;
const sig = (signature: string, needed: boolean, finished: string[] = []) => ({
  signature,
  needed,
  counted: finished.length,
  required: 1,
  finished,
  complete: false,
});
const entry = (index: number, clock: string, audit: Json[] = [], extra: Json = {}) =>
  ({
    index,
    input: null,
    accepted: true,
    refusal: null,
    audit,
    microsteps: [],
    guards: [],
    changed: {},
    diagnostics: [],
    clock,
    ...extra,
  }) as unknown as StepTrace;
const result = (trace: StepTrace[], clock: string, finished: string[] = ["mark"]) =>
  ({
    trace,
    clock,
    timers: [],
    gates: [
      {
        gate: "g",
        transition: "t",
        signatures: [],
        satisfied: false,
        stages: [
          { stage: "st1", complete: false, round: 1, signatures: [sig("m", true, finished)] },
          { stage: "st2", complete: false, round: 2, signatures: [sig("c", false)] },
        ],
      },
    ],
  }) as unknown as SimulationResult;
const delegated = { gate: "g", outcome: "delegated", signer: "mark", signature: "m", delegateTo: "carla" };

describe("the Approvals rows", () => {
  const n = namesOf(process, new Map());
  it("shows a signature of a later round as waiting for it, not as not needed", () => {
    const rows = approvalRows(result([entry(0, "2000-01-01T00:00:00Z")], "2000-01-01T00:00:00Z"), process, n);
    const [first, later] = rows[0].stages.flatMap((s) => s.signatures);
    expect(first.waitsForRound).toBeUndefined();
    expect(later.waitsForRound).toBe(2);
    expect(SL.signatureState("Controller", false, 0, 1, 2)).toBe("Controller: waits for round 2");
    expect(SL.signatureState("Controller", false, 0, 1)).toBe("Controller: not needed");
  });
  it("names a delegation and an answer on behalf in the finished list", () => {
    const t = [entry(0, "2000-01-01T00:00:00Z"), entry(1, "2000-01-01T00:00:00Z", [delegated])];
    expect(approvalRows(result(t, "2000-01-01T00:00:00Z"), process, n)[0].stages[0].signatures[0].finished).toEqual(["mark (delegated to carla)"]);
    const behalf = [...t, entry(2, "2000-01-02T00:00:00Z", [{ gate: "g", outcome: "signed", signer: "carla", signature: "m", onBehalfOf: "mark" }])];
    const r = result(behalf, "2000-01-02T00:00:00Z", ["mark", "carla"]);
    expect(approvalRows(r, process, n)[0].stages[0].signatures[0].finished).toEqual(["mark (delegated to carla)", "carla (on behalf of mark)"]);
  });
});

describe("the Time section's next notice", () => {
  const n = namesOf(process, new Map());
  it("says when a pending reminder falls due, and none once it was listed", () => {
    const t = [entry(0, "2000-01-01T00:00:00Z")];
    expect(nextNoticeDue(result(t, "2000-01-01T00:00:00Z"), process, n)).toEqual({ signature: "Manager", kind: "remind", dueAt: "2000-01-02T00:00:00Z" });
    expect(SL.nextNotice("Manager", "remind", "2000-01-02T00:00:00Z")).toBe("No timer is scheduled. Next notice: Manager reminder at 2000-01-02T00:00:00Z");
    const fired = [...t, entry(1, "2000-01-02T00:00:00Z", [], { notices: [{ signature: "m", kind: "remind", recipients: [] }] })];
    expect(nextNoticeDue(result(fired, "2000-01-02T00:00:00Z"), process, n)).toBeNull();
  });
});
