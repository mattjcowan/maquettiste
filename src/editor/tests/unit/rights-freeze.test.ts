// The mock's part 2 rights check (rights-design.md decisions 16 to 18 and 21): freezing refuses before any right and no allow lifts
// it; a right with `whileFrozen` holds only while frozen; `read` and `modify` are answered per attribute, and attributes not known
// fail closed; naming `freeze` on an event freezes the subject; MQ9309 names the step that froze it; and a project whose rights
// name no attributes keeps part 1's answers.
import { describe, expect, it } from "vitest";
import { simulate } from "@/mocks/model/stepper";
import { rightsGrid } from "@/mocks/model/rightsCheck";
import { rightDiagnostics } from "@/mocks/model/rightRules";
import { Membership } from "@/mocks/model/membership";
import { freezingStep } from "@/mocks/processHandlers";

type Json = Record<string, unknown>;

const st = (id: string): Json => ({ id, name: id });
const ev = (name: string, extra: Json = {}): Json => ({ id: name, name, ...extra });
const right = (id: string, access: "allow" | "deny", verbs: string[], extra: Json = {}): Json => ({ id, name: id, access, verbs, ...extra });

function life(extra: Json = {}): Json {
  return {
    kind: "process",
    id: "L",
    name: "L",
    use: "lifecycle",
    subjects: [{ id: "s", entity: "E" }],
    events: [ev("promote", { verb: "promote" }), ev("lock", { verb: "freeze" }), ev("unlock", { verb: "thaw" }), ev("edit", { verb: "modify" })],
    states: [st("A"), st("B")],
    transitions: [
      { id: "t1", source: "A", event: "promote", targets: ["B"] },
      { id: "t2", source: "A", event: "lock", targets: ["A"] },
      { id: "t3", source: "A", event: "unlock", targets: ["A"] },
      { id: "t4", source: "A", event: "edit", targets: ["A"] },
    ],
    ...extra,
  };
}

const actors = [
  { id: "X", json: { name: "X", type: "person" } },
  { id: "Y", json: { name: "Y", type: "person" } },
];
const attributesOf = () => [
  { id: "salary", name: "salary" },
  { id: "title", name: "title" },
];
/** The subject o1 linked to o2 through `parts`; `frozen` freezes the subject, `link` the link. */
const objects = (frozen = false, link = false): Json[] => [
  { id: "o1", name: "o1", entity: "E", ...(frozen ? { frozen: true } : {}), links: [{ end: "parts", object: "o2", ...(link ? { frozen: true } : {}) }] },
  { id: "o2", name: "o2", entity: "E" },
];

function run(doc: Json, steps: Json[], cast: Json[] = objects(), lifecycles: Json[] = [doc]) {
  return simulate(doc, null, steps, {
    lifecycles,
    subjectEntity: "E",
    membership: new Membership(actors),
    attributesOf,
    objects: cast,
    subject: "o1",
  } as never).trace.filter((t) => t.index >= 0);
}
const lastOf = (doc: Json, steps: Json[], cast: Json[] = objects(), lifecycles: Json[] = [doc]) => run(doc, steps, cast, lifecycles).at(-1)!;
const verb = (v: string, extra: Json = {}): Json => ({ input: "verb", verb: v, actor: "X", ...extra });
const event = (name: string): Json => ({ event: name, actor: "X" });

