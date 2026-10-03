// Identifiers in hand-written SQL (a check's expression, an index's filter, a computed column, a default, a view's body, a
// routine's or SQL object's text), token aware and per dialect: string literals and comments are skipped (MySQL's backslash
// escapes and "…" strings, PostgreSQL's E'…' and $$…$$ strings, SQL Server's [a]]b] identifiers); an unquoted word (letters
// of any script) matches a name whatever its case, as the databases fold unquoted names; a quoted one ("x", [x], `x`) only
// exactly. Each identifier gets a role from where it stands: a column (bare or after a qualifier, `o.customer`), a qualifier
// (`customer.id`), an object (after FROM, JOIN, UPDATE, INTO or TABLE, or in a FROM list), an alias (after AS, or after an
// object), a type (after ::) or a call (before "("). A rename rewrites only columns; where the reading is not certain (a name
// that is also an SQL keyword, or one the text also uses as a table, an alias or a qualifier while some use of it stands
// bare) nothing is rewritten and the caller lists the place to check.

/** Words an SQL text uses as keywords or type names: a column with one of these names is never rewritten blindly. */
const KEYWORDS = new Set(
  (
    "all and any as asc between bigint binary bit blob boolean by case cast char character check collate column constraint " +
    "create cross current_date current_time current_timestamp date datetime day decimal default delete desc distinct double " +
    "else end except exists false fetch float from full group having hour ilike in index inner insert int integer intersect " +
    "interval into is join key lateral left like limit minute month natural not null numeric offset on or order outer primary " +
    "over partition percent real references returning right second select set smallint table text then time timestamp to top " +
    "true union unique update user using uuid value values varchar view when where window with year zone"
  ).split(" "),
);

/** The words that give a statement its structure: never an identifier when unquoted. All of them are in KEYWORDS. */
const STRUCTURE = new Set(
  (
    "all and as asc between by case collate cross delete desc distinct else end except exists fetch from full group having " +
    "ilike in inner insert intersect into is join lateral left like limit natural not null offset on or order outer over " +
    "partition percent returning right select set table then top union update using values view when where window with"
  ).split(" "),
);

/** After these, a name is a table or view (an object), not a column. */
const OBJECT_BEFORE = new Set(["from", "join", "update", "into", "table", "view"]);
/** These end a FROM list. */
const FROM_ENDS = new Set([
  "where",
  "group",
  "order",
  "having",
  "limit",
  "offset",
  "union",
  "except",
  "intersect",
  "window",
  "returning",
  "set",
  "values",
  "fetch",
]);

/** Whether a name is also an SQL keyword or type name (renaming it in SQL text is not certain). */
export const isSqlKeyword = (name: string): boolean => KEYWORDS.has(name.toLowerCase());

export type Role = "column" | "qualifier" | "object" | "alias" | "type" | "call" | "keyword";

interface Token {
  kind: "word" | "quoted" | "string" | "comment" | "space" | "number" | "other";
  text: string;
  /** A word's or quoted identifier's name. */
  value: string;
  open?: string;
  close?: string;
  role?: Role;
  /** A column after a qualifier (`o.customer`). */
  qualified?: boolean;
}

const WORD_START = /[\p{L}\p{M}_]/u;
const WORD_PART = /[\p{L}\p{M}\p{N}_$]/u;

/** The dialect a text of a per-dialect map is written in ("*": the database's). */
const dialectOf = (key: string, fallback: string | undefined) => (key === "*" ? fallback : key);

