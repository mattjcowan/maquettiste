// The mock's reduced statechart interpreter (phase-3-design.md 4.1), used only in mock mode to answer simulate and
// record with a real trace for any input list. The engine's StatechartInterpreter is the reference: this file follows
// its rules (configuration, macrostep and microstep, selection with priority and conflict removal, exit and entry sets,
// shallow and deep history, `done` for compound and parallel states, a simulated clock, pending invokes, gates with
// audit records, actors and refusal reasons) and leaves out what the mock does not need: sub-process instances (a
// `process` invoke waits like a service task), MQ9505 type checks, MQ9506 overlap reports and the sandbox's limits.
// Guards and actions with an expression run through `new Function` over frozen copies of the context and event.

type Json = Record<string, unknown>;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string | null => (typeof v === "string" && v !== "" ? v : null);
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
  assume: Record<string, boolean>;
  expect: null;
  description: unknown;
}

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
  microsteps: { transitions: string[]; exited: string[]; entered: string[]; actions: { action: string; source: "expression" | "stub"; changed: Json }[] }[];
  guards: { guard: string; transition: string; result: boolean | null; source: "expression" | "assumed" | "missing" }[];
  audit: Json[];
  configuration: string[];
  context: Json;
  changed: Json;
  clock: string;
  final: boolean;
  diagnostics: DiagnosticJson[];
  incomplete: boolean;
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
    gate: { have: number; need: number } | null;
  }[];
  pending: { invoke: string; state: string }[];
  timers: { transition: string; dueAt: string }[];
  gates: {
    gate: string;
    transition: string;
    signatures: { signer: string; actor: string; meaning: string | null; reason: string | null }[];
    satisfied: boolean;
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
}

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
  const assume: Record<string, boolean> = {};
  for (const [k, v] of Object.entries((step.assume as Json | undefined) ?? {})) if (typeof v === "boolean") assume[byName(guards, k) ?? k] = v;
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
  };
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
}

interface Scope {
  input: SimInputJson | null;
  event: Json;
  refusal: string | null;
  microsteps: StepTraceJson["microsteps"];
  guards: StepTraceJson["guards"];
  audit: Json[];
  diagnostics: DiagnosticJson[];
}

class Overflow extends Error {}

const emptyEvent = (): Json => ({ name: null, actor: null, payload: {} });
const newScope = (input: SimInputJson | null): Scope => ({ input, event: emptyEvent(), refusal: null, microsteps: [], guards: [], audit: [], diagnostics: [] });

class Interpreter {
  readonly config = new Set<Node>();
  readonly history = new Map<string, Node[]>();
  context: Json = {};
  timers: Timer[] = [];
  pending: { invoke: Json; state: Node }[] = [];
  readonly signatures = new Map<string, Signature[]>();
  queue: { kind: string; id: string }[] = [];
  done = false;
  clock = Date.parse(DEFAULT_START);
  private auditSeq = 0;
  private timerSeq = 0;
  private index = -1;
  private micro = 0;
  private scope: Scope = newScope(null);

  constructor(
    readonly chart: Chart,
    private readonly actorName: (id: string) => string | undefined = () => undefined,
  ) {}

  leaves(): Node[] {
    return [...this.config].filter((s) => !s.isRoot && isLeaf(s)).sort((a, b) => a.order - b.order);
  }

  start(context: Json | undefined, at: number | null): StepTraceJson {
    if (at !== null) this.clock = at;
    for (const a of this.chart.attributes) this.context[String(a.id)] = a.default ?? null;
    for (const [id, v] of Object.entries(context ?? {})) if (this.chart.attributes.some((a) => a.id === id)) this.context[id] = v;
    this.scope = newScope(null);
    const before = { ...this.context };
    this.macrostep(() => {
      this.config.add(this.chart.root);
      if (this.chart.root.initialChild) this.microstep([], this.chart.root.initialChild);
      this.runToCompletion();
    });
    return this.finish(before);
  }

