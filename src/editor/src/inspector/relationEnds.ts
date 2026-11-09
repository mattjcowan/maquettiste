// What the relation form offers for an end's Type (several-types-design.md section 11): One entity, Several types or Any
// entity, the limits on each, and the edits that move an end between them. Pure, so the form's choices and limits are
// unit tested without React. An end writes exactly one of `entity`, `types` or `anyEntity` (decision 1).
import { docEndHasSeveralTypes, docEndSingleEntity, type EndDocLike } from "@/model/endTypes";

export type EndChoice = "one" | "several" | "any";

export const END_CHOICE_LABELS: Record<EndChoice, string> = { one: "One entity", several: "Several types", any: "Any entity" };

/** The words of the limits (section 11). */
export const END_LIMIT_TEXTS = {
  oneEnd: "Only one end may have several types.",
  nary: "An n-ary relation's ends name one entity each.",
  child: "The part end of a composition can't be any entity; list the types it may be.",
  lastType: "An end needs at least one type. Choose One entity or Any entity instead.",
} as const;

export interface EndEntry {
  entity?: string | undefined;
  stereotype?: string | undefined;
}

/** A relation end as the form edits it. */
export interface EndForm extends EndDocLike {
  id?: string;
  max?: 1 | "*" | undefined;
  types?: EndEntry[] | undefined;
}

/** The relation as far as the limits need it. */
export interface RelationForm {
  relationKind?: string | undefined;
  ends: readonly EndForm[];
}

/** What the file says the end is: `anyEntity`, a `types` list (even one holding a single entity, which MQ3042 warns about), else One entity. */
export function endChoiceOf(end: EndForm): EndChoice {
  if (end.anyEntity === true) return "any";
  if (end.types !== undefined && !end.entity) return "several";
  return "one";
}

/** The index of the composition's child end (the end opposite the one with max 1), as the engine reads it; null for any other relation. */
export function compositionChildIndex(relation: RelationForm): number | null {
  if (relation.relationKind !== "composition" || relation.ends.length !== 2) return null;
  if (relation.ends[0].max === 1) return 1;
  if (relation.ends[1].max === 1) return 0;
  return null;
}

/** For each choice that is not offered, why (the tooltip); a choice that is offered is absent. The end's own choice is always offered. */
export function endChoiceLimits(relation: RelationForm, index: number): Partial<Record<EndChoice, string>> {
  const end = relation.ends[index];
  const own = end ? endChoiceOf(end) : "one";
  const out: Partial<Record<EndChoice, string>> = {};
  const nary = relation.relationKind === "n-ary" || relation.ends.length > 2;
  const otherHasSeveral = relation.ends.some((e, i) => i !== index && docEndHasSeveralTypes(e));
  if (nary) {
    out.several = END_LIMIT_TEXTS.nary;
    out.any = END_LIMIT_TEXTS.nary;
  } else if (otherHasSeveral) {
    out.several = END_LIMIT_TEXTS.oneEnd;
    out.any = END_LIMIT_TEXTS.oneEnd;
  } else if (compositionChildIndex(relation) === index) out.any = END_LIMIT_TEXTS.child;
  delete out[own];
  return out;
}

/**
 * Moves the end to a choice, in place, when that needs no pick; returns false when the person has to pick first (the end stays as it
 * was). To One entity: an end listing exactly one entity entry writes `entity`; anything else needs a pick. To Several types: an end
 * with one entity or any entity waits for a picked entry, which makes the list (with the entity first). To Any entity: the other keys go.
 */
export function moveEnd(end: EndForm, to: EndChoice): boolean {
  if (to === "any") {
    delete end.entity;
    delete end.types;
    end.anyEntity = true;
    return true;
  }
  if (to === "one") {
    if (end.entity) return true;
    const only = docEndSingleEntity(end);
    if (only === null) return false;
    end.entity = only;
    delete end.types;
    return true;
  }
  // Several types. An end with one entity waits for a second entry: a list of one entity is a warning (MQ3042) the person didn't cause,
  // and the editor always writes `entity` for one entity (decision 2). `addEntry` starts the list from the entity and the new entry.
  if (end.entity) return false;
  return end.types !== undefined && end.anyEntity !== true;
}

/** Writes the one entity the person picked: `entity` replaces `types` and `anyEntity`. */
export function pickOneEntity(end: EndForm, entity: string): void {
  delete end.types;
  delete end.anyEntity;
  end.entity = entity;
}

/** Adds an entry to the end's list (after any others, in file order); from One entity or Any entity it starts the list. Repeats are ignored. */
export function addEntry(end: EndForm, entry: EndEntry): void {
  const existing = end.types ?? (end.entity ? [{ entity: end.entity }] : []);
  if (existing.some((e) => e.entity === entry.entity && e.stereotype === entry.stereotype)) return;
  delete end.entity;
  delete end.anyEntity;
  end.types = [...existing, entry];
}

/** Removes the entry at a position; the last entry stays (a list holds at least one). Returns whether anything was removed. */
export function removeEntry(end: EndForm, position: number): boolean {
  const entries = end.types ?? [];
  if (entries.length <= 1 || position < 0 || position >= entries.length) return false;
  end.types = entries.filter((_, i) => i !== position);
  return true;
}

/** An entry's identity, for keys and for "already listed". */
export const entryKey = (e: EndEntry): string => (e.entity !== undefined ? `entity:${e.entity}` : `stereotype:${e.stereotype ?? ""}`);