function tokenize(sql: string, dialect?: string): Token[] {
  const out: Token[] = [];
  const mysql = dialect === "mysql";
  const brackets = !dialect || dialect === "sqlserver" || dialect === "sqlite";
  const n = sql.length;
  let i = 0;
  // A string from `at` (its opening quote): '' or the quote doubled escapes it, and a backslash too when `backslash`.
  const stringEnd = (at: number, quote: string, backslash: boolean) => {
    let j = at + 1;
    while (j < n) {
      if (backslash && sql[j] === "\\") j += 2;
      else if (sql[j] === quote && sql[j + 1] === quote) j += 2;
      else if (sql[j] === quote) return j + 1;
      else j++;
    }
    return n;
  };
  while (i < n) {
    const c = sql[i];
    if (/\s/.test(c)) {
      let j = i + 1;
      while (j < n && /\s/.test(sql[j])) j++;
      out.push({ kind: "space", text: sql.slice(i, j), value: "" });
      i = j;
      continue;
    }
    if ((c === "-" && sql[i + 1] === "-") || (mysql && c === "#")) {
      const j = sql.indexOf("\n", i);
      const end = j < 0 ? n : j;
      out.push({ kind: "comment", text: sql.slice(i, end), value: "" });
      i = end;
      continue;
    }
    if (c === "/" && sql[i + 1] === "*") {
      const j = sql.indexOf("*/", i + 2);
      const end = j < 0 ? n : j + 2;
      out.push({ kind: "comment", text: sql.slice(i, end), value: "" });
      i = end;
      continue;
    }
    if (c === "'" || (mysql && c === '"')) {
      const end = stringEnd(i, c, mysql);
      out.push({ kind: "string", text: sql.slice(i, end), value: "" });
      i = end;
      continue;
    }
    // PostgreSQL's E'…' (backslash escapes), N'…', X'…', B'…' prefixes.
    if (/[EeNnXxBb]/.test(c) && sql[i + 1] === "'" && !(i > 0 && WORD_PART.test(sql[i - 1]))) {
      const end = stringEnd(i + 1, "'", mysql || ((c === "E" || c === "e") && dialect !== "sqlserver"));
      out.push({ kind: "string", text: sql.slice(i, end), value: "" });
      i = end;
      continue;
    }
    // PostgreSQL's dollar quoting: $$…$$ or $tag$…$tag$.
    if (c === "$" && (!dialect || dialect === "postgresql")) {
      const m = /^\$([A-Za-z_][A-Za-z0-9_]*)?\$/.exec(sql.slice(i));
      if (m) {
        const close = sql.indexOf(m[0], i + m[0].length);
        const end = close < 0 ? n : close + m[0].length;
        out.push({ kind: "string", text: sql.slice(i, end), value: "" });
        i = end;
        continue;
      }
    }
    const close = c === '"' ? '"' : c === "`" ? "`" : c === "[" && brackets ? "]" : null;
    if (close) {
      let j = i + 1;
      let value = "";
      while (j < n) {
        if (sql[j] === close && sql[j + 1] === close) {
          value += close;
          j += 2;
        } else if (sql[j] === close) break;
        else value += sql[j++];
      }
      out.push({ kind: "quoted", text: sql.slice(i, j + 1), value, open: c, close });
      i = j + 1;
      continue;
    }
    // A variable (@x, @@x) or a temporary table (#x) of SQL Server, a bind parameter (:x): not an identifier of the model.
    if ((c === "@" || (c === "#" && !mysql) || (c === ":" && sql[i + 1] !== ":")) && i + 1 < n && (WORD_START.test(sql[i + 1]) || sql[i + 1] === "@")) {
      let j = i + 1;
      while (j < n && (WORD_PART.test(sql[j]) || sql[j] === "@")) j++;
      out.push({ kind: "other", text: sql.slice(i, j), value: "" });
      i = j;
      continue;
    }
    if (WORD_START.test(c)) {
      let j = i + 1;
      while (j < n && WORD_PART.test(sql[j])) j++;
      out.push({ kind: "word", text: sql.slice(i, j), value: sql.slice(i, j) });
      i = j;
      continue;
    }
    if (/[0-9]/.test(c)) {
      const m = /^[0-9]+(\.[0-9]*)?([eE][+-]?[0-9]+)?/.exec(sql.slice(i))!;
      out.push({ kind: "number", text: m[0], value: "" });
      i += m[0].length;
      continue;
    }
    if (c === ":" && sql[i + 1] === ":") {
      out.push({ kind: "other", text: "::", value: "" });
      i += 2;
      continue;
    }
    out.push({ kind: "other", text: c, value: "" });
    i++;
  }
  return out;
}

const isName = (t: Token | undefined): boolean => !!t && (t.kind === "quoted" || (t.kind === "word" && !STRUCTURE.has(t.value.toLowerCase())));
const lower = (t: Token | undefined) => (t?.kind === "word" ? t.value.toLowerCase() : (t?.text ?? ""));

