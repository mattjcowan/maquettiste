// The mock's rights check (src/mocks/model/rightsCheck.ts and the stepper's use of it): each lifecycle answers on its own
// (denies, then allows, then its default), then any "no" wins; `except` never grants; closed lists count as allows that
// explicit rights only narrow; refusals follow the design's order and name the element; verb steps; and the grid.
import { describe, expect, it } from "vitest";
import { simulate } from "@/mocks/model/stepper";
import { rightsGrid } from "@/mocks/model/rightsCheck";
import { Membership } from "@/mocks/model/membership";
import { INVOICE_LIFECYCLE, PAID_IS_READ_ONLY } from "@/mocks/model/processSeed";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const st = (id: string, extra: Json = {}): Json => ({ id, name: id, ...extra });
const tr = (id: string, source: string, extra: Json = {}): Json => ({ id, source, ...extra });
const ev = (name: string, extra: Json = {}): Json => ({ id: name, name, ...extra });
const right = (id: string, access: "allow" | "deny", verbs: string[], extra: Json = {}): Json => ({ id, name: id, access, verbs, ...extra });

/** A lifecycle with one move, A to B, by the event `promote` (verb promote); ids are their own names. */
function life(id: string, extra: Json = {}): Json {
  return {
    kind: "process",
    id,
    name: id,
    use: "lifecycle",
    subjects: [{ id: `${id}-s`, entity: "E" }],
    events: [ev("promote", { verb: "promote" })],
    states: [st("A"), st("B")],
    transitions: [tr("t1", "A", { event: "promote", targets: ["B"] })],
    ...extra,
  };
}

const actors = [
  { id: "X", json: { name: "X", type: "person" } },
  { id: "Y", json: { name: "Y", type: "person" } },
];

/** The last trace entry of running the steps, with every lifecycle known. */
function last(doc: Json, steps: Json[], others: Json[] = [], options: Json = {}) {
  const result = simulate(doc, null, steps, { lifecycles: [...others, doc], subjectEntity: "E", membership: new Membership(actors), ...options } as never);
  return result.trace[result.trace.length - 1];
}
const promote = (actor: string): Json => ({ event: "promote", actor });

describe("one lifecycle: denies, then allows, then the default", () => {
  it("allows when nothing is said", () => {
    expect(last(life("L"), [promote("X")]).accepted).toBe(true);
  });

  it("refuses by a deny that holds, naming the right, and leaves other actors alone", () => {
    const doc = life("L", { rights: [{ id: "r1", ...right("r1", "deny", ["promote"], { actors: ["X"] }) }] });
    const refused = last(doc, [promote("X")]);
    expect([refused.accepted, refused.refusal, refused.refusedBy]).toEqual([false, "right", "r1"]);
    expect(refused.rights).toEqual([{ right: "r1", lifecycle: "L", access: "deny" }]);
    expect(last(doc, [promote("Y")]).accepted).toBe(true);
  });

  it("a deny beats an allow of the same lifecycle", () => {
    const doc = life("L", { rights: [right("allow1", "allow", ["promote"]), right("deny1", "deny", ["promote"])] });
    expect(last(doc, [promote("X")]).refusedBy).toBe("deny1");
  });

  it("under a deny default only an allow lets it through, and the default is named", () => {
    const base = { rightsDefault: "deny" };
    const none = last(life("L", base), [promote("X")]);
    expect([none.refusal, none.refusedBy]).toEqual(["default", "L"]);
    const allowed = life("L", { ...base, rights: [right("a1", "allow", ["promote"], { actors: ["X"] })] });
    expect(last(allowed, [promote("X")]).accepted).toBe(true);
    expect(last(allowed, [promote("Y")]).refusal).toBe("default");
  });

  it("a right holds only in its states", () => {
    const doc = life("L", { rights: [right("r1", "deny", ["promote"], { states: ["B"] })] });
    expect(last(doc, [promote("X")]).accepted).toBe(true);
  });

  it("a right whose condition is false or unknown says nothing", () => {
    const doc = life("L", { rights: [right("r1", "deny", ["promote"], { condition: { text: "subject.secret = 1" } })] });
    expect(last(doc, [promote("X")]).accepted).toBe(true);
  });
});

