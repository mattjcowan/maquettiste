// The editor's side of object effects (object-effects-design.md section 10), pure: the attributes an action may set, the
// edits of an action's `sets`, a value tree as text, and the states a scenario object may be in. The engine decides what
// is valid (MQ9801 to MQ9803, MQ3116); the picker only offers what it would accept.
import { newId } from "@/lib/ids";
import type { Json, ProcessDoc, SetDoc } from "./process";
import { stateIndex } from "./process";

type Attribute = { id: string; name: string; displayName?: string; derived?: unknown; collection?: boolean; type?: unknown };

export interface SettableAttribute {
  id: string;
  name: string;
}

const isValueObject = (a: Attribute, kindOf: (id: string) => string | undefined) => {
  const ref = typeof a.type === "object" && a.type ? (a.type as { ref?: string }).ref : undefined;
  return !!ref && kindOf(ref) === "value-object";
};

/**
 * The attributes an action may set: those every subject of the process holds under one id (the intersection of what each
 * subject holds), without derived attributes, collections, value objects and the attributes that hold the chart's state
 * (`bound`, the process's own bound attributes; another lifecycle's is the engine's MQ9802). `perSubject` lists each
 * subject's own, inherited and stereotype attributes.
 */
export function settableAttributes(
  perSubject: readonly (readonly Attribute[])[],
  bound: ReadonlySet<string> = new Set(),
  kindOf: (id: string) => string | undefined = () => undefined,
): SettableAttribute[] {
  if (!perSubject.length) return [];
  const [first, ...rest] = perSubject;
  return first
    .filter((a) => rest.every((list) => list.some((b) => b.id === a.id)))
    .filter((a) => !a.derived && a.collection !== true && !bound.has(a.id) && !isValueObject(a, kindOf))
    .map((a) => ({ id: a.id, name: a.displayName || a.name }));
}

// ------------------------------------------------------------------ the sets of an action

export function actionOf(process: ProcessDoc, action: string) {
  return (process.actions ?? []).find((a) => a.id === action);
}

/** Adds a set to an action: the first attribute it does not yet set (one action sets one attribute once, MQ9803), a value to write. Returns its id, or null when none is free. */
export function addSet(process: ProcessDoc, action: string, choices: readonly SettableAttribute[]): string | null {
  const a = actionOf(process, action);
  if (!a) return null;
  const taken = new Set((a.sets ?? []).map((s) => s.attribute));
  const free = choices.find((c) => !taken.has(c.id));
  if (!free) return null;
  const id = newId();
  // The value is null until written: a set must fit its attribute, and null fits any type.
  (a.sets ??= []).push({ id, attribute: free.id, value: { null: true } });
  return id;
}

export function removeSet(process: ProcessDoc, action: string, set: string): void {
  const a = actionOf(process, action);
  if (!a?.sets) return;
  a.sets = a.sets.filter((s) => s.id !== set);
  if (!a.sets.length) delete a.sets;
}

export function setSetAttribute(process: ProcessDoc, action: string, set: string, attribute: string): void {
  const s = actionOf(process, action)?.sets?.find((x) => x.id === set);
  if (s) s.attribute = attribute;
}

/** Writes a value as text; the server stores it as a tree (an empty text is the null literal). */
export function setSetValue(process: ProcessDoc, action: string, set: string, text: string): void {
  const s = actionOf(process, action)?.sets?.find((x) => x.id === set);
  if (s) s.value = text.trim() ? { text: text.trim() } : { null: true };
}

// ------------------------------------------------------------------ a value tree as text

export interface TreeNames {
  /** The name of an attribute, end, enum member or code by id. */
  name(id: string): string | undefined;
  /** The state's `Lifecycle.Path`. */
  state(id: string): string | undefined;
  /** Ids the subject holds (attributes and ends): a path starting with one reads `subject.…`. */
  subject(id: string): boolean;
}

const BINARY: Record<string, string> = { eq: "=", ne: "<>", lt: "<", le: "<=", gt: ">", ge: ">=", add: "+", sub: "-", mul: "*", div: "/", concat: "||" };

