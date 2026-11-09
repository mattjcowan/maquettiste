// The Problems panel's quick fixes (phase-3-design.md 3, "Quick fixes" and its derivation table): what a diagnostic's
// fix is, read from its rule, its element and its pointer. The named operations (`set-initial`, `sync-enum`,
// `refresh-scenario`, `set-lifecycle`, `text-to-trees`) are batch operations of the engine; the others are plain updates of the
// diagnostic's element (or, for MQ9205, of the subject entity) built here. Pure: the panel loads the documents
// `fixDocuments` names and applies the result as one batch that undo reverts.
import type { Diagnostic, ModelJson } from "@/api/types";
import { atPointer, clone } from "@/lib/json";
import { classifyAttribute } from "@/model/binding";
import { newId } from "@/lib/ids";
import { fixEscalationPair, reorderStages } from "@/model/approvals";
import { subjectsOf, uniqueName, type GateDoc, type NamedSignatureDoc, type SubjectDoc } from "@/model/process";
import { builtInVerbs } from "@/model/verbs";
import type { VerbFix } from "@/verbs/verbFixes";
import { relationOfMapping, STORAGE_FIX_RULES, storageExtras, storageFix, type StorageFix } from "./storageFix";

type Json = Record<string, unknown>;

export type QuickFixOperation = "set-initial" | "set-lifecycle" | "refresh-scenario" | "text-to-trees" | "use-subjects";

export type QuickFix =
  /** A named batch operation; `ids` are every document it may change (the undo step's before and after). */
  | { kind: "operation"; op: QuickFixOperation; id: string; target?: string; label: string; ids: string[]; choices?: { id: string; name: string }[] }
  /** Sync enum: a dry run first, then the apply (the process's own endpoint). */
  | { kind: "sync-enum"; process: string; label: string; entity?: string; stereotype?: string }
  /** Sync codes (MQ9210): the process and the binding the finding sits on (a dry run first, then the apply). */
  | { kind: "sync-codes"; process: string; entity?: string; stereotype?: string; attribute: string; label: string }
  /** A fix that reaches the project's verb vocabulary (verbFixes.ts). */
  | VerbFix
  /** A storage finding of a relation with an end of several types (storageFix.ts). */
  | StorageFix
  /** A plain update of one document. */
  | { kind: "update"; id: string; json: ModelJson; label: string }
  /** Opens a part of a lifecycle on its tab (no change to the model). */
  | { kind: "open"; process: string; tab: "events"; node: string; label: string };

/** The rules whose fix is built in the editor as a plain update (the derivation table's last row) or needs two findings of MQ9201. */
const EDITOR_FIXES = new Set([
  "MQ9013",
  "MQ9016",
  "MQ9102",
  "MQ9105",
  "MQ9205",
  "MQ9201",
  "MQ9209",
  "MQ9601",
  "MQ9605",
  "MQ9606",
  "MQ9701",
  "MQ9702",
  "MQ9703",
  "MQ9704",
  "MQ9705",
  "MQ9706",
  "MQ9801",
  "MQ9802",
  "MQ9803",
  "MQ9901",
  "MQ9902",
  "MQ9903",
  "MQ9904",
  "MQ9905",
  "MQ9906",
  "MQ8101",
  "MQ8102",
  "MQ8103",
  "MQ8105",
  "MQ8106",
  "MQ8107",
  "MQ8201",
  "MQ8202",
  "MQ8203",
  "MQ8204",
  "MQ3116",
  "MQ3042",
  "MQ3043",
  ...STORAGE_FIX_RULES,
]);

/** True when the diagnostic's rule has a quick fix: a catalog `quickFix` or one the editor builds. */
export function hasQuickFix(rule: string, catalogFix: string | null | undefined): boolean {
  if (rule === "MQ9206") return false; // a code map the codes themselves break; no sync-codes fix (decision 19)
  return !!catalogFix || EDITOR_FIXES.has(rule);
}

/**
 * True when the fix of a finding in an unsaved draft can still be offered: it edits the project's verbs, not the draft
 * (MQ9701, "add it to the project's verbs") or rewrites the draft itself (MQ8201, MQ8203 and MQ8204, which the server would refuse to save as it
 * is). Every other fix reads the saved document and waits for the draft to save.
 */
export function hasDraftQuickFix(rule: string): boolean {
  return rule === "MQ9701" || rule === "MQ8201" || rule === "MQ8203" || rule === "MQ8204";
}

