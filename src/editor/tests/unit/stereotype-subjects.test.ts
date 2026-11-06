// A stereotype as a set of types (stereotype-sets-design.md section 8): subjectsOf keeps stereotype entries, and the mock
// agrees with the engine on coverage, the index row, the rules, deletes and sync-enum / sync-codes by stereotype.
import { describe, expect, it } from "vitest";
import type { Diagnostic } from "@/api/types";
import { MockBackend } from "@/mocks/backend";
import { subjectsOf, soleSubject, stereotypeAppliesToEntities } from "@/model/process";
import { buildForest, childKeys, nodeOf } from "@/explorer/tree";
import { stereotypeFields } from "@/model/objectFields";

type Json = Record<string, unknown>;
type Answer = { outcome: string; items: { diagnostics: Diagnostic[] }[] };

const PKG = "01JQST00000000000000000001";
const STEREO = "01JQST00000000000000000002";
const STATUS = "01JQST00000000000000000003";
const OWNER = "01JQST00000000000000000004";
const COLUMN_ONLY = "01JQST00000000000000000005";
const ORDER = "01JQST00000000000000000010";
const QUOTE = "01JQST00000000000000000011";
const DERIVED = "01JQST00000000000000000012";
const OTHER = "01JQST00000000000000000013";
const PROCESS = "01JQST00000000000000000020";
const ENTRY = "01JQST00000000000000000021";
const ENUM = "01JQST00000000000000000030";
const ENUM_ATTR = "01JQST00000000000000000031";
const STEREO_ENUM = "01JQST00000000000000000032";

const run = (backend: MockBackend, operations: unknown[]) => backend.model.batch({ operations } as never).body as unknown as Answer;
const json = (backend: MockBackend, id: string) => backend.model.get(id)!.json as Json;
const rules = (backend: MockBackend, id: string) => backend.model.validate().diagnostics.filter((d) => d.rule === id);
const row = (backend: MockBackend, id: string) => backend.model.index().find((r) => r.id === id)!;

function model(options: { entries?: Json[]; processExtras?: Json } = {}) {
  const backend = new MockBackend();
  const entity = (id: string, name: string, more: Json = {}): Json => ({ kind: "entity", id, name, abstract: true, attributes: [], ...more });
  const created = run(backend, [
    { op: "create", element: { kind: "package", id: PKG, name: "Sales", defaultStereotypes: ["approvable"] } },
    {
      op: "create",
      element: {
        kind: "stereotype",
        id: STEREO,
        key: "approvable",
        name: "Approvable",
        appliesTo: ["entity"],
        attributes: [
          { id: STATUS, name: "approvalStatus", type: "string" },
          { id: OWNER, name: "requestedBy", type: "string" },
        ],
      },
    },
    { op: "create", element: { kind: "stereotype", id: COLUMN_ONLY, key: "columnar", name: "Columnar", appliesTo: ["table"] } },
    // Order and Quote carry approvable by the package's default; Other excludes it; Derived is a Order.
    { op: "create", element: entity(ORDER, "Order", { package: PKG }) },
    { op: "create", element: entity(QUOTE, "Quote", { package: PKG }) },
    { op: "create", element: entity(OTHER, "Other", { package: PKG, excludeStereotypes: ["approvable"] }) },
    { op: "create", element: entity(DERIVED, "Derived", { package: PKG, base: ORDER, excludeStereotypes: ["approvable"] }) },
    {
      op: "create",
      element: {
        kind: "process",
        id: PROCESS,
        name: "Approval",
        use: "lifecycle",
        initial: "01JQST00000000000000000040",
        states: [{ id: "01JQST00000000000000000040", name: "Draft" }],
        subjects: [{ id: ENTRY, stereotype: "approvable" }],
        ...options.processExtras,
      },
    },
  ]);
  expect(created.outcome).toBe("saved");
  return backend;
}

