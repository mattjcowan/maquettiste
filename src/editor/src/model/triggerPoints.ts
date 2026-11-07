// The Trigger points tab's pure model (trigger-points-design.md sections 4, 6 and 11): the rows its grid shows, one per entry of a
// lifecycle's `triggerPoints` in run order, and the edits the grid makes on a process document. Every edit mutates the draft
// document it is given, so one grid gesture is one draft change and one undo step.
import { newId } from "@/lib/ids";
import { uniqueName, type Json, type ProcessDoc, type SetDoc, type TriggerPointDoc } from "./process";
import { ATTRIBUTE_VERBS, LINK_VERBS } from "./rights";
import { builtInVerb } from "./verbList";

export type TriggerType = TriggerPointDoc["type"];
export type TriggerTiming = NonNullable<TriggerPointDoc["timing"]>;
export type TriggerSeverity = NonNullable<TriggerPointDoc["severity"]>;

/** The verbs that allow a replace (decision 17); a replace on any other is MQ8103. */
export const REPLACE_VERBS: ReadonlySet<string> = new Set(["delete", "disconnect", "copy", "revise"]);

export const TRIGGER_TYPES: readonly TriggerType[] = ["check", "action", "replace"];
export const TRIGGER_TIMINGS: readonly TriggerTiming[] = ["before", "with-change", "after-commit"];

/** What an empty body is: the team writes it. */
export const TEAM_CODE = "The team's code";

const strs = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
const text = (v: unknown): string => (typeof v === "string" ? v : "");

export const triggerPointsOf = (process: Pick<ProcessDoc, "triggerPoints">): TriggerPointDoc[] => process.triggerPoints ?? [];

/** Whether a replace can be made on every one of these verbs (and there is at least one). */
export const allowsReplace = (verbs: readonly string[]): boolean => verbs.length > 0 && verbs.every((v) => REPLACE_VERBS.has(v));

/** A move verb is a built-in verb of group `move` (decision 4). */
export const isMoveVerb = (key: string): boolean => builtInVerb(key)?.group === "move";

/** The keys `targets` and `transitions` fit: a move verb, `sign`, or an own verb (decision 4; whether an event names it is the engine's check). */
export const takesMoves = (verbs: readonly string[]): boolean => verbs.some((v) => isMoveVerb(v) || v === "sign" || !builtInVerb(v));

/** Whether `attributes` fits: `read` or `modify` (MQ8102). */
export const takesAttributes = (verbs: readonly string[]): boolean => verbs.some((v) => ATTRIBUTE_VERBS.has(v));

/** Whether `ends` fits: a verb that can act on a link (MQ8102). */
export const takesEnds = (verbs: readonly string[]): boolean => verbs.some((v) => LINK_VERBS.has(v));

export const TYPE_LABELS: Record<TriggerType, string> = { check: "Check", action: "Action", replace: "Replace" };
export const TIMING_LABELS: Record<TriggerTiming, string> = { before: "Before checks", "with-change": "With the change", "after-commit": "After commit" };
export const SEVERITY_LABELS: Record<TriggerSeverity, string> = { error: "Error", warning: "Warning" };

/** The names a row prints ids as. */
export interface TriggerNames {
  state(id: string): string;
  transition(id: string): string;
  entity(id: string): string;
  /** An attribute's or a relation end's name. */
  field(id: string): string;
  verb(key: string): string;
  /** A stored expression as text. */
  expression(tree: Json | undefined): string;
}

export interface TriggerPointRow {
  id: string;
  pointer: string;
  name: string;
  type: TriggerType;
  /** Cell texts: names joined, or empty. */
  verbs: string;
  typeLabel: string;
  timing: string;
  severity: string;
  /** The Where chips as a text, "in Draft, into Released"; empty means everywhere. */
  where: string;
  /** The Where parts, each a label and its names, for chips. */
  whereParts: { key: WhereKey; label: string; names: string }[];
  when: string;
  /** A check's condition, or an action's or a replace's sets; TEAM_CODE when the body is the team's. */
  what: string;
  teamCode: boolean;
  outside: boolean;
  why: string;
  verbKeys: string[];
  timingKey: TriggerTiming | null;
  severityKey: TriggerSeverity | null;
}

export type WhereKey = "states" | "targets" | "transitions" | "entities" | "attributes" | "ends";

export const WHERE_KEYS: readonly WhereKey[] = ["states", "targets", "transitions", "entities", "attributes", "ends"];

