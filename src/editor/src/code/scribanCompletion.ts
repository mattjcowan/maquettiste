// Template completion and hover for the Scriban editor (generation-ui.md 3.3, step 6), pure: the suggestions for the
// text before the cursor and the documentation of a word, both from the unit's GET /api/templates/context answer
// (variables, member lists, built-in and pack-registered helpers) plus the template language's own pipe functions.
// Suggestions are offered only inside a code block (`{{ ... }}`); after `|` they are helpers and pipe functions, after
// `name.` the members of that name.
import { SCRIBAN_KEYWORDS } from "./scriban";

/** The completion data of one unit (the shape of TemplateContextResult; kept structural so the code folder stays apart from the API types). */
export interface TemplateCompletionData {
  unit: string;
  scope: string;
  variables: { name: string; detail: string }[];
  members: Record<string, { name: string; type: string }[]>;
  helpers: string[];
  registrations: { kind: string; name: string; declaredIn: string }[];
}

export type SuggestionKind = "variable" | "member" | "helper" | "function" | "keyword" | "module";

export interface Suggestion {
  label: string;
  kind: SuggestionKind;
  detail: string;
  documentation?: string;
}

/** The template language's built-in functions most templates use, by object, with one line each. */
export const PIPE_FUNCTIONS: Record<string, Record<string, string>> = {
  array: {
    add: "Appends a value to a list.",
    compact: "Drops the null values.",
    concat: "Joins two lists.",
    contains: "True when the list holds the value.",
    each: "Applies a function to every item.",
    filter: "Keeps the items a function accepts.",
    first: "The first item.",
    join: "The items as text with a separator.",
    last: "The last item.",
    limit: "The first N items.",
    map: "A member of every item.",
    offset: "The items after the first N.",
    reverse: "The items in reverse order.",
    size: "The number of items.",
    sort: "The items sorted, optionally by a member.",
    uniq: "The items without duplicates.",
  },
  math: {
    abs: "The absolute value.",
    ceil: "Rounds up.",
    floor: "Rounds down.",
    format: "Formats a number with a .NET format string.",
    round: "Rounds to N decimals.",
  },
  object: {
    default: "The value, or a default when it is null or empty.",
    format: "Formats a value with a .NET format string.",
    has_key: "True when the object has the member.",
    keys: "The member names.",
    size: "The number of members or items.",
    typeof: "The value's type name.",
    values: "The member values.",
  },
  string: {
    append: "Adds text at the end.",
    capitalize: "Upper-cases the first letter.",
    contains: "True when the text holds the value.",
    downcase: "Lower-cases the text.",
    ends_with: "True when the text ends with the value.",
    handleize: "The text as a lower-case, hyphenated handle.",
    lstrip: "Removes leading white space.",
    pad_left: "Pads on the left to a width.",
    pad_right: "Pads on the right to a width.",
    prepend: "Adds text at the start.",
    remove: "Removes every occurrence of a value.",
    replace: "Replaces every occurrence of a value.",
    rstrip: "Removes trailing white space.",
    size: "The number of characters.",
    slice: "A part of the text.",
    split: "Splits the text into a list.",
    starts_with: "True when the text starts with the value.",
    strip: "Removes white space at both ends.",
    truncate: "Cuts the text to a length.",
    upcase: "Upper-cases the text.",
  },
  date: {
    now: "The current date and time.",
    parse: "Reads a date from text.",
    to_string: "Formats a date.",
  },
  regex: {
    match: "The first match of a pattern.",
    replace: "Replaces the matches of a pattern.",
    split: "Splits the text on a pattern.",
  },
  html: {
    escape: "Escapes the text for HTML.",
    strip: "Removes HTML tags.",
  },
  timespan: {
    from_days: "A time span of N days.",
    from_hours: "A time span of N hours.",
  },
};

/** True when `before` (the text up to the cursor) ends inside an open `{{` code block. */
export function inCodeBlock(before: string): boolean {
  const open = before.lastIndexOf("{{");
  if (open < 0) return false;
  if (before.indexOf("}}", open) >= 0) return false;
  const raw = before.lastIndexOf("{%{");
  return raw < 0 || before.indexOf("}%}", raw) >= 0;
}