describe("subjectsOf and the model", () => {
  it("keeps a stereotype entry, so a stereotype plus one entity is not a single subject", () => {
    const doc = {
      subjects: [
        { id: "a", stereotype: "approvable", boundAttribute: "b", owner: "o" },
        { id: "c", entity: "E" },
      ],
    };
    expect(subjectsOf(doc)).toEqual([
      { id: "a", stereotype: "approvable", boundAttribute: "b", owner: "o" },
      { id: "c", entity: "E" },
    ]);
    expect(soleSubject(doc)).toBeUndefined();
    expect(soleSubject({ subjects: [{ id: "a", stereotype: "approvable" }] })?.stereotype).toBe("approvable");
  });

  it("a stereotype applies to entities when its appliesTo is unset, empty or lists entity", () => {
    expect(stereotypeAppliesToEntities({})).toBe(true);
    expect(stereotypeAppliesToEntities({ appliesTo: ["entity", "table"] })).toBe(true);
    expect(stereotypeAppliesToEntities({ appliesTo: ["table"] })).toBe(false);
  });

  it("a stereotype's fields are its own attributes and no ends", () => {
    const fields = stereotypeFields(
      {
        attributes: [
          { id: "a", name: "approvalStatus", type: "string" },
          { id: "b", name: "notes", type: "string", collection: true },
        ],
      },
      { doc: () => undefined, kindOf: () => undefined },
    );
    expect(fields.map((f) => [f.name, f.kind, f.many])).toEqual([
      ["approvalStatus", "attribute", false],
      ["notes", "attribute", true],
    ]);
  });
});

describe("coverage and the index row", () => {
  it("lists governed carriers: package default, derived entity covered, excluded entity left out", () => {
    const backend = model();
    const r = row(backend, PROCESS);
    expect(r.subjectStereotypes).toEqual([STEREO]);
    // Derived excludes the default, and is covered through its base (Order), but it is not a carrier itself.
    expect(r.covers).toEqual([ORDER, QUOTE]);
    expect(r.subject).toBeUndefined();
    expect(r.subjects).toBeUndefined();
  });

  it("an entry naming the entity wins and the carrier leaves covers; subjects lists the entity entries", () => {
    const backend = model();
    const doc = json(backend, PROCESS);
    doc.subjects = [
      { id: ENTRY, stereotype: "approvable" },
      { id: "01JQST00000000000000000022", entity: ORDER },
    ];
    expect(run(backend, [{ op: "update", id: PROCESS, expectedHash: backend.model.get(PROCESS)!.hash, element: doc }]).outcome).toBe("saved");
    const r = row(backend, PROCESS);
    expect(r.covers).toEqual([QUOTE]);
    expect(r.subject).toBeUndefined();
    expect(r.subjects).toEqual([ORDER]);
    expect(rules(backend, "MQ9201")).toEqual([]);
  });

  it("leaves both fields out for an entity-only process", () => {
    const backend = model();
    const doc = json(backend, PROCESS);
    doc.subjects = [{ id: ENTRY, entity: ORDER }];
    run(backend, [{ op: "update", id: PROCESS, expectedHash: backend.model.get(PROCESS)!.hash, element: doc }]);
    const r = row(backend, PROCESS);
    expect(r.subject).toBe(ORDER);
    expect(r.covers).toBeUndefined();
    expect(r.subjectStereotypes).toBeUndefined();
  });

  it("an entity names a lifecycle that covers it only through the stereotype without MQ9201", () => {
    const backend = model();
    const quote = json(backend, QUOTE);
    quote.lifecycle = PROCESS;
    expect(run(backend, [{ op: "update", id: QUOTE, expectedHash: backend.model.get(QUOTE)!.hash, element: quote }]).outcome).toBe("saved");
    expect(rules(backend, "MQ9201")).toEqual([]);
    const other = json(backend, OTHER);
    other.lifecycle = PROCESS;
    const refused = run(backend, [{ op: "update", id: OTHER, expectedHash: backend.model.get(OTHER)!.hash, element: other }]);
    expect(refused.outcome).toBe("invalid");
    expect(refused.items[0].diagnostics.filter((d) => d.rule === "MQ9201").map((d) => d.elementId)).toEqual([OTHER]);
  });
});

