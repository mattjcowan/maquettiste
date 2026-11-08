// The mock's trigger points in a run (trigger-points-design.md decisions 18, 24 to 28; section 8): rows 8 to 12 in selection and in verb
// steps, other lifecycles' points for object verbs, `assume` words, create values, and the four behaviours the owner asked to keep exact.
import { describe, expect, it } from "vitest";
import { replay, simulate } from "@/mocks/model/stepper";
import { resolveCheckNames, sequenceOf } from "@/mocks/model/triggerRun";

type Json = Record<string, unknown>;

const st = (id: string, extra: Json = {}): Json => ({ id, name: id, ...extra });
const tr = (id: string, source: string, extra: Json = {}): Json => ({ id, source, ...extra });
const ev = (name: string, extra: Json = {}): Json => ({ id: name, name, ...extra });
const path = (id: string, old = false): Json => ({ path: [id], ...(old ? { old: true } : {}) });
const gt = (a: Json, n: number): Json => ({ op: "gt", args: [a, { value: n }] });
const ATTRIBUTES = ["total", "number", "secret", "note", "flag"];
const options = { attributesOf: () => ATTRIBUTES.map((id) => ({ id, name: id })), subjectEntity: "E" };
const lifecycle = (parts: Json): Json => ({
  kind: "lifecycle-test",
  id: "P",
  name: "P",
  use: "lifecycle",
  subjects: [{ id: "s", entity: "E" }],
  states: [st("A"), st("B"), st("C")],
  ...parts,
});
const point = (id: string, extra: Json): Json => ({ id, name: id, ...extra });
const verbStep = (verb: string, extra: Json = {}): Json => ({ input: "verb", verb, ...extra });
const runs = (t: { triggerPoints?: { triggerPoint: string; outcome: string }[] }) => (t.triggerPoints ?? []).map((r) => `${r.triggerPoint}:${r.outcome}`);

