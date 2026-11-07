// The mock's rights check (rights-design.md decisions 9 to 14 and 31), one evaluator for the stepper and `get_rights`.
// Each lifecycle answers on its own: its denies, then its allows (an event's, task's or gate's closed list that the caller
// satisfies counts as one), then its default. Then any "no" wins. A lifecycle that allows by default and says nothing about the
// verb answers yes. `except` never grants. A condition the mock cannot read says nothing in a run and "depends" in the grid.
// Part 2: freezing refuses first and no allow lifts it (decisions 17 and 18); a right with `whileFrozen` holds only while frozen; and
// `read` and `modify` are answered per attribute (decision 21), where attributes not known fail closed.
import { builtInVerbs } from "@/model/verbs";
import type { Membership } from "./membership";

type Json = Record<string, unknown>;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const strs = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
const str = (v: unknown): string | null => (typeof v === "string" && v !== "" ? v : null);

/** True, false, or null: it cannot be told here. */
export type Tri = boolean | null;
const or = (items: Tri[]): Tri => (items.some((x) => x === true) ? true : items.some((x) => x === null) ? null : false);
const and = (items: Tri[]): Tri => (items.some((x) => x === false) ? false : items.some((x) => x === null) ? null : true);
const not = (x: Tri): Tri => (x === null ? null : !x);

const GROUPS = new Map(builtInVerbs.map((v) => [v.key, v.group]));

/** Moves, signatures and tasks are answered by the lifecycle they happen in; objects, links and own verbs by every lifecycle that governs the object (decision 10). */
export function ownLifecycleOnly(verb: string): boolean {
  const g = GROUPS.get(verb);
  return g === "move" || g === "signature" || g === "task";
}

/** The verbs freezing refuses before any right (decision 17). */
export const FROZEN_VERBS: ReadonlySet<string> = new Set(["modify", "connect", "disconnect", "delete"]);

/** The verbs a right can narrow to attributes (MQ9903). */
export const ATTRIBUTE_VERBS: ReadonlySet<string> = new Set(["read", "modify"]);

export const usesRights = (lifecycles: readonly Json[]): boolean =>
  lifecycles.some((l) => l.use === "lifecycle" && (l.rightsDefault !== undefined || arr(l.rights).length > 0));

export interface Holding {
  /** The right's id: an inline right's own, or the named right's. */
  right: string;
  lifecycle: string;
  access: "allow" | "deny";
}

export interface Facts {
  named: ReadonlyMap<string, Json>;
  /** Whether the caller satisfies the listed actor. */
  satisfies(listed: string): Tri;
  verb: string | null;
  /** The relation end a link verb goes through. */
  end?: string | null;
  /** The states active in the lifecycle, with their ancestors; null when unknown (every state may be). */
  active: ReadonlySet<string> | null;
  /** A right's condition: true, false or unknown (null). */
  condition(tree: unknown): Tri;
  /** The elements whose closed list lets the caller through: an allow of this lifecycle. */
  closed?: readonly string[];
  /**
   * For `read` and `modify`: the attributes the input names, each checked on its own. Missing, null or empty means they are not
   * known (an event naming the verb, or a step without attributes): the check fails closed.
   */
  attributes?: readonly string[] | null;
  /** Whether the object is frozen, and whether the link through an end (null: not known) is. A right with `whileFrozen` holds only then. */
  frozenObject?: boolean;
  frozenLink?(end: string | null): boolean;
  /** Internal: the one attribute being checked (null: not known); undefined for a verb that has none. */
  attr?: string | null;
}

const entryId = (e: Json): string => str(e.right) ?? String(e.id);
const sourceOf = (e: Json, f: Facts): Json | undefined => (str(e.right) ? f.named.get(String(e.right)) : e);