const str = (value: unknown): string | undefined => (typeof value === "string" && value ? value : undefined);
const parent = (pointer: string) => pointer.slice(0, pointer.lastIndexOf("/"));
const last = (pointer: string) => pointer.slice(pointer.lastIndexOf("/") + 1);

/** The subject entry a finding of a process is about: the `/subjects/<n>` its pointer names, else the first (the one-subject form has one). */
function entryOf(element: Json, pointer: string): SubjectDoc | undefined {
  const all = subjectsOf(element);
  const at = /^\/subjects\/(\d+)(\/|$)/.exec(pointer);
  return at ? all[Number(at[1])] : all[0];
}

/**
 * The documents a fix reads: the diagnostic's element and, for MQ9201 and MQ9205, the partner on the other side; for
 * MQ9205 also the bound attribute's type, once `have` can read the entity.
 */
export function fixDocuments(d: Diagnostic, element: ModelJson | undefined, have?: (id: string) => ModelJson | undefined): string[] {
  if (!d.elementId) return [];
  const mappingRelation = relationOfMapping(d, element);
  if (mappingRelation) return [d.elementId, mappingRelation];
  const related =
    d.rule === "MQ9205" || (d.rule === "MQ9201" && (d.jsonPointer === "/subject" || d.jsonPointer?.startsWith("/subjects")))
      ? element
        ? entryOf(element, d.jsonPointer ?? "")?.entity
        : undefined
      : d.rule === "MQ9201" && d.jsonPointer === "/lifecycle"
        ? str(element?.lifecycle)
        : undefined;
  if (!related) return [d.elementId];
  const typeId = d.rule === "MQ9205" && element ? typeOfBound(element, d.jsonPointer ?? "", have) : undefined;
  return typeId ? [d.elementId, related, typeId] : [d.elementId, related];
}

/** The id of the type of the bound attribute a finding is about, when it is an own attribute of an entity `have` reads. */
function typeOfBound(process: ModelJson, pointer: string, have?: (id: string) => ModelJson | undefined): string | undefined {
  const entry = entryOf(process, pointer);
  const entity = entry?.entity ? have?.(entry.entity) : undefined;
  const attribute = ((entity?.attributes as Json[] | undefined) ?? []).find((a) => a.id === entry?.boundAttribute);
  const type = attribute?.type as { ref?: string } | string | undefined;
  return typeof type === "object" && typeof type?.ref === "string" ? type.ref : undefined;
}

/**
 * The code of the configuration a lifecycle starts in, when it has one: the initial root state, or, while that is
 * compound with no code, its initial child. History and choice states have none.
 */
export function initialCode(process: Json): string | number | undefined {
  let node = initialState(process);
  while (node && node.type !== "history" && node.type !== "choice") {
    const code = node.code;
    if (typeof code === "string" || typeof code === "number") return code;
    const children = (node.states as Json[] | undefined) ?? [];
    if (node.type !== "compound" && !(node.type === undefined && children.length)) return undefined;
    const from: Json = node;
    node = children.find((s) => s.id === from.initial) ?? children[0];
  }
  return undefined;
}

/**
 * The quick fixes of MQ3042 and MQ3043 (several-types-design.md section 6). MQ3042 (an end's `types` lists one entity) writes `entity`
 * in its place; the finding's pointer names the end (`/ends/<i>/types`). MQ3043 (an entry another entry covers) removes the entry its
 * pointer names (`/ends/<i>/types/<j>`); the list's last entry stays, since a list holds at least one.
 */
export function endTypesFix(rule: string, element: ModelJson, pointer: string): { json: ModelJson; label: string } | null {
  const at = /^\/ends\/(\d+)(?:\/types(?:\/(\d+))?)?(?:\/(?:entity|stereotype))?$/.exec(pointer);
  if (!at) return null;
  const next = clone(element);
  const end = (next.ends as Json[] | undefined)?.[Number(at[1])];
  const types = end?.types;
  if (!end || !Array.isArray(types)) return null;
  if (rule === "MQ3042") {
    const only = types.length === 1 ? str((types[0] as Json).entity) : undefined;
    if (!only) return null;
    delete end.types;
    end.entity = only;
    return { json: next, label: "Use entity" };
  }
  const entry = at[2] === undefined ? undefined : types[Number(at[2])];
  if (!entry || types.length < 2) return null;
  // An entity entry holds an id, which the fix has no name for; a stereotype entry holds its key.
  const key = str((entry as Json).stereotype);
  types.splice(Number(at[2]), 1);
  return { json: next, label: key ? `Remove ${key} (stereotype)` : "Remove the entry" };
}

