// Processes linked to operations, editor part 2 (processes-operations-design.md sections 7, 8 and 12): the Calls dialog's conversions
// (a value given, left empty, not givable), the mock's rules MQ3032, MQ3044, MQ9024, MQ9025, MQ9026 and the library case of MQ9020, the
// trace's call, the `input` preview site, the quick fixes of MQ9025 and MQ9026 and the simulation panel's lines.
import { describe, expect, it } from "vitest";
import { callFromRows, callText, givenSources, inputRows, isMissing, memberGivable, memberTypeText, type GivenLookup } from "@/model/callInputs";
import type { CallDoc, ProcessDoc } from "@/model/process";
import { lastStepLines, namesOf, pendingRows } from "@/editors/process/simulation/model";
import { operationLinkDiagnostics, processLinkDiagnostics, type LinkLookup } from "@/mocks/model/operationRules";
import { applyRules } from "@/mocks/model/validate";
import { previewExpression } from "@/mocks/model/expressionPreview";
import { simulate } from "@/mocks/model/stepper";
import { deriveQuickFix, hasQuickFix } from "@/problems/quickFix";

type Json = Record<string, unknown>;

const names = { name: (id: string) => ({ days: "termsDays" })[id], state: () => undefined, subject: () => false };
const nameOf = (id: string) => ({ INV: "Invoice", Money: "Money" })[id];

const members: Json[] = [
  { id: "m-due", name: "dueDays", type: "int32", required: true },
  { id: "m-note", name: "note", type: "string" },
  { id: "m-doc", name: "attachment", file: true, required: true },
  { id: "m-many", name: "tags", type: "string", collection: true },
  { id: "m-inv", name: "invoice", keyOf: "INV", required: true },
  { id: "m-def", name: "currency", type: "string", required: true, default: "EUR" },
];

describe("the Calls dialog's rows and call", () => {
  it("reads one row per member, with the value the call already gives and the type in words", () => {
    const call: CallDoc = { operation: "OP", inputs: [{ id: "i1", member: "m-due", value: { path: ["days"] } }] };
    const rows = inputRows(members, call, names, nameOf);
    expect(rows.map((r) => [r.name, r.type, r.required, r.text])).toEqual([
      ["dueDays", "int32", true, "termsDays"],
      ["note", "string", false, ""],
      ["attachment", "file", true, ""],
      ["tags", "list of string", false, ""],
      ["invoice", "Invoice key", true, ""],
      ["currency", "string", true, ""],
    ]);
    expect(rows[0].inputId).toBe("i1");
    expect(memberTypeText({ type: { ref: "Money" } }, nameOf)).toBe("Money");
  });

  it("writes only the values given: empty is not given, an unchanged one keeps its stored tree and id", () => {
    const stored = { path: ["days"] };
    const call: CallDoc = { operation: "OP", inputs: [{ id: "i1", member: "m-due", value: stored }] };
    const rows = inputRows(members, call, names, nameOf);
    expect(callFromRows("OP", rows, () => "new")).toEqual({ operation: "OP", inputs: [{ id: "i1", member: "m-due", value: stored }] });
    rows[1] = { ...rows[1], text: " 'hello' " };
    rows[0] = { ...rows[0], text: "" };
    expect(callFromRows("OP", rows, () => "new")).toEqual({ operation: "OP", inputs: [{ id: "new", member: "m-note", value: { text: "'hello'" } }] });
    rows[0] = { ...rows[0], text: "30" };
    expect(callFromRows("OP", rows, () => "n2")!.inputs).toEqual([
      { id: "i1", member: "m-due", value: { text: "30" } },
      { id: "n2", member: "m-note", value: { text: "'hello'" } },
    ]);
  });

  it("has no inputs key when none is given, and no call without an operation", () => {
    expect(callFromRows("OP", inputRows(members, undefined, names, nameOf), () => "x")).toEqual({ operation: "OP" });
    expect(callFromRows(undefined, [], () => "x")).toBeNull();
  });

  it("marks files, collections and a record key with several attributes as supplied by the system, never written", () => {
    const rows = inputRows(members, undefined, names, nameOf, (m) => m.keyOf !== "INV");
    expect(rows.filter((r) => r.supplied).map((r) => r.name)).toEqual(["attachment", "tags", "invoice"]);
    expect(inputRows(members, undefined, names, nameOf)[4].supplied).toBe(false);
    const typed = rows.map((r) => ({ ...r, text: "1" }));
    expect(callFromRows("OP", typed, () => "n")!.inputs!.map((i) => i.member)).toEqual(["m-due", "m-note", "m-def"]);
  });

  it("shows MQ9026 for a required input with no default, that can be given, and is empty", () => {
    const rows = inputRows(members, undefined, names, nameOf);
    expect(rows.filter(isMissing).map((r) => r.name)).toEqual(["dueDays", "invoice"]);
  });

  it("prints a call on one line", () => {
    const call: CallDoc = { operation: "OP", inputs: [{ id: "i1", member: "m-due", value: { path: ["days"] } }] };
    expect(callText(call, "issue", (id) => (id === "m-due" ? "dueDays" : "?"), names)).toBe("issue(dueDays = termsDays)");
    expect(callText({ operation: "OP" }, "issue", () => "?", names)).toBe("issue()");
    expect(callText(undefined, undefined, () => "?", names)).toBe("");
  });
});

