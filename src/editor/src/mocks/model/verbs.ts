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
  owner: "event" | "transition" | "operation" | "right" | "trigger-point" | "step" | "not-by";
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
    // The verbs of the lifecycle's inline rights (rights-design.md section 8); a use names none.
    arr(doc.rights).forEach((r, i) =>
      (Array.isArray(r.verbs) ? (r.verbs as unknown[]) : []).forEach((key, j) => {
        if (typeof key === "string") out.push({ pointer: `/rights/${i}/verbs/${j}`, holder: str(r.id), key, owner: "right" });
      }),
    );
    // The verbs of its named signatures' notBy, inline or in a library's gate policies (signoff-design.md decision 15).
    const gates = [
      ...arr(doc.transitions).map((t, i) => ({ gate: t.gate as Json | undefined, at: `/transitions/${i}/gate` })),
      ...arr(doc.gates).map((gate, i) => ({ gate, at: `/gates/${i}` })),
    ];
    for (const { gate, at } of gates)
      arr(gate?.stages).forEach((stage, s) =>
        arr(stage.signatures).forEach((sig, j) =>
          arr(sig.notBy).forEach((n, k) => {
            const key = str(n.verb);
            if (key !== null) out.push({ pointer: `${at}/stages/${s}/signatures/${j}/notBy/${k}/verb`, holder: str(sig.id), key, owner: "not-by" });
          }),
        ),
      );
    // The verbs of its trigger points (trigger-points-design.md section 9).
    arr(doc.triggerPoints).forEach((t, i) =>
      (Array.isArray(t.verbs) ? (t.verbs as unknown[]) : []).forEach((key, j) => {
        if (typeof key === "string") out.push({ pointer: `/triggerPoints/${i}/verbs/${j}`, holder: str(t.id), key, owner: "trigger-point" });
      }),
    );
  } else if (doc.kind === "right" || doc.kind === "trigger-point") {
    (Array.isArray(doc.verbs) ? (doc.verbs as unknown[]) : []).forEach((key, j) => {
      if (typeof key === "string") out.push({ pointer: `/verbs/${j}`, holder: str(doc.id), key, owner: doc.kind as "right" | "trigger-point" });
    });
  } else if (doc.kind === "scenario") {
    // A verb step names one verb (rights-design.md section 8).
    arr(doc.steps).forEach((st, i) => {
      const key = str(st.verb);
      if (st.input === "verb" && key !== null) out.push({ pointer: `/steps/${i}/verb`, holder: str(st.id), key, owner: "step" });
    });
  } else if (doc.kind === "operation") {
    const key = str(doc.verb);
    if (key !== null) out.push({ pointer: "/verb", holder: str(doc.id), key, owner: "operation" });
  }
  return out;
}

