// The Trigger points tab's pure model (trigger-points-design.md section 11): the rows, the edits, the order, the narrowing lists, what a
// deleted state or transition takes with it, and the quick fixes of MQ8101 to MQ8107.
import { describe, expect, it } from "vitest";
import { deleteState, dropTriggerPointRefs, type Json, type ProcessDoc } from "@/model/process";
import { deriveQuickFix } from "@/problems/quickFix";
import {
  addTriggerPoint,
  allowsReplace,
  editTriggerPoint,
  moveTriggerPoint,
  removeTriggerPoint,
  setTriggerSets,
  setTriggerWhere,
  triggerPointRows,
  triggerPointsLostWithStates,
  whereKeysFor,
  TEAM_CODE,
  type TriggerNames,
} from "@/model/triggerPoints";

const names: TriggerNames = {
  state: (id) => `S:${id}`,
  transition: (id) => `T:${id}`,
  entity: (id) => `E:${id}`,
  field: (id) => `F:${id}`,
  verb: (key) => key.toUpperCase(),
  expression: (tree) => (tree && typeof tree.text === "string" ? tree.text : ""),
};

const make = (): ProcessDoc =>
  ({
    kind: "process",
    id: "P",
    name: "Order",
    use: "lifecycle",
    states: [
      { id: "S1", name: "Draft" },
      { id: "S2", name: "Issued" },
    ],
    transitions: [{ id: "T1", source: "S1", event: "E1", targets: ["S2"] }],
    guards: [{ id: "G1", name: "IsPaid" }],
    triggerPoints: [
      { id: "A", name: "Number", type: "action", timing: "before", verbs: ["create"], sets: [{ id: "X1", attribute: "N", value: { text: "'x'" } }] },
      { id: "B", name: "Positive", type: "check", verbs: ["modify"], attributes: ["N"], condition: { text: "subject.n > 0" }, description: "Must be positive" },
      { id: "C", name: "Portal", type: "action", timing: "after-commit", verbs: ["promote"], states: ["S1"], targets: ["S2"], outside: true },
      { id: "D", name: "Archive", type: "replace", verbs: ["delete"] },
    ],
  }) as unknown as ProcessDoc;

describe("the grid's rows", () => {
  it("one row per trigger point in file order, with the cells of its type", () => {
    const rows = triggerPointRows(make(), names);
    expect(rows.map((r) => r.name)).toEqual(["Number", "Positive", "Portal", "Archive"]);
    expect(rows.map((r) => r.typeLabel)).toEqual(["Action", "Check", "Action", "Replace"]);
    // Timing only on actions, severity only on checks.
    expect(rows.map((r) => r.timing)).toEqual(["Before checks", "", "After commit", ""]);
    expect(rows.map((r) => r.severity)).toEqual(["", "Error", "", ""]);
    expect(rows[1]).toMatchObject({ verbs: "MODIFY", what: "subject.n > 0", why: "Must be positive", pointer: "/triggerPoints/1" });
  });

  it("shows the sets of an action as what it does, and the team's code when the body is empty", () => {
    const rows = triggerPointRows(make(), names);
    expect(rows[0]).toMatchObject({ what: "F:N = 'x'", teamCode: false });
    expect(rows[2]).toMatchObject({ what: TEAM_CODE, teamCode: true, outside: true });
    expect(rows[3].what).toBe(TEAM_CODE);
  });

  it("lists the places it narrows to as chips, in one order", () => {
    const rows = triggerPointRows(make(), names);
    expect(rows[0].whereParts).toEqual([]);
    expect(rows[1].whereParts.map((p) => [p.key, p.names])).toEqual([["attributes", "F:N"]]);
    expect(rows[2].whereParts.map((p) => [p.label, p.names])).toEqual([
      ["In states", "S:S1"],
      ["Into states", "S:S2"],
    ]);
    expect(rows[2].where).toBe("in states S:S1 · into states S:S2");
  });

  it("marks a warning check", () => {
    const process = make();
    editTriggerPoint(process, "B", "severity", "warning");
    expect(triggerPointRows(process, names)[1].severity).toBe("Warning");
  });
});