/** Whether a right holds: its verb, link, state, who (without `except`) and condition. */
function applies(entry: Json, f: Facts): Tri {
  const src = sourceOf(entry, f);
  if (!src || !f.verb || !strs(src.verbs).includes(f.verb)) return false;
  const ends = strs(src.ends);
  const link = GROUPS.get(f.verb) === "link";
  if (ends.length) {
    // A link verb with no end known: a deny with `ends` still holds; an allow with `ends` does not grant.
    if (f.end ? !ends.includes(f.end) : !(link && (str(src.access) ?? "deny") === "deny")) return false;
  } else if (f.end && !link) return false; // a right with no `ends` speaks of the object only, and of every link for a link verb
  if (src.whileFrozen === true && !(ends.length ? (f.frozenLink?.(f.end ?? null) ?? false) : f.frozenObject === true)) return false;
  const attrs = strs(src.attributes);
  if (attrs.length && f.attr !== undefined) {
    // An attribute not known: a deny naming attributes holds, and an allow naming attributes grants nothing.
    if (f.attr === null ? (str(src.access) ?? "deny") !== "deny" : !attrs.includes(f.attr)) return false;
  }
  const states = strs(entry.states);
  if (states.length && f.active && !states.some((s) => f.active!.has(s))) return false;
  const actors = strs(src.actors);
  const who: Tri = actors.length === 0 ? true : or(actors.map((a) => f.satisfies(a)));
  const except: Tri = or(strs(src.except).map((a) => f.satisfies(a)));
  const condition: Tri = src.condition === undefined || src.condition === null ? true : f.condition(src.condition);
  return and([who, not(except), condition]);
}

export interface Answer {
  verdict: "allow" | "deny" | "depends";
  byDefault: boolean;
  /** The ids that decide: the rights, the closed lists' elements, or the lifecycle for its default. */
  by: string[];
  /** The rights that hold (certainly), in file order. */
  holding: Holding[];
  /** Whether a condition or an actor from the object decides it. */
  uncertain: boolean;
}

function decide(lifecycle: Json, f: Facts, maybe: boolean, applied: { entry: Json; h: Tri }[]): Omit<Answer, "holding" | "uncertain"> {
  const holds = (h: Tri) => h === true || (h === null && maybe);
  const access = (e: Json) => str(sourceOf(e, f)?.access) ?? "deny";
  const denies = applied.filter((a) => access(a.entry) === "deny" && holds(a.h));
  if (denies.length) return { verdict: "deny", byDefault: false, by: denies.map((a) => entryId(a.entry)) };
  const allows = applied.filter((a) => access(a.entry) === "allow" && holds(a.h)).map((a) => entryId(a.entry));
  const by = [...allows, ...(f.closed ?? [])];
  if (by.length) return { verdict: "allow", byDefault: false, by };
  return { verdict: lifecycle.rightsDefault === "deny" ? "deny" : "allow", byDefault: true, by: [String(lifecycle.id)] };
}

/**
 * One lifecycle's answer on its own: its denies, then its allows, then its default. For `read` and `modify` each attribute is
 * answered on its own, by the rights that name it and those that name none; then any "no" wins.
 */
export function answerLifecycle(lifecycle: Json, f: Facts): Answer {
  if (!f.verb || !ATTRIBUTE_VERBS.has(f.verb)) return answerOne(lifecycle, { ...f, attr: undefined });
  const list: (string | null)[] = f.attributes && f.attributes.length ? [...f.attributes] : [null];
  const answers = list.map((attr) => answerOne(lifecycle, { ...f, attr }));
  if (answers.length === 1) return answers[0];
  const holding = [...new Map(answers.flatMap((a) => a.holding).map((h) => [`${h.lifecycle}|${h.right}|${h.access}`, h])).values()];
  const uncertain = answers.some((a) => a.uncertain);
  // Several attributes denied by rights: name the first deny in file order, whichever attribute it came from.
  const byRight = answers.filter((a) => a.verdict === "deny" && !a.byDefault);
  if (byRight.length) {
    const held = new Set(byRight.flatMap((a) => a.by));
    const first = arr(lifecycle.rights)
      .map(entryId)
      .find((id) => held.has(id));
    return { ...byRight[0], by: first ? [first, ...byRight[0].by.filter((b) => b !== first)] : byRight[0].by, holding, uncertain };
  }
  const pick = answers.find((a) => a.verdict === "deny") ?? answers.find((a) => a.verdict === "depends") ?? answers.find((a) => !a.byDefault) ?? answers[0];
  if (pick.verdict === "depends") return { ...pick, by: [...new Set(answers.filter((a) => a.verdict === "depends").flatMap((a) => a.by))], holding, uncertain };
  return { ...pick, holding, uncertain };
}

