// Trigger points in the mock backend (trigger-points-design.md sections 6 and 9): rules MQ8101 to MQ8107 and the new MQ3001 cases, the
// references and deletes of trigger points, the index rows that carry `triggerPoints`, and the seed's lifecycle.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { INVOICE, INVOICE_LIFECYCLE, PURCHASE_APPROVAL } from "@/mocks/model/processSeed";
import { referencesOf } from "@/mocks/model/refs";
import { processVerbs, verbUses } from "@/mocks/model/verbs";

type Json = Record<string, unknown>;

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

/** Saves the document with the edit applied, keeping a refusal's findings for `find`. */
function edit(backend: MockBackend, id: string, update: (json: Json) => void): void {
  const json = structuredClone(backend.model.get(id)!.json) as Json;
  update(json);
  const result = backend.model.save(id, json, backend.model.get(id)!.hash);
  refusals.set(backend, result.outcome === "saved" ? [] : result.diagnostics.map((d) => `${d.rule} ${d.jsonPointer}`));
}

const points = (backend: MockBackend, id = INVOICE_LIFECYCLE) => ((backend.model.get(id)!.json as Json).triggerPoints as Json[]) ?? [];
const byName = (backend: MockBackend, name: string) => points(backend).findIndex((t) => t.name === name);
/** Edits one seeded trigger point by name. */
const editPoint = (backend: MockBackend, name: string, update: (t: Json) => void) =>
  edit(backend, INVOICE_LIFECYCLE, (j) => update((j.triggerPoints as Json[])[(j.triggerPoints as Json[]).findIndex((t) => t.name === name)]));

describe("trigger points in the mock: seed and index rows", () => {
  it("seeds a lifecycle with trigger points of each type and no findings", () => {
    const backend = new MockBackend();
    const list = points(backend);
    expect(new Set(list.map((t) => t.type))).toEqual(new Set(["check", "action", "replace"]));
    expect(new Set(list.filter((t) => t.type === "action").map((t) => t.timing))).toEqual(new Set(["before", "with-change", "after-commit"]));
    expect(backend.model.validate().diagnostics.filter((d) => /^MQ81/.test(d.rule) || d.rule === "MQ3001" || d.rule === "MQ2001")).toEqual([]);
  });

  it("gives a process row its trigger point count, and its verbs include theirs", () => {
    const rows = new MockBackend().model.index();
    const process = rows.find((r) => r.id === INVOICE_LIFECYCLE)! as Json;
    expect(process.triggerPoints).toBe(8);
    expect(process.verbs).toEqual(expect.arrayContaining(["copy", "create", "modify", "delete"]));
    expect((rows.find((r) => r.id === PURCHASE_APPROVAL) as Json).triggerPoints).toBeUndefined();
  });

  it("lists the verbs of trigger points as verb uses", () => {
    const doc = new MockBackend().model.get(INVOICE_LIFECYCLE)!.json as Json;
    expect(verbUses(doc).filter((u) => u.owner === "trigger-point").length).toBeGreaterThan(5);
    expect(processVerbs(doc)).toContain("copy");
  });
});

