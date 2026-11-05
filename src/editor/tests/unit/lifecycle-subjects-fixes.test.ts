// Review fixes for the editor and mock of roadmap item 4.1 (lifecycle-design.md decisions 5 to 10): findings about a
// `subjects` entry are reported under the entry's id, the Use and Subject controls keep the model valid, and the mock
// agrees with the engine on the lifecycle rules.
import { describe, expect, it } from "vitest";
import type { Diagnostic, ModelJson } from "@/api/types";
import { MockBackend } from "@/mocks/backend";
import { INVOICE, INVOICE_LIFECYCLE, INVOICE_STATUS, PAYMENT_LIFECYCLE, PURCHASE_APPROVAL } from "@/mocks/model/processSeed";
import { subjectsOf } from "@/model/process";
import { deriveQuickFix } from "@/problems/quickFix";
import { freshQuickFix } from "@/problems/applyQuickFix";
import { demoteLifecycle, replaceSoleSubject } from "@/editors/process/api";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;
type Answer = { outcome: string; items: { diagnostics: Diagnostic[] }[] };
const rule = (backend: MockBackend, id: string) => backend.model.validate().diagnostics.filter((d) => d.rule === id);
const run = (backend: MockBackend, operations: unknown[]) => backend.model.batch({ operations } as never).body as unknown as Answer;
const json = (backend: MockBackend, id: string) => backend.model.get(id)!.json as Json;
const hash = (backend: MockBackend, id: string) => backend.model.get(id)!.hash;
const create = (backend: MockBackend, element: Json) => {
  expect(run(backend, [{ op: "create", element }]).outcome).toBe("saved");
  return String(element.id);
};
const update = (backend: MockBackend, id: string, element: Json) => run(backend, [{ op: "update", id, expectedHash: hash(backend, id), element }]);
const entity = (id: string, name: string, more: Json = {}): Json => ({ kind: "entity", id, name, abstract: true, attributes: [], ...more });
const state = (id: string, name = "A") => ({ id, name });

describe("findings about a subjects entry", () => {
  it("the mock reports them under the entry's id, and one-subject findings under the process's id", () => {
    const backend = new MockBackend({ scenarios: ["drift"] });
    const entry = subjectsOf(json(backend, PAYMENT_LIFECYCLE))[0].id;
    expect(entry).not.toBe(PAYMENT_LIFECYCLE);
    expect(rule(backend, "MQ9203").map((d) => d.elementId)).toEqual([entry]);
    // Listed twice: the second entry carries the finding.
    const list = json(backend, INVOICE_LIFECYCLE);
    const first = subjectsOf(list)[0];
    list.subjects = [first, { id: "01JQZZZ0000000000000000001", entity: first.entity }];
    const saved = update(backend, INVOICE_LIFECYCLE, list);
    expect(saved.outcome).toBe("invalid");
    const twice = saved.items[0].diagnostics.filter((d) => d.rule === "MQ9201");
    expect(twice.map((d) => [d.elementId, d.jsonPointer])).toEqual([["01JQZZZ0000000000000000001", "/subjects/1/entity"]]);
  });

  it("the one-subject form's findings stay under the process's id", () => {
    const backend = new MockBackend({ scenarios: ["lifecycle"] });
    const found = [...rule(backend, "MQ9201"), ...rule(backend, "MQ9205")];
    expect(found.length).toBeGreaterThan(0);
    for (const d of found) expect(backend.model.get(d.elementId!)!.json.id).toBe(d.elementId);
  });

  it("a fix derives from the fetched process, not from the finding's element id", () => {
    const process = {
      kind: "process",
      id: "P",
      name: "Order",
      use: "lifecycle",
      initial: "S1",
      states: [{ id: "S1", name: "Review" }],
      subjects: [{ id: "X1", entity: "E", boundAttribute: "A" }],
    } as unknown as ModelJson;
    const subject = { kind: "entity", id: "E", name: "Order", attributes: [{ id: "A", name: "status", type: { ref: "N" } }] } as unknown as ModelJson;
    const finding = (ruleId: string, pointer: string): Diagnostic => ({
      rule: ruleId,
      severity: "error",
      message: ruleId,
      elementId: "X1",
      filePath: null,
      jsonPointer: pointer,
      line: null,
      column: null,
    });
    // A fetch by the entry's id answers the process, as the engine does.
    const docs = (id: string) => (id === "X1" || id === "P" ? process : id === "E" ? subject : undefined);
    expect(deriveQuickFix(finding("MQ9203", "/subjects/0/boundAttribute"), "sync-enum", docs)).toEqual({
      kind: "sync-enum",
      process: "P",
      label: "Sync enum",
      entity: "E",
    });
    expect(deriveQuickFix(finding("MQ9201", "/subjects/0/entity"), null, docs)).toMatchObject({ op: "set-lifecycle", id: "E", target: "P" });
  });
});

