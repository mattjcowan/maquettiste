// The mock's reduced statechart interpreter (phase-3-design.md 4.1), used only in mock mode to answer simulate and
// record with a real trace for any input list. The engine's StatechartInterpreter is the reference: this file follows
// its rules (configuration, macrostep and microstep, selection with priority and conflict removal, exit and entry sets,
// shallow and deep history, `done` for compound and parallel states, a simulated clock, pending invokes, gates with
// audit records, actors and refusal reasons) and leaves out what the mock does not need: sub-process instances (a
// `process` invoke waits like a service task), MQ9505 type checks, MQ9506 overlap reports and the sandbox's limits.
// Guards and actions with an expression run through `new Function` over frozen copies of the context and event.

import { Membership } from "./membership";
import { evalTree, UNKNOWN, type EvalEnv, type Value } from "./effects";
import { answerLifecycle, checkAll, FROZEN_VERBS, ownLifecycleOnly, takesPart, usesRights, type Facts, type Holding, type Tri } from "./rightsCheck";
import {
  assumedWord,
  CHECK_WORDS,
  isTeamCode,
  matchPoint,
  momentOf,
  namedFor,
  pointsOf,
  usesTriggers,
  type Moment,
  type Place,
  type StereotypeCover,
} from "./triggerRun";
import { subjectsOf } from "@/model/process";
import { builtInVerbs } from "@/model/verbs";

type Json = Record<string, unknown>;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string | null => (typeof v === "string" && v !== "" ? v : null);
const num = (v: unknown): number | null => (typeof v === "number" ? v : null);
const ids = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);

export const DEFAULT_START = "2000-01-01T00:00:00Z";
export const MAX_MICROSTEPS = 1000;

// ------------------------------------------------------------------ durations and the clock

const DURATION = /^P(?=\d|T\d)(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?=\d)(?:(\d+)H)?(?:(\d+)M)?(\d+(?:[.,]\d+)?S)?)?$/;

/** An ISO 8601 duration in milliseconds, as the engine reads it (a year is 365 days, a month 30); null when it is not one. */
export function durationMs(text: string | null | undefined): number | null {
  if (!text) return null;
  const m = DURATION.exec(text);
  if (!m) return null;
  const n = (i: number) => (m[i] ? Number(m[i]) : 0);
  const days = n(1) * 365 + n(2) * 30 + n(3) * 7 + n(4);
  const seconds = m[7] ? Number(m[7].slice(0, -1).replace(",", ".")) : 0;
  return ((days * 24 + n(5)) * 60 + n(6)) * 60_000 + Math.round(seconds * 1000);
}

/** The clock as the engine writes it: UTC, seconds, and a fraction only when there is one. */
export function clockText(ms: number): string {
  return new Date(ms).toISOString().replace(/\.(\d+)Z$/, (_, f: string) => (/^0+$/.test(f) ? "Z" : `.${f.replace(/0+$/, "")}Z`));
}

// ------------------------------------------------------------------ the chart

interface Node {
  id: string;
  name: string;
  type: string;
  parent: Node | null;
  order: number;
  children: Node[];
  initialChild: Node | null;
  defaultTarget: Node | null;
  doc: Json;
  transitions: Edge[];
  timers: Edge[];
  isRoot: boolean;
}

interface Edge {
  id: string;
  index: number;
  source: Node;
  trigger: string;
  key: string;
  guardRef: string | null;
  guard: Json | null;
  targets: Node[];
  actions: Json[];
  external: boolean;
  gate: Json | null;
  delay: number | null;
  displayName: string | null;
  /** The verb of the move: the transition's own, else its event's (trigger-points-design.md decision 4); null runs no trigger points. */
  verb: string | null;
}

class Chart {
  readonly root: Node;
  readonly states: Node[] = [];
  readonly byId = new Map<string, Node>();
  readonly transitions: Edge[] = [];
  readonly events = new Map<string, Json>();
  readonly guards = new Map<string, Json>();
  readonly actions = new Map<string, Json>();
  readonly invokes = new Map<string, { invoke: Json; state: Node }>();
  readonly attributes: Json[];
  readonly compiled = new Set<string>();
  private readonly groups = new Map<string, Edge[]>();

  constructor(readonly doc: Json) {
    this.root = node(doc, null, -1, true);
    this.root.type = "compound";
    this.root.children = this.build(doc.states, this.root);
    this.root.initialChild = this.initial(str(doc.initial), this.root);
    for (const s of this.states) if (s.type === "compound") s.initialChild = this.initial(str(s.doc.initial), s);
    for (const s of this.states)
      if (s.type === "history" && s.parent) {
        const d = str(s.doc.defaultTarget);
        s.defaultTarget = (d && this.byId.get(d)) || s.parent.initialChild;
      }
    for (const e of arr(doc.events)) if (!this.events.has(String(e.id))) this.events.set(String(e.id), e);
    for (const g of arr(doc.guards)) if (!this.guards.has(String(g.id))) this.guards.set(String(g.id), g);
    for (const a of arr(doc.actions)) if (!this.actions.has(String(a.id))) this.actions.set(String(a.id), a);
    for (const s of this.states)
      for (const i of arr(s.doc.invoke)) if (!this.invokes.has(String(i.id))) this.invokes.set(String(i.id), { invoke: i, state: s });
    this.attributes = arr(doc.context);
    for (const x of [...this.guards.values(), ...this.actions.values()]) if (compiles(str(x.expression))) this.compiled.add(String(x.id));
    arr(doc.transitions).forEach((t, index) => {
      const source = this.byId.get(String(t.source));
      if (!source) return;
      const trigger = str(t.trigger) ?? "event";
      const key = trigger === "event" ? (str(t.event) ?? "") : trigger === "invoke-done" || trigger === "invoke-error" ? (str(t.invoke) ?? "") : "";
      const guardRef = str(t.guard);
      const edge: Edge = {
        id: String(t.id),
        index,
        source,
        trigger,
        key,
        guardRef,
        guard: guardRef ? (this.guards.get(guardRef) ?? null) : null,
        targets: ids(t.targets)
          .map((x) => this.byId.get(x))
          .filter((x): x is Node => !!x),
        actions: ids(t.actions)
          .map((x) => this.actions.get(x))
          .filter((x): x is Json => !!x),
        external: t.external === true,
        gate: t.gate && typeof t.gate === "object" ? (t.gate as Json) : null,
        delay: trigger === "after" ? durationMs(str(t.after)) : null,
        displayName: str(t.displayName),
        verb: str(t.verb) ?? (trigger === "event" ? str(this.events.get(key)?.verb) : null),
      };
      this.transitions.push(edge);
      source.transitions.push(edge);
      if (trigger === "after" && edge.delay !== null) source.timers.push(edge);
      const k = `${source.id}|${trigger}|${key}`;
      this.groups.set(k, [...(this.groups.get(k) ?? []), edge]);
    });
  }

  candidates(source: Node, trigger: string, key: string): Edge[] {
    return this.groups.get(`${source.id}|${trigger}|${key}`) ?? [];
  }

  private build(list: unknown, parent: Node): Node[] {
    return arr(list).map((s) => {
      const n = node(s, parent, this.states.length, false);
      this.states.push(n);
      if (!this.byId.has(n.id)) this.byId.set(n.id, n);
      n.children = this.build(s.states, n);
      return n;
    });
  }

  private initial(id: string | null, holder: Node): Node | null {
    const named = id ? this.byId.get(id) : undefined;
    if (named && named.parent === holder) return named;
    return holder.children[0] ?? null;
  }
}

function node(doc: Json, parent: Node | null, order: number, isRoot: boolean): Node {
  return {
    id: String(doc.id),
    name: String(doc.name ?? ""),
    type: str(doc.type) ?? "atomic",
    parent,
    order,
    children: [],
    initialChild: null,
    defaultTarget: null,
    doc,
    transitions: [],
    timers: [],
    isRoot,
  };
}

const isLeaf = (s: Node) => s.children.length === 0 && s.type !== "history";

function isDescendantOf(s: Node, ancestor: Node): boolean {
  for (let p = s.parent; p; p = p.parent) if (p === ancestor) return true;
  return false;
}

function ancestorsOf(s: Node): Node[] {
  const out: Node[] = [];
  for (let p = s.parent; p; p = p.parent) out.push(p);
  return out;
}

// ------------------------------------------------------------------ expressions

function compiles(expression: string | null): boolean {
  if (!expression) return false;
  try {
    compile(expression);
    return true;
  } catch {
    return false;
  }
}

function compile(expression: string): (context: unknown, event: unknown) => unknown {
  return new Function("context", "event", `"use strict"; return (\n${expression}\n);`) as (context: unknown, event: unknown) => unknown;
}

function frozen<T>(value: T): T {
  const copy = JSON.parse(JSON.stringify(value ?? null)) as T;
  const freeze = (v: unknown) => {
    if (v && typeof v === "object") {
      Object.freeze(v);
      for (const x of Object.values(v)) freeze(x);
    }
  };
  freeze(copy);
  return copy;
}

const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);

const describe = (v: unknown) =>
  v === null || v === undefined
    ? "null"
    : typeof v === "string"
      ? `the string '${v}'`
      : typeof v === "number"
        ? `the number ${v}`
        : typeof v === "boolean"
          ? String(v)
          : Array.isArray(v)
            ? "a list"
            : "an object";

// ------------------------------------------------------------------ the trace

export interface SimInputJson {
  id: string;
  input: string;
  event: string | null;
  invoke: string | null;
  after: string | null;
  actor: string | null;
  signer: string | null;
  meaning: string | null;
  reason: string | null;
  payload: Json;
  /** A guard's result by its id, or a team-code check's outcome word by its id or name. */
  assume: Record<string, boolean | "pass" | "warning" | "error">;
  expect: null;
  description: unknown;
  /** The participant that acts (actors-design.md decision 15); written only when the input names one. */
  participant?: string;
  /** A verb step's verb, and for `modify` its values (by attribute id) and the link it acts on (rights-design.md decision 25). */
  verb?: string;
  values?: Json;
  /** For a `read` step, the attributes it reads (ids, or names as written). */
  attributes?: string[];
  end?: string;
  object?: string;
  /** How the step answers an approval (signoff-design.md decision 25); left out when it is `sign`. */
  response?: Response;
  /** The named signature the step answers; left out when the engine picks it. */
  signature?: string;
}

export type Response = "sign" | "reject" | "ignore" | "withdraw";
const RESPONSES: readonly Response[] = ["sign", "reject", "ignore", "withdraw"];

export interface DiagnosticJson {
  rule: string;
  severity: "error" | "warning" | "info";
  message: string;
  elementId: string | null;
  filePath: null;
  jsonPointer: null;
  line: null;
  column: null;
}

export interface StepTraceJson {
  index: number;
  input: SimInputJson | null;
  accepted: boolean;
  refusal: string | null;
  microsteps: {
    transitions: string[];
    exited: string[];
    entered: string[];
    actions: { action: string; source: "expression" | "sets" | "stub"; changed: Json; sets?: Json }[];
  }[];
  guards: { guard: string; transition: string; result: boolean | null; source: "expression" | "assumed" | "missing" }[];
  audit: Json[];
  configuration: string[];
  context: Json;
  changed: Json;
  clock: string;
  final: boolean;
  diagnostics: DiagnosticJson[];
  incomplete: boolean;
  /** The system actor when it made the move: the initial entry, time steps and invoke completions that are not human tasks, naming no actor. */
  actor?: string;
  /** With a participant on the input: the actor the step acted as; left out when the participant satisfies none of the list. */
  actsAs?: string | null;
  /** The attributes of the object the step set (by attribute id, to the new value); left out when none changed. */
  valuesChanged?: Json;
  /** What a reader should know about the step (assumed states, values the mock could not work out); left out when empty. */
  notes?: string[];
  /** The element that refused the step (rights-design.md decision 12); written for a refusal in a project that uses rights. */
  refusedBy?: string;
  /** The rights that held for the step, in check order; left out when none did. */
  rights?: { right: string; lifecycle: string; access: "allow" | "deny" }[];
  /** True while the subject is frozen after the input (rights-design.md section 7); left out when not. */
  frozen?: true;
  /** The trigger points whose places matched, in run order (trigger-points-design.md decision 28); left out when none. */
  triggerPoints?: TriggerRunJson[];
  /** The checks that warned, each once, in the order they first warned; left out when none. */
  checkWarnings?: string[];
}

/** A trigger point that ran in a step, and what it did. */
export interface TriggerRunJson {
  triggerPoint: string;
  /** The lifecycle the point belongs to; left out for a named trigger point. */
  lifecycle?: string;
  verb: string;
  type: "check" | "action" | "replace";
  timing?: "before" | "with-change" | "after-commit";
  outcome: "passed" | "warned" | "refused" | "ran" | "replaced" | "queued" | "skipped";
  assumed?: true;
  outside?: true;
  sets?: Json;
}

export interface SimulationJson {
  trace: StepTraceJson[];
  configuration: string[];
  context: Json;
  enabled: {
    trigger: string;
    event: string | null;
    invoke: string | null;
    transitions: string[];
    actors: string[];
    guardUnknown: boolean;
    checksUnknown?: string[];
    gate: { have: number; need: number } | null;
    responses?: Response[];
  }[];
  pending: { invoke: string; state: string }[];
  timers: { transition: string; dueAt: string }[];
  gates: {
    gate: string;
    transition: string;
    signatures: { signer: string; actor: string; meaning: string | null; reason: string | null; signature?: string }[];
    satisfied: boolean;
    stages?: {
      stage: string;
      complete: boolean;
      signatures: { signature: string; needed: boolean; counted: number; required: number; finished: string[]; complete: boolean }[];
    }[];
  }[];
  final: boolean;
  clock: string;
  diagnostics: DiagnosticJson[];
}

export interface StepperOptions {
  /** The first trace index returned (-1: the initial entry). */
  from?: number;
  /** The id of an actor named `name` (inputs may name actors), or undefined. */
  actorId?: (name: string) => string | undefined;
  /** An actor's name, for the `event.actor` an expression sees. */
  actorName?: (id: string) => string | undefined;
  /** The actors' memberships and the system actor; without it every actor stands only for itself. */
  membership?: Membership;
  /** The scenario's cast and the object the process runs for (decision 22); carried in the run, not yet read by guards. */
  objects?: Json[];
  subject?: string;
  /** The run's participants (a scenario's `participants`, actors-design.md decision 14). */
  participants?: Json[];
  /** Actors from the object that carry `owner`: satisfied by the participant the subject object names as its owner. */
  ownerActors?: ReadonlySet<string>;
  /** The name of the actor that stands for the members of a role within a context, by the role's id (for MQ9305's advice). */
  membersActors?: ReadonlyMap<string, string>;
  /** The attributes of an entity (its own and inherited), for the subject's row: id and default. */
  attributesOf?: (entity: string) => { id: string; name?: string; default?: unknown }[];
  /** The named rights by id (rights-design.md decision 7). */
  namedRights?: ReadonlyMap<string, Json>;
  /** Write `refusedBy` for every refusal, not only in a project that uses rights (a step may expect it). */
  refusedBy?: boolean;
  /** The entity of the process's first subject: the row of a run with no objects. */
  subjectEntity?: string;
  /** Every lifecycle process document, for in_state. */
  lifecycles?: Json[];
  /** An entity and the entities it is based on, for a trigger point's `entities` key; the entity alone without it. */
  entityChain?: (entity: string) => string[];
  /** The named trigger points (kind `trigger-point`), and whether a stereotype (by key) covers an entity, for the ones that name stereotypes. */
  namedPoints?: Json[];
  stereotypeCovers?: StereotypeCover;
  /** The entities that carry a stereotype themselves (by key), in name order: a run with no object tests a stereotype subject against them. */
  stereotypeCarriers?: (key: string) => string[];
  /** Replay also records each step's `expect.triggerPoints` (the record operation's `triggerPoints: true`). */
  recordTriggerPoints?: boolean;
}

