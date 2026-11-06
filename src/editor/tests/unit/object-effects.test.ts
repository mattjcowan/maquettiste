// Object effects in the editor (object-effects-design.md section 10): the Sets editor's pure side (the attributes a set
// may name, its edits, a value tree as text), the states a scenario object may be in, the expected values of a step, the
// mock's reading of sets, in_state, actor() and the trace members, its rules MQ9801 to MQ9803, MQ9307 in verify, the
// quick fixes and the simulation panel's lines.
import { describe, expect, it } from "vitest";
import {
  addSet,
  expectedValuesText,
  objectStatesText,
  removeSet,
  setSetAttribute,
  setSetValue,
  settableAttributes,
  setsText,
  stateChoices,
  statesOfLabels,
  treeText,
  type TreeNames,
} from "@/model/effects";
import type { ProcessDoc, ScenarioDoc } from "@/model/process";
import { stepRows } from "@/model/process";
import { lastStepLines, namesOf } from "@/editors/process/simulation/model";
import { setDiagnostics } from "@/mocks/model/effectRules";
import { evalTree, treeOfText, UNKNOWN } from "@/mocks/model/effects";
import { previewExpression } from "@/mocks/model/expressionPreview";
import { replay, simulate } from "@/mocks/model/stepper";
import { deriveQuickFix, hasQuickFix, removeCall } from "@/problems/quickFix";

type Json = Record<string, unknown>;

const names: TreeNames = {
  name: (id) => ({ by: "submittedBy", on: "submittedOn", amount: "amount" })[id as "by"],
  state: (id) => ({ active: "Employment.Active", leave: "Employment.OnLeave" })[id as "active"],
  subject: (id) => ["by", "on", "amount"].includes(id),
};

const process = (): ProcessDoc =>
  ({ kind: "process", id: "P", name: "P", states: [{ id: "A", name: "A" }], actions: [{ id: "act", name: "Record" }] }) as unknown as ProcessDoc;

describe("the attributes a set may name", () => {
  const a = (id: string, extra: Json = {}) => ({ id, name: id, ...extra });
  it("keeps what every subject holds, without derived attributes, collections, value objects and the bound attribute", () => {
    const one = [a("x"), a("d", { derived: { text: "x" } }), a("c", { collection: true }), a("v", { type: { ref: "VO" } }), a("s"), a("only1")];
    const two = [a("x"), a("d"), a("c"), a("v"), a("s")];
    const got = settableAttributes([one, two], new Set(["s"]), (id) => (id === "VO" ? "value-object" : undefined));
    expect(got.map((g) => g.id)).toEqual(["x"]);
    expect(settableAttributes([])).toEqual([]);
  });
});

describe("the edits of an action's sets", () => {
  const choices = [
    { id: "by", name: "submittedBy" },
    { id: "on", name: "submittedOn" },
  ];
  it("adds the first attribute not yet set, never the same twice, and removes the key with the last set", () => {
    const p = process();
    const first = addSet(p, "act", choices)!;
    addSet(p, "act", choices);
    expect(p.actions![0].sets!.map((s) => s.attribute)).toEqual(["by", "on"]);
    expect(addSet(p, "act", choices)).toBeNull();
    setSetAttribute(p, "act", first, "on");
    expect(p.actions![0].sets![0].attribute).toBe("on");
    setSetValue(p, "act", first, "  actor() ");
    expect(p.actions![0].sets![0].value).toEqual({ text: "actor()" });
    setSetValue(p, "act", first, "");
    expect(p.actions![0].sets![0].value).toEqual({ null: true });
    for (const s of [...p.actions![0].sets!]) removeSet(p, "act", s.id);
    expect(p.actions![0].sets).toBeUndefined();
  });
});

describe("a value tree as text", () => {
  it("prints the new nodes and the summary of an action's sets", () => {
    expect(treeText({ actor: true }, names)).toBe("actor()");
    expect(treeText({ op: "ne", args: [{ path: ["by"] }, { actor: true }] }, names)).toBe("subject.submittedBy <> actor()");
    expect(treeText({ inState: ["active", "leave"] }, names)).toBe("in_state(subject, Employment.Active, Employment.OnLeave)");
    expect(treeText({ text: "now()" }, names)).toBe("now()");
    expect(
      setsText(
        [
          { id: "1", attribute: "by", value: { actor: true } },
          { id: "2", attribute: "on", value: { now: true } },
        ],
        (id) => names.name(id) ?? "?",
        names,
      ),
    ).toBe("submittedBy ← actor(), submittedOn ← now()");
  });
});