function answerOne(lifecycle: Json, f: Facts): Answer {
  const applied = arr(lifecycle.rights).map((entry) => ({ entry, h: applies(entry, f) }));
  const lo = decide(lifecycle, f, false, applied);
  const hi = decide(lifecycle, f, true, applied);
  const id = String(lifecycle.id);
  const access = (e: Json) => (str(sourceOf(e, f)?.access) === "allow" ? "allow" : "deny");
  const holding = applied.filter((a) => a.h === true).map((a): Holding => ({ right: entryId(a.entry), lifecycle: id, access: access(a.entry) }));
  const uncertain = applied.some((a) => a.h === null);
  if (lo.verdict === hi.verdict) return { ...lo, holding, uncertain };
  return { verdict: "depends", byDefault: false, by: [...new Set([...lo.by, ...hi.by])], holding, uncertain: true };
}

/** A lifecycle takes part when it has rights or denies by default (decision 7, speed). */
export const takesPart = (l: Json): boolean => l.use === "lifecycle" && (arr(l.rights).length > 0 || l.rightsDefault === "deny");

export interface Refusal {
  refusal: "right" | "default" | "frozen";
  by: string;
}

/**
 * The checks of rows 3 and 4 over the answering lifecycles in check order: every lifecycle's holding deny first (the acting
 * lifecycle's first), then the first whose default denies. `facts` is asked per lifecycle, with its own states and closed list.
 */
export function checkAll(lifecycles: readonly Json[], facts: (l: Json) => Facts): { refused: Refusal | null; rights: Holding[] } {
  const rights: Holding[] = [];
  const answers: { l: Json; a: Answer }[] = [];
  for (const l of lifecycles) {
    if (!takesPart(l)) continue;
    const a = answerLifecycle(l, facts(l));
    answers.push({ l, a });
    rights.push(...a.holding);
  }
  const denied = answers.find(({ a }) => a.verdict === "deny" && !a.byDefault);
  if (denied) return { refused: { refusal: "right", by: denied.a.by[0] }, rights };
  const byDefault = answers.find(({ a }) => a.verdict === "deny" && a.byDefault);
  if (byDefault) return { refused: { refusal: "default", by: String(byDefault.l.id) }, rights };
  return { refused: null, rights };
}

// ------------------------------------------------------------------ who can do what

export interface RightsRowJson {
  state: string;
  actor: string | null;
  verb: string;
  answer: "allowed" | "denied" | "depends";
  byDefault: boolean;
  by: string[];
  dependsOn: string[];
}

export interface RightsWorld {
  /** Every lifecycle, with the one asked about in its saved form. */
  lifecycles: readonly Json[];
  named: ReadonlyMap<string, Json>;
  membership: Membership;
  /** The actors that stand for the object's owner: whether a caller is it depends on the object. */
  ownerActors: ReadonlySet<string>;
  /** The entities each lifecycle's subjects name. */
  entitiesOf(lifecycle: Json): string[];
}

/** The atomic states of a lifecycle in document order, each with the ids active in it (itself and its ancestors). */
export function atomicStates(doc: Json): { id: string; active: Set<string>; node: Json }[] {
  const out: { id: string; active: Set<string>; node: Json }[] = [];
  const walk = (list: unknown, above: string[]) =>
    arr(list).forEach((s) => {
      if (s.type === "history" || s.type === "choice") return;
      const id = String(s.id);
      if (arr(s.states).length) walk(s.states, [...above, id]);
      else out.push({ id, active: new Set([...above, id]), node: s });
    });
  walk(doc.states, []);
  return out;
}