describe("attributes are checked one by one", () => {
  const denyRead = life({ rights: [right("d1", "deny", ["read"], { attributes: ["salary"], actors: ["Y"] })] });

  it("a deny naming an attribute refuses a read of it, naming the right", () => {
    const refused = lastOf(denyRead, [verb("read", { actor: "Y", attributes: ["salary"] })]);
    expect([refused.accepted, refused.refusal, refused.refusedBy]).toEqual([false, "right", "d1"]);
  });

  it("leaves another attribute, and other actors, alone", () => {
    expect(lastOf(denyRead, [verb("read", { actor: "Y", attributes: ["title"] })]).accepted).toBe(true);
    expect(lastOf(denyRead, [verb("read", { actor: "X", attributes: ["salary"] })]).accepted).toBe(true);
  });

  it("one denied attribute refuses a modify of several", () => {
    const doc = life({ rights: [right("d1", "deny", ["modify"], { attributes: ["salary"] })] });
    expect(lastOf(doc, [verb("modify", { values: { title: "a", salary: 2 } })]).refusedBy).toBe("d1");
    expect(lastOf(doc, [verb("modify", { values: { title: "a" } })]).accepted).toBe(true);
  });

  it("an allow naming only one attribute says nothing about another", () => {
    const doc = life({ rightsDefault: "deny", rights: [right("a1", "allow", ["read"], { attributes: ["title"] })] });
    expect(lastOf(doc, [verb("read", { attributes: ["title"] })]).accepted).toBe(true);
    const other = lastOf(doc, [verb("read", { attributes: ["salary"] })]);
    expect([other.refusal, other.refusedBy]).toEqual(["default", "L"]);
    expect(lastOf(doc, [verb("read", { attributes: ["title", "salary"] })]).refusal).toBe("default");
  });

  it("a right naming no attributes applies to every attribute", () => {
    const doc = life({ rightsDefault: "deny", rights: [right("a1", "allow", ["read"])] });
    expect(lastOf(doc, [verb("read", { attributes: ["title", "salary"] })]).accepted).toBe(true);
  });

  it("attributes not known fail closed: a deny naming attributes holds, an allow grants nothing", () => {
    const deny = life({ rights: [right("d1", "deny", ["modify"], { attributes: ["salary"] })] });
    expect(lastOf(deny, [event("edit")]).refusedBy).toBe("d1");
    expect(lastOf(deny, [verb("read")]).accepted).toBe(true);
    expect(lastOf(deny, [verb("modify")]).refusedBy).toBe("d1");
    const allow = life({ rightsDefault: "deny", rights: [right("a1", "allow", ["modify"], { attributes: ["salary"] })] });
    expect(lastOf(allow, [event("edit")]).refusal).toBe("default");
    expect(lastOf(allow, [verb("modify")]).refusal).toBe("default");
    const plain = life({ rightsDefault: "deny", rights: [right("a2", "allow", ["modify"])] });
    expect(lastOf(plain, [event("edit")]).accepted).toBe(true);
  });

  it("a right that names attributes on another lifecycle refuses too (any no wins)", () => {
    const hr = { ...life({ rights: [right("h1", "deny", ["read"], { attributes: ["salary"] })] }), id: "HR", name: "HR" };
    const refused = lastOf(life(), [verb("read", { attributes: ["salary"] })], objects(), [life(), hr]);
    expect(refused.refusedBy).toBe("h1");
  });
});

describe("a project whose rights name no attributes keeps part 1's answers", () => {
  const doc = life({
    rightsDefault: "deny",
    rights: [right("a1", "allow", ["modify", "read"], { actors: ["X"] }), right("d1", "deny", ["modify"], { actors: ["Y"] })],
  });

  it("answers alike with or without attributes on the step, and never says frozen", () => {
    for (const extra of [{}, { attributes: ["salary"] }, { values: { salary: 1 } }]) {
      expect(lastOf(doc, [verb("read", extra)]).accepted).toBe(true);
      const refused = lastOf(doc, [verb("modify", { actor: "Y", ...extra })]);
      expect([refused.refusal, refused.refusedBy, refused.frozen]).toEqual(["right", "d1", undefined]);
    }
    expect(lastOf(doc, [event("edit")]).accepted).toBe(true);
    expect(lastOf(doc, [verb("modify", { actor: "Y" })]).refusal).toBe("right");
    expect(lastOf(doc, [verb("modify", { actor: "Y" })]).refusal).not.toBe("default");
  });
});