describe("the states of a scenario object", () => {
  const employment = {
    kind: "process",
    id: "E",
    name: "Employment",
    states: [
      { id: "Act", name: "Active" },
      { id: "H", name: "H", type: "history" },
      { id: "L", name: "Left" },
    ],
  } as unknown as ProcessDoc;
  const groups = stateChoices([employment, { ...employment, id: "RUN", name: "Run" }], "RUN");
  it("offers the resting states of the lifecycles but the one the scenario runs, and reads labels back as ids", () => {
    expect(groups).toHaveLength(1);
    expect(groups[0].states.map((s) => s.label)).toEqual(["Employment.Active", "Employment.Left"]);
    expect(statesOfLabels("employment.active, Employment.Left, nope, Employment.Active", groups)).toEqual(["Act", "L"]);
    expect(objectStatesText(["L", "zzz"], groups)).toBe("Employment.Left, ?");
  });
});

describe("expected values", () => {
  it("shows a step's expected values by the subject's attribute names", () => {
    const sc = {
      kind: "scenario",
      id: "S",
      name: "S",
      process: "P",
      steps: [{ id: "1", expect: { values: { a1: "bob", a2: 3 } } }, { id: "2" }],
    } as unknown as ScenarioDoc;
    const rows = stepRows(sc, process(), new Map(), new Map([["a1", "submittedBy"]]));
    expect(rows.map((r) => r.values)).toEqual(["submittedBy=bob, a2=3", ""]);
    expect(expectedValuesText({ a: "x" })).toBe("a=x");
  });
});

// ---------------------------------------------------------------- the mock

const lifecycle = (): Json => ({
  kind: "process",
  id: "HR",
  name: "Employment",
  use: "lifecycle",
  states: [
    { id: "Active", name: "Active" },
    { id: "Left", name: "Left" },
  ],
});

/** A process with one subject whose attributes are `by` (string), `n` (int, default 1) and `m` (int, default 2). */
const run = (extra: Json = {}): Json => ({
  kind: "process",
  id: "P",
  name: "P",
  use: "lifecycle",
  subjects: [{ id: "se", entity: "Request" }],
  events: [{ id: "go", name: "go" }],
  actions: [
    {
      id: "swap",
      name: "swap",
      sets: [
        { id: "s1", attribute: "n", value: { path: ["m"] } },
        { id: "s2", attribute: "m", value: { path: ["n"] } },
        { id: "s3", attribute: "by", value: { actor: true } },
      ],
    },
    { id: "next", name: "next", sets: [{ id: "s4", attribute: "n", value: { op: "add", args: [{ path: ["n"] }, { value: 10 }] } }] },
  ],
  guards: [{ id: "g", name: "g", condition: { inState: ["Active"] } }],
  states: [
    { id: "A", name: "A" },
    { id: "B", name: "B" },
    { id: "C", name: "C" },
  ],
  transitions: [
    { id: "t1", source: "A", trigger: "event", event: "go", targets: ["B"], actions: ["swap"] },
    { id: "t2", source: "B", trigger: "event", event: "go", targets: ["C"], actions: ["next"], guard: "g" },
  ],
  ...extra,
});

const options = (objects: Json[] = [], lifecycles: Json[] = [lifecycle(), run()]) => ({
  attributesOf: () => [
    { id: "by", default: null },
    { id: "n", default: 1 },
    { id: "m", default: 2 },
  ],
  subjectEntity: "Request",
  lifecycles,
  actorName: (id: string) => (id === "actor-1" ? "Clerk" : undefined),
  ...(objects.length ? { objects, subject: String(objects[0].id) } : {}),
});

