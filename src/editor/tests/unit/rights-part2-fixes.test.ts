// Fixes for rights part 2 that follow the engine: the first deny in file order is named when several attributes are denied;
// whileFrozen with ends asks about the step's own link; attributes never stand beside ends (MQ9903); an allow that names
// attributes does not count for MQ9905; a thaw's own move carries no frozen note; Add right takes attributes and While frozen.
import { describe, expect, it } from "vitest";
import { simulate } from "@/mocks/model/stepper";
import { rightDiagnostics } from "@/mocks/model/rightRules";
import { Membership } from "@/mocks/model/membership";
import { addRight, editRight, hasAttributes, hasWhileFrozen } from "@/model/rights";
import { namesOf, lastStepLines, traceRows } from "@/editors/process/simulation/model";
import { recordVerify, resetScenarioStatuses, scenarioStatuses, verifyLines } from "@/explorer/processApi";
import { scenarioStatusText } from "@/explorer/tree";
import { scenarioRows } from "@/model/process";

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
    events: [ev("go"), ev("unlock", { verb: "thaw" })],
    actions: [{ id: "bump", name: "bump", sets: [{ id: "s1", attribute: "salary", value: { value: 7 } }] }],
    states: [st("A"), st("B")],
    transitions: [
      { id: "t1", source: "A", event: "go", targets: ["B"] },
      { id: "t2", source: "A", event: "unlock", targets: ["A"], actions: ["bump"] },
    ],
    ...extra,
  };
}

const cast = (frozen = false): Json[] => [
  {
    id: "o1",
    name: "o1",
    entity: "E",
    ...(frozen ? { frozen: true } : {}),
    links: [
      { end: "parts", object: "bolt", frozen: true },
      { end: "parts", object: "nut" },
    ],
  },
  { id: "bolt", name: "bolt", entity: "E" },
  { id: "nut", name: "nut", entity: "E" },
];

function last(doc: Json, steps: Json[], objects: Json[] = cast()) {
  return simulate(doc, null, steps, {
    lifecycles: [doc],
    subjectEntity: "E",
    membership: new Membership([{ id: "X", json: { name: "X", type: "person" } }]),
    attributesOf: () => [
      { id: "name", name: "name" },
      { id: "salary", name: "salary" },
    ],
    objects,
    subject: "o1",
  } as never)
    .trace.filter((t) => t.index >= 0)
    .at(-1)!;
}

describe("several attribute denies name the first in file order", () => {
  const doc = life({
    rights: [right("dSalary", "deny", ["modify"], { attributes: ["salary"] }), right("dName", "deny", ["modify"], { attributes: ["name"] })],
  });

  it("whichever attribute the step lists first", () => {
    expect(last(doc, [{ input: "verb", verb: "modify", values: { name: "a", salary: 2 } }]).refusedBy).toBe("dSalary");
    expect(last(doc, [{ input: "verb", verb: "modify", values: { salary: 2, name: "a" } }]).refusedBy).toBe("dSalary");
  });
});

describe("whileFrozen with ends asks about the step's own link", () => {
  const doc = life({ rights: [right("d1", "deny", ["read"], { ends: ["parts"], whileFrozen: true })] });
  it("refuses the frozen link, accepts the other", () => {
    expect(last(doc, [{ input: "verb", verb: "read", end: "parts", object: "bolt" }]).refusedBy).toBe("d1");
    expect(last(doc, [{ input: "verb", verb: "read", end: "parts", object: "nut" }]).accepted).toBe(true);
  });
});

describe("rule findings", () => {
  const found = (json: Json) => {
    const out: string[] = [];
    rightDiagnostics(
      json,
      () => [],
      (rule) => void out.push(rule),
    );
    return out;
  };
  it("MQ9903: attributes beside ends", () => {
    expect(found(life({ rights: [right("r", "deny", ["read"], { attributes: ["salary"], ends: ["parts"] })] }))).toContain("MQ9903");
  });
  it("MQ9905: an allow that names attributes does not let the event through", () => {
    const base = { rightsDefault: "deny", events: [ev("edit", { verb: "modify" })], transitions: [] };
    expect(found(life({ ...base, rights: [right("a", "allow", ["modify"], { attributes: ["salary"] })] }))).toContain("MQ9905");
    expect(found(life({ ...base, rights: [right("a", "allow", ["modify"])] }))).not.toContain("MQ9905");
  });
});

