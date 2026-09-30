// The process editor's pure model (phase-3-design.md 2.2-2.4 and 6.2): the rows its grids show (the state tree, the
// transitions, guards, actions and events, the gates, the scenarios and their steps) and the edits they make on a
// process document. Every edit mutates the draft document it is given, so one grid gesture is one draft change and one
// undo step. Ids are ULIDs (lib/ids); names are made unique where the model needs them unique.
import type { AttributeDoc, Diagnostic } from "@/api/types";
import { newId } from "@/lib/ids";

export type Json = Record<string, unknown>;

export const STATE_TYPES = ["atomic", "compound", "parallel", "final", "history", "choice"] as const;
export type StateType = (typeof STATE_TYPES)[number];
export const TRIGGERS = ["event", "after", "done", "always", "invoke-done", "invoke-error"] as const;
export type Trigger = (typeof TRIGGERS)[number];
export const INVOKE_TYPES = ["process", "service", "human-task"] as const;
export const STEP_INPUTS = ["event", "time", "invoke-done", "invoke-error"] as const;

export interface InvokeDoc {
  id: string;
  name: string;
  displayName?: string;
  type?: (typeof INVOKE_TYPES)[number];
  process?: string;
  actors?: string[];
  description?: unknown;
}

export interface StateDoc {
  id: string;
  name: string;
  displayName?: string;
  type?: StateType;
  initial?: string;
  history?: "shallow" | "deep";
  defaultTarget?: string;
  entry?: string[];
  exit?: string[];
  invoke?: InvokeDoc[];
  states?: StateDoc[];
  description?: unknown;
  stereotypes?: string[];
  properties?: Json;
}

export interface MeaningDoc {
  id: string;
  name: string;
  displayName?: string;
  description?: unknown;
}

export interface GateDoc {
  id: string;
  name: string;
  displayName?: string;
  required?: number;
  signers?: string[];
  requiredActors?: string[];
  allowRepeatSigner?: boolean;
  reasonRequired?: boolean;
  meanings?: MeaningDoc[];
  auditAttributes?: AttributeDoc[];
  description?: unknown;
}

export interface TransitionDoc {
  id: string;
  displayName?: string;
  source: string;
  trigger?: Trigger;
  event?: string;
  after?: string;
  invoke?: string;
  guard?: string;
  targets?: string[];
  actions?: string[];
  external?: boolean;
  gate?: GateDoc;
  description?: unknown;
}

export interface EventDoc {
  id: string;
  name: string;
  displayName?: string;
  payload?: AttributeDoc[];
  actors?: string[];
  description?: unknown;
}

export interface ExpressionDoc {
  id: string;
  name: string;
  displayName?: string;
  expression?: string;
  raises?: string[];
  description?: unknown;
}

export interface ProcessDoc {
  kind: "process";
  id: string;
  name: string;
  package?: string;
  use?: "lifecycle" | "orchestration";
  subject?: string;
  boundAttribute?: string;
  context?: AttributeDoc[];
  events?: EventDoc[];
  guards?: ExpressionDoc[];
  actions?: ExpressionDoc[];
  states: StateDoc[];
  initial?: string;
  transitions?: TransitionDoc[];
  source?: Json;
  generation?: Json;
}

export interface StepDoc {
  id: string;
  input?: (typeof STEP_INPUTS)[number];
  event?: string;
  invoke?: string;
  after?: string;
  actor?: string;
  signer?: string;
  meaning?: string;
  reason?: string;
  payload?: Json;
  assume?: Record<string, boolean>;
  expect?: { accepted?: boolean; states?: string[]; context?: Json };
  description?: unknown;
}

export interface ScenarioDoc {
  kind: "scenario";
  id: string;
  name: string;
  process: string;
  start?: { context?: Json; at?: string };
  steps: StepDoc[];
  outcome?: "final" | "active";
}

// ------------------------------------------------------------------ the state tree

export interface StateInfo {
  state: StateDoc;
  parent: StateDoc | null;
  depth: number;
  /** Dotted path from the root (`Review.Budget.Checking`). */
  path: string;
  /** JSON pointer in the process document (`/states/1/states/0`). */
  pointer: string;
}

