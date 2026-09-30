// The process editor's models (phase-3-design.md 6.2): the state tree rows, the transition rows and their edits, the
// gate forms, the scenario and step rows, the expression check (MQ9501) and the Sync enum plan; and the mock's
// MQ9501 diagnostic and refresh-scenario operation the editor relies on.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { clone } from "@/lib/json";
import {
  addNamed,
  addSiblingState,
  addState,
  addTransition,
  deleteState,
  expressionError,
  gateForms,
  idsOfNames,
  incomingTransitions,
  mapText,
  namedRows,
  parseMapText,
  removeNamed,
  scenarioRows,
  setInitial,
  setStateType,
  setTrigger,
  stateRows,
  stepRows,
  stepStatuses,
  syncPlanLines,
  transitionRows,
  usedBy,
  type ProcessDoc,
  type ScenarioDoc,
} from "@/model/process";
import { APPROVER, BUDGET_HOLDER, BUDGET_REJECTED, INVOICE_LIFECYCLE, PURCHASE_APPROVAL, QUICK_APPROVAL } from "@/mocks/model/processSeed";

const backend = new MockBackend();
const doc = <T>(id: string) => clone(backend.model.get(id)!.json) as unknown as T;
const purchase = () => doc<ProcessDoc>(PURCHASE_APPROVAL);
const S = (n: number) => `01JQSTA0000000000000000${n}`;

describe("state tree rows", () => {
  it("flattens the tree in document order with depth, path, initial markers and invokes", () => {
    const rows = stateRows(purchase());
    expect(rows).toHaveLength(13);
    expect(rows.slice(0, 5).map((r) => [r.path, r.depth, r.type, r.initial])).toEqual([
      ["Drafting", 0, "atomic", true],
      ["Review", 0, "parallel", false],
      ["Review.Budget", 1, "compound", true],
      ["Review.Budget.Checking", 2, "atomic", true],
      ["Review.Budget.BudgetOk", 2, "final", false],
    ]);
    expect(rows.find((r) => r.name === "Pending")).toMatchObject({ invokes: "complianceReview", pointer: "/states/1/states/1/states/0" });
    expect(rows.every((r) => r.bound === "")).toBe(true);
  });

  it("shows a lifecycle's bound members and marks drift", () => {
    const p = doc<ProcessDoc>(INVOICE_LIFECYCLE);
    expect(stateRows(p, ["Draft", "Issued", "Paid", "Void"]).map((r) => [r.bound, r.drift])).toEqual([
      ["Draft", false],
      ["Issued", false],
      ["Paid", false],
      ["Void", false],
    ]);
    const drifted = stateRows(p, ["Draft", "Paid", "Issued"]);
    expect(drifted.map((r) => r.drift)).toEqual([false, true, true, true]);
  });

  it("adds siblings and children with unique names, sets the initial and the type", () => {
    const p = purchase();
    const sibling = addSiblingState(p, S(101));
    expect(p.states[1]).toMatchObject({ id: sibling, name: "State" });
    const child = addState(p, sibling);
    expect(p.states[1]).toMatchObject({ type: "compound", states: [{ id: child, name: "State" }] });
    expect(addState(p, sibling)).not.toBe(child);
    expect(p.states[1].states!.map((s) => s.name)).toEqual(["State", "State2"]);
    setInitial(p, p.states[1].states![1].id);
    expect(p.states[1].initial).toBe(p.states[1].states![1].id);
    setInitial(p, child);
    expect(p.states[1].initial).toBeUndefined();
    setStateType(p.states[1], "history");
    expect(p.states[1]).toMatchObject({ type: "history" });
    expect(p.states[1].initial).toBeUndefined();
  });

  it("refuses to delete a state a transition enters, and deletes a subtree with its outgoing transitions", () => {
    const p = purchase();
    expect(incomingTransitions(p, S(110)).map((t) => t.id)).toHaveLength(1);
    const refused = deleteState(p, S(110));
    expect(refused).toMatchObject({ ok: false });
    expect(!refused.ok && refused.reason).toContain("Review · done → Approval");
    const before = p.transitions!.length;
    // Budget's region is entered only from inside Review's own transitions (invoke-done / invoke-error).
    expect(deleteState(p, S(113))).toMatchObject({ ok: false });
    p.transitions = p.transitions!.filter((t) => !(t.targets ?? []).includes(S(113)));
    expect(deleteState(p, S(113))).toEqual({ ok: true });
    expect(p.transitions.length).toBe(before - 1);
    const lone = { ...purchase(), states: [purchase().states[0]], transitions: [] } as ProcessDoc;
    expect(deleteState(lone, S(101))).toMatchObject({ ok: false, reason: "A process keeps at least one state." });
  });
});