describe("a thaw's own move", () => {
  it("carries no frozen note, while another move on a frozen object does", () => {
    expect(last(life(), [{ event: "unlock" }], cast(true)).notes).toBeUndefined();
    const doc = life({ events: [ev("go"), ev("bumpit")], transitions: [{ id: "t1", source: "A", event: "bumpit", targets: ["A"], actions: ["bump"] }] });
    expect(last(doc, [{ event: "bumpit" }], cast(true)).notes?.some((n) => /frozen/.test(n))).toBe(true);
  });
});

describe("the rights editor", () => {
  const process = (right: Json) => ({ id: "L", rights: [right] }) as never;
  it("refuses attributes beside ends, and ends beside attributes", () => {
    const withEnds = process({ id: "r", name: "r", access: "deny", verbs: ["read"], ends: ["parts"] });
    expect(editRight(withEnds, "r", "attributes", "salary", { actors: [] })).toBe(false);
    const withAttributes = process({ id: "r", name: "r", access: "deny", verbs: ["read"], attributes: ["salary"] });
    expect(editRight(withAttributes, "r", "ends", "parts", { actors: [] })).toBe(false);
  });
  it("shows Attributes and While frozen only when a right uses them", () => {
    expect(hasAttributes([{ attributeIds: [] }])).toBe(false);
    expect(hasAttributes([{ attributeIds: ["a"] }])).toBe(true);
    expect(hasWhileFrozen([{ whileFrozen: false }])).toBe(false);
    expect(hasWhileFrozen([{ whileFrozen: true }])).toBe(true);
  });
  it("Add right takes attributes (read and modify only) and While frozen", () => {
    const p = { id: "L" } as never;
    addRight(p, { name: "a", access: "deny", verbs: ["read"], attributes: ["salary"], whileFrozen: true });
    addRight(p, { name: "b", access: "deny", verbs: ["delete"], attributes: ["salary"] });
    const [a, b] = (p as { rights: Json[] }).rights;
    expect([a.attributes, a.whileFrozen, b.attributes, b.whileFrozen]).toEqual([["salary"], true, undefined, undefined]);
  });
});

describe("verify warnings", () => {
  const warning = {
    rule: "MQ9309",
    severity: "warning" as const,
    message: "Step 2 is refused as frozen because step 1 froze it.",
    elementId: "S",
    filePath: null,
    jsonPointer: "/steps/1/expect",
    line: null,
    column: null,
  };
  it("a passing scenario with a warning shows as passed with a warning, in the explorer and the output", () => {
    resetScenarioStatuses();
    const result = { process: "L", passed: true, results: [{ scenario: "S", name: "s", passed: true, steps: 2, failure: null, warnings: [warning] }] };
    recordVerify(result as never);
    const status = scenarioStatuses().get("S");
    expect(scenarioStatusText(status)).toBe("passed, 1 warning");
    expect(scenarioRows([{ id: "S", name: "s" }], scenarioStatuses())[0]).toMatchObject({ status: "passed", warnings: 1 });
    expect(verifyLines("L", result as never)).toContain(`    warning: ${warning.message}`);
    resetScenarioStatuses();
  });
});

describe("the simulation panel", () => {
  const doc = life({ events: [ev("pay", { verb: "freeze" })], transitions: [{ id: "t1", source: "A", event: "pay", targets: ["B"] }] }) as never;
  const names = namesOf(doc, new Map());
  it("tags the row of an event that freezes", () => {
    const rows = traceRows([{ event: "pay" } as never], { trace: [{ index: 0, accepted: true, configuration: [] }] } as never, names);
    expect(rows[1].froze).toBe("froze");
  });
  it("says why a read with no attribute is refused by a right that names one", () => {
    const entry = {
      index: 0,
      accepted: false,
      refusal: "right",
      refusedBy: "R",
      input: { input: "verb", verb: "read" },
      microsteps: [],
      guards: [],
      diagnostics: [],
      audit: [],
      changed: {},
    } as never;
    const lines = lastStepLines(
      entry,
      names,
      (id) => id,
      () => ["total"],
    );
    expect(lines.some((l) => /unknown attributes are refused because the right names attribute total/.test(l.text))).toBe(true);
  });
});