describe("the mock's sets, in_state and trace members", () => {
  it("applies the sets of an action together, from the values at its start, and reads the signer as actor()", () => {
    const result = simulate(run(), null, [{ event: "go", signer: "bob" }], options());
    const step = result.trace[1];
    expect(step.microsteps[0].actions[0]).toMatchObject({ action: "swap", source: "sets", sets: { n: 2, m: 1, by: "bob" } });
    expect(step.valuesChanged).toEqual({ by: "bob", m: 1, n: 2 });
    expect(step.notes).toBeUndefined();
    // The first entry changed nothing.
    expect(result.trace[0].valuesChanged).toBeUndefined();
  });

  it("falls back to the acting actor's name for actor(), and null when nobody acts", () => {
    const named = simulate(run(), null, [{ event: "go", actor: "actor-1" }], options());
    expect(named.trace[1].valuesChanged?.by).toBe("Clerk");
    const nobody = simulate(run(), null, [{ event: "go" }], options());
    expect(nobody.trace[1].valuesChanged).toEqual({ m: 1, n: 2 });
  });

  it("reads in_state of a linked lifecycle from the object's states, else assumes the initial state with a note", () => {
    const given = [{ id: "o1", name: "req", entity: "Request", states: ["Left"] }];
    const refused = simulate(run(), null, [{ event: "go" }, { event: "go" }], options(given));
    expect(refused.trace[2].accepted).toBe(false);
    // The subject's own object is also the one the lifecycle governs: Left is active, so Active is false.
    const assumed = simulate(run(), null, [{ event: "go" }, { event: "go" }], options([{ id: "o1", name: "req", entity: "Request" }]));
    expect(assumed.trace[2].accepted).toBe(true);
    expect(assumed.trace[2].notes?.[0]).toContain("initial state is assumed");
    expect(assumed.trace[2].valuesChanged).toEqual({ n: 12 });
  });

  it("does nothing, with a note, for a value outside what the mock reads", () => {
    const doc = run({ actions: [{ id: "swap", name: "swap", sets: [{ id: "s", attribute: "n", value: { text: "n + trim(by)" } }] }] });
    const trace = simulate(doc, null, [{ event: "go" }], options()).trace[1];
    expect(trace.valuesChanged).toBeUndefined();
    expect(trace.notes?.[0]).toContain("could not work out");
    expect(trace.microsteps[0].actions[0].source).toBe("sets");
  });

  it("records expect.values from a replay", () => {
    const replayed = replay(run(), null, [{ event: "go", signer: "bob" }], options());
    expect(replayed.expects[0].values).toEqual({ by: "bob", m: 1, n: 2 });
  });

  it("evaluates the closed subset and gives up on the rest", () => {
    const env = { path: () => 5, now: () => "t", actor: () => null, inState: () => null };
    expect(evalTree({ op: "and", args: [{ value: true }, { null: true }] }, env)).toBeNull();
    expect(evalTree({ op: "or", args: [{ value: true }, { null: true }] }, env)).toBe(true);
    expect(evalTree({ op: "coalesce", args: [{ actor: true }, { value: "x" }] }, env)).toBe("x");
    expect(evalTree({ call: "round", args: [{ value: 1 }] }, env)).toBe(UNKNOWN);
    expect(treeOfText("'it''s'")).toEqual({ value: "it's" });
    expect(treeOfText("a + b")).toBe(UNKNOWN);
  });

  it("previews text and stored sets, with no SQL for actor() and in_state", () => {
    const entries = [{ id: "P", json: run() }];
    const text = previewExpression(entries, { element: "swap", site: "set", attribute: "n", text: "actor()" });
    expect(text.preview).toMatchObject({ text: "actor()", type: "string", sql: null, answer: null });
    expect(previewExpression(entries, { element: "s4" }).preview?.text).toBe("n + 10");
    expect(previewExpression(entries, { element: "swap", site: "set", text: "(1" }).diagnostics[0].rule).toBe("MQ3101");
  });
});

describe("the mock's rules for sets", () => {
  const entity = {
    id: "Request",
    kind: "entity",
    name: "Request",
    attributes: [
      { id: "n", name: "n" },
      { id: "d", name: "d", derived: { text: "n" } },
      { id: "st", name: "st" },
    ],
  };
  const lookup = (id: string) => (id === "Request" ? (entity as Json) : undefined);
  const none = () => undefined;
  const doc = (sets: Json[], extra: Json = {}): Json => ({
    kind: "process",
    id: "P",
    name: "P",
    subjects: [{ id: "se", entity: "Request", boundAttribute: "st" }],
    actions: [{ id: "a", name: "a", sets }],
    ...extra,
  });
  const rules = (d: Json) => setDiagnostics(d, lookup, none, []).map((f) => `${f.rule} ${f.pointer}`);
  it("reports an unknown attribute, a derived one, a bound one and a second set, each at its pointer", () => {
    expect(rules(doc([{ id: "1", attribute: "n", value: { null: true } }]))).toEqual([]);
    expect(rules(doc([{ id: "1", attribute: "zzz", value: { null: true } }]))).toEqual(["MQ9801 /actions/0/sets/0/attribute"]);
    expect(
      rules(
        doc([
          { id: "1", attribute: "d", value: { null: true } },
          { id: "2", attribute: "st", value: { null: true } },
        ]),
      ),
    ).toEqual(["MQ9802 /actions/0/sets/0/attribute", "MQ9802 /actions/0/sets/1/attribute"]);
    expect(
      rules(
        doc([
          { id: "1", attribute: "n", value: { null: true } },
          { id: "2", attribute: "n", value: { null: true } },
        ]),
      ),
    ).toEqual(["MQ9803 /actions/0/sets/1"]);
  });
  it("reports a process with no subject and a context attribute that hides the object", () => {
    expect(rules(doc([{ id: "1", attribute: "n", value: { null: true } }], { subjects: [] }))).toEqual(["MQ9801 /actions/0/sets/0/attribute"]);
    expect(rules(doc([{ id: "1", attribute: "n", value: { null: true } }], { context: [{ id: "c", name: "subject" }] }))).toEqual([
      "MQ9801 /actions/0/sets/0/attribute",
    ]);
  });
  it("reports another lifecycle's bound attribute as MQ9802", () => {
    const other = { kind: "process", id: "Q", use: "lifecycle", subjects: [{ id: "x", entity: "Request", boundAttribute: "n" }] };
    expect(setDiagnostics(doc([{ id: "1", attribute: "n", value: { null: true } }]), lookup, none, [other]).map((f) => f.rule)).toEqual(["MQ9802"]);
  });
});

