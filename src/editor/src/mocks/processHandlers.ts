// The mock's process operations (phase-3-design.md 4.4): simulate, record, verify, export, import and sync-enum. The
// engine recordings of the processes fixture answer simulate and export while the mock model is pristine and the
// request is the recorded one (PurchaseApproval keeps the fixture's ids, processSeed.ts); any other simulate, and the
// expectations a record fills in, come from the mock's reduced interpreter (model/stepper.ts). Verify passes every
// scenario but those processSeed.ts lists as failing.
import { http as rawHttp, HttpResponse, type HttpHandler } from "msw";
import { newId } from "@/lib/ids";
import { bindableAttributes } from "@/model/binding";
import { subjectsOf } from "@/model/process";
import { sha256Hex } from "@/lib/sha256";
import type { MockBackend } from "./backend";
import { mentions, replayable, type Recording } from "./recorded";

const kebab = (name: string) => name.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
import { MOCK_FAILURES } from "./model/processSeed";
import { normalizeInput, PARTICIPANT_TEXTS, readAt, replay, signatureCounts, simulate, unsatisfiedLists } from "./model/stepper";
import { enumDrift, schemaDiagnostics } from "./model/validate";
import { syncCodesOf } from "./model/syncCodes";
import { report } from "./model/store";
import { Membership } from "./model/membership";
import { rightsGrid } from "./model/rightsCheck";
import { Coverage } from "./model/coverage";
import { namedCovered } from "./model/triggerRules";
import { entitySequenceOf, resolveCheckNames, sequenceOf } from "./model/triggerRun";
import { entityChain } from "@/model/objectFields";
import { builtInVerbs } from "@/model/verbs";

type Json = Record<string, unknown>;
type StateDoc = { id: string; name: string; type?: string; initial?: string; states?: StateDoc[] };
const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

export interface ProcessHandlerKit {
  baseUrl: string;
  recorded: ReadonlyMap<string, Recording>;
  pristine: () => { pristine: boolean };
  answer: (r: Recording) => Response;
  problem: (status: number, code: never, title: string, detail?: string) => Response;
}

/** The active atomic states on entry to a state list (initial child, every region of a parallel state). */
export function initialLeaves(states: StateDoc[] | undefined, initial?: string): string[] {
  const list = (states ?? []).filter((s) => s.type !== "history");
  const first = list.find((s) => s.id === initial) ?? list[0];
  if (!first) return [];
  if (first.type === "parallel") return (first.states ?? []).flatMap((region) => enter(region));
  return enter(first);
}

function enter(state: StateDoc): string[] {
  if (state.type === "parallel") return (state.states ?? []).flatMap((r) => enter(r));
  if (state.states?.length) return initialLeaves(state.states, state.initial);
  return [state.id];
}

/** A minimal statechart config of the kind the engine exports: nested states and their event transitions. */
export function exportConfig(doc: Json): Json {
  const events = new Map(arr(doc.events).map((e) => [String(e.id), String(e.name)]));
  const names = new Map<string, string>();
  const walk = (list: unknown) => arr(list).forEach((s) => (names.set(String(s.id), String(s.name)), walk(s.states)));
  walk(doc.states);
  const on = new Map<string, Json>();
  for (const t of arr(doc.transitions)) {
    if (t.trigger && t.trigger !== "event") continue;
    const entry = on.get(String(t.source)) ?? {};
    entry[events.get(String(t.event)) ?? String(t.event)] = arr(t.targets).map((x) => `#${names.get(String(x)) ?? String(x)}`)[0] ?? {};
    on.set(String(t.source), entry);
  }
  const node = (s: Json): Json => {
    const out: Json = { id: s.name };
    if (s.type === "parallel") out.type = "parallel";
    if (s.type === "final") out.type = "final";
    const children = arr(s.states);
    if (children.length) {
      if (s.type !== "parallel") out.initial = names.get(String(s.initial)) ?? children[0].name;
      out.states = Object.fromEntries(children.map((c) => [String(c.name), node(c)]));
    }
    const transitions = on.get(String(s.id));
    if (transitions) out.on = transitions;
    return out;
  };
  const root = arr(doc.states);
  return {
    id: doc.name,
    initial: names.get(String(doc.initial)) ?? root[0]?.name,
    states: Object.fromEntries(root.map((s) => [String(s.name), node(s)])),
  };
}

/**
 * The objects a run carries (lifecycle-design.md decision 22): the request's, else the scenario's. Every object needs an
 * id (none are minted for a run), ids are unique, `subject` is an object id (never a name), and each link names a known
 * object. Recording mints a fresh id for every object and remaps `subject` and the links' `object`.
 */
/** The step that froze what step `at` was refused for: the last accepted freeze before it that no accepted thaw followed; null when none. */
export function freezingStep(
  doc: Json,
  trace: { index: number; accepted: boolean; input: { verb?: string; event?: string | null } | null }[],
  at: number,
): number | null {
  const verbOf = (t: (typeof trace)[number]): string | null =>
    t.input?.verb ?? (t.input?.event ? ((arr(doc.events).find((e) => e.id === t.input?.event)?.verb as string | undefined) ?? null) : null);
  for (let i = at - 1; i >= 0; i--) {
    const t = trace.find((x) => x.index === i);
    if (!t?.accepted) continue;
    const verb = verbOf(t);
    if (verb === "thaw") return null;
    if (verb === "freeze") return i;
  }
  return null;
}