/** Every state of a process in document order (depth first), with its parent, path and pointer. */
export function stateIndex(process: Pick<ProcessDoc, "states">): Map<string, StateInfo> {
  const out = new Map<string, StateInfo>();
  const walk = (list: StateDoc[] | undefined, parent: StateDoc | null, depth: number, path: string, pointer: string) =>
    (list ?? []).forEach((state, i) => {
      const p = path ? `${path}.${state.name}` : state.name;
      const ptr = `${pointer}/states/${i}`;
      out.set(state.id, { state, parent, depth, path: p, pointer: ptr });
      walk(state.states, state, depth + 1, p, ptr);
    });
  walk(process.states, null, 0, "", "");
  return out;
}

/** The children list a state lives in (the process's root list for a root state). */
function siblingsOf(process: ProcessDoc, parent: StateDoc | null): StateDoc[] {
  if (!parent) return process.states;
  parent.states ??= [];
  return parent.states;
}

/** The initial child of a process or a compound state: `initial` when set, else the first child. */
export function initialOf(holder: { initial?: string; states?: StateDoc[] }): string | undefined {
  return holder.initial ?? holder.states?.[0]?.id;
}

/** The root states a lifecycle binds to its enum's members: not history or choice, in document order (2.3). */
export function boundStates(process: Pick<ProcessDoc, "states">): StateDoc[] {
  return process.states.filter((s) => s.type !== "history" && s.type !== "choice");
}

export interface StateRow {
  id: string;
  name: string;
  depth: number;
  path: string;
  pointer: string;
  type: StateType;
  /** The initial child of its parent (or of the process). */
  initial: boolean;
  history: string;
  entry: string;
  exit: string;
  invokes: string;
  /** The enum member this root state stands for in a lifecycle ("" otherwise). */
  bound: string;
  /** The bound member differs from the state's name (MQ9203). */
  drift: boolean;
  children: number;
}

const nameOf = (list: { id: string; name: string }[] | undefined) => {
  const map = new Map((list ?? []).map((x) => [x.id, x.name]));
  return (ids: string[] | undefined) => (ids ?? []).map((id) => map.get(id) ?? "?").join(", ");
};

/** The States tab's tree grid rows; `members` are the bound enum's member names for a lifecycle. */
export function stateRows(process: ProcessDoc, members: string[] | null = null): StateRow[] {
  const actions = nameOf(process.actions);
  const bound = process.use === "lifecycle" && members ? boundStates(process).map((s) => s.id) : [];
  const rows: StateRow[] = [];
  for (const [id, info] of stateIndex(process)) {
    const s = info.state;
    const type = s.type ?? "atomic";
    const at = info.parent ? -1 : bound.indexOf(id);
    const member = at >= 0 ? (members?.[at] ?? "") : "";
    rows.push({
      id,
      name: s.name,
      depth: info.depth,
      path: info.path,
      pointer: info.pointer,
      type,
      initial: initialOf(info.parent ?? process) === id && type !== "history",
      history: type === "history" ? (s.history ?? "shallow") : "",
      entry: actions(s.entry),
      exit: actions(s.exit),
      invokes: (s.invoke ?? []).map((i) => i.name).join(", "),
      bound: member,
      drift: at >= 0 && member !== s.name,
      children: s.states?.length ?? 0,
    });
  }
  return rows;
}

/** A name not yet used in `taken`: `base`, then `base2`, `base3`, ... */
export function uniqueName(taken: Iterable<string>, base: string): string {
  const set = new Set(taken);
  if (!set.has(base)) return base;
  let n = 2;
  while (set.has(`${base}${n}`)) n++;
  return `${base}${n}`;
}

