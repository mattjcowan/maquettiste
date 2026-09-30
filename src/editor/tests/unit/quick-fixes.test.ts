// The Problems panel's quick fixes (phase-3-design.md 3, the derivation table): each rule's fix read from the
// diagnostic's element and pointer, the mock's set-initial and set-lifecycle batch operations, and a fix applied
// through the real endpoints as one undo step.
import { describe, expect, it } from "vitest";
import type { Diagnostic, ModelJson } from "@/api/types";
import { MockBackend } from "@/mocks/backend";
import { PAYMENT_LIFECYCLE, PURCHASE_APPROVAL, QUICK_APPROVAL } from "@/mocks/model/processSeed";
import { deriveQuickFix, fixDocuments, hasQuickFix } from "@/problems/quickFix";
import { applyQuickFix, freshQuickFix } from "@/problems/applyQuickFix";
import { applySyncEnum, syncEnum } from "@/editors/process/api";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const diag = (rule: string, elementId: string, jsonPointer: string): Diagnostic => ({
  rule,
  severity: "error",
  message: rule,
  elementId,
  filePath: null,
  jsonPointer,
  line: null,
  column: null,
});

const process = {
  kind: "process",
  id: "P",
  name: "Order",
  use: "lifecycle",
  subject: "E",
  boundAttribute: "A",
  initial: "S2",
  events: [
    { id: "E1", name: "submit", actors: ["R1"] },
    { id: "E2", name: "unused" },
  ],
  states: [
    { id: "S1", name: "Draft", initial: "S2" },
    {
      id: "S2",
      name: "Review",
      type: "compound",
      initial: "S1",
      states: [
        { id: "S3", name: "First" },
        { id: "S4", name: "Second" },
      ],
    },
  ],
  transitions: [{ id: "T1", source: "S1", event: "E1", targets: ["S2"], gate: { id: "G", name: "g", signers: ["R2"], requiredActors: ["R3"] } }],
} as unknown as ModelJson;
const entity = { kind: "entity", id: "E", name: "Order", lifecycle: "Q", attributes: [{ id: "A", name: "status" }] } as unknown as ModelJson;
const docs =
  (...list: ModelJson[]) =>
  (id: string) =>
    list.find((d) => d.id === id);

describe("quick fix derivation", () => {
  it("offers a fix for the catalog rules and the rules the editor builds", () => {
    expect(hasQuickFix("MQ9001", "set-initial")).toBe(true);
    expect(hasQuickFix("MQ9205", undefined)).toBe(true);
    expect(hasQuickFix("MQ9002", undefined)).toBe(false);
  });

  it("MQ9001 sets the initial to the first child with the others to choose from, or removes a misplaced initial", () => {
    const nested = deriveQuickFix(diag("MQ9001", "P", "/states/1/initial"), "set-initial", docs(process));
    expect(nested).toMatchObject({ kind: "operation", op: "set-initial", id: "S2", target: "S3", ids: ["P"] });
    expect(nested && "choices" in nested ? nested.choices?.map((c) => c.name) : []).toEqual(["First", "Second"]);
    expect(deriveQuickFix(diag("MQ9001", "P", "/initial"), "set-initial", docs(process))).toMatchObject({ id: "P", target: "S1" });
    const atomic = deriveQuickFix(diag("MQ9001", "P", "/states/0/initial"), "set-initial", docs(process));
    expect(atomic).toMatchObject({ kind: "update", label: "Remove initial" });
    expect(atomic?.kind === "update" && (atomic.json.states as Json[])[0].initial).toBe(undefined);
  });

  it("MQ9203 syncs the enum and MQ9302 to MQ9304 refresh the scenario", () => {
    expect(deriveQuickFix(diag("MQ9203", "P", "/boundAttribute"), "sync-enum", docs(process))).toEqual({ kind: "sync-enum", process: "P", label: "Sync enum" });
    const scenario = { kind: "scenario", id: "C", name: "Happy" } as unknown as ModelJson;
    expect(deriveQuickFix(diag("MQ9303", "C", "/steps/0/expect"), "refresh-scenario", docs(scenario))).toMatchObject({
      op: "refresh-scenario",
      id: "C",
      ids: ["C"],
    });
  });

  it("MQ9013 and MQ9016 remove the node, MQ9102 and MQ9105 add the missing actor", () => {
    const unused = deriveQuickFix(diag("MQ9013", "P", "/events/1"), null, docs(process));
    expect(unused).toMatchObject({ kind: "update", id: "P", label: "Remove unused" });
    expect(unused?.kind === "update" && (unused.json.events as Json[]).map((e) => e.name)).toEqual(["submit"]);
    const diagram = { kind: "diagram", id: "D", name: "d", members: [{ element: "S1" }, { element: "X" }] } as unknown as ModelJson;
    const member = deriveQuickFix(diag("MQ9016", "D", "/members/1/element"), null, docs(diagram));
    expect(member?.kind === "update" && member.json.members).toEqual([{ element: "S1" }]);
    const signer = deriveQuickFix(diag("MQ9102", "P", "/transitions/0/gate/requiredActors/0"), null, docs(process));
    expect(signer?.kind === "update" && ((signer.json.transitions as Json[])[0].gate as Json).signers).toEqual(["R2", "R3"]);
    const raise = deriveQuickFix(diag("MQ9105", "P", "/transitions/0/gate/signers/0"), null, docs(process));
    expect(raise?.kind === "update" && (raise.json.events as Json[])[0].actors).toEqual(["R1", "R2"]);
  });

  it("MQ9205 sets the subject's bound attribute default to the initial state's name", () => {
    expect(fixDocuments(diag("MQ9205", "P", "/boundAttribute"), process)).toEqual(["P", "E"]);
    const fix = deriveQuickFix(diag("MQ9205", "P", "/boundAttribute"), null, docs(process, entity));
    expect(fix).toMatchObject({ kind: "update", id: "E", label: "Set default to Review" });
    expect(fix?.kind === "update" && (fix.json.attributes as Json[])[0].default).toBe("Review");
  });

  it("MQ9201 fixes a subject that names another lifecycle and an entity naming an orchestration, nothing else", () => {
    expect(deriveQuickFix(diag("MQ9201", "P", "/subject"), null, docs(process, entity))).toMatchObject({
      op: "set-lifecycle",
      id: "E",
      target: "P",
      ids: ["E", "P", "Q"],
    });
    const orchestration = { kind: "process", id: "Q", name: "Flow" } as unknown as ModelJson;
    expect(deriveQuickFix(diag("MQ9201", "E", "/lifecycle"), null, docs(entity, orchestration))).toMatchObject({ id: "E", target: "Q", ids: ["E", "Q"] });
    const other = { ...orchestration, use: "lifecycle", subject: "F" } as ModelJson;
    expect(deriveQuickFix(diag("MQ9201", "E", "/lifecycle"), null, docs(entity, other))).toBeNull();
    expect(deriveQuickFix(diag("MQ9201", "P", "/use"), null, docs(process, entity))).toBeNull();
  });
});