describe("except never grants", () => {
  it("leaves the listed actor out of a deny, so the default still answers", () => {
    const doc = life("L", { rights: [right("r1", "deny", ["promote"], { except: ["X"] })] });
    expect(last(doc, [promote("X")]).accepted).toBe(true);
    expect(last(doc, [promote("Y")]).refusedBy).toBe("r1");
  });

  it("does not make an allow of an excluded actor under a deny default", () => {
    const doc = life("L", { rightsDefault: "deny", rights: [right("a1", "allow", ["promote"], { except: ["X"] })] });
    expect(last(doc, [promote("X")]).refusal).toBe("default");
    expect(last(doc, [promote("Y")]).accepted).toBe(true);
  });
});

describe("closed lists count as allows that rights only narrow", () => {
  const listed = (extra: Json = {}) => life("L", { events: [ev("promote", { verb: "promote", actors: ["X"] })], ...extra });

  it("an event's list is an allow under a deny default", () => {
    expect(last(listed({ rightsDefault: "deny" }), [promote("X")]).accepted).toBe(true);
  });

  it("refuses an actor off the list by the event, before any right", () => {
    const doc = listed({ rights: [right("r1", "deny", ["promote"], { actors: ["Y"] })] });
    const refused = last(doc, [promote("Y")]);
    expect([refused.refusal, refused.refusedBy]).toEqual(["actor", "promote"]);
  });

  it("a deny narrows the list, and an allow cannot widen it", () => {
    const narrowed = listed({ rights: [right("r1", "deny", ["promote"], { actors: ["X"] })] });
    expect(last(narrowed, [promote("X")]).refusal).toBe("right");
    const widened = listed({ rights: [right("a1", "allow", ["promote"], { actors: ["Y"] })] });
    expect(last(widened, [promote("Y")]).refusal).toBe("actor");
  });

  it("a gate's signers count as an allow for the gated event", () => {
    const gated = life("L", {
      rightsDefault: "deny",
      transitions: [tr("t1", "A", { event: "promote", targets: ["B"], gate: { id: "g1", name: "g1", signers: ["X"], requiredCount: 1 } })],
    });
    expect(last(gated, [{ event: "promote", actor: "X", signer: "x" }]).refusal).not.toBe("default");
    expect(last(gated, [{ event: "promote", actor: "Y", signer: "y" }]).refusal).toBe("default");
  });
});

describe("any no wins across lifecycles", () => {
  const modify = (actor: string): Json => ({ input: "verb", verb: "modify", actor });

  it("a lifecycle that allows by default and says nothing answers yes", () => {
    const quiet = life("Q");
    expect(last(life("L"), [modify("X")], [quiet]).accepted).toBe(true);
  });

  it("another lifecycle's deny refuses an object verb, naming its right", () => {
    const hr = life("HR", { rights: [{ id: "r1", ...right("r1", "deny", ["modify"]) }] });
    const refused = last(life("L"), [modify("X")], [hr]);
    expect([refused.refusal, refused.refusedBy]).toEqual(["right", "r1"]);
  });

  it("each answers on its own: one lifecycle's allow does not beat another's deny default", () => {
    const hr = life("HR", { rightsDefault: "deny" });
    const projects = life("L", { rights: [right("a1", "allow", ["modify"], { actors: ["X"] })] });
    const refused = last(projects, [modify("X")], [hr]);
    expect([refused.refusal, refused.refusedBy]).toEqual(["default", "HR"]);
  });

  it("the acting lifecycle's deny is named before another's", () => {
    const hr = life("AAA", { rights: [right("hr-deny", "deny", ["modify"])] });
    const own = life("L", { rights: [right("own-deny", "deny", ["modify"])] });
    expect(last(own, [modify("X")], [hr]).refusedBy).toBe("own-deny");
  });

  it("an explicit deny is named before a default", () => {
    const hr = life("AAA", { rightsDefault: "deny" });
    const own = life("L", { rights: [right("own-deny", "deny", ["modify"])] });
    expect(last(own, [modify("X")], [hr]).refusedBy).toBe("own-deny");
  });

  it("moves are answered by their own lifecycle only", () => {
    const hr = life("HR", { rightsDefault: "deny" });
    expect(last(life("L"), [promote("X")], [hr]).accepted).toBe(true);
  });
});

