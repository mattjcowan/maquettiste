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

/**
 * The attributes of an entity (own and inherited, root first) that a copy entry may name. `clear` is left out for a required attribute,
 * `reset` for a required one with no default, and `original` unless the key is one attribute of the same type (MQ3045, MQ3046).
 */
export function copyAttributes(chain: readonly Json[]): CopyAttribute[] {
  const keys = new Set<string>();
  for (const doc of chain) for (const id of ((doc.key as { attributes?: string[] } | undefined)?.attributes ?? []) as string[]) keys.add(id);
  const attributes = [...chain].reverse().flatMap((doc) => arr(doc.attributes).map((a) => ({ a: a as unknown as AttributeLike, owner: String(doc.id) })));
  const keyAttributes = attributes.filter((x) => keys.has(x.a.id));
  const keyType = keyAttributes.length === 1 ? JSON.stringify(keyAttributes[0].a.type ?? null) : null;
  return attributes
    .filter((x) => !keys.has(x.a.id) && !x.a.derived)
    .map(({ a, owner }) => {
      const words: AttributeWord[] = ["copy"];
      const hasDefault = a.default !== undefined || !!a.defaultExpression;
      if (!a.required || hasDefault) words.push("reset");
      if (!a.required) words.push("clear");
      if (keyType !== null && !a.collection && JSON.stringify(a.type ?? null) === keyType) words.push("original");
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
    for (const row of related(owner)) {
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
  const ORDER = new Set(["string", "text", "int16", "int32", "int64", "decimal", "float", "double", "date", "time", "datetime", "datetimeoffset"]);
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
  const entry = ownPart(json, end, inherited);
  entry.onCopy = word;
  if (word !== "one") delete entry.choose;
  if (word === "none") delete entry.attributes;
  dropPlainPart(json, end, inherited);
}

/** How one part is chosen: a condition or a state, and the attribute whose greatest value is the latest. An empty choice is left out. */
export interface Choice {
  /** The condition's text, or its tree as it is stored (kept when it didn't change). */
  condition?: string | Json;
  state?: string;
  latestBy?: string;
}

export function setPartChoice(json: Json, end: string, inherited: Json | undefined, choice: Choice): void {
  const entry = ownPart(json, end, inherited);
  const choose: Json = {};
  if (typeof choice.condition === "string" ? choice.condition.trim() : choice.condition) {
    choose.condition = typeof choice.condition === "string" ? { text: choice.condition.trim() } : choice.condition;
  } else if (choice.state) choose.state = choice.state;
  if (choice.latestBy) choose.latestBy = choice.latestBy;
  if (Object.keys(choose).length) entry.choose = choose;
  else delete entry.choose;
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

/** Removes the entity's own entry for a part, so a base's applies again (or the part is copied whole). */
export function removeOwnPart(json: Json, end: string): void {
  const copy = json.copy as Json | undefined;
  if (!copy || !Array.isArray(copy.parts)) return;
  copy.parts = (copy.parts as Json[]).filter((e) => e.end !== end);
  tidy(json);
}