// ------------------------------------------------------------------ the mock's rules

const entity = (id: string, extra: Json = {}): Json => ({ kind: "entity", id, name: id, ...extra });
const docs: Record<string, Json> = {
  INV: entity("INV", { key: { attributes: ["k"] }, attributes: [{ id: "k", name: "id", type: "guid" }] }),
  PART: entity("PART", { base: "INV" }),
  OTHER: entity("OTHER", { key: { attributes: ["a", "b"] }, attributes: [] }),
  OP: {
    kind: "operation",
    id: "OP",
    name: "issue",
    subject: "INV",
    effect: "change",
    input: members.filter((m) => m.id !== "m-inv"),
    fires: ["E1"],
  },
  OP2: { kind: "operation", id: "OP2", name: "notify", subject: "OTHER", effect: "change", input: [{ id: "n1", name: "who", keyOf: "OTHER", required: true }] },
  OPC: { kind: "operation", id: "OPC", name: "bulk", subject: "OTHER", level: "collection", effect: "change", input: [] },
};
const proc = (extra: Json = {}): Json => ({
  kind: "process",
  id: "PR",
  name: "Life",
  use: "lifecycle",
  subjects: [{ id: "s", entity: "INV" }],
  events: [
    { id: "E1", name: "Issue", payload: [{ id: "p1", name: "dueDays", type: "int32", required: true }] },
    { id: "E2", name: "Void" },
    { id: "E3", name: "Raised" },
  ],
  states: [{ id: "S1", name: "A" }],
  transitions: [
    { id: "T1", source: "S1", trigger: "event", event: "E1", targets: ["S1"] },
    { id: "T2", source: "S1", trigger: "event", event: "E2", targets: ["S1"] },
    { id: "T3", source: "S1", trigger: "event", event: "E3", targets: ["S1"] },
  ],
  actions: [{ id: "A1", name: "RequestIssue", raises: ["E3"] }],
  ...extra,
});
const lookup = (process: Json): LinkLookup => {
  const all = [...Object.values(docs), process];
  return { get: (id) => all.find((d) => d.id === id), all: () => all, stereotype: () => undefined };
};
const withCall = (call: Json | undefined, extra: Json = {}) =>
  proc({ actions: [{ id: "A1", name: "RequestIssue", raises: ["E3"], ...(call ? { call } : {}) }], ...extra });
const all9024 = (process: Json) => processLinkDiagnostics(process, lookup(process));
const found = (process: Json, keep = false) =>
  all9024(process)
    .filter((f) => keep || f.rule !== "MQ9024")
    .map((f) => `${f.rule} ${f.severity} ${f.pointer}`);