describe("rows 8 to 12 in a verb step", () => {
  const doc = lifecycle({
    triggerPoints: [
      point("number", { type: "action", timing: "before", verbs: ["create"], sets: [{ id: "x1", attribute: "number", value: { value: "N-1" } }] }),
      point("seen", { type: "check", verbs: ["create"], condition: { op: "isNotNull", args: [path("number")] } }),
      point("stamp", { type: "action", timing: "with-change", verbs: ["modify"], sets: [{ id: "x2", attribute: "note", value: { value: "changed" } }] }),
      point("portal", { type: "action", timing: "after-commit", verbs: ["modify"], outside: true }),
      point("positive", { type: "check", verbs: ["modify"], attributes: ["total"], condition: gt(path("total"), 0), description: "Must be positive." }),
      point("drop", { type: "check", severity: "warning", verbs: ["modify"], attributes: ["total"], condition: gt(path("total"), 0) }),
    ],
  });

  it("runs before actions first, so a check reads what they filled, and writes the values of an accepted create (decision 27)", () => {
    const result = simulate(doc, null, [verbStep("create", { values: { total: 5 } })], options);
    const step = result.trace[1];
    expect(step.accepted).toBe(true);
    expect(runs(step)).toEqual(["number:ran", "seen:passed"]);
    expect(step.triggerPoints![0].sets).toEqual({ number: "N-1" });
    expect(step.valuesChanged).toEqual({ number: "N-1", total: 5 });
  });

  it("refuses a modify whose error check fails, names the check, and writes nothing", () => {
    const result = simulate(doc, null, [verbStep("modify", { values: { total: -1 } })], options);
    const step = result.trace[1];
    expect(step.accepted).toBe(false);
    expect([step.refusal, step.refusedBy]).toEqual(["check", "positive"]);
    expect(runs(step)).toEqual(["positive:refused"]);
    expect(step.valuesChanged).toBeUndefined();
    expect(step.checkWarnings).toBeUndefined();
  });

  it("lets a warning through, lists it in checkWarnings, runs with-change sets, and queues after-commit last", () => {
    const warnDoc = lifecycle({
      triggerPoints: [
        ...(doc.triggerPoints as Json[]).slice(2, 4),
        point("warn", { type: "check", severity: "warning", verbs: ["modify"], condition: gt(path("total"), 10) }),
      ],
    });
    const step = simulate(warnDoc, null, [verbStep("modify", { values: { total: 5 } })], options).trace[1];
    expect(step.accepted).toBe(true);
    expect(step.checkWarnings).toEqual(["warn"]);
    expect(runs(step)).toEqual(["warn:warned", "stamp:ran", "portal:queued"]);
    expect(step.triggerPoints![2]).toMatchObject({ outside: true, type: "action", timing: "after-commit" });
    expect(step.valuesChanged).toEqual({ total: 5, note: "changed" });
  });

  it("reads old. as the values before the verb", () => {
    const drop = lifecycle({
      triggerPoints: [
        point("drop", {
          type: "check",
          verbs: ["modify"],
          condition: { op: "ge", args: [path("total"), { op: "coalesce", args: [path("total", true), { value: 0 }] }] },
        }),
      ],
    });
    const result = simulate(drop, null, [verbStep("modify", { values: { total: 10 } }), verbStep("modify", { values: { total: 4 } })], options);
    expect(result.trace.slice(1).map((t) => t.refusal)).toEqual([null, "check"]);
  });

  it("answers a team-code check from assume, with the assumed flag, and passes without an entry", () => {
    const team = lifecycle({ triggerPoints: [point("credit", { type: "check", verbs: ["create"], outside: true })] });
    const ran = simulate(
      team,
      null,
      [verbStep("create"), verbStep("create", { assume: { credit: "error" } }), verbStep("create", { assume: { credit: "warning" } })],
      options,
    );
    expect(ran.trace.slice(1).map((t) => t.refusal)).toEqual([null, "check", null]);
    expect(ran.trace[1].triggerPoints![0]).toMatchObject({ outcome: "passed", assumed: true, outside: true });
    expect(ran.trace[3].checkWarnings).toEqual(["credit"]);
    expect(ran.trace[3].triggerPoints![0]).toMatchObject({ outcome: "warned", assumed: true });
  });

  it("a check narrowed by attributes runs when the step names none, an action does not (decision 10)", () => {
    const narrowed = lifecycle({
      triggerPoints: [
        point("c", { type: "check", verbs: ["read"], attributes: ["secret"] }),
        point("a", { type: "action", timing: "with-change", verbs: ["read"], attributes: ["secret"] }),
      ],
    });
    expect(runs(simulate(narrowed, null, [verbStep("read")], options).trace[1])).toEqual(["c:passed"]);
    expect(runs(simulate(narrowed, null, [verbStep("read", { attributes: ["secret"] })], options).trace[1])).toEqual(["c:passed", "a:ran"]);
  });
});