describe("the Sync enum fix against the mock", () => {
  const api = useMockApi({ scenarios: ["drift"] });

  it("derives from the entry-id finding with the process's id", async () => {
    const d = rule(api.backend, "MQ9203")[0];
    expect(d.elementId).not.toBe(PAYMENT_LIFECYCLE);
    expect(await freshQuickFix(api.services.drafts, d, "sync-enum")).toEqual({
      kind: "sync-enum",
      process: PAYMENT_LIFECYCLE,
      label: "Sync enum",
      entity: subjectsOf(json(api.backend, PAYMENT_LIFECYCLE))[0].entity,
    });
  });
});

describe("the Subject control", () => {
  const api = useMockApi();

  it("changing the subject of a one-entry subjects lifecycle moves the entity's lifecycle in one batch", async () => {
    const { queryClient, store, drafts } = api.services;
    const before = subjectsOf(json(api.backend, INVOICE_LIFECYCLE))[0];
    expect(json(api.backend, before.entity).lifecycle).toBe(INVOICE_LIFECYCLE);
    const other = create(api.backend, entity("01JQZZZ0000000000000000010", "Quote"));
    const refused = await replaceSoleSubject(queryClient, store, drafts, { process: INVOICE_LIFECYCLE, entity: other, label: "Set the subject" });
    expect(refused).toBeNull();
    const after = subjectsOf(json(api.backend, INVOICE_LIFECYCLE));
    expect(after.map((e) => e.entity)).toEqual([other]);
    expect(after[0].id).toBe(before.id);
    expect(json(api.backend, before.entity).lifecycle).toBeUndefined();
    expect(json(api.backend, other).lifecycle).toBe(INVOICE_LIFECYCLE);
    expect(rule(api.backend, "MQ9201")).toEqual([]);
  });

  it("the old entity's naming is what the mock now catches: an edit in place alone is refused", () => {
    const list = json(api.backend, INVOICE_LIFECYCLE);
    const other = create(api.backend, entity("01JQZZZ0000000000000000011", "Quote"));
    list.subjects = [{ id: subjectsOf(list)[0].id, entity: other }];
    const answer = update(api.backend, INVOICE_LIFECYCLE, list);
    expect(answer.outcome).toBe("invalid");
    expect(answer.items[0].diagnostics.some((d) => d.rule === "MQ9201" && d.jsonPointer === "/lifecycle")).toBe(true);
  });
});

describe("the Use control", () => {
  const api = useMockApi({ scenarios: ["lifecycle"] });

  it("demoting a one-subject lifecycle keeps its subject, drops its binding and leaves the entity's other lifecycles alone", async () => {
    const { queryClient, store, drafts } = api.services;
    const one = rule(api.backend, "MQ9209")[0].elementId!;
    const before = json(api.backend, one);
    expect(before.boundAttribute).toBeDefined();
    // A second lifecycle lists the same entity.
    const second = json(api.backend, PURCHASE_APPROVAL);
    second.use = "lifecycle";
    second.subjects = [{ id: "01JQZZZ0000000000000000050", entity: before.subject }];
    expect(update(api.backend, PURCHASE_APPROVAL, second).outcome).toBe("saved");
    const refused = await demoteLifecycle(queryClient, store, drafts, { process: one, label: "Make it an orchestration" });
    expect(refused).toBeNull();
    const after = json(api.backend, one);
    expect(after.subject).toBe(before.subject);
    expect(after.boundAttribute).toBeUndefined();
    expect(after.use).toBeUndefined();
    expect(json(api.backend, PURCHASE_APPROVAL)).toMatchObject({ use: "lifecycle", subjects: [{ entity: before.subject }] });
  });
});

