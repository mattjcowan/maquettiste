// Trigger points part 2 in the mock, after the review and the browser pass: named check names in `assume`, which named points a verb
// reaches (a move only inside its lifecycle), the order's id tie-break, stereotype subjects in a run with no object, the covers route and
// the lifecycle sequence's subjects. The engine is the reference.
import { describe, expect, it } from "vitest";
import { BILLING, CUSTOMER_NUMBER, INVOICE } from "@/mocks/model/processSeed";
import { simulate } from "@/mocks/model/stepper";
import { namedFor, resolveCheckNames, sequenceOf } from "@/mocks/model/triggerRun";
import { usageText, verbUsage, verbUsers } from "@/model/verbList";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const named = (extra: Json): Json => ({ kind: "trigger-point", id: "n1", name: "N1", type: "check", verbs: ["create"], subjects: [{ entity: "E" }], ...extra });

describe("resolveCheckNames with named checks", () => {
  const own: Json = { id: "P", name: "P", use: "lifecycle", guards: [{ id: "g", name: "Taken" }], triggerPoints: [{ id: "own", name: "Own", type: "check" }] };
  const qualified = (p: Json) => (p.package ? `${String(p.package)}.${String(p.name)}` : String(p.name));
  const points = [
    named({ id: "a", name: "Stamp", package: "Sales" }),
    named({ id: "b", name: "Root" }),
    named({ id: "c", name: "Own" }),
    named({ id: "d", name: "Taken" }),
  ];
  const resolve = (assume: Json) => resolveCheckNames({ assume }, own, [own], points, qualified).assume as Json;

  it("maps Package.Name, and the bare name in the root package, to the id", () => {
    expect(resolve({ "Sales.Stamp": "error", Root: "warning" })).toEqual({ a: "error", b: "warning" });
  });

  it("leaves a name that also matches a lifecycle check or a guard unchanged", () => {
    expect(resolve({ Own: "pass", Taken: "pass" })).toEqual({ own: "pass", Taken: "pass" });
  });
});

describe("named points on moves, in a lifecycle only", () => {
  const states = [
    { id: "A", name: "A" },
    { id: "B", name: "B" },
  ];
  const base = { kind: "process", id: "P", name: "P", subjects: [{ id: "s", entity: "E" }], states, events: [{ id: "go", name: "go", verb: "promote" }] };
  const doc = (use: string): Json => ({
    ...base,
    use,
    transitions: [{ id: "t", source: "A", targets: ["B"], event: "go" }],
    ...(use === "lifecycle" ? { initial: "A" } : {}),
  });
  const point = named({ id: "m1", name: "M1", type: "check", verbs: ["promote"], condition: { text: "false" } });
  const options = { attributesOf: () => [], subjectEntity: "E", namedPoints: [point] };
  const ran = (d: Json, extra: Json = {}) => {
    const trace = simulate(d, null, [{ input: "event", event: "go", ...extra }], options).trace.at(-1)!;
    return (trace.triggerPoints ?? []).map((r) => r.triggerPoint);
  };

  it("runs a named check on a move inside the lifecycle that owns it", () => {
    expect(ran(doc("lifecycle"))).toEqual(["m1"]);
  });

  it("never runs it in an orchestration", () => {
    expect(ran(doc("orchestration"))).toEqual([]);
  });

  it("lets an assume word name a named check covering the object's entity, whatever the verb, and refuses one that does not cover", () => {
    const check = named({ id: "c1", name: "C1", verbs: ["create"] });
    const lifecycle = doc("lifecycle");
    const word = (key: string) =>
      simulate(lifecycle, null, [{ input: "event", event: "go", assume: { [key]: "pass" } }], {
        ...options,
        namedPoints: [check, named({ id: "c2", name: "C2", subjects: [{ entity: "Z" }] })],
      })
        .trace.at(-1)!
        .diagnostics.filter((d) => d.rule === "MQ9305").length;
    expect(word("c1")).toBe(0);
    expect(word("c2")).toBe(1);
  });
});

