// Lifecycle bindings and state codes in the editor (lifecycle-design.md decisions 11 to 19, section 10): what the picker lists,
// the mock's rules at the engine's pointers and element ids, the sync-codes operation and endpoint, sync-enum's `entity`, and
// the references a code under a reference binding makes.
import { createElement, type ReactNode } from "react";
import { QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { bindableAttributes, classifyAttribute, codeKindOf } from "@/model/binding";
import { setStateCode, stateRows, type ProcessDoc } from "@/model/process";
import type { Diagnostic, ModelJson } from "@/api/types";
import { useBinding } from "@/editors/process/ProcessControls";
import { applySyncCodes, syncCodes } from "@/editors/process/api";
import { deriveQuickFix, fixDocuments, hasQuickFix, initialCode } from "@/problems/quickFix";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const U = (n: number) => `01JQTSB${String(n).padStart(19, "0")}`;
const [TYPE_REF, SEED, ENTITY, OTHER, PROCESS, SUBJ, ENUM] = [U(1), U(2), U(3), U(4), U(5), U(6), U(7)];
const A = { key: U(10), ref: U(11), plain: U(12), int: U(13), enumA: U(14), money: U(15), many: U(16), ref2: U(17) };
const ROW = { draft: U(20), issued: U(21), paid: U(22) };
const S = { draft: U(30), issued: U(31), paid: U(32), nested: U(33) };

/** Puts a document in whole, past the save check: the rules under test report errors a save would refuse. */
function force(backend: MockBackend, doc: Json, file = "invoice-flow") {
  const model = backend.model as unknown as { put(path: string, json: Json): unknown; derivedState: unknown; version: number };
  model.put(`.maquettiste/model/processes/${file}.json`, doc);
  model.derivedState = null;
  model.version++;
}

function seedModel(backend: MockBackend, process: Json) {
  const { model } = backend;
  const make = (doc: Json) => {
    const result = model.create(doc as never);
    if (result.outcome !== "saved") throw new Error(`${String(doc.kind)}: ${JSON.stringify(result.diagnostics)}`);
  };
  make({ kind: "reference-type", id: TYPE_REF, name: "InvoiceStatus", code: { id: U(8) }, label: { id: U(9) } });
  make({
    kind: "seed",
    id: SEED,
    name: "InvoiceStatuses",
    target: TYPE_REF,
    columns: ["code", "label"],
    rows: [
      { id: ROW.draft, values: ["DRF", "Draft"] },
      { id: ROW.issued, values: ["ISS", "Issued"] },
      { id: ROW.paid, values: ["PD", "Paid"] },
    ],
  });
  make({
    kind: "enum",
    id: ENUM,
    name: "Phase",
    members: [
      { id: U(40), name: "Draft", code: "D", value: 1 },
      { id: U(41), name: "Issued" },
    ],
  });
  make({
    kind: "entity",
    id: ENTITY,
    name: "Invoice",
    key: { attributes: [A.key] },
    attributes: [
      { id: A.key, name: "id", type: "uuid" },
      { id: A.ref, name: "status", type: { ref: TYPE_REF } },
      { id: A.plain, name: "state", type: "string", length: 3 },
      { id: A.int, name: "stage", type: "int16" },
      { id: A.enumA, name: "phase", type: { ref: ENUM } },
      { id: A.many, name: "tags", type: "string", collection: true },
    ],
  });
  make({
    kind: "entity",
    id: OTHER,
    name: "Receipt",
    key: { attributes: [U(50)] },
    attributes: [
      { id: U(50), name: "id", type: "uuid" },
      { id: A.ref2, name: "status", type: { ref: TYPE_REF } },
    ],
  });
  force(backend, process);
}

// Each root state moves on to the next, so every state is reachable (MQ9210 skips the ones that are not).
const lifecycle = (states: Json[], bound: string | undefined, extra: Json = {}): Json => ({
  kind: "process",
  id: PROCESS,
  name: "InvoiceFlow",
  use: "lifecycle",
  subjects: [{ id: SUBJ, entity: ENTITY, ...(bound ? { boundAttribute: bound } : {}) }],
  states,
  transitions: states.slice(1).map((s, i) => ({ id: U(80 + i), source: states[i].id, trigger: "always", targets: [s.id] })),
  ...extra,
});

const findings = (backend: MockBackend) =>
  backend.model
    .validate()
    .diagnostics.filter((d) => /^MQ92(0[2-8]|10)$/.test(d.rule))
    .map((d) => `${d.rule} ${d.jsonPointer} ${d.elementId}`)
    .sort();

describe("what a lifecycle may bind (model/binding.ts)", () => {
  const docs = new Map<string, Json>([
    [TYPE_REF, { kind: "reference-type", id: TYPE_REF, name: "T" }],
    [ENUM, { kind: "enum", id: ENUM, name: "E" }],
    ["FLAGS", { kind: "enum", id: "FLAGS", name: "F", flags: true }],
    ["SCALAR", { kind: "scalar-type", id: "SCALAR", name: "Code3", base: "string" }],
    ["MONEY", { kind: "scalar-type", id: "MONEY", name: "Money", base: "decimal" }],
    ["VO", { kind: "value-object", id: "VO", name: "Address" }],
  ]);
  const doc = (id: string) => docs.get(id);
  const kind = (attribute: Json) => classifyAttribute(attribute, doc).kind;

  it("lists enums, reference types and plain statuses, and nothing else", () => {
    expect(kind({ type: { ref: ENUM } })).toBe("enum");
    expect(kind({ type: { ref: TYPE_REF } })).toBe("reference");
    expect(kind({ type: "string" })).toBe("plain");
    expect(kind({ type: "text" })).toBe("plain");
    expect(kind({ type: "int64" })).toBe("plain");
    expect(kind({ type: { ref: "SCALAR" } })).toBe("plain");
    expect(kind({ type: { ref: "FLAGS" } })).toBe("unsupported");
    expect(kind({ type: { ref: "MONEY" } })).toBe("unsupported");
    expect(kind({ type: { ref: "VO" } })).toBe("unsupported");
    expect(kind({ type: "decimal" })).toBe("unsupported");
    expect(kind({ type: "bool" })).toBe("unsupported");
    expect(kind({ type: "string", collection: true })).toBe("unsupported");
    expect(kind({ type: { ref: "MISSING" } })).toBe("unknown");
  });

  it("reads an entity's own, inherited and stereotype attributes", () => {
    const base = { kind: "entity", id: "B", name: "B", attributes: [{ id: "A1", name: "a" }], stereotypes: ["audit"] };
    const entity = { kind: "entity", id: "E", name: "E", base: "B", attributes: [{ id: "A2", name: "b" }] };
    const all = new Map<string, Json>([["B", base]]);
    const stereotypes = new Map<string, Json>([["audit", { attributes: [{ id: "A3", name: "c" }] }]]);
    expect(
      bindableAttributes(
        entity,
        (id) => all.get(id),
        (key) => stereotypes.get(key),
      ).map((a) => a.id),
    ).toEqual(["A2", "A1", "A3"]);
  });
});

describe("the States tab's Code cell", () => {
  const process = (): ProcessDoc =>
    ({
      kind: "process",
      id: "P",
      name: "P",
      states: [
        { id: "S1", name: "A", code: 1 },
        { id: "S2", name: "B" },
      ],
    }) as unknown as ProcessDoc;

  it("writes a number for integer text, a string otherwise, and removes the key when cleared", () => {
    const p = process();
    setStateCode(p, "S2", "2");
    expect(p.states[1].code).toBe(2);
    setStateCode(p, "S2", "TWO");
    expect(p.states[1].code).toBe("TWO");
    setStateCode(p, "S1", "");
    expect("code" in p.states[0]).toBe(false);
    setStateCode(p, "S1", "007");
    expect(p.states[0].code).toBe("007");
    expect(stateRows(p).map((r) => r.code)).toEqual(["007", "TWO"]);
  });

  it("keeps integer text a string while another state's code is a string", () => {
    const p = process();
    setStateCode(p, "S1", "ONE");
    setStateCode(p, "S2", "2");
    expect(p.states[1].code).toBe("2");
  });

  it("follows the binding's type: strings for a string status, numbers for an integer one", () => {
    const p = process();
    setStateCode(p, "S2", "10", "string");
    expect(p.states[1].code).toBe("10");
    setStateCode(p, "S1", "7", "integer");
    expect(p.states[0].code).toBe(7);
    setStateCode(p, "S1", "SEVEN", "integer");
    expect(p.states[0].code).toBe("SEVEN");
  });

  it("under an enum binding takes the codes' kind, else a number only where a member's value says so", () => {
    const fresh = (): ProcessDoc =>
      ({
        kind: "process",
        id: "P",
        name: "P",
        states: [
          { id: "S1", name: "A" },
          { id: "S2", name: "B" },
        ],
      }) as unknown as ProcessDoc;
    const first = fresh();
    setStateCode(first, "S1", "1", { enumValues: [1] });
    setStateCode(first, "S2", "2", { enumValues: [1] });
    expect(first.states.map((s) => s.code)).toEqual([1, 2]);
    const text = fresh();
    setStateCode(text, "S1", "1", { enumValues: [] });
    expect(text.states[0].code).toBe("1");
    const named = fresh();
    setStateCode(named, "S1", "A", { enumValues: [1] });
    setStateCode(named, "S2", "2", { enumValues: [1, 2] });
    expect(named.states[1].code).toBe("2");
  });

  it("codeKindOf reads the binding's type", () => {
    const docs: Record<string, Json> = {
      sc: { kind: "scalar-type", base: "int32" },
      rt: { kind: "reference-type", code: { type: "int16" } },
      rs: { kind: "reference-type", code: { type: "string" } },
      en: { kind: "enum", members: [{ name: "A", value: 3 }, { name: "B" }] },
    };
    const doc = (id: string) => docs[id];
    expect(codeKindOf({ kind: "plain" }, { type: "text" }, doc)).toBe("string");
    expect(codeKindOf({ kind: "plain" }, { type: "int64" }, doc)).toBe("integer");
    expect(codeKindOf({ kind: "plain", typeId: "sc" }, { type: { ref: "sc" } }, doc)).toBe("integer");
    expect(codeKindOf({ kind: "reference", typeId: "rt" }, {}, doc)).toBe("integer");
    expect(codeKindOf({ kind: "reference", typeId: "rs" }, {}, doc)).toBe("string");
    expect(codeKindOf({ kind: "enum", typeId: "en" }, {}, doc)).toEqual({ enumValues: [3] });
  });
});

describe("mock lifecycle rules", () => {
  it("MQ9210 once per binding, at the binding, under a plain binding; none under an enum binding", () => {
    const backend = new MockBackend();
    seedModel(
      backend,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "DRF" },
          { id: S.issued, name: "Issued" },
        ],
        A.plain,
      ),
    );
    expect(findings(backend)).toEqual([`MQ9205 /subjects/0/boundAttribute ${SUBJ}`, `MQ9210 /subjects/0/boundAttribute ${SUBJ}`]);
    const enumBackend = new MockBackend();
    seedModel(
      enumBackend,
      lifecycle(
        [
          { id: S.draft, name: "Draft" },
          { id: S.issued, name: "Issued" },
        ],
        A.enumA,
      ),
    );
    expect(findings(enumBackend)).toEqual([`MQ9205 /subjects/0/boundAttribute ${SUBJ}`]);
  });

  it("MQ9210 is a warning that names the binding and the uncoded states, five at most", () => {
    const backend = new MockBackend();
    seedModel(
      backend,
      lifecycle([{ id: S.draft, name: "Draft", code: "DRF" }, ...["A", "B", "C", "D", "E", "F", "G"].map((n, i) => ({ id: U(100 + i), name: n }))], A.plain),
    );
    const found = backend.model.validate().diagnostics.filter((x) => x.rule === "MQ9210");
    expect(found).toHaveLength(1);
    expect(found[0].severity).toBe("warning");
    expect(found[0].message).toBe(
      "7 states of process 'InvoiceFlow' have no code, which binding 'Invoice.state' needs: A, B, C, D, E, and 2 more; give each a code, or run Sync codes.",
    );
    const one = new MockBackend();
    seedModel(
      one,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "DRF" },
          { id: S.issued, name: "Issued" },
        ],
        A.ref,
      ),
    );
    expect(one.model.validate().diagnostics.find((x) => x.rule === "MQ9210")!.message).toBe(
      "1 state of process 'InvoiceFlow' has no code, which binding 'Invoice.status' needs: Issued; give each a code, or run Sync codes.",
    );
  });

  it("MQ9210 sits at /boundAttribute under the process id in the one-subject form", () => {
    const backend = new MockBackend();
    const doc = lifecycle(
      [
        { id: S.draft, name: "Draft" },
        { id: S.issued, name: "Issued" },
      ],
      undefined,
    );
    delete doc.subjects;
    seedModel(backend, { ...doc, subject: ENTITY, boundAttribute: A.plain });
    expect(findings(backend)).toContain(`MQ9210 /boundAttribute ${PROCESS}`);
  });

  it("MQ9202 when the bound attribute's type is not in the model", () => {
    const backend = new MockBackend();
    seedModel(backend, lifecycle([{ id: S.draft, name: "Draft" }], A.ref));
    const entity = backend.model.entries.get(ENTITY)!.json as Json;
    const attrs = (entity.attributes as Json[]).map((a) => (a.id === A.ref ? { ...a, type: { ref: U(999) } } : a));
    force(backend, { ...entity, attributes: attrs }, "invoice-entity");
    expect(findings(backend).some((f) => f.startsWith("MQ9202"))).toBe(true);
  });

  it("MQ9210 lists an uncoded compound once, not its children", () => {
    const backend = new MockBackend();
    seedModel(
      backend,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "D" },
          {
            id: S.issued,
            name: "Issued",
            type: "compound",
            initial: S.paid,
            states: [
              { id: S.paid, name: "A" },
              { id: S.nested, name: "B" },
            ],
          },
        ],
        A.plain,
        { transitions: [{ id: U(60), source: S.draft, trigger: "always", targets: [S.issued] }] },
      ),
    );
    const rules = findings(backend);
    expect(rules.filter((r) => r.startsWith("MQ9210"))).toEqual([`MQ9210 /subjects/0/boundAttribute ${SUBJ}`]);
    const message = backend.model.validate().diagnostics.find((x) => x.rule === "MQ9210")!.message;
    expect(message).toContain("needs: Issued;");
  });

  it("MQ9206 clauses sit at the state's code: a shared code, mixed kinds, a coded history, an ancestor and a coded child", () => {
    const backend = new MockBackend();
    seedModel(
      backend,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "X" },
          { id: S.issued, name: "Issued", code: "X" },
          { id: S.paid, name: "Paid", code: 3 },
          { id: S.nested, name: "Back", type: "history", code: "H" },
        ],
        undefined,
      ),
    );
    expect(findings(backend)).toEqual([`MQ9206 /states/1/code ${S.issued}`, `MQ9206 /states/2/code ${S.paid}`, `MQ9206 /states/3/code ${S.nested}`].sort());
    const nested = new MockBackend();
    seedModel(nested, lifecycle([{ id: S.draft, name: "Draft", code: "A", states: [{ id: S.issued, name: "Inner", code: "B" }] }], A.plain));
    expect(findings(nested)).toContain(`MQ9206 /states/0/states/0/code ${S.issued}`);
  });

  it("MQ9207 at the code: a plain status's type and length, a reference type's live codes, an enum member's identifiers", () => {
    const plain = new MockBackend();
    seedModel(
      plain,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "TOOLONG" },
          { id: S.issued, name: "Issued", code: "OK" },
        ],
        A.plain,
      ),
    );
    expect(findings(plain).filter((r) => r.startsWith("MQ9207"))).toEqual([`MQ9207 /states/0/code ${S.draft}`]);
    const integer = new MockBackend();
    seedModel(
      integer,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: 7 },
          { id: S.issued, name: "Issued", code: "x" },
        ],
        A.int,
      ),
    );
    expect(findings(integer).filter((r) => r.startsWith("MQ9207"))).toEqual([`MQ9207 /states/1/code ${S.issued}`].sort());
    const reference = new MockBackend();
    seedModel(
      reference,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "DRF" },
          { id: S.issued, name: "Issued", code: "NOPE" },
          { id: S.paid, name: "Paid", code: "PD" },
        ],
        A.ref,
      ),
    );
    expect(findings(reference).filter((r) => r.startsWith("MQ9207"))).toEqual([`MQ9207 /states/1/code ${S.issued}`]);
    const member = new MockBackend();
    seedModel(
      member,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "D" },
          { id: S.issued, name: "Issued", code: "zzz" },
        ],
        A.enumA,
      ),
    );
    expect(findings(member).filter((r) => r.startsWith("MQ9207"))).toEqual([`MQ9207 /states/1/code ${S.issued}`]);
    const named = new MockBackend();
    seedModel(
      named,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: 1 },
          { id: S.issued, name: "Issued", code: "Issued" },
        ],
        A.enumA,
      ),
    );
    expect(findings(named).filter((r) => r.startsWith("MQ9207"))).toEqual([]);
  });

  it("MQ9208 once per binding for the rows no state covers; MQ9205 by code for a binding that stores codes", () => {
    const backend = new MockBackend();
    seedModel(
      backend,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "DRF" },
          { id: S.issued, name: "Issued", code: "ISS" },
        ],
        A.ref,
      ),
    );
    expect(findings(backend)).toEqual([`MQ9205 /subjects/0/boundAttribute ${SUBJ}`, `MQ9208 /subjects/0/boundAttribute ${SUBJ}`]);
  });

  it("MQ9202 for a binding that cannot hold a state, one on an orchestration and one attribute bound by two lifecycles", () => {
    const flags = new MockBackend();
    seedModel(flags, lifecycle([{ id: S.draft, name: "Draft" }], A.many));
    expect(findings(flags)).toEqual([`MQ9202 /subjects/0/boundAttribute ${SUBJ}`]);
    const orchestration = new MockBackend();
    seedModel(orchestration, lifecycle([{ id: S.draft, name: "Draft" }], A.plain, { use: "orchestration" }));
    expect(findings(orchestration)).toEqual([`MQ9202 /subjects/0/boundAttribute ${SUBJ}`]);
    const two = new MockBackend();
    seedModel(two, lifecycle([{ id: S.draft, name: "Draft", code: "A" }], A.plain));
    force(
      two,
      {
        kind: "process",
        id: U(70),
        name: "Second",
        use: "lifecycle",
        subjects: [{ id: U(71), entity: ENTITY, boundAttribute: A.plain }],
        states: [{ id: U(72), name: "Only", code: "B" }],
      },
      "second",
    );
    expect(findings(two).filter((r) => r.startsWith("MQ9202"))).toEqual(
      [`MQ9202 /subjects/0/boundAttribute ${SUBJ}`, `MQ9202 /subjects/0/boundAttribute ${U(71)}`].sort(),
    );
  });

  it("in the one-subject form the binding findings are at /boundAttribute under the process id", () => {
    const backend = new MockBackend();
    seedModel(backend, {
      kind: "process",
      id: PROCESS,
      name: "InvoiceFlow",
      use: "lifecycle",
      subject: ENTITY,
      boundAttribute: A.many,
      states: [{ id: S.draft, name: "Draft" }],
    });
    expect(findings(backend)).toContain(`MQ9202 /boundAttribute ${PROCESS}`);
  });
});

