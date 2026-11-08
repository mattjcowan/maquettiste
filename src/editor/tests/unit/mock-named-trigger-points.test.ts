// Named trigger points in the mock backend (trigger-points-design.md decisions 33 and 34, part 2): the rules, the references and the
// cascade of the `trigger-point` kind, the index row, the stepper's order and the two sequence answers.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { BILLING, CUSTOMER_NUMBER, INVOICE } from "@/mocks/model/processSeed";
import { referencesOf } from "@/mocks/model/refs";
import { simulate } from "@/mocks/model/stepper";
import { namedTriggerDiagnostics } from "@/mocks/model/triggerRules";
import { entitySequenceOf, namedFor, sequenceOf } from "@/mocks/model/triggerRun";

type Json = Record<string, unknown>;

const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const [NP, R1, R2, A, B] = ["30", "31", "32", "33", "34"].map((n) => `01JQTGP00000000000000000${n}`);
const named = (extra: Json): Json => ({
  kind: "trigger-point",
  id: NP,
  name: "NP",
  package: BILLING,
  type: "check",
  verbs: ["create"],
  subjects: [{ entity: CUSTOMER }],
  ...extra,
});

/** The findings of one rule on a document saved (or refused) in a fresh model. */
function findings(rule: string, json: Json): string[] {
  const backend = new MockBackend();
  const result = backend.model.create(json);
  const diagnostics = result.outcome === "saved" ? backend.model.validate().diagnostics : result.diagnostics;
  return [...new Set(diagnostics.filter((d) => d.rule === rule).map((d) => `${d.jsonPointer}`))].sort();
}