describe("the order of the checks", () => {
  it("the actor list, then rights, then the guard", () => {
    const guarded = (extra: Json) =>
      life("L", {
        events: [ev("promote", { verb: "promote", actors: ["X"] })],
        guards: [{ id: "g", name: "g", expression: "false" }],
        transitions: [tr("t1", "A", { event: "promote", targets: ["B"], guard: "g" })],
        ...extra,
      });
    expect(last(guarded({}), [promote("X")]).refusal).toBe("guard");
    expect(last(guarded({}), [promote("X")], [], { refusedBy: true }).refusedBy).toBe("g");
    const denied = guarded({ rights: [right("r1", "deny", ["promote"])] });
    expect(last(denied, [promote("X")]).refusal).toBe("right");
    expect(last(denied, [promote("Y")]).refusal).toBe("actor");
  });

  it("no move leaves refusedBy out", () => {
    const refused = last(life("L", { rightsDefault: "deny", rights: [right("a1", "allow", ["promote"])] }), [promote("X"), promote("X")]);
    expect([refused.refusal, refused.refusedBy]).toEqual(["no-transition", undefined]);
  });

  it("a deny on sign refuses after the signer list", () => {
    const gated = life("L", {
      rights: [right("nosign", "deny", ["sign"], { actors: ["X"] })],
      transitions: [tr("t1", "A", { event: "promote", targets: ["B"], gate: { id: "g1", name: "g1", signers: ["X", "Y"], requiredCount: 1 } })],
    });
    const refused = last(gated, [{ event: "promote", actor: "X", signer: "x" }]);
    expect([refused.refusal, refused.refusedBy]).toEqual(["right", "nosign"]);
    expect(last(gated, [{ event: "promote", actor: "Y", signer: "y" }]).accepted).toBe(true);
  });
});

describe("verb steps", () => {
  const verb = (v: string, extra: Json = {}): Json => ({ input: "verb", verb: v, actor: "X", ...extra });

  it("modify sets the values on the object's row", () => {
    const doc = life("L");
    const trace = last(doc, [verb("modify", { values: { amount: 5 } })], [], { attributesOf: () => [{ id: "amount", name: "amount" }] });
    expect(trace.accepted).toBe(true);
    expect(trace.valuesChanged).toEqual({ amount: 5 });
  });

  it("delete stops the run: later steps are refused with no element", () => {
    const result = simulate(life("L"), null, [verb("delete"), promote("X")], { lifecycles: [], subjectEntity: "E" } as never);
    expect(result.trace[1].accepted).toBe(true);
    expect(result.trace[1].notes?.[0]).toMatch(/deleted/);
    expect([result.trace[2].refusal, result.trace[2].refusedBy]).toEqual(["no-transition", undefined]);
  });

  it("records a verb it did not apply, and refuses the ones with their own steps", () => {
    expect(last(life("L"), [verb("read")]).notes?.[0]).toMatch(/did not apply/);
    const move = last(life("L"), [verb("promote")]);
    expect(move.diagnostics.map((d) => d.rule)).toContain("MQ9305");
  });

  it("is refused by a right, and the refusal names it", () => {
    const doc = life("L", { rights: [{ id: "r1", ...right("r1", "deny", ["delete"]) }] });
    const refused = last(doc, [verb("delete")]);
    expect([refused.refusal, refused.refusedBy]).toEqual(["right", "r1"]);
  });
});