describe("a code under a reference binding uses the row", () => {
  it("is a reference from the state, field code, so the row's references list it", () => {
    const backend = new MockBackend();
    seedModel(
      backend,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "DRF" },
          { id: S.issued, name: "Issued", code: 5 },
        ],
        A.ref,
      ),
    );
    const uses = backend.model.references(ROW.draft)!.filter((r) => r.field === "code");
    expect(uses).toEqual([{ fromElementId: PROCESS, fromId: S.draft, jsonPointer: "/states/0/code", field: "code", toId: ROW.draft }]);
    expect(backend.model.references(ROW.issued)!.filter((r) => r.field === "code")).toEqual([]);
  });

  it("lists one use when two bindings of a subject fit the same type", () => {
    const backend = new MockBackend();
    seedModel(
      backend,
      lifecycle([{ id: S.draft, name: "Draft", code: "DRF" }], A.ref, {
        subjects: [
          { id: SUBJ, entity: ENTITY, boundAttribute: A.ref },
          { id: U(7000), entity: OTHER, boundAttribute: A.ref2 },
        ],
      }),
    );
    expect(backend.model.references(ROW.draft)!.filter((r) => r.field === "code")).toHaveLength(1);
  });

  it("is no reference under a plain binding", () => {
    const backend = new MockBackend();
    seedModel(backend, lifecycle([{ id: S.draft, name: "Draft", code: "DRF" }], A.plain));
    expect(backend.model.references(ROW.draft)!.filter((r) => r.field === "code")).toEqual([]);
  });
});

