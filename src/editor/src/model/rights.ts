// The Rights tab's pure model (rights-design.md section 10): the rows its grid shows, one per entry of a lifecycle's `rights`
// (an inline right, or a use of a named right), and the edits the grid makes on a process document. Every edit mutates the
// draft document it is given, so one grid gesture is one draft change and one undo step. A use shows the named right's fields
// read-only, with only its states editable.
import { newId } from "@/lib/ids";
import { idsOfNames, stateIndex, uniqueName, type Json, type ProcessDoc, type RightDoc } from "./process";

export type RightAccess = "allow" | "deny";

/** The verbs that can act on a link: `ends` beside any other verb is MQ9903. */
export const LINK_VERBS: ReadonlySet<string> = new Set(["connect", "disconnect", "read", "modify", "freeze", "thaw"]);

export const rightsOf = (process: Pick<ProcessDoc, "rights">): RightDoc[] => process.rights ?? [];

/** Whether the lifecycle says anything about rights: a default (written even as allow) or an entry. */
export const usesRights = (process: Pick<ProcessDoc, "rights" | "rightsDefault">): boolean =>
  process.rightsDefault !== undefined || rightsOf(process).length > 0;

/** The names a row prints ids as. */
export interface RightNames {
  actor(id: string): string;
  state(id: string): string;
  end(id: string): string;
  verb(key: string): string;
  /** A stored condition as text. */
  condition(tree: Json | undefined): string;
}

export interface RightRow {
  id: string;
  pointer: string;
  /** The entry uses a named right: its fields are read-only. */
  use: boolean;
  /** The named right's id, for a use. */
  named: string | null;
  name: string;
  access: RightAccess;
  /** Cell texts: names joined, or the word for none. */
  who: string;
  except: string;
  verbs: string;
  states: string;
  ends: string;
  when: string;
  why: string;
  /** The stored lists, for editors. */
  actorIds: string[];
  exceptIds: string[];
  verbKeys: string[];
  stateIds: string[];
  endIds: string[];
}

export const EVERYONE = "Everyone";
export const ALL_STATES = "All states";

const strings = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
const text = (v: unknown): string => (typeof v === "string" ? v : "");
const join = (ids: readonly string[], name: (id: string) => string, none = ""): string => (ids.length ? ids.map(name).join(", ") : none);

/** The rows of the grid, in file order. `named` holds the named rights' documents by id. */
export function rightRows(process: ProcessDoc, named: ReadonlyMap<string, Json>, names: RightNames): RightRow[] {
  return rightsOf(process).map((entry, i) => {
    const use = typeof entry.right === "string";
    const source = (use ? (named.get(entry.right!) ?? {}) : entry) as Json;
    const actorIds = strings(source.actors);
    const exceptIds = strings(source.except);
    const verbKeys = strings(source.verbs);
    const endIds = strings(source.ends);
    const stateIds = strings(entry.states);
    return {
      id: entry.id,
      pointer: `/rights/${i}`,
      use,
      named: use ? entry.right! : null,
      name: text(source.name) || (use ? "?" : ""),
      access: source.access === "allow" ? "allow" : "deny",
      who: join(actorIds, names.actor, EVERYONE),
      except: join(exceptIds, names.actor),
      verbs: join(verbKeys, names.verb),
      states: join(stateIds, names.state, ALL_STATES),
      ends: join(endIds, names.end),
      when: names.condition(source.condition as Json | undefined),
      why: text(entry.description) || text(source.description),
      actorIds,
      exceptIds,
      verbKeys,
      stateIds,
      endIds,
    };
  });
}

/** The states a right may name: this lifecycle's, but not a history or a choice (MQ9902), with their dotted paths. */
export function rightStateOptions(process: ProcessDoc): { value: string; label: string }[] {
  return [...stateIndex(process)]
    .filter(([, info]) => info.state.type !== "history" && info.state.type !== "choice")
    .map(([id, info]) => ({ value: id, label: info.path }));
}

/** What the Add right dialog asks: a right has at least one verb, so nothing is added without them. */
export interface NewRight {
  name: string;
  access: RightAccess;
  verbs: string[];
  /** Empty: everyone. */
  actors?: string[];
  /** Empty: every state. */
  states?: string[];
}

/** The access a new right starts with: it denies, or allows when the lifecycle denies by default (an allow beside an allowing default is MQ9904). */
export const defaultAccess = (process: Pick<ProcessDoc, "rightsDefault">): RightAccess => (process.rightsDefault === "deny" ? "allow" : "deny");

/** A name for a new right that no right of the lifecycle has. */
export const freeRightName = (process: ProcessDoc, base = "right"): string =>
  uniqueName(
    rightsOf(process).flatMap((r) => (r.name ? [r.name] : [])),
    base,
  );