describe("trigger points in the mock: rules", () => {
  it("MQ8101 timing missing on an action, or beside another type", () => {
    const backend = new MockBackend();
    editPoint(backend, "AssignNumber", (t) => void delete t.timing);
    expect(find(backend, "MQ8101")).toEqual([`/triggerPoints/${byName(backend, "AssignNumber")}`]);
    editPoint(backend, "TotalNotNegative", (t) => void (t.timing = "before"));
    expect(find(backend, "MQ8101")).toContain(`/triggerPoints/${byName(backend, "TotalNotNegative")}/timing`);
  });

  it("MQ8101 severity, condition and sets beside the wrong type, and outside on a point with no team code", () => {
    const backend = new MockBackend();
    editPoint(backend, "AssignNumber", (t) => void (t.severity = "error"));
    expect(find(backend, "MQ8101")).toEqual(["/triggerPoints/0/severity"]);
    editPoint(backend, "AssignNumber", (t) => void ((t.condition = { text: "true" }), delete t.severity));
    expect(find(backend, "MQ8101")).toEqual(["/triggerPoints/0/condition"]);
    editPoint(
      backend,
      "TotalNotNegative",
      (t) => void (t.sets = [{ id: "01JQTGP0000000000000000990", attribute: "01J92P0V0R3VSP7D5238DTNZX1", value: { text: "1" } }]),
    );
    expect(find(backend, "MQ8101")).toContain("/triggerPoints/1/sets");
    editPoint(backend, "TotalNotNegative", (t) => void ((t.outside = true), delete t.sets));
    expect(find(backend, "MQ8101")).toEqual(["/triggerPoints/1/outside"]);
  });

  it("MQ8101 a with-change action with sets on delete that no replace covers", () => {
    const backend = new MockBackend();
    // The seeded Archive replaces delete for every entity, so the sets pass; without it they are refused.
    editPoint(backend, "StampIssuedOn", (t) => void (t.verbs = ["delete"]));
    expect(find(backend, "MQ8101")).toEqual([]);
    const bare = new MockBackend();
    edit(bare, INVOICE_LIFECYCLE, (j) => {
      j.triggerPoints = (j.triggerPoints as Json[]).filter((t) => t.name !== "Archive");
      (j.triggerPoints as Json[]).find((t) => t.name === "StampIssuedOn")!.verbs = ["delete"];
    });
    expect(find(bare, "MQ8101")).toEqual([`/triggerPoints/${byName(bare, "StampIssuedOn")}/sets`]);
  });

  it("MQ8101 trigger points on an orchestration", () => {
    const backend = new MockBackend();
    edit(backend, PURCHASE_APPROVAL, (j) => void (j.triggerPoints = [{ id: "01JQTGP0000000000000000991", name: "X", type: "check", verbs: ["modify"] }]));
    expect(find(backend, "MQ8101")).toEqual(["/triggerPoints"]);
  });

  it("MQ8102 states, targets and transitions that are not the lifecycle's", () => {
    const backend = new MockBackend();
    const other = ((backend.model.get(PURCHASE_APPROVAL)!.json as Json).states as Json[])[0].id;
    editPoint(backend, "StampIssuedOn", (t) => void (t.states = [other]));
    expect(find(backend, "MQ8102")).toEqual([`/triggerPoints/${byName(backend, "StampIssuedOn")}/states/0`]);
    editPoint(backend, "StampIssuedOn", (t) => void ((t.states = []), (t.transitions = [String(other)])));
    expect(find(backend, "MQ8102")).toContain(`/triggerPoints/${byName(backend, "StampIssuedOn")}/transitions/0`);
  });

  it("MQ8102 targets beside a verb that is not a move verb, and attributes beside another verb", () => {
    const backend = new MockBackend();
    const issued = ((backend.model.get(INVOICE_LIFECYCLE)!.json as Json).states as Json[])[1].id;
    editPoint(backend, "TotalNotNegative", (t) => void (t.targets = [issued]));
    expect(find(backend, "MQ8102")).toEqual(["/triggerPoints/1/targets"]);
    const second = new MockBackend();
    editPoint(second, "TotalNotNegative", (t) => void (t.verbs = ["delete"]));
    expect(find(second, "MQ8102")).toEqual(["/triggerPoints/1/attributes"]);
  });

  it("MQ8102 an attribute the entity does not hold, and a set attribute it does not hold", () => {
    const backend = new MockBackend();
    editPoint(backend, "TotalNotNegative", (t) => void (t.attributes = [PURCHASE_APPROVAL]));
    expect(find(backend, "MQ8102")).toEqual(["/triggerPoints/1/attributes/0"]);
    editPoint(backend, "AssignNumber", (t) => void ((t.sets as Json[])[0].attribute = PURCHASE_APPROVAL));
    expect(find(backend, "MQ8102")).toContain("/triggerPoints/0/sets/0/attribute");
  });

  it("MQ8102 an entity the lifecycle does not govern", () => {
    const backend = new MockBackend();
    editPoint(backend, "TotalNotNegative", (t) => void (t.entities = [PURCHASE_APPROVAL]));
    expect(find(backend, "MQ8102")).toContain("/triggerPoints/1/entities/0");
  });

  it("MQ8103 a replace on a verb that does not allow it", () => {
    const backend = new MockBackend();
    editPoint(backend, "Archive", (t) => void (t.verbs = ["delete", "modify"]));
    expect(find(backend, "MQ8103")).toEqual([`/triggerPoints/${byName(backend, "Archive")}/type`]);
  });

  it("MQ8104 two replaces on one verb of one entity, in one lifecycle", () => {
    const backend = new MockBackend();
    edit(backend, INVOICE_LIFECYCLE, (j) => {
      (j.triggerPoints as Json[]).push({ id: "01JQTGP0000000000000000992", name: "Soft", type: "replace", verbs: ["delete"] });
    });
    expect(find(backend, "MQ8104")).toEqual(["/triggerPoints/8/verbs"]);
    // Narrowing one to an entity that is not the other's, or a different verb, clears it.
    edit(backend, INVOICE_LIFECYCLE, (j) => void ((j.triggerPoints as Json[])[7].verbs = ["revise"]));
    expect(find(backend, "MQ8104")).toEqual([]);
  });

  it("MQ8105 an after-commit action with sets, and MQ8106 a with-change action that reaches outside systems", () => {
    const backend = new MockBackend();
    editPoint(backend, "StampIssuedOn", (t) => void (t.timing = "after-commit"));
    expect(find(backend, "MQ8105")).toEqual([`/triggerPoints/${byName(backend, "StampIssuedOn")}/timing`]);
    const second = new MockBackend();
    editPoint(second, "PublishToPortal", (t) => void (t.timing = "with-change"));
    const found = second.model.validate().diagnostics.filter((d) => d.rule === "MQ8106");
    expect(found.map((d) => [d.severity, d.jsonPointer])).toEqual([["warning", `/triggerPoints/${byName(second, "PublishToPortal")}/timing`]]);
  });

  it("MQ8107 a point no move verb can reach, and a point on verbs no input does yet", () => {
    const backend = new MockBackend();
    editPoint(backend, "TotalNotNegative", (t) => void ((t.verbs = ["promote"]), delete t.attributes));
    expect(find(backend, "MQ8107")).toEqual(["/triggerPoints/1"]);
    editPoint(backend, "TotalNotNegative", (t) => void (t.verbs = ["override"]));
    expect(find(backend, "MQ8107")).toEqual(["/triggerPoints/1"]);
    // An event that names the verb makes it reachable.
    edit(backend, INVOICE_LIFECYCLE, (j) => void ((j.events as Json[])[0].verb = "override"));
    expect(find(backend, "MQ8107")).toEqual([]);
  });

  it("MQ3001 two trigger points with one name, and a trigger point named like a guard", () => {
    const backend = new MockBackend();
    editPoint(backend, "CopyInvoice", (t) => void (t.name = "archive"));
    expect(find(backend, "MQ3001")).toEqual(["/triggerPoints/7/name"]);
    const second = new MockBackend();
    edit(second, INVOICE_LIFECYCLE, (j) => void (j.guards = [{ id: "01JQGRD0000000000000000001", name: "CreditLimit" }]));
    expect(find(second, "MQ3001")).toEqual(["/triggerPoints/3/name"]);
  });
});