/** The label of each place in the Where chips and popover. */
export const WHERE_LABELS: Record<WhereKey, string> = {
  states: "In states",
  targets: "Into states",
  transitions: "Moves",
  entities: "Types",
  attributes: "Attributes",
  ends: "Links",
};

export const EVERYWHERE = "Everywhere";

/** What a set prints as: "attribute = value". */
export const setText = (set: SetDoc, names: Pick<TriggerNames, "field" | "expression">): string =>
  `${names.field(set.attribute)} = ${names.expression(set.value as Json) || "null"}`;

/** The rows of the grid, in file order (their run order at each moment). */
export function triggerPointRows(process: ProcessDoc, names: TriggerNames): TriggerPointRow[] {
  return triggerPointsOf(process).map((t, i) => {
    const type: TriggerType = t.type === "action" || t.type === "replace" ? t.type : "check";
    const nameOf: Record<WhereKey, (id: string) => string> = {
      states: names.state,
      targets: names.state,
      transitions: names.transition,
      entities: names.entity,
      attributes: names.field,
      ends: names.field,
    };
    const whereParts = WHERE_KEYS.flatMap((key) => {
      const ids = strs(t[key]);
      return ids.length ? [{ key, label: WHERE_LABELS[key], names: ids.map(nameOf[key]).join(", ") }] : [];
    });
    const sets = Array.isArray(t.sets) ? t.sets : [];
    const teamCode = type === "check" ? !t.condition : sets.length === 0;
    const what = type === "check" ? names.expression(t.condition) : sets.map((s) => setText(s, names)).join("; ");
    const timingKey = type === "action" && t.timing ? t.timing : null;
    const severityKey = type === "check" ? (t.severity === "warning" ? "warning" : "error") : null;
    return {
      id: t.id,
      pointer: `/triggerPoints/${i}`,
      name: text(t.name),
      type,
      verbs: strs(t.verbs).map(names.verb).join(", "),
      typeLabel: TYPE_LABELS[type],
      timing: timingKey ? TIMING_LABELS[timingKey] : "",
      severity: severityKey ? SEVERITY_LABELS[severityKey] : "",
      where: whereParts.map((p) => `${p.label.toLowerCase()} ${p.names}`).join(" · "),
      whereParts,
      when: names.expression(t.when),
      what: teamCode ? TEAM_CODE : what,
      teamCode,
      outside: t.outside === true,
      why: text(t.description),
      verbKeys: strs(t.verbs),
      timingKey,
      severityKey,
    };
  });
}

/** A name for a new trigger point that none of the lifecycle's trigger points, guards or actions has (MQ3001 covers points and guards). */
export const freeTriggerName = (process: ProcessDoc, base = "TriggerPoint"): string =>
  uniqueName(
    [...triggerPointsOf(process).map((t) => t.name), ...(process.guards ?? []).map((g) => g.name)].filter((n): n is string => typeof n === "string"),
    base,
  );

/** What Add trigger point makes: a check on `modify`, the commonest first one, until the cells say more. */
export interface NewTriggerPoint {
  name?: string;
  type?: TriggerType;
  verbs?: string[];
}

/** Adds a trigger point after `after` (or at the foot); returns its id. An action starts `before`, the moment that prepares the change. */
export function addTriggerPoint(process: ProcessDoc, init: NewTriggerPoint = {}, after?: string): string {
  const list = (process.triggerPoints ??= []);
  const id = newId();
  const type = init.type ?? "check";
  const point: TriggerPointDoc = {
    id,
    name: init.name?.trim() || freeTriggerName(process),
    type,
    verbs: init.verbs?.length ? init.verbs : ["modify"],
  };
  if (type === "action") point.timing = "before";
  const at = after ? list.findIndex((t) => t.id === after) : -1;
  list.splice(at < 0 ? list.length : at + 1, 0, point);
  return id;
}

export function removeTriggerPoint(process: ProcessDoc, id: string): void {
  process.triggerPoints = triggerPointsOf(process).filter((t) => t.id !== id);
  if (!process.triggerPoints.length) delete process.triggerPoints;
}

/** Moves a trigger point to an index of the list: the run order is the file order (decision 19). */
export function moveTriggerPoint(process: ProcessDoc, id: string, to: number): boolean {
  const list = triggerPointsOf(process);
  const from = list.findIndex((t) => t.id === id);
  if (from < 0 || to < 0 || to >= list.length || to === from) return false;
  const [one] = list.splice(from, 1);
  list.splice(to, 0, one);
  return true;
}

