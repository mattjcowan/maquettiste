// The mock's reduced interpreter (src/mocks/model/stepper.ts) over small charts: parallel regions and done, guards
// with expressions and assumptions, a gate with two signers, a timer, a refusal per reason, shallow and deep history,
// a choice, invokes; and its agreement with the engine's recording of the processes fixture.
import { describe, expect, it } from "vitest";
import { clockText, durationMs, replay, simulate } from "@/mocks/model/stepper";
import { MockBackend } from "@/mocks/backend";
import { BUDGET_HOLDER, BUDGET_REJECTED, PURCHASE_APPROVAL } from "@/mocks/model/processSeed";
import recorded from "@/mocks/recorded/simulateProcess.budget-rejected.json";

type Json = Record<string, unknown>;

/** A process document from short ids: every id is its own name, so expectations read as names. */
function chart(parts: Partial<Json> & { states: Json[] }): Json {
  return { kind: "process", id: "P", name: "P", ...parts };
}
const st = (id: string, extra: Json = {}): Json => ({ id, name: id, ...extra });
const tr = (id: string, source: string, extra: Json = {}): Json => ({ id, source, ...extra });
const ev = (name: string, extra: Json = {}): Json => ({ id: name, name, ...extra });
const configAfter = (doc: Json, steps: Json[], start?: Json) => simulate(doc, start as never, steps).trace.map((t) => t.configuration);

describe("durations and the clock", () => {
  it("reads ISO 8601 durations as the engine does and writes the clock without a zero fraction", () => {
    expect(durationMs("PT30M")).toBe(30 * 60_000);
    expect(durationMs("P1DT2H")).toBe(26 * 3_600_000);
    expect(durationMs("P1M")).toBe(30 * 86_400_000);
    expect(durationMs("PT1.5S")).toBe(1500);
    expect(durationMs("P")).toBeNull();
    expect(durationMs("5 days")).toBeNull();
    expect(clockText(Date.parse("2000-01-01T00:00:00Z"))).toBe("2000-01-01T00:00:00Z");
    expect(clockText(Date.parse("2000-01-01T00:00:00.250Z"))).toBe("2000-01-01T00:00:00.25Z");
  });
});

describe("parallel regions and done", () => {
  const doc = chart({
    events: [ev("x"), ev("y")],
    states: [
      st("Par", {
        type: "parallel",
        states: [
          st("A", { type: "compound", states: [st("a1"), st("a2", { type: "final" })] }),
          st("B", { type: "compound", states: [st("b1"), st("b2", { type: "final" })] }),
        ],
      }),
      st("Z", { type: "final" }),
    ],
    transitions: [
      tr("t1", "a1", { event: "x", targets: ["a2"] }),
      tr("t2", "b1", { event: "y", targets: ["b2"] }),
      tr("t3", "Par", { trigger: "done", targets: ["Z"] }),
    ],
  });

  it("enters every region, waits for each to be final, then takes the parallel state's done", () => {
    const result = simulate(doc, null, [{ event: "x" }, { event: "y" }]);
    expect(result.trace.map((t) => t.configuration)).toEqual([["a1", "b1"], ["a2", "b1"], ["Z"]]);
    expect(result.trace[0].microsteps[0].entered).toEqual(["Par", "A", "a1", "B", "b1"]);
    expect(result.trace[2].microsteps.map((m) => m.transitions)).toEqual([["t2"], ["t3"]]);
    expect(result.final).toBe(true);
    expect(result.enabled).toEqual([]);
  });

  it("refuses an event no active state handles, and one after the instance is final", () => {
    const result = simulate(doc, null, [{ event: "y" }, { event: "y" }, { event: "x" }, { event: "x" }]);
    expect(result.trace.map((t) => [t.accepted, t.refusal])).toEqual([
      [true, null],
      [true, null],
      [false, "no-transition"],
      [true, null],
      [false, "no-transition"],
    ]);
  });
});

