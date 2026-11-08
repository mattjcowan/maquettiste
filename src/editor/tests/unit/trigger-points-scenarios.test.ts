// Trigger points in the editor's second part: the get_trigger_sequence handler of the mock, What happens when's rows, the scenario cells'
// model functions (assume words, reachable checks, expectations) and the simulation panel's lines for a run.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { INVOICE_LIFECYCLE } from "@/mocks/model/processSeed";
import { simulate } from "@/mocks/model/stepper";
import { defaultRowValue, enabledRows, inputOf, lastStepLines, namesOf } from "@/editors/process/simulation/model";
import type { SimulationResult } from "@/editors/process/simulation/api";
import { stepRows, type ProcessDoc, type ScenarioDoc, type StepDoc } from "@/model/process";
import { parseAssume, ranPoints, reachableChecks, sequenceRows } from "@/model/triggerPoints";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const st = (id: string): Json => ({ id, name: id });
const doc = (extra: Json = {}): ProcessDoc =>
  ({
    kind: "process",
    id: "P",
    name: "P",
    use: "lifecycle",
    subjects: [{ id: "s", entity: "E" }],
    states: [st("A"), st("B")],
    events: [{ id: "go", name: "go", verb: "promote" }],
    transitions: [{ id: "t", source: "A", event: "go", targets: ["B"], verb: "promote" }],
    triggerPoints: [
      { id: "hold", name: "hold", type: "check", verbs: ["promote"], description: "Not ready yet." },
      { id: "credit", name: "credit", type: "check", verbs: ["create"], outside: true },
      { id: "cond", name: "cond", type: "check", verbs: ["promote"], condition: { text: "true" } },
    ],
    ...extra,
  }) as unknown as ProcessDoc;

describe("the scenario cells' model functions", () => {
  it("reachableChecks lists the team-code checks a step can reach, by the verb it does", () => {
    const process = doc();
    expect(reachableChecks(process, { id: "1", event: "go" } as StepDoc).map((t) => t.id)).toEqual(["hold"]);
    expect(reachableChecks(process, { id: "2", input: "verb", verb: "create" } as StepDoc).map((t) => t.id)).toEqual(["credit"]);
    expect(reachableChecks(process, { id: "3", input: "verb", verb: "modify" } as StepDoc)).toEqual([]);
  });

  it("parseAssume keeps a boolean for a guard and a word for a check, and drops a word that is not one", () => {
    const checks = new Set(["hold"]);
    expect(parseAssume({ g: "true", h: false, hold: "Warning" }, checks)).toEqual({ g: true, h: false, hold: "warning" });
    expect(parseAssume({ hold: "maybe", g: true }, checks)).toEqual({ g: true });
  });

  it("ranPoints keeps the ids in order, repeats included, and leaves skipped ones out", () => {
    expect(
      ranPoints([
        { triggerPoint: "a", outcome: "ran" },
        { triggerPoint: "b", outcome: "skipped" },
        { triggerPoint: "a", outcome: "passed" },
      ]),
    ).toEqual(["a", "a"]);
    expect(ranPoints(undefined)).toEqual([]);
  });

  it("stepRows names the assume words, the expected check warnings and the expected trigger points", () => {
    const scenario = {
      kind: "scenario",
      id: "S",
      name: "S",
      process: "P",
      steps: [
        {
          id: "1",
          event: "go",
          assume: { hold: "warning" },
          expect: { checkWarnings: ["hold"], triggerPoints: ["hold", "cond"], refusal: "check", refusedBy: "hold" },
        },
      ],
    } as unknown as ScenarioDoc;
    const [row] = stepRows(scenario, doc(), new Map());
    expect(row.assume).toBe("hold=warning");
    expect([row.checkWarnings, row.triggerPoints, row.refusal, row.refusedBy]).toEqual(["hold", "hold, cond", "check", "hold"]);
  });
});

