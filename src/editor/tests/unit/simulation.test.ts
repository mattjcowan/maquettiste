// The simulation panel's client side (phase-3-design.md 6.4): the session (inputs, start, selection, delete from an
// input, reset, From scenario), the debounce and abort of the simulate calls (fake timers and a fake fetch that
// records each call's signal), the rows the panel derives from an answer (enabled rows and their default input, the
// last step's lines, the trace and a replay), and the request store the explorer writes.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, createApiClient, setApiClient } from "@/api/client";
import { clone } from "@/lib/json";
import { MockBackend } from "@/mocks/backend";
import type { MockModel } from "@/mocks/model/store";
import { simulate } from "@/mocks/model/stepper";
import { APPROVER, BUDGET_HOLDER, BUDGET_REJECTED, PURCHASE_APPROVAL, QUICK_APPROVAL } from "@/mocks/model/processSeed";
import type { ProcessDoc, ScenarioDoc } from "@/model/process";
import { simulateProcess, type SimulationResult } from "@/editors/process/simulation/api";
import {
  addInput,
  atEnd,
  clean,
  loadScenario,
  newSession,
  removeFrom,
  requestOf,
  resetSession,
  selectEntry,
  setStartValue,
  shownEntry,
  takenBy,
  viewOf,
  withAnswer,
  withResult,
} from "@/editors/process/simulation/session";
import { useMockApi } from "./harness";
import { SIMULATE_DEBOUNCE_MS, SimulationRunner } from "@/editors/process/simulation/useSimulation";
import {
  changesText,
  defaultRowValue,
  durationText,
  enabledRows,
  failureText,
  inputLabel,
  inputOf,
  isDuration,
  lastStepLines,
  mergeRowEdit,
  namesOf,
  nextDue,
  pendingRows,
  replayRows,
  replaySummary,
  traceRows,
} from "@/editors/process/simulation/model";
import { consumeSimulationRequest, requestSimulation, simulationRequest } from "@/editors/process/simulation/requests";

const backend = new MockBackend();
const purchase = () => clone(backend.model.get(PURCHASE_APPROVAL)!.json) as unknown as ProcessDoc;
const scenario = (id: string) => clone(backend.model.get(id)!.json) as unknown as ScenarioDoc;
const actorNames = new Map([
  [BUDGET_HOLDER, "BudgetHolder"],
  [APPROVER, "Approver"],
]);
const allActors = [...actorNames].map(([id, name]) => ({ id, name }));
const S = (n: number) => `01JQSTA0000000000000000${n}`;
const X = (n: number) => `01JQPRX00000000000000000${n}`;
/** The engine's answer for a start and inputs, from the mock's reduced interpreter (the tests need a real trace). */
const answer = (steps: object[], start: object | null = null) =>
  ({ processHash: "h", ...simulate(purchase() as never, start as never, steps as never) }) as unknown as SimulationResult;

