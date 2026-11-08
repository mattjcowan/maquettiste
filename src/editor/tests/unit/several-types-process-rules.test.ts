// Relations with several types at an end, editor part two beyond the canvas (several-types-design.md decisions 13 to 18, section 11): the
// pickers that list an entity's links, the any-entity relation in every entity's list, and the mock's rights, trigger point and stepper
// rules through such an end.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { relatedOf } from "@/editors/related";
import { commonFields, fieldOptionLabel, objectFields, pathProblem, type FieldSources } from "@/model/objectFields";
import { MockBackend } from "@/mocks/backend";
import { severalEnds } from "@/mocks/model/endCover";
import { simulate } from "@/mocks/model/stepper";

type Json = Record<string, unknown>;

// Part, Assembly and Tool each have a `weight`; Part and Assembly also share `code`. Comment is attached to a Part, an Assembly or an approvable.
const docs: Record<string, Json> = {
  C: { kind: "entity", id: "C", name: "Comment", attributes: [{ id: "text", name: "text" }] },
  P: {
    kind: "entity",
    id: "P",
    name: "Part",
    attributes: [
      { id: "code", name: "code" },
      { id: "weight", name: "weight" },
    ],
  },
  A: {
    kind: "entity",
    id: "A",
    name: "Assembly",
    attributes: [
      { id: "code", name: "code" },
      { id: "weight", name: "weight" },
    ],
  },
  PP: { kind: "entity", id: "PP", name: "PurchasedPart", base: "P" },
  T: { kind: "entity", id: "T", name: "Tool", attributes: [{ id: "weight", name: "weight" }] },
  D: { kind: "entity", id: "D", name: "Document", attributes: [{ id: "title", name: "title" }] },
  R: {
    kind: "relation",
    id: "R",
    name: "AttachedTo",
    ends: [
      { id: "eC", entity: "C", role: "comment", navigation: "comments", max: "*" },
      { id: "eP", types: [{ entity: "P" }, { entity: "A" }, { stereotype: "approvable" }], role: "attachedTo", navigation: "attachedTo", max: 1 },
    ],
  },
  RA: {
    kind: "relation",
    id: "RA",
    name: "Tagged",
    ends: [
      { id: "aT", entity: "T", role: "tool", navigation: "tools", max: "*" },
      { id: "aAny", anyEntity: true, role: "subject", navigation: "subject", max: 1 },
    ],
  },
};

const sources = (carriers: Record<string, string[]> = { eP: ["D"] }): FieldSources => ({
  doc: (id) => docs[id],
  relations: [docs.R, docs.RA],
  stereotype: () => undefined,
  kindOf: (id) => docs[id]?.kind as string | undefined,
  carriers: (relation, endIndex) => carriers[String((relation.ends as Json[])[endIndex].id)],
});

describe("the pickers: an entity's links through an end with several types", () => {
  it("offers the end on the entity at the other end, naming the covered types", () => {
    const fields = objectFields("C", sources());
    const end = fields.find((f) => f.id === "eP")!;
    expect(end).toMatchObject({ kind: "end", name: "attachedTo", many: false, targets: ["P", "A", "D"] });
    expect(end.target).toBeUndefined();
    expect(end.typesLabel).toBe("Part, Assembly or approvable (stereotype)");
    expect(fieldOptionLabel(end)).toBe("attachedTo (Part, Assembly or approvable (stereotype))");
    // A one-type end keeps its plain label.
    const oneType = objectFields("D", sources()).find((f) => f.id === "eC")!;
    expect(fieldOptionLabel(oneType)).toBe("comments");
  });

  it("offers the other end's navigation on every covered type, derived entities included, and through any entity on every entity", () => {
    for (const covered of ["P", "A", "PP", "D"])
      expect(objectFields(covered, sources()).find((f) => f.id === "eC")).toMatchObject({ kind: "end", target: "C", many: true });
    expect(objectFields("T", sources()).find((f) => f.id === "eC")).toBeUndefined();
    // Any entity covers Comment too: it holds the end that leads to Tool.
    expect(objectFields("C", sources()).find((f) => f.id === "aT")).toMatchObject({ target: "T" });
    expect(objectFields("D", sources()).find((f) => f.id === "aT")).toMatchObject({ target: "T" });
    const any = objectFields("T", sources()).find((f) => f.id === "aAny")!;
    expect(any).toMatchObject({ anyEntity: true, typesLabel: "any entity" });
  });

  it("a path goes on through the fields every covered type has by id, and none through any entity", () => {
    const fieldsOf = (entity: string) => objectFields(entity, sources());
    // Part and Assembly hold `code` and `weight` under one id each; the stereotype's carrier Document holds neither.
    expect(pathProblem("C", ["eP", "code"], (e) => objectFields(e, sources({ eP: [] })), "field")).toBeNull();
    expect(pathProblem("C", ["eP", "code"], fieldsOf, "field")).toContain("no attribute or end");
    expect(pathProblem("C", ["eP", "weight"], fieldsOf, "field")).toContain("no attribute or end");
    expect(pathProblem("C", ["eP", "title"], fieldsOf, "field")).toContain("no attribute or end");
    expect(pathProblem("T", ["aAny", "weight"], (e) => objectFields(e, sources()), "field")).toContain("no attribute or end");
  });

  it("common fields of covered types: what an id means for all of them", () => {
    const lists = ["P", "A"].map((e) => objectFields(e, sources()));
    expect(commonFields(lists).map((f) => f.id)).toEqual(expect.arrayContaining(["code", "weight", "eC"]));
    // Document holds the end to Comment, and the any-entity end, but none of Part's attributes.
    expect(commonFields([objectFields("P", sources()), objectFields("D", sources())]).map((f) => f.id)).toEqual(["eC", "aT"]);
  });
});