/** Adds an inline right as the dialog described it; returns its id, or null when it names no verb (nothing is added). */
export function addRight(process: ProcessDoc, init: NewRight, after?: string): string | null {
  if (!init.verbs.length) return null;
  const list = (process.rights ??= []);
  const id = newId();
  const right: RightDoc = { id, name: init.name.trim() || freeRightName(process), access: init.access, verbs: init.verbs };
  if (init.actors?.length) right.actors = init.actors;
  if (init.states?.length) right.states = init.states;
  const at = after ? list.findIndex((r) => r.id === after) : -1;
  list.splice(at < 0 ? list.length : at + 1, 0, right);
  return id;
}

/** Adds a use of a named right; returns its entry's id. */
export function addRightUse(process: ProcessDoc, right: string): string {
  const id = newId();
  (process.rights ??= []).push({ id, right });
  return id;
}

export function removeRight(process: ProcessDoc, id: string): void {
  process.rights = rightsOf(process).filter((r) => r.id !== id);
  if (!process.rights.length) delete process.rights;
}

/** The default a lifecycle says: `deny` is written, allow is the absence (it is what nothing said means). */
export function setRightsDefault(process: ProcessDoc, value: RightAccess): void {
  if (value === "deny") process.rightsDefault = "deny";
  else delete process.rightsDefault;
}

const setList = (target: Json, key: string, ids: string[]) => {
  if (ids.length) target[key] = ids;
  else delete target[key];
};

/** The names in a typed list of actors that no actor has: the cell refuses them, it never reads them as everyone. "Everyone" is no name. */
export function unknownActors(raw: string, actors: { id: string; name: string }[]): string[] {
  const known = new Set(actors.map((a) => a.name.toLowerCase()));
  return raw
    .split(",")
    .map((p) => p.trim())
    .filter((p) => p && p.toLowerCase() !== EVERYONE.toLowerCase() && !known.has(p.toLowerCase()));
}

export interface RightEditOptions {
  actors: { id: string; name: string }[];
}

/**
 * One cell's commit. The lists come as the cell edits them: `who` and `except` as typed names, `verbs`, `states` and `ends` as
 * comma-separated keys or ids from a pick cell. An empty Who means everyone and an empty In states every state, so the key is
 * left out. A use takes only `states`. Returns false when nothing changed (a use's read-only cell, or an inline right's verbs
 * left empty, which a right cannot be).
 */
export function editRight(process: ProcessDoc, id: string, key: string, value: string | boolean, options: RightEditOptions): boolean {
  const entry = rightsOf(process).find((r) => r.id === id);
  if (!entry) return false;
  const raw = String(value);
  const ids = raw.split(",").filter(Boolean);
  if (key === "states") {
    setList(entry as unknown as Json, "states", ids);
    return true;
  }
  if (typeof entry.right === "string") return false;
  const doc = entry as unknown as Json;
  if ((key === "who" || key === "except") && unknownActors(raw, options.actors).length) return false;
  switch (key) {
    case "name":
      entry.name = raw.trim() || entry.name;
      return true;
    case "access":
      entry.access = raw === "allow" ? "allow" : "deny";
      return true;
    case "who":
      setList(doc, "actors", idsOfNames(raw, options.actors));
      return true;
    case "except":
      setList(doc, "except", idsOfNames(raw, options.actors));
      return true;
    case "verbs":
      if (!ids.length) return false;
      entry.verbs = ids;
      return true;
    case "ends":
      setList(doc, "ends", ids);
      return true;
    case "when": {
      const t = raw.trim();
      if (t) entry.condition = { text: t };
      else delete entry.condition;
      return true;
    }
    case "why": {
      const t = raw.trim();
      if (t) entry.description = t;
      else delete entry.description;
      return true;
    }
    default:
      return false;
  }
}

/** A short summary of a right for lists: "Deny modify, delete in Released". */
export function rightSummary(row: Pick<RightRow, "access" | "verbs" | "states">): string {
  const where = row.states === ALL_STATES ? "" : ` in ${row.states}`;
  return `${row.access === "allow" ? "Allow" : "Deny"} ${row.verbs.toLowerCase()}${where}`;
}

/**
 * The rights that go when these states are deleted: those whose every state is among them (an empty list would widen a right to
 * every state, so the right goes with its last one). A use of a named right is called by the name `named` gives it.
 */
export function rightsLostWithStates(
  process: Pick<ProcessDoc, "rights">,
  gone: ReadonlySet<string>,
  named: (id: string) => string = () => "a named right",
): string[] {
  return rightsOf(process)
    .filter((r) => r.states?.length && r.states.every((s) => gone.has(s)))
    .map((r) => (r.right ? named(r.right) : r.name || "an unnamed right"));
}

/** The Links column shows only when some right says `ends`. */
export const hasEnds = (rows: readonly Pick<RightRow, "endIds">[]): boolean => rows.some((r) => r.endIds.length > 0);

/** Whether a right's verbs include one that can't act on a link, while it names ends (MQ9903). */
export const endsBesideNonLinkVerb = (verbs: readonly string[], ends: readonly string[]): boolean => ends.length > 0 && verbs.some((v) => !LINK_VERBS.has(v));
