// The "When revised" part of the entity editor's Revisions tab (revisions-design.md section 12, decisions 32 to 36): what a revise does to
// each lifecycle that governs the entity, and the Attributes and Links tables. The entity's own block is what is written; an entry a base
// wrote shows "from <Base>" and is replaced by writing one of the entity's own. Pure, so the tab is tested without a screen.
import type { MergedRevisions, RevisionsBlock } from "@/model/revisionSequence";

type Rec = Record<string, unknown>;

export type AttributeRule = "copy" | "reset" | "clear";
export type LinkRule = "stay" | "move" | "duplicate";

export const ATTRIBUTE_RULE_LABELS: Record<AttributeRule, string> = { copy: "Copied", reset: "Reset", clear: "Cleared" };
export const LINK_RULE_LABELS: Record<LinkRule, string> = { stay: "Stay", move: "Move", duplicate: "Duplicated" };
export const LIFECYCLE_RULE_LABELS = { restart: "Start over", keep: "Keep" } as const;

/** One row of an entries table: what the rule is and which entity wrote it. */
export interface EntryRow<R extends string> {
  /** The attribute, relation end or lifecycle id. */
  id: string;
  rule: R;
  /** The entity that wrote the entry; null for a lifecycle nobody names (the fallback). */
  from: string | null;
}

/** The lifecycle rows: every governing lifecycle, with the rule the merged block gives it. */
export function lifecycleRows(merged: MergedRevisions): EntryRow<string>[] {
  return merged.lifecycles.map((l) => ({ id: l.lifecycle, rule: l.onRevise, from: l.from }));
}

/** The attribute rows: the merged entries, plus the entity's own `copy` entries (which undo a base's, and so show as Copied). */
export function attributeRows(merged: MergedRevisions, own: RevisionsBlock | undefined, entityId: string): EntryRow<AttributeRule>[] {
  const rows: EntryRow<AttributeRule>[] = merged.attributes.map((a) => ({ id: a.attribute, rule: a.onRevise, from: a.from }));
  for (const e of own?.attributes ?? [])
    if (e.onRevise === "copy" && !rows.some((r) => r.id === e.attribute)) rows.push({ id: e.attribute, rule: "copy", from: entityId });
  return rows;
}

export function linkRows(merged: MergedRevisions): EntryRow<LinkRule>[] {
  return merged.links.map((l) => ({ id: l.end, rule: l.onRevise, from: l.from }));
}

function entries(json: Rec, key: "lifecycles" | "attributes" | "links"): Rec[] {
  const block = (typeof json.revisions === "object" && json.revisions !== null ? json.revisions : (json.revisions = {})) as Rec;
  if (!Array.isArray(block[key])) block[key] = [];
  return block[key] as Rec[];
}

function prune(json: Rec, key: "lifecycles" | "attributes" | "links"): void {
  const block = json.revisions as Rec | undefined;
  if (!block) return;
  if (Array.isArray(block[key]) && (block[key] as unknown[]).length === 0) delete block[key];
  if (Object.keys(block).length === 0) delete json.revisions;
}

const FIELD = { lifecycles: "lifecycle", attributes: "attribute", links: "end" } as const;

/** Writes the entity's own entry for an item, replacing the one it has (one entry per lifecycle, attribute or end). */
export function setOwnEntry(json: Rec, key: "lifecycles" | "attributes" | "links", id: string, onRevise: string): void {
  const list = entries(json, key);
  const field = FIELD[key];
  const at = list.findIndex((e) => e[field] === id);
  const entry = { [field]: id, onRevise };
  if (at >= 0) list[at] = entry;
  else list.push(entry);
}

/** Removes the entity's own entry for an item, so a base's applies again (or the fallback). */
export function removeOwnEntry(json: Rec, key: "lifecycles" | "attributes" | "links", id: string): void {
  const block = json.revisions as Rec | undefined;
  if (!block || !Array.isArray(block[key])) return;
  block[key] = (block[key] as Rec[]).filter((e) => e[FIELD[key]] !== id);
  prune(json, key);
}

/** Whether the entity itself has an entry for the item. */
export const hasOwnEntry = (own: RevisionsBlock | undefined, key: "lifecycles" | "attributes" | "links", id: string): boolean =>
  ((own?.[key] ?? []) as unknown as Rec[]).some((e) => e[FIELD[key]] === id);

/** The id of the entry's choice in the lifecycle select: `restart`, `keep`, or the state's id. */
export const lifecycleChoice = (rule: string): string => rule;

/** A lifecycle choice as the select shows it, given the state labels by id. */
export function lifecycleRuleText(rule: string, stateLabels: ReadonlyMap<string, string>): string {
  if (rule === "restart") return LIFECYCLE_RULE_LABELS.restart;
  if (rule === "keep") return LIFECYCLE_RULE_LABELS.keep;
  return `Go to ${stateLabels.get(rule) ?? "a state"}`;
}

export interface AttributeChoice {
  id: string;
  name: string;
  derived?: unknown;
}

/**
 * The attributes the Attributes table may add (MQ8312): ones the type holds that are not derived, not a key attribute, not a lifecycle's
 * bound state attribute, not the one that holds the revision, and not listed already.
 */
export function attributeChoices(
  attributes: readonly AttributeChoice[],
  exclude: { key: readonly string[]; lifecycleBound: ReadonlySet<string>; held: string | null; listed: readonly string[] },
): AttributeChoice[] {
  return attributes.filter(
    (a) => !a.derived && !exclude.key.includes(a.id) && !exclude.lifecycleBound.has(a.id) && a.id !== exclude.held && !exclude.listed.includes(a.id),
  );
}

export interface EndChoice {
  id: string;
  label: string;
}

/**
 * The relation ends the type reaches (MQ8312): for a relation of two ends, the end whose other end covers the entity or a base of it.
 * `relations` gives each relation's name, its document's ends and the index row's ends in the same order.
 */
export function reachedEnds(relations: readonly { name: string; ends: { id: string; role: string }[]; covers: readonly boolean[] }[]): EndChoice[] {
  return relations.flatMap((r) => (r.ends.length !== 2 ? [] : r.ends.flatMap((e, i) => (r.covers[1 - i] ? [{ id: e.id, label: `${r.name}.${e.role}` }] : []))));
}