describe("freezing", () => {
  it("a frozen object refuses modify, delete, connect and disconnect as frozen, naming no element, and the trace says it is frozen", () => {
    for (const v of ["modify", "delete", "connect", "disconnect"]) {
      const t = lastOf(life(), [verb(v)], objects(true));
      expect([v, t.accepted, t.refusal, t.refusedBy, t.frozen]).toEqual([v, false, "frozen", undefined, true]);
    }
  });

  it("refuses before the actor list and before any right, and no allow lifts it", () => {
    const doc = life({
      events: [ev("edit", { verb: "modify", actors: ["Y"] }), ev("promote", { verb: "promote" })],
      rights: [right("deny1", "deny", ["modify"]), right("a1", "allow", ["modify"], { whileFrozen: true })],
    });
    const t = lastOf(doc, [event("edit")], objects(true));
    expect([t.refusal, t.refusedBy]).toEqual(["frozen", undefined]);
    expect(lastOf(doc, [verb("modify")], objects(true)).refusal).toBe("frozen");
  });

  it("stops nothing else: reads, moves and own verbs go on", () => {
    expect(lastOf(life(), [verb("read")], objects(true)).accepted).toBe(true);
    expect(lastOf(life(), [verb("change-owner")], objects(true)).accepted).toBe(true);
    expect(lastOf(life(), [event("promote")], objects(true)).accepted).toBe(true);
  });

  it("a right with whileFrozen holds only while frozen", () => {
    const doc = life({ rights: [right("d1", "deny", ["change-owner"], { whileFrozen: true })] });
    expect(lastOf(doc, [verb("change-owner")]).accepted).toBe(true);
    const frozen = lastOf(doc, [verb("change-owner")], objects(true));
    expect([frozen.refusal, frozen.refusedBy]).toEqual(["right", "d1"]);
    expect(run(doc, [verb("freeze"), verb("change-owner")]).map((t) => t.refusedBy)).toEqual([undefined, "d1"]);
  });

  it("a freeze verb step freezes the subject, and a thaw undoes it", () => {
    const trace = run(life(), [verb("modify"), verb("freeze"), verb("modify"), verb("thaw"), verb("modify")]);
    expect(trace.map((t) => [t.accepted, t.frozen ?? false])).toEqual([
      [true, false],
      [true, true],
      [false, true],
      [true, false],
      [true, false],
    ]);
    expect(trace[2].refusal).toBe("frozen");
  });

  it("freezing what is frozen, or thawing what is not, changes nothing and adds a note", () => {
    const trace = run(life(), [verb("thaw"), verb("freeze"), verb("freeze")]);
    expect(trace[0].notes?.[0]).toMatch(/not frozen/);
    expect(trace[1].notes).toBeUndefined();
    expect(trace[2].notes?.[0]).toMatch(/already frozen/);
    expect(trace[2].accepted).toBe(true);
  });

  it("a freeze that a right refuses does not freeze", () => {
    const doc = life({ rights: [right("d1", "deny", ["freeze"])] });
    const trace = run(doc, [verb("freeze"), verb("modify")]);
    expect([trace[0].refusal, trace[1].accepted]).toEqual(["right", true]);
  });

  it("naming freeze on an event is the opt-in: the accepted event freezes, a thaw event thaws", () => {
    const trace = run(life(), [event("lock"), event("edit"), event("unlock"), event("edit")]);
    expect(trace.map((t) => [t.accepted, t.refusal])).toEqual([
      [true, null],
      [false, "frozen"],
      [true, null],
      [true, null],
    ]);
    expect(trace[0].frozen).toBe(true);
  });

  it("a refused freeze event does not freeze", () => {
    const doc = life({ events: [ev("promote", { verb: "promote" }), ev("lock", { verb: "freeze", actors: ["Y"] }), ev("edit", { verb: "modify" })] });
    const trace = run(doc, [event("lock"), event("edit")]);
    expect([trace[0].accepted, trace[1].accepted]).toEqual([false, true]);
  });

  it("a project that never names freeze never freezes", () => {
    const trace = run(life(), [event("promote"), verb("modify")]);
    expect(trace.every((t) => t.accepted && t.frozen === undefined)).toBe(true);
  });

  it("names the step that froze it, for MQ9309, and none once thawed", () => {
    const doc = life();
    const trace = run(doc, [event("lock"), verb("modify"), event("unlock"), verb("freeze"), verb("modify")]);
    expect(freezingStep(doc, run(doc, [event("lock"), event("unlock"), verb("modify")]) as never, 2)).toBeNull();
    expect(freezingStep(doc, trace as never, 1)).toBe(0);
    expect(freezingStep(doc, trace as never, 4)).toBe(3);
    const started = run(doc, [verb("modify")], objects(true));
    expect(freezingStep(doc, started as never, 0)).toBeNull();
  });
});

describe("frozen links", () => {
  it("a frozen link can't be disconnected or have its attributes modified, and its objects are not frozen", () => {
    const cast = objects(false, true);
    for (const v of ["disconnect", "modify"]) {
      const t = lastOf(life(), [verb(v, { end: "parts", object: "o2" })], cast);
      expect([v, t.refusal, t.refusedBy, t.frozen]).toEqual([v, "frozen", undefined, undefined]);
    }
    expect(lastOf(life(), [verb("connect", { end: "parts", object: "o2" })], cast).accepted).toBe(true);
    expect(lastOf(life(), [verb("modify")], cast).accepted).toBe(true);
    expect(lastOf(life(), [verb("disconnect", { end: "other", object: "o2" })], cast).accepted).toBe(true);
  });

  it("a thaw of the link lets it go, and a freeze of a link freezes only it", () => {
    const step = { end: "parts", object: "o2" };
    const thawed = run(life(), [verb("thaw", step), verb("disconnect", step)], objects(false, true));
    expect(thawed.map((t) => t.accepted)).toEqual([true, true]);
    const frozen = run(life(), [verb("freeze", step), verb("disconnect", step), verb("modify")]);
    expect(frozen.map((t) => t.accepted)).toEqual([true, false, true]);
    expect(frozen[0].frozen).toBeUndefined();
  });

  it("a whileFrozen right with ends holds only while the link is frozen", () => {
    const doc = life({ rights: [right("d1", "deny", ["read"], { ends: ["parts"], whileFrozen: true })] });
    const step = verb("read", { end: "parts", object: "o2" });
    expect(lastOf(doc, [step]).accepted).toBe(true);
    expect(lastOf(doc, [step], objects(false, true)).refusedBy).toBe("d1");
  });
});

