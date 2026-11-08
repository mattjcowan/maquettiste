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
export const STEP_INPUTS = ["event", "time", "invoke-done", "invoke-error", "verb"] as const;

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
  /** The state's stored value: a string, or an integer written as a JSON number (lifecycle-design.md decision 1). */
  code?: string | number;
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
  /** The verb of an automatic move: only on a transition without an event (verbs-design.md decision 12). */
  verb?: string;
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
  /** The verb the event names (verbs-design.md decision 11): a built-in verb's key or an own verb's. */
  verb?: string;
  payload?: AttributeDoc[];
  actors?: string[];
  description?: unknown;
}

/** One value an action sets on the object (object-effects-design.md decision 1): an attribute id and a value in the expression language. */
export interface SetDoc {
  id: string;
  /** The id of an attribute every subject of the process holds. */
  attribute: string;
  /** A tree as stored, or `{ text }` as written (the server stores text as a tree). */
  value: Json;
}

export interface ExpressionDoc {
  id: string;
  name: string;
  displayName?: string;
  expression?: string;
  /** What an action sets on the object; actions only. */
  sets?: SetDoc[];
  raises?: string[];
  description?: unknown;
}

/** One thing a process governs (lifecycle-design.md decision 5). */
export interface SubjectDoc {
  id: string;
  /** An entity's id; absent on a stereotype entry. */
  entity?: string;
  /** A stereotype's key, in place of `entity` (stereotype-sets-design.md decision 2). */
  stereotype?: string;
  boundAttribute?: string;
  /** Where the object's owner is held (actors-design.md decision 10); the `subjects` form only. */
  owner?: string;
}

/**
 * One entry of a lifecycle's `rights` (rights-design.md decisions 1, 3 and 7): an inline right, which says what it allows or
 * denies itself, or a use of a named right (`right`), which carries only the states where it holds.
 */
export interface RightDoc {
  id: string;
  /** A use: the id of the named right. */
  right?: string;
  name?: string;
  displayName?: string;
  access?: "allow" | "deny";
  /** Who it applies to; absent means everyone. */
  actors?: string[];
  /** Who it does not apply to; it never grants. */
  except?: string[];
  verbs?: string[];
  /** Relation ends of the subject: the right then speaks of the links through them. */
  ends?: string[];
  /** Attributes of the subject: the right then speaks of reading or modifying only those (read and modify only). */
  attributes?: string[];
  /** The right holds only while the object (or, with ends, the link) is frozen. */
  whileFrozen?: boolean;
  /** A tree as stored, or `{ text }` as written. */
  condition?: Json;
  /** The states where it holds; absent means every state. */
  states?: string[];
  description?: unknown;
}

/**
 * One entry of a lifecycle's `triggerPoints` (trigger-points-design.md decisions 1 to 12): what runs when one of its verbs
 * happens, in the places its optional keys name. A check has a `condition`, an action or a replace has `sets`; with neither, the
 * body is the team's code.
 */
export interface TriggerPointDoc {
  id: string;
  name: string;
  displayName?: string;
  type: "check" | "action" | "replace";
  /** An action's moment; allowed nowhere else (MQ8101). */
  timing?: "before" | "with-change" | "after-commit";
  /** A check's severity; absent is error. */
  severity?: "error" | "warning";
  verbs: string[];
  states?: string[];
  targets?: string[];
  transitions?: string[];
  entities?: string[];
  attributes?: string[];
  ends?: string[];
  /** A tree as stored, or `{ text }` as written. */
  when?: Json;
  condition?: Json;
  sets?: SetDoc[];
  outside?: boolean;
  description?: unknown;
}

export interface ProcessDoc {
  kind: "process";
  id: string;
  name: string;
  package?: string;
  use?: "lifecycle" | "orchestration";
  subjects?: SubjectDoc[];
  /** The deprecated one-subject form (MQ9209); never written together with `subjects`. */
  subject?: string;
  boundAttribute?: string;
  context?: AttributeDoc[];
  events?: EventDoc[];
  guards?: ExpressionDoc[];
  actions?: ExpressionDoc[];
  states: StateDoc[];
  initial?: string;
  transitions?: TransitionDoc[];
  /** What the lifecycle answers where no right says (rights-design.md decision 9); absent allows. Lifecycles only. */
  rightsDefault?: "allow" | "deny";
  rights?: RightDoc[];
  /** What runs when a verb happens, in run order (trigger-points-design.md decision 19). Lifecycles only. */
  triggerPoints?: TriggerPointDoc[];
  source?: Json;
  generation?: Json;
}

