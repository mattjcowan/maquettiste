// The mock's reading of trigger points for a run (trigger-points-design.md decisions 10, 11, 18, 19 and 26): which points a verb
// reaches in which places, in what order, and what an `assume` word says about a check the team writes. The stepper runs them; the
// sequence handler lists them. The engine's TriggerSequence is the reference; the mock answers a simpler question there (this
// lifecycle's points only), and the editor never works out a sequence itself.

type Json = Record<string, unknown>;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const strs = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);

/** Whether any lifecycle lists trigger points: refusals then name their element, as they do with rights. */
export const usesTriggers = (lifecycles: readonly Json[]): boolean => lifecycles.some((l) => l.use === "lifecycle" && arr(l.triggerPoints).length > 0);

export const pointsOf = (lifecycle: Json): Json[] => (lifecycle.use === "lifecycle" ? arr(lifecycle.triggerPoints) : []);

/** A check with no condition, an action or a replace with no sets: the team writes the body. */
export const isTeamCode = (t: Json): boolean => (t.type === "check" ? t.condition === undefined : arr(t.sets).length === 0);

/** The moment a point runs at: the row of decision 18 it belongs to. */
export type Moment = "before" | "checks" | "effect" | "with-change" | "after-commit";

export function momentOf(t: Json): Moment {
  if (t.type === "check") return "checks";
  if (t.type === "replace") return "effect";
  return t.timing === "with-change" ? "with-change" : t.timing === "after-commit" ? "after-commit" : "before";
}

/** Where a verb happens: what the narrowing keys are tested against. */
export interface Place {
  verb: string;
  /** The acting lifecycle's id (the running one); only its points can use `targets` and `transitions`. */
  acting: string;
  transition?: string;
  /** The states a move enters, with their ancestors and default states; absent outside a move. */
  targets?: ReadonlySet<string>;
  /** The states the object is in, in a lifecycle (for a move in the acting lifecycle, its source). Asked only by a point with `states`. */
  statesOf(lifecycle: Json): ReadonlySet<string> | null;
  /** One chain (the entity and its bases) for the object, or one for each subject entity of a run with no object. */
  entities: readonly (readonly string[])[];
  /** The attributes the step reads or changes, or null when it does not say. */
  attributes: readonly string[] | null;
  end: string | null;
}

export interface Match {
  ok: boolean;
  /** An `attributes` or `ends` key was met without the step saying which: a check runs, an action does not (decision 10). */
  assumed: boolean;
}

/** Whether a point runs for the place: its verbs, then each narrowing key that is set. */
export function matchPoint(t: Json, lifecycle: Json, place: Place): Match {
  const no: Match = { ok: false, assumed: false };
  if (!strs(t.verbs).includes(place.verb)) return no;
  const own = lifecycle.id === place.acting;
  const transitions = strs(t.transitions);
  const targets = strs(t.targets);
  if (transitions.length && !(own && place.transition !== undefined && transitions.includes(place.transition))) return no;
  if (targets.length && !(own && place.targets && targets.some((x) => place.targets!.has(x)))) return no;
  const states = strs(t.states);
  if (states.length) {
    const active = place.statesOf(lifecycle);
    if (active && !states.some((s) => active.has(s))) return no;
  }
  const entities = strs(t.entities);
  if (entities.length && !place.entities.every((chain) => chain.some((e) => entities.includes(e)))) return no;
  let assumed = false;
  for (const [keys, given] of [
    [strs(t.attributes), place.attributes],
    [strs(t.ends), place.end === null ? null : [place.end]],
  ] as const) {
    if (!keys.length) continue;
    if (given === null) {
      // Not said: a check runs (fail closed), an action or a replace does not.
      if (t.type !== "check") return no;
      assumed = true;
    } else if (!keys.some((k) => given.includes(k))) return no;
  }
  return { ok: true, assumed };
}

/** The words an `assume` entry for a check can use. */
export type CheckWord = "pass" | "warning" | "error";
export const CHECK_WORDS: readonly string[] = ["pass", "warning", "error"];

