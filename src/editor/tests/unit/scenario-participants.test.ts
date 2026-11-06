// The editor's side of scenario participants (actors-design.md section 9): the holdings text and rows of the Participants
// grid, a step's Participant column, the simulation panel's session, rows and trace, the explorer's Participants list under
// a scenario, and simulate and record through the mock carrying participants.
import { describe, expect, it } from "vitest";
import { clone } from "@/lib/json";
import { MockBackend } from "@/mocks/backend";
import { simulate } from "@/mocks/model/stepper";
import { APPROVER, BUDGET_HOLDER, BUDGET_REJECTED, PURCHASE_APPROVAL } from "@/mocks/model/processSeed";
import { PARTICIPANT_LABELS } from "@/model/labels";
import {
  forgetParticipant,
  holdable,
  holdsText,
  identityOf,
  newParticipantName,
  parseHolds,
  participantRows,
  standsFor,
  type ActorChoice,
} from "@/model/participants";
import { stepRows, type ProcessDoc, type ScenarioDoc } from "@/model/process";
import { buildForest, childKeys, documentChildren, isExpandable, needsDocument, nodeOf } from "@/explorer/tree";
import { castOf, loadScenario, loadScenarioStart, newSession, requestOf, setStartValue } from "@/editors/process/simulation/session";
import { defaultRowValue, enabledRows, inputLabel, inputOf, lastStepLines, namesOf, traceRows } from "@/editors/process/simulation/model";
import type { SimulationResult } from "@/editors/process/simulation/api";
import { useMockApi } from "./harness";

const backend = new MockBackend();
const process = () => clone(backend.model.get(PURCHASE_APPROVAL)!.json) as unknown as ProcessDoc;
const scenario = () => clone(backend.model.get(BUDGET_REJECTED)!.json) as unknown as ScenarioDoc;

const REVIEWER = "01JQ0C00000000000000000001";
const ANN = "01JQ0C00000000000000000002";
const AUTO = "01JQ0C00000000000000000003";
const OBJ = "01JQ0C00000000000000000004";
const P1 = "01JQ0C00000000000000000005";
const P2 = "01JQ0C00000000000000000006";

const choices: ActorChoice[] = [
  { id: REVIEWER, name: "Reviewer", type: "role" },
  { id: ANN, name: "Ann", type: "person" },
  { id: AUTO, name: "Scheduler", type: "system" },
  { id: "x", name: "Owner", type: "role", relative: true },
];
const actorNames = new Map(choices.map((c) => [c.id, c.name]));
const objects = [{ id: OBJ, name: "invoice", entity: "E" }];
const withPeople = (): ScenarioDoc => ({
  ...scenario(),
  objects,
  subject: OBJ,
  participants: [
    {
      id: P1,
      name: "ann",
      displayName: "Ann A",
      identity: 12,
      actor: ANN,
      holds: [
        { id: "h1", actor: REVIEWER, within: OBJ },
        { id: "h2", actor: REVIEWER },
      ],
      object: OBJ,
    },
    { id: P2, name: "bot", actor: AUTO },
  ],
});

describe("the Participants grid's model", () => {
  it("offers a person, external system or Automatic to stand for, and a role, group or organization to hold, never one from the object", () => {
    expect(standsFor(choices).map((c) => c.name)).toEqual(["Ann", "Scheduler"]);
    expect(holdable(choices).map((c) => c.name)).toEqual(["Reviewer"]);
  });

  it("writes a holding as the role, then 'within' and the object, and reads it back keeping the ids it can", () => {
    const doc = withPeople();
    const text = holdsText(doc.participants![0].holds, actorNames, new Map(objects.map((o) => [o.id, o.name])));
    expect(text).toBe("Reviewer within invoice, Reviewer");
    let n = 0;
    const parsed = parseHolds("reviewer, Reviewer in Invoice", holdable(choices), objects, doc.participants![0].holds, () => `new${++n}`);
    expect(parsed.error).toBeNull();
    // The plain holding keeps h2 and the one within the object keeps h1, in the text's order.
    expect(parsed.holds).toEqual([
      { id: "h2", actor: REVIEWER },
      { id: "h1", actor: REVIEWER, within: OBJ },
    ]);
    expect(parseHolds("Ann", holdable(choices), objects, undefined, () => "n").error).toBe(PARTICIPANT_LABELS.holdsUnknown("Ann"));
    expect(parseHolds("Reviewer in nothing", holdable(choices), objects, undefined, () => "n").error).toContain("nothing");
    expect(parseHolds("", holdable(choices), objects, undefined, () => "n")).toEqual({ holds: [], error: null });
  });

  it("makes one row per participant with names for ids, and reads an identity as an integer when it is one", () => {
    const rows = participantRows(withPeople(), actorNames);
    expect(rows[0]).toMatchObject({ name: "ann", displayName: "Ann A", identity: "12", actor: "Ann", object: "invoice", pointer: "/participants/0" });
    expect(rows[1]).toMatchObject({ actor: "Scheduler", holds: "" });
    expect(identityOf(" 42 ")).toBe(42);
    expect(identityOf("a@b")).toBe("a@b");
    expect(identityOf("")).toBeUndefined();
  });

  it("names the next participant and forgets a removed one in steps and owners", () => {
    const doc = withPeople();
    expect(newParticipantName(doc)).toBe("participant");
    expect(newParticipantName({ participants: [{ id: "a", name: "participant" }] })).toBe("participant2");
    doc.steps[0].participant = P1;
    doc.objects![0].owner = P1;
    forgetParticipant(doc, P1);
    expect(doc.participants!.map((p) => p.id)).toEqual([P2]);
    expect(doc.steps[0].participant).toBeUndefined();
    expect(doc.objects![0].owner).toBeUndefined();
    forgetParticipant(doc, P2);
    expect(doc.participants).toBeUndefined();
  });

  it("shows a step's participant by name in the steps grid", () => {
    const doc = withPeople();
    doc.steps[0].participant = P2;
    expect(stepRows(doc, process(), actorNames)[0].participant).toBe("bot");
    expect(stepRows(doc, process(), actorNames)[1].participant).toBe("");
  });
});