const doc = (t: TriggerPointDoc) => t as unknown as Json;

const setList = (target: Json, key: string, ids: string[]) => {
  if (ids.length) target[key] = ids;
  else delete target[key];
};

/** The keys a type does not have, taken out when the type changes. */
function fitType(t: TriggerPointDoc, type: TriggerType): void {
  t.type = type;
  if (type === "check") {
    delete t.timing;
    delete t.sets;
  } else {
    delete t.severity;
    delete t.condition;
    if (type === "replace") delete t.timing;
    else t.timing ??= "before";
  }
}

/** Replaces the sets of an action or a replace; none removes the key. */
export function setTriggerSets(process: ProcessDoc, id: string, sets: SetDoc[]): boolean {
  const t = triggerPointsOf(process).find((x) => x.id === id);
  if (!t || t.type === "check") return false;
  if (sets.length) t.sets = sets;
  else delete t.sets;
  return true;
}

/** The narrowing lists, as the Where popover gives them: each key is set, or removed when empty. */
export function setTriggerWhere(process: ProcessDoc, id: string, where: Partial<Record<WhereKey, string[]>>): boolean {
  const t = triggerPointsOf(process).find((x) => x.id === id);
  if (!t) return false;
  for (const key of WHERE_KEYS) if (key in where) setList(doc(t), key, where[key] ?? []);
  return true;
}

/**
 * One cell's commit. `verbs` comes as comma-separated keys from the pick cell, `type`, `timing` and `severity` as their keys. An
 * empty Does is refused (a trigger point names a verb), and so is a Replace beside a verb that does not allow it (MQ8103): the
 * cell keeps what it had. Changing the type takes out the keys the new type does not have. Returns false when nothing changed.
 */
export function editTriggerPoint(process: ProcessDoc, id: string, key: string, value: string | boolean): boolean {
  const t = triggerPointsOf(process).find((x) => x.id === id);
  if (!t) return false;
  const raw = String(value);
  const ids = raw.split(",").filter(Boolean);
  switch (key) {
    case "name": {
      const next = raw.trim();
      if (!next || next === t.name) return false;
      t.name = next;
      return true;
    }
    case "verbs":
      if (!ids.length) return false;
      if (t.type === "replace" && !allowsReplace(ids)) return false;
      t.verbs = ids;
      return true;
    case "type":
      if (!(TRIGGER_TYPES as readonly string[]).includes(raw) || raw === t.type) return false;
      if (raw === "replace" && !allowsReplace(t.verbs ?? [])) return false;
      fitType(t, raw as TriggerType);
      return true;
    case "timing":
      if (t.type !== "action" || !(TRIGGER_TIMINGS as readonly string[]).includes(raw)) return false;
      t.timing = raw as TriggerTiming;
      return true;
    case "severity":
      if (t.type !== "check") return false;
      if (raw === "warning") t.severity = "warning";
      else delete t.severity;
      return true;
    case "outside":
      if (value === true || raw === "true") t.outside = true;
      else delete t.outside;
      return true;
    case "when": {
      const next = raw.trim();
      if (next) t.when = { text: next };
      else delete t.when;
      return true;
    }
    case "condition": {
      if (t.type !== "check") return false;
      const next = raw.trim();
      if (next) t.condition = { text: next };
      else delete t.condition;
      return true;
    }
    case "why": {
      const next = raw.trim();
      if (next) t.description = next;
      else delete t.description;
      return true;
    }
    default:
      return false;
  }
}

/** The trigger points that go when these states are deleted: those whose every state or every target is among them. */
export function triggerPointsLostWithStates(process: Pick<ProcessDoc, "triggerPoints">, gone: ReadonlySet<string>): string[] {
  return triggerPointsOf(process)
    .filter((t) => [t.states, t.targets].some((list) => list?.length && list.every((s) => gone.has(s))))
    .map((t) => t.name || "an unnamed trigger point");
}

/** Which narrowing keys a trigger point's verbs leave room for, so the Where popover shows only those. */
export function whereKeysFor(verbs: readonly string[]): WhereKey[] {
  return WHERE_KEYS.filter((key) => {
    if (key === "targets" || key === "transitions") return takesMoves(verbs);
    if (key === "attributes") return takesAttributes(verbs);
    if (key === "ends") return takesEnds(verbs);
    return true;
  });
}