describe("guards", () => {
  const doc = chart({
    context: [{ id: "amount", name: "amount", type: "decimal", default: 10 }],
    events: [ev("go"), ev("check")],
    guards: [
      { id: "big", name: "big", expression: "context.amount > 100" },
      { id: "gStub", name: "stub" },
    ],
    actions: [
      { id: "double", name: "double", expression: "({ amount: context.amount * 2 })" },
      { id: "log", name: "log" },
    ],
    states: [st("Idle"), st("Large"), st("Small"), st("Done")],
    transitions: [
      tr("t1", "Idle", { event: "check", guard: "big", targets: ["Large"] }),
      tr("t2", "Idle", { event: "check", targets: ["Small"], actions: ["double", "log"] }),
      tr("t3", "Small", { event: "go", guard: "gStub", targets: ["Done"] }),
    ],
  });

  it("evaluates an expression over the context and takes the first transition whose guard holds", () => {
    const small = simulate(doc, null, [{ event: "check" }]);
    expect(small.trace[1].guards).toEqual([{ guard: "big", transition: "t1", result: false, source: "expression" }]);
    expect(small.configuration).toEqual(["Small"]);
    expect(small.trace[1].microsteps[0].actions).toEqual([
      { action: "double", source: "expression", changed: { amount: 20 } },
      { action: "log", source: "stub", changed: {} },
    ]);
    expect(small.trace[1].changed).toEqual({ amount: 20 });
    const large = simulate(doc, { context: { amount: 500 } }, [{ event: "check" }]);
    expect(large.configuration).toEqual(["Large"]);
  });

  it("asks for a stub guard's result: missing stops the replay, assume decides it", () => {
    const missing = simulate(doc, null, [{ event: "check" }, { event: "go" }, { event: "go" }]);
    expect(missing.trace).toHaveLength(3);
    expect(missing.trace[2]).toMatchObject({ accepted: false, refusal: "guard", incomplete: true });
    expect(missing.trace[2].guards).toEqual([{ guard: "gStub", transition: "t3", result: null, source: "missing" }]);
    expect(missing.diagnostics.map((d) => d.rule)).toEqual(["MQ9306"]);
    const before = simulate(doc, null, [{ event: "check" }]);
    expect(before.enabled.find((e) => e.event === "go")).toMatchObject({ guardUnknown: true, transitions: ["t3"] });
    expect(configAfter(doc, [{ event: "check" }, { event: "go", assume: { gStub: true } }]).at(-1)).toEqual(["Done"]);
    const no = simulate(doc, null, [{ event: "check" }, { event: "go", assume: { gStub: false } }]);
    expect(no.trace[2]).toMatchObject({ accepted: false, refusal: "guard", incomplete: false });
    expect(no.trace[2].guards[0].source).toBe("assumed");
    // Guard names resolve to ids in `assume`, as the engine resolves them.
    expect(configAfter(doc, [{ event: "check" }, { event: "go", assume: { stub: true } }])).toEqual([["Idle"], ["Small"], ["Done"]]);
  });
});

describe("gates", () => {
  const doc = chart({
    events: [ev("approve", { actors: [] })],
    states: [st("Review"), st("Approved")],
    transitions: [
      tr("t1", "Review", {
        event: "approve",
        targets: ["Approved"],
        gate: {
          id: "g",
          name: "g",
          required: 2,
          signers: ["Finance", "Legal"],
          requiredActors: ["Legal"],
          reasonRequired: true,
          meanings: [{ id: "m", name: "approved" }],
        },
      }),
    ],
  });
  const sign = (actor: string | null, signer: string | null, reason: string | null) => ({ event: "approve", actor, signer, reason });

  it("counts signatures until N with every required actor, refusing each bad attempt with its reason", () => {
    const result = simulate(doc, null, [
      sign("Stranger", "eve", "x"),
      sign("Finance", "ann", null),
      sign("Finance", "ann", "ok"),
      sign("Finance", "ann", "again"),
      sign("Finance", "bob", "ok"),
      sign("Legal", "lea", "ok"),
    ]);
    expect(result.trace.slice(1).map((t) => t.refusal)).toEqual(["gate-signer", "gate-reason", null, "gate-repeat", null, null]);
    expect(result.trace.slice(1).map((t) => t.configuration)).toEqual([["Review"], ["Review"], ["Review"], ["Review"], ["Review"], ["Approved"]]);
    // Two signatures but no Legal one: not satisfied yet.
    expect(result.trace[5].audit.map((a) => a.outcome)).toEqual(["signed"]);
    expect(result.trace[6].audit.map((a) => [a.outcome, a.signer, a.meaning])).toEqual([
      ["signed", "lea", "m"],
      ["completed", "lea", "m"],
    ]);
    expect(result.trace[1].audit[0]).toMatchObject({ outcome: "refused", instance: "simulation", gate: "g", transition: "t1", sequence: 1 });
  });

  it("reports gate progress in enabled and the signatures in gates", () => {
    const result = simulate(doc, null, [sign("Finance", "ann", "ok")]);
    expect(result.enabled).toEqual([
      { trigger: "event", event: "approve", invoke: null, transitions: ["t1"], actors: ["Finance", "Legal"], guardUnknown: false, gate: { have: 1, need: 2 } },
    ]);
    expect(result.gates).toEqual([
      { gate: "g", transition: "t1", signatures: [{ signer: "ann", actor: "Finance", meaning: "m", reason: "ok" }], satisfied: false },
    ]);
  });
});