describe("adding, removing and moving", () => {
  it("adds a check on modify with a name no point or guard has, after the row asked for", () => {
    const process = make();
    const id = addTriggerPoint(process, {}, "A");
    const list = process.triggerPoints!;
    expect(list[1]).toMatchObject({ id, type: "check", verbs: ["modify"], name: "TriggerPoint" });
    expect(addTriggerPoint(process)).not.toBe(id);
    expect(list[list.length - 1].name).toBe("TriggerPoint2");
    // A guard's name is taken too (MQ3001).
    process.guards!.push({ id: "G2", name: "TriggerPoint3" } as never);
    addTriggerPoint(process);
    expect(process.triggerPoints!.at(-1)!.name).toBe("TriggerPoint4");
  });

  it("an action starts before the checks", () => {
    const process = make();
    addTriggerPoint(process, { type: "action", verbs: ["create"] });
    expect(process.triggerPoints!.at(-1)).toMatchObject({ type: "action", timing: "before" });
  });

  it("removes a point and the key with the last one", () => {
    const process = make();
    for (const id of ["A", "B", "C"]) removeTriggerPoint(process, id);
    expect(process.triggerPoints!.map((t) => t.id)).toEqual(["D"]);
    removeTriggerPoint(process, "D");
    expect(process.triggerPoints).toBeUndefined();
  });

  it("moves a point to an index: the file order is the run order", () => {
    const process = make();
    expect(moveTriggerPoint(process, "D", 0)).toBe(true);
    expect(process.triggerPoints!.map((t) => t.id)).toEqual(["D", "A", "B", "C"]);
    expect(moveTriggerPoint(process, "D", 3)).toBe(true);
    expect(process.triggerPoints!.map((t) => t.id)).toEqual(["A", "B", "C", "D"]);
    expect(moveTriggerPoint(process, "A", 0)).toBe(false);
    expect(moveTriggerPoint(process, "A", 9)).toBe(false);
    expect(moveTriggerPoint(process, "nope", 1)).toBe(false);
  });
});

describe("edits", () => {
  it("renames, and refuses an empty name", () => {
    const process = make();
    expect(editTriggerPoint(process, "A", "name", " Numbering ")).toBe(true);
    expect(process.triggerPoints![0].name).toBe("Numbering");
    expect(editTriggerPoint(process, "A", "name", " ")).toBe(false);
  });

  it("Does takes several verbs and refuses none; a replace refuses a verb that does not allow it", () => {
    const process = make();
    expect(editTriggerPoint(process, "B", "verbs", "modify,read")).toBe(true);
    expect(process.triggerPoints![1].verbs).toEqual(["modify", "read"]);
    expect(editTriggerPoint(process, "B", "verbs", "")).toBe(false);
    expect(editTriggerPoint(process, "D", "verbs", "delete,copy")).toBe(true);
    expect(editTriggerPoint(process, "D", "verbs", "delete,modify")).toBe(false);
    expect(process.triggerPoints![3].verbs).toEqual(["delete", "copy"]);
  });

  it("offers a replace only for delete, disconnect, copy and revise", () => {
    expect(allowsReplace(["delete", "disconnect", "copy", "revise"])).toBe(true);
    expect(allowsReplace(["delete", "modify"])).toBe(false);
    expect(allowsReplace(["promote"])).toBe(false);
    expect(allowsReplace([])).toBe(false);
    const process = make();
    expect(editTriggerPoint(process, "B", "type", "replace")).toBe(false);
    expect(editTriggerPoint(process, "B", "verbs", "copy")).toBe(true);
    expect(editTriggerPoint(process, "B", "type", "replace")).toBe(true);
  });

  it("changing the type takes out the keys the new type does not have", () => {
    const process = make();
    expect(editTriggerPoint(process, "B", "type", "action")).toBe(true);
    expect(process.triggerPoints![1]).toMatchObject({ type: "action", timing: "before" });
    expect(process.triggerPoints![1]).not.toHaveProperty("condition");
    expect(editTriggerPoint(process, "A", "type", "check")).toBe(true);
    expect(process.triggerPoints![0]).not.toHaveProperty("timing");
    expect(process.triggerPoints![0]).not.toHaveProperty("sets");
    editTriggerPoint(process, "A", "severity", "warning");
    expect(editTriggerPoint(process, "A", "type", "action")).toBe(true);
    expect(process.triggerPoints![0]).not.toHaveProperty("severity");
    expect(editTriggerPoint(process, "D", "type", "action")).toBe(true);
    expect(process.triggerPoints![3]).toMatchObject({ type: "action", timing: "before" });
  });

  it("a replace has no timing", () => {
    const process = make();
    expect(editTriggerPoint(process, "C", "verbs", "delete")).toBe(true);
    expect(editTriggerPoint(process, "C", "type", "replace")).toBe(true);
    expect(process.triggerPoints![2]).not.toHaveProperty("timing");
    expect(editTriggerPoint(process, "C", "timing", "before")).toBe(false);
  });

  it("timing belongs to actions and severity to checks", () => {
    const process = make();
    expect(editTriggerPoint(process, "A", "timing", "with-change")).toBe(true);
    expect(process.triggerPoints![0].timing).toBe("with-change");
    expect(editTriggerPoint(process, "A", "timing", "someday")).toBe(false);
    expect(editTriggerPoint(process, "B", "timing", "before")).toBe(false);
    expect(editTriggerPoint(process, "A", "severity", "warning")).toBe(false);
    expect(editTriggerPoint(process, "B", "severity", "warning")).toBe(true);
    expect(editTriggerPoint(process, "B", "severity", "error")).toBe(true);
    expect(process.triggerPoints![1]).not.toHaveProperty("severity");
  });

  it("Only if, a check's condition, Why and Outside write and clear their keys", () => {
    const process = make();
    editTriggerPoint(process, "A", "when", " subject.n = 1 ");
    expect(process.triggerPoints![0].when).toEqual({ text: "subject.n = 1" });
    editTriggerPoint(process, "A", "when", "");
    expect(process.triggerPoints![0]).not.toHaveProperty("when");
    editTriggerPoint(process, "B", "condition", "");
    expect(process.triggerPoints![1]).not.toHaveProperty("condition");
    expect(editTriggerPoint(process, "A", "condition", "true")).toBe(false);
    editTriggerPoint(process, "B", "why", "");
    expect(process.triggerPoints![1]).not.toHaveProperty("description");
    editTriggerPoint(process, "D", "outside", true);
    expect(process.triggerPoints![3].outside).toBe(true);
    editTriggerPoint(process, "D", "outside", false);
    expect(process.triggerPoints![3]).not.toHaveProperty("outside");
  });

  it("sets belong to actions and replaces; none removes the key", () => {
    const process = make();
    expect(setTriggerSets(process, "B", [])).toBe(false);
    expect(setTriggerSets(process, "D", [{ id: "X9", attribute: "N", value: { text: "1" } as Json }])).toBe(true);
    expect(process.triggerPoints![3].sets).toHaveLength(1);
    expect(setTriggerSets(process, "D", [])).toBe(true);
    expect(process.triggerPoints![3]).not.toHaveProperty("sets");
  });
});

