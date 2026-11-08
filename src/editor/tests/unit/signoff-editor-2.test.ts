// The editor's second part of sign-off (signoff-design.md section 11): the Verbs tab's uses, a scenario step's response and expected
// signatures, the simulation panel's response and signature fields and its approval rows, and the trace words.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { approvalRows, defaultRowValue, inputLabel, inputOf, namesOf, type EnabledRow } from "@/editors/process/simulation/model";
import type { SimulationResult } from "@/editors/process/simulation/api";
import { approvalsOfEvent, hasApproval } from "@/model/approvals";
import { stepRows, type ProcessDoc, type ScenarioDoc } from "@/model/process";
import { stepVerbs } from "@/model/triggerPoints";
import { usageText, verbUsage, verbUsers } from "@/model/verbList";

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
      gate: { id: "g", name: "Release", stages: [{ id: "st", name: "Review", signatures: [{ id: "s1", name: "Finance", signers: ["x"] }] }] },
    },
  ],
} as unknown as ProcessDoc;

describe("the Verbs tab and approvals", () => {
  it("counts approvals among the uses of reject, ignore and withdraw, and lists their process", () => {
    const rows = [
      { kind: "process", id: "P", name: "P", verbs: [], approvals: 2 },
      { kind: "process", id: "Q", name: "Q", verbs: [] },
    ] as unknown as ElementSummary[];
    const usage = verbUsage(rows);
    for (const key of ["reject", "ignore", "withdraw"]) {
      expect(usage.get(key)?.approvals).toBe(2);
      expect(usageText(usage.get(key))).toBe("2 approvals");
      expect(verbUsers(rows, key).map((r) => r.id)).toEqual(["P"]);
    }
    expect(usage.get("sign")).toBeUndefined();
  });
});

describe("a scenario step that answers an approval", () => {
  it("finds the approvals of an event and prints the response, signature and expected signatures by name", () => {
    expect(hasApproval(process)).toBe(true);
    expect(approvalsOfEvent(process, "ev").map((g) => g.id)).toEqual(["g"]);
    expect(approvalsOfEvent(process, "other")).toEqual([]);
    const scenario = {
      kind: "scenario",
      id: "sc",
      name: "sc",
      process: "P",
      steps: [{ id: "a", event: "ev", response: "reject", signature: "s1", expect: { signatures: { s1: 0 } } }],
    } as unknown as ScenarioDoc;
    const [row] = stepRows(scenario, process, new Map());
    expect(row).toMatchObject({ response: "reject", signature: "Finance", signatures: "Finance=0" });
  });

  it("runs the trigger points of the response's verb, not sign's, on an approval", () => {
    const verbs = (response?: string) => [...stepVerbs(process, { id: "a", event: "ev", ...(response ? { response } : {}) } as never)];
    expect(verbs()).toEqual(["sign"]);
    expect(verbs("withdraw")).toEqual(["withdraw"]);
  });
});

describe("the simulation panel's approvals", () => {
  const row = (extra: Partial<EnabledRow> = {}): EnabledRow =>
    ({
      trigger: "event",
      label: "approve",
      event: "ev",
      invoke: null,
      transitions: ["t"],
      actors: [],
      anyActor: true,
      payload: [],
      gate: { have: 0, need: 1, meanings: [], reasonRequired: false },
      responses: ["sign", "reject", "ignore", "withdraw"],
      signatures: [{ id: "s1", name: "Finance" }],
      assume: [],
      checks: [],
      ...extra,
    }) as EnabledRow;
  const result = { final: false, clock: "", enabled: [], gates: [], timers: [] } as unknown as SimulationResult;

  it("raises a response other than sign with the signature picked, and leaves the defaults out", () => {
    const value = defaultRowValue(row(), result);
    expect(value).toMatchObject({ response: "sign", signature: "" });
    expect(inputOf(row(), value)).toEqual({ event: "ev", signer: "signer1" });
    expect(inputOf(row(), { ...value, response: "reject", signature: "s1" })).toEqual({ event: "ev", signer: "signer1", response: "reject", signature: "s1" });
    // A plain gate has no response to send.
    expect(inputOf(row({ responses: [], signatures: [] }), { ...value, response: "reject", signature: "s1" })).toEqual({ event: "ev", signer: "signer1" });
  });

  it("shows each signature's needed, counted, required and finished, and names a rejection in the trace", () => {
    const names = namesOf(process, new Map());
    const answer = {
      gates: [
        {
          gate: "g",
          transition: "t",
          signatures: [],
          satisfied: false,
          stages: [
            { stage: "st", complete: false, signatures: [{ signature: "s1", needed: true, counted: 1, required: 2, finished: ["ann"], complete: false }] },
          ],
        },
        { gate: "plain", transition: "u", signatures: [], satisfied: false },
      ],
    } as unknown as SimulationResult;
    const rows = approvalRows(answer, process, names);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      name: "Release",
      stages: [{ name: "Review", signatures: [{ name: "Finance", counted: 1, required: 2, finished: ["ann"] }] }],
    });
    expect(inputLabel({ event: "ev", response: "reject", actor: "x" }, names)).toBe("approve (reject) as x");
  });
});