/** The dotted path being typed before the cursor (`model.entities`, `pack.params.q`), and whether a pipe precedes it. */
export function pathBefore(before: string): { path: string; afterPipe: boolean } {
  const m = /([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*\.?|)$/.exec(before);
  const path = m?.[1] ?? "";
  const rest = before.slice(0, before.length - path.length);
  return { path, afterPipe: /\|>?\s*$/.test(rest) };
}

/** The name a scope variable has (`each entity` gives `entity`, `each reference type` gives `reference_type`). */
function scopeVariable(scope: string): string | null {
  return scope.startsWith("each ") ? scope.slice(5).replace(/ /g, "_") : null;
}

/** The member list a dotted prefix names: `model`, `element` (or the scope's own name), `pack`, `pack.params`, a function object. */
function membersOf(data: TemplateCompletionData, owner: string): Suggestion[] | null {
  if (owner === "model" || owner === "element" || owner === scopeVariable(data.scope)) {
    const list = data.members[owner === "model" ? "model" : "element"];
    return list ? list.map((m) => ({ label: m.name, kind: "member" as const, detail: m.type })) : null;
  }
  const dotted = data.variables.filter((v) => v.name.startsWith(`${owner}.`) && !v.name.slice(owner.length + 1).includes("."));
  if (dotted.length) return dotted.map((v) => ({ label: v.name.slice(owner.length + 1), kind: "variable" as const, detail: v.detail }));
  if (owner === "pack")
    return [
      { label: "name", kind: "member", detail: "string" },
      { label: "version", kind: "member", detail: "string" },
      { label: "params", kind: "member", detail: "map" },
    ];
  if (owner === "unit")
    return [
      { label: "id", kind: "member", detail: "string" },
      { label: "key", kind: "member", detail: "string" },
    ];
  const functions = PIPE_FUNCTIONS[owner];
  if (functions) return Object.entries(functions).map(([name, doc]) => ({ label: name, kind: "function" as const, detail: doc }));
  return null;
}

function helperSuggestions(data: TemplateCompletionData): Suggestion[] {
  const declared = new Map(data.registrations.filter((r) => r.kind === "helper").map((r) => [r.name, r.declaredIn]));
  return data.helpers.map((name) => {
    const from = declared.get(name);
    return { label: name, kind: "helper" as const, detail: from ? `helper from ${from}` : "built-in helper" };
  });
}

function pipeSuggestions(): Suggestion[] {
  const out: Suggestion[] = [];
  for (const [owner, functions] of Object.entries(PIPE_FUNCTIONS))
    for (const [name, doc] of Object.entries(functions)) out.push({ label: `${owner}.${name}`, kind: "function", detail: doc });
  return out;
}

/**
 * The suggestions for the text before the cursor: nothing outside a code block; members after `name.`; helpers and
 * pipe functions after `|`; else the top-level variables, helpers, function objects and keywords. Ordinal by label.
 */
export function completionsAt(data: TemplateCompletionData, before: string): Suggestion[] {
  if (!inCodeBlock(before)) return [];
  const { path, afterPipe } = pathBefore(before);
  const dot = path.lastIndexOf(".");
  let items: Suggestion[];
  if (dot >= 0) items = membersOf(data, path.slice(0, dot)) ?? [];
  else if (afterPipe) items = [...helperSuggestions(data), ...pipeSuggestions()];
  else {
    const seen = new Set<string>();
    items = [];
    for (const v of data.variables) {
      const top = v.name.split(".")[0];
      if (seen.has(top)) continue;
      seen.add(top);
      items.push({ label: top, kind: "variable", detail: v.name === top ? v.detail : "object" });
    }
    items.push(...helperSuggestions(data));
    for (const owner of Object.keys(PIPE_FUNCTIONS)) items.push({ label: owner, kind: "module", detail: "built-in functions" });
    for (const k of SCRIBAN_KEYWORDS) items.push({ label: k, kind: "keyword", detail: "keyword" });
  }
  return items.sort((a, b) => (a.label < b.label ? -1 : a.label > b.label ? 1 : 0));
}

/** The dotted word under `column` (0-based) of a line, or null. */
export function wordAt(line: string, column: number): string | null {
  const isWord = (c: string | undefined) => !!c && /[\w$.]/.test(c);
  let start = column;
  let end = column;
  while (start > 0 && isWord(line[start - 1])) start--;
  while (end < line.length && isWord(line[end]) && line[end] !== ".") end++;
  const word = line.slice(start, end).replace(/^\.+|\.+$/g, "");
  return word || null;
}

/** The documentation of a (dotted) word, from the same data as completion; null when nothing is known about it. */
export function hoverFor(data: TemplateCompletionData, word: string): string | null {
  const variable = data.variables.find((v) => v.name === word);
  if (variable) return `**${variable.name}**: ${variable.detail}`;
  const dot = word.lastIndexOf(".");
  if (dot > 0) {
    const owner = word.slice(0, dot);
    const name = word.slice(dot + 1);
    const member = membersOf(data, owner)?.find((m) => m.label === name);
    if (member) return member.kind === "function" ? `**${word}**: ${member.detail}` : `**${word}** (${member.detail})`;
  }
  const helper = helperSuggestions(data).find((h) => h.label === word);
  if (helper) return `**${word}**: ${helper.detail}`;
  if (PIPE_FUNCTIONS[word]) return `**${word}**: built-in functions (${Object.keys(PIPE_FUNCTIONS[word]).length})`;
  const own = scopeVariable(data.scope);
  if (own && word === own) return `**${own}**: the ${data.scope.slice(5)} this unit renders for`;
  return null;
}