/** Removes the array item (or the property) at `pointer`; null when nothing is there. */
function removeAt(json: ModelJson, pointer: string): ModelJson | null {
  const next = clone(json);
  const container = atPointer(next, parent(pointer));
  const key = last(pointer);
  if (Array.isArray(container)) {
    const index = Number(key);
    if (!Number.isInteger(index) || index < 0 || index >= container.length) return null;
    container.splice(index, 1);
    return next;
  }
  if (container && typeof container === "object" && key in container) {
    delete (container as Json)[key];
    return next;
  }
  return null;
}

/** Removes `pointer`, then the list or key it leaves empty (`rights`, or the named list), which would otherwise be written as []. */
function removeAndTidy(json: ModelJson, pointer: string, tidy: "rights" | "list"): ModelJson | null {
  const next = removeAt(json, pointer);
  if (!next) return null;
  const holder = atPointer(next, parent(pointer));
  if (Array.isArray(holder) && holder.length === 0) {
    const key = last(parent(pointer));
    const owner = atPointer(next, parent(parent(pointer))) ?? next;
    if (tidy === "rights" || key === "except" || key === "ends") delete (owner as Json)[key];
  }
  return next;
}

/** The quick fixes of MQ9901 to MQ9906 (rights-design.md section 10). Every one is a plain update of the lifecycle, or of the named right. */
function rightFix(rule: string, element: ModelJson, pointer: string): { json: ModelJson; label: string } | null {
  const entry = /^(\/rights\/\d+)(\/|$)/.exec(pointer)?.[1];
  const done = (json: ModelJson | null, label: string) => (json ? { json, label } : null);
  switch (rule) {
    case "MQ9901":
      if (pointer === "/rights" || pointer === "/rightsDefault") return done(removeAt(element, pointer), "Remove the entry");
      return entry ? done(removeAndTidy(element, entry, "rights"), "Remove the entry") : null;
    case "MQ9902": {
      // The last state a right names goes with the right: an empty list would widen it to every state.
      const list = entry ? (atPointer(element, `${entry}/states`) as unknown[] | undefined) : undefined;
      if (!entry || !/\/states\/\d+$/.test(pointer)) return null;
      return list && list.length === 1
        ? done(removeAndTidy(element, entry, "rights"), "Remove the entry")
        : done(removeAt(element, pointer), "Remove the state");
    }
    case "MQ9903":
      if (!entry) return null;
      if (pointer === entry) return done(removeAndTidy(element, entry, "rights"), "Remove the entry");
      return /\/(ends|attributes)(\/\d+)?$/.test(pointer) ? done(removeAndTidy(element, pointer, "list"), "Remove the entry") : null;
    case "MQ9904":
      return entry ? done(removeAndTidy(element, entry, "rights"), "Remove the right") : null;
    case "MQ9906":
      return /\/except\/\d+$/.test(pointer) ? done(removeAndTidy(element, pointer, "list"), "Remove it from Except") : null;
    case "MQ9905": {
      // Add an allow right for the verb the event names, or `complete` for a human task.
      const part = atPointer(element, pointer) as Json | undefined;
      const verb = /^\/events\/\d+$/.test(pointer) ? str(part?.verb) : /\/invoke\/\d+$/.test(pointer) ? "complete" : undefined;
      if (!verb || element.kind !== "process") return null;
      const next = clone(element);
      const rights = (Array.isArray(next.rights) ? next.rights : (next.rights = [])) as Json[];
      const name = uniqueName(
        rights.flatMap((r) => (typeof r.name === "string" ? [r.name] : [])),
        `Allow${verb.replace(/(^|-)([a-z])/g, (_, __, c: string) => c.toUpperCase())}`,
      );
      rights.push({ id: newId(), name, access: "allow", verbs: [verb] });
      return { json: next, label: `Add an allow right for ${verb}` };
    }
    default:
      return null;
  }
}

/**
 * The quick fixes of MQ8201 to MQ8204 (signoff-design.md sections 6 and 11): MQ8204 adds or removes the missing half of the escalation
 * pair; MQ8201 and MQ8202 remove the key or the entry (a `notBy`
 * left empty goes with its key), MQ8203 puts the approval's stages in round order. Every one is a plain update of the process.
 */
