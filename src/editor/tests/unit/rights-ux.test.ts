// The rights UX fixes of the browser pass: refusal and verb lines of the Last step, a failed replay in plain words, and the
// extra quick fixes of MQ9904 and MQ9905.
import { describe, expect, it } from "vitest";
import type { Diagnostic } from "@/api/types";
import { failureSentence } from "@/editors/scenario/replayFailure";
import { lastStepLines, namesOf } from "@/editors/process/simulation/model";
import type { StepTrace } from "@/editors/process/simulation/api";
import type { ProcessDoc } from "@/model/process";
import { extraQuickFixes } from "@/problems/quickFix";

const RIGHT = "01JR000000000000000000001";
const process = {
  id: "P",
  name: "Invoice",
  events: [{ id: "E1", name: "issue" }],
  rights: [{ id: RIGHT, name: "NoDelete", access: "deny", verbs: ["delete"], description: "An issued invoice is voided." }],
  states: [],
} as unknown as ProcessDoc;
const names = namesOf(process, new Map([["A", "Approver"]]));
const trace = (extra: Partial<StepTrace>): StepTrace =>
  ({ index: 1, accepted: true, microsteps: [], guards: [], audit: [], changed: {}, diagnostics: [], configuration: [], ...extra }) as unknown as StepTrace;

describe("the Last step of a refusal and of a verb", () => {
  it("names the refusing right once, with its Why, and leaves it out of the rights that answered", () => {
    const lines = lastStepLines(
      trace({ accepted: false, refusal: "right", refusedBy: RIGHT, rights: [{ right: RIGHT, access: "deny", lifecycle: "P" }] } as never),
      names,
    );
    const refusal = lines.find((l) => l.kind === "refusal")!;
    expect(refusal.element).toEqual({ id: RIGHT, name: "NoDelete" });
    expect(refusal.why).toBe("An issued invoice is voided.");
    expect(refusal.text).not.toContain("NoDelete");
    expect(lines.some((l) => l.kind === "right")).toBe(false);
  });

  it("says a verb that was done, as the event line does", () => {
    const lines = lastStepLines(trace({ input: { input: "verb", verb: "modify", actor: "A" }, actor: "A" } as never), names);
    expect(lines.find((l) => l.kind === "verb")?.text).toBe("Did modify as Approver");
  });
});

describe("a failed replay in plain words", () => {
  const name = (id: string) => (id === RIGHT ? "NoDelete" : null);
  it("says what the step was expected to do and what happened instead", () => {
    const failure = { step: 1, rule: "MQ9301", message: "Step 2 is accepted while expect.accepted is false; fix", expected: false, actual: true };
    expect(failureSentence(failure, { expect: { accepted: false, refusal: "right", refusedBy: RIGHT } }, name)).toBe(
      "Step 2 was expected to be refused by NoDelete (Right), but in the replay it is accepted.",
    );
    expect(failureSentence({ ...failure, expected: true, actual: false }, {}, name)).toBe(
      "Step 2 was expected to be accepted, but in the replay it is refused.",
    );
  });

  it("compares the refusals of MQ9308, and reads a refuser that is gone as (deleted)", () => {
    const failure = {
      step: 0,
      rule: "MQ9308",
      message: "Step 1: differs",
      expected: { refusal: "right", refusedBy: "GONE" },
      actual: { refusal: "default", refusedBy: RIGHT },
    };
    expect(failureSentence(failure, undefined, name)).toBe(
      "Step 1 was expected to be refused by (deleted) (Right), but in the replay it is refused by NoDelete (Default).",
    );
  });

  it("keeps the engine's words for other failures, and names an outcome failure", () => {
    expect(failureSentence({ step: 2, rule: "MQ9302", message: "Step 3: states differ" }, undefined, name)).toBe("Step 3 (MQ9302): states differ");
    expect(failureSentence({ step: -1, rule: "MQ9304", message: "the outcome differs" }, undefined, name)).toBe("Outcome (MQ9304): the outcome differs");
  });
});

describe("the other quick fixes", () => {
  const d = (rule: string, jsonPointer: string): Diagnostic => ({ rule, severity: "warning", message: "", jsonPointer, elementId: "P" }) as Diagnostic;
  const doc = (id: string) => (id === "P" ? ({ kind: "process", id: "P", name: "Invoice", events: [{ id: "E1", name: "issue" }] } as never) : undefined);

  it("MQ9904 may set the default to deny", () => {
    const [fix] = extraQuickFixes(d("MQ9904", "/rights/0"), doc);
    expect(fix).toMatchObject({ kind: "update", id: "P", label: "Set the default to deny" });
    expect((fix as { json: { rightsDefault?: string } }).json.rightsDefault).toBe("deny");
  });

  it("MQ9905 may open the event", () => {
    expect(extraQuickFixes(d("MQ9905", "/events/0"), doc)).toEqual([{ kind: "open", process: "P", tab: "events", node: "E1", label: "Open the event" }]);
    expect(extraQuickFixes(d("MQ9901", "/rights/0"), doc)).toEqual([]);
  });
});