/** Adds a state: a child of `parent` (null: the root), after `after` when given, else last. Returns its id. */
export function addState(process: ProcessDoc, parent: string | null, after?: string): string {
  const index = stateIndex(process);
  const holder = parent ? (index.get(parent)?.state ?? null) : null;
  const list = siblingsOf(process, holder);
  const id = newId();
  const state: StateDoc = {
    id,
    name: uniqueName(
      list.map((s) => s.name),
      "State",
    ),
  };
  const at = after ? list.findIndex((s) => s.id === after) : -1;
  list.splice(at < 0 ? list.length : at + 1, 0, state);
  // A child makes an atomic parent compound.
  if (holder && (holder.type ?? "atomic") === "atomic") holder.type = "compound";
  return id;
}

/** Adds a sibling after a state (a child of its parent). */
export function addSiblingState(process: ProcessDoc, of: string): string {
  const parent = stateIndex(process).get(of)?.parent ?? null;
  return addState(process, parent?.id ?? null, of);
}

/** The ids of a state and its descendants. */
export function subtreeIds(process: ProcessDoc, id: string): Set<string> {
  const ids = new Set<string>();
  const state = stateIndex(process).get(id)?.state;
  const walk = (s: StateDoc) => {
    ids.add(s.id);
    for (const c of s.states ?? []) walk(c);
  };
  if (state) walk(state);
  return ids;
}

/** Transitions from outside a state's subtree that enter it (or a descendant): they block its delete. */
export function incomingTransitions(process: ProcessDoc, id: string): TransitionDoc[] {
  const inside = subtreeIds(process, id);
  return (process.transitions ?? []).filter((t) => !inside.has(t.source) && (t.targets ?? []).some((x) => inside.has(x)));
}

export type DeleteStateResult = { ok: true } | { ok: false; reason: string };

/** Deletes a state and its subtree with the transitions leaving them; refused while a transition enters it, or for
 * the only root state. */
export function deleteState(process: ProcessDoc, id: string): DeleteStateResult {
  const index = stateIndex(process);
  const info = index.get(id);
  if (!info) return { ok: false, reason: "The state no longer exists." };
  if (!info.parent && process.states.length === 1) return { ok: false, reason: "A process keeps at least one state." };
  const incoming = incomingTransitions(process, id);
  if (incoming.length) {
    const label = transitionLabeller(process, index);
    return { ok: false, reason: `Transitions still enter ${info.state.name}: ${incoming.map(label).join("; ")}. Change or delete them first.` };
  }
  const inside = subtreeIds(process, id);
  const list = siblingsOf(process, info.parent);
  list.splice(
    list.findIndex((s) => s.id === id),
    1,
  );
  const holder: { initial?: string } = info.parent ?? process;
  if (holder.initial === id) delete holder.initial;
  if (info.parent && !list.length) {
    delete info.parent.states;
    if (info.parent.type === "compound") delete info.parent.type;
  }
  if (process.transitions) process.transitions = process.transitions.filter((t) => !inside.has(t.source));
  for (const { state } of stateIndex(process).values()) if (state.defaultTarget && inside.has(state.defaultTarget)) delete state.defaultTarget;
  return { ok: true };
}

/** Makes a state the initial child of its parent (or the process): `initial` is omitted when it is the first child. */
export function setInitial(process: ProcessDoc, id: string): void {
  const info = stateIndex(process).get(id);
  if (!info) return;
  const holder: { initial?: string; states?: StateDoc[] } = info.parent ?? process;
  if (holder.states?.[0]?.id === id) delete holder.initial;
  else holder.initial = id;
}

/** Sets a state's type, dropping the keys the new type does not have. */
export function setStateType(state: StateDoc, type: StateType): void {
  if (type === "atomic") delete state.type;
  else state.type = type;
  if (type !== "compound") delete state.initial;
  if (type !== "history") {
    delete state.history;
    delete state.defaultTarget;
  }
}

// ------------------------------------------------------------------ transitions, guards, actions, events

export interface TransitionRow {
  id: string;
  index: number;
  pointer: string;
  source: string;
  trigger: Trigger;
  /** The event's name, the duration or the invoke's name, per trigger. */
  detail: string;
  guard: string;
  targets: string;
  actions: string;
  external: boolean;
  gate: string;
}