/** What a step assumes a check the team writes does (trigger-points-design.md decision 26). */
export type CheckWord = "pass" | "warning" | "error";

export interface StepDoc {
  id: string;
  input?: (typeof STEP_INPUTS)[number];
  event?: string;
  invoke?: string;
  after?: string;
  /** The id of a scenario participant that acts (actors-design.md decision 15). */
  participant?: string;
  actor?: string;
  signer?: string;
  meaning?: string;
  reason?: string;
  payload?: Json;
  /** A guard with no expression by `true` or `false`; a check the team writes by `pass`, `warning` or `error` (trigger-points-design.md decision 26). */
  assume?: Record<string, boolean | CheckWord>;
  /** A verb step (rights-design.md decision 25): the verb, for `modify` the values by attribute id, and the link it acts on. */
  verb?: string;
  values?: Json;
  /** For a `read` verb step: the attributes it reads (ids); absent, the object as a whole. */
  attributes?: string[];
  end?: string;
  object?: string;
  expect?: {
    accepted?: boolean;
    states?: string[];
    context?: Json;
    values?: Json;
    refusal?: string;
    refusedBy?: string;
    /** The checks that warned, and the trigger points that ran in order (decision 28). */
    checkWarnings?: string[];
    triggerPoints?: string[];
  };
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
  /** The objects the run is about (lifecycle-design.md decision 22); `subject` names the one the process runs for. */
  objects?: ScenarioObjectDoc[];
  subject?: string;
  /** Who acts in the scenario (actors-design.md decision 14). */
  participants?: ParticipantDoc[];
}

export interface HoldingDoc {
  id: string;
  /** A role, group or organization. */
  actor: string;
  /** A scenario object id: the holding counts only within it. */
  within?: string;
}

export interface ParticipantDoc {
  id: string;
  name: string;
  displayName?: string;
  identity?: string | number;
  /** A person, external system or system actor. */
  actor?: string;
  holds?: HoldingDoc[];
  /** A scenario object id: their record. */
  object?: string;
}

