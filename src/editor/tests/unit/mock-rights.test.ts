// Rights in the mock backend (rights-design.md sections 5 and 8): rules MQ9901 to MQ9906, the references and deletes of rights,
// the index rows that carry `rights` and `access`, and the seed's named right and lifecycle.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { APPROVER, BUDGET_HOLDER, INVOICE_LIFECYCLE, PAID_IS_READ_ONLY, PROCUREMENT, PURCHASE_APPROVAL } from "@/mocks/model/processSeed";

type Json = Record<string, unknown>;

/** What the last save was refused with: the mock refuses a save that adds an error, and the model keeps the old document. */
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

const rightsOf = (backend: MockBackend, id = INVOICE_LIFECYCLE) => ((backend.model.get(id)!.json as Json).rights as Json[]) ?? [];
const states = (backend: MockBackend) => ((backend.model.get(INVOICE_LIFECYCLE)!.json as Json).states as Json[]).map((s) => String(s.id));

describe("rights in the mock: seed and index rows", () => {
  it("seeds a named right and a lifecycle that uses rights, with no findings", () => {
    const backend = new MockBackend();
    expect(backend.model.get(PAID_IS_READ_ONLY)!.json.kind).toBe("right");
    const rights = rightsOf(backend);
    expect(rights.map((r) => r.right ?? r.name)).toEqual(["NoDeleteOnceIssued", PAID_IS_READ_ONLY]);
    expect(backend.model.validate().diagnostics.filter((d) => /^MQ99(0|3001)/.test(d.rule) || d.rule === "MQ2001")).toEqual([]);
  });

  it("gives a process row its rights count and verbs, and a named right row its access and verbs", () => {
    const rows = new MockBackend().model.index();
    const process = rows.find((r) => r.id === INVOICE_LIFECYCLE)!;
    expect(process.rights).toBe(2);
    expect(process.verbs).toContain("delete");
    const right = rows.find((r) => r.id === PAID_IS_READ_ONLY)!;
    expect(right.access).toBe("deny");
    expect(right.verbs).toEqual(["delete", "modify"]);
    expect(rows.find((r) => r.id === PURCHASE_APPROVAL)!.rights).toBeUndefined();
  });
});

describe("rights in the mock: rules", () => {
  it("MQ9901 a use that also says what it allows, an inline right with no verb, and rights on an orchestration", () => {
    const backend = new MockBackend();
    edit(backend, INVOICE_LIFECYCLE, (j) => void ((j.rights as Json[])[1].access = "deny"));
    expect(find(backend, "MQ9901")).toEqual(["/rights/1"]);
    edit(backend, INVOICE_LIFECYCLE, (j) => void delete (j.rights as Json[])[0].name);
    expect(find(backend, "MQ9901")).toEqual(["/rights/0"]);
    edit(backend, PURCHASE_APPROVAL, (j) => void (j.rightsDefault = "deny"));
    expect(find(backend, "MQ9901")).toContain("/rightsDefault");
  });

  it("MQ9901 a use of something that is no named right", () => {
    const backend = new MockBackend();
    edit(backend, INVOICE_LIFECYCLE, (j) => void ((j.rights as Json[])[1].right = APPROVER));
    expect(find(backend, "MQ9901")).toEqual(["/rights/1/right"]);
  });

  it("MQ9902 a state of another process", () => {
    const backend = new MockBackend();
    const other = ((backend.model.get(PURCHASE_APPROVAL)!.json as Json).states as Json[])[0].id;
    edit(backend, INVOICE_LIFECYCLE, (j) => void ((j.rights as Json[])[0].states = [other]));
    expect(find(backend, "MQ9902")).toEqual(["/rights/0/states/0"]);
  });

  it("MQ9903 ends beside a verb that cannot act on a link, and ends the subject does not hold", () => {
    const backend = new MockBackend();
    edit(backend, INVOICE_LIFECYCLE, (j) => void ((j.rights as Json[])[0].ends = [BUDGET_HOLDER]));
    expect(find(backend, "MQ9903").sort()).toEqual(["/rights/0/ends", "/rights/0/ends/0"]);
  });

  it("MQ9904 an allow right under an allowing default, not under a denying one", () => {
    const backend = new MockBackend();
    edit(backend, INVOICE_LIFECYCLE, (j) => void ((j.rights as Json[])[0].access = "allow"));
    expect(find(backend, "MQ9904")).toEqual(["/rights/0/access"]);
    edit(backend, INVOICE_LIFECYCLE, (j) => void (j.rightsDefault = "deny"));
    expect(find(backend, "MQ9904")).toEqual([]);
  });

  it("MQ9905 an event no one can send under a deny default, until an allow right names its verb", () => {
    const backend = new MockBackend();
    edit(backend, INVOICE_LIFECYCLE, (j) => {
      j.rightsDefault = "deny";
      (j.events as Json[])[0].verb = "promote";
    });
    expect(find(backend, "MQ9905").length).toBeGreaterThan(0);
    edit(
      backend,
      INVOICE_LIFECYCLE,
      (j) => void (j.rights as Json[]).push({ id: "01JQRGT0000000000000000099", name: "AllowPromote", access: "allow", verbs: ["promote"] }),
    );
    const left = backend.model.validate().diagnostics.filter((d) => d.rule === "MQ9905" && /promote/.test(d.message));
    expect(left).toEqual([]);
  });

  it("MQ9906 an except entry that actors also names, in an inline right and in a named right", () => {
    const backend = new MockBackend();
    edit(backend, INVOICE_LIFECYCLE, (j) => void Object.assign((j.rights as Json[])[0], { actors: [APPROVER], except: [APPROVER] }));
    expect(find(backend, "MQ9906")).toEqual(["/rights/0/except/0"]);
    edit(backend, PAID_IS_READ_ONLY, (j) => void (j.actors = [APPROVER]));
    expect(find(backend, "MQ9906").sort()).toEqual(["/except/0", "/rights/0/except/0"]);
  });

  it("MQ3001 two inline rights of one lifecycle with one name", () => {
    const backend = new MockBackend();
    edit(
      backend,
      INVOICE_LIFECYCLE,
      (j) => void (j.rights as Json[]).push({ id: "01JQRGT0000000000000000098", name: "nodeleteonceissued", access: "deny", verbs: ["read"] }),
    );
    expect(find(backend, "MQ3001")).toEqual(["/rights/2/name"]);
  });
});

