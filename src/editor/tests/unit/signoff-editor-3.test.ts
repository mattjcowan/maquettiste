// Sign-off part 2, the editor's first wave (signoff-design.md sections 6 and 11, decisions 32 to 36): the stage and round functions of
// `model/approvals.ts`, the part 2 columns of a signature, the quick fixes of MQ8203 and MQ8204, and the mock's rules, references and
// deletes for stages, rounds, delegation, reminders and escalation.
import { describe, expect, it } from "vitest";
import {
  addSignature,
  addStage,
  canRevert,
  detailsCount,
  detailsText,
  durationMs,
  editSignature,
  editStage,
  fixEscalationPair,
  moveSignature,
  refusal,
  removeSignature,
  removeStage,
  reorderStages,
  roundGroups,
  roundOf,
  signatureRows,
  stageRefusal,
  NO_DETAILS,
} from "@/model/approvals";
import type { Diagnostic } from "@/api/types";
import type { GateDoc } from "@/model/process";
import { MockBackend } from "@/mocks/backend";
import { planDelete, type CascadeDoc } from "@/mocks/model/cascade";
import { APPROVER, BUDGET_HOLDER, INVOICE_LIFECYCLE, PROCUREMENT } from "@/mocks/model/processSeed";
import { referencesOf } from "@/mocks/model/refs";
import { deriveQuickFix } from "@/problems/quickFix";

type Json = Record<string, unknown>;

const ACTORS = [
  { id: "a1", name: "Approver" },
  { id: "a2", name: "Controller" },
  { id: "a3", name: "Clerk" },
];
const names = {
  actor: (id: string) => ACTORS.find((a) => a.id === id)?.name ?? "?",
  transition: (id: string) => id,
  verb: (key: string) => key,
  condition: (t: Record<string, unknown> | undefined) => String((t as { text?: string } | undefined)?.text ?? ""),
};
const opts = { actors: ACTORS };

const approval = (): GateDoc => ({
  id: "g1",
  name: "Release",
  meanings: [{ id: "m1", name: "approved" }],
  stages: [
    {
      id: "s1",
      name: "Review",
      signatures: [
        { id: "x1", name: "Finance", signers: ["a1"] },
        { id: "x2", name: "Manager", signers: ["a2"] },
      ],
    },
    { id: "s2", name: "Final", round: 2, signatures: [{ id: "x3", name: "Director", signers: ["a1"] }] },
  ],
});