describe("the mock agrees with the engine", () => {
  it("MQ9201 on an entity: an orchestration, or a lifecycle that does not list it or a base of it", () => {
    const backend = new MockBackend();
    const base = create(backend, entity("01JQZZZ0000000000000000020", "Doc"));
    const derived = create(backend, entity("01JQZZZ0000000000000000021", "Memo", { base }));
    const naming = () => update(backend, derived, { ...json(backend, derived), lifecycle: PURCHASE_APPROVAL });
    const refusal = (answer: Answer) => answer.items[0].diagnostics.filter((d) => d.rule === "MQ9201").map((d) => [d.elementId, d.jsonPointer]);
    // PURCHASE_APPROVAL is an orchestration.
    expect(refusal(naming())).toEqual([[derived, "/lifecycle"]]);
    // A lifecycle that lists another entity does not cover it.
    const process = json(backend, PURCHASE_APPROVAL);
    process.use = "lifecycle";
    process.subjects = [{ id: "01JQZZZ0000000000000000022", entity: INVOICE }];
    expect(update(backend, PURCHASE_APPROVAL, process).outcome).toBe("saved");
    expect(refusal(naming())).toEqual([[derived, "/lifecycle"]]);
    // Listing the base covers the derived entity.
    process.subjects = [{ id: "01JQZZZ0000000000000000022", entity: base }];
    expect(update(backend, PURCHASE_APPROVAL, process).outcome).toBe("saved");
    expect(naming().outcome).toBe("saved");
    expect(rule(backend, "MQ9201")).toEqual([]);
    // Taking the base away again is refused, since the entity names the process.
    process.subjects = [{ id: "01JQZZZ0000000000000000022", entity: INVOICE }];
    expect(update(backend, PURCHASE_APPROVAL, process).outcome).toBe("invalid");
  });

  it("set-lifecycle does not add an entity whose base is already listed", () => {
    const backend = new MockBackend();
    const base = create(backend, entity("01JQZZZ0000000000000000030", "Doc"));
    const derived = create(backend, entity("01JQZZZ0000000000000000031", "Memo", { base }));
    expect(run(backend, [{ op: "set-lifecycle", id: base, target: PURCHASE_APPROVAL }]).outcome).toBe("saved");
    expect(run(backend, [{ op: "set-lifecycle", id: derived, target: PURCHASE_APPROVAL }]).outcome).toBe("saved");
    expect(subjectsOf(json(backend, PURCHASE_APPROVAL)).map((e) => e.entity)).toEqual([base]);
    expect(json(backend, derived).lifecycle).toBe(PURCHASE_APPROVAL);
  });

  it("MQ9209 fires only on a subject and never on a library", () => {
    const backend = new MockBackend();
    create(backend, { kind: "process", id: "01JQZZZ0000000000000000040", name: "Shared", use: "library", subject: INVOICE, states: [] });
    create(backend, {
      kind: "process",
      id: "01JQZZZ0000000000000000041",
      name: "Bare",
      boundAttribute: INVOICE_STATUS,
      states: [state("01JQZZZ0000000000000000042")],
    });
    expect(rule(backend, "MQ9209")).toEqual([]);
  });

  it("an empty subjects list reads as the engine's text at /subjects, and no subject keeps today's text at /use", () => {
    const backend = new MockBackend();
    const empty = {
      kind: "process",
      id: "01JQZZZ0000000000000000043",
      name: "Empty",
      use: "lifecycle",
      subjects: [],
      states: [state("01JQZZZ0000000000000000044")],
    };
    // The save is refused; the diagnostics say why.
    const refused = run(backend, [{ op: "create", element: empty }]);
    expect(refused.outcome).toBe("invalid");
    expect(refused.items[0].diagnostics.filter((d) => d.rule === "MQ9201").map((d) => [d.jsonPointer, d.message])).toEqual([
      ["/subjects", "Process 'Empty' is a lifecycle with an empty subjects list; list the entities it governs."],
    ]);
    const none = { kind: "process", id: "01JQZZZ0000000000000000045", name: "None", use: "lifecycle", states: [state("01JQZZZ0000000000000000046")] };
    const noSubject = run(backend, [{ op: "create", element: none }]);
    expect(noSubject.items[0].diagnostics.filter((d) => d.rule === "MQ9201").map((d) => d.jsonPointer)).toEqual(["/use"]);
  });

  it("use-subjects with an id that is not a process is refused with MQ9019", () => {
    const backend = new MockBackend();
    const answer = run(backend, [{ op: "use-subjects", elementIds: [INVOICE] }]);
    expect(answer.outcome).toBe("invalid");
    expect(answer.items[0].diagnostics.map((d) => d.rule)).toEqual(["MQ9019"]);
  });
});
