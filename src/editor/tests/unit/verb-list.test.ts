// The verb picker's order per place, its hidden filter, the suggestions and the quick fixes of MQ9701 to MQ9706.
import { describe, expect, it } from "vitest";
import { builtInVerbs, suggestedVerbKeys } from "@/model/verbs";
import { freeKey, labelOfKey, usageText, verbGroups, verbSuggestions, verbUsage, type OwnVerb } from "@/model/verbList";
import { deriveQuickFix } from "@/problems/quickFix";
import type { Diagnostic, ElementSummary, ModelJson } from "@/api/types";

const own: OwnVerb[] = [{ id: "01JVERB000000000000000E001", key: "escalate" }];
const labels = (place: Parameters<typeof verbGroups>[0], hidden: string[] = [], current?: string) =>
  verbGroups(place, own, new Set(hidden), current).map((g) => g.label);

describe("verb picker groups", () => {
  it("orders the groups by place", () => {
    expect(labels("lifecycle-event")).toEqual(["Moves", "Your verbs", "Signatures", "Tasks", "Objects", "Links"]);
    expect(labels("transition")).toEqual(labels("lifecycle-event"));
    expect(labels("orchestration-event")).toEqual(["Your verbs", "Tasks", "Moves", "Signatures", "Objects", "Links"]);
    expect(labels("operation")).toEqual(["Objects", "Links", "Your verbs", "Moves", "Signatures", "Tasks"]);
  });

  it("lists every built-in once and leaves out hidden ones, except the one in use", () => {
    const all = verbGroups("operation", [], new Set()).flatMap((g) => g.options.map((o) => o.key));
    expect(all).toEqual(expect.arrayContaining(builtInVerbs.map((v) => v.key)));
    expect(new Set(all).size).toBe(all.length);
    const some = verbGroups("operation", own, new Set(["lock", "unlock", "promote"])).flatMap((g) => g.options.map((o) => o.key));
    expect(some).not.toContain("lock");
    expect(some).not.toContain("promote");
    expect(verbGroups("operation", own, new Set(["promote"]), "promote").flatMap((g) => g.options.map((o) => o.key))).toContain("promote");
  });

  it("labels an own verb by its display name, else its key", () => {
    expect(labelOfKey("check-out")).toBe("Check out");
    expect(verbGroups("operation", own, new Set()).find((g) => g.id === "own")!.options[0]).toMatchObject({ key: "escalate", label: "Escalate", own: true });
  });
});

describe("suggestions and usage", () => {
  it("offers the suggested keys the project lacks and finds a free key", () => {
    expect(verbSuggestions([]).map((s) => s.key)).toEqual([...suggestedVerbKeys]);
    expect(verbSuggestions([{ id: "x", key: "schedule" }]).map((s) => s.key)).not.toContain("schedule");
    expect(freeKey("schedule", new Set(["schedule"]))).toBe("schedule-2");
  });

  it("counts processes and operations per verb", () => {
    const rows = [
      { kind: "process", verbs: ["promote", "escalate"] },
      { kind: "process", verbs: ["promote"] },
      { kind: "operation", verb: "promote" },
    ] as unknown as ElementSummary[];
    const usage = verbUsage(rows);
    expect(usageText(usage.get("promote"))).toBe("2 processes, 1 operation");
    expect(usageText(usage.get("escalate"))).toBe("1 process");
    expect(usageText(usage.get("lock"))).toBe("unused");
  });
});

describe("verb quick fixes", () => {
  const diag = (rule: string, elementId: string, jsonPointer: string): Diagnostic => ({
    rule,
    severity: "error",
    message: "",
    elementId,
    filePath: null,
    jsonPointer,
    line: null,
    column: null,
  });
  const process = {
    kind: "process",
    id: "P",
    events: [
      { id: "E1", name: "Submit" },
      { id: "E2", name: "Send", verb: "promote" },
    ],
    transitions: [
      { id: "T1", event: "E1", verb: "promote" },
      { id: "T2", event: "E2", verb: "demote" },
    ],
  } as unknown as ModelJson;

  it("MQ9701 adds the verb as an own verb", () => {
    const doc = { ...process, events: [{ id: "E1", name: "Submit", verb: "escalate" }] } as unknown as ModelJson;
    expect(deriveQuickFix(diag("MQ9701", "P", "/events/0/verb"), null, () => doc)).toMatchObject({ kind: "verb", action: "add", key: "escalate" });
  });

  it("MQ9705 moves the verb to an event with none, else clears it", () => {
    const moved = deriveQuickFix(diag("MQ9705", "P", "/transitions/0/verb"), null, () => process);
    expect(moved).toMatchObject({ kind: "update", label: "Move the verb to the event" });
    const json = (moved as unknown as { json: { events: { verb?: string }[]; transitions: { verb?: string }[] } }).json;
    expect(json.events[0].verb).toBe("promote");
    expect(json.transitions[0].verb).toBeUndefined();
    expect(deriveQuickFix(diag("MQ9705", "P", "/transitions/1/verb"), null, () => process)).toMatchObject({ kind: "update", label: "Clear the verb" });
  });

  it("MQ9706 and MQ9702 use the built-in verb; MQ9703 removes the entry; MQ9704 shows the verb", () => {
    const vocab = {
      kind: "verb-vocabulary",
      id: "V",
      verbs: [
        { id: "A", key: "setstate" },
        { id: "B", key: "promote" },
      ],
      hidden: ["x"],
    } as unknown as ModelJson;
    expect(deriveQuickFix(diag("MQ9706", "V", "/verbs/0/key"), null, () => vocab)).toMatchObject({ action: "use-built-in", key: "setstate", to: "set-state" });
    expect(deriveQuickFix(diag("MQ9702", "V", "/verbs/1/key"), null, () => vocab)).toMatchObject({ action: "use-built-in", key: "promote", to: "promote" });
    expect(deriveQuickFix(diag("MQ9703", "V", "/hidden/0"), null, () => vocab)).toMatchObject({ kind: "update", label: "Remove the entry" });
    const op = { kind: "operation", id: "O", verb: "lock" } as unknown as ModelJson;
    expect(deriveQuickFix(diag("MQ9704", "O", "/verb"), null, () => op)).toMatchObject({ action: "show", key: "lock" });
  });
});