describe("transitions, guards, actions and events", () => {
  it("shows source paths, triggers, details, targets and the gate badge", () => {
    const rows = transitionRows(purchase());
    expect(rows[0]).toMatchObject({ source: "Drafting", trigger: "event", detail: "submit", targets: "Review" });
    expect(rows[1]).toMatchObject({ source: "Review.Budget.Checking", trigger: "invoke-done", detail: "checkBudget" });
    expect(rows.find((r) => r.gate)).toMatchObject({ source: "Approval", detail: "approve", gate: "Purchase approval" });
    expect(rows.find((r) => r.trigger === "after")).toMatchObject({ detail: "P5D", actions: "sendReminder", targets: "" });
  });

  it("adds a transition, switches its trigger and keeps only the keys the trigger uses", () => {
    const p = purchase();
    const id = addTransition(p, S(101));
    const t = p.transitions!.find((x) => x.id === id)!;
    expect(t).toMatchObject({ source: S(101), event: p.events![0].id });
    setTrigger(t, "after", p);
    expect(t).toMatchObject({ trigger: "after", after: "PT1H" });
    expect(t.event).toBeUndefined();
    setTrigger(t, "event", p);
    expect(t.trigger).toBeUndefined();
    expect(t.after).toBeUndefined();
    expect(
      idsOfNames("approval, nope, Drafting", [
        { id: "a", name: "Approval" },
        { id: "d", name: "Drafting" },
      ]),
    ).toEqual(["a", "d"]);
  });

  it("lists what uses each event, guard and action, and removes one with its references", () => {
    const p = purchase();
    const guard = addNamed(p, "guards");
    p.transitions![0].guard = guard;
    const uses = usedBy(p);
    expect(uses.get(guard)).toEqual(["Drafting · submit → Review"]);
    expect(namedRows(p, "actions")[0]).toMatchObject({ name: "sendReminder", usedBy: "Approval · P5D → (no target)" });
    const events = namedRows(p, "events", new Map([[BUDGET_HOLDER, "BudgetHolder"]]));
    expect(events[0]).toMatchObject({ name: "submit", actors: "BudgetHolder", pointer: "/events/0" });
    removeNamed(p, "guards", guard);
    expect(p.guards).toBeUndefined();
    expect(p.transitions![0].guard).toBeUndefined();
  });

  it("checks an expression's syntax (MQ9501) without running it", () => {
    expect(expressionError("context.amount > 1000")).toBeNull();
    expect(expressionError("({ reminders: context.reminders + 1 })")).toBeNull();
    expect(expressionError("")).toBeNull();
    expect(expressionError("context.amount >")).toBeTruthy();
    expect(expressionError("a; b")).toBeTruthy();
  });
});

describe("gate forms", () => {
  it("computes N of M from the signers and flags required actors that do not sign", () => {
    const p = purchase();
    const [form] = gateForms(p);
    expect(form).toMatchObject({ required: 2, of: 1, signers: [APPROVER], pointer: "/transitions/6/gate", transitionLabel: "Approval · approve → Ordering" });
    form.gate.signers = [APPROVER, BUDGET_HOLDER];
    form.gate.requiredActors = ["someone"];
    expect(gateForms(p)[0]).toMatchObject({ of: 2, strayRequired: ["someone"] });
  });
});