describe("the rules", () => {
  const withEntry = (backend: MockBackend, entries: Json[]) => {
    const doc = json(backend, PROCESS);
    doc.subjects = entries;
    return run(backend, [{ op: "update", id: PROCESS, expectedHash: backend.model.get(PROCESS)!.hash, element: doc }]);
  };

  it("MQ9211: a stereotype that does not apply to entities", () => {
    const backend = model();
    const answer = withEntry(backend, [{ id: ENTRY, stereotype: "columnar" }]);
    expect(answer.items[0].diagnostics.map((d) => d.rule)).toContain("MQ9211");
  });

  it("MQ9201: a stereotype listed twice, at the second entry", () => {
    const backend = model();
    const answer = withEntry(backend, [
      { id: ENTRY, stereotype: "approvable" },
      { id: "01JQST00000000000000000022", stereotype: "approvable" },
    ]);
    const found = answer.items[0].diagnostics.filter((d) => d.rule === "MQ9201");
    expect(found.map((d) => d.jsonPointer)).toEqual(["/subjects/1/stereotype"]);
  });

  it("MQ9202: a bound attribute must be one of the stereotype's own attributes", () => {
    const backend = model();
    expect(withEntry(backend, [{ id: ENTRY, stereotype: "approvable", boundAttribute: STATUS }]).outcome).toBe("saved");
    expect(rules(backend, "MQ9202")).toEqual([]);
    const refused = withEntry(backend, [{ id: ENTRY, stereotype: "approvable", boundAttribute: "01JQST00000000000000000099" }]);
    expect(refused.items[0].diagnostics.map((d) => d.rule)).toContain("MQ9202");
  });

  it("MQ9606: a stereotype attribute is a valid owner on its own entry and on an entity entry", () => {
    const backend = model();
    expect(withEntry(backend, [{ id: ENTRY, stereotype: "approvable", owner: OWNER }]).outcome).toBe("saved");
    expect(rules(backend, "MQ9606")).toEqual([]);
    const refused = withEntry(backend, [{ id: ENTRY, stereotype: "approvable", owner: "01JQST00000000000000000099" }]);
    expect(refused.items[0].diagnostics.map((d) => d.rule)).toContain("MQ9606");
    const order = json(backend, ORDER);
    order.stereotypes = ["approvable"];
    run(backend, [{ op: "update", id: ORDER, expectedHash: backend.model.get(ORDER)!.hash, element: order }]);
    expect(withEntry(backend, [{ id: ENTRY, entity: ORDER, owner: OWNER }]).outcome).toBe("saved");
  });

  it("MQ2003: an unknown stereotype key in an entry", () => {
    const backend = model();
    const answer = withEntry(backend, [{ id: ENTRY, stereotype: "nope" }]);
    expect(answer.items[0].diagnostics.map((d) => d.rule)).toContain("MQ2003");
  });
});

describe("deletes and references", () => {
  it("deleting a stereotype removes its entries under either choice, and the emptied lifecycle gets MQ9201", () => {
    const backend = model();
    expect(backend.model.references(STEREO)!.map((r) => r.jsonPointer)).toContain("/subjects/0/stereotype");
    const answer = backend.model.delete(STEREO, backend.model.get(STEREO)!.hash, "refuse");
    expect(answer.outcome).toBe("invalid");
    expect(answer.diagnostics.map((d) => d.rule)).toContain("MQ9201");
    expect(backend.model.get(STEREO)).not.toBeNull();
    // With a second entry the entry goes, and the lifecycle stays valid.
    const doc = json(backend, PROCESS);
    doc.subjects = [
      { id: ENTRY, stereotype: "approvable" },
      { id: "01JQST00000000000000000022", entity: ORDER },
    ];
    run(backend, [{ op: "update", id: PROCESS, expectedHash: backend.model.get(PROCESS)!.hash, element: doc }]);
    expect(backend.model.delete(STEREO, backend.model.get(STEREO)!.hash, "refuse").outcome).toBe("saved");
    expect(subjectsOf(json(backend, PROCESS)).map((e) => e.entity)).toEqual([ORDER]);
  });
});