describe("named trigger points in the mock: seed, index and rules", () => {
  it("seeds CustomerNumber with no findings, and its index row carries type, verbs and subjects", () => {
    const backend = new MockBackend();
    expect(backend.model.validate().diagnostics.filter((d) => /^MQ81/.test(d.rule) || d.rule === "MQ3001" || d.rule === "MQ2001")).toEqual([]);
    const row = backend.model.index().find((r) => r.id === CUSTOMER_NUMBER) as Json;
    expect(row).toMatchObject({ kind: "trigger-point", type: "action", verbs: ["create"], subjects: [CUSTOMER] });
    expect(row.subjectStereotypes).toBeUndefined();
  });

  it("gives the stereotype subjects' ids in subjectStereotypes", () => {
    const backend = new MockBackend();
    backend.model.create(named({ subjects: [{ stereotype: "audited" }] }));
    const row = backend.model.index().find((r) => r.id === NP) as Json;
    expect(row.subjectStereotypes).toHaveLength(1);
    expect(row.subjects).toBeUndefined();
  });

  it("MQ8101 shape: timing on a check, condition on an action, outside beside sets", () => {
    expect(findings("MQ8101", named({ timing: "before" }))).toContain("/timing");
    expect(findings("MQ8101", named({ type: "action", condition: { text: "true" } }))).toEqual(expect.arrayContaining(["/condition", ""]));
  });

  it("MQ8102 part 2: states, targets and transitions, and an exception no subject covers", () => {
    // The schema refuses the keys of one lifecycle first, so the rule is asked directly.
    const backend = new MockBackend();
    const emitted: string[] = [];
    const entries = () => backend.model.entries.values();
    namedTriggerDiagnostics(named({ states: ["X"], targets: ["Y"] }), entries as never, (rule, _s, _m, pointer) => emitted.push(`${rule} ${pointer}`));
    expect(emitted).toEqual(["MQ8102 /states", "MQ8102 /targets"]);
    expect(findings("MQ8102", named({ exceptEntities: [INVOICE] }))).toEqual(["/exceptEntities/0"]);
    expect(findings("MQ8102", named({ exceptEntities: [] }))).toEqual([]);
  });

  it("MQ8103 and MQ8105 apply to a named point as to an inline one", () => {
    expect(findings("MQ8103", named({ type: "replace", verbs: ["create"] }))).toEqual(["/verbs/0"]);
    expect(
      findings("MQ8105", named({ type: "action", timing: "after-commit", sets: [{ id: A, attribute: "01J92P0V0MS09YFZHX07JQ3KMN", value: { text: "1" } }] })),
    ).toEqual(["/sets"]);
  });

  it("MQ8104 refuses two named replaces on one verb and entity, the later by path", () => {
    const backend = new MockBackend();
    expect(backend.model.create(named({ id: R1, name: "AaReplace", type: "replace", verbs: ["delete"] })).outcome).toBe("saved");
    const second = backend.model.create(named({ id: R2, name: "BbReplace", type: "replace", verbs: ["delete"] }));
    expect(second.outcome).not.toBe("saved");
    expect(second.diagnostics.filter((d) => d.rule === "MQ8104").map((d) => d.elementId)).toEqual([R2]);
  });

  it("MQ8104 refuses a lifecycle replace on an entity a named replace covers", () => {
    const backend = new MockBackend();
    const result = backend.model.create(named({ id: R1, name: "InvoiceReplace", type: "replace", verbs: ["delete"], subjects: [{ entity: INVOICE }] }));
    const hits = result.diagnostics.filter((d) => d.rule === "MQ8104");
    expect(hits.length).toBeGreaterThan(0);
    expect(hits[0].jsonPointer).toMatch(/^\/triggerPoints\//);
  });

  it("MQ3001 names are unique per package, and the same name in another package is fine", () => {
    const backend = new MockBackend();
    backend.model.create(named({ id: A, name: "Same" }));
    const second = backend.model.create(named({ id: B, name: "Same" }));
    const diagnostics = second.outcome === "saved" ? backend.model.validate().diagnostics : second.diagnostics;
    expect(diagnostics.some((d) => d.rule === "MQ3001")).toBe(true);
  });
});

describe("named trigger points in the mock: references and delete", () => {
  it("names its subjects, exceptions and verbs as references", () => {
    const refs = referencesOf(named({ subjects: [{ entity: CUSTOMER }, { entity: INVOICE }], exceptEntities: [INVOICE] }));
    expect(refs.find((r) => r.pointer === "/subjects/0/entity")).toMatchObject({ toId: CUSTOMER, removeItem: "/subjects/0" });
    expect(refs.find((r) => r.pointer === "/exceptEntities/0")).toMatchObject({ toId: INVOICE, removeItem: "/exceptEntities/0" });
  });

  it("marks the last subject as taking the point with it", () => {
    const refs = referencesOf(named({}));
    expect(refs.find((r) => r.pointer === "/subjects/0/entity")).toMatchObject({ deletesHolder: true });
  });

  it("deleting the last subject entity deletes the point; deleting an excepted entity only drops the entry", () => {
    const backend = new MockBackend();
    expect(backend.model.deletePlan([CUSTOMER], "remove-references").deletes.map((d) => d.id)).toContain(CUSTOMER_NUMBER);
    backend.model.create(named({ id: NP, subjects: [{ entity: CUSTOMER }, { entity: INVOICE }], exceptEntities: [INVOICE] }));
    const plan = backend.model.deletePlan([INVOICE], "remove-references");
    expect(plan.deletes.map((d) => d.id)).not.toContain(NP);
    expect(plan.removes.length).toBeGreaterThan(0);
  });
});

describe("named trigger points in a run and its order (decision 34)", () => {
  const covers = (key: string, entity: string) => key === "audited" && entity !== "Plain";
  const chain = ["Leaf", "Mid", "Root"];
  it("orders the farthest base first, then entity-brought before stereotype-brought, then by name", () => {
    const list = [
      { id: "1", name: "Near", subjects: [{ entity: "Leaf" }] },
      { id: "2", name: "Far", subjects: [{ entity: "Root" }] },
      { id: "3", name: "Zed", subjects: [{ entity: "Mid" }] },
      { id: "4", name: "Aaa", subjects: [{ entity: "Mid" }] },
      { id: "5", name: "Star", subjects: [{ stereotype: "audited" }] },
      { id: "6", name: "Elsewhere", subjects: [{ entity: "Other" }] },
      { id: "7", name: "Skipped", subjects: [{ entity: "Root" }], exceptEntities: ["Mid"] },
    ];
    // The stereotype covers every entity of the chain, so its farthest base is the root: it comes after the entity-brought Far.
    expect(namedFor(list, [chain], covers).map((t) => t.name)).toEqual(["Far", "Star", "Aaa", "Zed", "Near"]);
  });

  const lifecycle: Json = {
    kind: "process",
    id: "P",
    name: "P",
    use: "lifecycle",
    subjects: [{ id: "s", entity: "E" }],
    states: [{ id: "A", name: "A" }],
    triggerPoints: [{ id: "own", name: "Own", type: "check", verbs: ["create"], condition: { value: true } }],
  };
  const options = (points: Json[]) => ({ attributesOf: () => [], subjectEntity: "E", namedPoints: points });
  const run = (t: { triggerPoints?: { triggerPoint: string; lifecycle?: string }[] }) =>
    (t.triggerPoints ?? []).map((r) => `${r.triggerPoint}:${r.lifecycle ?? "-"}`);

  it("runs named points covering the object before the lifecycle's own, with no lifecycle on them", () => {
    const points = [
      named({ id: "n1", name: "N1", type: "check", verbs: ["create"], subjects: [{ entity: "E" }] }),
      named({ id: "n2", name: "N2", type: "check", verbs: ["create"], subjects: [{ entity: "Z" }] }),
    ];
    const step = simulate(lifecycle, null, [{ input: "verb", verb: "create" }], options(points)).trace[1];
    expect(run(step)).toEqual(["n1:-", "own:P"]);
  });

  it("runs a create step on an entity with no lifecycle, and refuses by a named check", () => {
    const orchestration: Json = { kind: "process", id: "O", name: "O", use: "orchestration", states: [{ id: "A", name: "A" }] };
    const check = named({ id: "n1", name: "N1", type: "check", verbs: ["create"], subjects: [{ entity: "E" }] });
    const ok = simulate(orchestration, null, [{ input: "verb", verb: "create" }], options([check])).trace[1];
    expect(run(ok)).toEqual(["n1:-"]);
    expect(ok.accepted).toBe(true);
    const refused = simulate(orchestration, null, [{ input: "verb", verb: "create", assume: { n1: "error" } }], options([check])).trace[1];
    expect([refused.accepted, refused.refusal, refused.refusedBy]).toEqual([false, "check", "n1"]);
  });
});

describe("the mock's trigger sequences with named points", () => {
  const chain = (e: string) => [e];
  const covers = () => false;
  const lifecycle: Json = {
    kind: "process",
    id: "P",
    name: "P",
    use: "lifecycle",
    subjects: [{ id: "s", entity: "E" }],
    triggerPoints: [{ id: "own", name: "Own", type: "check", verbs: ["delete"] }],
  };
  const points = [named({ id: "n1", name: "N1", type: "check", verbs: ["delete"], noBypass: true, subjects: [{ entity: "E" }], exceptEntities: ["X"] })];

  it("lists named points before the lifecycle's, with subjects, exceptions and the bypass, and no lifecycle", () => {
    const steps = sequenceOf(lifecycle, { verb: "delete" }, chain, points, covers, ["E"]);
    const checks = steps.filter((s) => s.step === "checks");
    expect(checks.map((s) => s.triggerPoint)).toEqual(["n1", "own"]);
    expect(checks[0]).toMatchObject({ subjects: [{ entity: "E" }], exceptEntities: ["X"], noBypass: true });
    expect(checks[0].lifecycle).toBeUndefined();
    expect(checks[1].lifecycle).toBe("P");
  });

  it("answers for an entity alone, named points only, and the entity route refuses nothing it should list", () => {
    const steps = entitySequenceOf({ verb: "delete", entity: "E" }, chain, points, covers);
    expect(steps.map((s) => s.triggerPoint ?? s.step)).toEqual(["frozen", "n1", "effect"]);
    expect(entitySequenceOf({ verb: "delete", entity: "Other" }, chain, points, covers).some((s) => s.triggerPoint)).toBe(false);
  });
});
