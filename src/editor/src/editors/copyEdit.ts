// What a copy of a whole record carries over (parts-and-copy-design.md): the entity editor's Copy tab, free of React. The entity's own
// `copy` block is what is written; an entry a base wrote shows "from <Base>" and is replaced by choosing another word, which writes
// the entity's own entry (`copy` and `all` undo a base's entry). Only entries that differ from the default are written.
import type { ElementSummary } from "@/api/types";
import { endCoversEntity, type EndLike } from "@/model/endTypes";

export type Json = Record<string, unknown>;

export type AttributeWord = "copy" | "reset" | "clear" | "original";
export type LinkWord = "copy" | "original";
export type PartWord = "all" | "one" | "none";

export const ATTRIBUTE_WORDS: Record<AttributeWord, string> = { copy: "Copied", reset: "Reset", clear: "Cleared", original: "Id of the original" };
export const LINK_WORDS: Record<LinkWord, string> = { copy: "Copied", original: "The original" };
export const PART_WORDS: Record<PartWord, string> = { all: "All", one: "One", none: "None" };

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string | undefined => (typeof v === "string" ? v : undefined);

// ------------------------------------------------------------------ reading the blocks

export interface Entry {
  /** The entry as written. */
  entry: Json;
  /** The id of the entity whose block writes it. */
  from: string;
}

/**
 * The entries of one list of the block merged through the entity's bases: the nearest entry for each attribute or end wins. `chain`
 * holds the entity's document, then its bases', nearest first.
 */
export function mergedEntries(chain: readonly Json[], list: "attributes" | "links" | "parts", idKey: "attribute" | "end"): Map<string, Entry> {
  const out = new Map<string, Entry>();
  for (const doc of [...chain].reverse())
    for (const entry of arr((doc.copy as Json | undefined)?.[list])) {
      const id = str(entry[idKey]);
      if (id) out.set(id, { entry, from: String(doc.id) });
    }
  return out;
}

/** The same, from the bases alone: what applies when the entity writes no entry of its own. */
export const inheritedEntries = (chain: readonly Json[], list: "attributes" | "links" | "parts", idKey: "attribute" | "end") =>
  mergedEntries(chain.slice(1), list, idKey);

// ------------------------------------------------------------------ what can be listed

export interface AttributeLike {
  id: string;
  name: string;
  type?: unknown;
  required?: boolean;
  default?: unknown;
  defaultExpression?: string;
  derived?: unknown;
  collection?: boolean;
}

/** An attribute a copy entry may name: held by the entity or a base, root first, not derived and not a key attribute (MQ3045). */
export interface CopyAttribute extends AttributeLike {
  /** The entity that holds it. */
  owner: string;
  /** The words the engine accepts for it, `copy` first. */
  words: AttributeWord[];
}

/** What the engine's expression types make of a type: built-ins by kind and width, a custom type by its base; null for anything else (an enum, a value object). */
function kindOfType(type: unknown, scalarBase: (typeId: string) => unknown): { kind: string; bits: number } | null {
  const kinds: Record<string, [string, number]> = {
    string: ["string", 0],
    text: ["string", 0],
    bool: ["bool", 0],
    int16: ["int", 16],
    int32: ["int", 32],
    int64: ["int", 64],
    decimal: ["decimal", 0],
    double: ["double", 0],
    date: ["date", 0],
    time: ["time", 0],
    datetime: ["datetime", 0],
    datetimeoffset: ["datetimeoffset", 0],
    uuid: ["uuid", 0],
    ulid: ["uuid", 0],
    json: ["json", 0],
  };
  let at = type;
  for (let hops = 0; hops < 8; hops++) {
    if (typeof at === "object" && at !== null && typeof (at as { ref?: unknown }).ref === "string") at = scalarBase((at as { ref: string }).ref);
    else if (typeof at === "string") {
      const found = kinds[at];
      if (found) return { kind: found[0], bits: found[1] };
      const next = scalarBase(at);
      if (next === undefined) return null;
      at = next;
    } else return null;
  }
  return null;
}

/**
 * The attributes of an entity (own and inherited, root first) that a copy entry may name (MQ3045, MQ3046): not derived, not in the key (the
 * nearest key of the entity or a base). `clear` is left out for a required attribute, `reset` for a required one with no default, and `original`
 * unless the key is one attribute and this one is a single value of the same kind with at least its width, never a value object.
 * `scalarBase` gives a custom type's base.
 */
