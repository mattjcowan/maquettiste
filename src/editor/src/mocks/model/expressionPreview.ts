// The mock's expression preview (POST /api/model/expressions/preview), for a set's value only. The mock has no expression
// engine: it prints a stored tree, echoes text, and guesses the type of the few forms it reads (effects.ts). A tree that uses
// current_actor() or in_state previews JavaScript but no SQL and no answer, as the engine's does (object-effects-design.md decision 11).
import { printTree } from "./effects";

type Json = Record<string, unknown>;
interface Entry {
  id: string;
  json: Json;
}
const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

interface Request {
  element?: string;
  at?: string | null;
  text?: string | null;
  site?: string | null;
  attribute?: string | null;
  member?: string | null;
}

const diagnostic = (rule: string, message: string, elementId: string) => ({
  rule,
  severity: "error",
  message,
  elementId,
  filePath: null,
  jsonPointer: null,
  line: null,
  column: null,
});

function typeOfText(text: string): string {
  if (text === "current_actor()") return "string";
  if (text === "now()") return "datetimeoffset";
  if (text === "true" || text === "false" || /^in_state\(/.test(text)) return "bool";
  if (/^-?\d+$/.test(text)) return "int32";
  if (/^'.*'$/.test(text)) return "string";
  return "unknown";
}

/** The member id of a stored call input, by the input's id. */
function found(all: Entry[], inputId: string): string | undefined {
  for (const e of all)
    for (const a of arr(e.json.actions))
      for (const x of arr((a.call as Json | undefined)?.inputs)) if (x.id === inputId && typeof x.member === "string") return x.member;
  return undefined;
}

/** A member's type as the preview names it: the keyword, the name of a referenced type, or its record's key type for a record key. */
function memberTypeName(member: Json, all: Entry[]): string | null {
  const nameOf = (id: string) => String(all.find((e) => e.id === id)?.json.name ?? id);
  if (typeof member.keyOf === "string") return nameOf(member.keyOf) + " key";
  const t = member.type;
  if (typeof t === "string") return t;
  if (t && typeof t === "object" && typeof (t as Json).ref === "string") return nameOf((t as Json).ref as string);
  return null;
}

export function previewExpression(entries: Iterable<Entry>, req: Request) {
  const all = [...entries];
  const names = new Map<string, string>();
  for (const e of all) for (const a of arr(e.json.attributes)) names.set(String(a.id), String(a.name));
  const states = new Map<string, string>();
  const walk = (proc: Json, list: unknown, path: string) =>
    arr(list).forEach((s) => {
      const p = path ? `${path}.${String(s.name)}` : String(s.name);
      states.set(String(s.id), `${String(proc.name)}.${p}`);
      walk(proc, s.states, p);
    });
  for (const e of all) if (e.json.kind === "process") walk(e.json, e.json.states, "");
  for (const e of all) for (const a of arr(e.json.context)) names.set(String(a.id), String(a.name));
  const element = String(req.element ?? "");
  let text: string | null = null;
  let tree: unknown;
  if (typeof req.text === "string") {
    if (!req.site) return { preview: null, diagnostics: [diagnostic("MQ3101", "Text to preview needs a site.", element)] };
    text = req.text.trim();
    if (!text) return { preview: null, diagnostics: [diagnostic("MQ3101", "The expression is empty.", element)] };
    // A signature's `when` (site `approval`) names its lifecycle and is a condition: true or false, never a text or a number.
    if (req.site === "approval") {
      const holder = all.find((e) => e.id === element)?.json;
      if (!holder || holder.kind !== "process")
        return {
          preview: null,
          diagnostics: [diagnostic("MQ3101", "A signature's condition is previewed in its lifecycle; name the lifecycle as the element.", element)],
        };
      const type = typeOfText(text);
      if (type === "string" || type === "int32" || type === "datetimeoffset")
        return { preview: null, diagnostics: [diagnostic("MQ3101", `A signature's condition must be true or false, not ${type}.`, element)] };
    }
    if ((text.match(/\(/g) ?? []).length !== (text.match(/\)/g) ?? []).length)
      return { preview: null, diagnostics: [diagnostic("MQ3101", "The expression does not parse: the parentheses do not match.", element)] };
  } else {
    // A stored set, by its id.
    // A stored set of an action or of a trigger point, by its id, or a stored expression of an element at a pointer (a trigger point's `when`).
    const found = all.flatMap((e) => [
      ...[...arr(e.json.actions), ...arr(e.json.triggerPoints)].flatMap((a) => arr(a.sets).filter((s) => s.id === element)),
      ...arr(e.json.actions).flatMap((a) => arr((a.call as Json | undefined)?.inputs).filter((x) => x.id === element)),
    ]);
    const holder = req.at ? all.find((e) => e.id === element)?.json : undefined;
    const stored = holder
      ? req
          .at!.split("/")
          .slice(1)
          .reduce<unknown>((node, part) => (node && typeof node === "object" ? (node as Json)[part] : undefined), holder)
      : undefined;
    if (!found.length && !stored) return { preview: null, diagnostics: [diagnostic("MQ3101", `No expression is stored at ${element}.`, element)] };
    tree = found.length ? found[0].value : stored;
    text = printTree(
      tree,
      (id) => names.get(id) ?? id,
      (id) => states.get(id) ?? id,
    );
  }
  const t = tree as Json | undefined;
  const usesIdentity = /current_actor\(\)|in_state\(/.test(text) || (t && (t.currentActor === true || Array.isArray(t.inState)));
  const type = t
    ? "null" in t
      ? "unknown"
      : t.currentActor === true
        ? "string"
        : t.now === true
          ? "datetimeoffset"
          : Array.isArray(t.inState)
            ? "bool"
            : typeOfText(text)
    : typeOfText(text);
  // A call's input is typed against its member: the member's type, not a guess from the text (decision 6).
  const memberId = req.site === "input" ? req.member : found(all, element);
  const member = memberId ? all.flatMap((e) => (e.json.kind === "operation" ? arr(e.json.input) : [])).find((m) => m.id === memberId) : undefined;
  const memberType = member ? memberTypeName(member, all) : null;
  return {
    preview: {
      text,
      type: memberType ?? type,
      nullable: true,
      javascript: `(row, mq) => ${text}`,
      sql: null,
      sqlNote: usesIdentity ? "The expression reads the actor or a lifecycle's state, which no database can hold." : "The mock does not write SQL.",
      answer: null,
    },
    diagnostics: [],
  };
}
