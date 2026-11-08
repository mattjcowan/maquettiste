// The editor's part 2 of sign-off, second wave (signoff-design.md section 11): the scenario cells for delegation and notices, the
// simulation panel's rounds, delegation and notices, the replay failure for notices, and who may delegate.
import { describe, expect, it } from "vitest";
import { approvalRows, defaultRowValue, inputOf, lastStepLines, namesOf, type EnabledRow } from "@/editors/process/simulation/model";
import type { SimulationResult, StepTrace } from "@/editors/process/simulation/api";
import { failureSentence } from "@/editors/scenario/replayFailure";
import { gateDelegatorIds, hasNotices, mayDelegate } from "@/model/approvals";
import { RIGHT_LABELS as L, SIMULATION_LABELS as SL } from "@/model/labels";
import { stepRows, type ProcessDoc, type ScenarioDoc } from "@/model/process";

const process = {
  kind: "process",
  id: "P",
  name: "P",
  use: "lifecycle",
  events: [
    { id: "ev", name: "approve" },
    { id: "ev2", name: "other" },
  ],
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
          {
            id: "st",
            name: "Review",
            signatures: [
              { id: "s1", name: "Finance", signers: ["x"], delegateTo: ["y"], remindAfter: "PT1H" },
              { id: "s2", name: "Manager", signers: ["z"] },
            ],
          },
        ],
      },
    },
    { id: "u", source: "A", event: "ev2" },
  ],
} as unknown as ProcessDoc;

describe("who may delegate, and which notices exist", () => {
  it("reads delegateTo and the notice durations from the approvals", () => {
    expect(mayDelegate(process)).toBe(true);
    expect(mayDelegate(process, "ev")).toBe(true);
    expect(mayDelegate(process, "ev2")).toBe(false);
    expect(hasNotices(process)).toBe(true);
    const plain = { transitions: [{ id: "t", gate: { id: "g", stages: [{ id: "s", signatures: [{ id: "a", signers: ["x"] }] }] } }] } as unknown as ProcessDoc;
    expect(mayDelegate(plain)).toBe(false);
    expect(hasNotices(plain)).toBe(false);
  });

  it("lists only the signers of a signature that may be delegated, for who can do what", () => {
    const gate = (process.transitions![0] as unknown as { gate: Record<string, unknown> }).gate;
    expect(gateDelegatorIds(gate)).toEqual(["x"]);
    expect(gateDelegatorIds({ signers: ["x"] })).toEqual([]);
  });
});

describe("a scenario step that delegates", () => {
  const scenario = {
    kind: "scenario",
    id: "S",
    name: "S",
    process: "P",
    participants: [
      { id: "pa", name: "Ann" },
      { id: "pb", name: "Bob" },
    ],
    steps: [
      { id: "1", event: "ev", response: "delegate", delegateTo: "pb", participant: "pa" },
      { id: "2", event: "ev", onBehalfOf: "pa", participant: "pb", expect: { notices: [{ signature: "s1", kind: "remind" }] } },
    ],
  } as unknown as ScenarioDoc;

  it("prints the participants and the expected notices by name", () => {
    const rows = stepRows(scenario, process, new Map());
    expect(rows[0]).toMatchObject({ response: "delegate", delegateTo: "Bob", onBehalfOf: "" });
    expect(rows[1]).toMatchObject({ delegateTo: "", onBehalfOf: "Ann", notices: "Finance remind" });
  });

  it("says a failed notices expectation in plain words, naming the signatures", () => {
    const failure = {
      step: 1,
      rule: "MQ9311",
      message: "Step 2: differ",
      expected: { notices: [{ signature: "s1", kind: "remind" }] },
      actual: { notices: [] },
    };
    expect(failureSentence(failure, undefined, (id) => (id === "s1" ? "Finance" : null))).toBe(L.failedNotices("Step 2", "Finance remind", "no notice"));
  });
});

describe("the simulation panel: rounds, delegation and notices", () => {
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
      responses: ["sign", "reject", "ignore", "withdraw", "delegate"],
      signatures: [{ id: "s1", name: "Finance" }],
      assume: [],
      checks: [],
      ...extra,
    }) as EnabledRow;
  const result = { final: false, clock: "", enabled: [], gates: [], timers: [] } as unknown as SimulationResult;

  it("raises delegate with the person it goes to, and an answer with the person it is on behalf of, never both", () => {
    const value = defaultRowValue(row(), result);
    expect(inputOf(row(), { ...value, response: "delegate", delegateTo: "pb", onBehalfOf: "pa" })).toEqual({
      event: "ev",
      signer: "signer1",
      response: "delegate",
      delegateTo: "pb",
    });
    expect(inputOf(row(), { ...value, onBehalfOf: "pa" })).toEqual({ event: "ev", signer: "signer1", onBehalfOf: "pa" });
    // No delegate among the responses: neither key goes.
    expect(inputOf(row({ responses: ["sign", "reject"] }), { ...value, onBehalfOf: "pa" })).toEqual({ event: "ev", signer: "signer1" });
  });

  it("shows the current round and each stage's round and Skipped", () => {
    const answer = {
      gates: [
        {
          gate: "g",
          transition: "t",
          signatures: [],
          satisfied: false,
          stages: [
            { stage: "st", complete: true, round: 1, signatures: [] },
            { stage: "st2", complete: true, round: 2, skipped: true, signatures: [] },
            { stage: "st3", complete: false, round: 3, signatures: [] },
          ],
        },
      ],
    } as unknown as SimulationResult;
    const [a] = approvalRows(answer, process, namesOf(process, new Map()));
    expect(a.round).toBe(3);
    expect(a.stages.map((s) => [s.round, s.skipped ?? false])).toEqual([
      [1, false],
      [2, true],
      [3, false],
    ]);
  });

  it("words the delegated audit, the audit's on behalf of, and the due notices of a time step", () => {
    expect(SL.auditLine("Release", "delegated", "ann", "Finance", "", "Finance", { delegateTo: "dan" })).toBe(
      "Release, Finance: ann delegated to dan as Finance",
    );
    expect(SL.auditLine("Release", "signed", "dan", "Finance", "", "Finance", { onBehalfOf: "ann" })).toContain("by dan on behalf of ann as Finance");
    const entry = {
      index: 1,
      input: { input: "time", after: "PT2H" },
      accepted: true,
      refusal: null,
      audit: [],
      microsteps: [],
      guards: [],
      changed: {},
      diagnostics: [],
      notices: [{ signature: "s1", kind: "escalate", recipients: ["y"] }],
    } as unknown as StepTrace;
    const lines = lastStepLines(entry, namesOf(process, new Map()));
    expect(lines.find((l) => l.kind === "notice")?.text).toContain("escalation due");
  });
});