function transitionLabeller(process: ProcessDoc, index = stateIndex(process)) {
  const events = new Map((process.events ?? []).map((e) => [e.id, e.name]));
  return (t: TransitionDoc) => {
    const src = index.get(t.source)?.path ?? "?";
    const on = triggerDetail(process, t, events);
    const to = (t.targets ?? []).map((x) => index.get(x)?.path ?? "?").join(", ") || "(no target)";
    return `${src} ${on ? `· ${on} ` : ""}→ ${to}`;
  };
}

function invokeName(process: ProcessDoc, id: string | undefined): string {
  if (!id) return "";
  for (const { state } of stateIndex(process).values()) for (const i of state.invoke ?? []) if (i.id === id) return i.name;
  return "?";
}

function triggerDetail(process: ProcessDoc, t: TransitionDoc, events: Map<string, string>): string {
  const trigger = t.trigger ?? "event";
  if (trigger === "event") return t.event ? (events.get(t.event) ?? "?") : "";
  if (trigger === "after") return t.after ?? "";
  if (trigger === "invoke-done" || trigger === "invoke-error") return invokeName(process, t.invoke);
  return trigger;
}

/** A transition as one line (`Drafting · submit → Review`), for used-by lists and messages. */
export function transitionLabel(process: ProcessDoc, t: TransitionDoc): string {
  return transitionLabeller(process)(t);
}

export function transitionRows(process: ProcessDoc): TransitionRow[] {
  const index = stateIndex(process);
  const events = new Map((process.events ?? []).map((e) => [e.id, e.name]));
  const guards = new Map((process.guards ?? []).map((g) => [g.id, g.name]));
  const actions = nameOf(process.actions);
  return (process.transitions ?? []).map((t, i) => ({
    id: t.id,
    index: i,
    pointer: `/transitions/${i}`,
    source: index.get(t.source)?.path ?? "?",
    trigger: t.trigger ?? "event",
    detail: triggerDetail(process, t, events),
    guard: t.guard ? (guards.get(t.guard) ?? "?") : "",
    targets: (t.targets ?? []).map((x) => index.get(x)?.path ?? "?").join(", "),
    actions: actions(t.actions),
    external: t.external === true,
    gate: t.gate ? t.gate.displayName || t.gate.name : "",
  }));
}

/** Adds a transition from `source` (else the first state) on the first event; returns its id. */
export function addTransition(process: ProcessDoc, source?: string, after?: string): string {
  const id = newId();
  const t: TransitionDoc = { id, source: source ?? process.states[0].id };
  const event = process.events?.[0]?.id;
  if (event) t.event = event;
  else t.trigger = "always";
  const list = (process.transitions ??= []);
  const at = after ? list.findIndex((x) => x.id === after) : -1;
  list.splice(at < 0 ? list.length : at + 1, 0, t);
  return id;
}

/** Sets a transition's trigger, keeping only the keys it uses (event, after or invoke) and the gate on events. */
export function setTrigger(t: TransitionDoc, trigger: Trigger, process: ProcessDoc): void {
  if (trigger === "event") delete t.trigger;
  else t.trigger = trigger;
  if (trigger !== "event") {
    delete t.event;
    delete t.gate;
  } else if (!t.event && process.events?.[0]) t.event = process.events[0].id;
  if (trigger !== "after") delete t.after;
  else t.after ??= "PT1H";
  if (trigger !== "invoke-done" && trigger !== "invoke-error") delete t.invoke;
  else t.invoke ??= stateIndex(process).get(t.source)?.state.invoke?.[0]?.id;
}

/** What uses each event, guard and action of a process: one line per transition or state. */
export function usedBy(process: ProcessDoc): Map<string, string[]> {
  const out = new Map<string, string[]>();
  const add = (id: string | undefined, what: string) => {
    if (!id) return;
    const list = out.get(id) ?? [];
    if (!list.includes(what)) list.push(what);
    out.set(id, list);
  };
  const label = transitionLabeller(process);
  for (const t of process.transitions ?? []) {
    const line = label(t);
    add(t.event, line);
    add(t.guard, line);
    for (const a of t.actions ?? []) add(a, line);
  }
  for (const { state, path } of stateIndex(process).values()) {
    for (const a of state.entry ?? []) add(a, `${path} (entry)`);
    for (const a of state.exit ?? []) add(a, `${path} (exit)`);
  }
  for (const a of process.actions ?? []) for (const e of a.raises ?? []) add(e, `${a.name} (raises)`);
  return out;
}