describe("the session", () => {
  const submit = { event: X(14), actor: BUDGET_HOLDER };
  const done = { input: "invoke-done" as const, invoke: X(19) };

  it("adds inputs, selects a trace entry, deletes an input with everything after it, and resets", () => {
    let s = newSession(PURCHASE_APPROVAL);
    s = addInput(s, submit);
    s = addInput(s, done);
    s = addInput(s, { input: "invoke-done", invoke: X(20), actor: APPROVER });
    expect(s.inputs).toHaveLength(3);
    expect(atEnd(s)).toBe(true);
    s = selectEntry(s, 0);
    expect(s.selected).toBe(0);
    expect(atEnd(s)).toBe(false);
    // Selecting the last input follows the end again.
    expect(selectEntry(s, 2).selected).toBeNull();
    // Raising from an earlier entry branches there: the inputs after it are dropped.
    const branched = addInput(s, { event: X(15), actor: BUDGET_HOLDER });
    expect(branched.inputs.map((i) => i.event ?? i.invoke)).toEqual([X(14), X(15)]);
    expect(branched.selected).toBeNull();
    const deleted = removeFrom(s, 1);
    expect(deleted.inputs).toEqual([submit]);
    expect(deleted.result).toBeNull();
    expect(removeFrom(s, -1).inputs).toEqual([]);
    expect(resetSession(s).inputs).toEqual([]);
    expect(resetSession(s).start).toBe(s.start);
  });

  it("restarts on a start edit and loads a scenario's start and steps", () => {
    let s = addInput(newSession(PURCHASE_APPROVAL), submit);
    s = setStartValue(s, "01JQATT0000000000000000201", 900);
    expect(s.inputs).toEqual([]);
    expect(s.start.context).toEqual({ "01JQATT0000000000000000201": 900 });
    expect(setStartValue(s, "01JQATT0000000000000000201", undefined).start.context).toEqual({});
    const loaded = loadScenario(s, scenario(BUDGET_REJECTED));
    expect(loaded.start.context).toEqual({ "01JQATT0000000000000000201": 25000 });
    // Inputs carry no id, expectation or description, and no default `input`.
    expect(loaded.inputs).toEqual([submit, { input: "invoke-error", invoke: X(19) }, { input: "invoke-done", invoke: X(20), actor: APPROVER }]);
    expect(requestOf(loaded)).toEqual({ start: { context: { "01JQATT0000000000000000201": 25000 } }, steps: loaded.inputs, from: -1 });
    expect(requestOf(loaded, { kind: "process" }).document).toEqual({ kind: "process" });
    expect(clean({ id: "x", event: "e", payload: {}, assume: {}, reason: "" } as never)).toEqual({ event: "e" });
  });

  it("shows the entry selected (or the last) and publishes its configuration and the transitions it took", () => {
    let s = addInput(addInput(newSession(PURCHASE_APPROVAL), submit), { event: X(14), actor: BUDGET_HOLDER });
    expect(shownEntry(s)).toBeNull();
    expect(viewOf(s)).toBeNull();
    s = withResult(s, answer([submit, submit]));
    // The second submit is refused: nothing was taken.
    expect(viewOf(s)).toEqual({ process: PURCHASE_APPROVAL, configuration: [S(104), S(108)], taken: [], step: 1 });
    s = selectEntry(s, 0);
    expect(viewOf(s)).toEqual({ process: PURCHASE_APPROVAL, configuration: [S(104), S(108)], taken: ["01JQTRN0000000000000000015"], step: 0 });
    s = selectEntry(s, -1);
    expect(shownEntry(s)?.configuration).toEqual([S(101)]);
    expect(takenBy(null)).toEqual([]);
  });

  it("drops an answer asked for an older version of the inputs", () => {
    const first = addInput(newSession(PURCHASE_APPROVAL), submit);
    const asked = first.version;
    // The inputs change while the call is out: its answer describes the old list and is not shown.
    const second = removeFrom(first, 0);
    expect(second.version).toBeGreaterThan(asked);
    expect(withAnswer(second, asked, { kind: "result", result: answer([submit]) })).toBe(second);
    expect(withAnswer(second, asked, { kind: "error", message: "late" })).toBe(second);
    // The answer for the version asked is applied; a selection change keeps the version.
    const shown = withAnswer(second, second.version, { kind: "result", result: answer([]) });
    expect(shown.result?.trace).toHaveLength(1);
    expect(selectEntry(shown, -1).version).toBe(second.version);
    expect(withAnswer(second, second.version, { kind: "invalid", diagnostics: [] }).invalid).toEqual([]);
  });
});