describe("who can do what, part 2", () => {
  const world = (lifecycles: Json[]) => ({
    lifecycles,
    named: new Map<string, Json>(),
    membership: new Membership(actors),
    ownerActors: new Set<string>(),
    entitiesOf: (l: Json) => (l.subjects as Json[]).map((s) => String(s.entity)),
  });
  const cellOf = (rows: ReturnType<typeof rightsGrid>, verb: string, actor: string | null = "X") =>
    rows.find((r) => r.state === "A" && r.actor === actor && r.verb === verb)!;

  it("the Frozen switch denies modify with no element, and answers whileFrozen rights", () => {
    const doc = life({
      rights: [right("r1", "deny", ["modify", "change-owner"], { actors: ["X"] }), right("w1", "deny", ["change-owner"], { actors: ["Y"], whileFrozen: true })],
    });
    const thawed = rightsGrid(doc, world([doc]));
    const frozen = rightsGrid(doc, world([doc]), { frozen: true });
    expect(cellOf(thawed, "modify")).toMatchObject({ answer: "denied", by: ["r1"] });
    expect(cellOf(frozen, "modify")).toMatchObject({ answer: "denied", byDefault: false, by: [] });
    expect(cellOf(thawed, "change-owner", "Y")).toMatchObject({ answer: "allowed" });
    expect(cellOf(frozen, "change-owner", "Y")).toMatchObject({ answer: "denied", by: ["w1"] });
  });

  it("a cell that a right with attributes would change depends, and names that right", () => {
    const doc = life({
      rights: [right("r1", "deny", ["read"], { actors: ["X"], attributes: ["salary"] }), right("r2", "deny", ["modify"], { actors: ["X"] })],
    });
    const rows = rightsGrid(doc, world([doc]));
    expect(cellOf(rows, "read")).toMatchObject({ answer: "depends", by: ["r1"] });
    expect(cellOf(rows, "read", null)).toMatchObject({ answer: "allowed" });
    expect(cellOf(rows, "modify")).toMatchObject({ answer: "denied", by: ["r2"] });
  });
});

describe("validation", () => {
  const collect = (json: Json) => {
    const found: string[] = [];
    rightDiagnostics(
      json,
      () => [],
      (rule) => void found.push(rule),
    );
    return found;
  };

  it("MQ9305: attributes beside a verb other than read, and a frozen link of an object that is not the subject", () => {
    const found = collect({
      kind: "scenario",
      id: "S",
      subject: "o1",
      objects: [
        { id: "o1", name: "o1", links: [{ end: "parts", object: "o2", frozen: true }] },
        { id: "o2", name: "o2", links: [{ end: "parts", object: "o1", frozen: true }] },
      ],
      steps: [
        { input: "verb", verb: "modify", attributes: ["salary"] },
        { input: "verb", verb: "read", attributes: ["salary"] },
      ],
    });
    expect(found).toEqual(["MQ9305", "MQ9305"]);
  });

  it("MQ9903: attributes beside a verb other than read and modify", () => {
    expect(collect(life({ rights: [right("r1", "deny", ["delete"], { attributes: ["salary"] })] }))).toContain("MQ9903");
    expect(collect(life({ rights: [right("r1", "deny", ["read"], { attributes: [] })] }))).not.toContain("MQ9903");
  });
});

describe("a move on a frozen object", () => {
  const doc: Json = {
    kind: "process",
    id: "P",
    name: "P",
    use: "lifecycle",
    subjects: [{ id: "se", entity: "E" }],
    events: [ev("go")],
    actions: [{ id: "bump", name: "bump", sets: [{ id: "s1", attribute: "n", value: { value: 7 } }] }],
    states: [st("A"), st("B")],
    transitions: [{ id: "t1", source: "A", event: "go", targets: ["B"], actions: ["bump"] }],
  };
  const go = (frozen: boolean) =>
    simulate(doc, null, [{ event: "go" }], {
      lifecycles: [doc],
      subjectEntity: "E",
      attributesOf: () => [{ id: "n", default: 1 }],
      objects: objects(frozen),
      subject: "o1",
    } as never).trace[1];

  it("applies its sets, and the trace adds a note", () => {
    const frozen = go(true);
    expect([frozen.accepted, frozen.valuesChanged]).toEqual([true, { n: 7 }]);
    expect(frozen.notes?.some((n) => /frozen/.test(n))).toBe(true);
    expect(go(false).notes).toBeUndefined();
  });
});