  step(input: SimInputJson): StepTraceJson {
    this.index++;
    this.scope = newScope(input);
    const before = { ...this.context };
    if (input.input === "time") this.handleTime(input);
    else if (input.input === "invoke-done" || input.input === "invoke-error") this.handleInvoke(input);
    else this.handleEvent(input);
    return this.finish(before);
  }

  // ---- inputs ----

  private handleEvent(input: SimInputJson) {
    if (this.done) return this.refuse("no-transition");
    const ev = input.event ? this.chart.events.get(input.event) : undefined;
    if (!ev) {
      this.problem("MQ9305", `The event '${input.event}' is not an event of process '${String(this.chart.doc.name)}'; pick one of its events.`);
      return this.refuse("no-transition");
    }
    const actors = ids(ev.actors);
    if (actors.length && (!input.actor || !actors.includes(input.actor))) return this.refuse("actor");
    this.scope.event = this.eventObject(String(ev.name), input.actor, arr(ev.payload), input.payload);
    this.macrostep(() => {
      const selection = this.select("event", String(ev.id), true);
      if (!selection.transitions.length && !selection.consumed) return this.refuse(selection.reason);
      if (selection.transitions.length) this.microstep(selection.transitions);
      this.runToCompletion();
    });
  }

  private handleTime(input: SimInputJson) {
    const span = durationMs(input.after);
    if (span === null || span <= 0) {
      this.problem("MQ9305", `A time step needs a positive ISO 8601 duration in after (for example P5D), not '${input.after}'.`);
      return this.refuse("no-transition");
    }
    const end = this.clock + span;
    this.scope.event = this.eventObject(null, input.actor, [], {});
    while (!this.done) {
      const next = this.timers.filter((t) => t.due <= end).sort(timerOrder)[0];
      if (!next) break;
      this.clock = next.due;
      this.timers = this.timers.filter((t) => t !== next);
      this.macrostep(() => {
        if (this.guard(next.edge) !== true) return;
        this.microstep([next.edge]);
        this.runToCompletion();
      });
    }
    this.clock = end;
  }

  private handleInvoke(input: SimInputJson) {
    const found = this.pending.find((p) => p.invoke.id === input.invoke);
    if (!found) {
      const name = input.invoke ? (this.chart.invokes.get(input.invoke)?.invoke.name ?? input.invoke) : input.invoke;
      this.problem(
        "MQ9305",
        `The invoke '${String(name)}' is not pending (its state is not active, or it already completed); complete an invoke of an active state.`,
      );
      return this.refuse("no-transition");
    }
    const actors = ids(found.invoke.actors);
    if (found.invoke.type === "human-task" && actors.length && (!input.actor || !actors.includes(input.actor))) return this.refuse("actor");
    this.scope.event = this.eventObject(null, input.actor, [], {});
    this.macrostep(() => {
      const selection = this.selectAt(found.state, input.input, String(found.invoke.id));
      if (!selection.transitions.length) return this.refuse(selection.reason);
      this.pending = this.pending.filter((p) => p.invoke !== found.invoke);
      this.microstep(selection.transitions);
      this.runToCompletion();
    });
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

  private select(trigger: string, key: string, external: boolean): { transitions: Edge[]; consumed: boolean; reason: string } {
    const enabled: Edge[] = [];
    const tried = new Map<Edge, boolean>();
    let consumed = false;
    let reason = "no-transition";
    let rank = 0;
    const fail = (why: string, level: number) => {
      if (level > rank) {
        rank = level;
        reason = why;
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
            fail("guard", 1);
            continue;
          }
          if (t.gate) {
            if (!external) {
              fail("gate-signer", 2);
              continue;
            }
            const refusal = this.checkSigner(t, t.gate);
            if (refusal) {
              fail(refusal, 2);
              continue;
            }
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
      if (!t.gate || this.sign(t, t.gate)) return true;
      consumed = true; // the occurrence signed an incomplete gate: accepted, and the transition waits for more signatures
      return false;
    });
    return { transitions: selected, consumed, reason };
  }

  private selectAt(source: Node, trigger: string, key: string): { transitions: Edge[]; reason: string } {
    let reason = "no-transition";
    for (const t of this.chart.candidates(source, trigger, key)) {
      if (this.guard(t) === true) return { transitions: [t], reason };
      reason = "guard";
    }
    return { transitions: [], reason };
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
    let source: "expression" | "stub" = "stub";
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
    for (const raised of ids(action.raises)) if (this.chart.events.has(raised)) this.queue.push({ kind: "event", id: raised });
    return { action: id, source, changed: sorted(changed) };
  }

  // ---- gates ----

  private checkSigner(t: Edge, gate: Json): string | null {
    const input = this.scope.input!;
    const signatures = this.signatures.get(t.id) ?? [];
    let refusal: string | null = null;
    if (!input.actor || !input.signer || !ids(gate.signers).includes(input.actor)) refusal = "gate-signer";
    else if (gate.allowRepeatSigner !== true && signatures.some((s) => s.signer === input.signer)) refusal = "gate-repeat";
    else if (gate.reasonRequired === true && !(input.reason ?? "").trim()) refusal = "gate-reason";
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
    const complete = gateSatisfied(gate, signatures);
    if (complete) {
      this.audit(gate, t, input.signer, input.actor, meaning, input.reason, "completed", attributes);
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
    });
  }