/** The texts of the participant cases of MQ9301 and MQ9305, as RuleCatalog words them (the engine's rule list). */
export const PARTICIPANT_TEXTS = {
  unknown: (id: string) => `A step's participant '${id}' is not a participant of the scenario; fix the step, the object or the participant.`,
  actorNotSatisfied: (participant: string, actor: string) =>
    `A step's actor '${actor}' is one the participant '${participant}' does not satisfy; fix the step, the object or the participant.`,
  /** MQ9305: the participant holds the actor's role only within a context, and the step needs it held without one. */
  heldOnlyWithin: (participant: string, role: string, context: string, actor: string, named: string | undefined) =>
    `participant '${participant}' holds ${role} only within ${context}; this step needs ${actor} held without a context. Hold it without a context too, or ${
      named ? `name the actor for ${named} within ${context}` : `name an actor taken from the object for the members of ${role} within ${context}`
    }.`,
  /** MQ9301: a refusal by actor names the participant and the list it did not satisfy. */
  refusedByActor: (step: number, participant: string, list: string[]) =>
    `Step ${step} is refused while expect.accepted is true: the participant '${participant}' satisfies none of ${list.length ? list.join(", ") : "the actors the step needs"}; fix the step's input or set accepted to what the process does.`,
} as const;

/** The list a participant's refused step did not satisfy, by trace entry (kept off the answer: the engine's trace has no such member). */
export const unsatisfiedLists = new WeakMap<object, string[]>();

const WARNINGS = new Set(["MQ9502", "MQ9503", "MQ9504", "MQ9505"]);

function diagnostic(rule: string, message: string, elementId: string | null): DiagnosticJson {
  return { rule, severity: WARNINGS.has(rule) ? "warning" : "error", message, elementId, filePath: null, jsonPointer: null, line: null, column: null };
}

/** A step or input as the trace echoes it: every member written, names resolved to ids, `expect` dropped. */
export function normalizeInput(step: Json, index: number, chart: { doc: Json }, actorId?: (name: string) => string | undefined): SimInputJson {
  const doc = chart.doc;
  const byName = (list: Json[], v: string | null) => {
    if (!v) return v;
    if (list.some((x) => x.id === v)) return v;
    const hits = list.filter((x) => x.name === v);
    return hits.length === 1 ? String(hits[0].id) : v;
  };
  const invokes: Json[] = [];
  const walk = (list: unknown) => arr(list).forEach((s) => (invokes.push(...arr(s.invoke)), walk(s.states)));
  walk(doc.states);
  const guards = arr(doc.guards);
  const assume: SimInputJson["assume"] = {};
  for (const [k, v] of Object.entries((step.assume as Json | undefined) ?? {})) {
    if (typeof v === "boolean") assume[byName(guards, k) ?? k] = v;
    else if (typeof v === "string" && CHECK_WORDS.includes(v)) assume[k] = v as "pass" | "warning" | "error";
  }
  const actor = str(step.actor);
  return {
    id: str(step.id) ?? `input-${index}`,
    input: str(step.input) ?? "event",
    event: byName(arr(doc.events), str(step.event)),
    invoke: byName(invokes, str(step.invoke)),
    after: str(step.after),
    actor: actor && actorId && !/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/.test(actor) ? (actorId(actor) ?? actor) : actor,
    signer: str(step.signer),
    meaning: str(step.meaning),
    reason: str(step.reason),
    payload: step.payload && typeof step.payload === "object" ? { ...(step.payload as Json) } : {},
    assume,
    expect: null,
    description: step.description ?? null,
    ...(str(step.participant) ? { participant: str(step.participant)! } : {}),
    ...(str(step.verb) ? { verb: str(step.verb)! } : {}),
    ...(step.values && typeof step.values === "object" && !Array.isArray(step.values) ? { values: { ...(step.values as Json) } } : {}),
    ...(Array.isArray(step.attributes) && step.attributes.length ? { attributes: step.attributes.filter((x): x is string => typeof x === "string") } : {}),
    ...(str(step.end) ? { end: str(step.end)! } : {}),
    ...(str(step.object) ? { object: str(step.object)! } : {}),
    ...(RESPONSES.includes(str(step.response) as Response) && step.response !== "sign" ? { response: step.response as Response } : {}),
    ...(str(step.signature) ? { signature: byName(signatureList(doc), str(step.signature)) ?? undefined } : {}),
  };
}

/** Every named signature of the process's gates, for a step that names one. */
function signatureList(doc: Json): Json[] {
  return arr(doc.transitions).flatMap((t) => gateSignatures(t.gate as Json | undefined));
}

/** A gate's named signatures in file order (stage, then signature); empty for a gate without stages. */
function gateSignatures(gate: Json | null | undefined): Json[] {
  return gate ? arr(gate.stages).flatMap((st) => arr(st.signatures).map((sg) => ({ ...sg, stage: st.id }))) : [];
}

// ------------------------------------------------------------------ the interpreter

interface Timer {
  edge: Edge;
  due: number;
  seq: number;
}

interface Signature {
  signer: string;
  actor: string;
  meaning: string | null;
  reason: string | null;
  /** On an approval, the named signature it counts for. */
  signature?: string;
}

/** An approval's progress on a gated move (signoff-design.md decisions 7 to 16): which signatures its round needs, what was counted and who answered. */
interface Approval {
  needed: Set<string>;
  counted: Signature[];
  answered: { signer: string; signature: string; kind: "sign" | "ignore" }[];
  /** Nothing was needed when the round started: the next occurrence moves as if there were no gate. */
  waiting: boolean;
}

/** Who did which verb or move, by the step's sequence number (decision 28). */
interface Act {
  seq: number;
  signer: string;
  verb: string | null;
  transition: string | null;
}

/** The refusal of a response, with the element that refused it. */
type Refusal = [string, string];

interface Scope {
  input: SimInputJson | null;
  /** The input as the trace echoes it: with a participant's actor and signer filled in, as the engine echoes it. */
  echo: SimInputJson | null;
  actsAs?: string | null;
  list?: string[];
  actor: string | null;
  event: Json;
  refusal: string | null;
  microsteps: StepTraceJson["microsteps"];
  guards: StepTraceJson["guards"];
  audit: Json[];
  diagnostics: DiagnosticJson[];
  notes: string[];
  refusedBy: string | null;
  rights: Holding[];
  /** The trigger points that ran in the step, in run order; after-commit ones wait in `queued` and come last (decision 28). */
  triggerRuns: TriggerRunJson[];
  queued: TriggerRunJson[];
  checkWarnings: string[];
  /** The gated move whose approval the step answered and was accepted on, for `expect.signatures`. */
  answered?: Edge;
}

/** The signatures counted after each accepted step that answered an approval, by named signature id (kept off the answer: not in the trace's contract). */
export const signatureCounts = new WeakMap<object, Record<string, number>>();

/** The rows 8 and 9 of a candidate (a move or a verb step), worked out on a scratch copy of the object's values (decisions 18 and 25). */
interface Prepared {
  verb: string;
  /** The values before the verb, null for `create`. */
  old: Json | null;
  /** The proposed values: the row, the step's values, then what the before actions set. */
  proposed: Json;
  fills: { point: string; sets: Json }[];
  runs: TriggerRunJson[];
  warnings: string[];
  refused: { run: TriggerRunJson; name: string } | null;
  /** The points of the later moments that matched, in run order. */
  later: Map<Moment, { lifecycle: Json; point: Json }[]>;
  listed: boolean;
}

class Overflow extends Error {}

const emptyEvent = (): Json => ({ name: null, actor: null, payload: {} });
const newScope = (input: SimInputJson | null): Scope => ({
  echo: input,
  input,
  actor: null,
  event: emptyEvent(),
  refusal: null,
  microsteps: [],
  guards: [],
  audit: [],
  diagnostics: [],
  notes: [],
  refusedBy: null,
  rights: [],
  triggerRuns: [],
  queued: [],
  checkWarnings: [],
});

class Interpreter {
  readonly config = new Set<Node>();
  readonly history = new Map<string, Node[]>();
  context: Json = {};
  timers: Timer[] = [];
  pending: { invoke: Json; state: Node }[] = [];
  readonly signatures = new Map<string, Signature[]>();
  /** The approvals' progress by gated move, the record of acts and the step at which each state was entered (decision 28). */
  readonly approvals = new Map<string, Approval>();
  private acts: Act[] = [];
  private enteredAt = new Map<string, number>();
  /** The signature and actor the current step answers each gated move with, picked once (decision 8). */
  private picks = new Map<string, { sg: Json; actor: string | null }>();
  queue: { kind: string; id: string }[] = [];
  done = false;
  clock = Date.parse(DEFAULT_START);
  /** The run's participants and the actors from the object that stand for the subject's owner. */
  participants: Json[] = [];
  ownerActors: ReadonlySet<string> = new Set();
  membersActors: ReadonlyMap<string, string> = new Map();
  /** The cast of the run (decision 22); the mock does not evaluate guard conditions, so nothing reads it yet. */
  cast: { objects: Json[]; subject: string | null } = { objects: [], subject: null };
  /** The subject's row by attribute id: the attributes' defaults, the object's values, then what the actions set. */
  row: Json = {};
  attributesOf: (entity: string) => { id: string; name?: string; default?: unknown }[] = () => [];
  /** Named rights by id, and whether refusals name their element (a project that uses rights, or a step that expects it). */
  named: ReadonlyMap<string, Json> = new Map();
  rightsOn = false;
  /** A delete stopped the chart: later inputs are refused, timers do not fire (rights-design.md decision 25). */
  deleted = false;
  /** Whether the subject is frozen, and the links that are, as `end|object` (rights-design.md decisions 16 and 17). */
  frozenObject = false;
  frozenLinks = new Set<string>();
  /** The linked object the step in hand names, so a `whileFrozen` right with ends asks about that link's own flag. */
  /** Whether the event in hand is a thaw: its own move's sets carry no frozen note. */
  private thawing = false;
  private checkObject: string | null = null;
  subjectEntity: string | undefined;
  lifecycles: Json[] = [];
  /** Whether any lifecycle lists trigger points, and the entity and its bases (decision 12). */
  triggers = false;
  /** The named trigger points, which run for an object before every lifecycle's points (decision 34). */
  namedPoints: Json[] = [];
  stereotypeCovers: StereotypeCover = () => false;
  stereotypeCarriers: (key: string) => string[] = () => [];
  entityChain: (entity: string) => string[] = (e) => [e];
  private prep = new Map<Edge, { sign: Prepared | null; move: Prepared | null }>();
  private auditSeq = 0;
  private timerSeq = 0;
  private index = -1;
  private micro = 0;
  private scope: Scope = newScope(null);

  constructor(
    readonly chart: Chart,
    private readonly actorName: (id: string) => string | undefined = () => undefined,
    readonly membership: Membership = new Membership([]),
  ) {}

  leaves(): Node[] {
    return [...this.config].filter((s) => !s.isRoot && isLeaf(s)).sort((a, b) => a.order - b.order);
  }

  start(context: Json | undefined, at: number | null): StepTraceJson {
    if (at !== null) this.clock = at;
    for (const a of this.chart.attributes) this.context[String(a.id)] = a.default ?? null;
    for (const [id, v] of Object.entries(context ?? {})) if (this.chart.attributes.some((a) => a.id === id)) this.context[id] = v;
    this.scope = newScope(null);
    this.scope.actor = this.membership.systemActor;
    const before = { ...this.context };
    this.initRow();
    const rowBefore = { ...this.row };
    this.macrostep(() => {
      this.config.add(this.chart.root);
      if (this.chart.root.initialChild) this.microstep([], this.chart.root.initialChild);
      this.runToCompletion();
    });
    return this.finish(before, rowBefore);
  }

  /** The row of the subject: each attribute's default, then the subject object's values (the engine's row, decision 4). */
  private initRow() {
    const object = this.subjectObject();
    for (const a of this.attributesOf(str(object?.entity) ?? this.subjectEntity ?? "")) this.row[a.id] = a.default ?? null;
    for (const [k, v] of Object.entries((object?.values as Json | undefined) ?? {})) this.row[k] = v;
    this.frozenObject = object?.frozen === true;
    for (const l of arr(object?.links)) if (l.frozen === true) this.frozenLinks.add(`${String(l.end)}|${String(l.object)}`);
  }

  /** Whether a link through an end is frozen; `object` narrows it to one linked object, and a missing end asks about any link. */
  private linkFrozen(end: string | null, object?: string | null): boolean {
    return [...this.frozenLinks].some((k) => {
      const [e, o] = k.split("|");
      return (!end || e === end) && (!object || !o || o === object);
    });
  }

  /** Row 1 (decision 17): freezing refuses modify, connect, disconnect and delete before any right, and no element refused it. */
  private frozenRefused(verb: string | null, end: string | null, object: string | null): boolean {
    if (!verb || !FROZEN_VERBS.has(verb)) return false;
    let frozen: boolean;
    if (end) {
      if (verb === "modify") frozen = this.linkFrozen(end, object);
      else if (verb === "disconnect") frozen = this.frozenObject || this.linkFrozen(end, object);
      else frozen = verb === "connect" && this.frozenObject;
    } else if (verb === "disconnect") frozen = this.frozenObject || this.linkFrozen(null);
    else frozen = this.frozenObject;
    if (frozen) this.refuse("frozen");
    return frozen;
  }

  /** An accepted `freeze` or `thaw`, from a verb step or an event that names it (decision 16). */
  private applyFreeze(verb: "freeze" | "thaw", end: string | null, object: string | null) {
    const freeze = verb === "freeze";
    let was: boolean;
    if (end) {
      was = this.linkFrozen(end, object);
      const linked = arr(this.subjectObject()?.links)
        .filter((l) => l.end === end && (!object || l.object === object))
        .map((l) => `${end}|${String(l.object)}`);
      const keys = linked.length ? linked : [`${end}|${object ?? ""}`];
      for (const k of keys) {
        if (freeze) this.frozenLinks.add(k);
        else this.frozenLinks.delete(k);
      }
    } else {
      was = this.frozenObject;
      this.frozenObject = freeze;
    }
    const what = end ? "The link" : "The object";
    if (was === freeze)
      this.scope.notes.push(freeze ? `${what} was already frozen, so freezing it changed nothing.` : `${what} was not frozen, so thawing it changed nothing.`);
  }

  private subjectObject(): Json | undefined {
    return this.cast.objects.find((o) => o.id === this.cast.subject);
  }

