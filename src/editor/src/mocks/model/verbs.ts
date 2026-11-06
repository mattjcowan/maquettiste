// Verbs in the mock backend (verbs-design.md): the uses of a verb key, the project's verb vocabulary, rules MQ9701 to MQ9707 and
// the batch operations rename-verb and remove-verb. It follows the engine's Model/Verbs code.
import { builtInVerbs } from "@/model/verbs";

type Json = Record<string, unknown>;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string | null => (typeof v === "string" ? v : null);

export const BUILT_IN_VERB_KEYS: ReadonlySet<string> = new Set(builtInVerbs.map((v) => v.key));
/** Built-in keys with their hyphens dropped, to the key (MQ9706). */
const FLAT = new Map(builtInVerbs.map((v) => [v.key.replace(/-/g, ""), v.key]));

/** A place that names a verb: an event, a transition (without an event) or an operation. */
export interface VerbUse {
  /** The JSON pointer of the `verb` field in its document. */
  pointer: string;
  /** The event, transition or operation that holds it. */
  holder: string | null;
  key: string;
  owner: "event" | "transition" | "operation";
}

/** Every use of a verb in a document (the three places of decisions 11 to 13), in file order. */
export function verbUses(doc: Json): VerbUse[] {
  const out: VerbUse[] = [];
  if (doc.kind === "process") {
    arr(doc.events).forEach((e, i) => {
      const key = str(e.verb);
      if (key !== null) out.push({ pointer: `/events/${i}/verb`, holder: str(e.id), key, owner: "event" });
    });
    arr(doc.transitions).forEach((t, i) => {
      const key = str(t.verb);
      if (key !== null) out.push({ pointer: `/transitions/${i}/verb`, holder: str(t.id), key, owner: "transition" });
    });
  } else if (doc.kind === "operation") {
    const key = str(doc.verb);
    if (key !== null) out.push({ pointer: "/verb", holder: str(doc.id), key, owner: "operation" });
  }
  return out;
}