/** The tokens with a role for each identifier (see the head of this file). */
function classify(sql: string, dialect?: string): Token[] {
  const all = tokenize(sql, dialect);
  const sig = all.filter((t) => t.kind !== "space" && t.kind !== "comment");
  // Per parenthesis depth: whether a FROM list is open there.
  const fromAt: boolean[] = [false];
  // Per depth: whether the parentheses are a call's arguments (EXTRACT(YEAR FROM x), SUBSTRING(x FROM 2): no table there).
  const callAt: boolean[] = [false];
  let depth = 0;
  let objectNext = false;
  for (let k = 0; k < sig.length; k++) {
    const t = sig[k];
    const word = lower(t);
    if (t.kind === "word" && STRUCTURE.has(word)) {
      t.role = "keyword";
      // IS [NOT] DISTINCT FROM compares, and a call's FROM is an argument: neither names a table.
      const comparing = word === "from" && lower(sig[k - 1]) === "distinct";
      if (OBJECT_BEFORE.has(word) && !callAt[depth] && !comparing) objectNext = true;
      if (word === "from" && !callAt[depth] && !comparing) fromAt[depth] = true;
      else if (FROM_ENDS.has(word)) fromAt[depth] = false;
      continue;
    }
    if (t.text === "(") {
      depth++;
      fromAt[depth] = false;
      callAt[depth] = sig[k - 1]?.role === "call";
      objectNext = false;
      continue;
    }
    if (t.text === ")") {
      fromAt[depth] = false;
      depth = Math.max(0, depth - 1);
      continue;
    }
    if (t.text === "," && fromAt[depth]) {
      objectNext = true;
      continue;
    }
    if (!isName(t)) {
      objectNext = false;
      continue;
    }
    // A chain: name (. name)* (. *)?
    const chain = [k];
    let end = k;
    while (sig[end + 1]?.text === "." && isName(sig[end + 2])) {
      end += 2;
      chain.push(end);
    }
    const star = sig[end + 1]?.text === "." && sig[end + 2]?.text === "*";
    const next = sig[star ? end + 3 : end + 1];
    const prev = sig[k - 1];
    // Two operands with nothing between: the second names the first (`customers c`, `count(*) n`, `email e`, `CASE … END x`).
    const afterOperand =
      !!prev &&
      (prev.text === ")" ||
        prev.kind === "string" ||
        prev.kind === "number" ||
        lower(prev) === "end" ||
        (isName(prev) && prev.role !== "call" && prev.role !== "qualifier"));
    if (objectNext) {
      for (const at of chain) sig[at].role = "object";
    } else if (next?.text === "(") {
      chain.forEach((at, i) => (sig[at].role = i === chain.length - 1 ? "call" : "qualifier"));
    } else if (prev && lower(prev) === "as") {
      for (const at of chain) sig[at].role = "alias";
    } else if (prev?.text === "::") {
      for (const at of chain) sig[at].role = "type";
    } else if (afterOperand && chain.length === 1 && !star) {
      sig[k].role = "alias";
    } else if (star) {
      for (const at of chain) sig[at].role = "qualifier";
    } else {
      chain.forEach((at, i) => {
        sig[at].role = i === chain.length - 1 ? "column" : "qualifier";
        if (i === chain.length - 1 && chain.length > 1) sig[at].qualified = true;
      });
    }
    objectNext = false;
    k = star ? end + 2 : end;
  }
  return all;
}

const sameName = (t: Token, name: string) => (t.kind === "word" ? t.value.toLowerCase() === name.toLowerCase() : t.kind === "quoted" && t.value === name);

/** Whether SQL text names `name` (a column, a table) outside string literals and comments, and not as a function call. */
export function mentions(sql: string, name: string, dialect?: string): boolean {
  return classify(sql, dialect).some((t) => (t.kind === "word" || t.kind === "quoted") && sameName(t, name) && t.role !== "call");
}

/** Whether SQL text reads `name` as a column (a name that is also a keyword counts wherever it stands: it may be one). */
export function mentionsColumn(sql: string, name: string, dialect?: string): boolean {
  const keyword = isSqlKeyword(name);
  return classify(sql, dialect).some(
    (t) => (t.kind === "word" || t.kind === "quoted") && sameName(t, name) && (t.role === "column" || (keyword && t.role === "keyword")),
  );
}

/** The names of `candidates` that SQL text mentions. */
export function mentioned(sql: string, candidates: readonly string[], dialect?: string): string[] {
  return candidates.filter((c) => mentions(sql, c, dialect));
}

/** Why a rewrite was left undone: a keyword name, a name the text also uses for a table or an alias, every column through `*`. */
export type Uncertainty = "keyword" | "ambiguous" | "star" | "output";

export interface Rewrite {
  text: string;
  count: number;
  uncertain: boolean;
  why?: Uncertainty;
}

/** `to` written the way `like` is (quoted the same way, its closing quote doubled inside). */
function written(like: Token, to: string): string {
  if (like.kind !== "quoted") return to;
  const close = like.close!;
  return `${like.open}${to.split(close).join(close + close)}${close}`;
}