  step(input: SimInputJson): StepTraceJson {
    this.index++;
    this.scope = newScope(input);
    const before = { ...this.context };
    const rowBefore = { ...this.row };
    input = this.withParticipant(input);
    this.scope.input = input;
    if (this.scope.actsAs) this.scope.echo = input;
    if (this.triggers) this.checkNamedAssume(input);
    if (input.input === "verb") this.handleVerb(input);
    else if (input.input === "time") this.handleTime(input);
    else if (input.input === "invoke-done" || input.input === "invoke-error") this.handleInvoke(input);
    else this.handleEvent(input);
    return this.finish(before, rowBefore);
  }

  // ---- participants ----

  /** Whether the participant satisfies the listed actor (decision 17): it is, or is a member of, the participant's actor or a
   * context-free holding; an actor from the object that stands for the owner is satisfied by the subject object's owner. */
  private satisfies(p: Json, listed: string): boolean {
    if (this.ownerActors.has(listed)) {
      const subject = this.cast.objects.find((o) => o.id === this.cast.subject);
      return !!subject && subject.owner === p.id;
    }
    const held = [
      str(p.actor),
      ...arr(p.holds)
        .filter((h) => !str(h.within))
        .map((h) => str(h.actor)),
    ];
    return held.some((a) => !!a && this.membership.accepts(listed, a));
  }

  /** Who may sign a gated move now: its signers, or on an approval the signers of the round's needed signatures, and nobody when nothing is needed (decision 14). */
  private allowSigners(t: Edge): string[] {
    const approval = this.approvalOf(t);
    if (!approval) return t.gate ? signersOf(t.gate) : [];
    if (approval.waiting) return [];
    return [
      ...new Set(
        gateSignatures(t.gate)
          .filter((sg) => approval.needed.has(String(sg.id)))
          .flatMap((sg) => ids(sg.signers)),
      ),
    ];
  }

  /** The list a participant's step is checked against when it names no move: the event's or the human task's actors. */
  private firstList(input: SimInputJson): string[] {
    if (input.input === "event") {
      const ev = input.event ? this.chart.events.get(input.event) : undefined;
      if (!ev) return [];
      const gated = this.eventMoves(input).find((t) => t.gate && this.allowSigners(t).length);
      return gated ? this.allowSigners(gated) : ids(ev.actors);
    }
    if (input.input === "invoke-done" || input.input === "invoke-error") {
      const found = this.pending.find((x) => x.invoke.id === input.invoke);
      return found && found.invoke.type === "human-task" ? ids(found.invoke.actors) : [];
    }
    return [];
  }

  private eventMoves(input: SimInputJson): Edge[] {
    return input.event ? activeTransitions(this).filter((t) => t.trigger === "event" && t.key === input.event) : [];
  }

  /** The actor a participant acts as, read from the moves the event can make now (decision 15); null when it satisfies none. */
  private actingActor(input: SimInputJson, p: Json): string | null {
    const fallback = str(p.actor) ?? null;
    const firstOf = (list: string[]) => (list.length ? (list.find((l) => this.satisfies(p, l)) ?? null) : fallback);
    if (input.input !== "event") return firstOf(this.firstList(input));
    const ev = input.event ? this.chart.events.get(input.event) : undefined;
    if (!ev) return fallback;
    const eventActors = ids(ev.actors);
    const moves = this.eventMoves(input);
    if (!moves.length) return firstOf(eventActors);
    for (const t of moves) {
      const approval = this.approvalOf(t);
      if (approval && !approval.waiting) {
        // The signature and the actor are picked together, within that signature's signers and required actors (decision 8).
        const signer = input.signer ?? this.identityOf(p);
        const actorFor = (sg: Json): string | null => {
          const counted = approval.counted.filter((c) => c.signature === sg.id);
          const required = ids(sg.requiredActors).find((r) => !counted.some((c) => this.membership.accepts(r, c.actor)) && this.satisfies(p, r));
          const mine = ids(sg.signers).filter((x) => this.satisfies(p, x));
          return required ?? mine.find((x) => !eventActors.length || this.membership.allows(eventActors, x)) ?? mine[0] ?? null;
        };
        const picked = this.pick(t, approval, input.response ?? "sign", signer, actorFor, input.signature);
        if ("sg" in picked && picked.actor) return picked.actor;
        continue;
      }
      const signers = t.gate ? this.allowSigners(t) : [];
      if (!t.gate || !signers.length) return firstOf(eventActors);
      const signed = this.signatures.get(t.id) ?? [];
      const required = ids(t.gate.requiredActors).find((a) => !signed.some((s) => this.membership.accepts(a, s.actor)) && this.satisfies(p, a));
      const mine = signers.filter((x) => this.satisfies(p, x));
      const found = required ?? mine.find((x) => !eventActors.length || this.membership.allows(eventActors, x)) ?? mine[0];
      if (found) return found;
    }
    return null;
  }

  /** The input with the actor the participant acts as and its identity as signer (decision 15); the trace carries `actsAs`. */
  private withParticipant(input: SimInputJson): SimInputJson {
    if (!input.participant) return input;
    const p = this.participants.find((x) => x.id === input.participant);
    if (!p) {
      this.problem("MQ9305", PARTICIPANT_TEXTS.unknown(input.participant));
      return input;
    }
    const first = this.firstList(input);
    let actsAs: string | null;
    if (input.actor) {
      actsAs = input.actor;
      if (!this.satisfies(p, input.actor)) {
        const listed = input.actor;
        const actor = this.actorName(listed) ?? listed;
        const within = arr(p.holds).find((h) => !!str(h.within) && !this.ownerActors.has(listed) && this.membership.accepts(listed, str(h.actor) ?? ""));
        const heldRole = within ? str(within.actor) : undefined;
        const role = heldRole ? (this.actorName(heldRole) ?? heldRole) : "";
        const context = within ? String(this.cast.objects.find((o) => o.id === within.within)?.name ?? within.within) : "";
        this.problem(
          "MQ9305",
          within && heldRole
            ? PARTICIPANT_TEXTS.heldOnlyWithin(String(p.name), role, context, actor, this.membersActors.get(heldRole))
            : PARTICIPANT_TEXTS.actorNotSatisfied(String(p.name), actor),
        );
      }
    } else actsAs = this.actingActor(input, p);
    this.scope.actsAs = actsAs;
    this.scope.list = first;
    return { ...input, actor: actsAs, signer: input.signer ?? this.identityOf(p) };
  }

  private identityOf(p: Json): string {
    return p.identity !== undefined && p.identity !== null ? String(p.identity) : String(p.name);
  }

  // ---- inputs ----

  private handleEvent(input: SimInputJson) {
    if (this.done || this.deleted) return this.refuse("no-transition");
    const ev = input.event ? this.chart.events.get(input.event) : undefined;
    if (!ev) {
      this.problem("MQ9305", `The event '${input.event}' is not an event of process '${String(this.chart.doc.name)}'; pick one of its events.`);
      return this.refuse("no-transition");
    }
    if (this.approvalFieldsProblem(input, ev)) return this.refuse("no-transition");
    if (this.frozenRefused(str(ev.verb), null, null)) return;
    const actors = ids(ev.actors);
    if (actors.length && (!input.actor || !this.membership.allows(actors, input.actor))) return this.refuse("actor", String(ev.id));
    // The event's list is an allow of its own lifecycle, and so are the signers of the gates on the moves it can make (decision 14): the acting actor only, never the participant's holdings.
    const closed = [
      ...(actors.length ? [String(ev.id)] : []),
      ...this.eventMoves(input).flatMap((t) =>
        t.gate && !!input.actor && this.allowSigners(t).some((x) => this.membership.accepts(x, input.actor!)) ? [String(t.gate.id)] : [],
      ),
    ];
    if (this.checkRights(str(ev.verb), closed)) return;
    this.scope.event = this.eventObject(String(ev.name), input.actor, arr(ev.payload), input.payload);
    this.thawing = ev.verb === "thaw";
    this.picks.clear();
    this.macrostep(() => {
      const selection = this.select("event", String(ev.id), true);
      if (!selection.transitions.length && !selection.consumed) {
        if (selection.run) this.scope.triggerRuns.push(selection.run);
        return this.refuse(selection.reason, selection.by);
      }
      // The people who did a move: whoever sent it (a gated move is done by those whose signatures counted, recorded when it completes).
      for (const t of selection.transitions) if (!t.gate) this.act(t.verb, t.id);
      if (selection.transitions.length) this.microstep(selection.transitions);
      this.runToCompletion();
    });
    this.thawing = false;
    // Naming `freeze` or `thaw` is the opt-in: the accepted event freezes or thaws the subject.
    if (this.scope.refusal === null && (ev.verb === "freeze" || ev.verb === "thaw")) this.applyFreeze(ev.verb, null, null);
  }

  /** MQ9305 on a step's response and signature (decision 25), judged before the step runs: only an event step gated by an approval takes them. */
  private approvalFieldsProblem(input: SimInputJson, ev: Json): boolean {
    const response = input.response ?? "sign";
    if (response === "sign" && !input.signature) return false;
    const approvals = this.chart.transitions.filter((t) => t.trigger === "event" && t.key === input.event && approvalStages(t.gate)).map((t) => t.gate!);
    const name = `event '${String(ev.name)}'`;
    if (!approvals.length && response !== "sign") {
      this.problem(
        "MQ9305",
        `The step gives response '${response}', but only an event step whose event is gated by an approval takes a response; remove it, or give the gate stages.`,
      );
      return true;
    }
    if (!approvals.length && input.signature) {
      this.problem(
        "MQ9305",
        "The step names a signature, but only an event step whose event is gated by an approval answers one; remove it, or give the gate stages.",
      );
      return true;
    }
    if (input.signature && !approvals.some((g) => gateSignatures(g).some((sg) => sg.id === input.signature))) {
      this.problem(
        "MQ9305",
        `The signature '${input.signature}' is not a named signature of an approval on ${name}; pick one of its signatures, or remove it.`,
      );
      return true;
    }
    return false;
  }

  private handleTime(input: SimInputJson) {
    const span = durationMs(input.after);
    if (span === null || span <= 0) {
      this.problem("MQ9305", `A time step needs a positive ISO 8601 duration in after (for example P5D), not '${input.after}'.`);
      return this.refuse("no-transition");
    }
    const end = this.clock + span;
    if (this.deleted) {
      this.clock = end;
      return;
    }
    if (!input.actor) this.scope.actor = this.membership.systemActor;
    this.scope.event = this.eventObject(null, input.actor, [], {});
    while (!this.done) {
      const next = this.timers.filter((t) => t.due <= end).sort(timerOrder)[0];
      if (!next) break;
      this.clock = next.due;
      this.timers = this.timers.filter((t) => t !== next);
      this.macrostep(() => {
        if (this.guard(next.edge) !== true) return;
        const refusing = this.triggerRefusal(next.edge, false);
        if (refusing) return this.leftOut(refusing);
        this.microstep([next.edge]);
        this.runToCompletion();
      });
    }
    this.clock = end;
  }

  private handleInvoke(input: SimInputJson) {
    const found = this.pending.find((p) => p.invoke.id === input.invoke);
    if (this.deleted) return this.refuse("no-transition");
    if (!found) {
      const name = input.invoke ? (this.chart.invokes.get(input.invoke)?.invoke.name ?? input.invoke) : input.invoke;
      this.problem(
        "MQ9305",
        `The invoke '${String(name)}' is not pending (its state is not active, or it already completed); complete an invoke of an active state.`,
      );
      return this.refuse("no-transition");
    }
    const actors = ids(found.invoke.actors);
    if (found.invoke.type === "human-task" && actors.length && (!input.actor || !this.membership.allows(actors, input.actor)))
      return this.refuse("actor", String(found.invoke.id));
    // A human task's completion, and its error, are checked as `complete` (decisions 14 and 23); other invokes are not checked.
    if (found.invoke.type === "human-task" && this.checkRights("complete", actors.length ? [String(found.invoke.id)] : [])) return;
    if (found.invoke.type !== "human-task" && !input.actor) this.scope.actor = this.membership.systemActor;
    this.scope.event = this.eventObject(null, input.actor, [], {});
    // A human task's end is the verb `complete`: its before actions and checks run first, and a refusing check leaves the task pending (section 8).
    let completion: Prepared | null = null;
    if (this.triggers && found.invoke.type === "human-task") {
      completion = this.prepare("complete", this.placeFor("complete", {}), { ...this.row }, { ...this.row }, false);
      if (completion.refused) {
        this.scope.triggerRuns.push(completion.refused.run);
        return this.refuse("check", completion.refused.run.triggerPoint);
      }
    }
    this.macrostep(() => {
      const selection = this.selectAt(found.state, input.input, String(found.invoke.id));
      if (!selection.transitions.length) {
        if (selection.run) this.scope.triggerRuns.push(selection.run);
        return this.refuse(selection.reason, selection.by);
      }
      if (completion) {
        this.list(completion, new Set());
        this.write(completion, new Set());
      }
      this.pending = this.pending.filter((p) => p.invoke !== found.invoke);
      if (completion) {
        this.runLater(completion, "with-change", new Set());
        this.runLater(completion, "after-commit", new Set());
      }
      // A person who ends a human task did `complete` and its move (decision 28); another invoke's end is nobody's.
      if (found.invoke.type === "human-task") {
        this.act("complete", null);
        for (const t of selection.transitions) this.act(t.verb, t.id);
      }
      this.microstep(selection.transitions);
      this.runToCompletion();
    });
  }

  // ---- rights (rights-design.md decisions 9 to 14 and 25) ----

  /** Whether the caller satisfies a listed actor: the participant holds it, or the step's actor is it or a member of it. */
  private callerSatisfies(listed: string): Tri {
    const input = this.scope.input;
    if (!input) return false;
    const p = input.participant ? this.participants.find((x) => x.id === input.participant) : undefined;
    if (p) return this.satisfies(p, listed);
    return input.actor ? this.membership.accepts(listed, input.actor) : false;
  }

  /** The running process's own lifecycle document (the draft being run), or null when it is an orchestration. */
  private actingLifecycle(): Json | null {
    return this.chart.doc.use === "lifecycle" ? this.chart.doc : null;
  }

  /** The states active in a lifecycle with their ancestors: the running chart, or the object's states, else the initial ones. */
  private activeOf(proc: Json, create: boolean): Set<string> {
    if (proc.id === this.chart.doc.id && !create) return new Set([...this.config].map((n) => n.id));
    const active = new Set<string>();
    const object = create ? undefined : this.subjectObject();
    const own = object ? ids(object.states).filter((x) => stateIds(proc.states).has(x)) : [];
    if (own.length) for (const x of own) enterState(proc.states, x, active);
    else {
      enterInitial(proc.states, str(proc.initial), active);
      if (!create)
        this.scope.notes.push(
          object
            ? `${String(object.name)} gives no state in ${String(proc.name)}, so its initial state is assumed.`
            : `No object gives its state in ${String(proc.name)}, so its initial state is assumed.`,
        );
    }
    return active;
  }

  private factsFor(l: Json, verb: string | null, closed: string[], end: string | null, create: boolean, attributes: string[] | null = null): Facts {
    return {
      named: this.named,
      satisfies: (listed) => this.callerSatisfies(listed),
      verb,
      end,
      active: this.activeOf(l, create),
      condition: (tree) => evalTree(tree, this.env()) === true,
      closed: l.id === this.chart.doc.id ? closed : [],
      attributes,
      frozenObject: this.frozenObject,
      frozenLink: (e) => this.linkFrozen(e, this.checkObject),
    };
  }

