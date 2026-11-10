// The mock's run of revisions (revisions-design.md section 9): a revise verb step at each level and with a chosen name, refusal `revision`,
// the type's reset and cleared values, thawing, a lifecycle starting over, keeping its states or going to a state, a move that gives the next
// revision (a labelling move and a revise move), the trace's `revision`, and a project with no block replaying as before.
import { describe, expect, it } from "vitest";
import { simulate, revisionAfter } from "@/mocks/model/stepper";
import { resolveRevisions } from "@/model/revisionSequence";

type Json = Record<string, unknown>;

const st = (id: string): Json => ({ id, name: id });
const DRAWING = {
  levels: [
    { name: "major", format: "letters", skip: ["I", "O", "Q", "S", "X", "Z"] },
    { name: "minor", format: "numbers", startEmpty: true },
  ],
  boundAttribute: "rev",
  attributes: [
    { attribute: "approvedBy", onRevise: "reset" },
    { attribute: "notes", onRevise: "clear" },
  ],
};
const entity = (revisions?: Json): Json => ({ kind: "entity", id: "E", name: "Drawing", ...(revisions ? { revisions } : {}) });

function life(extra: Json = {}): Json {
  return {
    kind: "process",
    id: "L",
    name: "DrawingLifecycle",
    use: "lifecycle",
    subjects: [{ id: "s", entity: "E" }],
    events: [
      { id: "release", name: "release", verb: "promote" },
      { id: "reopen", name: "reopen", verb: "revise" },
      { id: "lock", name: "lock", verb: "freeze" },
    ],
    states: [st("InWork"), st("Approved"), st("Released")],
    initial: "InWork",
    transitions: [
      { id: "t1", source: "InWork", event: "release", targets: ["Approved"] },
      { id: "t2", source: "Approved", event: "release", targets: ["Released"], nextRevision: 1 },
      { id: "t3", source: "Released", event: "reopen", targets: ["InWork"], nextRevision: 2 },
      { id: "t4", source: "InWork", event: "lock", targets: ["InWork"] },
    ],
    ...extra,
  };
}

const attributes = [
  { id: "rev", name: "revision", default: undefined },
  { id: "approvedBy", name: "approvedBy", default: "nobody" },
  { id: "notes", name: "notes" },
];

function run(doc: Json, steps: Json[], block: Json | null = DRAWING, object: Json = {}, lifecycles: Json[] = [doc]) {
  const e = entity(block ?? undefined);
  const governing = block ? ["L"] : [];
  return simulate(doc, null, steps, {
    lifecycles,
    subjectEntity: "E",
    attributesOf: () => attributes,
    objects: [{ id: "o1", name: "o1", entity: "E", values: { approvedBy: "Pat", notes: "x" }, ...object }],
    subject: "o1",
    revisionsOf: () => resolveRevisions("E", new Map(), () => e as never, governing),
  } as never).trace;
}

const verb = (extra: Json = {}): Json => ({ input: "verb", verb: "revise", ...extra });
const last = (trace: ReturnType<typeof run>) => trace[trace.length - 1];