function approvalFix(rule: string, element: ModelJson, pointer: string): { json: ModelJson; label: string } | null {
  if (rule === "MQ8203") {
    const gatePointer = /^(.*?)\/stages(\/|$)/.exec(pointer)?.[1];
    if (gatePointer === undefined) return null;
    const next = clone(element);
    const gate = atPointer(next, gatePointer) as GateDoc | undefined;
    return gate && reorderStages(gate) ? { json: next, label: "Reorder the stages" } : null;
  }
  if (rule === "MQ8204") {
    // The escalation pair: add the missing half (or take the lone key away); a delay that is no duration just goes.
    const sigPointer = /^(.*\/signatures\/\d+)(\/|$)/.exec(pointer)?.[1];
    if (sigPointer === undefined) return null;
    const next = clone(element);
    const sig = atPointer(next, sigPointer) as NamedSignatureDoc | undefined;
    if (!sig) return null;
    if (fixEscalationPair(sig)) return { json: next, label: sig.escalateAfter && sig.escalateTo?.length ? "Add the missing delay" : "Remove the key" };
    const json = removeAt(element, pointer);
    return json && pointer !== sigPointer ? { json, label: "Remove the key" } : null;
  }
  const entry = /^(.*\/notBy)\/\d+$/.exec(pointer);
  const json = removeAt(element, pointer);
  if (!json) return null;
  if (entry) {
    const list = atPointer(json, entry[1]);
    if (Array.isArray(list) && list.length === 0) delete (atPointer(json, parent(entry[1])) as Json)[last(entry[1])];
  }
  return { json, label: entry ? "Remove the entry" : "Remove the key" };
}

/**
 * The quick fixes of MQ8101 to MQ8107 (trigger-points-design.md sections 6 and 11): MQ8101 and MQ8102 remove the key or the entry,
 * MQ8103 makes the point an action, MQ8105 and MQ8106 change its timing, MQ8107 removes it. Every one is a plain update of the
 * lifecycle. The last entry of a narrowing list takes the trigger point with it: an empty list would widen it to everywhere.
 */
function triggerPointFix(rule: string, element: ModelJson, pointer: string): { json: ModelJson; label: string } | null {
  if (element.kind === "trigger-point") return namedTriggerPointFix(rule, element, pointer);
  const entry = /^(\/triggerPoints\/\d+)(\/|$)/.exec(pointer)?.[1];
  const done = (json: ModelJson | null, label: string) => (json ? { json, label } : null);
  const point = entry ? (atPointer(element, entry) as Json | undefined) : undefined;
  const timing = (value: "with-change" | "after-commit", label: string) => {
    if (!entry || !point) return null;
    const next = clone(element);
    (atPointer(next, entry) as Json).timing = value;
    return { json: next, label };
  };
  switch (rule) {
    case "MQ8101":
    case "MQ8102": {
      // The whole list on an orchestration or a library.
      if (pointer === "/triggerPoints") return done(removeAt(element, pointer), "Remove the key");
      if (!entry) return null;
      if (pointer === entry) return done(removeAndTidy(element, entry, "rights"), "Remove the entry");
      const item = /^(\/triggerPoints\/\d+\/\w+)\/\d+$/.exec(pointer)?.[1];
      if (item) {
        const list = atPointer(element, item) as unknown[] | undefined;
        if (list && list.length === 1) return done(removeAndTidy(element, entry, "rights"), "Remove the entry");
        return done(removeAt(element, pointer), "Remove the entry");
      }
      // A key: its list would be written as [] when it is the last, so a list key is removed whole.
      return done(removeAt(element, pointer), rule === "MQ8101" ? "Remove the key" : "Remove the entry");
    }
    case "MQ8103":
      if (!entry || !point) return null;
      {
        const next = clone(element);
        const holder = atPointer(next, entry) as Json;
        holder.type = "action";
        holder.timing = "with-change";
        return { json: next, label: "Make it an action" };
      }
    case "MQ8105":
      return timing("with-change", "Make it a with-change action");
    case "MQ8106":
      return timing("after-commit", "Make it an after-commit action");
    case "MQ8107":
      return entry ? done(removeAndTidy(element, entry, "rights"), "Remove it") : null;
    default:
      return null;
  }
}

/**
 * The same fixes on a named trigger point, whose pointers start at the document (`/exceptEntities/0`, `/timing`). It has no entry to
 * drop: the last item of an `exceptEntities` or `entities` list takes the key (an empty list would widen it), the last of `subjects` has
 * no fix, and MQ8107 (remove the point) is the delete the editor and the explorer already have, so it is no update here.
 */