  /** The lifecycles that answer for a verb, in check order: the acting one first, then the others by name (decision 10). */
  private answering(verb: string | null): Json[] {
    const running = this.chart.doc;
    const acting = this.actingLifecycle();
    if (verb && ownLifecycleOnly(verb)) return acting ? [acting] : [];
    if (!verb) return acting ? [acting] : [];
    const object = this.subjectObject();
    const entity = str(object?.entity) ?? this.subjectEntity;
    const wanted = object ? (entity ? [entity] : []) : subjectsOf(running).flatMap((s) => (s.entity ? [s.entity] : []));
    const governs = (l: Json) =>
      wanted.length > 0 && (object ? subjectsOf(l).some((s) => s.entity === wanted[0]) : wanted.every((e) => subjectsOf(l).some((s) => s.entity === e)));
    const others = this.lifecycles
      .filter((l) => l.id !== running.id && l.use === "lifecycle" && governs(l))
      .sort((a, b) => (String(a.name) < String(b.name) ? -1 : String(a.name) > String(b.name) ? 1 : 0));
    return [...(acting ? [acting] : []), ...others];
  }

  /** Rows 3 and 4: the denies, then the defaults. True when refused; the trace then names the right or the lifecycle. */
  private checkRights(
    verb: string | null,
    closed: string[],
    end: string | null = null,
    create = false,
    attributes: string[] | null = null,
    object: string | null = null,
  ): boolean {
    const lifecycles = this.answering(verb);
    if (!lifecycles.some(takesPart)) return false;
    this.checkObject = object;
    const result = checkAll(lifecycles, (l) => this.factsFor(l, verb, closed, end, create, attributes));
    this.scope.rights.push(...result.rights);
    if (!result.refused) return false;
    this.refuse(result.refused.refusal, result.refused.by);
    return true;
  }

  /** A verb step (decision 25): rights first, then what the run does with it. */
  private handleVerb(input: SimInputJson) {
    if (this.done || this.deleted) return this.refuse("no-transition");
    const verb = input.verb ?? null;
    const group = builtInVerbs.find((v) => v.key === verb)?.group;
    if (!verb || group === "move" || group === "signature" || group === "task") {
      this.problem(
        "MQ9305",
        verb
          ? `A verb step does an object, link or own verb, and '${verb}' is not one the run can do yet; moves, signatures and tasks have their own steps. Pick another verb.`
          : "A verb step needs a verb; pick one.",
      );
      return this.refuse("no-transition");
    }
    if (this.frozenRefused(verb, input.end ?? null, input.object ?? null)) return;
    const object = this.subjectObject();
    const entity = str(object?.entity) ?? this.subjectEntity ?? "";
    // The attributes a read or modify names, as ids: each is checked on its own (decision 21). None named: not known, so it fails closed.
    const known = this.attributesOf(entity);
    const toId = (k: string) => known.find((a) => a.id === k)?.id ?? known.find((a) => a.name === k)?.id ?? k;
    const named = verb === "read" ? (input.attributes ?? []) : verb === "modify" && !input.end ? Object.keys(input.values ?? {}) : [];
    if (this.checkRights(verb, [], input.end ?? null, verb === "create", named.length ? named.map(toId) : null, input.object ?? null)) return;
    // The values a modify (or a create, decision 27) step gives, by attribute id.
    const values: Json = {};
    if ((verb === "modify" && !input.end) || verb === "create") {
      const attributes = this.attributesOf(entity);
      for (const [key, value] of Object.entries(input.values ?? {})) {
        const hit = attributes.find((a) => a.id === key) ?? attributes.find((a) => a.name === key);
        if (!hit && attributes.length) {
          this.problem("MQ9305", `A ${verb} step sets attributes of the subject, and '${key}' is none of them; pick one of its attributes.`);
          continue;
        }
        values[hit?.id ?? key] = value;
      }
    }
    // Rows 8 and 9 after rights and freezing (decision 18): matching reads what the step itself names, never what before actions fill.
    const prepared = this.triggers
      ? this.prepare(
          verb,
          this.placeFor(verb, { attributes: named.length ? named.map(toId) : null, end: input.end ?? null, create: verb === "create" }),
          { ...this.row, ...values },
          verb === "create" ? null : { ...this.row },
          true,
        )
      : null;
    if (prepared?.refused) {
      this.scope.triggerRuns.push(prepared.refused.run);
      return this.refuse("check", prepared.refused.run.triggerPoint);
    }
    const seen = new Set<string>();
    if (prepared) {
      this.list(prepared, seen);
      // The proposed values are written before the effect once the step is accepted; copy and revise make a new object the run does not hold.
      if (verb !== "copy" && verb !== "revise") this.write(prepared, new Set(), values);
    } else Object.assign(this.row, values);
    // A replace stands in for the effect of a verb step only (decision 16); an event that names the verb keeps its move.
    const replaced = prepared ? this.replaceEffect(prepared, verb, !!input.end) : false;
    if (replaced) {
      // The built-in effect did not run.
    } else if (verb === "freeze" || verb === "thaw") this.applyFreeze(verb, input.end ?? null, input.object ?? null);
    else if (verb === "modify" && !input.end) {
      // The values are written above.
    } else if (verb === "delete" && !input.end) {
      this.deleted = true;
      this.scope.notes.push("The object was deleted, so the run stopped; the outcome is read from the chart as it stood.");
    } else if (!(verb === "create" && (prepared || Object.keys(values).length))) this.scope.notes.push(`The run checked ${verb} but did not apply it.`);
    if (prepared) {
      this.runLater(prepared, "with-change", seen, { gone: this.deleted && !replaced });
      this.runLater(prepared, "after-commit", seen);
    }
    this.act(verb, null);
  }

  // ---- trigger points (trigger-points-design.md decisions 10 to 28) ----

  /** The lifecycles whose points a verb reaches, acting one first, then the others by name (decisions 11 and 19). */
  private lifecyclesFor(verb: string): Json[] {
    const acting = this.actingLifecycle();
    return ownLifecycleOnly(verb) ? (acting ? [acting] : []) : this.answering(verb);
  }

  /** The named trigger points that cover the object, in decision 34's order. */
  private namedHere(): Json[] {
    return namedFor(this.namedPoints, this.entityChains(), this.stereotypeCovers);
  }

  /** The entity of the object, with its bases: one chain, or one for each subject entity of a run with no object. */
  private entityChains(): string[][] {
    const object = this.subjectObject();
    const entity = str(object?.entity) ?? (object ? undefined : this.subjectEntity);
    if (entity) return [this.entityChain(entity)];
    // With no object, each subject is one chain: an entity subject's own, and every carrier of a stereotype subject.
    return subjectsOf(this.chart.doc).flatMap((x) =>
      x.entity ? [this.entityChain(x.entity)] : x.stereotype ? this.stereotypeCarriers(x.stereotype).map((e) => this.entityChain(e)) : [],
    );
  }

  /**
   * The named points a verb reaches. A move, signature or task verb reaches them only inside the lifecycle that owns the move, for a
   * covered type, never in an orchestration (trigger-points-design.md decision 34, A5); the other verbs reach them wherever they happen.
   */
  private namedFor(verb: string): Json[] {
    if (ownLifecycleOnly(verb) && this.chart.doc.use !== "lifecycle") return [];
    return this.namedHere();
  }

  /**
   * MQ9305 on a step's assume words for named checks: a word names a named check that covers the object's entity, whatever the verb (with no
   * object, one that covers every subject), and has no condition. Lifecycle checks are not tested by the mock, as before.
   */
  private checkNamedAssume(input: SimInputJson) {
    const named = new Map(this.namedPoints.map((t) => [String(t.id), t]));
    const reached = new Set(this.namedHere().map((t) => String(t.id)));
    for (const [key, value] of Object.entries(input.assume)) {
      const point = named.get(key);
      if (typeof value !== "string" || !point) continue;
      if (point.type === "check" && isTeamCode(point) && reached.has(key)) continue;
      this.problem(
        "MQ9305",
        `assume gives ${value} for '${String(point.name)}', which is not a check the team writes of a lifecycle this step reaches; give true or false for a guard, or name such a check.`,
      );
    }
  }

  /** The states a move enters, its targets and what they enter by default, with their ancestors. */
  private enteredIds(t: Edge): Set<string> {
    const set = new Set<Node>();
    for (const s of t.targets) this.addDescendants(s, set);
    for (const s of this.effectiveTargets(t)) this.addAncestors(s, this.domain(t), set);
    return new Set([...set].map((n) => n.id));
  }

  private placeFor(verb: string, o: { edge?: Edge; attributes?: string[] | null; end?: string | null; create?: boolean }): Place {
    const acting = this.chart.doc;
    const edge = o.edge;
    return {
      verb,
      acting: String(acting.id),
      ...(edge ? { transition: edge.id, targets: this.enteredIds(edge) } : {}),
      // For a move, the source of the acting lifecycle; for anything else the object's states in the lifecycle.
      statesOf: (l) =>
        edge && l.id === acting.id
          ? new Set(
              [edge.source, ...ancestorsOf(edge.source), ...[...this.config].filter((n) => isDescendantOf(n, edge.source))]
                .filter((n) => !n.isRoot)
                .map((n) => n.id),
            )
          : this.activeOf(l, o.create === true),
      entities: this.entityChains(),
      attributes: o.attributes ?? null,
      end: o.end ?? null,
    };
  }

  private subjectEnv(proposed: Json, old: Json | null): EvalEnv {
    return this.env({ now: proposed, old });
  }

  /** A point's `when`: true runs it, false skips it; one the mock cannot read runs it, with a note. */
  private whenHolds(t: Json, env: EvalEnv): boolean {
    if (t.when === undefined) return true;
    const v = evalTree(t.when, env);
    if (v === UNKNOWN) {
      this.scope.notes.push(
        `The mock backend could not read the condition of trigger point '${String(t.name)}', so it ran; the engine reads every expression.`,
      );
      return true;
    }
    return v === true;
  }

  private runOf(l: Json, t: Json, verb: string, outcome: TriggerRunJson["outcome"], extra: Partial<TriggerRunJson> = {}): TriggerRunJson {
    return {
      triggerPoint: String(t.id),
      // A named trigger point stands in for its lifecycle here, and belongs to none.
      ...(l.kind === "trigger-point" ? {} : { lifecycle: String(l.id) }),
      verb,
      type: t.type as TriggerRunJson["type"],
      ...(t.type === "action" ? { timing: t.timing as TriggerRunJson["timing"] } : {}),
      outcome,
      ...(t.outside === true ? { outside: true as const } : {}),
      ...extra,
    };
  }

  /** The sets of a point, every value worked out first and then all applied (object effects decision 4); the values it set, by attribute id. */
  private applySets(t: Json, env: EvalEnv, into: Json): Json {
    const worked = arr(t.sets).map((x) => ({ attribute: String(x.attribute), value: evalTree(x.value, env) }));
    const out: Json = {};
    for (const w of worked) {
      if (w.value === UNKNOWN) {
        this.scope.notes.push(
          `The mock backend could not work out the value of a set in trigger point '${String(t.name)}', so the set did nothing; the engine reads every expression.`,
        );
        continue;
      }
      into[w.attribute] = w.value;
      out[w.attribute] = w.value;
    }
    return out;
  }

  /**
   * Rows 8 and 9 of decision 18 on a scratch copy: the before actions fill the proposed values, then the checks run. The first refusing
   * check ends it. A team-code check takes its outcome from the step's `assume`, else passes.
   */
  private prepare(verb: string, place: Place, start: Json, old: Json | null, verbStep: boolean): Prepared {
    const found: { l: Json; t: Json; moment: Moment }[] = [];
    // Named points run before the lifecycles' at each moment (decision 34): the list is in that order, and each moment keeps it.
    for (const t of this.namedFor(verb)) {
      const m = matchPoint(t, t, place);
      if (!m.ok) continue;
      found.push({ l: t, t, moment: momentOf(t) });
      if (m.assumed)
        this.scope.notes.push(`The step names no ${strsOf(t.ends).length ? "link end" : "attribute"}, so the check '${String(t.name)}' ran as if it applied.`);
    }
    for (const l of this.lifecyclesFor(verb))
      for (const t of pointsOf(l)) {
        const m = matchPoint(t, l, place);
        if (!m.ok) continue;
        found.push({ l, t, moment: momentOf(t) });
        if (m.assumed)
          this.scope.notes.push(
            `The step names no ${strsOf(t.ends).length ? "link end" : "attribute"}, so the check '${String(t.name)}' ran as if it applied.`,
          );
      }
    // A move keeps its own effect (decision 16): a replace on the verb does not run, and a note says so. The sets of with-change actions
    // still run on delete (the object stays), so they are not skipped here.
    if (!verbStep)
      for (const f of found.filter((x) => x.moment === "effect")) {
        const note = `An event that names '${verb}' still does its move, so replace '${String(f.t.name)}' does not run.`;
        if (!this.scope.notes.includes(note)) this.scope.notes.push(note);
      }
    const proposed = { ...start };
    const prepared: Prepared = { verb, old, proposed, fills: [], runs: [], warnings: [], refused: null, later: new Map(), listed: false };
    const env = () => this.subjectEnv(proposed, old);
    for (const { l, t } of found.filter((f) => f.moment === "before")) {
      if (!this.whenHolds(t, env())) {
        prepared.runs.push(this.runOf(l, t, verb, "skipped"));
        continue;
      }
      const sets = this.applySets(t, env(), proposed);
      if (Object.keys(sets).length) prepared.fills.push({ point: String(t.id), sets });
      prepared.runs.push(this.runOf(l, t, verb, "ran", Object.keys(sets).length ? { sets: sorted(sets) } : {}));
    }
    const assume = (this.scope.input?.assume ?? {}) as Record<string, unknown>;
    for (const { l, t } of found.filter((f) => f.moment === "checks")) {
      if (!this.whenHolds(t, env())) {
        prepared.runs.push(this.runOf(l, t, verb, "skipped"));
        continue;
      }
      const warning = t.severity === "warning";
      let outcome: TriggerRunJson["outcome"] = "passed";
      let assumed = false;
      if (t.condition !== undefined) {
        const v = evalTree(t.condition, env());
        if (v === UNKNOWN)
          this.scope.notes.push(`The mock backend could not read the condition of check '${String(t.name)}', so it passed; the engine reads every expression.`);
        else if (v !== true) outcome = warning ? "warned" : "refused";
      } else {
        // A check the team writes: the word the step assumes. A warning check never refuses, so its "error" counts as a warning.
        const word = assumedWord(assume, t);
        assumed = true;
        outcome = word === "warning" ? "warned" : word === "error" ? (warning ? "warned" : "refused") : "passed";
      }
      const run = this.runOf(l, t, verb, outcome, assumed ? { assumed: true as const } : {});
      prepared.runs.push(run);
      if (outcome === "warned") prepared.warnings.push(String(t.id));
      if (outcome === "refused") {
        prepared.refused = { run, name: String(t.name) };
        break;
      }
    }
    // Only a verb step has a replace in its effect; a move keeps its own (decision 16).
    for (const moment of ["effect", "with-change", "after-commit"] as const) {
      if (moment === "effect" && !verbStep) continue;
      prepared.later.set(
        moment,
        found.filter((f) => f.moment === moment).map(({ l, t }) => ({ lifecycle: l, point: t })),
      );
    }
    return prepared;
  }