/** One item of the first top-level select list: its tokens' range in the token list, and what names its output column. */
interface SelectItem {
  /** Indexes of its significant tokens. */
  tokens: number[];
  /** The alias token (AS x, or an implicit alias). */
  alias?: number;
  /** The bare column token, when the item is only a column (`email`, `c.email`). */
  bare?: number;
  star: boolean;
}

/** The items of the first SELECT at depth 0 (the view's output columns), or null when there is none. */
function selectItems(list: readonly Token[]): SelectItem[] | null {
  const sig: number[] = [];
  list.forEach((t, i) => {
    if (t.kind !== "space" && t.kind !== "comment") sig.push(i);
  });
  let depth = 0;
  let start = -1;
  for (let s = 0; s < sig.length; s++) {
    const t = list[sig[s]];
    if (t.text === "(") depth++;
    else if (t.text === ")") depth--;
    else if (depth === 0 && t.kind === "word" && t.value.toLowerCase() === "select") {
      start = s + 1;
      break;
    }
  }
  if (start < 0) return null;
  // DISTINCT [ON (…)], ALL, TOP n [PERCENT]: not items.
  for (;;) {
    const w = lower(list[sig[start]]);
    if (w === "distinct" || w === "all") {
      start++;
      if (lower(list[sig[start]]) === "on" && list[sig[start + 1]]?.text === "(") {
        let d = 0;
        let s = start + 1;
        for (; s < sig.length; s++) {
          if (list[sig[s]].text === "(") d++;
          else if (list[sig[s]].text === ")" && --d === 0) break;
        }
        start = s + 1;
      }
    } else if (w === "top") {
      start += 2;
      if (lower(list[sig[start]]) === "percent") start++;
    } else break;
  }
  const items: SelectItem[] = [];
  let current: number[] = [];
  depth = 0;
  const finish = () => {
    if (!current.length) return;
    const toks = current.map((i) => list[i]);
    const star = toks[toks.length - 1].text === "*" && (toks.length === 1 || toks[toks.length - 2].text === ".");
    const item: SelectItem = { tokens: current, star };
    const last = toks.length - 1;
    const isId = (t: Token) => t.kind === "quoted" || (t.kind === "word" && !STRUCTURE.has(t.value.toLowerCase()));
    if (last >= 1 && toks[last].role === "alias") item.alias = current[last];
    else if (toks.every((t, i) => (i % 2 === 0 ? isId(t) : t.text === "."))) item.bare = current[last];
    items.push(item);
    current = [];
  };
  for (let s = start; s < sig.length; s++) {
    const t = list[sig[s]];
    if (t.text === "(") depth++;
    else if (t.text === ")") {
      if (depth === 0) break;
      depth--;
    } else if (
      depth === 0 &&
      t.kind === "word" &&
      ["from", "into", "where", "union", "except", "intersect", "order", "group", "limit"].includes(t.value.toLowerCase())
    )
      break;
    if (depth === 0 && t.text === ",") {
      finish();
      continue;
    }
    current.push(sig[s]);
  }
  finish();
  return items;
}

/** Whether the first select list of SQL text has a `*` item (every column of a source, under the source's names). */
export function selectsAll(sql: string, dialect?: string): boolean {
  return !!selectItems(classify(sql, dialect))?.some((i) => i.star);
}

/** Whether any text of a per-dialect map selects `*` ("*": in `dialect`). */
export function mapSelectsAll(map: unknown, dialect?: string): boolean {
  return (
    !!map &&
    typeof map === "object" &&
    Object.entries(map as Record<string, unknown>).some(([key, t]) => typeof t === "string" && selectsAll(t, dialectOf(key, dialect)))
  );
}

/**
 * SQL text with column `from` renamed `to`, quoted forms kept quoted the same way; `count` is how many were rewritten. Only
 * columns are rewritten (never a table, an alias, a qualifier, a type or a call of the same name). Nothing is rewritten when
 * the reading is not certain (`uncertain`, with `why`): `from` is also an SQL keyword, or the text also uses it as a table, an
 * alias or a qualifier while some use of it stands bare. With `keepOutput` (a view whose body names its output columns), a
 * select item that is only the column (`email`, `c.email`) becomes `mail AS email`, so the view's column keeps its name; a
 * `*` item makes it uncertain (`star`: the view's column would be renamed).
 */