describe("the simulation session with participants", () => {
  it("carries them from a scenario into the start and the request, and keeps them through a start edit", () => {
    const doc = withPeople();
    const loaded = loadScenario(newSession(PURCHASE_APPROVAL), doc);
    expect(loaded.start.participants).toEqual(doc.participants);
    expect(requestOf(loaded)).toMatchObject({ participants: doc.participants, objects, subject: OBJ });
    expect(loadScenarioStart(newSession(PURCHASE_APPROVAL), doc).start.participants).toEqual(doc.participants);
    expect(setStartValue(loaded, "01JQATT0000000000000000201", 1).start.participants).toEqual(doc.participants);
    // A scenario with none adds nothing.
    expect(requestOf(loadScenario(newSession(PURCHASE_APPROVAL), scenario()))).not.toHaveProperty("participants");
    expect(castOf({})).toEqual({});
  });
});

describe("the simulation panel's rows with participants", () => {
  const names = namesOf(process(), actorNames, withPeople().participants);
  const result = (steps: object[]) => ({ processHash: "h", ...simulate(process() as never, null, steps as never) }) as unknown as SimulationResult;

  it("names a participant in an input's label and raises an input that names one", () => {
    expect(inputLabel({ event: "e", participant: P1 }, names)).toContain("by ann");
    const answer = result([]);
    const row = enabledRows(answer, process(), names, [{ id: BUDGET_HOLDER, name: "BudgetHolder" }])[0];
    const base = defaultRowValue(row, answer);
    expect(base.participant).toBe("");
    expect(inputOf(row, base)).not.toHaveProperty("participant");
    const input = inputOf(row, { ...base, participant: P1, actor: "" });
    expect(input).toMatchObject({ participant: P1 });
    expect(input).not.toHaveProperty("actor");
  });

  it("shows the actor a participant's step acted as, in the last step and the trace", () => {
    const entry = {
      index: 0,
      input: { event: "e", participant: P1 },
      accepted: true,
      refusal: null,
      microsteps: [],
      guards: [],
      audit: [],
      changed: {},
      diagnostics: [],
      incomplete: false,
      actsAs: ANN,
    };
    const lines = lastStepLines(entry as never, names);
    expect(lines.map((l) => l.text)).toContain("ann acted as Ann");
    const none = lastStepLines({ ...entry, accepted: false, refusal: "actor", actsAs: undefined } as never, names);
    expect(none.find((l) => l.text.includes("satisfies none"))?.tone).toBe("warning");
    const traced = traceRows(
      [{ event: "e", participant: P1 } as never],
      { trace: [{ index: 0, configuration: [], accepted: true, refusal: null, actsAs: ANN }] } as never,
      names,
    );
    expect(traced[1].label).toBe("e by ann, acting as Ann");
    // Without a participant the line and label are the old ones.
    expect(lastStepLines({ ...entry, input: { event: "e" }, actsAs: undefined } as never, names).map((l) => l.text)).not.toContain("ann acted as Ann");
  });
});