describe("the Where popover", () => {
  it("writes the lists it was given and removes the key of an empty one", () => {
    const process = make();
    setTriggerWhere(process, "C", { states: [], targets: ["S1", "S2"], entities: ["E"] });
    expect(process.triggerPoints![2]).not.toHaveProperty("states");
    expect(process.triggerPoints![2]).toMatchObject({ targets: ["S1", "S2"], entities: ["E"] });
  });

  it("offers only the lists a verb leaves room for", () => {
    expect(whereKeysFor(["modify"])).toEqual(["states", "entities", "attributes", "ends"]);
    expect(whereKeysFor(["promote"])).toEqual(["states", "targets", "transitions", "entities"]);
    expect(whereKeysFor(["connect"])).toEqual(["states", "entities", "ends"]);
    expect(whereKeysFor(["read", "connect"])).toEqual(["states", "entities", "attributes", "ends"]);
    // An own verb may be sent by an event, so a move can narrow it.
    expect(whereKeysFor(["escalate"])).toContain("targets");
    expect(whereKeysFor(["sign"])).toContain("transitions");
    expect(whereKeysFor(["delete"])).toEqual(["states", "entities"]);
  });
});

describe("what a delete takes with it", () => {
  it("a deleted state leaves the lists, and the point goes with its last state or target", () => {
    const process = make();
    dropTriggerPointRefs(process, { states: new Set(["S1"]) });
    expect(process.triggerPoints!.map((t) => t.id)).toEqual(["A", "B", "D"]);
    const other = make();
    dropTriggerPointRefs(other, { states: new Set(["S2"]) });
    expect(other.triggerPoints!.map((t) => t.id)).toEqual(["A", "B", "D"]);
    const both = make();
    both.triggerPoints![2].states = ["S1", "S2"];
    dropTriggerPointRefs(both, { states: new Set(["S1"]) });
    expect(both.triggerPoints![2].states).toEqual(["S2"]);
  });

  it("a deleted transition leaves transitions, and the point goes with its last one", () => {
    const process = make();
    process.triggerPoints![2].transitions = ["T1", "T2"];
    dropTriggerPointRefs(process, { transitions: new Set(["T1"]) });
    expect(process.triggerPoints![2].transitions).toEqual(["T2"]);
    dropTriggerPointRefs(process, { transitions: new Set(["T2"]) });
    expect(process.triggerPoints!.map((t) => t.id)).toEqual(["A", "B", "D"]);
  });

  it("the last trigger point going takes the key", () => {
    const process = make();
    process.triggerPoints = [process.triggerPoints![2]];
    dropTriggerPointRefs(process, { states: new Set(["S1"]) });
    expect(process.triggerPoints).toBeUndefined();
  });

  it("deleting a state does it, and names the points it takes", () => {
    const process = make();
    expect(triggerPointsLostWithStates(process, new Set(["S1"]))).toEqual(["Portal"]);
    expect(deleteState(process, "S2").ok).toBe(false);
    const free = make();
    free.transitions = [];
    expect(deleteState(free, "S1").ok).toBe(true);
    expect(free.triggerPoints!.map((t) => t.name)).toEqual(["Number", "Positive", "Archive"]);
  });

  it("deleting the state a transition leaves takes the transitions it names", () => {
    const process = make();
    process.triggerPoints![2] = { ...process.triggerPoints![2], states: undefined, targets: undefined, transitions: ["T1"] };
    process.states.push({ id: "S3", name: "Void" } as never);
    process.transitions!.push({ id: "T3", source: "S3", event: "E1", targets: ["S2"] } as never);
    process.triggerPoints![2].transitions = ["T3"];
    expect(deleteState(process, "S3").ok).toBe(true);
    expect(process.triggerPoints!.map((t) => t.id)).toEqual(["A", "B", "D"]);
  });
});