  /** The first refusing check of a candidate move, or null; it keeps the prepared rows for the microstep (rows 8 and 9 on a scratch copy). */
  private triggerRefusal(t: Edge, external: boolean): { run: TriggerRunJson; name: string } | null {
    this.prep.delete(t);
    if (!this.triggers) return null;
    const gated = !!t.gate && external;
    if (!t.verb && !gated) return null;
    const base = { ...this.row };
    let sign: Prepared | null = null;
    let move: Prepared | null = null;
    let refused: { run: TriggerRunJson; name: string } | null = null;
    // On an approval the response's verb runs its points where sign's run (decision 17); a waiting approval makes no response.
    const approval = gated ? this.approvalOf(t) : null;
    const waiting = !!approval && approval.waiting;
    const responseVerb: Response = approval && !waiting ? (this.scope.input?.response ?? "sign") : "sign";
    if (gated && !waiting) {
      sign = this.prepare(responseVerb, this.placeFor(responseVerb, { edge: t }), base, base, false);
      refused = sign.refused;
    }
    // A gated move's own points run only on the occurrence that completes the gate, and after sign's (decision 18).
    if (!refused && t.verb && (!t.gate || (external && this.wouldComplete(t)))) {
      move = this.prepare(t.verb, this.placeFor(t.verb, { edge: t }), sign ? sign.proposed : base, base, false);
      refused = move.refused;
    }
    if (refused) {
      // A check that refuses the signature completing a gate leaves it uncounted, audited as refused.
      if (gated && t.gate) {
        const input = this.scope.input!;
        this.audit(t.gate, t, input.signer, input.actor, meaningOf(input, t.gate), input.reason, "refused", auditAttributes(input, t.gate), this.namedOf(t));
      }
      return refused;
    }
    this.prep.set(t, { sign, move });
    return null;
  }

  /** Whether the next signature, by whoever signs, brings the gate to its count; who signs may still leave a required actor missing. */
  private couldComplete(t: Edge): boolean {
    const approval = this.approvalOf(t);
    if (approval) {
      const left = gateSignatures(t.gate)
        .filter((sg) => approval.needed.has(String(sg.id)) && !this.sigComplete(sg, approval.counted))
        .reduce((n, sg) => n + Math.max(1, num(sg.required) ?? 1) - approval.counted.filter((c) => c.signature === sg.id).length, 0);
      return approval.waiting || left <= 1;
    }
    const required = Math.max(1, typeof t.gate!.required === "number" ? t.gate!.required : 1);
    return (this.signatures.get(t.id)?.length ?? 0) + 1 >= required;
  }

  /** Whether this occurrence would complete the gate, worked out without counting it. */
  private wouldComplete(t: Edge): boolean {
    const input = this.scope.input!;
    const approval = this.approvalOf(t);
    if (approval) {
      if (approval.waiting) return true;
      const pick = this.picks.get(t.id);
      if ((input.response ?? "sign") !== "sign" || !pick) return false;
      return this.roundDone(t, approval, [
        ...approval.counted,
        { signer: input.signer!, actor: input.actor!, meaning: null, reason: null, signature: String(pick.sg.id) },
      ]);
    }
    const signatures = [...(this.signatures.get(t.id) ?? [])];
    signatures.push({ signer: input.signer!, actor: input.actor!, meaning: meaningOf(input, t.gate!), reason: input.reason });
    return gateSatisfied(t.gate!, signatures, this.membership);
  }

  /** An automatic move a check left out: the step that led to it stays accepted, and the trace names the check (decision 18). */
  private leftOut(refused: { run: TriggerRunJson; name: string }) {
    // The step stays accepted, so the check is named in a note and not listed as a refused run (decision 18); one note for each pass.
    const note = `Check '${refused.name}' refused an automatic move, so it was not taken; the step that led to it is still accepted.`;
    if (!this.scope.notes.includes(note)) this.scope.notes.push(note);
  }

  private warn(ids: string[]) {
    for (const id of ids) if (!this.scope.checkWarnings.includes(id)) this.scope.checkWarnings.push(id);
  }

  /** Lists the before actions and checks of a candidate that was taken, once per point (decision 18), with its warnings. */
  private list(p: Prepared, seen: Set<string>) {
    if (p.listed) return;
    p.listed = true;
    for (const r of p.runs) {
      if (seen.has(r.triggerPoint)) continue;
      seen.add(r.triggerPoint);
      this.scope.triggerRuns.push(r);
      if (r.outcome === "warned") this.warn([r.triggerPoint]);
    }
  }

  /** Writes the proposed values (before actions' sets, over `extra`, the step's own values) once the step is accepted; a point once per microstep. */
  private write(p: Prepared, seen: Set<string>, extra: Json = {}) {
    Object.assign(this.row, extra);
    const sets: Json = {};
    for (const f of p.fills) {
      if (seen.has(f.point)) continue;
      seen.add(f.point);
      Object.assign(sets, f.sets);
    }
    this.setRow(sets);
  }

  /** What a trigger point sets is part of its verb: freezing never stops it (decision 25). */
  private setRow(sets: Json) {
    if (!Object.keys(sets).length) return;
    Object.assign(this.row, sets);
    if (this.frozenObject && !this.thawing)
      this.scope.notes.push("The object is frozen, and a trigger point's sets were applied anyway; freezing does not stop what a trigger point sets.");
  }

  /** A replace on a verb step takes the place of its effect; at most one applies. True when it did (decision 16). */
  private replaceEffect(p: Prepared, verb: string, link: boolean): boolean {
    const entry = (p.later.get("effect") ?? [])[0];
    if (!entry || !["delete", "disconnect", "copy", "revise"].includes(verb)) return false;
    const env = this.subjectEnv({ ...this.row }, p.old);
    if (!this.whenHolds(entry.point, env)) {
      this.scope.triggerRuns.push(this.runOf(entry.lifecycle, entry.point, verb, "skipped"));
      return false;
    }
    // Copy and revise make a new object the run does not hold: the trace shows the sets and nothing is written.
    const scratch: Json = {};
    const sets = this.applySets(entry.point, env, scratch);
    if (verb !== "copy" && verb !== "revise") this.setRow(sets);
    void link;
    this.scope.triggerRuns.push(this.runOf(entry.lifecycle, entry.point, verb, "replaced", Object.keys(sets).length ? { sets: sorted(sets) } : {}));
    return true;
  }

  /** Rows 11 and 12: with-the-change actions run now, after-commit ones are queued and listed last. `gone`: the object was deleted. */
  private runLater(p: Prepared, moment: "with-change" | "after-commit", seen: Set<string>, o: { gone?: boolean } = {}) {
    for (const { lifecycle, point } of p.later.get(moment) ?? []) {
      const key = `${moment}:${String(point.id)}`;
      if (seen.has(key)) continue;
      seen.add(key);
      const env = this.subjectEnv({ ...this.row }, p.old);
      if (!this.whenHolds(point, env)) {
        this.scope.triggerRuns.push(this.runOf(lifecycle, point, p.verb, "skipped"));
        continue;
      }
      if (moment === "after-commit") {
        this.scope.queued.push(this.runOf(lifecycle, point, p.verb, "queued"));
        continue;
      }
      if (o.gone && arr(point.sets).length) {
        // A deleted object holds nothing to set (decision 16): without a replace the sets are skipped.
        this.scope.notes.push(`The sets of trigger point '${String(point.name)}' were skipped: a deleted object holds none.`);
        this.scope.triggerRuns.push(this.runOf(lifecycle, point, p.verb, "skipped"));
        continue;
      }
      const sets = this.applySets(point, env, {});
      this.setRow(sets);
      this.scope.triggerRuns.push(this.runOf(lifecycle, point, p.verb, "ran", Object.keys(sets).length ? { sets: sorted(sets) } : {}));
    }
  }

  /** The team-code checks the candidates reach, whose outcome the input takes from `assume` (the trigger's `checksUnknown`). */
  checksUnknown(edges: Edge[]): string[] {
    if (!this.triggers) return [];
    const out = new Set<string>();
    for (const t of edges)
      // On a gated move its own checks run only on the signature that brings the gate to its count (decision 18).
      // An open approval takes every response; one that needs nothing moves as a move without a gate (decision 17).
      for (const verb of [
        ...(t.gate && !this.waitingApproval(t) ? (this.approvalOf(t) ? RESPONSES : ["sign"]) : []),
        !t.gate || this.waitingApproval(t) || this.couldComplete(t) ? t.verb : null,
      ]) {
        if (!verb) continue;
        const place = this.placeFor(verb, { edge: t });
        for (const p of this.namedFor(verb)) if (p.type === "check" && isTeamCode(p) && matchPoint(p, p, place).ok) out.add(String(p.id));
        for (const l of this.lifecyclesFor(verb))
          for (const p of pointsOf(l)) if (p.type === "check" && isTeamCode(p) && matchPoint(p, l, place).ok) out.add(String(p.id));
      }
    return [...out];
  }

  // ---- macrostep ----

  private macrostep(body: () => void) {
    this.micro = 0;
    try {
      body();
    } catch (e) {
      if (!(e instanceof Overflow)) throw e;
      this.problem(
        "MQ9507",
        `A macrostep of process '${String(this.chart.doc.name)}' ran more than ${MAX_MICROSTEPS} microsteps; break the eventless loop or the chain of raised events with a guard that turns false.`,
      );
    }
  }

  private runToCompletion() {
    while (!this.done) {
      const always = this.select("always", "", false);
      if (always.transitions.length) {
        this.microstep(always.transitions);
        continue;
      }
      const next = this.queue.shift();
      if (!next) break;
      const saved = this.scope.event;
      let transitions: Edge[] = [];
      if (next.kind === "event") {
        const ev = this.chart.events.get(next.id);
        this.scope.event = this.eventObject(ev ? String(ev.name) : null, null, [], {});
        transitions = this.select("event", next.id, false).transitions;
      } else {
        const state = this.chart.byId.get(next.id);
        transitions = state && this.config.has(state) ? this.selectAt(state, "done", "").transitions : [];
      }
      if (transitions.length) this.microstep(transitions);
      this.scope.event = saved;
    }
  }

  // ---- selection ----

  private select(
    trigger: string,
    key: string,
    external: boolean,
  ): { transitions: Edge[]; consumed: boolean; reason: string; by?: string; run?: TriggerRunJson } {
    const enabled: Edge[] = [];
    const tried = new Map<Edge, boolean>();
    let consumed = false;
    let reason = "no-transition";
    let by: string | undefined;
    let run: TriggerRunJson | undefined;
    let rank = 0;
    const fail = (why: string, level: number, element?: string, refusing?: TriggerRunJson) => {
      if (level > rank) {
        rank = level;
        reason = why;
        by = element;
        run = refusing;
      }
    };
    for (const leaf of this.leaves()) {
      let found = false;
      for (const s of [leaf, ...ancestorsOf(leaf)]) {
        if (s.isRoot) break;
        for (const t of this.chart.candidates(s, trigger, key)) {
          const was = tried.get(t);
          if (was !== undefined) {
            if (was) {
              found = true;
              break;
            }
            continue;
          }
          tried.set(t, false);
          if (this.guard(t) !== true) {
            fail("guard", 1, t.guard ? String(t.guard.id) : undefined);
            continue;
          }
          if (t.gate) {
            if (!external) {
              fail("gate-signer", 2, String(t.gate.id));
              continue;
            }
            // A response other than sign acts only on an open approval (decision 9): anything else has no transition for it.
            const approval = this.approvalOf(t);
            const response = this.scope.input?.response ?? "sign";
            if (response !== "sign" && (!approval || approval.waiting)) continue;
            const refusal = approval ? (approval.waiting ? null : this.checkApproval(t, approval)) : this.checkSigner(t, t.gate);
            if (refusal) {
              fail(refusal[0], 2, refusal[1]);
              continue;
            }
          }
          // Rows 8 and 9 (decision 18): a check that refuses leaves the candidate out, as a false guard does; a check outranks a gate.
          const refusing = this.triggerRefusal(t, external);
          if (refusing) {
            fail("check", 3, refusing.run.triggerPoint, refusing.run);
            if (!external) this.leftOut(refusing);
            continue;
          }
          tried.set(t, true);
          enabled.push(t);
          found = true;
          break;
        }
        if (found) break;
      }
    }
    const selected = this.removeConflicts(enabled, (t) => {
      if (!t.gate) return true;
      const prepared = this.prep.get(t);
      // A signature is a verb of its own around the count (decision 18): its values first, then the count, then its later points.
      if (prepared?.sign) {
        this.list(prepared.sign, new Set());
        this.write(prepared.sign, new Set());
      }
      const approval = this.approvalOf(t);
      const complete = approval ? this.respond(t, approval) : this.sign(t, t.gate);
      // On the completing occurrence the move's own points are listed before sign's with-change points.
      if (complete && prepared?.move) this.list(prepared.move, new Set());
      if (prepared?.sign) {
        this.runLater(prepared.sign, "with-change", new Set());
        this.runLater(prepared.sign, "after-commit", new Set());
      }
      if (complete) return true;
      consumed = true; // the occurrence signed an incomplete gate: accepted, and the transition waits for more signatures
      return false;
    });
    return { transitions: selected, consumed, reason, by, ...(run ? { run } : {}) };
  }

  /** Selection of the move an invoke finished with (the step's own) or a `done` move (automatic). */
  private selectAt(source: Node, trigger: string, key: string): { transitions: Edge[]; reason: string; by?: string; run?: TriggerRunJson } {
    let reason = "no-transition";
    let by: string | undefined;
    let run: TriggerRunJson | undefined;
    const own = trigger !== "done";
    for (const t of this.chart.candidates(source, trigger, key)) {
      if (this.guard(t) === true) {
        const refusing = this.triggerRefusal(t, own);
        if (!refusing) return { transitions: [t], reason };
        // A check outranks a guard as the reason; the first of each is kept (decision 18).
        if (reason !== "check") {
          reason = "check";
          by = refusing.run.triggerPoint;
          run = refusing.run;
        }
        if (!own) this.leftOut(refusing);
        continue;
      }
      if (reason === "no-transition") {
        reason = "guard";
        by = t.guard ? String(t.guard.id) : undefined;
      }
    }
    return { transitions: [], reason, by, ...(run ? { run } : {}) };
  }

  private removeConflicts(enabled: Edge[], admit: (t: Edge) => boolean): Edge[] {
    let filtered: Edge[] = [];
    for (const t1 of enabled) {
      if (filtered.includes(t1)) continue;
      let preempted = false;
      const remove: Edge[] = [];
      const exit1 = this.exitSet(t1);
      for (const t2 of filtered) {
        const exit2 = this.exitSet(t2);
        if (![...exit1].some((s) => exit2.has(s))) continue;
        if (isDescendantOf(t1.source, t2.source)) remove.push(t2);
        else {
          preempted = true;
          break;
        }
      }
      if (preempted || !admit(t1)) continue;
      filtered = filtered.filter((t) => !remove.includes(t));
      filtered.push(t1);
    }
    return filtered;
  }