function namedTriggerPointFix(rule: string, element: ModelJson, pointer: string): { json: ModelJson; label: string } | null {
  const done = (json: ModelJson | null, label: string) => (json ? { json, label } : null);
  const timing = (value: "with-change" | "after-commit", label: string) => {
    const next = clone(element);
    next.timing = value;
    return { json: next, label };
  };
  switch (rule) {
    case "MQ8101":
    case "MQ8102": {
      if (pointer === "") return null;
      const item = /^(\/\w+)\/\d+$/.exec(pointer)?.[1];
      if (item) {
        const list = atPointer(element, item) as unknown[] | undefined;
        if (!list || (list.length === 1 && item === "/subjects")) return null;
        return list.length === 1 ? done(removeAt(element, item), "Remove the entry") : done(removeAt(element, pointer), "Remove the entry");
      }
      return done(removeAt(element, pointer), rule === "MQ8101" ? "Remove the key" : "Remove the entry");
    }
    case "MQ8103": {
      const next = clone(element);
      next.type = "action";
      next.timing = "with-change";
      return { json: next, label: "Make it an action" };
    }
    case "MQ8105":
      return timing("with-change", "Make it a with-change action");
    case "MQ8106":
      return timing("after-commit", "Make it an after-commit action");
    default:
      return null;
  }
}

/** The pointer of the set a finding sits on or under (`/actions/<n>/sets/<m>`), or null. */
export const setPointerOf = (pointer: string): string | null => /^(\/actions\/\d+\/sets\/\d+)(\/|$)/.exec(pointer)?.[1] ?? null;

/** Removes the set at `pointer`; an action left with no sets loses the key. */
function removeSetAt(json: ModelJson, pointer: string): ModelJson | null {
  const next = removeAt(json, pointer);
  const list = next ? (atPointer(next, parent(pointer)) as unknown[] | undefined) : undefined;
  if (next && Array.isArray(list) && !list.length) delete (atPointer(next, parent(parent(pointer))) as Json)[last(parent(pointer))];
  return next;
}

/**
 * Removes the `in_state` call a finding names (MQ3116): the whole condition or value when it is the root (a set goes with
 * its value), else the argument of an `and` or `or` it sits in, which collapses to its one remaining argument. A call inside
 * another operator has no safe removal: null.
 */
export function removeCall(json: ModelJson, pointer: string): ModelJson | null {
  const next = clone(json);
  const at = pointer.replace(/\/$/, "");
  const node = atPointer(next, at) as Json | undefined;
  if (!node || typeof node !== "object" || !("inState" in node)) return null;
  const set = /^(\/actions\/\d+\/sets\/\d+)\/value$/.exec(at);
  if (set) return removeSetAt(json, set[1]);
  if (/^\/guards\/\d+\/condition$/.test(at)) return removeAt(json, at);
  const m = /^(.*)\/args\/(\d+)$/.exec(at);
  if (!m) return null;
  const owner = atPointer(next, m[1]) as Json | undefined;
  if (!owner || (owner.op !== "and" && owner.op !== "or") || !Array.isArray(owner.args)) return null;
  (owner.args as unknown[]).splice(Number(m[2]), 1);
  if ((owner.args as unknown[]).length === 1) {
    // Replace the operator by its one argument.
    const only = (owner.args as Json[])[0];
    for (const k of Object.keys(owner)) delete owner[k];
    Object.assign(owner, only);
  } else if (!(owner.args as unknown[]).length) return removeAt(json, m[1]);
  return next;
}

/** Adds `value` to the string list at `pointer` (created when absent); null when it is already there. */
function addTo(json: ModelJson, owner: string, list: string, value: string): ModelJson | null {
  const next = clone(json);
  const node = atPointer(next, owner) as Json | undefined;
  if (!node || typeof node !== "object") return null;
  const current = Array.isArray(node[list]) ? (node[list] as string[]) : [];
  if (current.includes(value)) return null;
  node[list] = [...current, value];
  return next;
}

/** The root-level state a process starts in: `initial`, else the first root state. */
function initialState(process: Json): Json | undefined {
  const states = (process.states as Json[] | undefined) ?? [];
  return states.find((s) => s.id === process.initial) ?? states[0];
}

/** The built-in verb a key is, or reads like once its hyphens are dropped (MQ9706). */
const builtInFor = (key: string): string | undefined =>
  builtInVerbs.find((v) => v.key === key)?.key ?? builtInVerbs.find((v) => v.key.replace(/-/g, "") === key.replace(/-/g, ""))?.key;