function castOf(value: Json, scenario: Json | undefined, mint = false): { objects: Json[]; subject: string } | { error: string } | null {
  const own = value.objects !== undefined && value.objects !== null;
  if (own && !Array.isArray(value.objects)) return { error: "objects must be an array." };
  const list = own ? arr(value.objects) : arr(scenario?.objects);
  if (!list.length) return null;
  const ids = new Set<string>();
  const names = new Set<string>();
  for (const o of list) {
    if (typeof o.id !== "string" || !o.id) return { error: "each object needs an id." };
    if (ids.has(o.id)) return { error: `the object id ${o.id} is used twice.` };
    ids.add(o.id);
    if (typeof o.name !== "string" || !o.name || typeof o.entity !== "string" || !o.entity) return { error: "each object needs a name and an entity." };
    if (names.has(o.name)) return { error: `the scenario has two objects named ${o.name}.` };
    names.add(o.name);
  }
  for (const o of list)
    for (const l of arr(o.links))
      if (typeof l.object !== "string" || !ids.has(l.object)) return { error: `a link of ${String(o.name)} names an unknown object.` };
  const wanted = own ? value.subject : scenario?.subject;
  if (typeof wanted !== "string" || !wanted) return { error: "subject is required with objects." };
  if (!ids.has(wanted)) return { error: `subject ${wanted} is not the id of one of the objects.` };
  if (!mint) return { objects: list, subject: wanted };
  const fresh = new Map(list.map((o) => [o.id as string, newId()]));
  const objects = list.map((o) => ({
    ...o,
    id: fresh.get(o.id as string)!,
    ...(Array.isArray(o.links) ? { links: arr(o.links).map((l) => ({ ...l, object: fresh.get(l.object as string)! })) } : {}),
  }));
  return { objects, subject: fresh.get(wanted)! };
}

/**
 * The participants a run carries (actors-design.md decision 14): the request's, else the scenario's. Recording mints a fresh id
 * for every participant and holding, and remaps the objects' `owner`, a participant's `object` and a holding's `within`
 * (the objects take their fresh ids from `objectIds`).
 */
function participantsOf(
  value: Json,
  scenario: Json | undefined,
  mint = false,
  objectIds: ReadonlyMap<string, string> = new Map(),
): { participants: Json[]; ids: ReadonlyMap<string, string> } | { error: string } | null {
  const own = value.participants !== undefined && value.participants !== null;
  if (own && !Array.isArray(value.participants)) return { error: "participants must be an array." };
  const list = own ? arr(value.participants) : arr(scenario?.participants);
  if (!list.length) return null;
  const seen = new Set<string>();
  const names = new Set<string>();
  for (const p of list) {
    if (typeof p.id !== "string" || !p.id) return { error: "each participant needs an id." };
    if (seen.has(p.id)) return { error: `the participant id ${p.id} is used twice.` };
    seen.add(p.id);
    if (typeof p.name !== "string" || !p.name) return { error: "each participant needs a name." };
    if (names.has(p.name)) return { error: `the scenario has two participants named ${p.name}.` };
    names.add(p.name);
  }
  if (!mint) return { participants: list, ids: new Map(list.map((p) => [p.id as string, p.id as string])) };
  const fresh = new Map(list.map((p) => [p.id as string, newId()]));
  const remap = (id: unknown) => (typeof id === "string" ? (objectIds.get(id) ?? id) : id);
  const participants = list.map((p) => ({
    ...p,
    id: fresh.get(p.id as string)!,
    ...(p.object !== undefined ? { object: remap(p.object) } : {}),
    ...(Array.isArray(p.holds)
      ? { holds: arr(p.holds).map((h) => ({ ...h, id: newId(), ...(h.within !== undefined ? { within: remap(h.within) } : {}) })) }
      : {}),
  }));
  return { participants, ids: fresh };
}