describe("the mock's process quick-fix operations", () => {
  it("set-initial names a direct child and refuses another state", () => {
    const model = new MockBackend().model;
    const ok = model.batch({ operations: [{ op: "set-initial", id: PURCHASE_APPROVAL, target: "01JQSTA0000000000000000110" }] });
    expect((ok.body as { outcome: string }).outcome).toBe("saved");
    expect((model.get(PURCHASE_APPROVAL)!.json as Json).initial).toBe("01JQSTA0000000000000000110");
    const nested = model.batch({ operations: [{ op: "set-initial", id: "01JQSTA0000000000000000103", target: "01JQSTA0000000000000000106" }] });
    expect((nested.body as { outcome: string }).outcome).toBe("saved");
    const bad = model.batch({ operations: [{ op: "set-initial", id: PURCHASE_APPROVAL, target: "01JQSTA0000000000000000104" }] });
    expect((bad.body as { outcome: string }).outcome).not.toBe("saved");
  });

  it("set-lifecycle binds both sides", () => {
    const model = new MockBackend().model;
    const payment = "01J92P0V0HEGSC6MW92CST5KA6";
    const answer = model.batch({ operations: [{ op: "set-lifecycle", id: payment, target: PURCHASE_APPROVAL }] });
    expect((answer.body as { outcome: string }).outcome).toBe("saved");
    expect((model.get(payment)!.json as Json).lifecycle).toBe(PURCHASE_APPROVAL);
    expect(model.get(PURCHASE_APPROVAL)!.json).toMatchObject({ use: "lifecycle", subject: payment });
  });
});

describe("applying a quick fix", () => {
  const api = useMockApi();

  it("is one batch that undo reverts", async () => {
    const { queryClient, store, drafts, undo } = api.services;
    const fix = await freshQuickFix(drafts, diag("MQ9001", PURCHASE_APPROVAL, "/initial"), "set-initial");
    expect(fix).toMatchObject({ op: "set-initial", target: "01JQSTA0000000000000000101" });
    if (!fix || fix.kind === "sync-enum") throw new Error("expected an operation");
    expect(await applyQuickFix(queryClient, store, fix, "01JQSTA0000000000000000110")).toBeNull();
    expect((api.backend.model.get(PURCHASE_APPROVAL)!.json as Json).initial).toBe("01JQSTA0000000000000000110");
    expect(store.getState().undo.at(-1)?.label).toBe("Set initial");
    await undo.undo();
    expect((api.backend.model.get(PURCHASE_APPROVAL)!.json as Json).initial).toBeUndefined();
  });

  it("refreshes a scenario's expectations as one undo step", async () => {
    const { queryClient, store, drafts } = api.services;
    const fix = await freshQuickFix(drafts, diag("MQ9302", QUICK_APPROVAL, "/steps/1/expect/states"), "refresh-scenario");
    if (!fix || fix.kind === "sync-enum") throw new Error("expected an operation");
    expect(await applyQuickFix(queryClient, store, fix)).toBeNull();
    expect(store.getState().undo.at(-1)).toMatchObject({ label: "Update expectations from replay", ids: [QUICK_APPROVAL] });
  });
});

describe("the Sync enum quick fix", () => {
  const api = useMockApi({ scenarios: ["drift"] });

  // One undo step on the enum; undoing it would bring the MQ9203 error back, and a change that introduces an error is
  // refused (the element-save rule of the engine and the mock), so the undo is refused and the enum stays synced.
  it("applies after its dry run as one undo step, whose undo the element-save rule refuses", async () => {
    const { queryClient, store, undo } = api.services;
    const plan = await syncEnum(PAYMENT_LIFECYCLE, true);
    expect(plan.added).toContain("Disputed");
    expect((await applySyncEnum(queryClient, store, PAYMENT_LIFECYCLE, plan.enum!)).applied).toBe(true);
    const drift = () => api.backend.model.validate().diagnostics.filter((d) => d.rule === "MQ9203");
    expect(drift()).toHaveLength(0);
    expect(store.getState().undo.at(-1)).toMatchObject({ label: "Sync enum", ids: [plan.enum] });
    expect(await undo.undo()).toMatchObject({ ok: false, reason: expect.stringContaining("is invalid") });
    expect(drift()).toHaveLength(0);
  });
});