export function copyAttributes(chain: readonly Json[], scalarBase: (typeId: string) => unknown = () => undefined): CopyAttribute[] {
  const keys = new Set<string>(((chain.find((d) => d.key)?.key as { attributes?: string[] } | undefined)?.attributes ?? []) as string[]);
  const attributes = [...chain].reverse().flatMap((doc) => arr(doc.attributes).map((a) => ({ a: a as unknown as AttributeLike, owner: String(doc.id) })));
  const keyAttributes = attributes.filter((x) => keys.has(x.a.id));
  const keyType = keyAttributes.length === 1 && keys.size === 1 ? kindOfType(keyAttributes[0].a.type, scalarBase) : null;
  return attributes
    .filter((x) => !keys.has(x.a.id) && !x.a.derived)
    .map(({ a, owner }) => {
      const words: AttributeWord[] = ["copy"];
      const hasDefault = a.default !== undefined || !!a.defaultExpression;
      if (!a.required || hasDefault) words.push("reset");
      if (!a.required) words.push("clear");
      const mine = keyType && !a.collection ? kindOfType(a.type, scalarBase) : null;
      if (keyType && mine && mine.kind === keyType.kind && mine.bits >= keyType.bits) words.push("original");
      return { ...a, owner, words };
    });
}

/** A to-one end the entity holds at the other side of a two-ended relation that is not a composition (MQ3045). */
export interface LinkEnd {
  id: string;
  label: string;
  /** Whether the end's own type covers the entity, so `original` can stand (MQ3046). */
  original: boolean;
}

/**
 * The links of the entity a copy entry may name: for each two-ended relation that is not a composition, the end with max 1 whose other end
 * covers the entity or a base of it. `rows` are the relation's index rows (their ends in the document's order), `docs` the documents.
 */
export function linkEnds(
  entityId: string,
  rows: readonly ElementSummary[],
  docs: ReadonlyMap<string, Json>,
  baseOf: (id: string) => string | undefined,
): LinkEnd[] {
  const out: LinkEnd[] = [];
  for (const row of rows) {
    const doc = docs.get(row.id);
    const ends = arr(doc?.ends);
    const rowEnds = (row.ends ?? []) as EndLike[];
    if (!doc || ends.length !== 2 || rowEnds.length !== 2 || doc.relationKind === "composition" || doc.relationKind === "n-ary") continue;
    ends.forEach((end, i) => {
      if (end.max !== 1 || !endCoversEntity(rowEnds[1 - i], entityId, baseOf)) return;
      out.push({ id: String(end.id), label: `${String(doc.name)}.${String(end.role)}`, original: endCoversEntity(rowEnds[i], entityId, baseOf) });
    });
  }
  return out.sort((a, b) => a.label.localeCompare(b.label) || a.id.localeCompare(b.id));
}

/** One part of a whole record, as the Copy tab lists it: the engine's `entity.parts` without keys and order, worked out from the documents. */
export interface PartInfo {
  /** The part end ids on the way down, joined by `/`. */
  id: string;
  /** The composition's part end id: what a copy entry names. */
  end: string;
  entity: string;
  owner: string;
  depth: number;
  /** The part end's role: how the part is named in the model. */
  role: string;
  recursive: boolean;
}

/**
 * The parts of a record of an entity at every level down (parts-and-copy-design.md section 2): a composition of two ends, the owner end the
 * one with max 1, whose owner end covers the owner, and whose part end has one type. A part whose entity is already on the way down is listed
 * once and not walked again. `rows` are the relation index rows that can touch an entity (`related` gives them), `docs` their documents.
 */