describe("the explorer's participants under a scenario", () => {
  it("makes a scenario row expandable and lists its participants once its document is read", () => {
    // The index row carries the participant count, only above zero.
    const rows = backend.model.index().map((r) => (r.id === BUDGET_REJECTED ? { ...r, participants: 2 } : r));
    const f = buildForest({ rows });
    const key = f.place.get(BUDGET_REJECTED)!;
    const node = nodeOf(f, key)!;
    expect(node.kind).toBe("scenario");
    expect(isExpandable(f, key)).toBe(true);
    expect(needsDocument(f, key)).toBe(true);
    const doc = { ...backend.model.get(BUDGET_REJECTED)!, json: withPeople() } as never;
    documentChildren(f, key, doc);
    expect(childKeys(f, key).map((k) => nodeOf(f, k)!.label)).toEqual(["Participants"]);
    const items = childKeys(f, `${key}/participants`).map((k) => nodeOf(f, k)!);
    expect(items.map((i) => [i.label, i.kind, i.pointer])).toEqual([
      ["Ann A", "participant", "/participants/0"],
      ["bot", "participant", "/participants/1"],
    ]);
  });

  it("has nothing to open for a scenario without participants", () => {
    const f = buildForest({ rows: backend.model.index() });
    const key = f.place.get(BUDGET_REJECTED)!;
    documentChildren(f, key, backend.model.get(BUDGET_REJECTED)!);
    expect(childKeys(f, key)).toEqual([]);
    expect(isExpandable(f, key)).toBe(false);
  });
});

describe("simulate and record carry participants", () => {
  const mock = useMockApi();
  const post = (path: string, body: unknown) =>
    fetch(mock.url(path), { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  const people = [{ id: P1, name: "ann", identity: "ann@x", actor: BUDGET_HOLDER }];
  const submit = { event: "01JQPRX0000000000000000014" };
  const start = { context: { "01JQATT0000000000000000201": 25000 } };

  it("simulates a step by a participant, echoing the participant and answering the actor it acted as", async () => {
    const res = await post(`/api/processes/${PURCHASE_APPROVAL}/simulate`, { start, participants: people, steps: [{ ...submit, participant: P1 }] });
    expect(res.status).toBe(200);
    const body = (await res.json()) as { trace: { input: { participant?: string } | null; actsAs?: string | null; accepted: boolean }[] };
    expect(body.trace[1]).toMatchObject({ accepted: true, actsAs: BUDGET_HOLDER, input: { participant: P1 } });
  });

  it("refuses participants that are not a list of named, unique ids", async () => {
    for (const participants of [[{ name: "x" }], [{ id: P1 }], [people[0], people[0]], "nope"]) {
      expect((await post(`/api/processes/${PURCHASE_APPROVAL}/simulate`, { participants, steps: [] })).status).toBe(400);
      expect((await post(`/api/processes/${PURCHASE_APPROVAL}/scenarios`, { name: "Bad", participants, steps: [] })).status).toBe(400);
    }
  });

  it("records participants with new ids, and each step keeps its participant under the new id", async () => {
    const holds = [{ id: "01JQ0C000000000000000000H1", actor: APPROVER }];
    const request = { name: "WithPeople", start, participants: [{ ...people[0], holds }], steps: [{ ...submit, participant: P1 }] };
    const res = await post(`/api/processes/${PURCHASE_APPROVAL}/scenarios`, request);
    expect(res.status).toBe(201);
    const { element } = (await res.json()) as {
      element: { participants: { id: string; actor: string; holds: { id: string }[] }[]; steps: { participant: string }[] };
    };
    const [one] = element.participants;
    expect(one.id).not.toBe(P1);
    expect(one.actor).toBe(BUDGET_HOLDER);
    expect(one.holds[0].id).not.toBe(holds[0].id);
    expect(element.steps[0].participant).toBe(one.id);
    // The scenario simulates with its own participants when the request names none.
    const again = await post(`/api/processes/${PURCHASE_APPROVAL}/simulate`, { scenario: "WithPeople" });
    expect(again.status).toBe(200);
    expect(((await again.json()) as { trace: { actsAs?: string | null }[] }).trace[1].actsAs).toBe(BUDGET_HOLDER);
  });

  it("reports MQ9301 in verify for a participant's step refused while it expects to be accepted, naming the participant", async () => {
    const nobody = { id: P2, name: "nobody" };
    const record = await post(`/api/processes/${PURCHASE_APPROVAL}/scenarios`, {
      name: "Nobody",
      start,
      participants: [people[0]],
      steps: [{ ...submit, participant: P1 }],
    });
    expect(record.status).toBe(201);
    const saved = ((await record.json()) as { id: string; element: Record<string, unknown> }).element;
    // Swap the participant for one that stands for no actor: the recorded expectation (accepted) no longer holds.
    mock.backend.model.externalEdit(
      String(saved.id),
      (json) => void Object.assign(json, { participants: [{ ...nobody, id: (saved.participants as { id: string }[])[0].id }] }),
    );
    const verified = await post(`/api/processes/${PURCHASE_APPROVAL}/verify`, { scenarios: ["Nobody"] });
    const body = (await verified.json()) as { results: { passed: boolean; failure: { rule: string; message: string } | null }[] };
    expect(body.results[0].passed).toBe(false);
    expect(body.results[0].failure).toMatchObject({ rule: "MQ9301" });
    expect(body.results[0].failure!.message).toContain("participant 'nobody'");
  });
});