/** The distinct verb keys a process's events and transitions name, sorted ordinally (ElementSummary.verbs). */
export function processVerbs(doc: Json): string[] {
  return [...new Set(verbUses(doc).map((u) => u.key))].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/** What the model declares about verbs: the vocabulary file, the own verbs by key and the hidden built-in keys. */
export interface VerbGlobals {
  /** The first verb vocabulary file's id; a second one is MQ1009. */
  vocabularyId: string | null;
  own: Map<string, string>;
  hidden: Set<string>;
}

export function verbGlobalsOf(entries: Iterable<{ id: string; json: Json }>): VerbGlobals {
  const out: VerbGlobals = { vocabularyId: null, own: new Map(), hidden: new Set() };
  for (const e of entries) {
    if (e.json.kind !== "verb-vocabulary" || out.vocabularyId !== null) continue;
    out.vocabularyId = e.id;
    for (const v of arr(e.json.verbs)) if (str(v.key) !== null && !out.own.has(v.key as string)) out.own.set(v.key as string, String(v.id));
    for (const k of Array.isArray(e.json.hidden) ? (e.json.hidden as unknown[]) : []) if (typeof k === "string") out.hidden.add(k);
  }
  return out;
}

export type VerbEmit = (rule: string, severity: "error" | "warning", message: string, pointer: string, elementId?: string) => void;

/** MQ9701 to MQ9706, MQ1009 for a second verb file: the findings of one document. */
export function verbDiagnostics(json: Json, globals: VerbGlobals, emit: VerbEmit): void {
  if (json.kind === "verb-vocabulary") {
    if (globals.vocabularyId !== null && globals.vocabularyId !== json.id)
      emit("MQ1009", "error", "The project has more than one verb vocabulary; keep model/vocabularies/verbs.json.", "");
    (Array.isArray(json.hidden) ? (json.hidden as unknown[]) : []).forEach((k, i) => {
      if (typeof k === "string" && !BUILT_IN_VERB_KEYS.has(k))
        emit("MQ9703", "error", `'${k}' is not a built-in verb, so it cannot be hidden.`, `/hidden/${i}`);
    });
    const seen = new Map<string, string>();
    arr(json.verbs).forEach((v, i) => {
      const key = str(v.key);
      if (key === null) return;
      const at = `/verbs/${i}/key`;
      const flatOwn = key.replace(/-/g, "");
      if (BUILT_IN_VERB_KEYS.has(key)) emit("MQ9702", "error", `The own verb '${key}' has a built-in verb's key; use the built-in verb.`, at);
      else if (seen.has(flatOwn))
        emit(
          "MQ9702",
          "error",
          seen.get(flatOwn) === key ? `Two own verbs have the key '${key}'.` : `The own verbs '${seen.get(flatOwn)}' and '${key}' differ only by hyphens.`,
          at,
        );
      else {
        const flat = FLAT.get(key.replace(/-/g, ""));
        if (flat !== undefined) emit("MQ9706", "warning", `The own verb '${key}' reads like the built-in verb '${flat}'; use the built-in verb.`, at);
      }
      if (!seen.has(flatOwn)) seen.set(flatOwn, key);
    });
  }
  for (const use of verbUses(json)) {
    if (BUILT_IN_VERB_KEYS.has(use.key)) {
      if (globals.hidden.has(use.key))
        emit(
          "MQ9704",
          "warning",
          `The verb '${use.key}' is hidden in this project's verbs; show it again or name another.`,
          use.pointer,
          use.holder ?? undefined,
        );
    } else if (!globals.own.has(use.key))
      emit(
        "MQ9701",
        "error",
        `Unknown verb '${use.key}': it is neither a built-in verb nor one of the project's own verbs.`,
        use.pointer,
        use.holder ?? undefined,
      );
  }
  arr(json.transitions).forEach((t, i) => {
    if (json.kind === "process" && str(t.event) !== null && t.verb !== undefined)
      emit(
        "MQ9705",
        "error",
        "A transition triggered by an event takes the event's verb; move this verb to the event or clear it.",
        `/transitions/${i}/verb`,
        str(t.id) ?? undefined,
      );
  });
}

/** MQ9707: the own verbs of a vocabulary whose key a save changes while a use names it, as messages. Empty when none. */
export function verbKeyChanges(before: Json, after: Json, uses: (key: string) => boolean): string[] {
  if (before.kind !== "verb-vocabulary" || after.kind !== "verb-vocabulary") return [];
  const now = new Map(arr(after.verbs).map((v) => [String(v.id), str(v.key)]));
  const out: string[] = [];
  for (const v of arr(before.verbs)) {
    const next = now.get(String(v.id));
    if (next !== undefined && next !== v.key && typeof v.key === "string" && uses(v.key))
      out.push(`The key of the own verb '${v.key}' is in use and cannot change to '${next}' in a save; use the batch operation rename-verb.`);
  }
  return out;
}

export interface VerbOp {
  op: "rename-verb" | "remove-verb";
  verb: string;
  to?: string;
}

const KEY = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/;

/**
 * rename-verb and remove-verb over documents: the changed documents, or why the operation is refused (MQ1002). A rename to another
 * own or built-in key merges (the definition goes); `to` equal to `verb` adopts the built-in of that key.
 */
export function verbOperation(docs: ReadonlyMap<string, Json>, op: VerbOp): { changed: Map<string, Json> } | { error: string } {
  const vocabulary = [...docs.values()].find((d) => d.kind === "verb-vocabulary");
  const own = arr(vocabulary?.verbs).find((v) => v.key === op.verb);
  if (!vocabulary || !own) return { error: `The project has no own verb with the key '${op.verb}'.` };
  const rename = op.op === "rename-verb";
  const to = op.to ?? "";
  if (rename) {
    if (!KEY.test(to)) return { error: `'${to}' is not a verb key: lower-case words joined by hyphens.` };
    if (to === op.verb && !BUILT_IN_VERB_KEYS.has(to)) return { error: `There is no built-in verb '${to}' to adopt.` };
  }
  const changed = new Map<string, Json>();
  const working = (id: string, json: Json) => {
    if (!changed.has(id)) changed.set(id, JSON.parse(JSON.stringify(json)) as Json);
    return changed.get(id)!;
  };
  // The uses of a built-in key are not uses of the own verb: the rename and the remove leave them as they are.
  for (const [id, doc] of BUILT_IN_VERB_KEYS.has(op.verb) ? [] : docs) {
    for (const use of verbUses(doc)) {
      if (use.key !== op.verb) continue;
      const json = working(id, doc);
      const holder = pointerParent(json, use.pointer);
      if (rename) holder[lastSegment(use.pointer)] = to;
      else delete holder[lastSegment(use.pointer)];
    }
  }
  const target = arr(vocabulary.verbs).find((v) => v.key === to);
  const json = working(String(vocabulary.id), vocabulary);
  const verbs = arr(json.verbs);
  // A rename to the key of another own verb merges; to a built-in key (or the same one adopted) the definition goes.
  const keeps = rename && to !== op.verb && !BUILT_IN_VERB_KEYS.has(to) && !target;
  json.verbs = verbs.flatMap((v) => (v.key !== op.verb ? [v] : keeps ? [{ ...v, key: to }] : []));
  // A hidden built-in the rename adopts stays hidden: the project still hides it. An empty list is written as [].
  json.verbs = arr(json.verbs);
  // Writes that change nothing are skipped.
  for (const [id, doc] of [...changed]) if (JSON.stringify(doc) === JSON.stringify(docs.get(id))) changed.delete(id);
  return { changed };
}

function lastSegment(pointer: string): string {
  return pointer.slice(pointer.lastIndexOf("/") + 1);
}

function pointerParent(json: Json, pointer: string): Json {
  let at: unknown = json;
  for (const seg of pointer.split("/").slice(1, -1)) at = (at as Record<string, unknown>)[seg];
  return at as Json;
}