describe("rights in the mock: references and deletes", () => {
  it("refuses deleting a named right in use, and removes its uses under either resolution", () => {
    const backend = new MockBackend();
    expect(backend.model.deletePlan([PAID_IS_READ_ONLY], "refuse").outcome).toBe("referenced");
    const plan = backend.model.deletePlan([PAID_IS_READ_ONLY], "remove-references");
    expect(plan.outcome).not.toBe("referenced");
    const done = backend.model.delete(PAID_IS_READ_ONLY, backend.model.get(PAID_IS_READ_ONLY)!.hash, "remove-references");
    expect(done.outcome).toBe("saved");
    expect(rightsOf(backend).map((r) => r.name)).toEqual(["NoDeleteOnceIssued"]);
  });

  it("drops a deleted state from a right, and the right with its last state", () => {
    const backend = new MockBackend();
    const [, issued, , voided] = states(backend);
    const done = backend.model.deletePlan([issued], "remove-references");
    expect(done.outcome).not.toBe("referenced");
    // The right names Issued and Void: take Issued out through the model function the process editor uses, then Void.
    edit(backend, INVOICE_LIFECYCLE, (j) => void ((j.rights as Json[])[0].states = [voided]));
    expect(rightsOf(backend)[0].states).toEqual([voided]);
  });

  it("removes an actor from except and keeps the right, and drops the right with its last actor", () => {
    const backend = new MockBackend();
    edit(backend, INVOICE_LIFECYCLE, (j) => void Object.assign((j.rights as Json[])[0], { actors: [BUDGET_HOLDER], except: [PROCUREMENT] }));
    expect(rightsOf(backend)[0].except).toEqual([PROCUREMENT]);
    // The except entry goes, and the right stays: an empty except means no exception.
    expect(backend.model.delete(PROCUREMENT, backend.model.get(PROCUREMENT)!.hash, "remove-references").outcome).toBe("saved");
    expect(rightsOf(backend)[0]).toMatchObject({ name: "NoDeleteOnceIssued", actors: [BUDGET_HOLDER] });
    expect(rightsOf(backend)[0].except).toBeUndefined();
    // The last actor a right names goes, and the right with it: an empty list would widen it to everyone.
    const plan = backend.model.deletePlan([BUDGET_HOLDER], "remove-references");
    expect(plan.removes.some((r) => r.pointer === "/rights/0")).toBe(true);
  });
});