describe("scenario rows and steps", () => {
  it("shows each scenario's steps and last status", () => {
    const rows = scenarioRows(
      [
        { id: "a", name: "A", stepCount: 3 },
        { id: "b", name: "B", displayName: "Bee", stepCount: 2 },
        { id: "c", name: "C" },
      ],
      new Map([
        ["a", { passed: true as const }],
        ["b", { passed: false as const, step: 2 }],
      ]),
    );
    expect(rows.map((r) => [r.name, r.steps, r.status, r.failedAt])).toEqual([
      ["A", 3, "passed", null],
      ["Bee", 2, "failed", 2],
      ["C", 0, "not-run", null],
    ]);
  });

  it("names a step's event, actor, expected states and context, and maps name=value text", () => {
    const scenario = doc<ScenarioDoc>(BUDGET_REJECTED);
    const rows = stepRows(
      scenario,
      purchase(),
      new Map([
        [BUDGET_HOLDER, "BudgetHolder"],
        [APPROVER, "Approver"],
      ]),
    );
    expect(rows[0]).toMatchObject({
      input: "event",
      on: "submit",
      actor: "BudgetHolder",
      states: "Review.Budget.Checking, Review.Compliance.Pending",
      accepted: true,
    });
    expect(rows[1]).toMatchObject({ input: "invoke-error", on: "checkBudget", actor: "" });
    const names = new Map([["01JQATT0000000000000000201", "amount"]]);
    expect(mapText({ "01JQATT0000000000000000201": 2500 }, names)).toBe("amount=2500");
    expect(parseMapText("amount=3000, unknown=1, amount2", names)).toEqual({ "01JQATT0000000000000000201": 3000 });
  });

  it("derives each step's replay status from the failing step", () => {
    expect(stepStatuses(3, undefined)).toEqual(["not-run", "not-run", "not-run"]);
    expect(stepStatuses(3, null)).toEqual(["passed", "passed", "passed"]);
    expect(stepStatuses(3, 1)).toEqual(["passed", "failed", "not-run"]);
  });

  it("words the Sync enum plan", () => {
    expect(syncPlanLines({ added: ["Disputed"], removed: [], reordered: true, refused: [{ member: "Old", referencedBy: ["x", "y"] }] })).toEqual([
      "Adds Disputed",
      "Reorders the members as the states",
      "Keeps Old: still used by 2 elements",
    ]);
    expect(syncPlanLines({ added: [], removed: [], reordered: false, refused: [] })).toEqual(["The enum already matches the states."]);
  });
});

describe("the mock behind the process editor", () => {
  it("refuses a guard expression that does not parse (MQ9501) at its pointer", () => {
    const model = new MockBackend().model;
    const current = model.get(PURCHASE_APPROVAL)!;
    const json = clone(current.json) as unknown as ProcessDoc;
    json.guards = [{ id: "01JQGRD0000000000000000001", name: "large", expression: "context.amount >" }];
    const result = model.save(PURCHASE_APPROVAL, json as never, current.hash);
    expect(result.outcome).toBe("invalid");
    expect(result.diagnostics.find((d) => d.rule === "MQ9501")).toMatchObject({ jsonPointer: "/guards/0/expression" });
    json.guards[0].expression = "context.amount > 1000";
    expect(model.save(PURCHASE_APPROVAL, json as never, current.hash).outcome).toBe("saved");
  });

  it("answers refresh-scenario as a saved change of the scenario, never a delete", () => {
    const model = new MockBackend().model;
    const answer = model.batch({ operations: [{ op: "refresh-scenario", id: QUICK_APPROVAL }] });
    expect(answer.status).toBe(200);
    expect((answer.body as { outcome: string }).outcome).toBe("saved");
    expect(model.get(QUICK_APPROVAL)).not.toBeNull();
  });
});