describe("a revise verb step", () => {
  it("revises at level 2, then 1, and the bound attribute follows without showing in values", () => {
    const trace = run(life(), [verb({ revision: 2 }), verb({ revision: 2 }), verb({ revision: 1 })]);
    expect(trace.slice(1).map((t) => t.revision)).toEqual([
      { from: "A", to: "A.1", level: 2 },
      { from: "A.1", to: "A.2", level: 2 },
      { from: "A.2", to: "B", level: 1 },
    ]);
    expect(revisionAfter.get(last(trace))).toBe("B");
  });

  it("takes a chosen name that is later, and refuses an earlier one with `revision`", () => {
    const ok = run(life(), [verb({ revision: "D" })]);
    expect(ok[1].revision).toEqual({ from: "A", to: "D", level: null });
    const refused = run(life(), [verb({ revision: "D" }), verb({ revision: "B" }), verb({ revision: "I" })]);
    expect(refused[2]).toMatchObject({ accepted: false, refusal: "revision" });
    expect(refused[3]).toMatchObject({ accepted: false, refusal: "revision" });
    expect(refused[2].revision).toBeUndefined();
  });

  it("is refused at the end of a list", () => {
    const e = { levels: [{ values: ["Prototype", "Production"] }] };
    const trace = run(life(), [verb(), verb()], e);
    expect(trace[1].revision).toEqual({ from: "Prototype", to: "Production", level: 1 });
    expect(trace[2]).toMatchObject({ accepted: false, refusal: "revision" });
  });

  it("resets and clears the type's values, writes them as values, and leaves the bound attribute out of values", () => {
    const trace = run(life(), [verb()]);
    expect(trace[1].valuesChanged).toEqual({ approvedBy: "nobody", notes: null });
    expect(trace[1].notes?.join(" ")).toContain("Revised A to B");
  });

  it("thaws a frozen subject", () => {
    const trace = run(life(), [{ input: "event", event: "lock" }, verb()]);
    expect(trace[1].frozen).toBe(true);
    expect(trace[2].frozen).toBeUndefined();
  });

  it("starts the running lifecycle over by default, and again after it ended", () => {
    const trace = run(life(), [
      { input: "event", event: "release" },
      { input: "event", event: "release" },
      verb({ revision: 1 }),
    ]);
    expect(trace[2].configuration).toEqual(["Released"]);
    expect(trace[3].configuration).toEqual(["InWork"]);
    expect(trace[3].microsteps[0]).toMatchObject({ transitions: [], exited: ["Released"], entered: ["InWork"] });
    expect(trace[3].notes?.join(" ")).toContain("started over");
  });

  it("keeps the states with keep, and goes to a named state", () => {
    const keep = run(life(), [{ input: "event", event: "release" }, verb()], { ...DRAWING, lifecycles: [{ lifecycle: "L", onRevise: "keep" }] });
    expect(keep[2].configuration).toEqual(["Approved"]);
    expect(keep[2].notes?.join(" ")).toContain("kept its states");
    const named = run(life(), [{ input: "event", event: "release" }, verb()], { ...DRAWING, lifecycles: [{ lifecycle: "L", onRevise: "Released" }] });
    expect(named[2].configuration).toEqual(["Released"]);
  });

  it("gives another lifecycle its outcome through the object's states", () => {
    const other = { kind: "process", id: "M", name: "Delivery", use: "lifecycle", subjects: [{ id: "m", entity: "E" }], states: [st("Packed"), st("Shipped")], initial: "Packed" };
    const doc = life();
    const block = { ...DRAWING, lifecycles: [{ lifecycle: "M", onRevise: "Shipped" }] };
    const e = entity(block);
    const result = simulate(doc, null, [verb()], {
      lifecycles: [doc, other],
      subjectEntity: "E",
      attributesOf: () => attributes,
      objects: [{ id: "o1", name: "o1", entity: "E", states: ["Packed"] }],
      subject: "o1",
      revisionsOf: () => resolveRevisions("E", new Map(), () => e as never, ["L", "M"]),
    } as never);
    expect(result.trace[1].notes?.join(" ")).toContain("Delivery went to Shipped");
  });
});

describe("a move that gives the next revision", () => {
  it("labels with a promote move, and revises with a revise move", () => {
    const trace = run(life(), [
      { input: "event", event: "release" },
      { input: "event", event: "release" },
      { input: "event", event: "reopen" },
    ]);
    expect(trace[2].revision).toEqual({ from: "A", to: "B", level: 1 });
    expect(trace[2].configuration).toEqual(["Released"]);
    // The revise move at level 2 gives B.1 and resets the type's values; its lifecycle goes where the move goes.
    expect(trace[3].revision).toEqual({ from: "B", to: "B.1", level: 2 });
    expect(trace[3].configuration).toEqual(["InWork"]);
    expect(trace[3].valuesChanged).toEqual({ approvedBy: "nobody", notes: null });
  });

  it("is refused with `revision` when the sequence cannot give it", () => {
    const list = { levels: [{ values: ["Only"] }] };
    const trace = run(life(), [{ input: "event", event: "release" }, { input: "event", event: "release" }], list);
    expect(trace[2]).toMatchObject({ accepted: false, refusal: "revision" });
  });

  it("takes a chosen name on the event step", () => {
    const trace = run(life(), [{ input: "event", event: "release" }, { input: "event", event: "release", revision: "F" }]);
    expect(trace[2].revision).toEqual({ from: "A", to: "F", level: null });
  });
});

describe("a project with no block", () => {
  it("replays as before: no revision in the trace, a revise verb step only noted", () => {
    const trace = run(life({ transitions: life().transitions as Json[] }), [verb()], null);
    expect(trace[1].revision).toBeUndefined();
    expect(trace[1].notes?.join(" ")).toContain("did not apply");
  });

  it("starts from the object's revision", () => {
    const trace = run(life(), [verb({ revision: 1 })], { levels: DRAWING.levels }, { revision: "H" });
    expect(trace[1].revision).toEqual({ from: "H", to: "J", level: 1 });
  });
});