export interface NamedRow {
  id: string;
  name: string;
  expression: string;
  description: string;
  usedBy: string;
  /** The event's actors (names), for the Events grid. */
  actors: string;
  pointer: string;
}

const text = (v: unknown) => (typeof v === "string" ? v : "");

/** The Guards, Actions or Events grid rows. */
export function namedRows(process: ProcessDoc, field: "guards" | "actions" | "events", actorNames: Map<string, string> = new Map()): NamedRow[] {
  const uses = usedBy(process);
  const list = (process[field] ?? []) as (ExpressionDoc & EventDoc)[];
  return list.map((x, i) => ({
    id: x.id,
    name: x.name,
    expression: x.expression ?? "",
    description: text(x.description),
    usedBy: (uses.get(x.id) ?? []).join("; "),
    actors: (x.actors ?? []).map((a) => actorNames.get(a) ?? "?").join(", "),
    pointer: `/${field}/${i}`,
  }));
}

/** Adds a guard, an action or an event with a unique name; returns its id. */
export function addNamed(process: ProcessDoc, field: "guards" | "actions" | "events", after?: string): string {
  const list = (process[field] ??= []) as { id: string; name: string }[];
  const id = newId();
  const base = field === "guards" ? "guard" : field === "actions" ? "action" : "event";
  const at = after ? list.findIndex((x) => x.id === after) : -1;
  list.splice(at < 0 ? list.length : at + 1, 0, {
    id,
    name: uniqueName(
      list.map((x) => x.name),
      base,
    ),
  });
  return id;
}

/** Removes a guard, action or event and every reference to it (transitions, entry and exit lists, raises). */
export function removeNamed(process: ProcessDoc, field: "guards" | "actions" | "events", id: string): void {
  const list = process[field] as { id: string }[] | undefined;
  if (!list) return;
  (process as unknown as Json)[field] = list.filter((x) => x.id !== id);
  if (!(process[field] as unknown[]).length) delete (process as unknown as Json)[field];
  for (const t of process.transitions ?? []) {
    if (t.guard === id) delete t.guard;
    if (t.event === id) delete t.event;
    if (t.actions?.includes(id)) {
      t.actions = t.actions.filter((a) => a !== id);
      if (!t.actions.length) delete t.actions;
    }
  }
  for (const { state } of stateIndex(process).values())
    for (const key of ["entry", "exit"] as const) {
      const ids = state[key];
      if (!ids?.includes(id)) continue;
      state[key] = ids.filter((a) => a !== id);
      if (!state[key]!.length) delete state[key];
    }
  for (const a of process.actions ?? []) if (a.raises?.includes(id)) a.raises = a.raises.filter((e) => e !== id);
}

/** Why an expression does not parse as one JavaScript expression (MQ9501), or null when it does (or is empty). The
 * editor marks the field at once; the engine's diagnostic follows on save. */
export function expressionError(expression: string | undefined): string | null {
  const source = (expression ?? "").trim();
  if (!source) return null;
  try {
    // Parsed, never run: the body only returns the expression.
    new Function("context", "event", `"use strict"; return (\n${source}\n);`);
    return null;
  } catch (e) {
    return e instanceof Error ? e.message : String(e);
  }
}

/** Resolves a comma-separated list of names to ids (unknown names are skipped). */
export function idsOfNames(raw: string, options: { id: string; name: string }[]): string[] {
  const byName = new Map(options.map((o) => [o.name.toLowerCase(), o.id]));
  const out: string[] = [];
  for (const part of raw.split(",")) {
    const id = byName.get(part.trim().toLowerCase());
    if (id && !out.includes(id)) out.push(id);
  }
  return out;
}

// ------------------------------------------------------------------ gates

export interface GateForm {
  transition: string;
  transitionLabel: string;
  pointer: string;
  gate: GateDoc;
  /** N: signatures needed. */
  required: number;
  /** M: the signers' count. */
  of: number;
  signers: string[];
  requiredActors: string[];
  /** Required actors that are not signers (MQ9102). */
  strayRequired: string[];
}