describe("timers", () => {
  const doc = chart({
    context: [{ id: "reminders", name: "reminders", type: "int32", default: 0 }],
    actions: [{ id: "remind", name: "remind", expression: "({ reminders: context.reminders + 1 })" }],
    states: [st("Waiting"), st("Late")],
    transitions: [
      tr("t1", "Waiting", { trigger: "after", after: "PT1H", actions: ["remind"] }),
      tr("t2", "Waiting", { trigger: "after", after: "PT2H", targets: ["Late"] }),
    ],
  });

  it("schedules on entry and fires every due timer in due order on a time input", () => {
    const first = simulate(doc, { at: "2024-05-01T08:00:00Z" }, []);
    expect(first.timers).toEqual([
      { transition: "t1", dueAt: "2024-05-01T09:00:00Z" },
      { transition: "t2", dueAt: "2024-05-01T10:00:00Z" },
    ]);
    expect(first.enabled).toEqual([{ trigger: "time", event: null, invoke: null, transitions: ["t1", "t2"], actors: [], guardUnknown: false, gate: null }]);
    const half = simulate(doc, { at: "2024-05-01T08:00:00Z" }, [{ input: "time", after: "PT30M" }]);
    expect(half.clock).toBe("2024-05-01T08:30:00Z");
    expect(half.trace[1].microsteps).toEqual([]);
    const all = simulate(doc, { at: "2024-05-01T08:00:00Z" }, [{ input: "time", after: "PT3H" }]);
    expect(all.trace[1].microsteps.map((m) => m.transitions)).toEqual([["t1"], ["t2"]]);
    expect(all.context).toEqual({ reminders: 1 });
    expect(all.configuration).toEqual(["Late"]);
    expect(all.clock).toBe("2024-05-01T11:00:00Z");
    expect(all.timers).toEqual([]);
  });

  it("refuses a time input without a positive duration", () => {
    const bad = simulate(doc, null, [{ input: "time", after: "soon" }]);
    expect(bad.trace[1]).toMatchObject({ accepted: false, refusal: "no-transition", incomplete: true });
    expect(bad.diagnostics[0].rule).toBe("MQ9305");
  });
});

describe("refusals, actors and invokes", () => {
  const doc = chart({
    events: [ev("submit", { actors: ["Holder"] })],
    states: [
      st("Draft"),
      st("Checking", {
        invoke: [
          { id: "check", name: "check", type: "service" },
          { id: "review", name: "review", type: "human-task", actors: ["Approver"] },
        ],
      }),
      st("Ok"),
      st("Failed"),
    ],
    transitions: [
      tr("t1", "Draft", { event: "submit", targets: ["Checking"] }),
      tr("t2", "Checking", { trigger: "invoke-done", invoke: "check", targets: ["Ok"] }),
      tr("t3", "Checking", { trigger: "invoke-error", invoke: "check", targets: ["Failed"] }),
      tr("t4", "Checking", { trigger: "invoke-done", invoke: "review", targets: ["Ok"] }),
    ],
  });

  it("refuses an event from an actor it does not accept", () => {
    const result = simulate(doc, null, [{ event: "submit", actor: "Other" }, { event: "submit" }, { event: "submit", actor: "Holder" }]);
    expect(result.trace.slice(1).map((t) => t.refusal)).toEqual(["actor", "actor", null]);
  });

  it("keeps invokes pending on entry and completes them with invoke-done or invoke-error inputs", () => {
    const entered = simulate(doc, null, [{ event: "submit", actor: "Holder" }]);
    expect(entered.pending).toEqual([
      { invoke: "check", state: "Checking" },
      { invoke: "review", state: "Checking" },
    ]);
    expect(entered.enabled.map((e) => [e.trigger, e.invoke, e.transitions, e.actors])).toEqual([
      ["invoke-done", "check", ["t2"], []],
      ["invoke-error", "check", ["t3"], []],
      ["invoke-done", "review", ["t4"], ["Approver"]],
      ["invoke-error", "review", [], ["Approver"]],
    ]);
    expect(
      configAfter(doc, [
        { event: "submit", actor: "Holder" },
        { input: "invoke-error", invoke: "check" },
      ]).at(-1),
    ).toEqual(["Failed"]);
    const human = simulate(doc, null, [
      { event: "submit", actor: "Holder" },
      { input: "invoke-done", invoke: "review" },
      { input: "invoke-done", invoke: "review", actor: "Approver" },
    ]);
    expect(human.trace.slice(2).map((t) => t.refusal)).toEqual(["actor", null]);
    expect(human.pending).toEqual([]);
    const notPending = simulate(doc, null, [{ input: "invoke-done", invoke: "check" }]);
    expect(notPending.trace[1]).toMatchObject({ refusal: "no-transition", incomplete: true });
  });
});

