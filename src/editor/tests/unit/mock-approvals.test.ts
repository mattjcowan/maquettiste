// Approvals in the mock backend (signoff-design.md sections 6, 9 and 11): rules MQ8201 and MQ8202 with the MQ3001 cases, the changed
// trigger point rules, references and deletes of stages and signatures, notBy verbs in rename-verb and remove-verb, the index rows that
// carry `approvals` and `signatures`, the seed's approval and the quick fixes.
import { describe, expect, it } from "vitest";
import type { Diagnostic } from "@/api/types";
import { MockBackend } from "@/mocks/backend";
import { planDelete, type CascadeDoc } from "@/mocks/model/cascade";
import { APPROVER, BUDGET_HOLDER, INVOICE_LIFECYCLE, ISSUE_APPROVAL, PURCHASE_APPROVAL } from "@/mocks/model/processSeed";
import { referencesOf, subElementIds } from "@/mocks/model/refs";
import { verbOperation, verbUses } from "@/mocks/model/verbs";
import { deriveQuickFix } from "@/problems/quickFix";

type Json = Record<string, unknown>;

const GATE = "/transitions/0/gate";
const SIGNATURE = `${GATE}/stages/0/signatures`;

const refusals = new Map<MockBackend, string[]>();

/** The pointers of a rule's findings: those of the model, and of the last save when it was refused. */
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

/** Saves the lifecycle with the edit applied, keeping a refusal's findings for `find` (the engine refuses a save that has errors). */
function edit(backend: MockBackend, update: (json: Json, gate: Json) => void): void {
  const json = structuredClone(backend.model.get(INVOICE_LIFECYCLE)!.json) as Json;
  update(json, (json.transitions as Json[])[0].gate as Json);
  const result = backend.model.save(INVOICE_LIFECYCLE, json, backend.model.get(INVOICE_LIFECYCLE)!.hash);
  refusals.set(backend, result.outcome === "saved" ? [] : result.diagnostics.map((d) => `${d.rule} ${d.jsonPointer}`));
}
const sigs = (gate: Json) => ((gate.stages as Json[])[0].signatures as Json[]) ?? [];

describe("approvals in the mock: seed and index rows", () => {
  it("seeds one lifecycle gate as an approval with two named signatures and no findings", () => {
    const backend = new MockBackend();
    const gate = ((backend.model.get(INVOICE_LIFECYCLE)!.json as Json).transitions as Json[])[0].gate as Json;
    expect(gate.id).toBe(ISSUE_APPROVAL);
    expect(gate.signers).toBeUndefined();
    expect(sigs(gate).map((s) => s.name)).toEqual(["Finance", "Manager"]);
    expect(sigs(gate)[0].when).toBeDefined();
    expect(sigs(gate)[1].notBy).toEqual([{ verb: "promote" }]);
    const own = (d: Diagnostic) => /^MQ82/.test(d.rule) || ["MQ3001", "MQ9701", "MQ9704", "MQ2001", "MQ9104"].includes(d.rule);
    expect(backend.model.validate().diagnostics.filter(own)).toEqual([]);
  });

  it("gives a process row its approvals and signatures only when above zero", () => {
    const rows = new MockBackend().model.index() as Json[];
    expect(rows.find((r) => r.id === INVOICE_LIFECYCLE)).toMatchObject({ approvals: 1, signatures: 3 });
    const plain = rows.find((r) => r.id === PURCHASE_APPROVAL)!;
    expect(plain.approvals).toBeUndefined();
    expect(plain.signatures).toBeUndefined();
  });

  it("holds the stage and signature ids as sub-elements", () => {
    const doc = new MockBackend().model.get(INVOICE_LIFECYCLE)!.json as Json;
    const ids = subElementIds(doc);
    const gate = ((doc.transitions as Json[])[0].gate as Json).stages as Json[];
    expect(ids).toContain(String(gate[0].id));
    for (const s of sigs((doc.transitions as Json[])[0].gate as Json)) expect(ids).toContain(String(s.id));
  });
});