describe("stages and rounds", () => {
  it("groups stages by round, lowest first, keeping file order within a round", () => {
    const gate = approval();
    gate.stages!.push({ id: "s3", name: "Audit", round: 2, signatures: [{ id: "x4", name: "Audit", signers: ["a3"] }] });
    expect(roundGroups(gate).map((g) => [g.round, g.stages.map((s) => s.name)])).toEqual([
      [1, ["Review"]],
      [2, ["Final", "Audit"]],
    ]);
    expect(roundOf({})).toBe(1);
  });

  it("adds a stage in the next round with one signature that takes the last signature's signers", () => {
    const gate = approval();
    const id = addStage(gate)!;
    const stage = gate.stages!.find((s) => s.id === id)!;
    expect(stage.round).toBe(3);
    expect(stage.name).toBe("stage");
    expect(stage.signatures).toHaveLength(1);
    expect(stage.signatures[0].signers).toEqual(["a1"]);
    expect(stage.signatures[0].name).toBe("signature");
    expect(addStage({ id: "p", name: "plain", signers: ["a1"] })).toBeNull();
  });

  it("removes a stage but never the last one", () => {
    const gate = approval();
    expect(removeStage(gate, "s2")).toBe(true);
    expect(gate.stages!.map((s) => s.id)).toEqual(["s1"]);
    expect(removeStage(gate, "s1")).toBe(false);
    expect(removeStage(gate, "nope")).toBe(false);
  });

  it("moves a signature to another stage, and a stage left empty goes with it", () => {
    const gate = approval();
    expect(moveSignature(gate, "x1", "s2")).toBe(true);
    expect(gate.stages![0].signatures.map((s) => s.id)).toEqual(["x2"]);
    expect(gate.stages![1].signatures.map((s) => s.id)).toEqual(["x3", "x1"]);
    expect(moveSignature(gate, "x1", "s2")).toBe(false);
    expect(moveSignature(gate, "x2", "s2")).toBe(true);
    expect(gate.stages!.map((s) => s.id)).toEqual(["s2"]);
  });

  it("adds a signature to a named stage, and removing the last of a stage takes the stage", () => {
    const gate = approval();
    const id = addSignature(gate, undefined, "s1")!;
    expect(gate.stages![0].signatures.at(-1)!.id).toBe(id);
    expect(removeSignature(gate, "x3")).toBe(true);
    expect(gate.stages!.map((s) => s.id)).toEqual(["s1"]);
  });

  it("edits a stage: name, round (kept in round order) and Skip unless", () => {
    const gate = approval();
    expect(editStage(gate, "s1", "round", "3")).toBe(true);
    expect(gate.stages!.map((s) => s.id)).toEqual(["s2", "s1"]);
    expect(editStage(gate, "s1", "round", "1")).toBe(true);
    expect(gate.stages!.map((s) => s.id)).toEqual(["s1", "s2"]);
    expect(gate.stages![0].round).toBeUndefined();
    expect(editStage(gate, "s2", "when", "subject.total > 5")).toBe(true);
    expect(gate.stages![1].when).toEqual({ text: "subject.total > 5" });
    expect(editStage(gate, "s2", "when", "  ")).toBe(true);
    expect(gate.stages![1].when).toBeUndefined();
    expect(editStage(gate, "s2", "name", "Last")).toBe(true);
  });

  it("refuses a stage name that is empty or taken, and a round that is not a whole number from 1", () => {
    const gate = approval();
    expect(stageRefusal(gate, "s1", "name", " ")).toMatch(/needs a name/);
    expect(stageRefusal(gate, "s1", "name", "final")).toMatch(/Another stage/);
    expect(stageRefusal(gate, "s1", "name", "Review")).toBeNull();
    for (const bad of ["0", "1.5", "x", ""]) expect(stageRefusal(gate, "s1", "round", bad)).toMatch(/whole number/);
    expect(editStage(gate, "s1", "round", "0")).toBe(false);
  });

  it("puts stages in round order for MQ8203's fix, and only changes a gate that is out of order", () => {
    const gate = approval();
    expect(reorderStages(gate)).toBe(false);
    gate.stages!.reverse();
    expect(reorderStages(gate)).toBe(true);
    expect(gate.stages!.map((s) => s.id)).toEqual(["s1", "s2"]);
  });

  it("goes back to one list of signers only with one stage, round 1, one plain signature", () => {
    const gate = approval();
    expect(canRevert(gate)).toBe(false);
    gate.stages = [gate.stages![0]];
    gate.stages[0].signatures = [gate.stages[0].signatures[0]];
    expect(canRevert(gate)).toBe(true);
    gate.stages[0].round = 2;
    expect(canRevert(gate)).toBe(false);
    delete gate.stages[0].round;
    gate.stages[0].when = { text: "true" };
    expect(canRevert(gate)).toBe(false);
    delete gate.stages[0].when;
    for (const key of ["delegateTo", "escalateTo"] as const) {
      gate.stages[0].signatures[0][key] = ["a2"];
      expect(canRevert(gate)).toBe(false);
      delete gate.stages[0].signatures[0][key];
    }
    gate.stages[0].signatures[0].reauthenticate = true;
    expect(canRevert(gate)).toBe(false);
  });
});