export function partTree(
  entityId: string,
  related: (entity: string) => readonly ElementSummary[],
  docs: ReadonlyMap<string, Json>,
  baseOf: (id: string) => string | undefined,
): PartInfo[] {
  const out: PartInfo[] = [];
  const walk = (owner: string, depth: number, path: string[], prefix: string) => {
    // The owner's relations in name, then id, order, as the engine walks them.
    const sorted = [...related(owner)].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
    for (const row of sorted) {
      const doc = docs.get(row.id);
      const ends = arr(doc?.ends);
      const rowEnds = (row.ends ?? []) as EndLike[];
      if (!doc || doc.relationKind !== "composition" || ends.length !== 2 || rowEnds.length !== 2) continue;
      const ownerIdx = ends[0].max === 1 ? 0 : ends[1].max === 1 ? 1 : -1;
      if (ownerIdx < 0 || !endCoversEntity(rowEnds[ownerIdx], owner, baseOf)) continue;
      const part = rowEnds[1 - ownerIdx].entity;
      if (!part || out.length >= 10_000) continue;
      const end = ends[1 - ownerIdx];
      const recursive = path.includes(part);
      const id = prefix ? `${prefix}/${String(end.id)}` : String(end.id);
      out.push({ id, end: String(end.id), entity: part, owner, depth, role: String(end.role), recursive });
      if (!recursive) walk(part, depth + 1, [...path, part], id);
    }
  };
  walk(entityId, 1, [entityId], "");
  return out;
}

/** Whether a type holds an order a copy can take the latest by: a number, a text, a date or a time (MQ3048). `scalarBase` gives a custom type's base. */
export function isOrderable(type: unknown, scalarBase: (typeId: string) => unknown): boolean {
  const ORDER = new Set(["string", "text", "int16", "int32", "int64", "decimal", "double", "date", "time", "datetime", "datetimeoffset"]);
  for (let at = type, hops = 0; hops < 8; hops++) {
    if (typeof at === "string") {
      if (ORDER.has(at)) return true;
      const next = scalarBase(at);
      if (next === undefined) return false;
      at = next;
    } else if (typeof at === "object" && at !== null && typeof (at as { ref?: unknown }).ref === "string") {
      at = scalarBase((at as { ref: string }).ref);
    } else return false;
  }
  return false;
}

// ------------------------------------------------------------------ edits (each one gesture: one save, one undo step)

function block(json: Json): Json {
  if (typeof json.copy !== "object" || json.copy === null) json.copy = {};
  return json.copy as Json;
}

/** Drops an empty list, then an empty block. */
function tidy(json: Json): void {
  const copy = json.copy as Json | undefined;
  if (!copy) return;
  for (const k of ["attributes", "links", "parts"]) if (Array.isArray(copy[k]) && (copy[k] as unknown[]).length === 0) delete copy[k];
  if (Object.keys(copy).length === 0) delete json.copy;
}

const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T;

function listOf(copy: Json, key: "attributes" | "links" | "parts"): Json[] {
  if (!Array.isArray(copy[key])) copy[key] = [];
  return copy[key] as Json[];
}

/**
 * Writes what a copy does with one attribute or link. `copy` is the default: it removes the entity's own entry, unless a base has one
 * that is not `copy` (then `copy` is written, to undo it).
 */
export function setCopyWord(json: Json, key: "attributes" | "links", id: string, word: string, inheritedWord: string | undefined): void {
  const idKey = key === "attributes" ? "attribute" : "end";
  const list = listOf(block(json), key);
  const at = list.findIndex((e) => e[idKey] === id);
  if (word === "copy" && (inheritedWord === undefined || inheritedWord === "copy")) {
    if (at >= 0) list.splice(at, 1);
  } else if (at >= 0) list[at] = { [idKey]: id, onCopy: word };
  else list.push({ [idKey]: id, onCopy: word });
  tidy(json);
}

/** Whether the entity's own block has an entry for the attribute or link. */
export const hasOwnCopyEntry = (json: Json, key: "attributes" | "links" | "parts", id: string): boolean =>
  arr((json.copy as Json | undefined)?.[key]).some((e) => (key === "attributes" ? e.attribute : e.end) === id);

/** The entity's own entry for a part, cloned from the base's when it has none (an entry replaces a base's as a whole), else a new `all`. */
function ownPart(json: Json, end: string, inherited: Json | undefined): Json {
  const list = listOf(block(json), "parts");
  const at = list.findIndex((e) => e.end === end);
  if (at >= 0) return list[at];
  const entry = inherited ? clone(inherited) : { end, onCopy: "all" };
  list.push(entry);
  return entry;
}