/** One form per gated transition, in transition order. */
export function gateForms(process: ProcessDoc): GateForm[] {
  const label = transitionLabeller(process);
  return (process.transitions ?? []).flatMap((t, i) => {
    if (!t.gate) return [];
    const signers = t.gate.signers ?? [];
    const requiredActors = t.gate.requiredActors ?? [];
    return [
      {
        transition: t.id,
        transitionLabel: label(t),
        pointer: `/transitions/${i}/gate`,
        gate: t.gate,
        required: t.gate.required ?? 1,
        of: signers.length,
        signers,
        requiredActors,
        strayRequired: requiredActors.filter((a) => !signers.includes(a)),
      },
    ];
  });
}

/** A new gate for an event transition: one signer, one meaning. */
export function newGate(process: ProcessDoc, signer: string | undefined): GateDoc {
  const taken = (process.transitions ?? []).flatMap((t) => (t.gate ? [t.gate.name] : []));
  return { id: newId(), name: uniqueName(taken, "approval"), signers: signer ? [signer] : [], meanings: [{ id: newId(), name: "approved" }] };
}

// ------------------------------------------------------------------ scenarios

export type ScenarioStatus = { passed: true } | { passed: false; step: number };

export interface ScenarioRow {
  id: string;
  name: string;
  steps: number;
  status: "passed" | "failed" | "not-run";
  /** The failing step (1-based) when failed. */
  failedAt: number | null;
}

/** The Scenarios tab's rows: the process's scenarios with their last verify result. */
export function scenarioRows(
  scenarios: { id: string; name: string; displayName?: string | null; stepCount?: number | null }[],
  statuses: ReadonlyMap<string, ScenarioStatus>,
): ScenarioRow[] {
  return scenarios.map((s) => {
    const st = statuses.get(s.id);
    return {
      id: s.id,
      name: s.displayName || s.name,
      steps: s.stepCount ?? 0,
      status: !st ? "not-run" : st.passed ? "passed" : "failed",
      failedAt: st && !st.passed ? st.step : null,
    };
  });
}

export type StepStatus = "passed" | "failed" | "not-run";

/** Each step's replay status from a verify result: steps before the failure pass, the failing one fails, the rest
 * did not run. `failure` is the 0-based failing step, null when the scenario passed; undefined when never replayed. */
export function stepStatuses(count: number, failure: number | null | undefined): StepStatus[] {
  return Array.from({ length: count }, (_, i) =>
    failure === undefined ? "not-run" : failure === null || i < failure ? "passed" : i === failure ? "failed" : "not-run",
  );
}

export interface StepRow {
  id: string;
  index: number;
  pointer: string;
  input: string;
  /** The event's name, the invoke's name or the duration, per input. */
  on: string;
  actor: string;
  signer: string;
  meaning: string;
  reason: string;
  payload: string;
  assume: string;
  states: string;
  context: string;
  accepted: boolean;
}

/** `name=value` pairs of a map keyed by attribute (or guard) id, names from `names`. */
export function mapText(map: Json | undefined, names: Map<string, string>): string {
  return Object.entries(map ?? {})
    .map(([k, v]) => `${names.get(k) ?? k}=${typeof v === "string" ? v : JSON.stringify(v)}`)
    .join(", ");
}

/** Parses `name=value, ...` into a map keyed by id (values as JSON when they parse, else strings). */
export function parseMapText(raw: string, names: Map<string, string>): Json {
  const ids = new Map([...names].map(([id, name]) => [name.toLowerCase(), id]));
  const out: Json = {};
  for (const part of raw.split(",")) {
    const at = part.indexOf("=");
    if (at <= 0) continue;
    const key = part.slice(0, at).trim();
    const value = part.slice(at + 1).trim();
    const id = ids.get(key.toLowerCase()) ?? (names.has(key) ? key : null);
    if (!id) continue;
    try {
      out[id] = JSON.parse(value);
    } catch {
      out[id] = value;
    }
  }
  return out;
}