describe("a signature's part 2 settings", () => {
  it("reads ISO 8601 durations as an after transition's delay is read", () => {
    expect(durationMs("PT4H")).toBe(4 * 3_600_000);
    expect(durationMs("P2D")).toBe(2 * 86_400_000);
    expect(durationMs("P1W")).toBe(7 * 86_400_000);
    expect(durationMs("P1DT12H")).toBe(36 * 3_600_000);
    expect(durationMs("PT0S")).toBe(0);
    for (const bad of ["", "4h", "P", "PT", "P1.5D", "tomorrow"]) expect(durationMs(bad)).toBeNull();
  });

  it("refuses an unknown actor, a duration that is no duration, and one that is not positive", () => {
    const sig = approval().stages![0].signatures[0];
    expect(refusal(sig, "delegateTo", "Nobody", opts)).toMatch(/No actor is named 'Nobody'/);
    expect(refusal(sig, "escalateTo", "Clerk, Ghost", opts)).toMatch(/'Ghost'/);
    expect(refusal(sig, "remindAfter", "soon", opts)).toMatch(/ISO 8601/);
    expect(refusal(sig, "escalateAfter", "PT0S", opts)).toMatch(/longer than zero/);
    expect(refusal(sig, "remindAfter", "PT4H", opts)).toBeNull();
    expect(refusal(sig, "remindAfter", "", opts)).toBeNull();
    expect(refusal(sig, "delegateTo", "Clerk, Controller", opts)).toBeNull();
  });

  it("sets and clears each setting, and an emptied escalateTo goes with escalateAfter", () => {
    const gate = approval();
    const sig = gate.stages![0].signatures[0];
    expect(editSignature(gate, "x1", "delegateTo", "Clerk, Controller", opts)).toBe(true);
    expect(sig.delegateTo).toEqual(["a3", "a2"]);
    editSignature(gate, "x1", "remindAfter", " PT4H ", opts);
    editSignature(gate, "x1", "escalateAfter", "P2D", opts);
    editSignature(gate, "x1", "escalateTo", "Controller", opts);
    editSignature(gate, "x1", "reauthenticate", true, opts);
    expect(sig).toMatchObject({ remindAfter: "PT4H", escalateAfter: "P2D", escalateTo: ["a2"], reauthenticate: true });
    expect(detailsText(sig)).toBe("Delegation, Remind PT4H, Escalate P2D, Password");
    expect(detailsCount(sig)).toBe("4 set");
    editSignature(gate, "x1", "escalateTo", "", opts);
    expect(sig.escalateTo).toBeUndefined();
    expect(sig.escalateAfter).toBeUndefined();
    editSignature(gate, "x1", "delegateTo", "", opts);
    editSignature(gate, "x1", "remindAfter", "", opts);
    editSignature(gate, "x1", "reauthenticate", false, opts);
    expect(Object.keys(sig).sort()).toEqual(["id", "name", "signers"]);
    expect(detailsText(sig)).toBe(NO_DETAILS);
    expect(detailsCount(sig)).toBe(NO_DETAILS);
  });

  it("gives each row its stage and the part 2 cells", () => {
    const gate = approval();
    gate.stages![0].signatures[1].delegateTo = ["a1"];
    gate.stages![0].signatures[1].escalateTo = ["a3"];
    gate.stages![0].signatures[1].escalateAfter = "P3D";
    const rows = signatureRows(gate, "/g", names);
    expect(rows.map((r) => r.stage)).toEqual(["s1", "s1", "s2"]);
    expect(rows[1]).toMatchObject({ delegateTo: "Approver", escalateTo: "Clerk", escalateAfter: "P3D", remindAfter: "", reauthenticate: false });
    expect(rows[1].details).toBe("Delegation, Escalate P3D");
    expect(rows[2].pointer).toBe("/g/stages/1/signatures/0");
  });

  it("adds or removes the missing half of the escalation pair for MQ8204's fix", () => {
    const only: GateDoc = approval();
    const sig = only.stages![0].signatures[0];
    sig.escalateTo = ["a2"];
    sig.remindAfter = "PT1H";
    expect(fixEscalationPair(sig)).toBe(true);
    expect(durationMs(sig.escalateAfter)!).toBeGreaterThan(durationMs("PT1H")!);
    expect(fixEscalationPair(sig)).toBe(false);
    delete sig.escalateTo;
    expect(fixEscalationPair(sig)).toBe(true);
    expect(sig.escalateAfter).toBeUndefined();
    sig.escalateTo = ["a2"];
    delete sig.remindAfter;
    fixEscalationPair(sig);
    expect(sig.escalateAfter).toBe("P1D");
  });
});

// ------------------------------------------------------------------ the mock

const refusals = new Map<MockBackend, string[]>();
const find = (backend: MockBackend, rule: string) =>
  [
    ...new Set([
      ...backend.model
        .validate()
        .diagnostics.filter((d) => d.rule === rule)
        .map((d) => `${d.jsonPointer}`),
      ...(refusals.get(backend) ?? []).filter((x) => x.startsWith(`${rule} `)).map((x) => x.slice(rule.length + 1)),
    ]),
  ].sort();