describe("What happens when", () => {
  const mock = useMockApi();

  it("answers a lifecycle's own points in the order of the fixed rows over the web route", async () => {
    const backend = new MockBackend();
    const process = backend.model.get(INVOICE_LIFECYCLE)!.json as unknown as ProcessDoc;
    const res = await fetch(mock.url(`/api/processes/${INVOICE_LIFECYCLE}/trigger-sequence?verb=create`));
    expect(res.status).toBe(200);
    const body = (await res.json()) as { lifecycle: string; verb: string; steps: { order: number; step: string; triggerPoint?: string }[] };
    expect(body.lifecycle).toBe(INVOICE_LIFECYCLE);
    const named = (process.triggerPoints ?? []).filter((t) => t.verbs.includes("create")).map((t) => t.id);
    expect(body.steps.filter((s) => s.triggerPoint).map((s) => s.triggerPoint)).toEqual([
      ...named.filter((id) => process.triggerPoints!.find((t) => t.id === id)!.timing === "before"),
      ...named.filter((id) => process.triggerPoints!.find((t) => t.id === id)!.type === "check"),
    ]);
    expect(body.steps.map((s) => s.order)).toEqual([...body.steps.map((s) => s.order)].sort((a, b) => a - b));
    expect((await fetch(mock.url(`/api/processes/${INVOICE_LIFECYCLE}/trigger-sequence`))).status).toBe(400);
    expect((await fetch(mock.url(`/api/processes/NOPE/trigger-sequence?verb=create`))).status).toBe(404);
  });

  it("turns the answer into rows as given, naming another lifecycle's point with its lifecycle", () => {
    const rows = sequenceRows(
      {
        lifecycle: "P",
        steps: [
          { order: 1, step: "frozen" },
          { order: 9, step: "checks", triggerPoint: "mine", lifecycle: "P", type: "check", severity: "warning", states: ["A"], when: true },
          { order: 9, step: "checks", triggerPoint: "theirs", lifecycle: "Q", type: "check", severity: "error" },
          { order: 12, step: "after-commit", triggerPoint: "late", lifecycle: "P", type: "action", timing: "after-commit", outside: true },
        ],
      },
      { point: (id) => id.toUpperCase(), lifecycle: (id) => `Lifecycle ${id}`, where: (_k, ids) => ids.join(",") },
    );
    expect(rows.map((r) => [r.order, r.point?.name ?? null, r.kind, r.other])).toEqual([
      [1, null, "", null],
      [9, "MINE", "Check (warning)", null],
      [9, "THEIRS", "Check (error)", "Lifecycle Q"],
      [12, "LATE", "Action, after commit", null],
    ]);
    expect(rows[1]).toMatchObject({ where: "in states A", conditional: true });
    expect(rows[3].outside).toBe(true);
  });
});

describe("the simulation panel's lines and rows", () => {
  const process = doc();
  const result = (steps: object[], options: Json = {}) =>
    ({ processHash: "h", ...simulate(process as never, null, steps as never, { subjectEntity: "E", ...options }) }) as unknown as SimulationResult;
  const names = namesOf(process as never, new Map());

  it("lists the trigger points that ran with their outcome, check warnings, and the message of the refusing check with its link", () => {
    const warned = result([{ event: "go", assume: { hold: "warning" } }]);
    const lines = lastStepLines(warned.trace[1], names);
    const point = lines.find((l) => l.kind === "trigger" && l.badge);
    expect(point).toMatchObject({ element: { id: "hold" }, badge: { text: "warned", tone: "warning" }, flags: [{ text: "assumed" }] });
    expect(lines.some((l) => l.kind === "trigger" && l.tone === "warning" && l.text.startsWith("Check warnings"))).toBe(true);
    const refused = result([{ event: "go", assume: { hold: "error" } }]);
    const line = lastStepLines(refused.trace[1], names).find((l) => l.kind === "refusal")!;
    expect(line.element).toMatchObject({ id: "hold" });
    expect(line.text).toContain("refused by");
  });

  it("says an automatic move left out by a check, and offers Pass, Warning or Error for each reachable check", () => {
    const auto = doc({
      transitions: [
        { id: "t", source: "A", event: "go", targets: ["B"], verb: "promote" },
        { id: "auto", source: "B", trigger: "always", targets: ["A"], verb: "demote" },
      ],
      triggerPoints: [{ id: "back", name: "back", type: "check", verbs: ["demote"] }],
    });
    const run = {
      processHash: "h",
      ...simulate(auto as never, null, [{ event: "go", assume: { back: "error" } }] as never, { subjectEntity: "E" }),
    } as unknown as SimulationResult;
    const lines = lastStepLines(run.trace[1], namesOf(auto as never, new Map()));
    // The refused check is a note, not a listed run (the step stays accepted).
    expect(lines.some((l) => l.kind === "note" && l.text.includes("refused an automatic move"))).toBe(true);
    expect(lines.some((l) => l.kind === "trigger" && l.badge?.text === "refused")).toBe(false);
    // The enabled event reaches `hold` of the first doc; its row offers a word, defaulting to Pass, and the input carries it.
    const open = result([]);
    const [row] = enabledRows(open, process, names, []);
    expect(row.checks).toEqual([{ id: "hold", name: "hold" }]);
    const value = defaultRowValue(row, open);
    expect(inputOf(row, { ...value, assume: { hold: "error" } }).assume).toEqual({ hold: "error" });
    expect(inputOf(row, value).assume).toEqual({ hold: "pass" });
  });
});