describe("the behaviours the owner asked to keep exact", () => {
  it("an event that names delete still does its move; a replace stands in only for a verb step, and delete's other points still run", () => {
    const doc = lifecycle({
      events: [ev("remove", { verb: "delete" })],
      transitions: [tr("t", "A", { event: "remove", targets: ["B"] })],
      triggerPoints: [
        point("archive", { type: "replace", verbs: ["delete"], sets: [{ id: "x", attribute: "note", value: { value: "archived" } }] }),
        point("audit", { type: "check", verbs: ["delete"], outside: true }),
      ],
    });
    const event = simulate(doc, null, [{ event: "remove" }], options).trace[1];
    expect(event.configuration).toEqual(["B"]);
    expect(runs(event)).toEqual(["audit:passed"]);
    expect(event.valuesChanged).toBeUndefined();
    // As a verb step the replace takes the place of the delete: the object is not deleted, so a later step still runs.
    const result = simulate(doc, null, [verbStep("delete"), verbStep("modify", { values: { total: 1 } })], options);
    expect(runs(result.trace[1])).toEqual(["audit:passed", "archive:replaced"]);
    expect(result.trace[1].valuesChanged).toEqual({ note: "archived" });
    expect(result.trace[2].refusal).toBeNull();
    // Without the replace a delete stops the run.
    const plain = simulate(lifecycle({ triggerPoints: [] }), null, [verbStep("delete"), verbStep("modify")], options);
    expect(plain.trace[2].refusal).toBe("no-transition");
  });

  it("a check that refuses an automatic move leaves it untaken; the step that led to it stays accepted and the trace names the check", () => {
    const doc = lifecycle({
      events: [ev("go")],
      transitions: [
        tr("first", "A", { event: "go", targets: ["B"], verb: "promote" }),
        tr("auto", "B", { trigger: "always", targets: ["C"], verb: "promote" }),
      ],
      triggerPoints: [point("hold", { type: "check", verbs: ["promote"], transitions: ["auto"], description: "Not yet." })],
    });
    const asked = simulate(doc, null, [{ event: "go", assume: { hold: "error" } }, { event: "go" }], options);
    const step = asked.trace[1];
    expect(step.accepted).toBe(true);
    expect(step.configuration).toEqual(["B"]);
    // The step stays accepted, so the refusing check is named in a note and not listed as a refused run (section A, item 1).
    expect(runs(step)).toEqual([]);
    expect(step.notes?.filter((n) => n.includes("automatic move"))).toHaveLength(1);
    expect(step.notes?.join(" ")).toContain("Check 'hold' refused an automatic move");
    // Passing, the move is taken.
    expect(asked.trace[2].refusal).toBe("no-transition");
    expect(simulate(doc, null, [{ event: "go" }], options).trace[1].configuration).toEqual(["C"]);
  });

  it("a check that refuses an event's only move refuses the step, as a false guard does, and outranks it", () => {
    const doc = lifecycle({
      events: [ev("go")],
      transitions: [tr("t", "A", { event: "go", targets: ["B"], verb: "promote" })],
      triggerPoints: [point("hold", { type: "check", verbs: ["promote"] })],
    });
    const step = simulate(doc, null, [{ event: "go", assume: { hold: "error" } }], options).trace[1];
    expect([step.accepted, step.refusal, step.refusedBy]).toEqual([false, "check", "hold"]);
    expect(step.configuration).toEqual(["A"]);
  });

  it("what a point sets is part of the verb: rights and freezing look at the actor's own change, and values a before action fills never make a point match", () => {
    const doc = lifecycle({
      rightsDefault: "allow",
      rights: [{ id: "r", name: "SecretIsHidden", access: "deny", verbs: ["modify"], attributes: ["secret"] }],
      triggerPoints: [
        point("fill", {
          type: "action",
          timing: "before",
          verbs: ["modify"],
          attributes: ["total"],
          sets: [{ id: "x", attribute: "secret", value: { value: "s" } }],
        }),
        point("onSecret", { type: "check", verbs: ["modify"], attributes: ["secret"], description: "Never matches." }),
        point("onFreeze", { type: "action", timing: "with-change", verbs: ["freeze"], sets: [{ id: "y", attribute: "note", value: { value: "frozen" } }] }),
      ],
    });
    const modify = simulate(doc, null, [verbStep("modify", { values: { total: 1 } })], options).trace[1];
    // The right denies `secret`, but the actor changed `total`: the point's set is not checked.
    expect(modify.accepted).toBe(true);
    expect(modify.valuesChanged).toEqual({ secret: "s", total: 1 });
    expect(runs(modify)).toEqual(["fill:ran"]);
    // Changing `secret` itself is refused by the right.
    expect(simulate(doc, null, [verbStep("modify", { values: { secret: "x" } })], options).trace[1].refusal).toBe("right");
    // A with-change set on a freeze step still applies; a note says the object is frozen.
    const freeze = simulate(doc, null, [verbStep("freeze")], options).trace[1];
    expect(freeze.accepted).toBe(true);
    expect(freeze.valuesChanged).toEqual({ note: "frozen" });
    expect(freeze.notes?.join(" ")).toContain("freezing does not stop what a trigger point sets");
  });

  it("a check that refuses the signature completing a gate leaves it uncounted, audited as refused", () => {
    const gate = { id: "g", name: "g", required: 2, signers: ["Finance"], meanings: [{ id: "m", name: "approved" }] };
    const doc = lifecycle({
      events: [ev("approve", { actors: [] })],
      transitions: [tr("t", "A", { event: "approve", targets: ["B"], verb: "promote", gate })],
      triggerPoints: [point("ready", { type: "check", verbs: ["promote"], description: "Not ready." })],
    });
    const sign = (signer: string, assume?: Json) => ({ event: "approve", actor: "Finance", signer, ...(assume ? { assume } : {}) });
    const result = simulate(doc, null, [sign("ann"), sign("bob", { ready: "error" }), sign("bob")], options);
    // The first signature does not complete the gate: the move's check does not run, and it is counted.
    expect(result.trace[1].accepted).toBe(true);
    expect(result.trace[1].audit.map((a) => a.outcome)).toEqual(["signed"]);
    expect(runs(result.trace[1])).toEqual([]);
    // The second would complete it, and the check refuses: audited as refused, uncounted.
    expect([result.trace[2].refusal, result.trace[2].refusedBy]).toEqual(["check", "ready"]);
    expect(result.trace[2].audit.map((a) => a.outcome)).toEqual(["refused"]);
    expect(result.trace[2].configuration).toEqual(["A"]);
    // Tried again with the check passing, the same signer completes it: the earlier attempt was never counted.
    expect(result.trace[3].configuration).toEqual(["B"]);
    expect(result.trace[3].audit.map((a) => a.outcome)).toEqual(["signed", "completed"]);
  });
});