describe("the simulate calls: debounce and abort", () => {
  interface Call {
    body: unknown;
    signal: AbortSignal;
    respond: (body: unknown, status?: number) => void;
  }
  let calls: Call[] = [];
  const previous = api();

  beforeEach(() => {
    vi.useFakeTimers();
    calls = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: Request) => {
        const body = JSON.parse(await input.clone().text());
        return new Promise<Response>((resolve, reject) => {
          const call: Call = {
            body,
            signal: input.signal,
            respond: (b, status = 200) => resolve(new Response(JSON.stringify(b), { status, headers: { "Content-Type": "application/json" } })),
          };
          input.signal.addEventListener("abort", () => reject(new DOMException("The call was aborted.", "AbortError")));
          calls.push(call);
        });
      }),
    );
    setApiClient(createApiClient("http://mock.invalid"));
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    setApiClient(previous);
  });

  const runner = (outcomes: unknown[], errors: Error[]) =>
    new SimulationRunner({
      call: (body, signal) => simulateProcess(PURCHASE_APPROVAL, body, signal),
      onOutcome: (o) => outcomes.push(o),
      onError: (e) => errors.push(e),
    });
  const flush = async () => {
    for (let i = 0; i < 5; i++) await Promise.resolve();
    await vi.advanceTimersByTimeAsync(0);
  };

  it("waits 150 ms after the last change and sends only the newest input list", async () => {
    const outcomes: unknown[] = [];
    const r = runner(outcomes, []);
    r.schedule({ steps: [{ event: "a" }] });
    await vi.advanceTimersByTimeAsync(SIMULATE_DEBOUNCE_MS - 50);
    r.schedule({ steps: [{ event: "a" }, { event: "b" }] });
    await vi.advanceTimersByTimeAsync(SIMULATE_DEBOUNCE_MS - 1);
    expect(calls).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1);
    await flush();
    expect(calls).toHaveLength(1);
    expect(calls[0].body).toEqual({ steps: [{ event: "a" }, { event: "b" }] });
    calls[0].respond(answer([]));
    await flush();
    expect(outcomes).toHaveLength(1);
    expect((outcomes[0] as { kind: string }).kind).toBe("result");
  });

  it("aborts the call in flight when the inputs change, and never reports its answer", async () => {
    const outcomes: unknown[] = [];
    const errors: Error[] = [];
    const r = runner(outcomes, errors);
    r.schedule({ steps: [] });
    await vi.advanceTimersByTimeAsync(SIMULATE_DEBOUNCE_MS);
    await flush();
    expect(calls).toHaveLength(1);
    r.schedule({ steps: [{ event: "b" }] });
    expect(calls[0].signal.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(SIMULATE_DEBOUNCE_MS);
    await flush();
    expect(calls).toHaveLength(2);
    expect(calls[1].signal.aborted).toBe(false);
    calls[1].respond(answer([]));
    await flush();
    expect(outcomes).toHaveLength(1);
    expect(errors).toEqual([]);
    r.cancel();
  });

  it("reports a 422 as the draft's diagnostics and cancels on dispose", async () => {
    const outcomes: { kind: string; diagnostics?: unknown[] }[] = [];
    const r = runner(outcomes, []);
    r.schedule({ document: {} as never, steps: [] });
    await vi.advanceTimersByTimeAsync(SIMULATE_DEBOUNCE_MS);
    await flush();
    calls[0].respond(
      {
        diagnostics: [{ rule: "MQ1002", severity: "error", message: "bad", elementId: null, filePath: null, jsonPointer: "", line: null, column: null }],
        errors: 1,
        warnings: 0,
        infos: 0,
        truncated: false,
        hasErrors: true,
      },
      422,
    );
    await flush();
    expect(outcomes).toEqual([{ kind: "invalid", diagnostics: [expect.objectContaining({ rule: "MQ1002" })] }]);
    r.schedule({ steps: [] });
    r.cancel();
    await vi.advanceTimersByTimeAsync(SIMULATE_DEBOUNCE_MS * 2);
    expect(calls).toHaveLength(1);
  });
});