describe("approvals in the mock: rules", () => {
  it("MQ8201 a gate with stages that keeps a counting key, and allowSignerAcrossSignatures without stages", () => {
    const backend = new MockBackend();
    edit(backend, (_, g) => void ((g.required = 2), (g.signers = [APPROVER]), (g.allowRepeatSigner = true)));
    expect(find(backend, "MQ8201")).toEqual([`${GATE}/allowRepeatSigner`, `${GATE}/required`, `${GATE}/signers`]);
    const fresh = new MockBackend();
    edit(fresh, (_, g) => {
      g.signers = [APPROVER];
      delete g.stages;
      g.allowSignerAcrossSignatures = true;
    });
    expect(find(fresh, "MQ8201")).toEqual([`${GATE}/allowSignerAcrossSignatures`]);
  });

  it("MQ8202 a notBy transition that is not the lifecycle's, and a verb its runs cannot see", () => {
    const backend = new MockBackend();
    edit(
      backend,
      (_, g) => void (sigs(g)[1].notBy = [{ verb: "promote" }, { transition: "01JQAPR0000000000000000999" }, { verb: "delete" }, { verb: "complete" }]),
    );
    expect(find(backend, "MQ8202")).toEqual([`${SIGNATURE}/1/notBy/1`, `${SIGNATURE}/1/notBy/2`]);
  });

  it("MQ8202 a notBy verb an event of the lifecycle names is seen, an own verb no event names is not", () => {
    const backend = new MockBackend();
    edit(backend, (j, g) => {
      (j.events as Json[])[0].verb = "x-reviewed";
      sigs(g)[1].notBy = [{ verb: "x-reviewed" }, { verb: "x-other" }];
    });
    expect(find(backend, "MQ8202")).toEqual([`${SIGNATURE}/1/notBy/1`]);
  });

  it("MQ8202 a notBy transition of the lifecycle is fine, and a when on a library's gate policy is not", () => {
    const backend = new MockBackend();
    edit(backend, (j, g) => void (sigs(g)[1].notBy = [{ transition: String((j.transitions as Json[])[0].id) }]));
    expect(find(backend, "MQ8202")).toEqual([]);
    const lib: Json = {
      kind: "process",
      id: "01JQAPR0000000000000000900",
      name: "Policies",
      use: "library",
      gates: [
        {
          id: "01JQAPR0000000000000000901",
          name: "Policy",
          meanings: [{ id: "01JQAPR0000000000000000902", name: "ok" }],
          stages: [
            {
              id: "01JQAPR0000000000000000903",
              name: "S",
              signatures: [
                {
                  id: "01JQAPR0000000000000000904",
                  name: "One",
                  signers: [APPROVER],
                  when: { text: "true" },
                  notBy: [{ transition: "01JQAPR0000000000000000999" }, { verb: "promote" }],
                },
              ],
            },
          ],
        },
      ],
    };
    const result = backend.model.create(lib);
    refusals.set(backend, result.outcome === "saved" ? [] : result.diagnostics.map((d) => `${d.rule} ${d.jsonPointer}`));
    expect(find(backend, "MQ8202")).toEqual(["/gates/0/stages/0/signatures/0/notBy/0", "/gates/0/stages/0/signatures/0/when"]);
  });

  it("MQ3001 two signatures of one gate with one name (a second stage is part 2: the schema holds one)", () => {
    const backend = new MockBackend();
    edit(backend, (_, g) => void (sigs(g)[1].name = "Finance"));
    expect(find(backend, "MQ3001")).toEqual([`${SIGNATURE}/1/name`]);
  });

  it("MQ9701 and MQ9704 reach a notBy verb", () => {
    const backend = new MockBackend();
    edit(backend, (_, g) => void (sigs(g)[1].notBy = [{ verb: "no-such-verb" }]));
    expect(find(backend, "MQ9701")).toEqual([`${SIGNATURE}/1/notBy/0/verb`]);
    expect(verbUses(backend.model.get(INVOICE_LIFECYCLE)!.json as Json).filter((u) => u.owner === "not-by")).toHaveLength(1);
  });
});