  // ---- guards and actions ----

  private guard(t: Edge): boolean | null {
    const g = t.guard;
    if (!g) return t.guardRef === null; // an undeclared guard never holds
    const id = String(g.id);
    if (this.chart.compiled.has(id)) {
      let value = false;
      try {
        const result = compile(String(g.expression))(frozen(this.contextObject()), frozen(this.scope.event));
        if (typeof result === "boolean") value = result;
        else
          this.problem(
            "MQ9504",
            `Guard '${String(g.name)}' returned ${describe(result)}, which counts as false; make the expression return true or false.`,
            id,
          );
      } catch (e) {
        this.problem("MQ9502", `Guard '${String(g.name)}' threw: ${(e as Error).message}`, id);
      }
      this.scope.guards.push({ guard: id, transition: t.id, result: value, source: "expression" });
      return value;
    }
    // A guard written in the expression language: read when the mock can (the engine reads every one).
    if (g.condition !== undefined && !str(g.expression)) {
      const result = evalTree(g.condition, this.env());
      if (result !== UNKNOWN) {
        const value = result === true;
        this.scope.guards.push({ guard: id, transition: t.id, result: value, source: "expression" });
        return value;
      }
    }
    const assumed = this.scope.input?.assume[id];
    if (typeof assumed === "boolean") {
      this.scope.guards.push({ guard: id, transition: t.id, result: assumed, source: "assumed" });
      return assumed;
    }
    this.scope.guards.push({ guard: id, transition: t.id, result: null, source: "missing" });
    this.problem(
      "MQ9306",
      `Guard '${String(g.name)}' has no expression and the step gives no assume value for it, so the replay stops here; add it to the step's assume, or give the guard an expression.`,
      id,
    );
    return null;
  }

  private runAction(action: Json): StepTraceJson["microsteps"][number]["actions"][number] {
    const id = String(action.id);
    const changed: Json = {};
    let source: "expression" | "sets" | "stub" = "stub";
    // Every set is worked out from the context and the row as they were when the action started; all are applied together.
    const sets = arr(action.sets);
    const worked = sets.map((x) => ({ attribute: String(x.attribute), value: this.setValue(action, x) }));
    if (this.chart.compiled.has(id)) {
      source = "expression";
      try {
        const result = compile(String(action.expression))(frozen(this.contextObject()), frozen(this.scope.event));
        if (result && typeof result === "object" && !Array.isArray(result)) {
          for (const [name, raw] of Object.entries(result as Json)) {
            const attribute = this.chart.attributes.find((a) => a.name === name);
            if (!attribute) {
              this.problem(
                "MQ9505",
                `Action '${String(action.name)}' returned '${name}', which is not a context attribute of process '${String(this.chart.doc.name)}'; the entry is ignored. Return only context attribute names.`,
                id,
              );
              continue;
            }
            const aid = String(attribute.id);
            const value = JSON.parse(JSON.stringify(raw ?? null)) as unknown;
            if (!same(this.context[aid], value)) changed[aid] = value;
            this.context[aid] = value;
          }
        } else if (result !== null && result !== undefined) {
          this.problem(
            "MQ9505",
            `Action '${String(action.name)}' returned ${describe(result)}, not an object of context updates; the result is ignored. Return an object such as ({ name: value }).`,
            id,
          );
        }
      } catch (e) {
        this.problem("MQ9502", `Action '${String(action.name)}' threw: ${(e as Error).message}`, id);
      }
    }
    const setOut: Json = {};
    if (worked.some((w) => w.value !== UNKNOWN) && this.frozenObject && !this.thawing)
      this.scope.notes.push("The object is frozen, and the move's sets were applied anyway; freezing does not stop moves.");
    for (const w of worked) {
      if (w.value === UNKNOWN) continue;
      this.row[w.attribute] = w.value;
      setOut[w.attribute] = w.value;
    }
    if (source === "stub" && sets.length) source = "sets";
    for (const raised of ids(action.raises)) if (this.chart.events.has(raised)) this.queue.push({ kind: "event", id: raised });
    return { action: id, source, changed: sorted(changed), ...(Object.keys(setOut).length ? { sets: sorted(setOut) } : {}) };
  }

  // ---- the object: sets, identity and states ----

  /** A set's value, or UNKNOWN (the mock cannot read it): then nothing is set and a note says so. */
  private setValue(action: Json, set: Json): Value {
    const value = evalTree(set.value, this.env());
    if (value === UNKNOWN)
      this.scope.notes.push(
        `The mock backend could not work out the value of a set in action '${String(action.name)}', so the set did nothing; the engine reads every expression.`,
      );
    return value;
  }

  /** The identity of whoever acts (decision 5): the signer, else the name of the actor it acts as, else the system actor's, else null. */
  private identity(): string | null {
    const signer = this.scope.input?.signer;
    if (signer) return signer;
    const acting = this.scope.input?.actor ?? this.scope.actor;
    return acting ? (this.actorName(acting) ?? null) : null;
  }

  private objectOf(end: string[]): Json | undefined {
    let object = this.subjectObject();
    for (const e of end) {
      const link = arr(object?.links).find((l) => l.end === e);
      object = link ? this.cast.objects.find((o) => o.id === link.object) : undefined;
      if (!object) return undefined;
    }
    return object;
  }

  /** `over` reads a trigger point's site: `subject.` is the proposed values and `old.` the values before the verb. */
  private env(over?: { now: Json; old: Json | null }): EvalEnv {
    return {
      path: (p, old) => {
        if (p.length === 1) {
          if (over && old && over.old === null) return null;
          const src = over ? (old ? over.old! : over.now) : this.row;
          if (p[0] in src) return src[p[0]];
          return p[0] in this.context ? this.context[p[0]] : UNKNOWN;
        }
        const object = this.objectOf(p.slice(0, -1));
        if (!object) return null;
        const found = (object.values as Json | undefined)?.[p[p.length - 1]];
        if (found !== undefined) return found;
        return this.attributesOf(String(object.entity)).find((a) => a.id === p[p.length - 1]) ? null : UNKNOWN;
      },
      now: () => clockText(this.clock),
      actor: () => this.identity(),
      inState: (states, of) => this.inState(states, of),
    };
  }

  /** In any of the states named (decision 7): the running chart for the process's own lifecycle, else the object's `states`, else the lifecycle's initial configuration. */
  private inState(states: string[], of: string[] | undefined): boolean | null {
    const proc = this.lifecycles.find((p) => stateIds(p.states).has(states[0]));
    if (!proc) return null;
    if (proc.id === this.chart.doc.id && (!of || of.length === 0)) return states.some((x) => [...this.config].some((n) => n.id === x));
    const object = this.objectOf(of ?? []);
    if (!object) return null;
    const own = ids(object.states).filter((x) => stateIds(proc.states).has(x));
    const active = new Set<string>();
    if (own.length) for (const x of own) enterState(proc.states, x, active);
    else {
      enterInitial(proc.states, str(proc.initial), active);
      this.scope.notes.push(`${String(object.name)} gives no state in ${String(proc.name)}, so its initial state is assumed.`);
    }
    return states.some((x) => active.has(x));
  }

  // ---- approvals (signoff-design.md decisions 7 to 16, 28) ----

  /** The approval of a gated move: its progress, started on first use; null for a plain gate or a move with no gate. */
  approvalOf(t: Edge): Approval | null {
    if (!approvalStages(t.gate)) return null;
    return this.approvals.get(t.id) ?? this.startApproval(t);
  }

  /** Whether the move's gate is an approval whose round needs nothing. */
  private waitingApproval(t: Edge): boolean {
    return !!this.approvalOf(t)?.waiting;
  }

  /** Starts a round: each signature's `when` is judged now, and only false means not needed (decision 7). */
  private startApproval(t: Edge): Approval {
    const needed = new Set<string>();
    for (const sg of gateSignatures(t.gate)) if (sg.when === undefined || evalTree(sg.when, this.env()) !== false) needed.add(String(sg.id));
    const approval: Approval = { needed, counted: [], answered: [], waiting: needed.size === 0 };
    this.approvals.set(t.id, approval);
    return approval;
  }

  /** Whether the signature has its count and each required actor signed among the signatures given. */
  sigComplete(sg: Json, counted: Signature[], approval?: Approval): boolean {
    if (approval && !approval.needed.has(String(sg.id))) return false;
    const mine = counted.filter((c) => c.signature === sg.id);
    return mine.length >= Math.max(1, num(sg.required) ?? 1) && ids(sg.requiredActors).every((r) => mine.some((c) => this.membership.accepts(r, c.actor)));
  }

  /** Whether every needed signature of the round is complete with the signatures given. */
  roundDone(t: Edge, approval: Approval, counted: Signature[]): boolean {
    return gateSignatures(t.gate)
      .filter((sg) => approval.needed.has(String(sg.id)))
      .every((sg) => this.sigComplete(sg, counted));
  }

  /** The named signature the current step is on for a gated move, as the audit names it. */
  private namedOf(t: Edge): Json {
    const pick = this.picks.get(t.id);
    return pick ? { signature: String(pick.sg.id), stage: String(pick.sg.stage) } : {};
  }

  /** Records an act of the step's person (decision 28); an automatic move has no person, so it records nothing. */
  private act(verb: string | null, transition: string | null, signer?: string | null) {
    const who = signer ?? this.scope.input?.signer;
    if (who && (verb || transition)) this.acts.push({ seq: this.index, signer: who, verb, transition });
  }

  /** Row 7c: whether the person did a verb or move named in `notBy` since the object entered the gated move's source state. */
  private excluded(t: Edge, sg: Json, signer: string | null): boolean {
    const by = arr(sg.notBy);
    if (!by.length || !signer) return false;
    const from = this.enteredAt.get(t.source.id) ?? -1;
    return this.acts.some(
      (x) =>
        x.seq >= from &&
        x.signer === signer &&
        by.some((n) => (str(n.verb) !== null && n.verb === x.verb) || (str(n.transition) !== null && n.transition === x.transition)),
    );
  }

  /** Row 7d by response (decision 14): an earlier answer on this approval blocks the response. */
  private repeated(t: Edge, approval: Approval, sg: Json, response: Response, signer: string | null): boolean {
    const mine = approval.answered.filter((a) => a.signer === signer);
    const here = mine.filter((a) => a.signature === sg.id);
    const across = t.gate!.allowSignerAcrossSignatures === true;
    if (response === "sign")
      return (
        (here.some((a) => a.kind === "sign") && sg.allowRepeatSigner !== true) ||
        here.some((a) => a.kind === "ignore") ||
        (mine.length > here.length && !across)
      );
    if (response === "ignore") return here.length > 0 || (mine.length > here.length && !across);
    if (response === "reject") return mine.some((a) => a.kind === "ignore" && (a.signature === sg.id || !across));
    return false;
  }

  /** Whether the person may fill the signature with the response: rows 7a, 7c and 7d only (decision 8). */
  private mayFill(t: Edge, approval: Approval, sg: Json, response: Response, signer: string | null, actor: string | null): boolean {
    return (
      !!actor &&
      !!signer &&
      this.membership.allows(ids(sg.signers), actor) &&
      !this.excluded(t, sg, signer) &&
      !this.repeated(t, approval, sg, response, signer)
    );
  }

  /**
   * The pick (decision 8): the named signature, else one by the rule of the response, and the actor the person acts as for it. `actorFor`
   * gives the actor for a signature, or null when the person meets none of its signers. A refusal names the first failing check.
   */
  private pick(
    t: Edge,
    approval: Approval,
    response: Response,
    signer: string | null,
    actorFor: (sg: Json) => string | null,
    explicit?: string,
  ): { sg: Json; actor: string | null } | { refuse: string } {
    const all = gateSignatures(t.gate);
    if (explicit) {
      const sg = all.find((x) => x.id === explicit);
      if (!sg || !approval.needed.has(String(sg.id))) return { refuse: "gate-signer" };
      return { sg, actor: actorFor(sg) };
    }
    const needed = all.filter((sg) => approval.needed.has(String(sg.id)));
    if (response === "withdraw") {
      const mine = [...approval.counted].reverse().find((c) => c.signer === signer);
      const sg = mine && all.find((x) => x.id === mine.signature);
      return sg && mine ? { sg, actor: mine.actor } : { refuse: "gate-not-signed" };
    }
    if (response === "reject") {
      const sg = needed.find((x) => actorFor(x) !== null);
      return sg ? { sg, actor: actorFor(sg) } : { refuse: "gate-signer" };
    }
    const open = needed.filter((sg) => !this.sigComplete(sg, approval.counted));
    const fills = open.filter((sg) => this.mayFill(t, approval, sg, response, signer, actorFor(sg)));
    const unsigned = (sg: Json, actor: string | null) =>
      !!actor &&
      ids(sg.requiredActors).some(
        (r) => !approval.counted.some((c) => c.signature === sg.id && this.membership.accepts(r, c.actor)) && this.membership.accepts(r, actor),
      );
    // A signature already complete still counts as met, so the person is refused on it (gate-repeat), not as no signer (review S1).
    const sg = fills.find((x) => unsigned(x, actorFor(x))) ?? fills[0] ?? needed.find((x) => actorFor(x) !== null);
    return sg ? { sg, actor: actorFor(sg) } : { refuse: "gate-signer" };
  }

  /** Row 7 on an approval: the pick, then 7a to 7f in order. The pick is kept for the step. Null when the response may go on. */
  private checkApproval(t: Edge, approval: Approval): Refusal | null {
    const input = this.scope.input!;
    const gate = t.gate!;
    const gateId = String(gate.id);
    const response = input.response ?? "sign";
    const signer = input.signer;
    const actor = input.actor;
    const actorFor = (sg: Json) => (actor && signer && this.membership.allows(ids(sg.signers), actor) ? actor : null);
    const picked = this.pick(t, approval, response, signer, actorFor, input.signature);
    let refusal: Refusal | null = null;
    if ("refuse" in picked) refusal = [picked.refuse, gateId];
    else {
      const { sg } = picked;
      this.picks.set(t.id, { sg, actor });
      if (!actor || !signer || !this.membership.allows(ids(sg.signers), actor)) refusal = ["gate-signer", gateId];
      else {
        const denied = this.denyOf(response);
        if (denied) {
          this.scope.rights.push(...denied.holding);
          refusal = ["right", denied.by[0]];
        } else if (this.excluded(t, sg, signer)) refusal = ["gate-excluded", gateId];
        else if (this.repeated(t, approval, sg, response, signer)) refusal = ["gate-repeat", gateId];
        else if (response === "sign" && this.sigComplete(sg, approval.counted)) refusal = ["gate-repeat", gateId];
        else if (response === "withdraw" && !approval.counted.some((c) => c.signer === signer && c.signature === sg.id)) refusal = ["gate-not-signed", gateId];
        else if (gate.reasonRequired === true && !(input.reason ?? "").trim()) refusal = ["gate-reason", gateId];
      }
    }
    if (refusal) this.audit(gate, t, signer, actor, meaningOf(input, gate), input.reason, "refused", auditAttributes(input, gate), this.namedOf(t));
    return refusal;
  }

  /** Row 7b: a right of the acting lifecycle that denies the response's verb. */
  private denyOf(verb: string) {
    const acting = this.actingLifecycle();
    if (!acting || !takesPart(acting)) return null;
    const answer = answerLifecycle(acting, this.factsFor(acting, verb, [], null, false));
    return answer.verdict === "deny" && !answer.byDefault ? answer : null;
  }