export interface ScenarioObjectDoc {
  id: string;
  name: string;
  entity: string;
  values?: Record<string, unknown>;
  /** The states the object is in, in lifecycles other than the one the scenario runs (object-effects-design.md decision 10). */
  states?: string[];
  links?: { end: string; object: string; frozen?: boolean }[];
  /** The object starts frozen (rights-design.md decision 16); only the subject's is read. */
  frozen?: boolean;
  /** A participant id; only when the subject entry names no owner field (decision 16). */
  owner?: string;
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
  walk(process.states ?? [], null, 0, "", "");
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
  /** The state's code as the grid shows it ("" when it has none). */
  code: string;
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
      code: s.code === undefined ? "" : String(s.code),
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

/**
 * Takes deleted states out of the rights that name them. When the last one named goes, the right goes too: an empty list would
 * widen it to every state (rights-design.md section 8).
 */
function dropRightStates(process: ProcessDoc, gone: ReadonlySet<string>): void {
  if (!process.rights) return;
  process.rights = process.rights.filter((r) => {
    if (!r.states?.some((s) => gone.has(s))) return true;
    r.states = r.states.filter((s) => !gone.has(s));
    return r.states.length > 0;
  });
  if (!process.rights.length) delete process.rights;
}

/**
 * Takes deleted states, or transitions, out of the trigger points that name them (`states` and `targets` for states,
 * `transitions` for transitions). When the last one named goes, the trigger point goes too: an empty list would widen it to
 * everywhere (trigger-points-design.md section 9).
 */
export function dropTriggerPointRefs(process: ProcessDoc, gone: { states?: ReadonlySet<string>; transitions?: ReadonlySet<string> }): void {
  if (!process.triggerPoints) return;
  const lists = [
    ["states", gone.states],
    ["targets", gone.states],
    ["transitions", gone.transitions],
  ] as const;
  process.triggerPoints = process.triggerPoints.filter((t) => {
    let keep = true;
    for (const [key, ids] of lists) {
      const list = t[key];
      if (!ids || !list?.some((x) => ids.has(x))) continue;
      t[key] = list.filter((x) => !ids.has(x));
      if (!t[key]!.length) keep = false;
    }
    return keep;
  });
  if (!process.triggerPoints.length) delete process.triggerPoints;
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
  const leaving = new Set((process.transitions ?? []).filter((t) => inside.has(t.source)).map((t) => t.id));
  if (process.transitions) process.transitions = process.transitions.filter((t) => !inside.has(t.source));
  for (const { state } of stateIndex(process).values()) if (state.defaultTarget && inside.has(state.defaultTarget)) delete state.defaultTarget;
  dropRightStates(process, inside);
  dropTriggerPointRefs(process, { states: inside, transitions: leaving });
  return { ok: true };
}

/**
 * What a bound attribute holds, so a code is written in its form: "string" or "integer" for a plain status or a
 * reference type, or the integer `values` of an enum's members (a code of an enum binding is a number only where a
 * member's value says so, and the codes the lifecycle already has decide first).
 */
export type CodeKind = "string" | "integer" | { enumValues: readonly number[] };

/**
 * Writes the code a cell holds (lifecycle-design.md decision 1): an empty cell removes the key. With a binding the code
 * follows the binding's type (`kind`, see CodeKind). Without one, integer text is a JSON number, unless another state's
 * code is a string (the codes of a process are all strings or all integers, MQ9206), and anything else a string.
 */
export function setStateCode(process: Pick<ProcessDoc, "states">, id: string, text: string, kind?: CodeKind): void {
  const state = stateIndex(process).get(id)?.state;
  if (!state) return;
  const value = text.trim();
  if (!value) {
    delete state.code;
    return;
  }
  const integer = /^-?(0|[1-9]\d*)$/.test(value) && Number.isSafeInteger(Number(value));
  const otherStrings = [...stateIndex(process).values()].some((i) => i.state.id !== id && typeof i.state.code === "string");
  if (kind === "string") state.code = value;
  else if (kind === "integer") state.code = integer ? Number(value) : value;
  else if (kind) {
    const others = [...stateIndex(process).values()].filter((i) => i.state.id !== id && i.state.code !== undefined);
    if (others.length) state.code = integer && !otherStrings ? Number(value) : value;
    else state.code = integer && kind.enumValues.includes(Number(value)) ? Number(value) : value;
  } else state.code = integer && !otherStrings ? Number(value) : value;
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

/**
 * The ids of a list of actions after its cell was edited: the names typed, with each action the cell cannot name (a library's) kept at its place,
 * since the cell has no way to say it should stay.
 */
export function actionIdsOfNames(raw: string, own: { id: string; name: string }[], current: string[] | undefined): string[] {
  const out = idsOfNames(raw, own);
  const owned = new Set(own.map((o) => o.id));
  (current ?? []).forEach((id, i) => {
    if (!owned.has(id) && !out.includes(id)) out.splice(Math.min(i, out.length), 0, id);
  });
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

export type ScenarioStatus = { passed: true; warnings?: string[] } | { passed: false; step: number };

export interface ScenarioRow {
  id: string;
  name: string;
  steps: number;
  status: "passed" | "failed" | "not-run";
  /** The failing step (1-based) when failed. */
  failedAt: number | null;
  /** How many warnings the last replay gave a passing scenario. */
  warnings: number;
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
      warnings: st?.passed ? (st.warnings?.length ?? 0) : 0,
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
  participant: string;
  actor: string;
  signer: string;
  meaning: string;
  reason: string;
  payload: string;
  assume: string;
  states: string;
  context: string;
  /** The expected values of the object, by attribute name (read-only: filled by Update from replay). */
  values: string;
  accepted: boolean;
  /** A verb step: the verb's key, the values it sets, the link end and the linked object (names). */
  verb: string;
  setValues: string;
  /** A read step's attributes (names). */
  attributes: string;
  end: string;
  object: string;
  /** What the step expects of a refusal: the reason's key and the element's name. */
  refusal: string;
  refusedBy: string;
  /** The checks the step expects to warn, and the trigger points it expects to run, in order (names). */
  checkWarnings: string;
  triggerPoints: string;
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
  // Guards and the trigger points of the lifecycle: both are named in a step's assume (decision 26).
  if (process) {
    for (const t of process.triggerPoints ?? []) ids.set(t.id, t.name);
    for (const [id, info] of stateIndex(process)) ids.set(id, info.path);
    for (const e of process.events ?? []) {
      ids.set(e.id, e.name);
      for (const a of e.payload ?? []) attrs.set(a.id, a.name);
    }
    for (const a of process.context ?? []) attrs.set(a.id, a.name);
    for (const g of process.guards ?? []) guards.set(g.id, g.name);
    for (const t of process.triggerPoints ?? []) guards.set(t.id, t.name);
    for (const { state } of stateIndex(process).values()) for (const i of state.invoke ?? []) ids.set(i.id, i.name);
    for (const t of process.transitions ?? []) for (const m of t.gate?.meanings ?? []) ids.set(m.id, m.name);
  }
  return { ids, attrs, guards };
}

export function stepRows(
  scenario: ScenarioDoc,
  process: ProcessDoc | undefined,
  actorNames: Map<string, string>,
  valueNames: Map<string, string> = new Map(),
  /** Names of what else a step names or a refusal points at: ends, rights, lifecycles, gates. */
  more: ReadonlyMap<string, string> = new Map(),
): StepRow[] {
  const { ids, attrs, guards } = stepNames(process, actorNames);
  for (const p of scenario.participants ?? []) ids.set(p.id, p.name);
  for (const o of scenario.objects ?? []) ids.set(o.id, o.name);
  for (const [id, name] of more) ids.set(id, name);
  for (const g of process?.guards ?? []) ids.set(g.id, g.name);
  for (const t of process?.transitions ?? []) if (t.gate) ids.set(t.gate.id, t.gate.name);
  if (process) ids.set(process.id, process.name);
  const name = (id: string | undefined) => (id ? (ids.get(id) ?? "?") : "");
  return scenario.steps.map((s, i) => {
    const input = s.input ?? "event";
    return {
      id: s.id,
      index: i,
      pointer: `/steps/${i}`,
      input,
      on: input === "event" ? name(s.event) : input === "time" ? (s.after ?? "") : name(s.invoke),
      participant: name(s.participant),
      actor: name(s.actor),
      signer: s.signer ?? "",
      meaning: name(s.meaning),
      reason: s.reason ?? "",
      payload: mapText(s.payload, attrs),
      assume: mapText(s.assume as Json | undefined, guards),
      states: (s.expect?.states ?? []).map(name).join(", "),
      context: mapText(s.expect?.context, attrs),
      values: mapText(s.expect?.values, valueNames),
      accepted: s.expect?.accepted !== false,
      verb: s.verb ?? "",
      setValues: mapText(s.values, valueNames),
      attributes: (s.attributes ?? []).map((a) => valueNames.get(a) ?? a).join(", "),
      end: name(s.end),
      object: name(s.object),
      refusal: s.expect?.refusal ?? "",
      refusedBy: s.expect?.refusedBy ? name(s.expect.refusedBy) : "",
      checkWarnings: (s.expect?.checkWarnings ?? []).map(name).join(", "),
      triggerPoints: (s.expect?.triggerPoints ?? []).map(name).join(", "),
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

/**
 * Every reader of what a process governs goes through here, over both forms (lifecycle-design.md decision 5). In the
 * one-subject form the one entry's id is the process's own id, as for findings.
 */
export function subjectsOf(doc: { id?: unknown; subjects?: unknown; subject?: unknown; boundAttribute?: unknown } | null | undefined): SubjectDoc[] {
  if (!doc) return [];
  if (Array.isArray(doc.subjects)) {
    return (doc.subjects as Record<string, unknown>[]).flatMap((e) =>
      e && (typeof e.entity === "string" || typeof e.stereotype === "string")
        ? [
            {
              id: typeof e.id === "string" ? e.id : "",
              ...(typeof e.entity === "string" ? { entity: e.entity } : { stereotype: e.stereotype as string }),
              ...(typeof e.boundAttribute === "string" ? { boundAttribute: e.boundAttribute } : {}),
              ...(typeof e.owner === "string" ? { owner: e.owner } : {}),
            },
          ]
        : [],
    );
  }
  if (typeof doc.subject !== "string" || doc.subject === "") return [];
  return [
    {
      id: typeof doc.id === "string" ? doc.id : "",
      entity: doc.subject,
      ...(typeof doc.boundAttribute === "string" ? { boundAttribute: doc.boundAttribute } : {}),
    },
  ];
}

/** Whether a stereotype applies to entities: its `appliesTo` is unset, empty or lists entity (MQ9211 refuses one that does not). */
export function stereotypeAppliesToEntities(stereotype: { appliesTo?: unknown } | null | undefined): boolean {
  const list = stereotype?.appliesTo;
  return !Array.isArray(list) || list.length === 0 || list.includes("entity");
}

/** The one subject of a process with exactly one, in either form; undefined with none or with two or more. */
export function soleSubject(doc: Parameters<typeof subjectsOf>[0]): SubjectDoc | undefined {
  const all = subjectsOf(doc);
  return all.length === 1 ? all[0] : undefined;
}