describe("approvals in the mock: trigger point rules that change", () => {
  const point = (transition: string): Json => ({
    id: "01JQAPR0000000000000000960",
    name: "OnReject",
    type: "check",
    verbs: ["reject"],
    transitions: [transition],
    condition: { text: "true" },
  });
  it("allows transitions beside reject, and does not call it dead, in a lifecycle with an approval", () => {
    const backend = new MockBackend();
    edit(backend, (j) => void (j.triggerPoints as Json[]).push(point(String((j.transitions as Json[])[0].id))));
    expect(find(backend, "MQ8102").filter((p) => String(p).includes("/transitions"))).toEqual([]);
    expect(find(backend, "MQ8107")).toEqual([]);
  });

  it("keeps both findings where no gate has stages", () => {
    const backend = new MockBackend();
    edit(backend, (j, g) => {
      delete g.stages;
      g.signers = [APPROVER];
      (j.triggerPoints as Json[]).push(point(String((j.transitions as Json[])[0].id)));
    });
    expect(find(backend, "MQ8102").some((p) => String(p).endsWith("/transitions"))).toBe(true);
    const fresh = new MockBackend();
    edit(fresh, (j, g) => {
      delete g.stages;
      g.signers = [APPROVER];
      (j.triggerPoints as Json[]).push({ ...point("x"), transitions: undefined });
    });
    expect(find(fresh, "MQ8107")).toHaveLength(1);
  });
});

describe("approvals in the mock: references, deletes and verbs", () => {
  const model = () => {
    const backend = new MockBackend();
    const docs = new Map<string, CascadeDoc>();
    for (const [id, json] of backend.model.docs()) docs.set(id, { id, path: id, json });
    return { backend, docs };
  };

  it("reports signers as required, required actors as removable, and the ids a when reads as required", () => {
    const { backend } = model();
    const doc = structuredClone(backend.model.get(INVOICE_LIFECYCLE)!.json) as Json;
    sigs((doc.transitions as Json[])[0].gate as Json)[0].requiredActors = [APPROVER];
    sigs((doc.transitions as Json[])[0].gate as Json)[0].when = { path: ["01JQAPR0000000000000000990"] };
    const refs = referencesOf(doc).filter((r) => r.pointer.startsWith(SIGNATURE));
    const signer = refs.find((r) => r.pointer === `${SIGNATURE}/0/signers/0`)!;
    expect(signer).toMatchObject({ toId: APPROVER, required: true, fromId: String(sigs((doc.transitions as Json[])[0].gate as Json)[0].id) });
    expect(refs.find((r) => r.pointer === `${SIGNATURE}/0/requiredActors/0`)).toMatchObject({ required: false, removeItem: `${SIGNATURE}/0/requiredActors/0` });
    expect(refs.some((r) => r.pointer.startsWith(`${SIGNATURE}/0/when`) && r.required)).toBe(true);
  });

  it("refuses to delete an actor that signs under remove-references, and takes the signature under delete-dependents", () => {
    const { docs } = model();
    const refused = planDelete(docs, [BUDGET_HOLDER], "remove-references");
    expect(refused.plan.refused.some((r) => r.id === INVOICE_LIFECYCLE && r.pointer === `${SIGNATURE}/1/signers/0`)).toBe(true);
    const dependents = planDelete(docs, [BUDGET_HOLDER], "delete-dependents");
    const edited = dependents.edited.get(INVOICE_LIFECYCLE) as Json;
    expect(sigs((edited.transitions as Json[])[0].gate as Json).map((s) => s.name)).toEqual(["Finance"]);
  });

  it("takes the gate with an approval's last signature", () => {
    const { docs } = model();
    const doc = docs.get(INVOICE_LIFECYCLE)!;
    const json = structuredClone(doc.json) as Json;
    ((json.transitions as Json[])[0].gate as Json).stages = [{ id: "S1", name: "S", signatures: [sigs((json.transitions as Json[])[0].gate as Json)[1]] }];
    docs.set(INVOICE_LIFECYCLE, { ...doc, json });
    const plan = planDelete(docs, [BUDGET_HOLDER], "delete-dependents");
    expect((plan.edited.get(INVOICE_LIFECYCLE) as Json).transitions).toSatisfy((t: Json[]) => t[0].gate === undefined);
  });

  it("clears a step's signature and drops the expected count of a signature that goes with its actor", () => {
    const { docs } = model();
    const manager = String(sigs((docs.get(INVOICE_LIFECYCLE)!.json.transitions as Json[])[0].gate as Json)[1].id);
    const scenario: Json = {
      kind: "scenario",
      id: "SCN",
      name: "Sc",
      process: INVOICE_LIFECYCLE,
      steps: [{ id: "ST", event: "e", signature: manager, expect: { signatures: { [manager]: 1 } } }],
    };
    docs.set("SCN", { id: "SCN", path: "s.json", json: scenario });
    const fields = referencesOf(scenario).map((r) => r.field);
    expect(fields).toEqual(expect.arrayContaining(["process", "signature", "signatures"]));
    const plan = planDelete(docs, [BUDGET_HOLDER], "delete-dependents");
    const step = ((plan.edited.get("SCN") as Json).steps as Json[])[0];
    expect(step.signature).toBeUndefined();
    expect(step.expect).toEqual({});
  });

  it("rename-verb and remove-verb walk notBy", () => {
    const { backend } = model();
    const docs = new Map<string, Json>([...backend.model.docs()].map(([id, json]) => [id, structuredClone(json)]));
    const vocabulary: Json = { kind: "verb-vocabulary", id: "01JQAPR0000000000000000980", name: "verbs", verbs: [] };
    docs.set(String(vocabulary.id), vocabulary);
    const lifecycle = docs.get(INVOICE_LIFECYCLE)!;
    sigs((lifecycle.transitions as Json[])[0].gate as Json)[1].notBy = [{ verb: "x-one" }, { verb: "promote" }];
    (vocabulary.verbs as Json[]).push({ id: "01JQAPR0000000000000000970", key: "x-one", displayName: "One" });
    const renamed = verbOperation(docs, { op: "rename-verb", verb: "x-one", to: "x-two" });
    expect("error" in renamed).toBe(false);
    const after = (renamed as { changed: Map<string, Json> }).changed.get(INVOICE_LIFECYCLE)!;
    expect(sigs((after.transitions as Json[])[0].gate as Json)[1].notBy).toEqual([{ verb: "x-two" }, { verb: "promote" }]);
    const removed = verbOperation(docs, { op: "remove-verb", verb: "x-one" });
    const left = (removed as { changed: Map<string, Json> }).changed.get(INVOICE_LIFECYCLE)!;
    expect(sigs((left.transitions as Json[])[0].gate as Json)[1].notBy).toEqual([{ verb: "promote" }]);
  });
});