/** A value tree as the text form (the engine prints the exact one in the preview; this one serves the summary cells). */
export function treeText(tree: Json | undefined, names: TreeNames): string {
  if (!tree || typeof tree !== "object") return "";
  const t = tree as Record<string, unknown>;
  const arg = (n: unknown): string => treeText(n as Json, names);
  if (typeof t.text === "string") return t.text;
  if ("null" in t) return "null";
  if ("value" in t) return typeof t.value === "string" ? `'${t.value.replace(/'/g, "''")}'` : String(t.value);
  if (Array.isArray(t.path)) {
    const ids = t.path as string[];
    const text = ids.map((id) => names.name(id) ?? "?").join(".");
    return ids.length && names.subject(ids[0]) ? `subject.${text}` : text;
  }
  if (typeof t.ref === "string") return names.name(t.ref) ?? "?";
  if (t.now === true) return "now()";
  if (t.actor === true) return "actor()";
  if (Array.isArray(t.inState)) {
    const of = Array.isArray(t.of) ? (t.of as string[]).map((id) => names.name(id) ?? "?").join(".") : "";
    return `in_state(${["subject" + (of ? `.${of}` : ""), ...(t.inState as string[]).map((id) => names.state(id) ?? "?")].join(", ")})`;
  }
  const args = (Array.isArray(t.args) ? t.args : []) as unknown[];
  if (typeof t.op === "string") {
    if (BINARY[t.op] && args.length === 2) return `${arg(args[0])} ${BINARY[t.op]} ${arg(args[1])}`;
    if ((t.op === "and" || t.op === "or") && args.length) return args.map(arg).join(` ${t.op} `);
    if (t.op === "not" && args.length === 1) return `not ${arg(args[0])}`;
    if (t.op === "neg" && args.length === 1) return `-${arg(args[0])}`;
    return `${t.op}(${args.map(arg).join(", ")})`;
  }
  if (typeof t.call === "string") return `${t.call}(${args.map(arg).join(", ")})`;
  if (Array.isArray(t.case)) return "case ...";
  return "...";
}

/** An action's sets as a line: `submittedBy <- actor(), submittedOn <- now()`. */
export function setsText(sets: readonly SetDoc[] | undefined, attributeName: (id: string) => string, names: TreeNames): string {
  return (sets ?? []).map((s) => `${attributeName(s.attribute)} ← ${treeText(s.value, names) || "?"}`).join(", ");
}

// ------------------------------------------------------------------ the states of a scenario object

export interface StateChoiceGroup {
  lifecycle: string;
  /** The states a configuration of the lifecycle can name, by id, with the text form `Lifecycle.Path`. */
  states: { id: string; label: string }[];
}

/** The lifecycles' states a scenario object may be in: states that rest (not history or choice), grouped by lifecycle. `run` is the lifecycle the scenario runs, which is left out. */
export function stateChoices(lifecycles: readonly ProcessDoc[], run?: string): StateChoiceGroup[] {
  return lifecycles
    .filter((p) => p.id !== run)
    .map((p) => ({
      lifecycle: p.name,
      states: [...stateIndex(p).values()]
        .filter((i) => i.state.type !== "history" && i.state.type !== "choice")
        .map((i) => ({ id: i.state.id, label: `${p.name}.${i.path}` })),
    }))
    .filter((g) => g.states.length);
}

/** Ids from a list of `Lifecycle.Path` labels (the Grid's multi cell gives labels back); unknown labels are dropped. */
export function statesOfLabels(raw: string, groups: readonly StateChoiceGroup[]): string[] {
  const byLabel = new Map(groups.flatMap((g) => g.states.map((s) => [s.label.toLowerCase(), s.id] as const)));
  return [
    ...new Set(
      raw
        .split(",")
        .map((x) => byLabel.get(x.trim().toLowerCase()))
        .filter((x): x is string => !!x),
    ),
  ];
}

/** The states of an object as the cell shows them, in file order. */
export function objectStatesText(states: readonly string[] | undefined, groups: readonly StateChoiceGroup[]): string {
  const labels = new Map(groups.flatMap((g) => g.states.map((s) => [s.id, s.label] as const)));
  return (states ?? []).map((id) => labels.get(id) ?? "?").join(", ");
}

/** An expected `values` map as `name=value, ...` (its keys are attribute names already). */
export function expectedValuesText(values: Json | undefined): string {
  return Object.entries(values ?? {})
    .map(([k, v]) => `${k}=${typeof v === "string" ? v : JSON.stringify(v)}`)
    .join(", ");
}