const row = (id: string, kind: string, name: string, extra: Partial<ElementSummary> = {}): ElementSummary => ({ id, kind, name, ...extra }) as ElementSummary;

describe("an entity's related list", () => {
  const rows = [
    row("P", "entity", "Part"),
    row("C", "entity", "Comment"),
    row("T", "entity", "Tool"),
    row("S", "stereotype", "approvable"),
    row("R", "relation", "AttachedTo", {
      ends: [
        { entity: "C", role: "comment" },
        { role: "attachedTo", entities: ["P"] },
      ],
    } as never),
    row("RA", "relation", "Tagged", {
      ends: [
        { entity: "T", role: "tool" },
        { role: "subject", anyEntity: true },
      ],
    } as never),
  ];

  it("lists an any-entity relation under every entity, once, beside the relations it names directly", () => {
    expect(relatedOf(rows, "P").relations.map((r) => r.id)).toEqual(["R", "RA"]);
    expect(relatedOf(rows, "T").relations.map((r) => r.id)).toEqual(["RA"]);
    expect(relatedOf(rows, "C").relations.map((r) => r.id)).toEqual(["R", "RA"]);
  });

  it("does not list it under a stereotype or other element, and follows an edit to the relation", () => {
    expect(relatedOf(rows, "S").relations).toEqual([]);
    const without = rows.map((r) =>
      r.id === "RA"
        ? row("RA", "relation", "Tagged", {
            ends: [
              { entity: "T", role: "tool" },
              { entity: "P", role: "subject" },
            ],
          } as never)
        : r,
    );
    expect(relatedOf(without, "T").relations.map((r) => r.id)).toEqual(["RA"]);
    expect(relatedOf(without, "P").relations.map((r) => r.id)).toEqual(["R", "RA"]);
    const gone = rows.filter((r) => r.id !== "RA");
    expect(relatedOf(gone, "T").relations).toEqual([]);
  });
});

// ---------------------------------------------------------------- the mock's process rules

const ID = {
  part: "01J92P0V0JR8BE8253SKT29C01",
  assembly: "01J92P0V0JR8BE8253SKT29C02",
  tool: "01J92P0V0JR8BE8253SKT29C03",
  comment: "01J92P0V0JR8BE8253SKT29C04",
  stereo: "01J92P0V0JR8BE8253SKT29C05",
  contract: "01J92P0V0JR8BE8253SKT29C06",
  rel: "01J92P0V0JR8BE8253SKT29C07",
  eNear: "01J92P0V0JR8BE8253SKT29C08",
  eFar: "01J92P0V0JR8BE8253SKT29C09",
  proc: "01J92P0V0JR8BE8253SKT29C0A",
  sub: "01J92P0V0JR8BE8253SKT29C0B",
  scenario: "01J92P0V0JR8BE8253SKT29C0C",
  s1: "01J92P0V0JR8BE8253SKT29C0D",
};