describe("the rows of an answer", () => {
  const names = namesOf(purchase(), actorNames);

  it("lists the enabled triggers with their fields and the input each raises by default", () => {
    const start = answer([]);
    const rows = enabledRows(start, purchase(), names, allActors);
    expect(rows.map((r) => [r.trigger, r.label, r.actors.map((a) => a.name), r.anyActor])).toEqual([["event", "submit", ["BudgetHolder"], false]]);
    expect(inputOf(rows[0], defaultRowValue(rows[0], start))).toEqual({ event: X(14), actor: BUDGET_HOLDER });

    const review = answer([{ event: X(14), actor: BUDGET_HOLDER }]);
    const invokes = enabledRows(review, purchase(), names, allActors);
    expect(invokes.map((r) => r.label)).toEqual(["checkBudget done", "checkBudget error", "complianceReview done", "complianceReview error"]);
    // Any actor may complete a service task: the picker offers every actor and raises with none.
    expect(invokes[0].anyActor).toBe(true);
    expect(inputOf(invokes[0], defaultRowValue(invokes[0], review))).toEqual({ input: "invoke-done", invoke: X(19) });
    expect(inputOf(invokes[2], defaultRowValue(invokes[2], review))).toEqual({ input: "invoke-done", invoke: X(20), actor: APPROVER });
    expect(pendingRows(review, purchase(), names).map((p) => [p.label, p.actor])).toEqual([
      ["checkBudget in Review.Budget.Checking", null],
      ["complianceReview in Review.Compliance.Pending", APPROVER],
    ]);
  });

  it("gives a gated event its signer, meaning and reason, a payload its fields, and time the next due delay", () => {
    const approval = answer([
      { event: X(14), actor: BUDGET_HOLDER },
      { input: "invoke-done", invoke: X(19) },
      { input: "invoke-done", invoke: X(20), actor: APPROVER },
    ]);
    const rows = enabledRows(approval, purchase(), names, allActors);
    expect(rows.map((r) => r.label)).toEqual(["approve", "reject", "advance time"]);
    const [approve, reject, time] = rows;
    expect(approve.gate).toEqual({ have: 0, need: 2, meanings: [{ id: X(23), name: "approved" }], reasonRequired: false });
    const value = defaultRowValue(approve, approval);
    expect(inputOf(approve, value)).toEqual({ event: X(16), actor: APPROVER, signer: "signer1", meaning: X(23) });
    expect(inputOf(approve, { ...value, reason: " fine " })).toMatchObject({ reason: "fine" });
    expect(reject.payload.map((a) => a.name)).toEqual(["reason"]);
    expect(inputOf(reject, { ...defaultRowValue(reject, approval), payload: { "01JQATT0000000000000000203": "too dear" } })).toEqual({
      event: X(17),
      actor: APPROVER,
      payload: { "01JQATT0000000000000000203": "too dear" },
    });
    expect(nextDue(approval)).toEqual({ transition: "01JQTRN0000000000000000023", dueAt: "2000-01-06T00:00:00Z", after: "P5D" });
    expect(inputOf(time, defaultRowValue(time, approval))).toEqual({ input: "time", after: "P5D" });
  });

  it("keeps a payload field's text while it is typed and converts it to the attribute's type when the input is built", () => {
    const approval = answer([
      { event: X(14), actor: BUDGET_HOLDER },
      { input: "invoke-done", invoke: X(19) },
      { input: "invoke-done", invoke: X(20), actor: APPROVER },
    ]);
    const reject = enabledRows(approval, purchase(), names, allActors)[1];
    const row = { ...reject, payload: [...reject.payload, { id: "AMT", name: "amount", type: "decimal" }, { id: "N", name: "count", type: "int32" }] };
    const value = defaultRowValue(row as never, approval);
    // "1." is a step on the way to "1.5": the edit keeps the text, so the next key makes "1.5", not "15".
    let edits = mergeRowEdit(undefined, { payload: { AMT: "1." } });
    edits = mergeRowEdit(edits, { payload: { AMT: "1.5" } });
    expect(edits.payload).toEqual({ AMT: "1.5" });
    const input = inputOf(row as never, { ...value, payload: { ...value.payload, ...edits.payload, N: "3", "01JQATT0000000000000000203": "late" } });
    expect(input.payload).toEqual({ "01JQATT0000000000000000203": "late", AMT: 1.5, N: 3 });
    // Text that is not a number stays text (the engine reports it); a built "1." reads as 1.
    expect(inputOf(row as never, { ...value, payload: { AMT: "1.", N: "many" } }).payload).toEqual({ AMT: 1, N: "many" });
  });

  it("merges one field's edit into the row's earlier edits: other payload fields and assumptions stay", () => {
    let edits = mergeRowEdit(undefined, { payload: { a: "x" }, actor: "A1" });
    edits = mergeRowEdit(edits, { payload: { b: "y" } });
    edits = mergeRowEdit(edits, { assume: { G1: false } });
    edits = mergeRowEdit(edits, { assume: { G2: true } });
    edits = mergeRowEdit(edits, { signer: "s" });
    expect(edits).toEqual({ actor: "A1", payload: { a: "x", b: "y" }, assume: { G1: false, G2: true }, signer: "s" });
  });

  it("assumes a stub guard's result, true by default", () => {
    const doc = purchase();
    doc.guards = [{ id: "G1", name: "withinBudget" }];
    doc.transitions![0].guard = "G1";
    const start = { processHash: "h", ...simulate(doc as never, null, []) } as unknown as SimulationResult;
    const [row] = enabledRows(start, doc, namesOf(doc, actorNames), allActors);
    expect(row.assume).toEqual([{ id: "G1", name: "withinBudget" }]);
    expect(inputOf(row, defaultRowValue(row, start)).assume).toEqual({ G1: true });
    expect(inputOf(row, { ...defaultRowValue(row, start), assume: { G1: false } }).assume).toEqual({ G1: false });
  });

  it("writes the last step's lines from the trace: transitions, guards, actions, context, audit, refusal", () => {
    const gated = answer([
      { event: X(14), actor: BUDGET_HOLDER },
      { input: "invoke-done", invoke: X(19) },
      { input: "invoke-done", invoke: X(20), actor: APPROVER },
      { event: X(16), actor: APPROVER, signer: "ann" },
      { event: X(16), actor: APPROVER, signer: "ann" },
      { input: "time", after: "P6D" },
    ]);
    const signed = lastStepLines(gated.trace[4], names);
    expect(signed.map((l) => l.text)).toEqual(["Purchase approval: signed by ann as Approver (approved)"]);
    const repeat = lastStepLines(gated.trace[5], names);
    expect(repeat.map((l) => [l.kind, l.text])).toEqual([
      ["refusal", "Refused: this signer already signed the gate"],
      ["audit", "Purchase approval: refused by ann as Approver (approved)"],
    ]);
    const timer = lastStepLines(gated.trace[6], names);
    expect(timer.map((l) => l.text)).toEqual(["Took Approval · P5D → (no target)", "sendReminder (expression): reminders=1", "Context: reminders=1"]);
    expect(changesText({ "01JQATT0000000000000000201": 5 }, names)).toBe("amount=5");
  });

  it("lists the trace from the start, marking refused inputs and those the replay did not run", () => {
    const inputs = [
      { event: X(14), actor: BUDGET_HOLDER },
      { event: X(14), actor: BUDGET_HOLDER },
      { input: "time" as const, after: "PT1H" },
    ];
    const rows = traceRows(inputs, answer(inputs), names);
    expect(rows.map((r) => [r.index, r.label, r.ran, r.accepted, r.refusal])).toEqual([
      [-1, "Start", true, true, null],
      [0, "submit as BudgetHolder", true, true, null],
      [1, "submit as BudgetHolder", true, false, "no-transition"],
      [2, "time +PT1H", true, true, null],
    ]);
    expect(rows[1].states).toBe("Review.Budget.Checking, Review.Compliance.Pending");
    expect(traceRows(inputs, null, names).every((r) => !r.ran)).toBe(true);
    expect(inputLabel({ event: X(16), actor: APPROVER, signer: "ann" }, names)).toBe("approve as Approver signed by ann");
  });

  it("gives a replay's verdicts from verify and its states from simulate, and shows expected and actual by name", () => {
    const quick = scenario(QUICK_APPROVAL);
    const failure = {
      step: 1,
      rule: "MQ9302",
      message: "Step 2 expected the states Approval.",
      expected: [S(110)],
      actual: [S(105), S(108)],
    };
    const simulation = answer(quick.steps);
    const rows = replayRows(quick, { scenario: QUICK_APPROVAL, name: "QuickApproval", passed: false, steps: 2, failure }, simulation, names);
    expect(rows.map((r) => [r.label, r.verdict, r.states])).toEqual([
      ["submit as BudgetHolder", "passed", "Review.Budget.Checking, Review.Compliance.Pending"],
      ["checkBudget done", "failed", "Review.Budget.BudgetOk, Review.Compliance.Pending"],
    ]);
    expect(failureText(failure.expected, names)).toBe("Approval");
    expect(failureText(failure.actual, names)).toBe("Review.Budget.BudgetOk, Review.Compliance.Pending");
    expect(failureText({ "01JQATT0000000000000000202": 1 }, names)).toBe("reminders=1");
    expect(failureText(null, names)).toBe("—");
    const passed = replayRows(quick, { scenario: QUICK_APPROVAL, name: "QuickApproval", passed: true, steps: 2, failure: null }, simulation, names);
    expect(passed.map((r) => r.verdict)).toEqual(["passed", "passed"]);
    // The summary names the failing step, or the outcome when the outcome failed (step -1).
    const verification = { scenario: QUICK_APPROVAL, name: "QuickApproval", passed: false, steps: 2, failure };
    expect(replaySummary(verification, 2)).toBe("Step 2 failed (MQ9302): Step 2 expected the states Approval.");
    expect(replaySummary({ ...verification, failure: { ...failure, step: -1, rule: "MQ9303", message: "The process is still active." } }, 2)).toBe(
      "Outcome failed (MQ9303): The process is still active.",
    );
    expect(replaySummary({ ...verification, passed: true, failure: null }, 2)).toBe("All 2 steps passed.");
  });

  it("writes and checks durations", () => {
    expect(durationText(5 * 86_400_000)).toBe("P5D");
    expect(durationText(90 * 60_000)).toBe("PT1H30M");
    expect(durationText(86_400_000 + 1500)).toBe("P1DT1.5S");
    expect(isDuration("PT30M")).toBe(true);
    expect(isDuration("P1DT2H")).toBe(true);
    expect(isDuration("30 minutes")).toBe(false);
  });
});