describe("other lifecycles' points, and the trace members", () => {
  const acting = lifecycle({ id: "P", name: "Zeta", triggerPoints: [point("own", { type: "check", verbs: ["modify"] })] });
  const other = lifecycle({ id: "Q", name: "Alpha", triggerPoints: [point("theirs", { type: "check", verbs: ["modify"], description: "Held by HR." })] });
  const quiet = lifecycle({
    id: "R",
    name: "Quiet",
    subjects: [{ id: "s", entity: "Other" }],
    triggerPoints: [point("never", { type: "check", verbs: ["modify"] })],
  });

  it("runs the other governing lifecycles' points for an object verb, the acting one first, and a refusal there names the point", () => {
    const step = simulate(acting, null, [verbStep("modify", { assume: { theirs: "error" } })], { ...options, lifecycles: [acting, other, quiet] }).trace[1];
    // A step refused by a check lists the refusing check once (decision 28).
    expect(runs(step)).toEqual(["theirs:refused"]);
    expect(step.triggerPoints![0].lifecycle).toBe("Q");
    const warned = simulate(acting, null, [verbStep("modify", { assume: { theirs: "warning" } })], { ...options, lifecycles: [acting, other] }).trace[1];
    expect(runs(warned)).toEqual(["own:passed", "theirs:warned"]);
    expect([step.refusal, step.refusedBy]).toEqual(["check", "theirs"]);
    // A lifecycle that does not govern the subject is not asked.
    expect(runs(simulate(acting, null, [verbStep("modify")], { ...options, lifecycles: [acting, other, quiet] }).trace[1])).not.toContain("never:passed");
  });

  it("keys a word by id only, resolves Lifecycle.Name in a request's input, and lists the team-code checks an enabled trigger reaches in checksUnknown", () => {
    const withLifecycles = { ...options, lifecycles: [acting, other] };
    const byId = simulate(acting, null, [verbStep("modify", { assume: { theirs: "warning" } })], withLifecycles).trace[1];
    expect(byId.checkWarnings).toEqual(["theirs"]);
    // A name is no key in a file (item 8): the check passes, as with no word.
    const byName = simulate(acting, null, [verbStep("modify", { assume: { "Alpha.theirs": "warning" } })], withLifecycles).trace[1];
    expect(byName.checkWarnings).toBeUndefined();
    // A request's names turn into ids first, as the engine's ResolveNames does.
    const resolved = resolveCheckNames(verbStep("modify", { assume: { "Alpha.theirs": "warning", own: "pass", flag: true } }), acting, [acting, other]);
    expect(resolved.assume).toEqual({ theirs: "warning", own: "pass", flag: true });
    const doc = lifecycle({
      events: [ev("go")],
      transitions: [tr("t", "A", { event: "go", targets: ["B"], verb: "promote" })],
      triggerPoints: [point("hold", { type: "check", verbs: ["promote"] }), point("cond", { type: "check", verbs: ["promote"], condition: { value: true } })],
    });
    expect(simulate(doc, null, [], options).enabled[0].checksUnknown).toEqual(["hold"]);
  });

  it("records refusal, refusedBy and checkWarnings, and triggerPoints only when asked", () => {
    const doc = lifecycle({
      triggerPoints: [point("hold", { type: "check", verbs: ["create"] }), point("note", { type: "check", severity: "warning", verbs: ["create"] })],
    });
    const steps = [verbStep("create", { assume: { hold: "error" } }), verbStep("create", { assume: { note: "warning" } })];
    const plain = replay(doc, null, steps, options).expects;
    expect(plain[0]).toMatchObject({ accepted: false, refusal: "check", refusedBy: "hold" });
    expect(plain[1]).toMatchObject({ checkWarnings: ["note"] });
    expect(plain[1].triggerPoints).toBeUndefined();
    const asked = replay(doc, null, steps, { ...options, recordTriggerPoints: true }).expects;
    expect(asked[1].triggerPoints).toEqual(["hold", "note"]);
  });

  it("leaves a project without trigger points as it was", () => {
    const step = simulate(lifecycle({}), null, [verbStep("modify", { values: { total: 1 } })], options).trace[1];
    expect(step.triggerPoints).toBeUndefined();
    expect(step.checkWarnings).toBeUndefined();
  });
});