describe("the mock's rules for calls", () => {
  it("MQ9026: a required input without a default left out, files and collections aside, a default or an optional one is fine", () => {
    expect(found(withCall({ operation: "OP", inputs: [] }))).toEqual(["MQ9026 warning /actions/0/call"]);
    expect(found(withCall({ operation: "OP", inputs: [{ id: "i", member: "m-due", value: { value: 30 } }] }))).toEqual([]);
  });

  it("MQ9025: a member that isn't an input, one twice, a file, a collection and a record key of several attributes", () => {
    const given = (...members: string[]) => withCall({ operation: "OP", inputs: members.map((m, i) => ({ id: `i${i}`, member: m, value: { value: 1 } })) });
    expect(found(given("nope", "m-due"))).toEqual(["MQ9025 error /actions/0/call/inputs/0/member"]);
    expect(found(given("m-due", "m-due"))).toEqual(["MQ9025 error /actions/0/call/inputs/1/member"]);
    expect(found(given("m-due", "m-doc", "m-many"))).toEqual(["MQ9025 error /actions/0/call/inputs/1/member", "MQ9025 error /actions/0/call/inputs/2/member"]);
    const composite = withCall({ operation: "OP2", inputs: [{ id: "i", member: "n1", value: { value: 1 } }] }, { subjects: [{ id: "s", entity: "OTHER" }] });
    expect(found(composite)).toEqual(["MQ9025 error /actions/0/call/inputs/0/member"]);
  });

  it("MQ9025 on an invoke that isn't a service step, and MQ3032 on a service step of another object", () => {
    const states = (type: string, operation: string) => [{ id: "S1", name: "A", invoke: [{ id: "V", name: "Charge", type, operation }] }];
    expect(found(proc({ states: states("process", "OP") }))).toEqual(["MQ9025 error /states/0/invoke/0/operation"]);
    expect(found(proc({ states: states("service", "OP") }))).toEqual([]);
    expect(found(proc({ states: states("service", "OP2") }))).toEqual(["MQ3032 warning /states/0/invoke/0/operation"]);
    expect(found(proc({ states: states("service", "OPC") }))).toEqual([]);
  });

  it("MQ3032 on a call: record-level operations of a subject the process doesn't govern, derived subjects match, no subjects no finding", () => {
    const call = (operation: string) => withCall({ operation, inputs: operation === "OP" ? [{ id: "i", member: "m-due", value: { value: 1 } }] : [] });
    expect(found(call("OP"))).toEqual([]);
    expect(found(call("OP2"))).toEqual(["MQ3032 warning /actions/0/call/operation"]);
    expect(found(call("OPC"))).toEqual([]);
    expect(found({ ...call("OP2"), subjects: [] })).toEqual([]);
    expect(found({ ...call("OP"), subjects: [{ id: "s", entity: "PART" }] })).toEqual([]);
  });

  it("MQ9020: a library's action with a call", () => {
    expect(found(withCall({ operation: "OP" }, { use: "library", subjects: [], states: [] }))).toEqual(["MQ9020 error /actions/0/call"]);
  });

  it("MQ9024: an event that a move uses and nothing raises or fires, reported as information and dropped unless validation.rules gives it a severity", () => {
    expect(found(proc(), true)).toEqual(["MQ9024 info /events/1"]);
    const d = processLinkDiagnostics(proc(), lookup(proc())).map((f) => ({ rule: f.rule, severity: f.severity }) as never);
    expect(applyRules(d, {})).toEqual([]);
    expect(applyRules(d, { MQ9024: "warning" }).map((x) => (x as Json).severity)).toEqual(["warning"]);
  });
});