/** A team-code check's assumed outcome: an entry keyed by its id, its name, or `Lifecycle.Name`; without one, none (the check passes). */
export function assumedWord(assume: Record<string, unknown>, t: Json, lifecycle: Json): CheckWord | null {
  for (const key of [String(t.id), `${String(lifecycle.id)}.${String(t.name)}`, `${String(lifecycle.name)}.${String(t.name)}`, String(t.name)]) {
    const v = assume[key];
    if (typeof v === "string" && CHECK_WORDS.includes(v)) return v as CheckWord;
  }
  return null;
}

/** The ids of a state and its ancestors in a state tree, innermost first; empty when the id is none of them. */
export function statePath(list: unknown, id: string): string[] {
  for (const s of arr(list)) {
    if (s.id === id) return [id];
    const inner = statePath(s.states, id);
    if (inner.length) return [...inner, String(s.id)];
  }
  return [];
}

// ------------------------------------------------------------------ get_trigger_sequence (the mock's simpler answer)

export interface SequenceStep {
  order: number;
  step: string;
  triggerPoint?: string;
  lifecycle?: string;
  type?: string;
  timing?: string;
  severity?: string;
  states?: string[];
  targets?: string[];
  transitions?: string[];
  entities?: string[];
  attributes?: string[];
  ends?: string[];
  when?: true;
  outside?: true;
}

export interface SequenceQuery {
  verb: string;
  state?: string;
  entity?: string;
  attributes?: string[];
  end?: string;
  transition?: string;
}

const ORDER: Record<Moment, number> = { before: 8, checks: 9, effect: 10, "with-change": 11, "after-commit": 12 };
const MOVE_VERBS = new Set(["promote", "demote", "override", "set-state"]);

/**
 * What runs, in order, when a verb happens in this lifecycle. Simpler than the engine's: this lifecycle's trigger points only, never
 * another lifecycle's, and a built-in step only where the lifecycle has the thing behind it. `chainOf` gives an entity and its bases.
 */
export function sequenceOf(lifecycle: Json, query: SequenceQuery, chainOf: (entity: string) => readonly string[]): SequenceStep[] {
  const steps: SequenceStep[] = [];
  const builtIn = (order: number, step: string) => steps.push({ order, step });
  const rights = lifecycle.rightsDefault !== undefined || arr(lifecycle.rights).length > 0;
  if (["modify", "connect", "disconnect", "delete"].includes(query.verb)) builtIn(1, "frozen");
  if (rights) {
    builtIn(3, "denies");
    builtIn(4, "defaults");
  }
  if (MOVE_VERBS.has(query.verb)) {
    builtIn(5, "no-move");
    builtIn(6, "guards");
  }
  if (query.verb === "sign") builtIn(7, "signatures");
  const place: Place = {
    verb: query.verb,
    acting: String(lifecycle.id),
    ...(query.transition ? { transition: query.transition } : {}),
    statesOf: () => (query.state ? new Set(statePath(lifecycle.states, query.state)) : null),
    entities: query.entity ? [chainOf(query.entity)] : [],
    attributes: query.attributes ?? null,
    end: query.end ?? null,
  };
  const points = pointsOf(lifecycle).flatMap((t) => (matchPoint(t, lifecycle, place).ok ? [{ t, moment: momentOf(t) }] : []));
  const listed = (moment: Moment) =>
    points
      .filter((p) => p.moment === moment)
      .map(({ t }): SequenceStep => {
        const step: SequenceStep = { order: ORDER[moment], step: moment, triggerPoint: String(t.id), lifecycle: String(lifecycle.id), type: String(t.type) };
        if (t.type === "action") step.timing = String(t.timing);
        if (t.type === "check") step.severity = t.severity === "warning" ? "warning" : "error";
        for (const key of ["states", "targets", "transitions", "entities", "attributes", "ends"] as const) if (strs(t[key]).length) step[key] = strs(t[key]);
        if (t.when !== undefined) step.when = true;
        if (t.outside === true) step.outside = true;
        return step;
      });
  steps.push(...listed("before"), ...listed("checks"));
  const replaces = listed("effect");
  if (replaces.length) steps.push(...replaces);
  else builtIn(10, "effect");
  steps.push(...listed("with-change"));
  if (!points.some((p) => p.moment === "with-change")) builtIn(11, "with-change");
  steps.push(...listed("after-commit"));
  return steps.sort((a, b) => a.order - b.order);
}