describe("the explorer's request", () => {
  it("holds one request until the panel of its process consumes it", () => {
    const first = requestSimulation(PURCHASE_APPROVAL);
    const second = requestSimulation(PURCHASE_APPROVAL, { record: { name: "Probe" } });
    expect(simulationRequest()).toEqual({ process: PURCHASE_APPROVAL, record: { name: "Probe" }, seq: second.seq });
    consumeSimulationRequest(first);
    expect(simulationRequest()).toBe(second);
    consumeSimulationRequest(second);
    expect(simulationRequest()).toBeNull();
  });
});

describe("scenario objects", () => {
  const mock = useMockApi();
  const OBJ_A = "01JQ0B10000000000000000001";
  const OBJ_B = "01JQ0B10000000000000000002";
  const ENTITY = "01JQ0B100000000000000000E1";
  const OTHER = "01JQ0B100000000000000000E2";
  const ATTR = "01JQ0B100000000000000000A1";
  const END = "01JQ0B100000000000000000D1";
  const objects = () => [
    { id: OBJ_A, name: "order", entity: ENTITY, values: { [ATTR]: 5 }, links: [{ end: END, object: OBJ_B }] },
    { id: OBJ_B, name: "customer", entity: OTHER },
  ];
  const withObjects = () => ({ ...scenario(BUDGET_REJECTED), objects: objects(), subject: OBJ_A }) as ScenarioDoc;
  const THIRD = "01JQ0B100000000000000000E3";
  const seedEntities = (model: MockModel) => {
    const entity = (id: string, name: string, extra: object = {}) =>
      expect(model.create({ kind: "entity", id, name, abstract: true, ...extra }).diagnostics.map((d) => d.message)).toEqual([]);
    entity(ENTITY, "Order", { attributes: [{ id: ATTR, name: "total", type: "int32" }] });
    entity(OTHER, "Customer");
    entity(THIRD, "Region");
    const ends = [
      { id: END, entity: OTHER, role: "parent", navigation: "region", min: 1, max: 1, onDelete: "restrict" },
      { id: "01JQ0B100000000000000000D2", entity: THIRD, role: "children", navigation: "customers" },
    ];
    expect(model.create({ kind: "relation", id: "01JQ0B100000000000000000R1", name: "inRegion", ends }).diagnostics.map((d) => d.message)).toEqual([]);
  };
  const post = (path: string, body: unknown) =>
    fetch(mock.url(path), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });

  it("copies objects and subject into the session and the request", () => {
    const loaded = loadScenario(newSession(PURCHASE_APPROVAL), withObjects());
    expect(loaded.start.objects).toEqual(objects());
    expect(loaded.start.subject).toBe(OBJ_A);
    expect(requestOf(loaded)).toMatchObject({ objects: objects(), subject: OBJ_A });
    // A scenario without objects adds nothing to the request, and a start edit keeps the cast.
    expect(requestOf(loadScenario(newSession(PURCHASE_APPROVAL), scenario(BUDGET_REJECTED)))).not.toHaveProperty("objects");
    expect(setStartValue(loaded, "01JQATT0000000000000000201", 1).start.subject).toBe(OBJ_A);
  });

  it("records the objects with new ids, remaps the subject and links, and simulates with them", async () => {
    seedEntities(mock.backend.model);
    const request = { name: "WithCast", steps: scenario(BUDGET_REJECTED).steps.slice(0, 1), objects: objects(), subject: OBJ_A };
    const recorded = await post(`/api/processes/${PURCHASE_APPROVAL}/scenarios`, request);
    expect(recorded.status).toBe(201);
    const { element } = (await recorded.json()) as {
      element: { objects: { id: string; name: string; links?: { end: string; object: string }[] }[]; subject: string };
    };
    const [order, customer] = element.objects;
    expect(element.objects.map((o) => o.name)).toEqual(["order", "customer"]);
    for (const o of element.objects) {
      expect(o.id).toMatch(/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/);
      expect([OBJ_A, OBJ_B]).not.toContain(o.id);
    }
    expect(element.subject).toBe(order.id);
    expect(order.links).toEqual([{ end: END, object: customer.id }]);
    expect((await post(`/api/processes/${PURCHASE_APPROVAL}/simulate`, { scenario: "WithCast" })).status).toBe(200);
    expect((await post(`/api/processes/${PURCHASE_APPROVAL}/simulate`, { objects: objects(), subject: OBJ_A })).status).toBe(200);
  });

  it("refuses a cast with a missing id, a subject by name, a repeated id, an unknown link or a repeated name", async () => {
    seedEntities(mock.backend.model);
    const steps = scenario(BUDGET_REJECTED).steps.slice(0, 1);
    const bad: [string, object][] = [
      ["objects without ids", { objects: [{ name: "order", entity: ENTITY }], subject: OBJ_A }],
      ["a subject by name", { objects: objects(), subject: "order" }],
      ["no subject", { objects: objects() }],
      ["a subject that is no object", { objects: objects(), subject: "01JQ0B10000000000000000099" }],
      ["a repeated id", { objects: [objects()[0], { ...objects()[1], id: OBJ_A }], subject: OBJ_A }],
      ["a repeated name", { objects: [objects()[0], { ...objects()[1], name: "order" }], subject: OBJ_A }],
      ["an unknown link", { objects: [{ ...objects()[0], links: [{ end: END, object: "01JQ0B10000000000000000099" }] }], subject: OBJ_A }],
    ];
    for (const [label, cast] of bad) {
      expect((await post(`/api/processes/${PURCHASE_APPROVAL}/simulate`, cast)).status, `simulate: ${label}`).toBe(400);
      expect((await post(`/api/processes/${PURCHASE_APPROVAL}/scenarios`, { name: "Bad", steps, ...cast })).status, `record: ${label}`).toBe(400);
    }
  });

  it("drops an object and the links to it when its entity goes, and refuses when the subject would go", () => {
    const model = mock.backend.model;
    seedEntities(model);
    const cast = (id: string, subject: string) => ({
      kind: "scenario",
      id,
      name: `Cast${id.slice(-1)}`,
      process: PURCHASE_APPROVAL,
      steps: scenario(BUDGET_REJECTED).steps.slice(0, 1),
      objects: [
        { id: OBJ_A, name: "order", entity: ENTITY },
        { id: OBJ_B, name: "customer", entity: OTHER, links: [{ end: END, object: OBJ_A }] },
      ],
      subject,
    });
    const keptId = "01JQ0B10000000000000000S01";
    expect(model.create(cast(keptId, OBJ_B)).outcome).toBe("saved");
    const plan = model.deletePlan([ENTITY], "remove-references");
    expect(plan.outcome).toBe("saved");
    expect(plan.removes.map((r) => r.subKind).sort()).toEqual(["link", "object"]);
    expect(model.delete(ENTITY, model.get(ENTITY)!.hash, "remove-references").outcome).toBe("saved");
    const after = model.get(keptId)!.json as { objects: { name: string; links?: unknown[] }[] };
    expect(after.objects.map((o) => o.name)).toEqual(["customer"]);
    expect(after.objects[0].links ?? []).toEqual([]);

    // The other entity's object is now the subject of a second scenario: its delete is refused.
    expect(model.create({ ...cast("01JQ0B10000000000000000S02", OBJ_B), objects: [{ id: OBJ_B, name: "customer", entity: OTHER }] }).outcome).toBe("saved");
    const refused = model.deletePlan([OTHER], "remove-references");
    expect(refused.refused.map((r) => [r.pointer, r.rule])).toContainEqual(["/subject", "MQ2001"]);
    expect(model.delete(OTHER, model.get(OTHER)!.hash, "remove-references").outcome).not.toBe("saved");
  });
});