describe("the mock's rules for an operation's fires", () => {
  const run = (operation: Json, process: Json = proc()) => {
    const l = lookup(process);
    return operationLinkDiagnostics(operation, { ...l, all: () => [...l.all(), operation] }).map((f) => `${f.rule} ${f.pointer}`);
  };
  it("MQ3044: a required payload attribute with no input of its name, or one of another type", () => {
    expect(run({ ...docs.OP, input: [{ id: "x", name: "dueDays", type: "int32" }] })).toEqual([]);
    expect(run({ ...docs.OP, input: [] })).toEqual(["MQ3044 /fires/0"]);
    expect(run({ ...docs.OP, input: [{ id: "x", name: "dueDays", type: "string" }] })).toEqual(["MQ3044 /fires/0"]);
    expect(run({ ...docs.OP, input: [{ id: "x", name: "dueDays", type: "int32", collection: true }] })).toEqual(["MQ3044 /fires/0"]);
  });
  it("MQ3032: an event of a process that governs other objects", () => {
    expect(run({ ...docs.OP, input: [{ id: "x", name: "dueDays", type: "int32" }], subject: "OTHER" })).toEqual(["MQ3032 /fires/0"]);
    expect(run({ ...docs.OP, input: [{ id: "x", name: "dueDays", type: "int32" }], subject: undefined })).toEqual([]);
  });
});

// ------------------------------------------------------------------ the trace, the preview, the quick fixes and the panel

describe("the trace records a call and runs nothing", () => {
  const doc = (): Json => ({
    kind: "process",
    id: "PR",
    name: "Life",
    use: "lifecycle",
    context: [{ id: "days", name: "termsDays", type: "int32" }],
    events: [{ id: "go", name: "go" }],
    actions: [
      {
        id: "act",
        name: "RequestIssue",
        call: {
          operation: "OP",
          inputs: [
            { id: "i1", member: "m-due", value: { value: 30 } },
            { id: "i2", member: "m-note", value: { text: "unreadable thing" } },
          ],
        },
      },
    ],
    states: [
      { id: "A", name: "A" },
      { id: "B", name: "B" },
    ],
    transitions: [{ id: "t1", source: "A", trigger: "event", event: "go", targets: ["B"], actions: ["act"] }],
  });

  it("records the operation and the input values by member, with the source staying stub and nothing changed", () => {
    const result = simulate(doc(), null, [{ event: "go" }]);
    const action = result.trace[1].microsteps[0].actions[0];
    expect(action).toMatchObject({ action: "act", source: "stub", changed: {}, call: { operation: "OP", inputs: { "m-due": 30, "m-note": null } } });
    expect(result.trace[1].notes?.some((n) => n.includes("could not work out"))).toBe(true);
    expect(result.trace[1].context).toEqual(result.trace[0].context);
  });

  it("leaves call out of an action with none", () => {
    const d = doc();
    delete (d.actions as Json[])[0].call;
    expect(simulate(d, null, [{ event: "go" }]).trace[1].microsteps[0].actions[0]).not.toHaveProperty("call");
  });

  it("shows the call in the last step and the operation of a pending service step", () => {
    const p = doc() as unknown as ProcessDoc;
    const n = namesOf(p, new Map(), [], new Map(), new Map(), {
      operation: new Map([
        ["OP", "issue"],
        ["OP9", "chargeCard"],
      ]),
      member: new Map([
        ["m-due", "dueDays"],
        ["m-note", "note"],
      ]),
    });
    const entry = simulate(doc(), null, [{ event: "go" }]).trace[1];
    const lines = lastStepLines(
      entry as never,
      n,
      (id) => id,
      () => [],
    );
    expect(lines.find((l) => l.kind === "call")?.text).toBe("called issue (dueDays = 30, note = null), recorded, not run");
    const service = {
      ...p,
      states: [
        {
          id: "A",
          name: "A",
          invoke: [
            { id: "V", name: "ChargeCard", type: "service", operation: "OP9" },
            { id: "W", name: "Plain", type: "service" },
          ],
        },
      ],
    } as unknown as ProcessDoc;
    const rows = pendingRows(
      {
        pending: [
          { invoke: "V", state: "A" },
          { invoke: "W", state: "A" },
        ],
      } as never,
      service,
      namesOf(service, new Map(), [], new Map(), new Map(), { operation: new Map([["OP9", "chargeCard"]]), member: new Map() }),
    );
    expect(rows.map((r) => r.label)).toEqual(["Waiting for ChargeCard: runs chargeCard", "Plain in A"]);
  });
});