describe("approvals: quick fixes", () => {
  const fix = (rule: string, pointer: string, json: Json) =>
    deriveQuickFix({ rule, severity: "error", message: "m", jsonPointer: pointer, elementId: "P" } as Diagnostic, null, () => json as never) as {
      json: Json;
      label: string;
    } | null;
  const process = (): Json => ({
    kind: "process",
    id: "P",
    name: "P",
    use: "lifecycle",
    transitions: [
      {
        id: "t",
        gate: {
          id: "g",
          name: "G",
          required: 2,
          stages: [{ id: "s", name: "S", signatures: [{ id: "x", name: "X", signers: ["a"], notBy: [{ verb: "delete" }] }] }],
        },
      },
    ],
  });

  it("MQ8201 removes the key", () => {
    const f = fix("MQ8201", "/transitions/0/gate/required", process())!;
    expect(f.label).toBe("Remove the key");
    expect("required" in (((f.json.transitions as Json[])[0].gate as Json) ?? {})).toBe(false);
  });

  it("MQ8202 removes the entry, and the notBy key with its last entry", () => {
    const f = fix("MQ8202", "/transitions/0/gate/stages/0/signatures/0/notBy/0", process())!;
    expect(f.label).toBe("Remove the entry");
    const sig = ((((f.json.transitions as Json[])[0].gate as Json).stages as Json[])[0].signatures as Json[])[0];
    expect("notBy" in sig).toBe(false);
    const when = fix("MQ8202", "/transitions/0/gate/stages/0/signatures/0/notBy", process())!;
    expect(when.label).toBe("Remove the key");
  });

  it("MQ8203 puts the stages in round order", () => {
    const p = process();
    ((p.transitions as Json[])[0].gate as Json).stages = [
      { id: "b", name: "B", round: 2, signatures: [] },
      { id: "a", name: "A", signatures: [] },
    ];
    const f = fix("MQ8203", "/transitions/0/gate/stages", p)!;
    expect(f.label).toBe("Reorder the stages");
    expect((((f.json.transitions as Json[])[0].gate as Json).stages as Json[]).map((s) => s.id)).toEqual(["a", "b"]);
  });
});