describe("quick fixes", () => {
  const element = {
    kind: "process",
    id: "P",
    name: "P",
    actions: [
      { id: "a", name: "a", sets: [{ id: "1", attribute: "n", value: { null: true } }] },
      {
        id: "b",
        name: "b",
        sets: [
          { id: "2", attribute: "n", value: { null: true } },
          { id: "3", attribute: "m", value: { null: true } },
        ],
      },
    ],
    guards: [{ id: "g", name: "g", condition: { op: "and", args: [{ inState: ["x"] }, { op: "eq", args: [{ path: ["n"] }, { value: 1 }] }] } }],
  };
  const finding = (rule: string, jsonPointer: string) => ({
    rule,
    severity: "error" as const,
    message: "m",
    elementId: "P",
    filePath: null,
    jsonPointer,
    line: null,
    column: null,
  });
  it("has a fix for each new rule, and removes the set whose pointer the finding names", () => {
    for (const rule of ["MQ9801", "MQ9802", "MQ9803", "MQ3116"]) expect(hasQuickFix(rule, null)).toBe(true);
    expect(hasQuickFix("MQ9307", "refresh-scenario")).toBe(true);
    const fix = deriveQuickFix(finding("MQ9803", "/actions/1/sets/0"), null, () => element as never);
    expect(fix).toMatchObject({ kind: "update", label: "Remove the set" });
    expect(((fix as { json: ProcessDoc }).json.actions![1].sets ?? []).map((s) => s.id)).toEqual(["3"]);
  });
  it("drops the sets key with the last set", () => {
    const fix = deriveQuickFix(finding("MQ9801", "/actions/0/sets/0/attribute"), null, () => element as never) as { json: ProcessDoc };
    expect(fix.json.actions![0].sets).toBeUndefined();
  });
  it("removes an in_state call from an and, leaving the other side", () => {
    const fix = deriveQuickFix(finding("MQ3116", "/guards/0/condition/args/0"), null, () => element as never) as { json: ProcessDoc };
    expect(fix.json.guards![0]).toEqual({ id: "g", name: "g", condition: { op: "eq", args: [{ path: ["n"] }, { value: 1 }] } });
  });
  it("has no safe removal inside another operator, and refreshes a scenario for MQ9307", () => {
    expect(
      removeCall({ ...element, guards: [{ id: "g", name: "g", condition: { op: "not", args: [{ inState: ["x"] }] } }] } as never, "/guards/0/condition/args/0"),
    ).toBeNull();
    const scenario = { kind: "scenario", id: "S" };
    const fix = deriveQuickFix({ ...finding("MQ9307", "/steps/0/expect/values"), elementId: "S" }, "refresh-scenario", () => scenario as never);
    expect(fix).toMatchObject({ kind: "operation", op: "refresh-scenario" });
  });
});

describe("the simulation panel's lines", () => {
  it("shows the sets that ran, the values the step changed and each note once", () => {
    const p = run() as unknown as ProcessDoc;
    const trace = simulate(run(), null, [{ event: "go", signer: "bob" }], options()).trace[1];
    const lines = lastStepLines({ ...trace, notes: ["a note", "a note"] } as never, namesOf(p, new Map()), (id) => ({ n: "count" })[id as "n"] ?? id);
    expect(lines.filter((l) => l.kind === "sets").map((l) => l.text)).toEqual(["Sets: by=bob, m=1, count=2"]);
    expect(lines.filter((l) => l.kind === "values").map((l) => l.text)).toEqual(["Values: by=bob, m=1, count=2"]);
    expect(lines.filter((l) => l.kind === "note").map((l) => l.text)).toEqual(["Note: a note"]);
    expect(lines.find((l) => l.kind === "action")?.text).toContain("(sets)");
  });
});