describe("the input preview site", () => {
  const entries = [
    { id: "OP", json: docs.OP },
    { id: "PR", json: withCall({ operation: "OP", inputs: [{ id: "i1", member: "m-due", value: { value: 30 } }] }) },
  ];
  it("types text against the member's type, and a stored input by its member", () => {
    expect(previewExpression(entries, { element: "A1", site: "input", member: "m-due", text: "'x'" }).preview?.type).toBe("int32");
    expect(previewExpression(entries, { element: "A1", site: "input", member: "m-note", text: "5" }).preview?.type).toBe("string");
    const stored = previewExpression(entries, { element: "i1" });
    expect(stored.preview).toMatchObject({ text: "30", type: "int32" });
  });
});

describe("the quick fixes of MQ9025 and MQ9026", () => {
  const diag = (rule: string, jsonPointer: string) => ({ rule, severity: "error", message: "m", elementId: "PR", jsonPointer }) as never;
  const bad = withCall({
    operation: "OP",
    inputs: [{ id: "i1", member: "nope", value: { value: 1 } }],
  });
  it("remove the bad input, and clear a step's operation", () => {
    expect(hasQuickFix("MQ9025", null)).toBe(true);
    expect(hasQuickFix("MQ9026", null)).toBe(true);
    const fix = deriveQuickFix(diag("MQ9025", "/actions/0/call/inputs/0/member"), null, () => bad as never) as { kind: string; json: Json; label: string };
    expect(fix.label).toBe("Remove the input");
    expect(((fix.json.actions as Json[])[0].call as Json).inputs).toBeUndefined();
    const step = proc({ states: [{ id: "S1", name: "A", invoke: [{ id: "V", name: "C", type: "process", operation: "OP" }] }] });
    const clear = deriveQuickFix(diag("MQ9025", "/states/0/invoke/0/operation"), null, () => step as never) as { json: Json; label: string };
    expect(clear.label).toBe("Clear the step's operation");
    expect(((clear.json.states as Json[])[0].invoke as Json[])[0]).not.toHaveProperty("operation");
  });
  it("MQ3032 on a fires row stops firing that one event, and has no fix elsewhere", () => {
    const op = { kind: "operation", id: "OP", name: "issue", fires: ["E1", "E2"] };
    const d = (pointer: string) =>
      ({
        rule: "MQ3032",
        severity: "warning",
        message: "Event 'approved' of process 'P' is fired by operation 'issue', but",
        elementId: "OP",
        jsonPointer: pointer,
      }) as never;
    expect(hasQuickFix("MQ3032", null)).toBe(true);
    const fix = deriveQuickFix(d("/fires/0"), null, () => op as never) as { json: Json; label: string };
    expect(fix.label).toBe("Stop firing approved");
    expect(fix.json.fires).toEqual(["E2"]);
    const only = deriveQuickFix(d("/fires/0"), null, () => ({ ...op, fires: ["E1"] }) as never) as { json: Json };
    expect(only.json).not.toHaveProperty("fires");
    expect(deriveQuickFix(d("/actions/0/operation"), null, () => op as never)).toBeNull();
    expect(deriveQuickFix(d("/fires/0"), null, () => ({ kind: "process", id: "OP" }) as never)).toBeNull();
  });
  it("MQ9026 opens the action's Calls dialog", () => {
    const fix = deriveQuickFix(diag("MQ9026", "/actions/0/call"), null, () => withCall({ operation: "OP" }) as never);
    expect(fix).toMatchObject({ kind: "open", process: "PR", tab: "transitions", node: "A1" });
  });
});