describe("history and choice", () => {
  const doc = (history: "shallow" | "deep") =>
    chart({
      events: [ev("next"), ev("inner"), ev("leave"), ev("back")],
      states: [
        st("C", {
          type: "compound",
          states: [st("c1"), st("c2", { type: "compound", states: [st("x1"), st("x2")] }), st("H", { type: "history", history })],
        }),
        st("Out"),
      ],
      transitions: [
        tr("t1", "c1", { event: "next", targets: ["c2"] }),
        tr("t2", "x1", { event: "inner", targets: ["x2"] }),
        tr("t3", "C", { event: "leave", targets: ["Out"] }),
        tr("t4", "Out", { event: "back", targets: ["H"] }),
      ],
    });
  const walk = [{ event: "next" }, { event: "inner" }, { event: "leave" }, { event: "back" }];

  it("restores the last active child (shallow) or the last active leaves (deep)", () => {
    expect(configAfter(doc("shallow"), walk)).toEqual([["c1"], ["x1"], ["x2"], ["Out"], ["x1"]]);
    expect(configAfter(doc("deep"), walk)).toEqual([["c1"], ["x1"], ["x2"], ["Out"], ["x2"]]);
  });

  it("enters the default target when no history is recorded", () => {
    const d = doc("shallow");
    (d.states as Json[]).push(st("Side"));
    (d.transitions as Json[]).push(tr("t5", "Side", { event: "back", targets: ["H"] }));
    d.initial = "Side";
    expect(configAfter(d, [{ event: "back" }])).toEqual([["Side"], ["c1"]]);
  });

  it("resolves a choice state through its always transitions, the unguarded last one as else", () => {
    const choice = chart({
      context: [{ id: "n", name: "n", type: "int32", default: 1 }],
      events: [ev("go")],
      guards: [{ id: "many", name: "many", expression: "context.n > 5" }],
      states: [st("Start"), st("Pick", { type: "choice" }), st("Many"), st("Few")],
      transitions: [
        tr("t1", "Start", { event: "go", targets: ["Pick"] }),
        tr("t2", "Pick", { trigger: "always", guard: "many", targets: ["Many"] }),
        tr("t3", "Pick", { trigger: "always", targets: ["Few"] }),
      ],
    });
    expect(configAfter(choice, [{ event: "go" }]).at(-1)).toEqual(["Few"]);
    expect(configAfter(choice, [{ event: "go" }], { context: { n: 9 } }).at(-1)).toEqual(["Many"]);
  });

  it("stops an eventless loop after 1,000 microsteps (MQ9507)", () => {
    const loop = chart({
      states: [st("A"), st("B")],
      transitions: [tr("t1", "A", { trigger: "always", targets: ["B"] }), tr("t2", "B", { trigger: "always", targets: ["A"] })],
    });
    const result = simulate(loop, null, []);
    expect(result.diagnostics.map((d) => d.rule)).toEqual(["MQ9507"]);
    expect(result.trace[0].incomplete).toBe(true);
  });
});

describe("the fixture's PurchaseApproval", () => {
  const backend = new MockBackend();
  const doc = backend.model.get(PURCHASE_APPROVAL)!.json as Json;
  const scenario = backend.model.get(BUDGET_REJECTED)!.json as { start: Json; steps: Json[] };

  it("gives the engine's recorded trace for BudgetRejected", () => {
    const result = simulate(doc, scenario.start, scenario.steps);
    const engine = recorded as unknown as { trace: { configuration: string[]; microsteps: unknown[]; context: Json; clock: string }[] };
    expect(result.trace.map((t) => t.configuration)).toEqual(engine.trace.map((t) => t.configuration));
    expect(result.trace.map((t) => t.microsteps)).toEqual(engine.trace.map((t) => t.microsteps));
    expect(result.trace.map((t) => t.context)).toEqual(engine.trace.map((t) => t.context));
    expect(result.configuration).toEqual((recorded as { configuration: string[] }).configuration);
  });

  it("observes each step's expectation and the outcome, as record fills them", () => {
    const observed = replay(doc, null, [
      { event: "submit", actor: BUDGET_HOLDER },
      { event: "submit", actor: BUDGET_HOLDER },
    ]);
    expect(observed.complete).toBe(true);
    expect(observed.expects).toEqual([
      { states: ["01JQSTA0000000000000000104", "01JQSTA0000000000000000108"] },
      { accepted: false, states: ["01JQSTA0000000000000000104", "01JQSTA0000000000000000108"] },
    ]);
    expect(observed.outcome).toBe("active");
  });
});