/** The closed lists of the cell: events that name the verb and can be sent from the state, its human tasks, its gates' signers. */
function closedLists(doc: Json, active: ReadonlySet<string>, verb: string): { id: string; list: string[]; signers: boolean }[] {
  const out: { id: string; list: string[]; signers: boolean }[] = [];
  const events = arr(doc.events);
  const taken = arr(doc.transitions).filter((t) => active.has(String(t.source)) && str(t.event));
  for (const ev of events.filter((e) => e.verb === verb)) {
    if (taken.some((t) => t.event === ev.id)) out.push({ id: String(ev.id), list: strs(ev.actors), signers: false });
  }
  if (verb === "complete") {
    const walk = (list: unknown) =>
      arr(list).forEach((s) => {
        if (active.has(String(s.id)))
          for (const i of arr(s.invoke)) if (i.type === "human-task") out.push({ id: String(i.id), list: strs(i.actors), signers: false });
        walk(s.states);
      });
    walk(doc.states);
  }
  // A gate's signers count as an allow for `sign` and for the gated event's verb.
  for (const t of taken) {
    const gate = t.gate as Json | undefined;
    const signers = strs(gate?.signers);
    if (!gate || !signers.length) continue;
    const ev = events.find((e) => e.id === t.event);
    if (verb === "sign" || ev?.verb === verb) out.push({ id: String(gate.id), list: signers, signers: true });
  }
  return out;
}

/** Who can do what in a lifecycle: one row per state, actor and verb (the mock never splits a row by entity). */
export function rightsGrid(
  doc: Json,
  world: RightsWorld,
  filter: { states?: string[]; actors?: string[]; verbs?: string[]; frozen?: boolean } = {},
): RightsRowJson[] {
  const lifecycle = doc;
  const named = world.named;
  const rights = arr(lifecycle.rights).map((e) => sourceOf(e, { named } as Facts) ?? {});
  const actorSet = new Set<string>();
  const verbSet = new Set<string>();
  for (const r of rights) {
    strs(r.actors).forEach((a) => actorSet.add(a));
    strs(r.except).forEach((a) => actorSet.add(a));
    strs(r.verbs).forEach((v) => verbSet.add(v));
  }
  for (const ev of arr(lifecycle.events)) {
    strs(ev.actors).forEach((a) => actorSet.add(a));
    if (str(ev.verb)) verbSet.add(String(ev.verb));
  }
  const gates = arr(lifecycle.transitions).flatMap((t) => (t.gate ? [t.gate as Json] : []));
  for (const g of gates) strs(g.signers).forEach((a) => actorSet.add(a));
  const walk = (list: unknown) =>
    arr(list).forEach((s) => {
      for (const i of arr(s.invoke)) if (i.type === "human-task") strs(i.actors).forEach((a) => actorSet.add(a));
      walk(s.states);
    });
  walk(lifecycle.states);
  const actors: (string | null)[] = [...[...actorSet].sort((a, b) => (a < b ? -1 : 1)), null];
  const verbs = [...verbSet].sort((a, b) => (a < b ? -1 : 1));
  // Other lifecycles that govern the object: those sharing a subject entity.
  const mine = new Set(world.entitiesOf(lifecycle));
  const others = world.lifecycles.filter((l) => l.id !== lifecycle.id && takesPart(l) && world.entitiesOf(l).some((e) => mine.has(e)));
  const rows: RightsRowJson[] = [];
  for (const st of atomicStates(lifecycle)) {
    if (filter.states?.length && !filter.states.some((s) => st.active.has(s))) continue;
    for (const actor of actors) {
      if (filter.actors?.length && !filter.actors.includes(actor ?? "")) continue;
      const satisfies = (listed: string): Tri => {
        if (actor === null) return false;
        if (world.membership.accepts(listed, actor)) return true;
        return world.ownerActors.has(listed) ? null : false;
      };
      for (const verb of verbs) {
        if (filter.verbs?.length && !filter.verbs.includes(verb)) continue;
        rows.push(cell(lifecycle, st, actor, verb, satisfies, world, others, filter.frozen === true));
      }
    }
  }
  return rows;
}