describe("trigger points in the mock: references and deletes", () => {
  it("a deleted entity drops from a list, and the point goes with its last one", () => {
    const backend = new MockBackend();
    editPoint(backend, "TotalNotNegative", (t) => void (t.entities = [INVOICE]));
    const plan = backend.model.deletePlan([INVOICE], "remove-references");
    const gone = plan.removes.find((r) => r.subKind === "trigger point");
    expect(gone?.what).toBe("trigger point TotalNotNegative");
    expect(gone?.because).toMatch(/only entit/);
  });

  it("references include the verbs of own verbs and the ids a trigger point names", () => {
    const doc = new MockBackend().model.get(INVOICE_LIFECYCLE)!.json as Json;
    const refs = referencesOf(doc);
    expect(refs.filter((r) => r.pointer.startsWith("/triggerPoints/1/attributes")).map((r) => r.toId)).toEqual(["01J92P0V0T6EA0XM025XQX8GBW"]);
    expect(refs.find((r) => r.pointer === "/triggerPoints/0/sets/0/attribute")).toMatchObject({ drops: true, removeItem: "/triggerPoints/0/sets/0" });
  });

  it("assume word keys are references to their trigger points, and boolean keys are not", () => {
    const scenario = {
      kind: "scenario",
      id: "SC",
      process: INVOICE_LIFECYCLE,
      steps: [{ id: "ST1", assume: { CHECK1: "error", GUARD1: true, CHECK2: "pass" } }],
    };
    const refs = referencesOf(scenario).filter((r) => r.field === "assume");
    expect(refs.map((r) => [r.toId, r.pointer, r.required])).toEqual([
      ["CHECK1", "/steps/0/assume/CHECK1", false],
      ["CHECK2", "/steps/0/assume/CHECK2", false],
    ]);
  });
});