describe("the quick fixes", () => {
  const diag = (rule: string, pointer: string) => ({
    rule,
    severity: "error" as const,
    message: "",
    elementId: "P",
    filePath: null,
    jsonPointer: pointer,
    line: null,
    column: null,
  });
  const fix = (rule: string, pointer: string, process: ProcessDoc = make()) =>
    deriveQuickFix(diag(rule, pointer), "x", (id) => (id === "P" ? (process as never) : undefined));
  const points = (f: ReturnType<typeof fix>) => ((f as { json: ProcessDoc }).json.triggerPoints ?? []) as unknown as Record<string, unknown>[];

  it("MQ8101 removes the key", () => {
    const f = fix("MQ8101", "/triggerPoints/0/timing");
    expect(f).toMatchObject({ kind: "update", id: "P", label: "Remove the key" });
    expect(points(f)[0]).not.toHaveProperty("timing");
    const orchestration = { ...make(), use: undefined } as ProcessDoc;
    expect(points(fix("MQ8101", "/triggerPoints", orchestration))).toEqual([]);
  });

  it("MQ8102 removes the entry, and a point with it when it was the last", () => {
    const process = make();
    process.triggerPoints![2].states = ["S1", "S9"];
    const one = fix("MQ8102", "/triggerPoints/2/states/1", process);
    expect(points(one)[2].states).toEqual(["S1"]);
    const only = fix("MQ8102", "/triggerPoints/1/attributes/0");
    expect(points(only).map((t) => t.id)).toEqual(["A", "C", "D"]);
    expect(points(fix("MQ8102", "/triggerPoints/1/attributes"))[1]).not.toHaveProperty("attributes");
  });

  it("MQ8103 makes the point an action", () => {
    const f = fix("MQ8103", "/triggerPoints/3/type");
    expect(points(f)[3]).toMatchObject({ type: "action", timing: "with-change" });
    expect((f as { label: string }).label).toBe("Make it an action");
  });

  it("MQ8105 and MQ8106 change the timing", () => {
    expect(points(fix("MQ8105", "/triggerPoints/2/timing"))[2].timing).toBe("with-change");
    expect(points(fix("MQ8106", "/triggerPoints/0/timing"))[0].timing).toBe("after-commit");
  });

  it("MQ8107 removes the point", () => {
    expect(points(fix("MQ8107", "/triggerPoints/2")).map((t) => t.id)).toEqual(["A", "B", "D"]);
  });
});