describe("what a call can give (the dialog's mirror of the engine's member types)", () => {
  const model: Record<string, Json> = {
    BASE: { kind: "entity", id: "BASE", key: { attributes: ["k"] }, attributes: [{ id: "k", type: "int64" }] },
    KIDS: { kind: "entity", id: "KIDS", base: "BASE", attributes: [] },
    NOKEY: { kind: "entity", id: "NOKEY", abstract: true, attributes: [] },
    TWO: { kind: "entity", id: "TWO", key: { attributes: ["a", "b"] }, attributes: [] },
    BIN: { kind: "entity", id: "BIN", key: { attributes: ["k"] }, attributes: [{ id: "k", type: "binary" }] },
    SC: { kind: "scalar-type", id: "SC", base: "float" },
    OKSC: { kind: "scalar-type", id: "OKSC", base: "decimal" },
    EN: { kind: "enum", id: "EN" },
    VO: { kind: "value-object", id: "VO" },
  };
  const given: GivenLookup = { kindOf: (id) => model[id]?.kind as string, baseOf: (id) => model[id]?.base as string | undefined, doc: (id) => model[id] };
  const ok = (m: Json) => memberGivable(m, given);

  it("gives a value to typed members the language has, an enum, a key through a base; not to the rest", () => {
    expect(ok({ type: "int32" })).toBe(true);
    expect(ok({ type: { ref: "EN" } })).toBe(true);
    expect(ok({ type: { ref: "OKSC" } })).toBe(true);
    expect(ok({ keyOf: "KIDS" })).toBe(true);
    for (const m of [
      { type: "binary" },
      { type: "float" },
      { type: "duration" },
      { type: { ref: "VO" } },
      { type: { ref: "SC" } },
      { file: true },
      { type: "string", collection: true },
    ])
      expect(ok(m), JSON.stringify(m)).toBe(false);
    // A key of an entity with no key, a composite key, or a key of a type the language lacks.
    for (const entityId of ["NOKEY", "TWO", "BIN"]) expect(ok({ keyOf: entityId }), entityId).toBe(false);
  });

  it("names the documents it needs: a keyed entity with its bases, a scalar type, nothing else", () => {
    expect(givenSources([{ keyOf: "KIDS" }, { type: { ref: "SC" } }, { type: "int32" }, { type: { ref: "EN" } }], given).sort()).toEqual([
      "BASE",
      "KIDS",
      "SC",
    ]);
  });

  it("Apply keeps a stored input that has no row, and a stored input of a member that can't be given", () => {
    const rows = inputRows(members, undefined, names, nameOf);
    const stored: CallDoc = {
      operation: "OP",
      inputs: [
        { id: "gone", member: "no-longer", value: { text: "1" } },
        { id: "file", member: "m-doc", value: { text: "2" } },
        { id: "i1", member: "m-due", value: { text: "3" } },
      ],
    };
    const withStored = inputRows(members, stored, names, nameOf);
    expect(callFromRows("OP", withStored, () => "x", stored)!.inputs!.map((i) => i.id)).toEqual(["gone", "file", "i1"]);
    // Clearing an editable value removes it; a call to another operation keeps nothing of the old one.
    const cleared = withStored.map((r) => (r.member === "m-due" ? { ...r, text: "" } : r));
    expect(callFromRows("OP", cleared, () => "x", stored)!.inputs!.map((i) => i.id)).toEqual(["gone", "file"]);
    expect(callFromRows("OTHER", rows, () => "x", stored)).toEqual({ operation: "OTHER" });
  });
});