describe("who can do what", () => {
  const world = (lifecycles: Json[]) => ({
    lifecycles,
    named: new Map<string, Json>(),
    membership: new Membership(actors),
    ownerActors: new Set<string>(),
    entitiesOf: (l: Json) => (l.subjects as Json[]).map((s) => String(s.entity)),
  });
  const cellOf = (rows: ReturnType<typeof rightsGrid>, state: string, actor: string | null, verb: string) =>
    rows.find((r) => r.state === state && r.actor === actor && r.verb === verb)!;

  it("answers allowed, denied and the default, with what decides", () => {
    const doc = life("L", {
      rights: [{ id: "r1", ...right("r1", "deny", ["modify"], { actors: ["X"], states: ["B"] }) }, right("r2", "allow", ["read"], { actors: ["Y"] })],
    });
    const rows = rightsGrid(doc, world([doc]));
    expect(cellOf(rows, "B", "X", "modify")).toMatchObject({ answer: "denied", byDefault: false, by: ["r1"] });
    expect(cellOf(rows, "A", "X", "modify")).toMatchObject({ answer: "allowed", byDefault: true, by: ["L"] });
    expect(cellOf(rows, "A", null, "modify")).toMatchObject({ answer: "allowed" });
    expect(cellOf(rows, "A", "Y", "read")).toMatchObject({ answer: "allowed", by: ["r2"] });
  });

  it("a deny default is denied for everyone else, and a closed list lets its actors through", () => {
    const doc = life("L", {
      rightsDefault: "deny",
      events: [ev("promote", { verb: "promote", actors: ["X"] })],
      rights: [right("r2", "allow", ["read"], { actors: ["Y"] })],
    });
    const rows = rightsGrid(doc, world([doc]));
    expect(cellOf(rows, "A", "X", "promote")).toMatchObject({ answer: "allowed", by: ["promote"] });
    expect(cellOf(rows, "A", "Y", "promote")).toMatchObject({ answer: "denied", by: ["promote"] });
    expect(cellOf(rows, "A", null, "read")).toMatchObject({ answer: "denied", byDefault: true });
  });

  it("depends on a condition, and on another lifecycle that could say no in some states", () => {
    const own = life("L", { rights: [right("r1", "deny", ["modify"], { actors: ["X"], condition: { text: "subject.a = 1" } })] });
    const hr = life("HR", { rights: [right("hr1", "deny", ["read"], { states: ["B"] })] });
    const rows = rightsGrid(own, world([own, hr]));
    expect(cellOf(rows, "A", "X", "modify")).toMatchObject({ answer: "depends" });
    expect(cellOf(rows, "A", "X", "read")).toBeUndefined();
    const read = life("L2", { rights: [right("r3", "allow", ["read"])] });
    expect(cellOf(rightsGrid(read, world([read, hr])), "A", null, "read")).toMatchObject({ answer: "depends", dependsOn: ["HR"] });
  });

  it("another lifecycle that says no in every state denies the cell", () => {
    const hr = life("HR", { rightsDefault: "deny" });
    const read = life("L2", { rights: [right("r3", "allow", ["read"], { actors: ["X"] })] });
    expect(cellOf(rightsGrid(read, world([read, hr])), "A", "X", "read")).toMatchObject({ answer: "denied", by: ["HR"] });
  });
});

describe("the mock's endpoints", () => {
  const mock = useMockApi();

  it("answers get_rights for the seeded lifecycle, with no entity rows, and pages", async () => {
    const answer = await (await fetch(mock.url(`/api/processes/${INVOICE_LIFECYCLE}/rights?limit=3`))).json();
    expect(answer.lifecycle).toBe(INVOICE_LIFECYCLE);
    expect(answer.rows).toHaveLength(3);
    expect(answer.next).toBe("3");
    const all = await (await fetch(mock.url(`/api/processes/${INVOICE_LIFECYCLE}/rights?limit=1000`))).json();
    expect(all.next).toBeNull();
    expect(all.rows.some((r: { entity?: string }) => r.entity !== undefined)).toBe(false);
    expect(all.rows.some((r: { by: string[] }) => r.by.includes(PAID_IS_READ_ONLY))).toBe(true);
    const only = await (await fetch(mock.url(`/api/processes/${INVOICE_LIFECYCLE}/rights?verbs=delete`))).json();
    expect(only.rows.every((r: { verb: string }) => r.verb === "delete")).toBe(true);
  });

  it("simulates a verb step and names the right that refuses it", async () => {
    const doc = mock.backend.model.get(INVOICE_LIFECYCLE)!.json as Json;
    const named = (doc.rights as Json[]).find((r) => r.right === PAID_IS_READ_ONLY)!;
    const states = ((named.states as string[]) ?? [])[0];
    expect(states).toBeDefined();
    const result = await (
      await fetch(mock.url(`/api/processes/${INVOICE_LIFECYCLE}/simulate`), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ steps: [{ input: "verb", verb: "read" }] }),
      })
    ).json();
    expect(result.trace.at(-1).accepted).toBe(true);
    expect(result.trace.at(-1).input.verb).toBe("read");
  });
});
