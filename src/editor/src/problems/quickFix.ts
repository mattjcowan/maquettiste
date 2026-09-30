// The Problems panel's quick fixes (phase-3-design.md 3, "Quick fixes" and its derivation table): what a diagnostic's
// fix is, read from its rule, its element and its pointer. The named operations (`set-initial`, `sync-enum`,
// `refresh-scenario`, `set-lifecycle`) are batch operations of the engine; the others are plain updates of the
// diagnostic's element (or, for MQ9205, of the subject entity) built here. Pure: the panel loads the documents
// `fixDocuments` names and applies the result as one batch that undo reverts.
import type { Diagnostic, ModelJson } from "@/api/types";
import { atPointer, clone } from "@/lib/json";

type Json = Record<string, unknown>;

export type QuickFixOperation = "set-initial" | "set-lifecycle" | "refresh-scenario";

export type QuickFix =
  /** A named batch operation; `ids` are every document it may change (the undo step's before and after). */
  | { kind: "operation"; op: QuickFixOperation; id: string; target?: string; label: string; ids: string[]; choices?: { id: string; name: string }[] }
  /** Sync enum: a dry run first, then the apply (the process's own endpoint). */
  | { kind: "sync-enum"; process: string; label: string }
  /** A plain update of one document. */
  | { kind: "update"; id: string; json: ModelJson; label: string };

/** The rules whose fix is built in the editor as a plain update (the derivation table's last row) or needs two findings of MQ9201. */
const EDITOR_FIXES = new Set(["MQ9013", "MQ9016", "MQ9102", "MQ9105", "MQ9205", "MQ9201"]);

/** True when the diagnostic's rule has a quick fix: a catalog `quickFix` or one the editor builds. */
export function hasQuickFix(rule: string, catalogFix: string | null | undefined): boolean {
  return !!catalogFix || EDITOR_FIXES.has(rule);
}

const str = (value: unknown): string | undefined => (typeof value === "string" && value ? value : undefined);
const parent = (pointer: string) => pointer.slice(0, pointer.lastIndexOf("/"));
const last = (pointer: string) => pointer.slice(pointer.lastIndexOf("/") + 1);

/** The documents a fix reads: the diagnostic's element and, for MQ9201 and MQ9205, the partner on the other side. */
export function fixDocuments(d: Diagnostic, element: ModelJson | undefined): string[] {
  if (!d.elementId) return [];
  const related =
    d.rule === "MQ9205" || (d.rule === "MQ9201" && d.jsonPointer === "/subject")
      ? str(element?.subject)
      : d.rule === "MQ9201" && d.jsonPointer === "/lifecycle"
        ? str(element?.lifecycle)
        : undefined;
  return related ? [d.elementId, related] : [d.elementId];
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

/**
 * The quick fix of a diagnostic, or null when it has none or its arguments cannot be read from the documents.
 * `catalogFix` is the rule's `quickFix` from GET /api/validation/rules; `doc` answers the documents `fixDocuments` named.
 */
export function deriveQuickFix(d: Diagnostic, catalogFix: string | null | undefined, doc: (id: string) => ModelJson | undefined): QuickFix | null {
  const element = d.elementId ? doc(d.elementId) : undefined;
  const pointer = d.jsonPointer ?? "";
  if (!element || !d.elementId) return null;
  const id = d.elementId;
  if (catalogFix === "sync-enum") return element.kind === "process" ? { kind: "sync-enum", process: id, label: "Sync enum" } : null;
  if (catalogFix === "refresh-scenario")
    return element.kind === "scenario" ? { kind: "operation", op: "refresh-scenario", id, label: "Update expectations from replay", ids: [id] } : null;
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
    case "MQ9016": {
      if (!pointer.endsWith("/element")) return null;
      const json = removeAt(element, parent(pointer));
      return json ? { kind: "update", id, json, label: "Remove the member" } : null;
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
      const subject = str(element.subject);
      const entity = subject ? doc(subject) : undefined;
      const state = str(initialState(element)?.name);
      const attributes = (entity?.attributes as Json[] | undefined) ?? [];
      const index = attributes.findIndex((a) => a.id === element.boundAttribute);
      // An inherited or stereotype-virtual bound attribute is not in the entity's own list: no fix here.
      if (!entity || !subject || !state || index < 0) return null;
      const json = clone(entity);
      (json.attributes as Json[])[index].default = state;
      return { kind: "update", id: subject, json, label: `Set default to ${state}` };
    }
    case "MQ9201": {
      // Two of its four findings are fixable (the derivation table): a subject whose lifecycle does not name this
      // process, and an entity whose lifecycle names an orchestration.
      if (pointer === "/subject" && element.kind === "process") {
        const subject = str(element.subject);
        const entity = subject ? doc(subject) : undefined;
        if (!subject || !entity) return null;
        const ids = [...new Set([subject, id, str(entity.lifecycle)].filter((x): x is string => !!x))];
        return { kind: "operation", op: "set-lifecycle", id: subject, target: id, label: "Set lifecycle on the subject", ids };
      }
      if (pointer === "/lifecycle" && element.kind === "entity") {
        const target = str(element.lifecycle);
        const process = target ? doc(target) : undefined;
        if (!target || !process || process.use === "lifecycle") return null;
        const ids = [...new Set([id, target, str(process.subject)].filter((x): x is string => !!x))];
        return { kind: "operation", op: "set-lifecycle", id, target, label: "Make it this entity's lifecycle", ids };
      }
      return null;
    }
  }
  return null;
}
