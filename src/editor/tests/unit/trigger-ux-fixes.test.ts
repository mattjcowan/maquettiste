// The browser pass's fixes on trigger points part 1: the quick fixes are offered, a replay failure names the check, and a step's
// checks that can warn are the ones that name its verb.
import { describe, expect, it } from "vitest";
import { failureSentence } from "@/editors/scenario/replayFailure";
import type { ProcessDoc, StepDoc, TriggerPointDoc } from "@/model/process";
import { canWarnAt, stepVerbs } from "@/model/triggerPoints";
import { hasQuickFix } from "@/problems/quickFix";

const names: Record<string, string> = { CL: "CreditLimit", AN: "AssignNumber" };
const name = (id: string) => names[id] ?? null;

describe("quick fixes of trigger points", () => {
  it("offers one for every rule that has a fix", () => {
    for (const rule of ["MQ8101", "MQ8102", "MQ8103", "MQ8105", "MQ8106", "MQ8107"]) expect(hasQuickFix(rule, null)).toBe(true);
  });
});

describe("a replay failure of a trigger point", () => {
  it("names the check that refused when the step assumes an error", () => {
    const failure = { step: 0, rule: "MQ9301", message: "Step 1 is refused while expect.accepted is true", expected: true, actual: false };
    expect(failureSentence(failure, { assume: { CL: "error" } }, name)).toBe(
      "Step 1 was expected to be accepted, but in the replay it is refused by CreditLimit (Check).",
    );
    expect(failureSentence(failure, { assume: { CL: "pass" } }, name)).toBe("Step 1 was expected to be accepted, but in the replay it is refused.");
  });

  it("says the difference in checks and trigger points in plain words", () => {
    const failure = {
      step: 0,
      rule: "MQ9310",
      message: "Step 1: the checks differ from expect.checkWarnings",
      expected: { checkWarnings: ["CL"], triggerPoints: null },
      actual: { checkWarnings: [], triggerPoints: ["AN"] },
    };
    expect(failureSentence(failure, undefined, name)).toBe("Step 1: The step was expected to warn from CreditLimit, but in the replay it warns from no check.");
    expect(failureSentence({ ...failure, expected: { checkWarnings: null, triggerPoints: ["AN", "CL"] } }, undefined, name)).toBe(
      "Step 1: It was expected to run AssignNumber, CreditLimit, but in the replay it runs AssignNumber.",
    );
  });
});

describe("the checks a step can warn from", () => {
  const point = (extra: Partial<TriggerPointDoc>) => ({ id: "x", name: "x", type: "check", verbs: ["modify"], ...extra }) as TriggerPointDoc;
  const verbs = stepVerbs({ events: [], transitions: [] } as unknown as ProcessDoc, { id: "s", input: "verb", verb: "modify" } as StepDoc);

  it("takes the verb of a verb step", () => {
    expect([...verbs]).toEqual(["modify"]);
  });

  it("keeps a warning that names the verb, and leaves out errors and other verbs", () => {
    expect(canWarnAt(point({ severity: "warning" }), verbs)).toBe(true);
    expect(canWarnAt(point({ severity: "error" }), verbs)).toBe(false);
    expect(canWarnAt(point({ severity: "warning", verbs: ["create"] }), verbs)).toBe(false);
    expect(canWarnAt(point({ type: "action" }), verbs)).toBe(false);
    expect(canWarnAt(point({ severity: "warning" }), new Set())).toBe(true);
  });
});