/** A part entry that says nothing a missing one doesn't: `all`, no choice, no field entries. */
const plain = (e: Json) => e.onCopy === "all" && !e.choose && arr(e.attributes).length === 0;

function dropPlainPart(json: Json, end: string, inherited: Json | undefined): void {
  const list = arr((json.copy as Json | undefined)?.parts);
  const at = list.findIndex((e) => e.end === end);
  if (at >= 0 && !inherited && plain(list[at])) list.splice(at, 1);
  tidy(json);
}

/** Says which parts of a kind a copy carries: all, one (chosen as `choose` says), or none. */
export function setPartWord(json: Json, end: string, word: PartWord, inherited: Json | undefined): void {
  // `one` needs a choice (MQ3047): without one already written, it is set by setPartChoice, not here.
  const own = arr((json.copy as Json | undefined)?.parts).find((e) => e.end === end);
  // An own entry without a choice (`all` over a base's `one`) takes the base's choice back.
  const choose = own?.choose ?? inherited?.choose;
  if (word === "one" && !choose) return;
  const entry = ownPart(json, end, inherited);
  if (word === "one" && !entry.choose) entry.choose = JSON.parse(JSON.stringify(choose));
  entry.onCopy = word;
  if (word !== "one") delete entry.choose;
  if (word === "none") delete entry.attributes;
  dropPlainPart(json, end, inherited);
}

/** How one part is chosen: a condition or a state, and the attribute whose greatest value is the latest. Writing one makes the entry `one`. */
export interface Choice {
  /** The condition's text, or its tree as it is stored (kept when it didn't change). */
  condition?: string | Json;
  state?: string;
  latestBy?: string;
}

export function setPartChoice(json: Json, end: string, inherited: Json | undefined, choice: Choice): boolean {
  const choose: Json = {};
  if (typeof choice.condition === "string" ? choice.condition.trim() : choice.condition) {
    choose.condition = typeof choice.condition === "string" ? { text: choice.condition.trim() } : choice.condition;
  } else if (choice.state) choose.state = choice.state;
  if (choice.latestBy) choose.latestBy = choice.latestBy;
  // `one` needs a choice (MQ3047): with none to write, nothing is written and the entry stays as it was.
  if (!Object.keys(choose).length) return false;
  const entry = ownPart(json, end, inherited);
  entry.onCopy = "one";
  entry.choose = choose;
  return true;
}

/** What the choice says now: a condition (kept as its tree when it is not text), a state and the latest-by attribute. */
export function choiceOf(entry: Json | undefined): { condition?: Json; state?: string; latestBy?: string } {
  const choose = entry?.choose as Json | undefined;
  return {
    ...(choose?.condition ? { condition: choose.condition as Json } : {}),
    ...(str(choose?.state) ? { state: str(choose?.state) } : {}),
    ...(str(choose?.latestBy) ? { latestBy: str(choose?.latestBy) } : {}),
  };
}

/** What a copy does with one attribute of a kind of part; `copy` removes the entry. */
export function setPartAttributeWord(json: Json, end: string, inherited: Json | undefined, attribute: string, word: string): void {
  const entry = ownPart(json, end, inherited);
  const list = arr(entry.attributes).filter((e) => e.attribute !== attribute);
  if (word !== "copy") list.push({ attribute, onCopy: word });
  if (list.length) entry.attributes = list;
  else delete entry.attributes;
  dropPlainPart(json, end, inherited);
}

/** Removes the entity's own entry for an attribute or link, so a base's applies again. */
export function removeOwnCopyEntry(json: Json, key: "attributes" | "links", id: string): void {
  const copy = json.copy as Json | undefined;
  if (!copy || !Array.isArray(copy[key])) return;
  const idKey = key === "attributes" ? "attribute" : "end";
  copy[key] = (copy[key] as Json[]).filter((e) => e[idKey] !== id);
  tidy(json);
}

/** Removes the entity's own entry for a part, so a base's applies again (or the part is copied whole). */
export function removeOwnPart(json: Json, end: string): void {
  const copy = json.copy as Json | undefined;
  if (!copy || !Array.isArray(copy.parts)) return;
  copy.parts = (copy.parts as Json[]).filter((e) => e.end !== end);
  tidy(json);
}

// ------------------------------------------------------------------ a condition as text