function backend() {
  const b = new MockBackend();
  const make = (json: Json) => expect(b.model.create(json).outcome).toBe("saved");
  const entity = (id: string, name: string, extra: Json = {}) => make({ kind: "entity", id, name, abstract: true, ...extra });
  entity(ID.part, "Part");
  entity(ID.assembly, "Assembly");
  entity(ID.tool, "Tool");
  entity(ID.comment, "Comment");
  make({ kind: "stereotype", id: ID.stereo, key: "approvable", name: "Approvable", appliesTo: ["entity"] });
  entity(ID.contract, "Contract", { stereotypes: ["approvable"] });
  make({
    kind: "relation",
    id: ID.rel,
    name: "AttachedTo",
    ends: [
      { id: ID.eNear, entity: ID.comment, role: "comment", max: "*" },
      { id: ID.eFar, entity: ID.part, role: "attachedTo", max: 1 },
    ],
  });
  b.model.externalEdit(ID.rel, (json) => {
    (json.ends as Json[])[1] = { id: ID.eFar, role: "attachedTo", max: 1, types: [{ entity: ID.part }, { entity: ID.assembly }, { stereotype: "approvable" }] };
  });
  return b;
}

const O1 = "01J92P0V0JR8BE8253SKT29C20";
const O2 = "01J92P0V0JR8BE8253SKT29C21";
const found = (b: MockBackend, rule: string, element: string) => b.model.validate().diagnostics.filter((d) => d.rule === rule && d.elementId === element);

describe("the mock's rights and trigger points through an end with several types", () => {
  it("every covered type holds the other end, so a right or trigger point naming it on those subjects is clean, and on one that doesn't it is not", () => {
    const b = backend();
    const process = (subject: string, extra: Json = {}) => ({
      kind: "process",
      id: ID.proc,
      name: "Review",
      use: "lifecycle",
      subjects: [{ id: ID.sub, entity: subject }],
      states: [{ id: "01J92P0V0JR8BE8253SKT29C10", name: "Open" }],
      initial: "01J92P0V0JR8BE8253SKT29C10",
      rights: [{ id: "01J92P0V0JR8BE8253SKT29C11", name: "Link", access: "allow", verbs: ["connect"], ends: [ID.eNear] }],
      ...extra,
    });
    // The end that leads to Comment is held by Part, an Assembly and a carrier of the stereotype.
    for (const covered of [ID.part, ID.assembly, ID.contract]) {
      const doc = process(covered);
      const result = b.model.get(ID.proc) ? b.model.save(ID.proc, doc, b.model.get(ID.proc)!.hash) : b.model.create(doc);
      expect(result.outcome, covered).toBe("saved");
      expect(found(b, "MQ9903", ID.proc), covered).toEqual([]);
    }
    // Tool is covered by no entry: it doesn't hold the end, so the right is refused.
    const refused = b.model.save(ID.proc, process(ID.tool), b.model.get(ID.proc)!.hash);
    expect(refused.diagnostics.some((d) => d.rule === "MQ9903")).toBe(true);
  });
});

describe("the mock's scenario links through an end with several types", () => {
  const scenario = (linked: string) => ({
    kind: "scenario",
    id: ID.scenario,
    name: "Attach",
    process: ID.proc,
    subject: O1,
    objects: [
      { id: O1, name: "note", entity: ID.comment, links: [{ end: ID.eFar, object: O2 }] },
      { id: O2, name: "target", entity: linked },
    ],
    steps: [{ id: "01J92P0V0JR8BE8253SKT29C32", event: "01J92P0V0JR8BE8253SKT29C31" }],
  });

  it("MQ9305 when the target's entity is not covered, with the design's text; clean for a covered entity, a derived one and a carrier", () => {
    const b = backend();
    expect(
      b.model.create({
        kind: "process",
        id: ID.proc,
        name: "Review",
        use: "lifecycle",
        subjects: [{ id: ID.sub, entity: ID.comment }],
        states: [{ id: "01J92P0V0JR8BE8253SKT29C10", name: "Open" }],
        events: [{ id: "01J92P0V0JR8BE8253SKT29C31", name: "go" }],
        initial: "01J92P0V0JR8BE8253SKT29C10",
      }).outcome,
    ).toBe("saved");
    for (const ok of [ID.part, ID.assembly, ID.contract]) {
      const created = b.model.get(ID.scenario) ? b.model.save(ID.scenario, scenario(ok), b.model.get(ID.scenario)!.hash) : b.model.create(scenario(ok));
      expect(created.outcome, ok).toBe("saved");
      expect(found(b, "MQ9305", ID.scenario), ok).toEqual([]);
    }
    const refused = b.model.save(ID.scenario, scenario(ID.tool), b.model.get(ID.scenario)!.hash);
    const message = refused.diagnostics.find((d) => d.rule === "MQ9305")?.message;
    expect(message).toBe(
      "Object 'note' links to 'target' through end 'AttachedTo.attachedTo', which leads to Part, Assembly, an entity with stereotype approvable, or an entity derived from one, not to entity 'Tool'; link to an object of one of those, or through another end.",
    );
  });

  it("an any-entity end covers every entity", () => {
    const entries = [
      { id: "e", json: { kind: "entity", id: "e", name: "E" } },
      {
        id: "r",
        json: {
          kind: "relation",
          id: "r",
          name: "R",
          ends: [
            { id: "x", entity: "e", role: "a" },
            { id: "y", anyEntity: true, role: "b", navigation: "b" },
          ],
        },
      },
    ];
    const ends = severalEnds(entries as never);
    expect(ends.get("y")!.covers("anything")).toBe(true);
    expect(ends.get("x")).toBeUndefined();
    expect(severalEnds([entries[0]] as never).size).toBe(0);
  });
});