describe("the mock follows the engine on unusual members, repeats and stereotypes", () => {
  it("MQ9025 for a record key with no key and for a binary member; MQ9026 skips both", () => {
    const extra: Record<string, Json> = {
      LOOSE: entity("LOOSE", { abstract: true }),
      OPX: {
        kind: "operation",
        id: "OPX",
        name: "weird",
        subject: "INV",
        effect: "change",
        input: [
          { id: "k1", name: "loose", keyOf: "LOOSE", required: true },
          { id: "b1", name: "picture", type: "binary", required: true },
          { id: "v1", name: "when", type: "datetime", required: true },
        ],
      },
    };
    const run = (inputs: Json[]) => {
      const process = withCall({ operation: "OPX", inputs });
      const all = [...Object.values(docs), ...Object.values(extra), process];
      return processLinkDiagnostics(process, { get: (id) => all.find((d) => d.id === id), all: () => all, stereotype: () => undefined })
        .filter((f) => f.rule !== "MQ9024")
        .map((f) => `${f.rule} ${f.pointer}`);
    };
    expect(run([{ id: "i", member: "v1", value: { value: 1 } }])).toEqual([]);
    expect(
      run([
        { id: "i1", member: "k1", value: { value: 1 } },
        { id: "i2", member: "b1", value: { value: 1 } },
        { id: "i3", member: "v1", value: { value: 1 } },
      ]),
    ).toEqual(["MQ9025 /actions/0/call/inputs/0/member", "MQ9025 /actions/0/call/inputs/1/member"]);
    // Left out: only the one that can be given is asked for.
    expect(run([])).toEqual(["MQ9026 /actions/0/call"]);
  });

  it("judges a repeated fires id once", () => {
    const l = lookup(proc());
    const op = { ...docs.OP, input: [], fires: ["E1", "E1"] };
    const found = operationLinkDiagnostics(op, { ...l, all: () => [...l.all(), op] }).map((f) => f.pointer);
    expect(found).toEqual(["/fires/0"]);
  });

  it("MQ3032 reads a stereotype entry through package defaults, implied stereotypes and derived carriers", () => {
    const all: Json[] = [
      { kind: "package", id: "PK", name: "Pk", defaultStereotypes: ["tracked"] },
      { kind: "stereotype", id: "ST1", key: "tracked", appliesTo: ["entity"] },
      { kind: "stereotype", id: "ST2", key: "audited", appliesTo: ["entity"], implies: ["tracked"] },
      entity("CAR", { package: "PK" }),
      entity("BASEOP", { stereotypes: ["audited"] }),
      entity("KID", { base: "BASEOP" }),
      entity("NONE"),
    ];
    const l = (process: Json): LinkLookup => {
      const docsAll = [...all, process];
      return { get: (id) => docsAll.find((d) => d.id === id), all: () => docsAll, stereotype: () => undefined };
    };
    const process = (stereotype: string) => ({
      kind: "process",
      id: "P",
      name: "P",
      use: "lifecycle",
      subjects: [{ id: "s", stereotype }],
      events: [{ id: "EV", name: "Go" }],
    });
    const fires = (subject: string, stereotype: string) => {
      const p = process(stereotype);
      const op = { kind: "operation", id: "O", name: "o", subject, effect: "change", input: [], fires: ["EV"] };
      return operationLinkDiagnostics(op, { ...l(p), all: () => [...l(p).all(), op] }).map((f) => f.rule);
    };
    // CAR carries "tracked" by its package's default; BASEOP carries "audited", which implies "tracked"; KID is derived from a carrier.
    expect(fires("CAR", "tracked")).toEqual([]);
    expect(fires("BASEOP", "tracked")).toEqual([]);
    expect(fires("KID", "tracked")).toEqual([]);
    expect(fires("NONE", "tracked")).toEqual(["MQ3032"]);
    // An operation on a base of a carrier lines up with the stereotype entry too (the carrier is derived from the subject).
    expect(fires("BASEOP", "audited")).toEqual([]);
  });

  it("keeps the first input of a member, says the call was recorded not run, and leaves a repeat out of the trace", () => {
    const d: Json = {
      kind: "process",
      id: "PR",
      name: "Life",
      use: "lifecycle",
      events: [{ id: "go", name: "go" }],
      actions: [
        {
          id: "act",
          name: "RequestIssue",
          call: {
            operation: "OP",
            inputs: [
              { id: "i1", member: "m-due", value: { value: 30 } },
              { id: "i2", member: "m-due", value: { value: 99 } },
            ],
          },
        },
      ],
      states: [
        { id: "A", name: "A" },
        { id: "B", name: "B" },
      ],
      transitions: [{ id: "t1", source: "A", trigger: "event", event: "go", targets: ["B"], actions: ["act"] }],
    };
    const step = simulate(d, null, [{ event: "go" }]).trace[1];
    expect(step.microsteps[0].actions[0].call).toEqual({ operation: "OP", inputs: { "m-due": 30 } });
    expect(step.notes).toContain("Action 'RequestIssue' called an operation: recorded, not run.");
  });
});
