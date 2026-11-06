// The mock's expression preview (POST /api/model/expressions/preview), for a set's value only. The mock has no expression
// engine: it prints a stored tree, echoes text, and guesses the type of the few forms it reads (effects.ts). A tree that uses
// actor() or in_state previews JavaScript but no SQL and no answer, as the engine's does (object-effects-design.md decision 11).
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
  if (text === "actor()") return "string";
  if (text === "now()") return "datetimeoffset";
  if (text === "true" || text === "false" || /^in_state\(/.test(text)) return "bool";
  if (/^-?\d+$/.test(text)) return "int32";
  if (/^'.*'$/.test(text)) return "string";
  return "unknown";
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
    if ((text.match(/\(/g) ?? []).length !== (text.match(/\)/g) ?? []).length)
      return { preview: null, diagnostics: [diagnostic("MQ3101", "The expression does not parse: the parentheses do not match.", element)] };
  } else {
    // A stored set, by its id.
    const found = all.flatMap((e) => arr(e.json.actions).flatMap((a) => arr(a.sets).filter((s) => s.id === element)));
    if (!found.length) return { preview: null, diagnostics: [diagnostic("MQ3101", `No expression is stored at ${element}.`, element)] };
    tree = found[0].value;
    text = printTree(
      tree,
      (id) => names.get(id) ?? id,
      (id) => states.get(id) ?? id,
    );
  }
  const t = tree as Json | undefined;
  const usesIdentity = /actor\(\)|in_state\(/.test(text) || (t && (t.actor === true || Array.isArray(t.inState)));
  const type = t
    ? "null" in t
      ? "unknown"
      : t.actor === true
        ? "string"
        : t.now === true
          ? "datetimeoffset"
          : Array.isArray(t.inState)
            ? "bool"
            : typeOfText(text)
    : typeOfText(text);
  return {
    preview: {
      text,
      type,
      nullable: true,
      javascript: `(row, mq) => ${text}`,
      sql: null,
      sqlNote: usesIdentity ? "The expression reads the actor or a lifecycle's state, which no database can hold." : "The mock does not write SQL.",
      answer: null,
    },
    diagnostics: [],
  };
}