/** The fixes of MQ9701 to MQ9706 (verbs-design.md section 9). */
function verbFix(rule: string, element: ModelJson, pointer: string, id: string): QuickFix | null {
  const value = str(atPointer(element, pointer));
  switch (rule) {
    case "MQ9701":
      return value ? { kind: "verb", action: "add", key: value, label: "Add it to the project's verbs" } : null;
    case "MQ9702":
    case "MQ9706": {
      const key = value && element.kind === "verb-vocabulary" ? value : undefined;
      const to = key ? builtInFor(key) : undefined;
      return key && to ? { kind: "verb", action: "use-built-in", key, to, label: `Use the built-in ${to}` } : null;
    }
    case "MQ9703": {
      const json = /^\/hidden\/\d+$/.test(pointer) ? removeAt(element, pointer) : null;
      return json ? { kind: "update", id, json, label: "Remove the entry" } : null;
    }
    case "MQ9704":
      return value ? { kind: "verb", action: "show", key: value, label: "Show the verb again" } : null;
    case "MQ9705": {
      // The verb moves to the event when it names none; otherwise it is cleared.
      const t = atPointer(element, parent(pointer)) as Json | undefined;
      const events = (element.events as Json[] | undefined) ?? [];
      const at = events.findIndex((e) => e.id === t?.event);
      if (!t || !value) return null;
      const json = removeAt(element, pointer);
      if (!json) return null;
      if (at < 0 || events[at].verb !== undefined) return { kind: "update", id, json, label: "Clear the verb" };
      (json.events as Json[])[at].verb = value;
      return { kind: "update", id, json, label: "Move the verb to the event" };
    }
  }
  return null;
}

/**
 * The other fixes a finding offers beside its first (rights-design.md section 10): MQ9904 may set the default to deny instead of
 * removing the right, and MQ9905 may open the event instead of adding an allow right.
 */
export function extraQuickFixes(d: Diagnostic, doc: (id: string) => ModelJson | undefined): QuickFix[] {
  const element = d.elementId ? doc(d.elementId) : undefined;
  if (element && d.rule === "MQ4115") return storageExtras(d, element);
  if (!element || !d.elementId || element.kind !== "process") return [];
  const id = typeof element.id === "string" ? element.id : d.elementId;
  if (d.rule === "MQ9904") {
    const json = clone(element);
    json.rightsDefault = "deny";
    return [{ kind: "update", id, json, label: "Set the default to deny" }];
  }
  if (d.rule === "MQ9905" && /^\/events\/\d+$/.test(d.jsonPointer ?? "")) {
    const event = str((atPointer(element, d.jsonPointer ?? "") as Json | undefined)?.id);
    return event ? [{ kind: "open", process: id, tab: "events", node: event, label: "Open the event" }] : [];
  }
  return [];
}

/**
 * The quick fix of a diagnostic, or null when it has none or its arguments cannot be read from the documents.
 * `catalogFix` is the rule's `quickFix` from GET /api/validation/rules; `doc` answers the documents `fixDocuments` named.
 */