describe("the order of named points and the subjects of a run with no object", () => {
  it("breaks a tie of name by id", () => {
    const list = [
      { id: "2", name: "Stamp", subjects: [{ entity: "E" }] },
      { id: "1", name: "Stamp", subjects: [{ entity: "E" }] },
    ];
    expect(namedFor(list, [["E"]], () => false).map((t) => t.id)).toEqual(["1", "2"]);
  });

  it("runs a point on a stereotype subject for the carriers when a run has no object", () => {
    const lifecycle: Json = {
      kind: "process",
      id: "P",
      name: "P",
      use: "lifecycle",
      subjects: [{ id: "s", stereotype: "audited" }],
      states: [{ id: "A", name: "A" }],
    };
    const point = named({ id: "s1", subjects: [{ stereotype: "audited" }] });
    const options = {
      attributesOf: () => [],
      namedPoints: [point],
      stereotypeCovers: (key: string, e: string) => key === "audited" && e === "E",
      stereotypeCarriers: (key: string) => (key === "audited" ? ["E"] : []),
    };
    const trace = simulate(lifecycle, null, [{ input: "verb", verb: "create" }], options).trace.at(-1)!;
    expect((trace.triggerPoints ?? []).map((r) => r.triggerPoint)).toEqual(["s1"]);
  });

  it("lists a lifecycle's sequence against the process's subjects, never every governed entity", () => {
    const lifecycle: Json = { kind: "process", id: "P", name: "P", use: "lifecycle", subjects: [{ id: "s", entity: "Party" }] };
    const except = [named({ id: "x", verbs: ["delete"], subjects: [{ entity: "Party" }], exceptEntities: ["Customer"] })];
    const chain = (e: string) => (e === "Customer" ? ["Customer", "Party"] : [e]);
    // The subjects are [Party]: the point covers it. The derived Customer is no subject, so it does not drop the point.
    expect(sequenceOf(lifecycle, { verb: "delete" }, chain, except, () => false, ["Party"]).some((s) => s.triggerPoint === "x")).toBe(true);
  });
});

describe("the covers route", () => {
  const mock = useMockApi();
  it("answers the entities a named point runs for, in name order, and 404 for anything else", async () => {
    const res = await fetch(mock.url(`/api/model/trigger-points/${CUSTOMER_NUMBER}/covers`));
    expect(res.status).toBe(200);
    const body = (await res.json()) as { entities: string[] };
    expect(body.entities.length).toBeGreaterThan(0);
    expect((await fetch(mock.url(`/api/model/trigger-points/${INVOICE}/covers`))).status).toBe(404);
  });

  it("counts the stereotypes a package gives its entities by default", async () => {
    const id = "01JQTGP0000000000000000099";
    const pkg = mock.backend.model.get(BILLING)!;
    const done = mock.backend.model.save(BILLING, { ...pkg.json, defaultStereotypes: ["audited"] }, pkg.hash);
    expect(done.outcome).toBe("saved");
    expect(
      mock.backend.model.create({
        kind: "trigger-point",
        id,
        name: "ByStereotype",
        package: BILLING,
        type: "check",
        verbs: ["create"],
        subjects: [{ stereotype: "audited" }],
      }).outcome,
    ).toBe("saved");
    const body = (await (await fetch(mock.url(`/api/model/trigger-points/${id}/covers`))).json()) as { entities: string[] };
    expect(body.entities).toContain(INVOICE);
  });
});

describe("the Verbs tab counts named points", () => {
  const rows = [
    { id: "p", kind: "process", name: "P", verbs: ["create"] },
    { id: "n", kind: "trigger-point", name: "N", verbs: ["create"] },
    { id: "o", kind: "operation", name: "O", verb: "create" },
  ] as never;
  it("counts them among a verb's uses and lists them first", () => {
    expect(usageText(verbUsage(rows).get("create"))).toBe("1 named trigger point, 1 process, 1 operation");
    expect(verbUsers(rows, "create").map((r) => r.id)).toEqual(["n", "p", "o"]);
  });
});