function cell(
  lifecycle: Json,
  st: { id: string; active: Set<string> },
  actor: string | null,
  verb: string,
  satisfies: (listed: string) => Tri,
  world: RightsWorld,
  others: readonly Json[],
  frozen: boolean,
): RightsRowJson {
  const condition = (tree: unknown): Tri => (tree && typeof tree === "object" ? null : true);
  const base = { state: st.id, actor, verb };
  // Row 1: freezing refuses before any right, and no element decides it.
  if (frozen && FROZEN_VERBS.has(verb)) return { ...base, answer: "denied", byDefault: false, by: [], dependsOn: [] };
  const answerOf = (l: Json, f: Omit<Facts, "named" | "satisfies" | "verb" | "condition">) =>
    gridAnswer(l, { named: world.named, satisfies, verb, condition, frozenObject: frozen, frozenLink: () => frozen, ...f });
  const lists = closedLists(lifecycle, st.active, verb);
  const passes = lists.map((c) => (c.list.length === 0 && !c.signers ? true : or(c.list.map(satisfies))));
  const events = lists.filter((c) => !c.signers);
  // Events that name the verb: the caller must pass one of their lists; a verb no event names is answered by rights alone.
  if (events.length && or(events.map((c) => passes[lists.indexOf(c)])) === false)
    return { ...base, answer: "denied", byDefault: false, by: events.map((c) => c.id), dependsOn: [] };
  const closed = lists.filter((c) => c.list.length > 0);
  const allowing = closed.filter((c) => or(c.list.map(satisfies)) === true).map((c) => c.id);
  const maybeAllowing = closed.filter((c) => or(c.list.map(satisfies)) === null).map((c) => c.id);
  const own = answerOf(lifecycle, { active: st.active, closed: allowing });
  let ownVerdict: Answer["verdict"] = own.verdict;
  let by = own.by;
  if (maybeAllowing.length && own.verdict === "deny" && own.byDefault) {
    ownVerdict = "depends";
    by = [...own.by, ...maybeAllowing];
  }
  if (ownVerdict === "deny") return { ...base, answer: "denied", byDefault: own.byDefault, by, dependsOn: [] };
  // Other lifecycles answer for object, link and own verbs only; their state is not known in a cell.
  const dependsOn: string[] = [];
  let deniedBy: Answer | null = null;
  if (!ownLifecycleOnly(verb)) {
    for (const other of others) {
      const answers = atomicStates(other).map((s) => answerOf(other, { active: s.active, closed: [] }));
      if (!answers.length) continue;
      if (answers.every((a) => a.verdict === "deny")) {
        deniedBy ??= answers[0];
      } else if (answers.some((a) => a.verdict !== "allow")) dependsOn.push(String(other.id));
    }
  }
  if (deniedBy) return { ...base, answer: "denied", byDefault: deniedBy.byDefault, by: deniedBy.by, dependsOn: [] };
  if (ownVerdict === "depends" || dependsOn.length) return { ...base, answer: "depends", byDefault: false, by, dependsOn };
  return { ...base, answer: "allowed", byDefault: own.byDefault, by, dependsOn: [] };
}

/** The attributes any right of the lifecycle names. */
function attributesNamed(lifecycle: Json, named: ReadonlyMap<string, Json>): string[] {
  const out = new Set<string>();
  for (const e of arr(lifecycle.rights)) strs(sourceOf(e, { named } as Facts)?.attributes).forEach((a) => out.add(a));
  return [...out];
}

/**
 * A cell of "who can do what" for `read` and `modify`: when a right that names attributes would change the answer, the cell
 * depends, and names that right (decision 21).
 */
function gridAnswer(lifecycle: Json, f: Facts): Answer {
  if (!f.verb || !ATTRIBUTE_VERBS.has(f.verb)) return answerLifecycle(lifecycle, f);
  const names = attributesNamed(lifecycle, f.named);
  // An attribute that no right names: only the rights that name none apply.
  const base = answerLifecycle(lifecycle, { ...f, attributes: [""] });
  const changed = names.map((n) => answerLifecycle(lifecycle, { ...f, attributes: [n] })).filter((a) => a.verdict !== base.verdict);
  if (!changed.length) return base;
  return { verdict: "depends", byDefault: false, by: [...new Set(changed.flatMap((a) => a.by))], holding: base.holding, uncertain: true };
}