/** The distinct verb keys a process's events, transitions and inline rights name, or a named right's, sorted ordinally (ElementSummary.verbs). */
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
export function verbOperation(docs: ReadonlyMap<string, Json>, op: VerbOp): { changed: Map<string, Json>; deleted: string[] } | { error: string } {
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
  // The named rights a remove leaves with no verb are deleted, and their uses leave the lifecycles' rights.
  const gone = new Set<string>();
  if (!rename && !BUILT_IN_VERB_KEYS.has(op.verb))
    for (const [id, doc] of docs) {
      const verbs = Array.isArray(doc.verbs) ? (doc.verbs as unknown[]) : [];
      if ((doc.kind === "right" || doc.kind === "trigger-point") && verbs.length > 0 && verbs.every((v) => v === op.verb)) gone.add(id);
    }
  // The uses of a built-in key are not uses of the own verb: the rename and the remove leave them as they are.
  for (const [id, doc] of BUILT_IN_VERB_KEYS.has(op.verb) ? [] : docs) {
    if (gone.has(id)) continue;
    for (const use of verbUses(doc)) {
      if (use.key !== op.verb) continue;
      const json = working(id, doc);
      const holder = pointerParent(json, use.pointer);
      if (rename) holder[lastSegment(use.pointer)] = to;
      // A right's verb is an item of its list: marked here, taken out below with the right that has no verb left.
      else if (use.owner === "right" || use.owner === "trigger-point") holder[lastSegment(use.pointer)] = null;
      else if (use.owner === "step")
        holder.__gone = true; // a verb step goes with its verb
      else delete holder[lastSegment(use.pointer)];
    }
  }
  const gonePoints = new Set<string>();
  // A named trigger point left with no verb goes too, and so do the scenario entries that name it.
  for (const id of gone) if (docs.get(id)?.kind === "trigger-point") gonePoints.add(id);
  if (!rename) {
    // Uses of a right that is going leave every lifecycle, touched or not.
    if (gone.size)
      for (const [id, doc] of docs)
        if (doc.kind === "process" && arr(doc.rights).some((r) => typeof r.right === "string" && gone.has(r.right))) working(id, doc);
    for (const json of changed.values()) {
      if (json.kind === "scenario") json.steps = arr(json.steps).filter((st) => !st.__gone);
      if (json.kind === "process" && Array.isArray(json.rights))
        json.rights = arr(json.rights).filter((r) => !(typeof r.right === "string" && gone.has(r.right)));
      dropRightsWithoutVerbs(json);
      dropTriggerPointsWithoutVerbs(json, gonePoints);
      dropEmptyNotBy(json);
    }
    // A step's word entries for a trigger point that went, and its id in the expected check warnings and trigger points.
    if (gonePoints.size)
      for (const [id, doc] of docs) {
        if (doc.kind !== "scenario") continue;
        const names = (st: Json) => {
          const expect = st.expect as Json | undefined;
          return (
            Object.keys((st.assume as Json | undefined) ?? {}).some((k) => gonePoints.has(k)) ||
            [expect?.checkWarnings, expect?.triggerPoints].some((l) => Array.isArray(l) && l.some((x) => gonePoints.has(String(x))))
          );
        };
        if (!arr(doc.steps).some(names)) continue;
        for (const st of arr(working(id, doc).steps)) {
          if (st.assume && typeof st.assume === "object") for (const k of Object.keys(st.assume as Json)) if (gonePoints.has(k)) delete (st.assume as Json)[k];
          const expect = st.expect as Json | undefined;
          for (const list of ["checkWarnings", "triggerPoints"])
            if (expect && Array.isArray(expect[list])) expect[list] = (expect[list] as unknown[]).filter((x) => !gonePoints.has(String(x)));
        }
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
  return { changed, deleted: [...gone].sort() };
}

/** A notBy entry whose verb a remove took is empty: it leaves its list, and an empty list is written without the key. */
function dropEmptyNotBy(json: Json): void {
  const clean = (gate: Json | undefined) =>
    arr(gate?.stages).forEach((stage) =>
      arr(stage.signatures).forEach((sig) => {
        if (!Array.isArray(sig.notBy)) return;
        const kept = arr(sig.notBy).filter((n) => n.verb !== undefined || n.transition !== undefined);
        if (kept.length) sig.notBy = kept;
        else delete sig.notBy;
      }),
    );
  if (json.kind !== "process") return;
  arr(json.transitions).forEach((t) => clean(t.gate as Json | undefined));
  arr(json.gates).forEach(clean);
}

/** Takes the verbs marked gone out of a document's rights; a right whose last verb goes goes too (rights-design.md section 8). */
function dropRightsWithoutVerbs(json: Json): void {
  if (json.kind === "right" || json.kind === "trigger-point") {
    json.verbs = arr(json.verbs).filter((v) => v !== null);
    return;
  }
  if (!Array.isArray(json.rights)) return;
  const kept = arr(json.rights).filter((r) => {
    if (!Array.isArray(r.verbs)) return true;
    r.verbs = (r.verbs as unknown[]).filter((v) => v !== null);
    return (r.verbs as unknown[]).length > 0;
  });
  if (kept.length) json.rights = kept;
  else delete json.rights;
}

/** Takes the verbs marked gone out of a lifecycle's trigger points; one whose last verb goes goes too (trigger-points-design.md section 9). */
function dropTriggerPointsWithoutVerbs(json: Json, gone: Set<string>): void {
  if (!Array.isArray(json.triggerPoints)) return;
  const kept = arr(json.triggerPoints).filter((t) => {
    if (!Array.isArray(t.verbs)) return true;
    t.verbs = (t.verbs as unknown[]).filter((v) => v !== null);
    if ((t.verbs as unknown[]).length > 0) return true;
    gone.add(String(t.id));
    return false;
  });
  if (kept.length) json.triggerPoints = kept;
  else delete json.triggerPoints;
}

function lastSegment(pointer: string): string {
  return pointer.slice(pointer.lastIndexOf("/") + 1);
}

function pointerParent(json: Json, pointer: string): Json {
  let at: unknown = json;
  for (const seg of pointer.split("/").slice(1, -1)) at = (at as Record<string, unknown>)[seg];
  return at as Json;
}