describe("the mock's get_trigger_sequence", () => {
  const doc = lifecycle({
    triggerPoints: [
      point("c", { type: "check", severity: "warning", verbs: ["delete"] }),
      point("r", { type: "replace", verbs: ["delete"] }),
      point("w", { type: "action", timing: "with-change", verbs: ["delete"], states: ["B"] }),
      point("other", { type: "check", verbs: ["modify"] }),
    ],
  });
  const chain = (e: string) => [e];

  it("lists the built-in steps and this lifecycle's points in the order of decision 18", () => {
    const steps = sequenceOf(doc, { verb: "delete" }, chain);
    expect(steps.map((s) => `${s.order}:${s.step}${s.triggerPoint ? `:${s.triggerPoint}` : ""}`)).toEqual([
      "1:frozen",
      "9:checks:c",
      "10:effect:r",
      "11:with-change:w",
    ]);
    expect(steps[1]).toMatchObject({ type: "check", severity: "warning", lifecycle: "P" });
  });

  it("narrows by state and ends with a built-in effect and with-change step when no point does", () => {
    const steps = sequenceOf(doc, { verb: "delete", state: "A" }, chain);
    expect(steps.map((s) => s.step)).toEqual(["frozen", "checks", "effect"]);
    expect(steps.some((s) => s.triggerPoint === "w")).toBe(false);
    expect(steps.find((s) => s.step === "effect")!.triggerPoint).toBe("r");
    // The engine has no built-in with-change row (finding 12); an own verb has no built-in effect.
    expect(sequenceOf(doc, { verb: "read" }, chain).map((s) => s.step)).toEqual(["effect"]);
    expect(sequenceOf(doc, { verb: "archive" }, chain)).toEqual([]);
  });
});