/** The names a scenario's grid shows for the ids its steps hold. */
export function stepNames(process: ProcessDoc | undefined, actorNames: Map<string, string>) {
  const ids = new Map<string, string>(actorNames);
  const attrs = new Map<string, string>();
  const guards = new Map<string, string>();
  if (process) {
    for (const [id, info] of stateIndex(process)) ids.set(id, info.path);
    for (const e of process.events ?? []) {
      ids.set(e.id, e.name);
      for (const a of e.payload ?? []) attrs.set(a.id, a.name);
    }
    for (const a of process.context ?? []) attrs.set(a.id, a.name);
    for (const g of process.guards ?? []) guards.set(g.id, g.name);
    for (const { state } of stateIndex(process).values()) for (const i of state.invoke ?? []) ids.set(i.id, i.name);
    for (const t of process.transitions ?? []) for (const m of t.gate?.meanings ?? []) ids.set(m.id, m.name);
  }
  return { ids, attrs, guards };
}

export function stepRows(scenario: ScenarioDoc, process: ProcessDoc | undefined, actorNames: Map<string, string>): StepRow[] {
  const { ids, attrs, guards } = stepNames(process, actorNames);
  const name = (id: string | undefined) => (id ? (ids.get(id) ?? "?") : "");
  return scenario.steps.map((s, i) => {
    const input = s.input ?? "event";
    return {
      id: s.id,
      index: i,
      pointer: `/steps/${i}`,
      input,
      on: input === "event" ? name(s.event) : input === "time" ? (s.after ?? "") : name(s.invoke),
      actor: name(s.actor),
      signer: s.signer ?? "",
      meaning: name(s.meaning),
      reason: s.reason ?? "",
      payload: mapText(s.payload, attrs),
      assume: mapText(s.assume as Json | undefined, guards),
      states: (s.expect?.states ?? []).map(name).join(", "),
      context: mapText(s.expect?.context, attrs),
      accepted: s.expect?.accepted !== false,
    };
  });
}

// ------------------------------------------------------------------ diagnostics

/** The diagnostics of one element at a pointer or under it. */
export function diagnosticsUnder(diagnostics: readonly Diagnostic[], element: string, pointer: string): Diagnostic[] {
  return diagnostics.filter(
    (d) => (d.elementId === element || d.elementId == null) && (d.jsonPointer === pointer || (d.jsonPointer ?? "").startsWith(`${pointer}/`)),
  );
}

/** The actor ids a process refers to: event actors, invoke actors, gate signers and required actors. */
export function actorUses(process: ProcessDoc, actor: string): string[] {
  const out: string[] = [];
  for (const e of process.events ?? []) if (e.actors?.includes(actor)) out.push(`${process.name}: event ${e.name}`);
  for (const { state, path } of stateIndex(process).values())
    for (const i of state.invoke ?? []) if (i.actors?.includes(actor)) out.push(`${process.name}: ${path} invokes ${i.name}`);
  for (const t of process.transitions ?? []) {
    const g = t.gate;
    if (!g) continue;
    if (g.signers?.includes(actor) || g.requiredActors?.includes(actor))
      out.push(`${process.name}: gate ${g.displayName || g.name}${g.requiredActors?.includes(actor) ? " (required)" : ""}`);
  }
  return out;
}

/** The lines of a Sync enum plan (sync-enum dry run), for its confirm. */
export function syncPlanLines(plan: {
  added: string[];
  removed: string[];
  reordered: boolean;
  refused: { member: string; referencedBy: string[] }[];
}): string[] {
  const lines: string[] = [];
  if (plan.added.length) lines.push(`Adds ${plan.added.join(", ")}`);
  if (plan.removed.length) lines.push(`Removes ${plan.removed.join(", ")}`);
  if (plan.reordered) lines.push("Reorders the members as the states");
  for (const r of plan.refused) lines.push(`Keeps ${r.member}: still used by ${r.referencedBy.length} ${r.referencedBy.length === 1 ? "element" : "elements"}`);
  if (!lines.length) lines.push("The enum already matches the states.");
  return lines;
}