describe("sync-enum and sync-codes by stereotype", () => {
  const enumModel = () => {
    const backend = model();
    run(backend, [
      { op: "create", element: { kind: "enum", id: ENUM, name: "ApprovalState", members: [{ id: ENUM_ATTR, name: "Old" }] } },
      {
        op: "update",
        id: STEREO,
        expectedHash: backend.model.get(STEREO)!.hash,
        element: {
          ...json(backend, STEREO),
          attributes: [
            { id: STEREO_ENUM, name: "state", type: { ref: ENUM } },
            { id: OWNER, name: "requestedBy", type: "string" },
          ],
        },
      },
    ]);
    // The drift (MQ9203) is what sync-enum repairs, so the process is written as a disk edit.
    backend.model.externalEdit(PROCESS, (j) => void (j.subjects = [{ id: ENTRY, stereotype: "approvable", boundAttribute: STEREO_ENUM }]));
    return backend;
  };

  it("sync-enum makes the bound enum's members the states", () => {
    const backend = enumModel();
    expect(rules(backend, "MQ9203")).toHaveLength(1);
    expect(run(backend, [{ op: "sync-enum", id: PROCESS, stereotype: "approvable" }]).outcome).toBe("saved");
    expect((json(backend, ENUM).members as Json[]).map((m) => m.name)).toEqual(["Draft"]);
    expect(rules(backend, "MQ9203")).toEqual([]);
  });

  it("sync-enum refuses a stereotype the process does not list", () => {
    const backend = enumModel();
    expect(run(backend, [{ op: "sync-enum", id: PROCESS, stereotype: "columnar" }]).outcome).not.toBe("saved");
  });

  it("sync-codes binds the stereotype's own attribute, and refuses an entity covered only through the stereotype", () => {
    const backend = model();
    const saved = run(backend, [
      { op: "sync-codes", id: PROCESS, stereotype: "approvable", attribute: STATUS, stateCodes: { "01JQST00000000000000000040": "draft" } },
    ]);
    expect(saved.outcome).toBe("saved");
    expect(subjectsOf(json(backend, PROCESS))[0]).toMatchObject({ stereotype: "approvable", boundAttribute: STATUS });
    const refused = run(backend, [{ op: "sync-codes", id: PROCESS, entity: QUOTE, attribute: STATUS }]);
    expect(refused.outcome).not.toBe("saved");
    expect(DERIVED).toBeTruthy();
  });

  it("set-lifecycle adds no entry for an entity a stereotype entry covers", () => {
    const backend = model();
    expect(run(backend, [{ op: "set-lifecycle", id: QUOTE, target: PROCESS }]).outcome).toBe("saved");
    expect(subjectsOf(json(backend, PROCESS))).toHaveLength(1);
    expect(json(backend, QUOTE).lifecycle).toBe(PROCESS);
  });

  describe("in the explorer", () => {
    const secondaryOf = (backend: MockBackend, id: string) => nodeOf(buildForest({ rows: backend.model.index() }), id)!.secondary;
    /** The rows in an entity's Lifecycles folder: [process id, text]. */
    const lifecycles = (backend: MockBackend, entity: string) => {
      const f = buildForest({ rows: backend.model.index() });
      const folder = childKeys(f, entity).find((k) => k.endsWith("/lifecycles"));
      return folder ? childKeys(f, folder).map((k) => [nodeOf(f, k)!.id, nodeOf(f, k)!.secondary]) : [];
    };

    it("a lifecycle row names 'every Approvable', and an entity beside it", () => {
      const backend = model();
      expect(secondaryOf(backend, PROCESS)).toMatch(/^lifecycle · every Approvable · /);
      const doc = json(backend, PROCESS);
      doc.subjects = [
        { id: ENTRY, stereotype: "approvable" },
        { id: "01JQST00000000000000000022", entity: ORDER },
      ];
      run(backend, [{ op: "update", id: PROCESS, expectedHash: backend.model.get(PROCESS)!.hash, element: doc }]);
      expect(secondaryOf(backend, PROCESS)).toMatch(/^lifecycle · Order, every Approvable · /);
    });

    it("an entity's Lifecycles folder lists the process through the stereotype, and for a derived entity through its base", () => {
      const backend = model();
      expect(lifecycles(backend, QUOTE)).toEqual([[PROCESS, "through Approvable"]]);
      expect(lifecycles(backend, DERIVED)).toEqual([[PROCESS, "through Approvable, via Order"]]);
      expect(lifecycles(backend, OTHER)).toEqual([]);
    });

    it("an entity entry puts the process in that entity's folder by the entity itself", () => {
      const backend = model();
      const doc = json(backend, PROCESS);
      doc.subjects = [
        { id: ENTRY, stereotype: "approvable" },
        { id: "01JQST00000000000000000022", entity: ORDER },
      ];
      run(backend, [{ op: "update", id: PROCESS, expectedHash: backend.model.get(PROCESS)!.hash, element: doc }]);
      expect(lifecycles(backend, ORDER).map((r) => r[0])).toEqual([PROCESS]);
      expect(lifecycles(backend, QUOTE).map((r) => r[0])).toEqual([PROCESS]);
    });
  });
});