function edit(backend: MockBackend, update: (gate: Json, json: Json) => void): void {
  const json = structuredClone(backend.model.get(INVOICE_LIFECYCLE)!.json) as Json;
  update((json.transitions as Json[])[0].gate as Json, json);
  const result = backend.model.save(INVOICE_LIFECYCLE, json, backend.model.get(INVOICE_LIFECYCLE)!.hash);
  refusals.set(backend, result.outcome === "saved" ? [] : result.diagnostics.map((d) => `${d.rule} ${d.jsonPointer}`));
}
const stagesOf = (gate: Json) => gate.stages as Json[];
const GATE = "/transitions/0/gate";

describe("part 2 in the mock", () => {
  it("seeds a second stage in round 2 with a Skip unless, and a signature with delegation, reminder and escalation, with no findings", () => {
    const backend = new MockBackend();
    const gate = ((backend.model.get(INVOICE_LIFECYCLE)!.json as Json).transitions as Json[])[0].gate as Json;
    expect(stagesOf(gate).map((s) => [s.name, s.round])).toEqual([
      ["Review", undefined],
      ["Release", 2],
    ]);
    expect(stagesOf(gate)[1].when).toBeDefined();
    const manager = (stagesOf(gate)[0].signatures as Json[])[1];
    expect(manager).toMatchObject({ remindAfter: "PT24H", escalateAfter: "P3D" });
    expect(manager.delegateTo).toBeDefined();
    expect(manager.escalateTo).toBeDefined();
    expect(backend.model.validate().diagnostics.filter((d) => /^MQ82/.test(d.rule) || ["MQ3001", "MQ2001", "MQ9104"].includes(d.rule))).toEqual([]);
  });

  it("MQ8203 stages that are not in round order", () => {
    const backend = new MockBackend();
    edit(backend, (gate) => void stagesOf(gate).reverse());
    expect(find(backend, "MQ8203")).toEqual([`${GATE}/stages/1`]);
    edit(backend, (gate) => {
      stagesOf(gate)[0].round = 3;
    });
    expect(find(backend, "MQ8203")).toEqual([`${GATE}/stages/1/round`]);
    edit(backend, (gate) => {
      stagesOf(gate).push({
        id: "01JQAPR0000000000000000960",
        name: "Late",
        round: 1,
        signatures: [{ id: "01JQAPR0000000000000000961", name: "Late", signers: [APPROVER] }],
      });
    });
    expect(find(backend, "MQ8203")).toEqual([`${GATE}/stages/2/round`]);
    edit(backend, () => undefined);
    expect(find(backend, "MQ8203")).toEqual([]);
  });

  it("MQ8204 an escalation pair with one half, and a delay that is no positive duration", () => {
    const backend = new MockBackend();
    const sig = (gate: Json) => (stagesOf(gate)[0].signatures as Json[])[1];
    edit(backend, (gate) => void delete sig(gate).escalateAfter);
    expect(find(backend, "MQ8204")).toEqual([`${GATE}/stages/0/signatures/1/escalateTo`]);
    edit(backend, (gate) => void delete sig(gate).escalateTo);
    expect(find(backend, "MQ8204")).toEqual([`${GATE}/stages/0/signatures/1/escalateAfter`]);
    edit(backend, (gate) => void (sig(gate).remindAfter = "tomorrow"));
    expect(find(backend, "MQ8204")).toEqual([`${GATE}/stages/0/signatures/1/remindAfter`]);
    edit(backend, (gate) => void (sig(gate).escalateAfter = "PT0S"));
    expect(find(backend, "MQ8204")).toEqual([`${GATE}/stages/0/signatures/1/escalateAfter`]);
    edit(backend, () => undefined);
    expect(find(backend, "MQ8204")).toEqual([]);
  });

  it("MQ8205 warns when the escalation is due no later than the reminder, and only when both are durations", () => {
    const backend = new MockBackend();
    const sig = (gate: Json) => (stagesOf(gate)[0].signatures as Json[])[1];
    edit(backend, (gate) => void (sig(gate).escalateAfter = "PT24H"));
    expect(find(backend, "MQ8205")).toEqual([`${GATE}/stages/0/signatures/1/escalateAfter`]);
    const rule = backend.model.validate().diagnostics.find((d) => d.rule === "MQ8205")!;
    expect(rule.severity).toBe("warning");
    edit(backend, (gate) => void (sig(gate).escalateAfter = "PT12H"));
    expect(find(backend, "MQ8205")).toEqual([`${GATE}/stages/0/signatures/1/escalateAfter`]);
    const fresh = new MockBackend();
    edit(fresh, (gate) => void ((stagesOf(gate)[0].signatures as Json[])[1].escalateAfter = "soon"));
    expect(find(fresh, "MQ8205")).toEqual([]);
  });

  it("MQ3001 two stages, or two signatures across stages, with one name", () => {
    const backend = new MockBackend();
    edit(backend, (gate) => void (stagesOf(gate)[1].name = "Review"));
    expect(find(backend, "MQ3001")).toContain(`${GATE}/stages/1/name`);
    edit(backend, (gate) => void ((stagesOf(gate)[1].signatures as Json[])[0].name = "Finance"));
    expect(find(backend, "MQ3001")).toContain(`${GATE}/stages/1/signatures/0/name`);
  });

  it("MQ8107 counts delegate as doable only where a signature may be delegated", () => {
    const backend = new MockBackend();
    const flip = (keep: boolean) =>
      edit(backend, (gate, json) => {
        const point = (json.triggerPoints as Json[]).find((t) => t.name === "AssignNumber")!;
        point.verbs = ["delegate"];
        delete point.sets;
        delete point.timing;
        point.type = "check";
        if (!keep) delete ((stagesOf(gate)[0].signatures as Json[])[1] as Json).delegateTo;
        if (!keep) delete ((stagesOf(gate)[0].signatures as Json[])[1] as Json).escalateTo;
        if (!keep) delete ((stagesOf(gate)[0].signatures as Json[])[1] as Json).escalateAfter;
      });
    flip(true);
    expect(find(backend, "MQ8107")).toEqual([]);
    flip(false);
    expect(find(backend, "MQ8107").length).toBe(1);
  });

  it("MQ8202 a stage's when in a gate policy", () => {
    const backend = new MockBackend();
    const library = [...backend.model.docs()].find(([, j]) => j.kind === "process" && j.use === "library");
    if (!library) return;
    const json = structuredClone(library[1]) as Json;
    const gates = (json.gates as Json[]) ?? [];
    gates.push({
      id: "01JQAPR0000000000000000950",
      name: "PolicyGate",
      meanings: [{ id: "01JQAPR0000000000000000951", name: "ok" }],
      stages: [
        {
          id: "01JQAPR0000000000000000952",
          name: "S",
          when: { text: "true" },
          signatures: [{ id: "01JQAPR0000000000000000953", name: "Sg", signers: [APPROVER] }],
        },
      ],
    });
    json.gates = gates;
    backend.model.save(String(json.id), json, backend.model.get(String(json.id))!.hash);
    expect(find(backend, "MQ8202").some((p) => p.endsWith("/stages/0/when"))).toBe(true);
  });
});