export function deriveQuickFix(d: Diagnostic, catalogFix: string | null | undefined, doc: (id: string) => ModelJson | undefined): QuickFix | null {
  const element = d.elementId ? doc(d.elementId) : undefined;
  const pointer = d.jsonPointer ?? "";
  if (!element || !d.elementId) return null;
  // A finding of a `subjects` entry names the entry, not the process: the fetched document is the process.
  const id = typeof element.id === "string" ? element.id : d.elementId;
  // The process comes from the fetched document, never from a finding's element id (a state's or an entry's id).
  if (d.rule === "MQ9206") return null;
  if (d.rule.startsWith("MQ97")) return verbFix(d.rule, element, pointer, id);
  if ((STORAGE_FIX_RULES as readonly string[]).includes(d.rule)) {
    const relation = relationOfMapping(d, element);
    return storageFix(d, element, relation ? doc(relation) : undefined);
  }
  if (catalogFix === "sync-codes") {
    // The finding sits on the binding (`/subjects/<i>/boundAttribute`, or `/boundAttribute`): its entry names the entity and the attribute.
    if (element.kind !== "process") return null;
    const entry = entryOf(element, pointer);
    if (!entry?.boundAttribute) return null;
    // A stereotype entry syncs through the batch, which takes the stereotype's key (stereotype-sets-design.md decision 10).
    if (entry.stereotype)
      return { kind: "sync-codes", process: str(element.id) ?? id, stereotype: entry.stereotype, attribute: entry.boundAttribute, label: "Sync codes" };
    return entry.entity
      ? { kind: "sync-codes", process: str(element.id) ?? id, entity: entry.entity, attribute: entry.boundAttribute, label: "Sync codes" }
      : null;
  }
  // Sync enum for one binding: a subjects entry's finding sends its entity; the one-subject form omits it (decision 15).
  if (catalogFix === "sync-enum" && element.kind === "process" && Array.isArray(element.subjects)) {
    const entry = entryOf(element, pointer);
    return {
      kind: "sync-enum",
      process: str(element.id) ?? id,
      label: "Sync enum",
      ...(entry?.entity ? { entity: entry.entity } : {}),
      ...(entry?.stereotype ? { stereotype: entry.stereotype } : {}),
    };
  }
  if (catalogFix === "sync-enum") return element.kind === "process" ? { kind: "sync-enum", process: id, label: "Sync enum" } : null;
  if (catalogFix === "refresh-scenario")
    return element.kind === "scenario" ? { kind: "operation", op: "refresh-scenario", id, label: "Update expectations from replay", ids: [id] } : null;
  // MQ3108: every expression the element holds as text becomes its tree (next-phase-3-design.md decision 4).
  if (catalogFix === "text-to-trees") return { kind: "operation", op: "text-to-trees", id, label: "Convert expressions to trees", ids: [id] };
  // MQ9209: the one-subject form becomes `subjects`, for this process only (lifecycle-design.md decision 6).
  if (catalogFix === "use-subjects" || d.rule === "MQ9209")
    return element.kind === "process" ? { kind: "operation", op: "use-subjects", id, label: "Convert to subjects", ids: [id] } : null;
  if (catalogFix === "set-initial") {
    if (!pointer.endsWith("/initial")) return null;
    const at = parent(pointer);
    const node = (at === "" ? element : atPointer(element, at)) as Json | undefined;
    if (!node || typeof node !== "object") return null;
    const children = ((node.states as Json[] | undefined) ?? []).filter((s) => typeof s.id === "string");
    const type = str(node.type);
    // An initial on a state that is not compound, or with no child to name: the fix is to remove it.
    if (!children.length || (at !== "" && type && type !== "compound")) {
      const json = removeAt(element, pointer);
      return json ? { kind: "update", id, json, label: "Remove initial" } : null;
    }
    const owner = at === "" ? id : str(node.id);
    if (!owner) return null;
    const choices = children.map((s) => ({ id: String(s.id), name: str(s.name) ?? String(s.id) }));
    return { kind: "operation", op: "set-initial", id: owner, target: choices[0].id, label: "Set initial", ids: [id], choices };
  }
  switch (d.rule) {
    case "MQ9013": {
      const json = removeAt(element, pointer);
      const name = str((atPointer(element, pointer) as Json | undefined)?.name);
      return json ? { kind: "update", id, json, label: name ? `Remove ${name}` : "Remove it" } : null;
    }
    case "MQ9801":
    case "MQ9802":
    case "MQ9803": {
      // A finding of a set names it: the second set of one attribute, or the set whose attribute cannot be set. Remove the set.
      const at = setPointerOf(pointer);
      if (!at || element.kind !== "process") return null;
      const json = removeSetAt(element, at);
      return json ? { kind: "update", id, json, label: "Remove the set" } : null;
    }
    case "MQ3042":
    case "MQ3043": {
      if (element.kind !== "relation") return null;
      const fix = endTypesFix(d.rule, element, pointer);
      return fix ? { kind: "update", id, json: fix.json, label: fix.label } : null;
    }
    case "MQ3116": {
      if (element.kind !== "process") return null;
      const json = removeCall(element, pointer);
      return json ? { kind: "update", id, json, label: "Remove the in_state call" } : null;
    }
    case "MQ9901":
    case "MQ9902":
    case "MQ9903":
    case "MQ9904":
    case "MQ9905":
    case "MQ9906": {
      const fix = rightFix(d.rule, element, pointer);
      return fix ? { kind: "update", id, json: fix.json, label: fix.label } : null;
    }
    case "MQ8101":
    case "MQ8102":
    case "MQ8103":
    case "MQ8105":
    case "MQ8106":
    case "MQ8107": {
      if (element.kind !== "process" && element.kind !== "trigger-point") return null;
      const fix = triggerPointFix(d.rule, element, pointer);
      return fix ? { kind: "update", id, json: fix.json, label: fix.label } : null;
    }
    case "MQ8201":
    case "MQ8202":
    case "MQ8203":
    case "MQ8204": {
      if (element.kind !== "process") return null;
      const fix = approvalFix(d.rule, element, pointer);
      return fix ? { kind: "update", id, json: fix.json, label: fix.label } : null;
    }
    case "MQ9016": {
      if (!pointer.endsWith("/element")) return null;
      const json = removeAt(element, parent(pointer));
      return json ? { kind: "update", id, json, label: "Remove the member" } : null;
    }
    case "MQ9601": {
      // A memberOf entry that names the actor itself, a repeat or an actor that holds no members: remove the entry.
      if (!/^\/memberOf\/\d+$/.test(pointer)) return null;
      const json = removeAt(element, pointer);
      return json ? { kind: "update", id, json, label: "Remove the entry" } : null;
    }
    case "MQ9606": {
      // A subject entry's owner that holds no one actor: clear the key (the owner is optional).
      if (!/^\/subjects\/\d+\/owner$/.test(pointer) || element.kind !== "process") return null;
      const json = removeAt(element, pointer);
      return json ? { kind: "update", id, json, label: "Clear the owner" } : null;
    }
    case "MQ9605": {
      // A second system actor: change its type to an external system (the finding is on /type).
      if (pointer !== "/type" || element.kind !== "actor") return null;
      const json = clone(element);
      json.type = "external-system";
      return { kind: "update", id, json, label: "Make it an external system" };
    }
    case "MQ9102": {
      const gate = parent(parent(pointer));
      const actor = str(atPointer(element, pointer));
      const json = actor && last(parent(pointer)) === "requiredActors" ? addTo(element, gate, "signers", actor) : null;
      return json ? { kind: "update", id, json, label: "Add to signers" } : null;
    }
    case "MQ9105": {
      const gate = parent(parent(pointer));
      const signer = str(atPointer(element, pointer));
      const event = str((atPointer(element, parent(gate)) as Json | undefined)?.event);
      const index = ((element.events as Json[] | undefined) ?? []).findIndex((e) => e.id === event);
      if (!signer || last(parent(pointer)) !== "signers" || index < 0) return null;
      const json = addTo(element, `/events/${index}`, "actors", signer);
      return json ? { kind: "update", id, json, label: "Add the signer to the event's actors" } : null;
    }
    case "MQ9205": {
      const entry = entryOf(element, pointer);
      const subject = entry?.entity;
      const entity = subject ? doc(subject) : undefined;
      const attributes = (entity?.attributes as Json[] | undefined) ?? [];
      const index = attributes.findIndex((a) => a.id === entry?.boundAttribute);
      // An inherited or stereotype-virtual bound attribute is not in the entity's own list: no fix here.
      if (!entity || !subject || index < 0) return null;
      // A binding by code (a reference type or a plain status) gets the initial configuration's code, as the state has it;
      // an enum binding, as before, the initial state's name.
      const byCode = classifyAttribute(attributes[index], (t) => doc(t)).kind;
      const value = byCode === "reference" || byCode === "plain" ? initialCode(element) : str(initialState(element)?.name);
      if (value === undefined) return null;
      const json = clone(entity);
      (json.attributes as Json[])[index].default = value;
      return { kind: "update", id: subject, json, label: `Set default to ${value}` };
    }
    case "MQ9201": {
      // Two of its four findings are fixable (the derivation table): a subject whose lifecycle does not name this
      // process, and an entity whose lifecycle names an orchestration.
      if ((pointer === "/subject" || pointer.startsWith("/subjects")) && element.kind === "process") {
        const subject = entryOf(element, pointer)?.entity;
        const entity = subject ? doc(subject) : undefined;
        if (!subject || !entity) return null;
        const ids = [...new Set([subject, id, str(entity.lifecycle)].filter((x): x is string => !!x))];
        return { kind: "operation", op: "set-lifecycle", id: subject, target: id, label: "Set lifecycle on the subject", ids };
      }
      if (pointer === "/lifecycle" && element.kind === "entity") {
        const target = str(element.lifecycle);
        const process = target ? doc(target) : undefined;
        if (!target || !process || process.use === "lifecycle") return null;
        const ids = [...new Set([id, target, ...subjectsOf(process).map((e) => e.entity)].filter((x): x is string => !!x))];
        return { kind: "operation", op: "set-lifecycle", id, target, label: "Make it this entity's lifecycle", ids };
      }
      return null;
    }
  }
  return null;
}
