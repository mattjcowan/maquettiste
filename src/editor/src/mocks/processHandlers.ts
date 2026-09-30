// The mock's process operations (phase-3-design.md 4.4): simulate, record, verify, export, import and sync-enum. The
// engine recordings of the processes fixture answer simulate and export while the mock model is pristine and the
// request is the recorded one (PurchaseApproval keeps the fixture's ids, processSeed.ts); any other simulate, and the
// expectations a record fills in, come from the mock's reduced interpreter (model/stepper.ts). Verify passes every
// scenario but those processSeed.ts lists as failing.
import { http as rawHttp, HttpResponse, type HttpHandler } from "msw";
import { newId } from "@/lib/ids";
import { sha256Hex } from "@/lib/sha256";
import type { MockBackend } from "./backend";
import { mentions, replayable, type Recording } from "./recorded";

const kebab = (name: string) => name.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
import { MOCK_FAILURES } from "./model/processSeed";
import { normalizeInput, readAt, replay, simulate } from "./model/stepper";
import { enumDrift, schemaDiagnostics } from "./model/validate";
import { report } from "./model/store";

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
    };
  };
  const scenariosOf = (processId: string) =>
    [...model.entries.values()].filter((e) => e.json.kind === "scenario" && e.json.process === processId).sort((a, b) => (a.path < b.path ? -1 : 1));

  return [
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
      let doc = entry.json;
      if (value.document && typeof value.document === "object") {
        doc = value.document as Json;
        const errors = schemaDiagnostics({ id: entry.id, path: entry.path, json: doc }).filter((d) => d.severity === "error");
        if (errors.length) return HttpResponse.json(report(errors), { status: 422 });
      }
      // A scenario's start and steps run before the request's steps; with a scenario, its start is the one used (the
      // request's `start` is ignored), as the engine does.
      const scenarioSteps = scenarioEntry ? arr(scenarioEntry.json.steps) : [];
      const scenarioStart = scenarioEntry?.json.start && typeof scenarioEntry.json.start === "object" ? (scenarioEntry.json.start as Json) : null;
      const steps = [...scenarioSteps, ...arr(value.steps)];
      const from = typeof value.from === "number" ? value.from : -1;
      const result = simulate(doc, (scenarioEntry ? scenarioStart : start) as never, steps, { from, ...actorLookup() });
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
      const dryRun = new URL(request.url).searchParams.get("dryRun") === "true";
      // The expectations come from a replay of the inputs, as the engine's record fills them (refusals as accepted: false).
      const replayed = replay(entry.json, start as never, value.steps as Json[], actorLookup());
      // The engine refuses inputs that cannot be replayed to the last step (422 with the replay's findings).
      if (!replayed.complete && value.steps.length) return HttpResponse.json(report(replayed.diagnostics.length ? replayed.diagnostics : []), { status: 422 });
      const lookup = actorLookup();
      const steps = (value.steps as Json[]).map((raw, i) => {
        // Names resolve to ids as the engine's record resolves them; defaults are left out (the canonical form).
        const s = normalizeInput(raw, i, { doc: entry.json }, lookup.actorId) as unknown as Json;
        const step: Json = { id: newId() };
        for (const key of ["input", "event", "invoke", "after", "actor", "signer", "meaning", "reason", "payload", "assume"]) {
          const v = s[key];
          if (v === undefined || v === null || v === "" || (key === "input" && v === "event")) continue;
          if (typeof v === "object" && !Array.isArray(v) && !Object.keys(v as Json).length) continue;
          step[key] = v;
        }
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
        const failure = MOCK_FAILURES[s.id] ?? null;
        return { scenario: s.id, name: String(s.json.name), passed: !failure, steps: arr(s.json.steps).length, failure };
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
      if (entry.json.use !== "lifecycle" || typeof entry.json.boundAttribute !== "string")
        return refusal("The process is not a lifecycle bound to an enum attribute.");
      if (typeof value.expectedHash === "string" && value.expectedHash !== entry.hash)
        return kit.problem(409, "conflict" as never, "The process changed since you loaded it.");
      const drift = enumDrift(entry.json, (id) => lookup(id));
      const subject = lookup(String(entry.json.subject));
      const attribute = arr(subject?.json.attributes).find((a) => a.id === entry.json.boundAttribute);
      const enumId = String(((attribute?.type as Json | undefined)?.ref as string | undefined) ?? "");
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
  ];
}
