// What the verb screens and pickers show (verbs-design.md section 9): the verbs a place may name in order, the labels, the
// suggestions that pre-fill an own verb and how many processes and operations name each verb.
import type { ElementSummary } from "@/api/types";
import { builtInVerbGroups, builtInVerbs, suggestedVerbKeys, type BuiltInVerb, type BuiltInVerbGroup } from "./verbs";

/** An own verb as the vocabulary file holds it. */
export interface OwnVerb {
  id: string;
  key: string;
  displayName?: string;
  description?: string;
}

/** Where a verb is named, which decides the order of the picker's groups. */
export type VerbPlace = "lifecycle-event" | "transition" | "orchestration-event" | "operation" | "right" | "trigger-point";

export interface VerbOption {
  key: string;
  label: string;
  meaning: string;
  /** True for the project's own verbs. */
  own: boolean;
}

export interface VerbGroup {
  id: string;
  label: string;
  options: VerbOption[];
}

export const BUILT_IN_GROUP_LABELS: Record<BuiltInVerbGroup, string> = {
  move: "Moves",
  signature: "Signatures",
  task: "Tasks",
  object: "Objects",
  link: "Links",
};

export const KEY_PATTERN = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/;

/** The label of an own verb without a display name: the key with its first letter upper case and hyphens as spaces. */
export function labelOfKey(key: string): string {
  const spaced = key.replace(/-/g, " ");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

const builtIn = new Map(builtInVerbs.map((v) => [v.key, v]));

export const builtInVerb = (key: string): BuiltInVerb | undefined => builtIn.get(key);

const asOption = (v: BuiltInVerb): VerbOption => ({ key: v.key, label: v.displayName, meaning: v.meaning, own: false });
const ownOption = (v: OwnVerb): VerbOption => ({ key: v.key, label: v.displayName || labelOfKey(v.key), meaning: v.description ?? "", own: true });

/** The label and meaning of a key, built-in or own; null when neither declares it. */
export function describeVerb(key: string, own: readonly OwnVerb[]): { label: string; meaning: string; own: boolean } | null {
  const b = builtIn.get(key);
  if (b) return { label: b.displayName, meaning: b.meaning, own: false };
  const o = own.find((v) => v.key === key);
  return o ? { label: o.displayName || labelOfKey(o.key), meaning: o.description ?? "", own: true } : null;
}

const GROUP_ORDER: Record<VerbPlace, (BuiltInVerbGroup | "own" | "rest")[]> = {
  "lifecycle-event": ["move", "own", "rest"],
  transition: ["move", "own", "rest"],
  "orchestration-event": ["own", "task", "rest"],
  operation: ["object", "link", "own", "rest"],
  // A right speaks of the object, its links and the project's own verbs first (rights-design.md section 10).
  right: ["object", "link", "own", "rest"],
  // A trigger point speaks of the same verbs a right does.
  "trigger-point": ["object", "link", "own", "rest"],
};

/**
 * The groups a picker lists for a place, most likely first (decisions 11 to 14): a lifecycle's event or a transition: Moves, your
 * verbs, the rest; an orchestration's event: your verbs, Tasks, the rest; an operation or a right: Objects and Links, your verbs, the rest.
 * Hidden built-in verbs are left out, except `current`, so a verb a file still names stays visible beside its warning.
 */
export function verbGroups(place: VerbPlace, own: readonly OwnVerb[], hidden: ReadonlySet<string>, current?: string): VerbGroup[] {
  const shown = builtInVerbs.filter((v) => !hidden.has(v.key) || v.key === current);
  const named = new Set<string>();
  const out: VerbGroup[] = [];
  const take = (id: string, label: string, options: VerbOption[]) => {
    const fresh = options.filter((o) => !named.has(o.key));
    for (const o of fresh) named.add(o.key);
    if (fresh.length) out.push({ id, label, options: fresh });
  };
  for (const step of GROUP_ORDER[place]) {
    if (step === "own") take("own", "Your verbs", own.map(ownOption));
    else if (step === "rest") for (const g of builtInVerbGroups) take(g, BUILT_IN_GROUP_LABELS[g], shown.filter((v) => v.group === g).map(asOption));
    else take(step, BUILT_IN_GROUP_LABELS[step], shown.filter((v) => v.group === step).map(asOption));
  }
  return out;
}

/** The suggestions "Add a verb…" offers: verbs left to projects that the project does not yet have, as pre-filled own verbs. */
export function verbSuggestions(own: readonly OwnVerb[]): { key: string; displayName: string }[] {
  const have = new Set(own.map((v) => v.key));
  return suggestedVerbKeys.filter((k) => !have.has(k) && !builtIn.has(k)).map((key) => ({ key, displayName: labelOfKey(key) }));
}

/** The first key of a suggestion's family that no verb takes yet (`verb`, `verb-2`, ...). */
export function freeKey(base: string, taken: ReadonlySet<string>): string {
  let key = base;
  for (let n = 2; taken.has(key) || builtIn.has(key); n++) key = `${base}-${n}`;
  return key;
}

/** How many processes and operations name each verb key, from the index rows (a process row lists its distinct verbs). */
export function verbUsage(rows: readonly ElementSummary[]): Map<string, VerbUsage> {
  const out = new Map<string, VerbUsage>();
  const entry = (key: string) => {
    let e = out.get(key);
    if (!e) out.set(key, (e = { processes: 0, operations: 0, rights: 0, triggerPoints: 0 }));
    return e;
  };
  for (const r of rows) {
    if (r.kind === "process") for (const key of r.verbs ?? []) entry(key).processes++;
    // An approval answers with reject, ignore and withdraw (signoff-design.md decision 9), so it uses them without naming them.
    if (r.kind === "process" && (r.approvals ?? 0) > 0)
      for (const key of APPROVAL_VERBS) entry(key).approvals = (entry(key).approvals ?? 0) + (r.approvals ?? 0);
    else if (r.kind === "operation" && r.verb) entry(r.verb).operations++;
    // A named right's verbs (a lifecycle's own rights are in its row's verbs, counted with the process).
    else if (r.kind === "right") for (const key of r.verbs ?? []) entry(key).rights = (entry(key).rights ?? 0) + 1;
    // A named trigger point's verbs (a lifecycle's own points are in its row's verbs, counted with the process).
    else if (r.kind === "trigger-point") for (const key of r.verbs ?? []) entry(key).triggerPoints = (entry(key).triggerPoints ?? 0) + 1;
  }
  return out;
}

/** How many places name a verb key. */
export interface VerbUsage {
  processes: number;
  operations: number;
  rights?: number;
  /** Named trigger points; listed first wherever trigger points naming a verb are listed. */
  triggerPoints?: number;
  /** Approvals, for the verbs an approval is answered with (`reject`, `ignore`, `withdraw`). */
  approvals?: number;
}

/** The verbs an approval is answered with, beside `sign`. */
const APPROVAL_VERBS = ["reject", "ignore", "withdraw"] as const;

export const isUsed = (u: VerbUsage | undefined): boolean =>
  !!u && (u.processes > 0 || u.operations > 0 || (u.rights ?? 0) > 0 || (u.triggerPoints ?? 0) > 0 || (u.approvals ?? 0) > 0);

/** The elements that name a verb key, from the index rows: named trigger points first (in index order), then the rest in index order. */
export function verbUsers(rows: readonly ElementSummary[], key: string): ElementSummary[] {
  const users = rows.filter(
    (r) =>
      ((r.kind === "process" || r.kind === "right" || r.kind === "trigger-point") && (r.verbs ?? []).includes(key)) ||
      (r.kind === "process" && (r.approvals ?? 0) > 0 && (APPROVAL_VERBS as readonly string[]).includes(key)) ||
      (r.kind === "operation" && r.verb === key),
  );
  return [...users.filter((r) => r.kind === "trigger-point"), ...users.filter((r) => r.kind !== "trigger-point")];
}

/**
 * What rename-verb or remove-verb of the own verb `key` writes, as the engine plans it: the vocabulary, and the elements that
 * name the key unless it is a built-in key (an own verb that shadows a built-in key has no uses of its own: they name the
 * built-in verb, so the operation leaves them). The batch call has no dry run to ask, so this answers it from the index; the
 * operation's `expectedHashes` come from these files.
 */
export function verbOperationFiles(rows: readonly ElementSummary[], key: string): { vocabulary: string | null; users: ElementSummary[] } {
  const builtIn = builtInVerbs.some((v) => v.key === key);
  return { vocabulary: rows.find((r) => r.kind === "verb-vocabulary")?.id ?? null, users: builtIn ? [] : verbUsers(rows, key) };
}

export function usageText(u: VerbUsage | undefined): string {
  if (!isUsed(u) || !u) return "unused";
  const parts = [];
  if (u.triggerPoints) parts.push(`${u.triggerPoints} named trigger ${u.triggerPoints === 1 ? "point" : "points"}`);
  if (u.processes) parts.push(`${u.processes} ${u.processes === 1 ? "process" : "processes"}`);
  if (u.operations) parts.push(`${u.operations} ${u.operations === 1 ? "operation" : "operations"}`);
  if (u.rights) parts.push(`${u.rights} named ${u.rights === 1 ? "right" : "rights"}`);
  if (u.approvals) parts.push(`${u.approvals} ${u.approvals === 1 ? "approval" : "approvals"}`);
  return parts.join(", ");
}