// The engine's text grammar (ExprText.cs, ExprFunctions.cs): precedence or 2, and 3, not 4, comparison 5, a primary 11; a name is written bare
// only when it is plain and not reserved, else in double quotes.
const RESERVED = new Set([
  "and",
  "or",
  "not",
  "implies",
  "is",
  "null",
  "true",
  "false",
  "between",
  "in",
  "distinct",
  "from",
  "case",
  "when",
  "then",
  "else",
  "end",
  "satisfies",
]);
const PREC: Record<string, number> = { or: 2, and: 3, not: 4, eq: 5, ne: 5, lt: 5, le: 5, gt: 5, ge: 5 };
const SYMBOL: Record<string, string> = { eq: "=", ne: "<>", lt: "<", le: "<=", gt: ">", ge: ">=" };
const TYPED = new Set(["decimal", "double", "date", "time", "datetime", "datetimeoffset", "uuid"]);

/** A name as the engine writes it: bare when plain and not reserved, else double-quoted. */
export function quoteName(name: string): string {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(name) && !RESERVED.has(name.toLowerCase()) ? name : '"' + name.replace(/"/g, '""') + '"';
}

export interface ConditionNames {
  /** The name of an attribute by id. */
  name(id: string): string | undefined;
  /** An enum member as written, `Enum.Member` with each part quoted as needed, by the member's id. */
  member(id: string): string | undefined;
}

/**
 * A stored condition tree as text, for the part of the engine's grammar printed exactly as the engine prints it, so the text reads back as
 * the same tree: and, or, not, the comparisons, attribute names, plain numbers, strings and booleans, typed literals (`1.50`, `date '2024-01-01'`)
 * and enum members (`Status.Draft`). Anything else (arithmetic, calls, null, navigation, cases, a negative number) returns null: the box then
 * shows it read only, with a note to edit it on the JSON tab, instead of printing something that reads back differently.
 */
export function printCondition(tree: unknown, names: ConditionNames): string | null {
  if (!tree || typeof tree !== "object") return null;
  if (typeof (tree as Json).text === "string") return (tree as Json).text as string;
  const str = (v: string) => "'" + v.replace(/'/g, "''") + "'";
  const leaf = (n: Json): string | null => {
    if (n.type !== undefined) {
      if (typeof n.type !== "string" || !TYPED.has(n.type) || typeof n.value !== "string") return null;
      return n.type === "decimal" && /^-?\d+\.\d+$/.test(n.value) ? n.value : n.type + " " + str(n.value);
    }
    if (typeof n.value === "boolean") return String(n.value);
    if (typeof n.value === "number") return Number.isFinite(n.value) && /^\d+(\.\d+)?$/.test(String(n.value)) ? String(n.value) : null;
    if (typeof n.value === "string") return str(n.value);
    if (Array.isArray(n.path)) {
      const id = n.path.length === 1 && n.old !== true ? names.name(String(n.path[0])) : undefined;
      return id === undefined ? null : quoteName(id);
    }
    if (typeof n.ref === "string") return names.member(n.ref) ?? null;
    return null;
  };
  // Prints a node that is written where an operand of at least `min` precedence stands, in parentheses when it binds looser.
  const write = (node: unknown, min: number): string | null => {
    const n = node as Json;
    if (!n || typeof n !== "object" || typeof n.text === "string") return null;
    const args = Array.isArray(n.args) ? (n.args as unknown[]) : [];
    if (typeof n.op !== "string") return leaf(n);
    const own = PREC[n.op];
    if (!own) return null;
    let out: string | null = null;
    if ((n.op === "and" || n.op === "or") && args.length >= 2) {
      const parts = args.map((a) => write(a, own));
      out = parts.every((x) => x !== null) ? parts.join(" " + n.op + " ") : null;
    } else if (n.op === "not" && args.length === 1) {
      const x = write(args[0], own);
      out = x === null ? null : "not " + x;
    } else if (SYMBOL[n.op] && args.length === 2) {
      // Comparisons do not chain: both sides bind tighter.
      const l = write(args[0], own + 1);
      const r = write(args[1], own + 1);
      out = l === null || r === null ? null : l + " " + SYMBOL[n.op] + " " + r;
    }
    return out === null ? null : own < min ? "(" + out + ")" : out;
  };
  return write(tree, 1);
}
