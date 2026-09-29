// The path summary (generation-ui.md 2.2): an output pattern read aloud as placeholders, without rendering. Literal
// text stays; a member path becomes <name> after its last member, except that a final `name` takes its owner's name
// (`table.database.name` -> <database>, `table.name` -> <table>); a helper call keeps its argument's placeholder
// (`kebab database.name` -> <database>); an `if ... end` body becomes [...]. Anything else (a loop, arithmetic, a
// pipe, a function the summary does not know) gives back the raw pattern, flagged so the UI shows it in monospace.
export interface PathSummary {
  text: string;
  /** True when the pattern could not be read aloud and `text` is the raw pattern. */
  raw: boolean;
}

export const NO_OUTPUT = "(files the template writes)";

const MEMBER = /^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$/;
const HELPER = /^([A-Za-z_][A-Za-z0-9_]*)\s+(\S+)$/;
const KEYWORDS = new Set(["if", "else", "end", "for", "in", "while", "func", "ret", "capture", "with", "include", "case", "when"]);

type Token = { kind: "text"; text: string } | { kind: "code"; code: string };

function tokenize(pattern: string, open: string, close: string): Token[] | null {
  const out: Token[] = [];
  let at = 0;
  while (at < pattern.length) {
    const start = pattern.indexOf(open, at);
    if (start < 0) {
      out.push({ kind: "text", text: pattern.slice(at) });
      break;
    }
    if (start > at) out.push({ kind: "text", text: pattern.slice(at, start) });
    const end = pattern.indexOf(close, start + open.length);
    if (end < 0) return null;
    let code = pattern.slice(start + open.length, end);
    // Whitespace-trimming markers {{~ ~}} and {{- -}}.
    code = code.replace(/^[~-]/, "").replace(/[~-]$/, "");
    out.push({ kind: "code", code: code.trim() });
    at = end + close.length;
  }
  return out;
}

/** The placeholder for a member path: its last member, or its owner's when the last is `name`. */
export function placeholder(path: string): string {
  const parts = path.split(".");
  const last = parts[parts.length - 1];
  return `<${last === "name" && parts.length > 1 ? parts[parts.length - 2] : last}>`;
}

function expression(code: string): string | null {
  if (MEMBER.test(code)) {
    const head = code.split(".")[0];
    return KEYWORDS.has(head) ? null : placeholder(code);
  }
  const call = HELPER.exec(code);
  if (call && !KEYWORDS.has(call[1]) && MEMBER.test(call[2]) && !KEYWORDS.has(call[2].split(".")[0])) return placeholder(call[2]);
  return null;
}

/** Summarizes `output` read with the unit's delimiters (default `{{ }}`). */
export function pathSummary(output: string | null | undefined, delimiters?: { open: string; close: string } | null): PathSummary {
  if (output === null || output === undefined || output === "") return { text: NO_OUTPUT, raw: false };
  const tokens = tokenize(output, delimiters?.open || "{{", delimiters?.close || "}}");
  const fallback = { text: output, raw: true };
  if (!tokens) return fallback;
  let text = "";
  let depth = 0;
  for (const token of tokens) {
    if (token.kind === "text") {
      text += token.text;
      continue;
    }
    const code = token.code;
    if (/^if\s+/.test(code)) {
      depth++;
      text += "[";
    } else if (code === "end") {
      if (depth === 0) return fallback;
      depth--;
      text += "]";
    } else {
      const part = expression(code);
      if (part === null) return fallback;
      text += part;
    }
  }
  return depth === 0 ? { text, raw: false } : fallback;
}