export function renameIdentifier(sql: string, from: string, to: string, options: { dialect?: string; keepOutput?: boolean } = {}): Rewrite {
  const none: Rewrite = { text: sql, count: 0, uncertain: false };
  if (!sql || from === to) return none;
  const list = classify(sql, options.dialect);
  const names = list.filter((t) => (t.kind === "word" || t.kind === "quoted") && sameName(t, from));
  const columns = names.filter((t) => t.role === "column");
  if (isSqlKeyword(from) && names.some((t) => t.role !== "call")) return { ...none, uncertain: true, why: "keyword" };
  const items = options.keepOutput ? selectItems(list) : null;
  if (!columns.length) return none;
  const elsewhere = names.some((t) => t.role === "object" || t.role === "alias" || t.role === "qualifier");
  if (elsewhere && columns.some((t) => !t.qualified)) return { ...none, uncertain: true, why: "ambiguous" };
  if (options.keepOutput && items?.some((i) => i.star)) return { ...none, uncertain: true, why: "star" };
  const keep = new Set<Token>();
  if (items) for (const item of items) if (item.bare !== undefined && columns.includes(list[item.bare])) keep.add(list[item.bare]);
  const target = new Set(columns);
  let count = 0;
  const text = list
    .map((t) => {
      if (!target.has(t)) return t.text;
      count++;
      return keep.has(t) ? `${written(t, to)} AS ${t.text}` : written(t, to);
    })
    .join("");
  return { text, count, uncertain: false };
}

/**
 * A view's body with its output column `from` renamed `to` (the view's declared column renamed while CREATE VIEW names no
 * column list): the alias that names it is renamed, or a bare column item gets `AS to`. Uncertain when no item of the first
 * select list names that column (a `*`, an expression without an alias, a body that is not a SELECT).
 */
export function renameOutputColumn(sql: string, from: string, to: string, dialect?: string): Rewrite {
  const none: Rewrite = { text: sql, count: 0, uncertain: false };
  if (!sql || from === to) return none;
  const list = classify(sql, dialect);
  const items = selectItems(list);
  const hit = items?.find((i) => {
    const at = i.alias ?? i.bare;
    return at !== undefined && sameName(list[at], from);
  });
  if (!hit) return { ...none, uncertain: true, why: "output" };
  const text = list.map((t, i) => (i === hit.alias ? written(t, to) : i === hit.bare ? `${t.text} AS ${written(t, to)}` : t.text)).join("");
  return { text, count: 1, uncertain: false };
}

/** A per-dialect text map (`{ "postgresql": "...", "*": "..." }`) with an identifier renamed in each text ("*": `dialect`'s). */
export function renameInDialectMap(
  map: unknown,
  from: string,
  to: string,
  options: { dialect?: string; keepOutput?: boolean } = {},
): { map: Record<string, string> | undefined; count: number; uncertain: boolean; why?: Uncertainty } {
  if (!map || typeof map !== "object") return { map: undefined, count: 0, uncertain: false };
  const out: Record<string, string> = {};
  let count = 0;
  let uncertain = false;
  let why: Uncertainty | undefined;
  for (const [key, text] of Object.entries(map as Record<string, unknown>)) {
    if (typeof text !== "string") continue;
    const r = renameIdentifier(text, from, to, { ...options, dialect: dialectOf(key, options.dialect) });
    out[key] = r.text;
    count += r.count;
    if (r.uncertain) {
      uncertain = true;
      why ??= r.why;
    }
  }
  return { map: out, count, uncertain, why };
}

/** A view body map with its output column renamed in each text (renameOutputColumn). */
export function renameOutputInDialectMap(
  map: unknown,
  from: string,
  to: string,
  dialect?: string,
): { map: Record<string, string> | undefined; uncertain: boolean } {
  if (!map || typeof map !== "object") return { map: undefined, uncertain: true };
  const out: Record<string, string> = {};
  let uncertain = false;
  for (const [key, text] of Object.entries(map as Record<string, unknown>)) {
    if (typeof text !== "string") continue;
    const r = renameOutputColumn(text, from, to, dialectOf(key, dialect));
    out[key] = r.text;
    uncertain ||= r.uncertain;
  }
  return { map: out, uncertain };
}

/** Whether any text of a per-dialect map mentions a name ("*": in `dialect`). */
export function mapMentions(map: unknown, name: string, dialect?: string): boolean {
  return (
    !!map &&
    typeof map === "object" &&
    Object.entries(map as Record<string, unknown>).some(([key, t]) => typeof t === "string" && mentions(t, name, dialectOf(key, dialect)))
  );
}

/** Whether any text of a per-dialect map reads a name as a column ("*": in `dialect`). */
export function mapMentionsColumn(map: unknown, name: string, dialect?: string): boolean {
  return (
    !!map &&
    typeof map === "object" &&
    Object.entries(map as Record<string, unknown>).some(([key, t]) => typeof t === "string" && mentionsColumn(t, name, dialectOf(key, dialect)))
  );
}