/** A process document from a statechart config: one state per config state, one event per `on` key (import dry run). */
export function importDocument(config: Json, name: string, pkg: string | null, id: string = newId()): { document: Json; created: string[] } {
  const created: string[] = [];
  const events = new Map<string, string>();
  const byName = new Map<string, string>();
  const pending: { source: string; event: string; target: string }[] = [];
  const states = (value: unknown): Json[] =>
    Object.entries((value as Json | undefined) ?? {}).map(([key, raw]) => {
      const s = (raw ?? {}) as Json;
      const sid = newId();
      created.push(sid);
      byName.set(key, sid);
      const out: Json = { id: sid, name: key };
      if (s.type === "parallel" || s.type === "final") out.type = s.type;
      for (const [event, target] of Object.entries((s.on as Json | undefined) ?? {})) {
        if (!events.has(event)) {
          const eid = newId();
          events.set(event, eid);
          created.push(eid);
        }
        const t = typeof target === "string" ? target : String((target as Json | null)?.target ?? "");
        pending.push({ source: sid, event, target: t.replace(/^#/, "").replace(/^\./, "") });
      }
      const children = states(s.states);
      if (children.length) {
        out.states = children;
        if (s.type !== "parallel") out.type = "compound";
      }
      return out;
    });
  const root = states(config.states);
  const transitions = pending.map((p) => {
    const tid = newId();
    created.push(tid);
    const target = byName.get(p.target.split(".").pop() ?? "");
    return { id: tid, source: p.source, event: events.get(p.event), targets: target ? [target] : [] };
  });
  const document: Json = {
    kind: "process",
    id,
    name,
    ...(pkg ? { package: pkg } : {}),
    events: [...events].map(([n, eid]) => ({ id: eid, name: n })),
    states: root.length ? root : [{ id: newId(), name: "Initial" }],
    ...(transitions.length ? { transitions } : {}),
  };
  const initial = typeof config.initial === "string" ? byName.get(config.initial) : undefined;
  if (initial && initial !== (root[0]?.id as string | undefined)) document.initial = initial;
  return { document, created };
}

export function processHandlers(backend: MockBackend, kit: ProcessHandlerKit): HttpHandler[] {
  const { model } = backend;
  const url = (path: string) => `${kit.baseUrl}${path}`;
  const bad = (title: string) => kit.problem(400, "bad-request" as never, title);
  const find = (idOrName: string) => {
    const direct = model.entries.get(idOrName);
    if (direct?.json.kind === "process") return direct;
    return [...model.entries.values()].find((e) => e.json.kind === "process" && e.json.name === idOrName);
  };
  const missing = (id: string) => kit.problem(404, "not-found" as never, `No process has the id or name ${id}.`);
  const body = async (request: Request): Promise<Json | null> => {
    try {
      const text = await request.text();
      const value = text.trim() ? (JSON.parse(text) as unknown) : {};
      return value && typeof value === "object" && !Array.isArray(value) ? (value as Json) : null;
    } catch {
      return null;
    }
  };
  // Inputs may name actors; expressions see an actor's name as event.actor.
  const actorLookup = () => {
    const actors = [...model.entries.values()].filter((e) => e.json.kind === "actor");
    return {
      actorId: (name: string) => {
        const hits = actors.filter((a) => a.json.name === name);
        return hits.length === 1 ? hits[0].id : undefined;
      },
      actorName: (id: string) => actors.find((a) => a.id === id)?.json.name as string | undefined,
      membership: new Membership(actors),
      ownerActors: new Set(actors.filter((a) => (a.json.relative as Json | undefined)?.owner === true).map((a) => a.id)),
      membersActors: new Map(
        actors.flatMap((a) => {
          const rel = a.json.relative as Json | undefined;
          return typeof rel?.membersOf === "string" ? [[rel.membersOf, String(a.json.name ?? a.id)] as [string, string]] : [];
        }),
      ),
    };
  };
  // What the mock's interpreter reads of the object (object-effects-design.md section 7): the attributes of the subject's
  // entity with their defaults, and every lifecycle (the run's own draft in place of its saved file).
  const stereotypeOf = (key: string) => [...model.entries.values()].map((e) => e.json).find((d) => d.kind === "stereotype" && d.key === key);
  // The named trigger points, and whether a stereotype covers an entity (the entity or a base carries it), for the stepper and the sequences.
  const namedPoints = () => [...model.entries.values()].map((e) => e.json).filter((j) => j.kind === "trigger-point");
  const stereotypeCovers = () => {
    let coverage: Coverage | null = null;
    return (key: string, entity: string) => (coverage ??= new Coverage(model.entries.values())).covers(key, entity);
  };
  // The entities a lifecycle's subjects name: its entity subjects and the carriers of its stereotype subjects, as the engine's SubjectsOf
  // reads them (never every entity derived from one: an exception then still tests against the subject the process names).
  const governedBy = (lifecycle: Json) => {
    const coverage = new Coverage(model.entries.values());
    return [...new Set(subjectsOf(lifecycle).flatMap((x) => (x.entity ? [x.entity] : x.stereotype ? coverage.carriers(x.stereotype) : [])))];
  };
  // A named trigger point's key in an assume: its package chain's names from the outermost, a dot, then its own name (the root package has none).
  const qualifiedName = (point: Json): string => {
    const names = [String(point.name)];
    for (let id = point.package, hops = 0; typeof id === "string" && hops < 32; hops++) {
      const p = model.entries.get(id)?.json;
      if (!p) break;
      names.unshift(String(p.name));
      id = p.parent;
    }
    return names.join(".");
  };
  const effectOptions = (doc: Json) => ({
    namedPoints: namedPoints(),
    stereotypeCovers: stereotypeCovers(),
    stereotypeCarriers: (key: string) => new Coverage(model.entries.values()).carriers(key),
    attributesOf: (entity: string) =>
      bindableAttributes(model.entries.get(entity)?.json, (id) => model.entries.get(id)?.json, stereotypeOf).map((a) => ({
        id: String(a.id),
        name: String(a.name ?? ""),
        default: a.default,
      })),
    namedRights: new Map([...model.entries.values()].filter((e) => e.json.kind === "right").map((e) => [e.id, e.json] as [string, Json])),
    subjectEntity: subjectsOf(doc).find((e) => e.entity)?.entity,
    entityChain: (entity: string) => entityChain(entity, (id) => model.entries.get(id)?.json).map((d) => String(d.id)),
    lifecycles: [...[...model.entries.values()].map((e) => e.json).filter((j) => j.kind === "process" && j.use === "lifecycle" && j.id !== doc.id), doc],
  });
  const hasExpectedValues = (scenario: Json) => arr(scenario.steps).some((st) => (st.expect as Json | undefined)?.values !== undefined);
  // Verb steps and expected refusals are replayed for real too (rights-design.md decisions 25 and 26).
  const hasRightsSteps = (scenario: Json) =>
    arr(scenario.steps).some((st) => {
      const expect = st.expect as Json | undefined;
      return (
        st.verb !== undefined ||
        st.response !== undefined ||
        st.signature !== undefined ||
        expect?.signatures !== undefined ||
        expect?.refusal !== undefined ||
        expect?.refusedBy !== undefined ||
        expect?.checkWarnings !== undefined ||
        expect?.triggerPoints !== undefined ||
        Object.values((st.assume as Json | undefined) ?? {}).some((v) => typeof v === "string")
      );
    });
  // An event that names freeze or thaw takes effect in a run (the opt-in, decision 16), so such a scenario is replayed for real.
  const namesFreeze = (doc: Json) => arr(doc.events).some((e) => e.verb === "freeze" || e.verb === "thaw");
  const sameValues = (a: Json, b: Json) => {
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    return [...keys].every((k) => JSON.stringify(a[k] ?? null) === JSON.stringify(b[k] ?? null)) && Object.keys(a).length === Object.keys(b).length;
  };
  // A scenario with participants is replayed for real: a step refused while it expects to be accepted (or the reverse) fails
  // with MQ9301, and a refusal by actor names the participant and the list it did not satisfy.
  const participantReplay = (doc: Json, scenario: Json): { failure: Json | null; warnings: Json[] } => {
    if (!arr(scenario.participants).length && !hasExpectedValues(scenario) && !hasRightsSteps(scenario) && !namesFreeze(doc))
      return { failure: null, warnings: [] };
    const cast = castOf({}, scenario);
    const result = simulate(doc, scenario.start as never, arr(scenario.steps), {
      ...actorLookup(),
      ...effectOptions(doc),
      ...(cast && !("error" in cast) ? { objects: cast.objects, subject: cast.subject } : {}),
      participants: arr(scenario.participants),
      refusedBy: true,
    });
    const steps = arr(scenario.steps);
    // MQ9309 is a warning: a step refused as frozen after an earlier step froze it, with no refusal frozen expected (a missing
    // expect counts as not expecting it). The scenario still passes; the warning names the step that froze it.
    const warnings: Json[] = [];
    for (const t of result.trace) {
      if (t.index < 0 || t.accepted || t.refusal !== "frozen") continue;
      if ((steps[t.index].expect as Json | undefined)?.refusal === "frozen") continue;
      const by = freezingStep(doc, result.trace, t.index);
      if (by === null) continue;
      warnings.push({
        rule: "MQ9309",
        severity: "warning",
        message: `Step ${t.index + 1} is refused as frozen because step ${by + 1} froze it; thaw it in between, or expect the refusal (update the expectations from a replay).`,
        elementId: String(scenario.id),
        filePath: null,
        jsonPointer: `/steps/${t.index}/expect`,
        line: null,
        column: null,
      });
    }
    const unexpected = (index: number) => {
      const expect = steps[index].expect as Json | undefined;
      return expect?.accepted === undefined && warnings.some((w) => w.jsonPointer === `/steps/${index}/expect`);
    };
    const failure = participantFailure(scenario, result, steps, unexpected);
    return { failure, warnings };
  };
  const participantFailure = (scenario: Json, result: ReturnType<typeof simulate>, steps: Json[], unexpected: (index: number) => boolean) => {
    for (const t of result.trace) {
      if (t.index < 0) continue;
      const step = steps[t.index];
      const expected = (step.expect as Json | undefined)?.accepted !== false;
      if (t.accepted === expected || unexpected(t.index)) continue;
      const people = arr(scenario.participants);
      const p = people.find((x) => x.id === step.participant);
      const message =
        !t.accepted && t.refusal === "actor" && p
          ? PARTICIPANT_TEXTS.refusedByActor(
              t.index + 1,
              String(p.name),
              (unsatisfiedLists.get(t) ?? []).map((a) => actorLookup().actorName(a) ?? a),
            )
          : `Step ${t.index + 1} is ${t.accepted ? "accepted" : "refused"} while expect.accepted is ${String(expected)}; fix the step's input or set accepted to what the process does.`;
      return { step: t.index, rule: "MQ9301", message, expected, actual: t.accepted };
    }
    // MQ9308: the refusal a step expects and the element that refused it.
    for (const t of result.trace) {
      if (t.index < 0) continue;
      const expect = (steps[t.index].expect as Json | undefined) ?? {};
      const wrong = (key: "refusal" | "refusedBy") => expect[key] !== undefined && expect[key] !== (t[key] ?? null);
      if (!wrong("refusal") && !wrong("refusedBy")) continue;
      return {
        step: t.index,
        rule: "MQ9308",
        message: `Step ${t.index + 1}: the step's refusal${t.refusedBy ? ` (by ${t.refusedBy})` : ""} differs from expect.refusal or expect.refusedBy; fix the step or update the expectations from a replay.`,
        expected: { refusal: expect.refusal ?? null, refusedBy: expect.refusedBy ?? null },
        actual: { refusal: t.refusal, refusedBy: t.refusedBy ?? null },
      };
    }
    // MQ9310: the checks that warned and the trigger points that ran, in order, against what a step expects (skipped ones are left out).
    for (const t of result.trace) {
      if (t.index < 0) continue;
      const expect = (steps[t.index].expect as Json | undefined) ?? {};
      const ran = (t.triggerPoints ?? []).filter((r) => r.outcome !== "skipped").map((r) => r.triggerPoint);
      const warned = t.checkWarnings ?? [];
      const wrong =
        (expect.checkWarnings !== undefined && JSON.stringify(expect.checkWarnings) !== JSON.stringify(warned)) ||
        (expect.triggerPoints !== undefined && JSON.stringify(expect.triggerPoints) !== JSON.stringify(ran));
      if (!wrong) continue;
      return {
        step: t.index,
        rule: "MQ9310",
        message: `Step ${t.index + 1}: the checks that warned or the trigger points that ran differ from expect.checkWarnings or expect.triggerPoints; fix the trigger points or update the expectations from a replay.`,
        expected: { checkWarnings: expect.checkWarnings ?? null, triggerPoints: expect.triggerPoints ?? null },
        actual: { checkWarnings: warned, triggerPoints: ran },
      };
    }
    // MQ9311: the signatures counted after a step that answered an approval, against expect.signatures (only the ids listed are compared).
    for (const t of result.trace) {
      if (t.index < 0) continue;
      const expected = (steps[t.index].expect as Json | undefined)?.signatures as Json | undefined;
      if (!expected) continue;
      const counted = signatureCounts.get(t) ?? {};
      if (Object.entries(expected).every(([id, n]) => (counted[id] ?? 0) === n)) continue;
      return {
        step: t.index,
        rule: "MQ9311",
        message: `Step ${t.index + 1}: the signatures counted after the step differ from expect.signatures; fix the step or update the expectations from a replay.`,
        expected,
        actual: Object.fromEntries(Object.keys(expected).map((id) => [id, counted[id] ?? 0])),
      };
    }
    // MQ9311, part 2: the notices due at a time step, compared as a set of signature and kind (decision 35).
    for (const t of result.trace) {
      if (t.index < 0) continue;
      const expected = (steps[t.index].expect as Json | undefined)?.notices as { signature: string; kind: string }[] | undefined;
      if (!Array.isArray(expected)) continue;
      const key = (n: { signature: string; kind: string }) => `${n.signature}|${n.kind}`;
      const actual = (t.notices ?? []).map((n) => ({ signature: n.signature, kind: n.kind }));
      const want = new Set(expected.map(key));
      const have = new Set(actual.map(key));
      if (want.size === have.size && [...want].every((k) => have.has(k))) continue;
      return {
        step: t.index,
        rule: "MQ9311",
        message: `Step ${t.index + 1}: the notices due at the step differ from expect.notices; fix the step or update the expectations from a replay.`,
        expected: { notices: expected },
        actual: { notices: actual },
      };
    }
    // MQ9307: the values a step set against its expect.values (exactly the attributes the step changed).
    for (const t of result.trace) {
      if (t.index < 0) continue;
      const expected = (steps[t.index].expect as Json | undefined)?.values as Json | undefined;
      if (!expected || sameValues(expected, t.valuesChanged ?? {})) continue;
      return {
        step: t.index,
        rule: "MQ9307",
        message: `Step ${t.index + 1}: the values of the object the step set differ from expect.values; fix the actions' sets or update the expectations from a replay.`,
        expected,
        actual: t.valuesChanged ?? {},
      };
    }
    return null;
  };
  // Refresh expectations (the batch operation of the store) replays the scenario with the same lookups as a verify.
  model.scenarioReplay = (doc, scenario, triggerPoints) => {
    const cast = castOf({}, scenario);
    const replayed = replay(doc, scenario.start as never, arr(scenario.steps), {
      ...actorLookup(),
      ...effectOptions(doc),
      ...(cast && !("error" in cast) ? { objects: cast.objects, subject: cast.subject } : {}),
      participants: arr(scenario.participants),
      recordTriggerPoints: triggerPoints === true,
    });
    return replayed.complete ? { expects: replayed.expects, outcome: replayed.outcome } : null;
  };
  const idList = (v: string | null) => (v ? v.split(",").filter(Boolean) : undefined);
  const scenariosOf = (processId: string) =>
    [...model.entries.values()].filter((e) => e.json.kind === "scenario" && e.json.process === processId).sort((a, b) => (a.path < b.path ? -1 : 1));

  return [
    // Who can do what (rights-design.md decision 31): one row per state, actor and verb, never split by entity in the mock.
    rawHttp.get(url("/api/processes/:id/rights"), ({ params, request }) => {
      const entry = find(String(params.id));
      if (!entry) return missing(String(params.id));
      const q = new URL(request.url).searchParams;
      const limit = Number(q.get("limit") ?? 100);
      if (!Number.isInteger(limit) || limit < 1 || limit > 1000) return bad("limit must be 1 to 1000.");
      const start = q.get("cursor") ? Number(q.get("cursor")) : 0;
      if (!Number.isInteger(start) || start < 0) return bad("The cursor is not one this answer gave.");
      const lifecycles = [...model.entries.values()].map((e) => e.json).filter((j) => j.kind === "process" && j.use === "lifecycle");
      const actors = [...model.entries.values()].filter((e) => e.json.kind === "actor");
      const lookup = actorLookup();
      const rows = rightsGrid(
        entry.json,
        {
          lifecycles,
          named: new Map([...model.entries.values()].filter((e) => e.json.kind === "right").map((e) => [e.id, e.json] as [string, Json])),
          membership: new Membership(actors),
          ownerActors: lookup.ownerActors,
          entitiesOf: (l) => subjectsOf(l).flatMap((x) => (x.entity ? [x.entity] : [])),
        },
        { states: idList(q.get("states")), actors: idList(q.get("actors")), verbs: idList(q.get("verbs")), frozen: q.get("frozen") === "true" },
      );
      const page = rows.slice(start, start + limit);
      return HttpResponse.json({
        lifecycle: entry.id,
        rightsDefault: entry.json.rightsDefault === "deny" ? "deny" : "allow",
        rows: page,
        next: start + limit < rows.length ? String(start + limit) : null,
      });
    }),
    // What runs, in order, when a verb happens (trigger-points-design.md decision 36). Simpler in one way: this lifecycle's points only.
    rawHttp.get(url("/api/processes/:id/trigger-sequence"), ({ params, request }) => {
      const entry = find(String(params.id));
      if (!entry) return missing(String(params.id));
      const q = new URL(request.url).searchParams;
      const verb = q.get("verb");
      if (!verb) return bad("verb is required.");
      if (entry.json.use !== "lifecycle") return bad("Only a lifecycle has trigger points; this process is not one.");
      // An attribute or an end the model does not have is a bad request, as the engine answers (ModelStore.TriggerSequence.cs).
      const held = (list: "attributes" | "ends", id: string) =>
        [...model.entries.values()].some((e) => Array.isArray(e.json[list]) && (e.json[list] as Json[]).some((x) => x.id === id));
      const attribute = (idList(q.get("attributes")) ?? []).find((a) => !held("attributes", a));
      if (attribute) return bad(`attribute ${attribute} is not an attribute of the model.`);
      if (q.get("end") && !held("ends", q.get("end")!)) return bad(`end ${q.get("end")} is not a relation end of the model.`);
      const steps = sequenceOf(
        entry.json,
        {
          verb,
          ...(q.get("state") ? { state: q.get("state")! } : {}),
          ...(q.get("entity") ? { entity: q.get("entity")! } : {}),
          ...(q.get("attributes") ? { attributes: idList(q.get("attributes")) } : {}),
          ...(q.get("end") ? { end: q.get("end")! } : {}),
          ...(q.get("transition") ? { transition: q.get("transition")! } : {}),
        },
        (id) => entityChain(id, (x) => model.entries.get(x)?.json).map((d) => String(d.id)),
        // Named points come before the lifecycle's own, for the entity asked about or else those the lifecycle governs.
        namedPoints(),
        stereotypeCovers(),
        governedBy(entry.json),
      );
      return HttpResponse.json({ lifecycle: entry.id, verb, steps });
    }),
    // The same for an entity alone, with no lifecycle acting (decision 36): the named points that cover it. Simpler than the engine's
    // answer in one way (the engine's Asked also lists the lifecycles that govern the entity, with their rights steps): this lists the
    // named points only. A lifecycle's own points are asked of the lifecycle's route, and the editor never reads this one.
    rawHttp.get(url("/api/model/entities/:id/trigger-sequence"), ({ params, request }) => {
      const id = String(params.id);
      const entity = model.entries.get(id);
      if (!entity || entity.json.kind !== "entity") return kit.problem(404, "not-found" as never, `No entity has the id ${id}.`);
      const q = new URL(request.url).searchParams;
      const verb = q.get("verb");
      if (!verb) return bad("verb is required.");
      const group = builtInVerbs.find((v) => v.key === verb)?.group;
      if (group === "move" || group === "signature" || group === "task")
        return bad(
          `'${verb}' happens in one lifecycle; ask the lifecycle's trigger sequence. Only object, link and own verbs can be asked of an entity alone.`,
        );
      const held = (list: "attributes" | "ends", x: string) =>
        [...model.entries.values()].some((e) => Array.isArray(e.json[list]) && (e.json[list] as Json[]).some((y) => y.id === x));
      const attribute = (idList(q.get("attributes")) ?? []).find((a) => !held("attributes", a));
      if (attribute) return bad(`attribute ${attribute} is not an attribute of the model.`);
      if (q.get("end") && !held("ends", q.get("end")!)) return bad(`end ${q.get("end")} is not a relation end of the model.`);
      const steps = entitySequenceOf(
        {
          verb,
          entity: id,
          ...(q.get("attributes") ? { attributes: idList(q.get("attributes")) } : {}),
          ...(q.get("end") ? { end: q.get("end")! } : {}),
        },
        (x) => entityChain(x, (y) => model.entries.get(y)?.json).map((d) => String(d.id)),
        namedPoints(),
        stereotypeCovers(),
      );
      return HttpResponse.json({ verb, steps });
    }),
    // The entities a named trigger point runs for, in name order (trigger-points-design.md decision 33), read from the mock's own coverage:
    // the subjects' entities and their derived ones, the carriers of a stereotype (package defaults and implied ones included), less the
    // exceptions, narrowed by `entities`.
    rawHttp.get(url("/api/model/trigger-points/:id/covers"), ({ params }) => {
      const id = String(params.id);
      const point = model.entries.get(id);
      if (!point || point.json.kind !== "trigger-point") return kit.problem(404, "not-found" as never, `No trigger point has the id ${id}.`);
      const coverage = new Coverage(model.entries.values());
      const entities = [...model.entries.values()].filter((e) => e.json.kind === "entity");
      const chainOf = (e: string) => entityChain(e, (x) => model.entries.get(x)?.json).map((d) => String(d.id));
      const ids = namedCovered(
        point.json,
        entities.map((e) => e.id),
        chainOf,
        coverage,
      );
      const name = (e: string) => String(model.entries.get(e)?.json.name ?? "");
      ids.sort((a, b) => (name(a) < name(b) ? -1 : name(a) > name(b) ? 1 : a < b ? -1 : a > b ? 1 : 0));
      return HttpResponse.json({ entities: ids });
    }),
    rawHttp.post(url("/api/processes/:id/simulate"), async ({ params, request }) => {
      const entry = find(String(params.id));
      if (!entry) return missing(String(params.id));
      const value = await body(request);
      if (!value) return bad("The body must be a JSON object.");
      const scenario = typeof value.scenario === "string" ? value.scenario : null;
      const scenarioEntry = scenario
        ? [...model.entries.values()].find((e) => e.json.kind === "scenario" && e.json.process === entry.id && (e.id === scenario || e.json.name === scenario))
        : undefined;
      if (scenario && !scenarioEntry) return kit.problem(404, "not-found" as never, `The process has no scenario ${scenario}.`);
      // The recording's variant names the scenario it replays (simulateProcess.budget-rejected.json); it answers only the
      // recorded request: that scenario alone, no steps, no `from`, no draft.
      const variant = scenarioEntry ? kebab(String(scenarioEntry.json.name)) : null;
      const rec = replayable(kit.recorded, "simulateProcess", kit.pristine(), (r) => !!variant && r.file.endsWith(`.${variant}.json`) && !value.document);
      if (rec && value.from === undefined && value.steps === undefined) return kit.answer(rec);
      if (value.steps !== undefined && value.steps !== null && !Array.isArray(value.steps)) return bad("steps must be an array of inputs.");
      const start = value.start && typeof value.start === "object" ? (value.start as Json) : null;
      if (readAt(start?.at) === "invalid") return bad(`start.at is not an ISO 8601 instant: ${String(start?.at)}.`);
      const cast = castOf(value, scenarioEntry?.json);
      if (cast && "error" in cast) return bad(cast.error);
      const people = participantsOf(value, scenarioEntry?.json);
      if (people && "error" in people) return bad(people.error);
      let doc = entry.json;
      if (value.document && typeof value.document === "object") {
        doc = value.document as Json;
        if (doc.use !== "library" && !arr(doc.states).length) {
          const message = `Process '${String(doc.name)}' has no states; add one, or set use to library if it only shares guards, actions and gates.`;
          return HttpResponse.json(
            report([
              { rule: "MQ9020", severity: "error", message, elementId: entry.id, filePath: entry.path, jsonPointer: "/states", line: null, column: null },
            ]),
            { status: 422 },
          );
        }
        const errors = schemaDiagnostics({ id: entry.id, path: entry.path, json: doc }).filter((d) => d.severity === "error");
        if (errors.length) return HttpResponse.json(report(errors), { status: 422 });
      }
      // A scenario's start and steps run before the request's steps; with a scenario, its start is the one used (the
      // request's `start` is ignored), as the engine does.
      const scenarioSteps = scenarioEntry ? arr(scenarioEntry.json.steps) : [];
      const scenarioStart = scenarioEntry?.json.start && typeof scenarioEntry.json.start === "object" ? (scenarioEntry.json.start as Json) : null;
      const steps = [
        ...scenarioSteps,
        ...arr(value.steps).map((st) => resolveCheckNames(st, doc, effectOptions(doc).lifecycles ?? [], namedPoints(), qualifiedName)),
      ];
      const from = typeof value.from === "number" ? value.from : -1;
      const result = simulate(doc, (scenarioEntry ? scenarioStart : start) as never, steps, {
        from,
        ...actorLookup(),
        ...effectOptions(doc),
        ...(cast ? { objects: cast.objects, subject: cast.subject } : {}),
        ...(people ? { participants: people.participants } : {}),
      });
      return HttpResponse.json({ processHash: value.document ? sha256Hex(JSON.stringify(doc)) : entry.hash, ...result });
    }),
    rawHttp.post(url("/api/processes/:id/scenarios"), async ({ params, request }) => {
      const entry = find(String(params.id));
      if (!entry) return missing(String(params.id));
      const value = await body(request);
      if (!value || typeof value.name !== "string" || !value.name.trim() || !Array.isArray(value.steps)) return bad("name and steps are required.");
      if (value.outcome !== undefined && value.outcome !== null && value.outcome !== "final" && value.outcome !== "active")
        return bad("outcome must be 'final' or 'active'.");
      const start = value.start && typeof value.start === "object" ? (value.start as Json) : null;
      if (readAt(start?.at) === "invalid") return bad(`start.at is not an ISO 8601 instant: ${String(start?.at)}.`);
      const cast = castOf(value, undefined, true);
      if (cast && "error" in cast) return bad(cast.error);
      const objectIds = new Map<string, string>();
      if (cast && !("error" in cast)) arr(value.objects).forEach((o, i) => objectIds.set(String(o.id), String(cast.objects[i].id)));
      const people = participantsOf(value, undefined, true, objectIds);
      if (people && "error" in people) return bad(people.error);
      const dryRun = new URL(request.url).searchParams.get("dryRun") === "true";
      // A step names its participant by name or id; the replay and the record work with the request's participant ids.
      const requestPeople = arr(value.participants);
      const rawSteps = (value.steps as Json[]).map((st) => {
        const hit = typeof st.participant === "string" ? requestPeople.find((x) => x.id === st.participant || x.name === st.participant) : undefined;
        return resolveCheckNames(
          hit ? { ...st, participant: hit.id } : st,
          entry.json,
          effectOptions(entry.json).lifecycles ?? [],
          namedPoints(),
          qualifiedName,
        );
      });
      // The expectations come from a replay of the inputs, as the engine's record fills them (refusals as accepted: false).
      const replayed = replay(entry.json, start as never, rawSteps, {
        ...actorLookup(),
        ...effectOptions(entry.json),
        ...(cast ? { objects: cast.objects, subject: cast.subject } : {}),
        // The steps name the request's participants: the replay runs before the fresh ids are given.
        ...(value.participants ? { participants: arr(value.participants) } : {}),
        recordTriggerPoints: value.triggerPoints === true,
      });
      // The engine refuses inputs that cannot be replayed to the last step (422 with the replay's findings).
      if (!replayed.complete && value.steps.length) return HttpResponse.json(report(replayed.diagnostics.length ? replayed.diagnostics : []), { status: 422 });
      const lookup = actorLookup();
      const steps = rawSteps.map((raw, i) => {
        // Names resolve to ids as the engine's record resolves them; defaults are left out (the canonical form).
        const s = normalizeInput(raw, i, { doc: entry.json }, lookup.actorId) as unknown as Json;
        const step: Json = { id: newId() };
        for (const key of [
          "input",
          "event",
          "invoke",
          "after",
          "verb",
          "values",
          "end",
          "object",
          "participant",
          "actor",
          "signer",
          "meaning",
          "reason",
          "response",
          "signature",
          "payload",
          "assume",
        ]) {
          const v = s[key];
          if (v === undefined || v === null || v === "" || (key === "input" && v === "event")) continue;
          if (typeof v === "object" && !Array.isArray(v) && !Object.keys(v as Json).length) continue;
          step[key] = v;
        }
        if (typeof step.participant === "string" && people && !("error" in people)) step.participant = people.ids.get(step.participant) ?? step.participant;
        step.expect = replayed.expects[i] ?? {};
        if (typeof s.description === "string" && s.description) step.description = s.description;
        return step;
      });
      const outcome = (value.outcome as string | null | undefined) ?? replayed.outcome;
      const element: Json = {
        kind: "scenario",
        id: newId(),
        name: value.name.trim(),
        process: entry.id,
        ...(start && (start.context || start.at) ? { start } : {}),
        ...(cast
          ? {
              objects: cast.objects.map((o) => (typeof o.owner === "string" && people ? { ...o, owner: people.ids.get(o.owner) ?? o.owner } : o)),
              subject: cast.subject,
            }
          : {}),
        ...(people ? { participants: people.participants } : {}),
        steps,
        ...(outcome === "final" ? { outcome } : {}),
      };
      if (dryRun) return HttpResponse.json({ id: element.id, element, hash: null, applied: false, diagnostics: [] });
      const saved = model.create(element);
      if (saved.outcome !== "saved") return HttpResponse.json(report(saved.diagnostics), { status: 422 });
      return HttpResponse.json(
        { id: element.id, element: saved.current?.json ?? element, hash: saved.hash, applied: true, diagnostics: [] },
        { status: 201, headers: { ETag: `"${saved.hash}"` } },
      );
    }),
    rawHttp.post(url("/api/processes/:id/verify"), async ({ params, request }) => {
      const entry = find(String(params.id));
      if (!entry) return missing(String(params.id));
      const value = await body(request);
      if (!value) return bad("The body must be a JSON object.");
      let list = scenariosOf(entry.id);
      if (Array.isArray(value.scenarios)) {
        const wanted = value.scenarios.map(String);
        const unknown = wanted.find((w) => !list.some((s) => s.id === w || s.json.name === w));
        if (unknown) return kit.problem(404, "not-found" as never, `The process has no scenario ${unknown}.`);
        list = list.filter((s) => wanted.includes(s.id) || wanted.includes(String(s.json.name)));
      }
      const results = list.map((s) => {
        const replayed = participantReplay(entry.json, s.json);
        const failure = MOCK_FAILURES[s.id] ?? replayed.failure ?? null;
        return {
          scenario: s.id,
          name: String(s.json.name),
          passed: !failure,
          steps: arr(s.json.steps).length,
          failure,
          ...(replayed.warnings.length ? { warnings: replayed.warnings } : {}),
        };
      });
      return HttpResponse.json({ process: entry.id, results, passed: results.every((r) => r.passed) });
    }),
    rawHttp.get(url("/api/processes/:id/export"), ({ params, request }) => {
      const entry = find(String(params.id));
      if (!entry) return missing(String(params.id));
      const format = new URL(request.url).searchParams.get("format");
      if (format && format !== "xstate") return bad(`Unknown format ${format}.`);
      const headers = { "X-Maquettiste-Diagnostics": "0", "X-Maquettiste-Warnings": "0" };
      const rec = replayable(kit.recorded, "exportProcess", kit.pristine(), (r) => mentions(r, String(entry.json.name)));
      const config = rec ? rec.body : exportConfig(entry.json);
      return new HttpResponse(JSON.stringify(config, null, 2) + "\n", { status: 200, headers: { "Content-Type": "application/json", ...headers } });
    }),
    rawHttp.post(url("/api/processes/import"), async ({ request }) => {
      const value = await body(request);
      if (!value || value.config === undefined) return bad("config is required.");
      let config: unknown = value.config;
      if (typeof config === "string") {
        try {
          config = JSON.parse(config);
        } catch {
          return HttpResponse.json(
            {
              document: null,
              diagnostics: [
                {
                  rule: "MQ9401",
                  severity: "error",
                  message: "The config is not valid JSON.",
                  elementId: null,
                  filePath: null,
                  jsonPointer: "/config",
                  line: null,
                  column: null,
                },
              ],
              created: [],
              removed: [],
              applied: false,
              id: null,
              hash: null,
            },
            { status: 422 },
          );
        }
      }
      const cfg = (config ?? {}) as Json;
      let pkg: string | null = null;
      if (typeof value.package === "string") {
        const p = [...model.entries.values()].find((e) => e.json.kind === "package" && (e.id === value.package || e.json.name === value.package));
        if (!p) return kit.problem(404, "not-found" as never, `No domain has the id or name ${value.package}.`);
        pkg = p.id;
      }
      const name = typeof value.name === "string" && value.name ? value.name : String(cfg.id ?? "ImportedProcess").replace(/[^A-Za-z0-9_]/g, "");
      const { document, created } = importDocument(cfg, name, pkg);
      const dryRun = new URL(request.url).searchParams.get("dryRun") !== "false";
      if (dryRun) return HttpResponse.json({ document, diagnostics: [], created, removed: [], applied: false, id: null, hash: null });
      const saved = model.create(document);
      if (saved.outcome !== "saved")
        return HttpResponse.json({ document, diagnostics: saved.diagnostics, created, removed: [], applied: false, id: null, hash: null }, { status: 422 });
      return HttpResponse.json({ document, diagnostics: [], created, removed: [], applied: true, id: document.id, hash: saved.hash });
    }),
    rawHttp.post(url("/api/processes/:id/sync-enum"), async ({ params, request }) => {
      const entry = find(String(params.id));
      if (!entry) return missing(String(params.id));
      const value = await body(request);
      if (!value) return bad("The body must be a JSON object.");
      const lookup = (id: string) => model.entries.get(id);
      const refusal = (message: string) =>
        HttpResponse.json(
          {
            process: entry.id,
            enum: null,
            added: [],
            removed: [],
            reordered: false,
            refused: [],
            applied: false,
            diagnostics: [{ rule: "MQ9019", severity: "error", message, elementId: entry.id, filePath: null, jsonPointer: "", line: null, column: null }],
          },
          { status: 422 },
        );
      // The enum binding of `entity`, else the lifecycle's one enum binding, in either form (lifecycle-design.md decision 15).
      const wanted = typeof value.entity === "string" && value.entity ? value.entity : undefined;
      if (wanted && !subjectsOf(entry.json).some((e) => e.entity === wanted))
        return refusal(`Entity ${wanted} is not a subject of process '${String(entry.json.name)}'.`);
      const stereotypes = (key: string) => [...model.entries.values()].map((e) => e.json).find((d) => d.kind === "stereotype" && d.key === key);
      const enumBinding = (e: { entity?: string; stereotype?: string; boundAttribute?: string }) => {
        const attribute =
          e.stereotype !== undefined
            ? arr(stereotypes(e.stereotype)?.attributes).find((a) => a.id === e.boundAttribute)
            : bindableAttributes(lookup(e.entity!)?.json, (id) => lookup(id)?.json, stereotypes).find((a) => a.id === e.boundAttribute);
        const ref = (attribute?.type as Json | undefined)?.ref;
        return typeof ref === "string" && lookup(ref)?.json.kind === "enum" ? { attribute: attribute!, enumId: ref } : null;
      };
      const binding = subjectsOf(entry.json).find((e) => e.boundAttribute !== undefined && (!wanted || e.entity === wanted) && enumBinding(e));
      if (entry.json.use !== "lifecycle" || !binding) return refusal("The process is not a lifecycle bound to an enum attribute.");
      if (typeof value.expectedHash === "string" && value.expectedHash !== entry.hash)
        return kit.problem(409, "conflict" as never, "The process changed since you loaded it.");
      const drift = enumDrift(entry.json, (id) => lookup(id), binding.entity, stereotypes, binding.stereotype);
      const enumId = enumBinding(binding)!.enumId;
      const enumEntry = lookup(enumId);
      if (!enumEntry) return refusal("The bound attribute's type is not an enum.");
      const members = arr(enumEntry.json.members);
      const states = drift?.states ?? members.map((m) => String(m.name));
      const kept = members.map((m) => String(m.name)).filter((n) => states.includes(n));
      const added = states.filter((s) => !members.some((m) => m.name === s));
      const removed = members.filter((m) => !states.includes(String(m.name))).map((m) => String(m.name));
      const reordered = kept.join("|") !== states.filter((s) => kept.includes(s)).join("|");
      const result = { process: entry.id, enum: enumEntry.id, added, removed, reordered, refused: [], diagnostics: [] as unknown[] };
      if (value.dryRun === true || !drift) return HttpResponse.json({ ...result, applied: false });
      const next = { ...enumEntry.json, members: states.map((s) => members.find((m) => m.name === s) ?? { id: newId(), name: s }) };
      const saved = model.save(enumEntry.id, next, enumEntry.hash);
      if (saved.outcome !== "saved") return refusal(saved.diagnostics[0]?.message ?? "The enum could not be saved.");
      return HttpResponse.json({ ...result, applied: true });
    }),
    // Sync codes (lifecycle-design.md decision 19): the plan, written unless `dryRun`.
    rawHttp.post(url("/api/processes/:id/sync-codes"), async ({ params, request }) => {
      const entry = find(String(params.id));
      if (!entry) return missing(String(params.id));
      const value = await body(request);
      if (!value) return bad("The body must be a JSON object.");
      const refusal = (message: string) =>
        HttpResponse.json(
          {
            process: entry.id,
            entity: typeof value.entity === "string" ? value.entity : "",
            attribute: typeof value.attribute === "string" ? value.attribute : "",
            binds: false,
            codes: [],
            unmatched: [],
            applied: false,
            diagnostics: [{ rule: "MQ9019", severity: "error", message, elementId: entry.id, filePath: null, jsonPointer: "", line: null, column: null }],
          },
          { status: 422 },
        );
      if (typeof value.entity !== "string" || typeof value.attribute !== "string") return bad("entity and attribute are required.");
      if (typeof value.expectedHash === "string" && value.expectedHash !== entry.hash)
        return kit.problem(409, "conflict" as never, "The process changed since you loaded it.");
      const outcome = syncCodesOf(model, entry.json, value.entity, value.attribute, value.stateCodes as Json | undefined, newId);
      if ("problem" in outcome) return refusal(outcome.problem);
      const result = { ...outcome.plan, diagnostics: [] as unknown[] };
      const writes = outcome.plan.binds || outcome.plan.codes.some((c) => c.source !== "kept");
      if (value.dryRun === true || !writes) return HttpResponse.json({ ...result, applied: false });
      const saved = model.save(entry.id, outcome.next, entry.hash);
      if (saved.outcome !== "saved") return refusal(saved.diagnostics[0]?.message ?? "The process could not be saved.");
      return HttpResponse.json({ ...result, applied: true });
    }),
  ];
}
