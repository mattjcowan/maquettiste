// The search syntax shared by the explorer's filter box, quick open and the palette (explorer-redesign.md 3.1):
// four operators and five qualifiers. Pure, so the worker and the main thread parse alike.
//
//   *text  contains (the default when no operator is typed)     ^text  starts with
//   ~a%b   like: `%` matches any run of characters, anchored     =text  equals
//   kind:enum  in:Billing  tag:pii  st:aggregate  cat:finance   (quote a value with spaces: in:"Order taking")
//
// Matching is case-insensitive. A qualifier narrows; several values of one qualifier are "any of".

export type Operator = "contains" | "prefix" | "like" | "equals";
export type QualifierKey = "kind" | "in" | "tag" | "st" | "cat";

export const QUALIFIER_KEYS: readonly QualifierKey[] = ["kind", "in", "tag", "st", "cat"];

export interface Qualifiers {
  kind: string[];
  in: string[];
  tag: string[];
  st: string[];
  cat: string[];
}

export interface SearchQuery {
  /** The text to match, lower-cased, without its operator. Empty when only qualifiers were typed. */
  term: string;
  op: Operator;
  /** Whether an operator was typed: quick open then uses the operator's plain match instead of ranking. */
  explicit: boolean;
  /** Lower-cased qualifier values, in the order typed. */
  q: Qualifiers;
}

const OPERATORS: Record<string, Operator> = { "*": "contains", "^": "prefix", "~": "like", "=": "equals" };
const QUALIFIER = /^(kind|in|tag|st|cat):(.*)$/i;

const emptyQualifiers = (): Qualifiers => ({ kind: [], in: [], tag: [], st: [], cat: [] });

/** Splits on white space, keeping double-quoted runs together (the quotes are removed). */
export function tokens(text: string): string[] {
  const out: string[] = [];
  let cur = "";
  let quoted = false;
  let any = false;
  for (const ch of text) {
    if (ch === '"') {
      quoted = !quoted;
      any = true;
    } else if (!quoted && /\s/.test(ch)) {
      if (any) out.push(cur);
      cur = "";
      any = false;
    } else {
      cur += ch;
      any = true;
    }
  }
  if (any) out.push(cur);
  return out;
}

export function parseQuery(text: string): SearchQuery {
  const q = emptyQualifiers();
  const words: string[] = [];
  for (const token of tokens(text)) {
    const m = QUALIFIER.exec(token);
    if (m && m[2]) q[m[1].toLowerCase() as QualifierKey].push(m[2].toLowerCase());
    else if (!m) words.push(token);
  }
  let term = words.join(" ").toLowerCase();
  let op: Operator = "contains";
  let explicit = false;
  const first = term.charAt(0);
  if (first in OPERATORS) {
    op = OPERATORS[first];
    explicit = true;
    term = term.slice(1).trimStart();
  }
  return { term, op, explicit, q };
}

/** Whether the query asks for anything: a term or a qualifier. */
export function isEmptyQuery(query: SearchQuery): boolean {
  return !query.term && QUALIFIER_KEYS.every((k) => query.q[k].length === 0);
}

/** A predicate over lower-cased text for the query's term and operator; undefined when there is no term. */
export function termMatcher(query: Pick<SearchQuery, "term" | "op">): ((lower: string) => boolean) | undefined {
  const { term, op } = query;
  if (!term) return undefined;
  switch (op) {
    case "prefix":
      return (s) => s.startsWith(term);
    case "equals":
      return (s) => s === term;
    case "like": {
      const source = term
        .split("%")
        .map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"))
        .join(".*");
      const re = new RegExp(`^${source}$`, "s");
      return (s) => re.test(s);
    }
    default:
      return (s) => s.includes(term);
  }
}

/** The span of `label` the term matched, for highlighting; undefined when it cannot be told (like) or none. */
export function matchRange(label: string, query: Pick<SearchQuery, "term" | "op">): [number, number] | undefined {
  const { term, op } = query;
  if (!term || op === "like") return undefined;
  const lower = label.toLowerCase();
  if (op === "equals") return lower === term ? [0, label.length] : undefined;
  if (op === "prefix") return lower.startsWith(term) ? [0, term.length] : undefined;
  const at = lower.indexOf(term);
  return at < 0 ? undefined : [at, at + term.length];
}

export interface QueryChip {
  key: QualifierKey;
  value: string;
  label: string;
}

const CHIP_LABELS: Record<QualifierKey, string> = { kind: "Kind", in: "In", tag: "Tag", st: "Stereotype", cat: "Category" };

/** The qualifiers typed in the box, as filter chips (3.2), with the value as typed. */
export function chipsOf(text: string): QueryChip[] {
  const out: QueryChip[] = [];
  for (const token of tokens(text)) {
    const m = QUALIFIER.exec(token);
    if (!m || !m[2]) continue;
    const key = m[1].toLowerCase() as QualifierKey;
    out.push({ key, value: m[2], label: `${CHIP_LABELS[key]}: ${m[2]}` });
  }
  return out;
}

/** The text without one qualifier (a chip's remove button). */
export function withoutQualifier(text: string, key: QualifierKey, value: string): string {
  const out: string[] = [];
  let removed = false;
  for (const token of tokens(text)) {
    const m = QUALIFIER.exec(token);
    if (!removed && m && m[1].toLowerCase() === key && m[2] === value) {
      removed = true;
      continue;
    }
    out.push(/\s/.test(token) ? (m ? `${m[1]}:"${m[2]}"` : `"${token}"`) : token);
  }
  return out.join(" ");
}