  /** The response's effect (decisions 10 to 13); true when the approval is complete and the move follows. */
  private respond(t: Edge, approval: Approval): boolean {
    const input = this.scope.input!;
    const gate = t.gate!;
    const response = input.response ?? "sign";
    const meaning = meaningOf(input, gate);
    const attributes = auditAttributes(input, gate);
    const audit = (outcome: string, named: Json) => this.audit(gate, t, input.signer, input.actor, meaning, input.reason, outcome, attributes, named);
    this.scope.answered = t;
    if (approval.waiting) {
      // Nothing was needed: the occurrence moves as if there were no gate, and the audit records `completed` with no signature.
      audit("completed", {});
      this.act(t.verb, t.id);
      this.approvals.delete(t.id);
      return true;
    }
    const pick = this.picks.get(t.id)!;
    const sg = String(pick.sg.id);
    const named = { signature: sg, stage: String(pick.sg.stage) };
    this.act(response, null);
    if (response === "sign") {
      approval.counted.push({ signer: input.signer!, actor: input.actor!, meaning, reason: input.reason, signature: sg });
      approval.answered.push({ signer: input.signer!, signature: sg, kind: "sign" });
      audit("signed", named);
      if (!this.roundDone(t, approval, approval.counted)) return false;
      audit("completed", {});
      // A gated move is done by everyone whose signature counted toward it (decision 15).
      for (const c of approval.counted) this.act(t.verb, t.id, c.signer);
      this.approvals.delete(t.id);
      return true;
    }
    if (response === "ignore") {
      approval.answered.push({ signer: input.signer!, signature: sg, kind: "ignore" });
      audit("ignored", named);
    } else if (response === "withdraw") {
      const at = approval.counted.map((c) => c.signer === input.signer && c.signature === sg).lastIndexOf(true);
      if (at >= 0) approval.counted.splice(at, 1);
      const answer = approval.answered.map((a) => a.signer === input.signer && a.signature === sg && a.kind === "sign").lastIndexOf(true);
      if (answer >= 0) approval.answered.splice(answer, 1);
      audit("withdrawn", named);
    } else {
      // A veto: it clears every signature and moves nothing, and round 1 starts again at once (decision 10).
      audit("rejected", named);
      this.startApproval(t);
    }
    return false;
  }

  // ---- gates ----

  /** The refusal of a signature and the element that refused it: the gate, or a right that denies `sign` (row 7, decision 12). */
  private checkSigner(t: Edge, gate: Json): [string, string] | null {
    const input = this.scope.input!;
    const signatures = this.signatures.get(t.id) ?? [];
    let refusal: [string, string] | null = null;
    const gateId = String(gate.id);
    const signDeny = () => {
      const acting = this.actingLifecycle();
      if (!acting || !takesPart(acting)) return null;
      const answer = answerLifecycle(acting, this.factsFor(acting, "sign", [], null, false));
      return answer.verdict === "deny" && !answer.byDefault ? answer : null;
    };
    if (!input.actor || !input.signer || !this.membership.allows(ids(gate.signers), input.actor)) refusal = ["gate-signer", gateId];
    else {
      const denied = signDeny();
      if (denied) {
        this.scope.rights.push(...denied.holding);
        refusal = ["right", denied.by[0]];
      } else if (gate.allowRepeatSigner !== true && signatures.some((s) => s.signer === input.signer)) refusal = ["gate-repeat", gateId];
      else if (gate.reasonRequired === true && !(input.reason ?? "").trim()) refusal = ["gate-reason", gateId];
    }
    if (refusal) this.audit(gate, t, input.signer, input.actor, meaningOf(input, gate), input.reason, "refused", auditAttributes(input, gate));
    return refusal;
  }

  private sign(t: Edge, gate: Json): boolean {
    const input = this.scope.input!;
    const signatures = this.signatures.get(t.id) ?? [];
    this.signatures.set(t.id, signatures);
    const meaning = meaningOf(input, gate);
    const attributes = auditAttributes(input, gate);
    signatures.push({ signer: input.signer!, actor: input.actor!, meaning, reason: input.reason });
    this.audit(gate, t, input.signer, input.actor, meaning, input.reason, "signed", attributes);
    this.act("sign", null);
    const complete = gateSatisfied(gate, signatures, this.membership);
    if (complete) {
      this.audit(gate, t, input.signer, input.actor, meaning, input.reason, "completed", attributes);
      for (const s of signatures) this.act(t.verb, t.id, s.signer);
      signatures.length = 0;
    }
    return complete;
  }

  private audit(
    gate: Json,
    t: Edge,
    signer: string | null,
    actor: string | null,
    meaning: string | null,
    reason: string | null,
    outcome: string,
    attributes: Json,
    named: Json = {},
  ) {
    this.scope.audit.push({
      instance: "simulation",
      process: String(this.chart.doc.id),
      gate: String(gate.id),
      transition: t.id,
      sequence: ++this.auditSeq,
      signer,
      actor,
      meaning,
      reason,
      at: clockText(this.clock),
      outcome,
      attributes,
      ...named,
    });
  }

  // ---- microstep ----

  private microstep(transitions: Edge[], initial?: Node) {
    if (++this.micro > MAX_MICROSTEPS) throw new Overflow();
    const exited: string[] = [];
    const entered: string[] = [];
    const actions: StepTraceJson["microsteps"][number]["actions"] = [];

    // The points of the moves taken (rows 8 and 9, decision 18): listed once per microstep, their before values written before the exits.
    const listedPoints = new Set<string>();
    const writtenPoints = new Set<string>();
    for (const t of transitions) {
      const prepared = this.prep.get(t)?.move;
      if (!prepared) continue;
      this.list(prepared, listedPoints);
      this.write(prepared, writtenPoints);
    }

    // Exit, in reverse document order, recording history first.
    const exitSet = new Set<Node>();
    for (const t of transitions) for (const s of this.exitSet(t)) exitSet.add(s);
    const exiting = [...exitSet].sort((a, b) => b.order - a.order);
    for (const s of exiting)
      for (const h of s.children.filter((c) => c.type === "history"))
        this.history.set(
          h.id,
          [...this.config].filter((c) => (h.doc.history === "deep" ? isLeaf(c) && isDescendantOf(c, s) : c.parent === s)).sort((a, b) => a.order - b.order),
        );
    for (const s of exiting) {
      for (const id of ids(s.doc.exit)) {
        const action = this.chart.actions.get(id);
        if (action) actions.push(this.runAction(action));
      }
      this.cancel(s);
      this.config.delete(s);
      exited.push(s.id);
    }

    // Transition actions, in transition order.
    for (const t of transitions) for (const action of t.actions) actions.push(this.runAction(action));

    // Entry, in document order.
    const toEnter = new Set<Node>();
    if (initial) {
      this.addDescendants(initial, toEnter);
      this.addAncestors(initial, this.chart.root, toEnter);
    }
    for (const t of transitions) {
      if (!t.targets.length) continue;
      const domain = this.domain(t);
      for (const s of t.targets) this.addDescendants(s, toEnter);
      for (const s of this.effectiveTargets(t)) this.addAncestors(s, domain, toEnter);
    }
    const doneQueued = new Set<Node>();
    const arrived: Node[] = [];
    for (const s of [...toEnter].sort((a, b) => a.order - b.order)) {
      if (s.isRoot || this.config.has(s)) continue;
      this.config.add(s);
      entered.push(s.id);
      arrived.push(s);
      this.enteredAt.set(s.id, this.index);
      for (const id of ids(s.doc.entry)) {
        const action = this.chart.actions.get(id);
        if (action) actions.push(this.runAction(action));
      }
      for (const edge of s.timers) this.timers.push({ edge, due: this.clock + (edge.delay ?? 0), seq: ++this.timerSeq });
      for (const invoke of arr(s.doc.invoke)) this.pending.push({ invoke, state: s });
      const parent = s.parent;
      if (s.type === "final" && parent) {
        if (parent.isRoot) this.done = true;
        else {
          // A final child completes a compound parent; a parallel state completes when every region is final, and that
          // completion travels up through enclosing parallel states (each queued once per microstep).
          if (parent.type !== "parallel" && !doneQueued.has(parent)) {
            doneQueued.add(parent);
            this.queue.push({ kind: "done", id: parent.id });
          }
          for (
            let marker: Node | null = parent.type === "parallel" ? parent : parent.parent;
            marker && marker.type === "parallel" && !marker.isRoot && marker.children.every((r) => this.inFinal(r));
            marker = marker.parent
          ) {
            if (!doneQueued.has(marker)) {
              doneQueued.add(marker);
              this.queue.push({ kind: "done", id: marker.id });
            }
          }
        }
      }
    }
    // A round starts when its source is entered, after the entering move's actions and sets, and again when a gated move completed
    // without leaving its source (decision 7).
    for (const s of arrived) for (const t of s.transitions) if (t.trigger === "event" && approvalStages(t.gate)) this.startApproval(t);
    for (const t of transitions) if (approvalStages(t.gate) && this.config.has(t.source) && !arrived.includes(t.source)) this.startApproval(t);
    // Rows 11 and 12 once the move's own actions have run: with-the-change points now, after-commit ones queued.
    const laterPoints = new Set<string>();
    for (const t of transitions) {
      const prepared = this.prep.get(t)?.move;
      if (!prepared) continue;
      this.runLater(prepared, "with-change", laterPoints);
      this.runLater(prepared, "after-commit", laterPoints);
    }
    for (const t of transitions) this.prep.delete(t);
    if (this.done) {
      this.timers = [];
      this.pending = [];
      this.queue = [];
    }
    this.scope.microsteps.push({ transitions: transitions.map((t) => t.id), exited, entered, actions });
  }

  private cancel(s: Node) {
    this.timers = this.timers.filter((t) => t.edge.source !== s);
    this.pending = this.pending.filter((p) => p.state !== s);
    for (const t of s.transitions) {
      const approval = this.approvals.get(t.id);
      if (approval) {
        // Whenever the approval holds any response, even only an ignore, so the audit shows the block ended (section 8).
        if (t.gate && (approval.counted.length || approval.answered.length)) this.audit(t.gate, t, null, null, null, null, "discarded", {});
        this.approvals.delete(t.id);
      }
      const list = this.signatures.get(t.id);
      if (t.gate && list && list.length) {
        this.audit(t.gate, t, null, null, null, null, "discarded", {});
        list.length = 0;
      }
    }
  }

  private inFinal(region: Node): boolean {
    if (region.type === "parallel") return region.children.every((r) => this.inFinal(r));
    if (region.type === "final") return this.config.has(region);
    return region.children.some((c) => c.type === "final" && this.config.has(c));
  }

  // ---- exit and entry sets ----

  private exitSet(t: Edge): Set<Node> {
    const set = new Set<Node>();
    const domain = this.domain(t);
    if (domain) for (const s of this.config) if (isDescendantOf(s, domain)) set.add(s);
    return set;
  }

  private domain(t: Edge): Node | null {
    const targets = this.effectiveTargets(t);
    if (!targets.length) return null;
    if (!t.external && targets.every((s) => s === t.source || isDescendantOf(s, t.source))) return t.source;
    const all = [...targets, t.source];
    for (const ancestor of ancestorsOf(all[0])) if (all.slice(1).every((s) => isDescendantOf(s, ancestor))) return ancestor;
    return this.chart.root;
  }

  private effectiveTargets(t: Edge): Node[] {
    const list: Node[] = [];
    const effective = (s: Node, depth: number) => {
      if (s.type !== "history") {
        if (!list.includes(s)) list.push(s);
        return;
      }
      const recorded = this.history.get(s.id);
      if (recorded && recorded.length) for (const r of recorded) effective(r, depth + 1);
      else if (s.defaultTarget && depth < 8) effective(s.defaultTarget, depth + 1);
    };
    for (const target of t.targets) effective(target, 0);
    return list;
  }

  private addDescendants(s: Node, toEnter: Set<Node>, depth = 0) {
    if (depth > 64) return;
    if (s.type === "history") {
      const parent = s.parent!;
      const recorded = this.history.get(s.id);
      if (recorded && recorded.length) {
        for (const r of recorded) this.addDescendants(r, toEnter, depth + 1);
        for (const r of recorded) this.addAncestors(r, parent, toEnter, depth + 1);
      } else if (s.defaultTarget) {
        this.addDescendants(s.defaultTarget, toEnter, depth + 1);
        this.addAncestors(s.defaultTarget, parent, toEnter, depth + 1);
      }
      return;
    }
    toEnter.add(s);
    if (s.type === "parallel") {
      for (const region of s.children)
        if (region.type !== "history" && ![...toEnter].some((x) => isDescendantOf(x, region))) this.addDescendants(region, toEnter, depth + 1);
    } else if (s.children.length && s.initialChild) {
      this.addDescendants(s.initialChild, toEnter, depth + 1);
      this.addAncestors(s.initialChild, s, toEnter, depth + 1);
    }
  }

  private addAncestors(s: Node, domain: Node | null, toEnter: Set<Node>, depth = 0) {
    const ancestors: Node[] = [];
    for (const a of ancestorsOf(s)) {
      if (a === domain) break;
      ancestors.push(a);
    }
    if (domain && domain.type === "parallel") ancestors.push(domain);
    for (const a of ancestors) {
      if (a !== domain && !a.isRoot) toEnter.add(a);
      if (a.type !== "parallel") continue;
      for (const region of a.children)
        if (region.type !== "history" && ![...toEnter].some((x) => isDescendantOf(x, region))) this.addDescendants(region, toEnter, depth + 1);
    }
  }

  // ---- helpers ----

