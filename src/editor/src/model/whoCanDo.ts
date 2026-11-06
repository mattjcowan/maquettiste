// "Who can do what" (rights-design.md section 10): the pure model behind the view the Rights tab toggles to. It reads the rows
// of `get_rights` (one per state, actor and verb, and per entity where answers differ) and shapes one state at a time: actors as
// rows, verbs as columns, each cell Allowed, Denied or Depends, greyed when the lifecycle's default decides it.
import type { components } from "@/api/schema";
import type { Json, ProcessDoc } from "./process";

export type RightsRow = components["schemas"]["RightsRow"];

export const EVERYONE_ELSE = "Everyone else";

/** The names a view prints ids as. */
export interface WhoNames {
  /** An actor's name; null is everyone else. */
  actor(id: string | null): string;
  verb(key: string): string;
  /** A lifecycle's name, for "Depends on HR". */
  lifecycle(id: string): string;
  entity(id: string): string;
}

export interface WhoCell {
  answer: RightsRow["answer"];
  /** "Allowed", "Denied", "Depends", or "Depends on HR" when other lifecycles decide. */
  text: string;
  /** The lifecycle's default decides it: shown greyed. */
  grey: boolean;
  by: string[];
  dependsOn: string[];
}

export interface WhoView {
  actors: { id: string | null; label: string }[];
  verbs: { key: string; label: string }[];
  cell(actor: string | null, verb: string): WhoCell | null;
}

const WORDS: Record<RightsRow["answer"], string> = { allowed: "Allowed", denied: "Denied", depends: "Depends" };

export function cellOf(row: RightsRow, names: Pick<WhoNames, "lifecycle">): WhoCell {
  const others = row.dependsOn.map(names.lifecycle).join(", ");
  return {
    answer: row.answer,
    text: row.answer === "depends" && others ? `${WORDS.depends} on ${others}` : WORDS[row.answer],
    grey: row.byDefault,
    by: row.by,
    dependsOn: row.dependsOn,
  };
}

/** The states the rows speak of, in the order they first appear. */
export function whoStates(rows: readonly RightsRow[]): string[] {
  return [...new Set(rows.map((r) => r.state))];
}

/** The entities rows are split by; empty when one row holds for every entity (the picker then stays hidden). */
export function whoEntities(rows: readonly RightsRow[]): string[] {
  return [...new Set(rows.flatMap((r) => (r.entity ? [r.entity] : [])))];
}

/** The rows of one state; with an entity, the rows that hold for it (those with no entity hold for all). */
export function rowsFor(rows: readonly RightsRow[], state: string, entity?: string | null): RightsRow[] {
  return rows.filter((r) => r.state === state && (!r.entity || !entity || r.entity === entity));
}

/** One state as a grid: actors as rows (everyone else last), verbs as columns, in the order the answer gave them. */
export function whoView(rows: readonly RightsRow[], state: string, entity: string | null, names: WhoNames): WhoView {
  const mine = rowsFor(rows, state, entity);
  const actors: WhoView["actors"] = [];
  const verbs: WhoView["verbs"] = [];
  const cells = new Map<string, WhoCell>();
  for (const r of mine) {
    if (!actors.some((a) => a.id === r.actor)) actors.push({ id: r.actor, label: r.actor === null ? EVERYONE_ELSE : names.actor(r.actor) });
    if (!verbs.some((v) => v.key === r.verb)) verbs.push({ key: r.verb, label: names.verb(r.verb) });
    // An entity's own row wins over the row that holds for all.
    if (!cells.has(`${r.actor}|${r.verb}`) || r.entity) cells.set(`${r.actor}|${r.verb}`, cellOf(r, names));
  }
  actors.sort((a, b) => (a.id === null ? 1 : b.id === null ? -1 : a.label.localeCompare(b.label)));
  return { actors, verbs, cell: (actor, verb) => cells.get(`${actor}|${verb}`) ?? null };
}

export interface Decider {
  id: string;
  name: string;
  /** What it is: a right, an event, a task, a gate or a lifecycle. */
  kind: "right" | "event" | "task" | "gate" | "lifecycle";
}

/** What decides a cell, named, from the lifecycle's own parts, the named rights it uses and the other lifecycles. */
export function deciders(
  cell: Pick<WhoCell, "by" | "dependsOn">,
  process: ProcessDoc,
  named: ReadonlyMap<string, Json>,
  lifecycle: (id: string) => string | null,
): Decider[] {
  const find = (id: string): Decider => {
    const entry = (process.rights ?? []).find((r) => r.id === id || r.right === id);
    if (entry) return { id, name: String(entry.right ? (named.get(entry.right)?.name ?? "?") : (entry.name ?? "?")), kind: "right" };
    const event = (process.events ?? []).find((e) => e.id === id);
    if (event) return { id, name: event.name, kind: "event" };
    for (const t of process.transitions ?? []) if (t.gate?.id === id) return { id, name: t.gate.name, kind: "gate" };
    const task = walk(process.states).find((i) => i.id === id);
    if (task) return { id, name: task.name, kind: "task" };
    const other = lifecycle(id);
    if (id === process.id || other !== null) return { id, name: id === process.id ? process.name : (other ?? "?"), kind: "lifecycle" };
    const right = named.get(id);
    return right ? { id, name: String(right.name ?? "?"), kind: "right" } : { id, name: "?", kind: "right" };
  };
  return [...new Set([...cell.by, ...cell.dependsOn])].map(find);
}

/**
 * The rights that except an actor for a verb in a state: they cover the verb and the state and list the actor under Except,
 * so they never apply to it (the cell's answer comes from the rest).
 */
export function exceptions(actor: string | null, verb: string, state: string, process: ProcessDoc, named: ReadonlyMap<string, Json>): string[] {
  if (!actor) return [];
  const list = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
  return (process.rights ?? []).flatMap((entry) => {
    const source = (entry.right ? (named.get(entry.right) ?? {}) : entry) as Json;
    const states = list(entry.states);
    if (!list(source.except).includes(actor) || !list(source.verbs).includes(verb) || (states.length && !states.includes(state))) return [];
    const name = typeof source.name === "string" ? source.name : "";
    return name ? [name] : [];
  });
}

function walk(list: ProcessDoc["states"] | undefined): { id: string; name: string }[] {
  return (list ?? []).flatMap((s) => [...(s.invoke ?? []), ...walk(s.states)]);
}