describe("sync-codes and sync-enum with an entity (the mock endpoints)", () => {
  const mock = useMockApi();
  const post = async (path: string, body: unknown) => {
    const response = await fetch(mock.url(path), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    return { status: response.status, body: (await response.json()) as Json };
  };

  it("proposes codes by name and by label, and writes them with the binding on apply", async () => {
    seedModel(
      mock.backend,
      lifecycle(
        [
          { id: S.draft, name: "Draft" },
          { id: S.issued, name: "Issued" },
          { id: S.paid, name: "Settled", displayName: "Paid" },
        ],
        undefined,
      ),
    );
    const dry = await post(`/api/processes/${PROCESS}/sync-codes`, { entity: ENTITY, attribute: A.ref, dryRun: true });
    expect(dry.status).toBe(200);
    expect(dry.body).toMatchObject({ process: PROCESS, entity: ENTITY, attribute: A.ref, binds: true, applied: false, unmatched: [] });
    expect(dry.body.codes).toEqual([
      { state: S.draft, path: "Draft", code: "DRF", source: "label" },
      { state: S.issued, path: "Issued", code: "ISS", source: "label" },
      { state: S.paid, path: "Settled", code: "PD", source: "label" },
    ]);
    expect(((mock.backend.model.get(PROCESS)!.json as Json).states as Json[])[0].code).toBeUndefined();
    const applied = await post(`/api/processes/${PROCESS}/sync-codes`, { entity: ENTITY, attribute: A.ref });
    expect(applied.body).toMatchObject({ applied: true });
    const json = mock.backend.model.get(PROCESS)!.json as Json;
    expect((json.states as Json[]).map((s) => s.code)).toEqual(["DRF", "ISS", "PD"]);
    expect((json.subjects as Json[])[0]).toMatchObject({ entity: ENTITY, boundAttribute: A.ref });
    expect(findings(mock.backend).filter((r) => /MQ92(06|07|10)/.test(r))).toEqual([]);
  });

  it("leaves a state with no match unmatched, takes a given code, and removes nothing it keeps", async () => {
    seedModel(
      mock.backend,
      lifecycle(
        [
          { id: S.draft, name: "Draft", code: "DRF" },
          { id: S.issued, name: "Shipped" },
        ],
        A.ref,
      ),
    );
    const dry = await post(`/api/processes/${PROCESS}/sync-codes`, { entity: ENTITY, attribute: A.ref, dryRun: true });
    expect(dry.body).toMatchObject({ binds: false, unmatched: [{ state: S.issued, path: "Shipped", candidates: [] }] });
    expect((dry.body.codes as Json[])[0]).toMatchObject({ state: S.draft, source: "kept" });
    const given = await post(`/api/processes/${PROCESS}/sync-codes`, { entity: ENTITY, attribute: A.ref, stateCodes: { [S.issued]: "PD" } });
    expect(given.body).toMatchObject({ applied: true, unmatched: [] });
    expect(((mock.backend.model.get(PROCESS)!.json as Json).states as Json[]).map((s) => s.code)).toEqual(["DRF", "PD"]);
  });

  it("refuses a process that has a bound attribute and no subject, writing neither form", async () => {
    seedModel(mock.backend, {
      kind: "process",
      id: PROCESS,
      name: "InvoiceFlow",
      use: "lifecycle",
      boundAttribute: A.enumA,
      states: [{ id: S.draft, name: "Draft" }],
    });
    const refused = await post(`/api/processes/${PROCESS}/sync-codes`, { entity: ENTITY, attribute: A.enumA });
    expect(refused.status).toBe(422);
    expect((refused.body.diagnostics as Json[])[0]).toMatchObject({ rule: "MQ9019" });
    expect((mock.backend.model.get(PROCESS)!.json as Json).subjects).toBeUndefined();
  });

  it("takes integer codes only when every resting state's code is an integer", async () => {
    seedModel(mock.backend, {
      kind: "process",
      id: PROCESS,
      name: "InvoiceFlow",
      use: "lifecycle",
      subject: ENTITY,
      states: [
        { id: S.draft, name: "Draft", code: 1 },
        { id: S.issued, name: "Issued", code: "ISS" },
        { id: S.paid, name: "Phase" },
      ],
    });
    const planned = await post(`/api/processes/${PROCESS}/sync-codes`, { entity: ENTITY, attribute: A.enumA, dryRun: true });
    expect(planned.status).toBe(200);
    expect(JSON.stringify(planned.body)).not.toContain('"code":1,"source":"name"');
  });

  it("matches enum members by name, writes the one-subject form's binding in place, and refuses another entity", async () => {
    seedModel(mock.backend, {
      kind: "process",
      id: PROCESS,
      name: "InvoiceFlow",
      use: "lifecycle",
      subject: ENTITY,
      states: [
        { id: S.draft, name: "Draft" },
        { id: S.issued, name: "Issued" },
      ],
    });
    const applied = await post(`/api/processes/${PROCESS}/sync-codes`, { entity: ENTITY, attribute: A.enumA });
    expect(applied.body).toMatchObject({ applied: true, binds: true });
    const json = mock.backend.model.get(PROCESS)!.json as Json;
    expect(json.boundAttribute).toBe(A.enumA);
    expect(json.subjects).toBeUndefined();
    expect((json.states as Json[]).map((s) => s.code)).toEqual(["D", "Issued"]);
    const refused = await post(`/api/processes/${PROCESS}/sync-codes`, { entity: OTHER, attribute: U(50) });
    expect(refused.status).toBe(422);
    expect((refused.body.diagnostics as Json[])[0]).toMatchObject({ rule: "MQ9019" });
    const unsupported = await post(`/api/processes/${PROCESS}/sync-codes`, { entity: ENTITY, attribute: A.many, dryRun: true });
    expect(unsupported.status).toBe(422);
  });

  it("is a batch operation: the process is written in the form the file has, and a refusal is MQ9019", async () => {
    seedModel(mock.backend, lifecycle([{ id: S.draft, name: "Draft" }], undefined));
    const refused = mock.backend.model.batch({ operations: [{ op: "sync-codes", id: PROCESS, entity: ENTITY, attribute: A.many }] });
    expect((refused.body as { outcome: string }).outcome).not.toBe("saved");
    const saved = mock.backend.model.batch({
      operations: [{ op: "sync-codes", id: PROCESS, entity: ENTITY, attribute: A.ref, stateCodes: { [S.draft]: "DRF" } }],
    });
    expect((saved.body as { outcome: string }).outcome).toBe("saved");
    const json = mock.backend.model.get(PROCESS)!.json as Json;
    expect(((json.states as Json[])[0] as Json).code).toBe("DRF");
    expect((json.subjects as Json[]).map((e) => e.boundAttribute)).toEqual([A.ref]);
  });

  it("sync-enum picks the entity's enum binding, and refuses an entity the lifecycle does not list", async () => {
    seedModel(
      mock.backend,
      lifecycle(
        [
          { id: S.draft, name: "Draft" },
          { id: S.issued, name: "Issued" },
          { id: S.paid, name: "Paid" },
        ],
        A.enumA,
      ),
    );
    const dry = await post(`/api/processes/${PROCESS}/sync-enum`, { dryRun: true, entity: ENTITY });
    expect(dry.body).toMatchObject({ enum: ENUM, added: ["Paid"], applied: false });
    expect((await post(`/api/processes/${PROCESS}/sync-enum`, { dryRun: true, entity: OTHER })).status).toBe(422);
    expect((await post(`/api/processes/${PROCESS}/sync-enum`, { entity: ENTITY })).body).toMatchObject({ applied: true });
  });
});

describe("quick fixes for bindings and codes", () => {
  const diag = (rule: string, elementId: string, jsonPointer: string): Diagnostic => ({
    rule,
    severity: "warning",
    message: "",
    elementId,
    filePath: null,
    jsonPointer,
    line: null,
    column: null,
  });
  const docs =
    (...list: Json[]) =>
    (id: string) =>
      list.find((d) => d.id === id) as unknown as ModelJson | undefined;
  const entity = (type: unknown): Json => ({ kind: "entity", id: "E", name: "E", attributes: [{ id: "A", name: "status", type }] });
  const process = (states: Json[], extra: Json = {}): Json => ({
    kind: "process",
    id: "P",
    name: "P",
    use: "lifecycle",
    subjects: [{ id: "X1", entity: "E", boundAttribute: "A" }],
    states,
    ...extra,
  });

  it("MQ9205 writes the initial configuration's code for a binding that stores codes, as the state has it", () => {
    const direct = (p: Json, type: unknown, extra: Json[] = []) =>
      deriveQuickFix(diag("MQ9205", "P", "/subjects/0/boundAttribute"), null, docs(p, entity(type), ...extra));
    const numbered = direct(process([{ id: "S1", name: "Draft", code: 7 }]), "int16");
    expect(numbered).toMatchObject({ kind: "update", id: "E", label: "Set default to 7" });
    expect(numbered?.kind === "update" && (numbered.json.attributes as Json[])[0].default).toBe(7);
    const text = direct(process([{ id: "S1", name: "Draft", code: "DRF" }]), "string");
    expect(text?.kind === "update" && (text.json.attributes as Json[])[0].default).toBe("DRF");
    // A compound start with no code of its own takes its initial child's.
    const compound = direct(process([{ id: "S1", name: "Open", type: "compound", states: [{ id: "S2", name: "Inner", code: "IN" }] }]), "string");
    expect(compound?.kind === "update" && (compound.json.attributes as Json[])[0].default).toBe("IN");
    const reference = direct(process([{ id: "S1", name: "Draft", code: "DRF" }]), { ref: "T" }, [{ kind: "reference-type", id: "T", name: "T" }]);
    expect(reference?.kind === "update" && (reference.json.attributes as Json[])[0].default).toBe("DRF");
    // An enum binding keeps writing the initial state's name; a state with no code has nothing to write otherwise.
    const member = direct(process([{ id: "S1", name: "Draft", code: "D" }]), { ref: "N" }, [{ kind: "enum", id: "N", name: "N" }]);
    expect(member?.kind === "update" && (member.json.attributes as Json[])[0].default).toBe("Draft");
    expect(direct(process([{ id: "S1", name: "Draft" }]), "string")).toBeNull();
    expect(initialCode(process([{ id: "S1", name: "A", type: "history", code: "H" }]))).toBeUndefined();
  });

  it("MQ9205 names the attribute's type as a document to read, once the entity is known", () => {
    const p = process([{ id: "S1", name: "Draft", code: "DRF" }]);
    const d = diag("MQ9205", "P", "/subjects/0/boundAttribute");
    expect(fixDocuments(d, p as unknown as ModelJson)).toEqual(["P", "E"]);
    expect(fixDocuments(d, p as unknown as ModelJson, docs(entity({ ref: "T" })))).toEqual(["P", "E", "T"]);
  });

  it("sync-enum sends the entry's entity from a subjects process and none from the one-subject form", () => {
    const listed = process([{ id: "S1", name: "Draft" }], {
      subjects: [
        { id: "X0", entity: "F" },
        { id: "X1", entity: "E", boundAttribute: "A" },
      ],
    });
    expect(deriveQuickFix(diag("MQ9203", "P", "/subjects/1/boundAttribute"), "sync-enum", docs(listed))).toEqual({
      kind: "sync-enum",
      process: "P",
      label: "Sync enum",
      entity: "E",
    });
    const one = process([{ id: "S1", name: "Draft" }], { subjects: undefined, subject: "E", boundAttribute: "A" });
    expect(deriveQuickFix(diag("MQ9203", "P", "/boundAttribute"), "sync-enum", docs(one))).toEqual({ kind: "sync-enum", process: "P", label: "Sync enum" });
  });

  it("MQ9210 syncs the codes of the binding the finding sits on, of the process the fetched document is", () => {
    const p = process([{ id: "S1", name: "Draft" }]);
    const get = (id: string) => (id === "X1" ? (p as unknown as ModelJson) : undefined);
    expect(deriveQuickFix(diag("MQ9210", "X1", "/subjects/0/boundAttribute"), "sync-codes", get)).toEqual({
      kind: "sync-codes",
      process: "P",
      entity: "E",
      attribute: "A",
      label: "Sync codes",
    });
    const unbound = process([{ id: "S1", name: "Draft" }], { subjects: [{ id: "X1", entity: "E" }] });
    expect(deriveQuickFix(diag("MQ9210", "X1", "/subjects/0/boundAttribute"), "sync-codes", () => unbound as unknown as ModelJson)).toBeNull();
    expect(deriveQuickFix(diag("MQ9206", "S1", "/states/0/code"), "sync-codes", get)).toBeNull();
    expect(hasQuickFix("MQ9206", "sync-codes")).toBe(false);
  });
});

describe("the sync-codes quick fix against the mock", () => {
  const mock = useMockApi();

  it("does a dry run, then applies as one undo step", async () => {
    seedModel(
      mock.backend,
      lifecycle(
        [
          { id: S.draft, name: "Draft" },
          { id: S.issued, name: "Issued" },
        ],
        undefined,
      ),
    );
    const { queryClient, store } = mock.services;
    const dry = await syncCodes(PROCESS, { entity: ENTITY, attribute: A.ref, dryRun: true });
    expect(dry.codes.map((c) => c.code)).toEqual(["DRF", "ISS"]);
    const result = await applySyncCodes(queryClient, store, PROCESS, { entity: ENTITY, attribute: A.ref });
    expect(result.applied).toBe(true);
    expect(store.getState().undo.at(-1)).toMatchObject({ label: "Sync codes", ids: [PROCESS] });
    expect(((mock.backend.model.get(PROCESS)!.json as Json).states as Json[]).map((s) => s.code)).toEqual(["DRF", "ISS"]);
  });
});

describe("the bound-attribute picker (useBinding)", () => {
  const mock = useMockApi();

  it("lists the single-valued enum, reference and plain attributes, not collections, and keeps enumId enum-only", async () => {
    seedModel(mock.backend, lifecycle([{ id: S.draft, name: "Draft" }], undefined));
    const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client: mock.services.queryClient }, children);
    const names = (result: { current: ReturnType<typeof useBinding> }) => result.current.attributes.map((a) => a.name).sort();
    const { result } = renderHook(() => useBinding(ENTITY, A.enumA), { wrapper });
    await waitFor(() => expect(names(result)).toEqual(["phase", "stage", "state", "status"]));
    await waitFor(() => expect(result.current.enumId).toBe(ENUM));
    await waitFor(() => expect(result.current.members).toEqual(["Draft", "Issued"]));
    const plain = renderHook(() => useBinding(ENTITY, A.plain), { wrapper });
    await waitFor(() => expect(plain.result.current.attributes.length).toBe(4));
    expect(plain.result.current.enumId).toBeNull();
    expect(plain.result.current.members).toBeNull();
  });
});