describe("part 2 references, deletes and quick fixes", () => {
  const docsOf = (backend: MockBackend) =>
    new Map<string, CascadeDoc>([...backend.model.docs()].map(([id, json]) => [id, { id, path: `${id}.json`, json: structuredClone(json) }]));

  it("reports delegateTo and escalateTo as removable references", () => {
    const doc = structuredClone(new MockBackend().model.get(INVOICE_LIFECYCLE)!.json) as Json;
    const refs = referencesOf(doc).filter((r) => r.field === "delegateTo" || r.field === "escalateTo");
    expect(refs.map((r) => r.pointer)).toEqual([`${GATE}/stages/0/signatures/1/delegateTo/0`, `${GATE}/stages/0/signatures/1/escalateTo/0`]);
    for (const r of refs) expect(r).toMatchObject({ required: false, removeItem: r.pointer, toId: APPROVER });
  });

  it("drops delegateTo, and escalateTo with escalateAfter, when their last actor goes", () => {
    const backend = new MockBackend();
    const docs = docsOf(backend);
    const doc = docs.get(INVOICE_LIFECYCLE)!;
    const json = structuredClone(doc.json) as Json;
    const manager = (stagesOf((json.transitions as Json[])[0].gate as Json)[0].signatures as Json[])[1];
    manager.delegateTo = [PROCUREMENT];
    manager.escalateTo = [PROCUREMENT];
    docs.set(INVOICE_LIFECYCLE, { ...doc, json });
    const plan = planDelete(docs, [PROCUREMENT], "remove-references");
    const after = plan.edited.get(INVOICE_LIFECYCLE) as Json;
    const left = (stagesOf((after.transitions as Json[])[0].gate as Json)[0].signatures as Json[])[1];
    expect(left.delegateTo).toBeUndefined();
    expect(left.escalateTo).toBeUndefined();
    expect(left.escalateAfter).toBeUndefined();
    expect(left.remindAfter).toBe("PT24H");
    void BUDGET_HOLDER;
  });

  it("keeps the other actors of a list when one goes", () => {
    const backend = new MockBackend();
    const docs = docsOf(backend);
    const doc = docs.get(INVOICE_LIFECYCLE)!;
    const json = structuredClone(doc.json) as Json;
    const manager = (stagesOf((json.transitions as Json[])[0].gate as Json)[0].signatures as Json[])[1];
    manager.escalateTo = [APPROVER, PROCUREMENT];
    docs.set(INVOICE_LIFECYCLE, { ...doc, json });
    const plan = planDelete(docs, [PROCUREMENT], "remove-references");
    const left = (stagesOf(((plan.edited.get(INVOICE_LIFECYCLE) as Json).transitions as Json[])[0].gate as Json)[0].signatures as Json[])[1];
    expect(left.escalateTo).toEqual([APPROVER]);
    expect(left.escalateAfter).toBe("P3D");
  });

  it("reports a step's delegateTo, onBehalfOf and expected notices, and drops them when their target goes", () => {
    const backend = new MockBackend();
    const docs = docsOf(backend);
    const gate = (docs.get(INVOICE_LIFECYCLE)!.json.transitions as Json[])[0].gate as Json;
    const manager = String((stagesOf(gate)[0].signatures as Json[])[1].id);
    const scenario: Json = {
      kind: "scenario",
      id: "SCN",
      name: "Sc",
      process: INVOICE_LIFECYCLE,
      participants: [{ id: "PA", name: "ann" }],
      steps: [
        { id: "ST", event: "e", response: "delegate", delegateTo: "PA", onBehalfOf: "PA", expect: { notices: [{ signature: manager, kind: "remind" }] } },
      ],
    };
    docs.set("SCN", { id: "SCN", path: "s.json", json: scenario });
    const refs = referencesOf(scenario);
    expect(refs.filter((r) => r.field === "delegateTo" || r.field === "onBehalfOf").map((r) => r.pointer)).toEqual([
      "/steps/0/delegateTo",
      "/steps/0/onBehalfOf",
    ]);
    expect(refs.find((r) => r.field === "notices")).toMatchObject({ toId: manager, removeItem: "/steps/0/expect/notices/0" });
    const plan = planDelete(docs, [BUDGET_HOLDER], "delete-dependents");
    const step = ((plan.edited.get("SCN") as Json).steps as Json[])[0];
    expect((step.expect as Json).notices).toBeUndefined();
  });

  it("MQ8204's quick fix adds the missing delay, or takes the lone key away, and MQ8203's reorders", () => {
    const manager = (json: Json) => (stagesOf((json.transitions as Json[])[0].gate as Json)[0].signatures as Json[])[1];
    const fix = (rule: string, pointer: string, update: (json: Json) => void) => {
      const json = structuredClone(new MockBackend().model.get(INVOICE_LIFECYCLE)!.json) as Json;
      update(json);
      return deriveQuickFix(
        { rule, severity: "error", message: "m", jsonPointer: pointer, elementId: INVOICE_LIFECYCLE } as Diagnostic,
        null,
        () => json as never,
      ) as {
        json: Json;
        label: string;
      } | null;
    };
    const at = `${GATE}/stages/0/signatures/1`;
    const added = fix("MQ8204", `${at}/escalateTo`, (json) => void delete manager(json).escalateAfter)!;
    expect(added.label).toBe("Add the missing delay");
    expect(typeof manager(added.json).escalateAfter).toBe("string");
    const removed = fix("MQ8204", `${at}/escalateAfter`, (json) => void delete manager(json).escalateTo)!;
    expect(removed.label).toBe("Remove the key");
    expect(manager(removed.json).escalateAfter).toBeUndefined();
    const bad = fix("MQ8204", `${at}/remindAfter`, (json) => void (manager(json).remindAfter = "soon"))!;
    expect(manager(bad.json).remindAfter).toBeUndefined();
    const reordered = fix("MQ8203", `${GATE}/stages/1`, (json) => void stagesOf((json.transitions as Json[])[0].gate as Json).reverse())!;
    expect(stagesOf((reordered.json.transitions as Json[])[0].gate as Json).map((s) => s.name)).toEqual(["Review", "Release"]);
  });
});