  // ---- microstep ----

  private microstep(transitions: Edge[], initial?: Node) {
    if (++this.micro > MAX_MICROSTEPS) throw new Overflow();
    const exited: string[] = [];
    const entered: string[] = [];
    const actions: StepTraceJson["microsteps"][number]["actions"] = [];

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
    for (const s of [...toEnter].sort((a, b) => a.order - b.order)) {
      if (s.isRoot || this.config.has(s)) continue;
      this.config.add(s);
      entered.push(s.id);
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

  private finish(before: Json): StepTraceJson {
    const changed: Json = {};
    for (const [id, v] of Object.entries(this.context)) if (!(id in before) || !same(before[id], v)) changed[id] = v;
    const seen = new Set<string>();
    const diagnostics = this.scope.diagnostics.filter((d) => {
      const key = `${d.rule}|${d.message}|${d.elementId}`;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
    return {
      index: this.index,
      input: this.scope.input,
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
    };
  }

  private refuse(reason: string) {
    this.scope.refusal ??= reason;
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

function gateSatisfied(gate: Json, signatures: Signature[]): boolean {
  const required = Math.max(1, typeof gate.required === "number" ? gate.required : 1);
  return signatures.length >= required && ids(gate.requiredActors).every((a) => signatures.some((s) => s.actor === a));
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
  const run = new Interpreter(chart, options.actorName);
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
      const signed = run.signatures.get(t.id) ?? [];
      return { gate: String(t.gate!.id), transition: t.id, signatures: signed.map((s) => ({ ...s })), satisfied: gateSatisfied(t.gate!, signed) };
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
    if (gated) {
      gate = { have: run.signatures.get(gated.id)?.length ?? 0, need: Math.max(1, typeof gated.gate!.required === "number" ? gated.gate!.required : 1) };
      if (!actors.length) actors = ids(gated.gate!.signers);
    }
    out.push({
      trigger: "event",
      event: String(ev.id),
      invoke: null,
      transitions: candidates.map((t) => t.id),
      actors,
      guardUnknown: candidates.some(unknown),
      gate,
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
        actors: p.invoke.type === "human-task" ? ids(p.invoke.actors) : [],
        guardUnknown: candidates.some(unknown),
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
      gate: null,
    });
  return out;
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
    return expect;
  });
  return { complete, expects, outcome: result.final ? "final" : "active", diagnostics: result.diagnostics };
}