describe("the mock's stepper through an end with several types", () => {
  const doc = {
    kind: "process",
    id: "PR",
    name: "Review",
    use: "lifecycle",
    states: [{ id: "S", name: "S" }],
    initial: "S",
    subjects: [{ id: "su", entity: "Comment" }],
  } as Json;
  const objects = [
    { id: "o1", name: "note", entity: "Comment" },
    { id: "o2", name: "part", entity: "PartDerived" },
    { id: "o3", name: "tool", entity: "Tool" },
  ];
  const ends = new Map([
    ["far", { name: "AttachedTo.attachedTo", covers: (e: string) => ["Part", "PartDerived"].includes(e), leadsTo: "Part, or an entity derived from it" }],
  ]);
  const options = { objects, subject: "o1", subjectEntity: "Comment", lifecycles: [doc], severalEnds: ends, entityNames: (id: string) => id };
  const connect = (object: string, end = "far") => [{ input: "verb", verb: "connect", end, object, actor: "X" }];

  it("a connect step through the end checks the connected object's entity (MQ9305); a covered or derived one passes", () => {
    const bad = simulate(doc, null, connect("o3"), options);
    expect(bad.trace[1].accepted).toBe(false);
    expect(bad.diagnostics?.some((d) => d.rule === "MQ9305" && d.message.includes("not to entity 'Tool'"))).toBe(true);
    const good = simulate(doc, null, connect("o2"), options);
    expect(good.diagnostics?.filter((d) => d.rule === "MQ9305") ?? []).toEqual([]);
  });

  it("a connect step through a one-type end keeps today's checks: no look at the connected object's entity", () => {
    const result = simulate(doc, null, connect("o3", "single"), options);
    expect(result.diagnostics?.filter((d) => d.rule === "MQ9305") ?? []).toEqual([]);
  });
});

describe("in_state through an object of a type the lifecycle doesn't govern", () => {
  const lifecycle: Json = {
    kind: "process",
    id: "HR",
    name: "Employment",
    use: "lifecycle",
    subjects: [{ id: "hs", entity: "Part" }],
    states: [
      { id: "Active", name: "Active" },
      { id: "Left", name: "Left" },
    ],
    initial: "Active",
  };
  const run: Json = {
    kind: "process",
    id: "P",
    name: "P",
    use: "lifecycle",
    subjects: [{ id: "se", entity: "Comment" }],
    events: [{ id: "go", name: "go" }],
    guards: [{ id: "g", name: "g", condition: { inState: ["Active"], of: ["eFar"] } }],
    states: [
      { id: "A", name: "A" },
      { id: "B", name: "B" },
    ],
    transitions: [{ id: "t1", source: "A", trigger: "event", event: "go", targets: ["B"], guard: "g" }],
  };
  const objects = (entity: string) => [
    { id: "o1", name: "note", entity: "Comment", links: [{ end: "eFar", object: "o2" }] },
    { id: "o2", name: "target", entity },
  ];
  const options = (entity: string, governs?: (l: Json, e: string) => boolean) => ({
    objects: objects(entity),
    subject: "o1",
    subjectEntity: "Comment",
    lifecycles: [lifecycle, run],
    ...(governs ? { governs } : {}),
  });
  const governs = (l: Json, e: string) => l.id === "HR" && e === "Part";

  it("answers unknown for an object the lifecycle doesn't govern, with no note: the guard is not true, so the step is refused", () => {
    const result = simulate(run, null, [{ event: "go" }], options("Tool", governs));
    expect(result.trace[1].accepted).toBe(false);
    expect(result.trace[1].notes ?? []).toEqual([]);
  });

  it("reads the states of an object the lifecycle governs, assuming the initial state with a note as before", () => {
    const result = simulate(run, null, [{ event: "go" }], options("Part", governs));
    expect(result.trace[1].accepted).toBe(true);
    expect(result.trace[1].notes?.[0]).toContain("initial state is assumed");
  });
});