describe("the mock matches the engine (review section A and findings 11, 12)", () => {
  it("item 2: an invoke-done pick keeps the first check over a guard, whatever the order", () => {
    const doc = lifecycle({
      guards: [{ id: "g1", name: "g1" }],
      states: [st("A", { invoke: [{ id: "inv", name: "inv", type: "service" }] }), st("B"), st("C")],
      transitions: [
        tr("viaCheck", "A", { trigger: "invoke-done", invoke: "inv", targets: ["B"], verb: "promote" }),
        tr("viaGuard", "A", { trigger: "invoke-done", invoke: "inv", targets: ["C"], guard: "g1" }),
      ],
      triggerPoints: [point("hold", { type: "check", verbs: ["promote"] })],
    });
    const step = simulate(doc, null, [{ input: "invoke-done", invoke: "inv", assume: { hold: "error", g1: false } }], options).trace[1];
    expect([step.refusal, step.refusedBy]).toEqual(["check", "hold"]);
  });

  it("item 3: an event that names delete runs its with-change sets; a verb delete with no replace skips them with one note", () => {
    const doc = lifecycle({
      events: [ev("remove", { verb: "delete" })],
      transitions: [tr("t", "A", { event: "remove", targets: ["B"] })],
      triggerPoints: [
        point("archive", { type: "replace", verbs: ["delete"], states: ["C"] }),
        point("stamp", { type: "action", timing: "with-change", verbs: ["delete"], sets: [{ id: "x", attribute: "note", value: { value: "gone" } }] }),
      ],
    });
    const event = simulate(doc, null, [{ event: "remove" }], options).trace[1];
    expect(runs(event)).toEqual(["stamp:ran"]);
    expect(event.valuesChanged).toEqual({ note: "gone" });
    const open = lifecycle({ ...doc, triggerPoints: [point("archive", { type: "replace", verbs: ["delete"] })] });
    expect(simulate(open, null, [{ event: "remove" }], options).trace[1].notes?.join(" ")).toContain("replace 'archive' does not run");
    const verb = simulate(doc, null, [verbStep("delete")], options).trace[1];
    expect(runs(verb)).toEqual(["stamp:skipped"]);
    expect(verb.notes?.filter((n) => n.includes("sets of trigger point 'stamp' were skipped"))).toHaveLength(1);
  });

  it("item 4: a recording writes triggerPoints only when some ran, and a refresh keeps asking where a step already expects them", () => {
    const doc = lifecycle({
      triggerPoints: [point("c", { type: "check", verbs: ["read"] })],
    });
    const steps = [verbStep("read"), verbStep("modify", { values: { total: 1 } })];
    const asked = replay(doc, null, steps, { ...options, recordTriggerPoints: true }).expects;
    expect(asked[0].triggerPoints).toEqual(["c"]);
    expect("triggerPoints" in asked[1]).toBe(false);
    const kept = replay(
      doc,
      null,
      [
        { ...steps[0], expect: { triggerPoints: ["c"] } },
        { ...steps[1], expect: { triggerPoints: ["c"] } },
      ],
      options,
    ).expects;
    expect(kept[0].triggerPoints).toEqual(["c"]);
    expect("triggerPoints" in kept[1]).toBe(false);
    expect("triggerPoints" in replay(doc, null, steps, options).expects[0]).toBe(false);
  });

  it("item 5: a create with values is applied, so no 'did not apply' note", () => {
    const doc = lifecycle({ triggerPoints: [] });
    const step = simulate(doc, null, [verbStep("create", { values: { total: 1 } })], options).trace[1];
    expect(step.notes?.join(" ") ?? "").not.toContain("did not apply");
    expect(simulate(doc, null, [verbStep("create")], options).trace[1].notes?.join(" ")).toContain("did not apply");
  });

  it("item 6: a move's states place matches its source, the active states inside it and its ancestors, never another region", () => {
    const doc = lifecycle({
      events: [ev("go", { verb: "promote" })],
      states: [
        st("P", {
          type: "parallel",
          states: [
            st("R1", { type: "compound", states: [st("A"), st("B")], initial: "A" }),
            st("R2", { type: "compound", states: [st("X"), st("Y")], initial: "X" }),
          ],
        }),
      ],
      transitions: [tr("t", "A", { event: "go", targets: ["B"] }), tr("sw", "X", { event: "flip", targets: ["Y"] })],
      triggerPoints: [
        point("inSource", { type: "check", verbs: ["promote"], states: ["A"] }),
        point("inX", { type: "check", verbs: ["promote"], states: ["X"] }),
        point("inY", { type: "check", verbs: ["promote"], states: ["Y"] }),
        point("inR1", { type: "check", verbs: ["promote"], states: ["R1"] }),
      ],
    });
    const step = simulate({ ...doc, events: [ev("go", { verb: "promote" }), ev("flip")] }, null, [{ event: "flip" }, { event: "go" }], options).trace[2];
    expect(runs(step)).toEqual(["inSource:passed", "inR1:passed"]);
  });

  it("item 6: a transition on a compound source matches a state inside it that is active", () => {
    const doc = lifecycle({
      events: [ev("go", { verb: "promote" })],
      states: [st("C", { type: "compound", states: [st("S1"), st("S2")], initial: "S1" }), st("D")],
      transitions: [tr("t", "C", { event: "go", targets: ["D"] })],
      triggerPoints: [point("inner", { type: "check", verbs: ["promote"], states: ["S1"] })],
    });
    expect(runs(simulate(doc, null, [{ event: "go" }], options).trace[1])).toEqual(["inner:passed"]);
  });

  it("item 7: checksUnknown lists sign's checks before the move's", () => {
    const gate = { id: "g", name: "g", required: 1, signers: ["Finance"], meanings: [{ id: "m", name: "approved" }] };
    const doc = lifecycle({
      events: [ev("approve")],
      transitions: [tr("t", "A", { event: "approve", targets: ["B"], verb: "promote", gate })],
      triggerPoints: [point("move", { type: "check", verbs: ["promote"] }), point("signs", { type: "check", verbs: ["sign"] })],
    });
    expect(simulate(doc, null, [], options).enabled[0].checksUnknown).toEqual(["signs", "move"]);
  });

  it("finding 11: a point that reads the event's payload runs on a move only, never on a verb step", () => {
    const doc = lifecycle({
      events: [ev("archive", { verb: "archive", payload: [{ id: "reason", name: "reason" }] })],
      transitions: [tr("t", "A", { event: "archive", targets: ["B"] })],
      triggerPoints: [point("why", { type: "check", verbs: ["archive"], condition: { op: "isNotNull", args: [path("reason")] } })],
    });
    expect(runs(simulate(doc, null, [verbStep("archive")], options).trace[1])).toEqual([]);
    expect(runs(simulate(doc, null, [{ event: "archive", payload: { reason: "x" } }], options).trace[1])).toEqual(["why:passed"]);
  });

  it("finding 12: the sequence lists closed lists, no-move, guards and signatures as the engine does, and drops a replace once a transition is given", () => {
    const gate = { id: "g", name: "g", required: 1, signers: ["Finance"], meanings: [{ id: "m", name: "approved" }] };
    const doc = lifecycle({
      events: [ev("approve")],
      transitions: [tr("t", "A", { event: "approve", targets: ["B"], verb: "promote", gate })],
      triggerPoints: [point("r", { type: "replace", verbs: ["delete"] })],
    });
    const chain = (e: string) => [e];
    expect(sequenceOf(doc, { verb: "promote" }, chain).map((s) => s.step)).toEqual(["closed-lists", "no-move", "guards", "signatures", "effect"]);
    expect(sequenceOf(doc, { verb: "delete", transition: "t" }, chain).map((s) => s.step)).toEqual([
      "frozen",
      "closed-lists",
      "no-move",
      "guards",
      "signatures",
      "effect",
    ]);
    expect(sequenceOf(doc, { verb: "sign" }, chain).map((s) => s.step)).toEqual(["closed-lists", "no-move", "guards", "signatures", "effect"]);
  });
});