  private finish(before: Json, rowBefore: Json): StepTraceJson {
    const changed: Json = {};
    const valuesChanged: Json = {};
    for (const [id, v] of Object.entries(this.row)) if (!(id in rowBefore) || !same(rowBefore[id], v)) valuesChanged[id] = v;
    for (const [id, v] of Object.entries(this.context)) if (!(id in before) || !same(before[id], v)) changed[id] = v;
    const seen = new Set<string>();
    const diagnostics = this.scope.diagnostics.filter((d) => {
      const key = `${d.rule}|${d.message}|${d.elementId}`;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
    const trace: StepTraceJson = {
      index: this.index,
      input: this.scope.echo,
      accepted: this.scope.refusal === null,
      refusal: this.scope.refusal,
      microsteps: this.scope.microsteps,
      guards: this.scope.guards,
      audit: this.scope.audit,
      configuration: this.leaves().map((s) => s.id),
      context: sorted(this.context),
      changed: sorted(changed),
      clock: clockText(this.clock),
      final: this.done,
      diagnostics,
      incomplete: this.scope.guards.some((g) => g.source === "missing") || diagnostics.some((d) => d.rule === "MQ9305" || d.rule === "MQ9507"),
      // A refused step moved nothing, so it names no actor.
      ...(this.scope.actor && this.scope.refusal === null ? { actor: this.scope.actor } : {}),
      ...(this.scope.actsAs ? { actsAs: this.scope.actsAs } : {}),
      ...(Object.keys(valuesChanged).length ? { valuesChanged: sorted(valuesChanged) } : {}),
      ...(this.scope.notes.length ? { notes: [...new Set(this.scope.notes)] } : {}),
      ...(this.scope.refusal !== null && this.scope.refusedBy && this.rightsOn ? { refusedBy: this.scope.refusedBy } : {}),
      ...(this.scope.rights.length ? { rights: uniqueHoldings(this.scope.rights) } : {}),
      ...(this.frozenObject ? { frozen: true as const } : {}),
      ...(this.scope.triggerRuns.length || this.scope.queued.length ? { triggerPoints: [...this.scope.triggerRuns, ...this.scope.queued] } : {}),
      ...(this.scope.checkWarnings.length ? { checkWarnings: [...this.scope.checkWarnings] } : {}),
    };
    if (this.scope.list) unsatisfiedLists.set(trace, this.scope.list);
    const answered = this.scope.answered;
    if (answered && this.scope.refusal === null) {
      const held = this.approvals.get(answered.id);
      signatureCounts.set(
        trace,
        Object.fromEntries(gateSignatures(answered.gate).map((sg) => [String(sg.id), held ? held.counted.filter((c) => c.signature === sg.id).length : 0])),
      );
    }
    return trace;
  }

  private refuse(reason: string, by?: string) {
    if (this.scope.refusal !== null) return;
    this.scope.refusal = reason;
    if (by) this.scope.refusedBy = by;
  }

  private problem(rule: string, message: string, elementId: string | null = null) {
    this.scope.diagnostics.push(diagnostic(rule, message, elementId ?? String(this.chart.doc.id)));
  }

  private contextObject(): Json {
    const out: Json = {};
    for (const a of this.chart.attributes) out[String(a.name)] = this.context[String(a.id)] ?? null;
    return out;
  }

  private eventObject(name: string | null, actor: string | null, declared: Json[], payload: Json): Json {
    const values: Json = {};
    for (const a of declared) if (Object.prototype.hasOwnProperty.call(payload, String(a.id))) values[String(a.name)] = payload[String(a.id)];
    return { name, actor: actor ? (this.actorName(actor) ?? actor) : null, payload: values };
  }
}

function timerOrder(x: Timer, y: Timer): number {
  return x.due - y.due || x.edge.source.order - y.edge.source.order || x.edge.index - y.edge.index || x.seq - y.seq;
}

function sorted(map: Json): Json {
  const out: Json = {};
  for (const k of Object.keys(map).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))) out[k] = map[k];
  return out;
}

function meaningOf(input: SimInputJson, gate: Json): string | null {
  const meanings = arr(gate.meanings);
  return input.meaning ?? (meanings.length === 1 ? String(meanings[0].id) : null);
}

function auditAttributes(input: SimInputJson, gate: Json): Json {
  const out: Json = {};
  for (const a of arr(gate.auditAttributes))
    if (Object.prototype.hasOwnProperty.call(input.payload, String(a.id))) out[String(a.id)] = input.payload[String(a.id)];
  return sorted(out);
}

/** A gate with stages is an approval. */
function approvalStages(gate: Json | null | undefined): boolean {
  return !!gate && arr(gate.stages).length > 0;
}

/** Who may sign a gate: its own signers, or the signers of every named signature of an approval, in file order. */
function signersOf(gate: Json): string[] {
  return approvalStages(gate) ? [...new Set(gateSignatures(gate).flatMap((sg) => ids(sg.signers)))] : ids(gate.signers);
}

/** A required actor counts as signed when a signature's actor is it or one of its members. */
function gateSatisfied(gate: Json, signatures: Signature[], membership: Membership): boolean {
  const required = Math.max(1, typeof gate.required === "number" ? gate.required : 1);
  return signatures.length >= required && ids(gate.requiredActors).every((a) => signatures.some((s) => membership.accepts(a, s.actor)));
}

// ------------------------------------------------------------------ the operations

/** Whether `at` is a start instant the stepper can read (the engine answers 400 otherwise). */
export function readAt(at: unknown): number | null | "invalid" {
  if (at === undefined || at === null) return null;
  if (typeof at !== "string") return "invalid";
  const ms = Date.parse(at);
  return Number.isNaN(ms) ? "invalid" : ms;
}

/** Runs a start and inputs through the reduced interpreter and describes where the process stands (the simulate answer). */
export function simulate(doc: Json, start: { context?: Json; at?: string } | null | undefined, steps: Json[], options: StepperOptions = {}): SimulationJson {
  const chart = new Chart(doc);
  const run = new Interpreter(chart, options.actorName, options.membership);
  run.cast = { objects: options.objects ?? [], subject: options.subject ?? null };
  run.participants = options.participants ?? [];
  run.ownerActors = options.ownerActors ?? new Set();
  run.membersActors = options.membersActors ?? new Map();
  run.attributesOf = options.attributesOf ?? (() => []);
  run.subjectEntity = options.subjectEntity;
  run.lifecycles = options.lifecycles ?? [];
  run.named = options.namedRights ?? new Map();
  run.namedPoints = options.namedPoints ?? [];
  run.stereotypeCovers = options.stereotypeCovers ?? (() => false);
  run.stereotypeCarriers = options.stereotypeCarriers ?? (() => []);
  run.triggers = usesTriggers([doc, ...run.lifecycles], run.namedPoints);
  run.entityChain = options.entityChain ?? ((e) => [e]);
  run.rightsOn = options.refusedBy === true || usesRights(run.lifecycles) || run.triggers;
  const at = readAt(start?.at);
  const traces = [run.start(start?.context, typeof at === "number" ? at : null)];
  let complete = !traces[0].incomplete;
  for (let i = 0; complete && i < steps.length; i++) {
    const trace = run.step(normalizeInput(steps[i], i, chart, options.actorId));
    traces.push(trace);
    complete = !trace.incomplete;
  }
  const seen = new Set<string>();
  const diagnostics = traces
    .flatMap((t) => t.diagnostics)
    .filter((d) => {
      const key = `${d.rule}|${d.message}`;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  const active = activeTransitions(run);
  const gates = active
    .filter((t) => t.gate && t.trigger === "event")
    .map((t) => {
      const approval = run.approvalOf(t);
      if (approval) {
        const sigs = gateSignatures(t.gate);
        const state = (sg: Json) => ({
          signature: String(sg.id),
          needed: approval.needed.has(String(sg.id)),
          counted: approval.counted.filter((c) => c.signature === sg.id).length,
          required: Math.max(1, num(sg.required) ?? 1),
          finished: [...new Set(approval.answered.filter((a) => a.signature === sg.id).map((a) => a.signer))],
          complete: run.sigComplete(sg, approval.counted, approval),
        });
        return {
          gate: String(t.gate!.id),
          transition: t.id,
          signatures: approval.counted.map((c) => ({ ...c })),
          satisfied: approval.waiting,
          stages: arr(t.gate!.stages).map((st) => {
            const own = sigs.filter((sg) => sg.stage === st.id);
            return {
              stage: String(st.id),
              complete: own.every((sg) => !approval.needed.has(String(sg.id)) || run.sigComplete(sg, approval.counted, approval)),
              signatures: own.map(state),
            };
          }),
        };
      }
      const signed = run.signatures.get(t.id) ?? [];
      return {
        gate: String(t.gate!.id),
        transition: t.id,
        signatures: signed.map((s) => ({ ...s })),
        satisfied: gateSatisfied(t.gate!, signed, run.membership),
      };
    });
  const from = options.from ?? -1;
  return {
    trace: traces.filter((t) => t.index >= from),
    configuration: run.leaves().map((s) => s.id),
    context: sorted(run.context),
    enabled: run.done ? [] : enabledOf(run, active),
    pending: run.pending.map((p) => ({ invoke: String(p.invoke.id), state: p.state.id })),
    timers: [...run.timers].sort(timerOrder).map((t) => ({ transition: t.edge.id, dueAt: clockText(t.due) })),
    gates,
    final: run.done,
    clock: traces[traces.length - 1].clock,
    diagnostics,
  };
}

// The transitions reachable from the active atomic states: each state, then its ancestors (priority order within one).
function activeTransitions(run: Interpreter): Edge[] {
  const seen = new Set<string>();
  const out: Edge[] = [];
  for (const leaf of run.leaves())
    for (const state of [leaf, ...ancestorsOf(leaf).reverse()])
      for (const t of [...state.transitions, ...state.timers])
        if (!seen.has(t.id)) {
          seen.add(t.id);
          out.push(t);
        }
  return out;
}

function enabledOf(run: Interpreter, active: Edge[]): SimulationJson["enabled"] {
  const chart = run.chart;
  const unknown = (t: Edge) => !!t.guard && !chart.compiled.has(String(t.guard.id));
  const out: SimulationJson["enabled"] = [];
  for (const ev of chart.events.values()) {
    const candidates = active.filter((t) => t.trigger === "event" && t.key === ev.id);
    if (!candidates.length) continue;
    let gate: { have: number; need: number } | null = null;
    let actors = ids(ev.actors);
    const gated = candidates.find((t) => t.gate);
    let responses: Response[] | undefined;
    const approval = gated ? run.approvalOf(gated) : null;
    if (gated && approval) {
      const open = gateSignatures(gated.gate).filter((sg) => approval.needed.has(String(sg.id)));
      gate = {
        have: open.reduce((n, sg) => n + approval.counted.filter((c) => c.signature === sg.id).length, 0),
        need: open.reduce((n, sg) => n + Math.max(1, num(sg.required) ?? 1), 0),
      };
      if (!actors.length) actors = [...new Set(open.flatMap((sg) => ids(sg.signers)))];
      responses = approval.waiting ? ["sign"] : [...RESPONSES];
    } else if (gated) {
      gate = { have: run.signatures.get(gated.id)?.length ?? 0, need: Math.max(1, typeof gated.gate!.required === "number" ? gated.gate!.required : 1) };
      if (!actors.length) actors = ids(gated.gate!.signers);
    }
    out.push({
      trigger: "event",
      event: String(ev.id),
      invoke: null,
      transitions: candidates.map((t) => t.id),
      actors: run.membership.withMembers(actors),
      guardUnknown: candidates.some(unknown),
      ...checksOf(run, candidates),
      gate,
      ...(responses ? { responses } : {}),
    });
  }
  for (const p of run.pending)
    for (const trigger of ["invoke-done", "invoke-error"]) {
      const candidates = active.filter((t) => t.trigger === trigger && t.key === p.invoke.id);
      out.push({
        trigger,
        event: null,
        invoke: String(p.invoke.id),
        transitions: candidates.map((t) => t.id),
        actors: p.invoke.type === "human-task" ? run.membership.withMembers(ids(p.invoke.actors)) : [],
        guardUnknown: candidates.some(unknown),
        ...checksOf(run, candidates),
        gate: null,
      });
    }
  if (run.timers.length)
    out.push({
      trigger: "time",
      event: null,
      invoke: null,
      transitions: [...run.timers].sort(timerOrder).map((t) => t.edge.id),
      actors: [],
      guardUnknown: active.filter((t) => run.timers.some((x) => x.edge === t)).some(unknown),
      ...checksOf(
        run,
        run.timers.map((t) => t.edge),
      ),
      gate: null,
    });
  return out;
}

/** `checksUnknown`, left out when there are none. */
function checksOf(run: Interpreter, edges: Edge[]): { checksUnknown?: string[] } {
  const ids = run.checksUnknown(edges);
  return ids.length ? { checksUnknown: ids } : {};
}

/** Every state id of a state tree (the states, and the states in them). */
function stateIds(list: unknown, out = new Set<string>()): Set<string> {
  for (const s of arr(list)) {
    out.add(String(s.id));
    stateIds(s.states, out);
  }
  return out;
}

/** Adds the state `target`, its ancestors and the states a compound or parallel state enters by default to `out`. */
function enterState(list: unknown, target: string, out: Set<string>): boolean {
  for (const s of arr(list)) {
    if (s.id === target) {
      out.add(String(s.id));
      enterInside(s, out);
      return true;
    }
    if (enterState(s.states, target, out)) {
      out.add(String(s.id));
      return true;
    }
  }
  return false;
}

function enterInside(s: Json, out: Set<string>) {
  const children = arr(s.states).filter((c) => c.type !== "history" && c.type !== "choice");
  if (!children.length) return;
  const regions = s.type === "parallel" ? children : [children.find((c) => c.id === s.initial) ?? children[0]];
  for (const c of regions) {
    out.add(String(c.id));
    enterInside(c, out);
  }
}

function enterInitial(list: unknown, initial: string | null, out: Set<string>) {
  const states = arr(list).filter((c) => c.type !== "history" && c.type !== "choice");
  const first = states.find((c) => c.id === initial) ?? states[0];
  if (!first) return;
  out.add(String(first.id));
  enterInside(first, out);
}

export interface Replay {
  /** Every input ran and none stopped the replay (MQ9305, MQ9306, MQ9507). */
  complete: boolean;
  /** The observed `expect` of each step: accepted only when false, the states, the changed context. */
  expects: Json[];
  outcome: "final" | "active";
  diagnostics: DiagnosticJson[];
}

/** Replays steps and observes each one's expectation, as the record operation fills `expect` and `outcome`. */
export function replay(doc: Json, start: { context?: Json; at?: string } | null | undefined, steps: Json[], options: StepperOptions = {}): Replay {
  const result = simulate(doc, start, steps, options);
  const traces = result.trace.filter((t) => t.index >= 0);
  const complete = traces.length === steps.length && !result.trace.some((t) => t.incomplete);
  const expects = traces.map((t) => {
    const expect: Json = {};
    if (!t.accepted) expect.accepted = false;
    expect.states = t.configuration;
    if (Object.keys(t.changed).length) expect.context = t.changed;
    if (t.valuesChanged && Object.keys(t.valuesChanged).length) expect.values = t.valuesChanged;
    // A refusal and its element are written when the project uses rights or trigger points (decision 26), and a frozen refusal always.
    if (
      !t.accepted &&
      t.refusal &&
      (usesRights(options.lifecycles ?? []) || usesTriggers([doc, ...(options.lifecycles ?? [])], options.namedPoints) || t.refusal === "frozen")
    ) {
      expect.refusal = t.refusal;
      if (t.refusedBy) expect.refusedBy = t.refusedBy;
    }
    // The checks that warned are written in a project that uses trigger points; the points that ran only when asked (decision 28).
    if (t.checkWarnings?.length) expect.checkWarnings = t.checkWarnings;
    // A step that answered an approval records the signatures counted after it (decision 27).
    const counted = signatureCounts.get(t);
    if (counted) expect.signatures = counted;
    // Written only when some ran: a step that expected points and now has none drops the key (as the engine's Observed does).
    const ran = (t.triggerPoints ?? []).filter((r) => r.outcome !== "skipped").map((r) => r.triggerPoint);
    const expected = (steps[t.index]?.expect as Json | undefined)?.triggerPoints !== undefined;
    if ((options.recordTriggerPoints || expected) && ran.length) expect.triggerPoints = ran;
    return expect;
  });
  return { complete, expects, outcome: result.final ? "final" : "active", diagnostics: result.diagnostics };
}

const strsOf = (v: unknown): string[] => ids(v);

function uniqueHoldings(list: Holding[]): Holding[] {
  const seen = new Set<string>();
  return list.filter((h) => {
    const key = `${h.right}|${h.lifecycle}|${h.access}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}
