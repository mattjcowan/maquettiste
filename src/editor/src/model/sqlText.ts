// The light reading of a query's SQL text: never a parse. A copy of the engine's Rendering/SqlText.cs; both read the shared
// vectors in tests/fixtures/sql-text/scanner.json. Per dialect it skips comments, string literals and quoted identifiers and
// reports four things only: the :name placeholders with their offsets, the first word and the statement's verb, the names that
// appear, and whether ORDER BY and OFFSET, FETCH or TOP appear outside parentheses.

export type SqlDialect = "postgresql" | "sqlserver" | "mysql" | "sqlite" | "oracle";

export interface SqlPlaceholder {
  name: string;
  /** The offset of the colon, in UTF-16 code units from 0. */
  offset: number;
  /** The length with the colon. */
  length: number;
}

export interface SqlTextScan {
  placeholders: SqlPlaceholder[];
  firstWord: string | null;
  verb: string | null;
  names: string[];
  orderBy: boolean;
  offsetFetchOrTop: boolean;
  /** The offset of a `;` at parenthesis depth 0 that is the last token (only white space and comments follow it), or null. */
  finalSemicolon: number | null;
}

const STATEMENT_VERBS = new Set(["SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "VALUES", "TABLE"]);

const isLetter = (c: string): boolean => /\p{L}/u.test(c);
const isDigit = (c: string): boolean => /\p{Nd}/u.test(c);
const isLetterOrDigit = (c: string): boolean => isLetter(c) || isDigit(c);
const isAsciiLetter = (c: string): boolean => /^[A-Za-z]$/.test(c);
const isAsciiDigit = (c: string): boolean => /^[0-9]$/.test(c);
// .NET's char.IsWhiteSpace set plus U+FEFF (a leading byte order mark counts as white space; JavaScript's \s lacks U+0085).
const WHITE = /[\t-\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\uFEFF]/;
const isWhite = (c: string): boolean => WHITE.test(c);

/** Removes white space (the scanner's set) at both ends. */
export function trimSqlWhite(text: string): string {
  let a = 0;
  let b = text.length;
  while (a < b && isWhite(text[a])) a++;
  while (b > a && isWhite(text[b - 1])) b--;
  return text.slice(a, b);
}

/** Upper-cases like .NET's invariant casing for what matters here: ASCII keywords; a letter that maps to several characters or to `I` (ı) stays. */
const upperWord = (w: string): string =>
  Array.from(w, (c) => {
    const u = c.toUpperCase();
    return u.length === c.length && c !== "\u0131" ? u : c;
  }).join("");

/** Reads `text` as SQL of `dialect`. */
export function scanSqlText(text: string, dialect: SqlDialect): SqlTextScan {
  const n = text.length;
  const placeholders: SqlPlaceholder[] = [];
  const names: string[] = [];
  const seen = new Set<string>();
  let i = 0;
  let depth = 0;
  let started = false;
  let firstWord: string | null = null;
  let firstDepth = 0;
  let verb: string | null = null;
  let verbFound = false;
  let orderPending = false;
  let orderBy = false;
  let bounded = false;
  let semicolon = -1;

  const other = (): void => {
    started = true;
    orderPending = false;
  };
  const addName = (name: string): void => {
    if (name.length > 0 && !seen.has(name)) {
      seen.add(name);
      names.push(name);
    }
  };
  const isWordStart = (c: string): boolean => isLetter(c) || c === "_" || (c === "#" && dialect === "sqlserver");
  const isWordPart = (c: string): boolean => isLetterOrDigit(c) || c === "_" || c === "$" || (c === "#" && (dialect === "sqlserver" || dialect === "oracle"));

  const skipLine = (): void => {
    while (i < n && text[i] !== "\n") i++;
  };
  const skipBlockComment = (nested: boolean): void => {
    i += 2;
    let level = 1;
    while (i < n && level > 0) {
      if (nested && text[i] === "/" && text[i + 1] === "*") {
        level++;
        i += 2;
      } else if (text[i] === "*" && text[i + 1] === "/") {
        level--;
        i += 2;
      } else {
        i++;
      }
    }
  };
  const skipString = (from: number, quote: string, backslash: boolean): void => {
    i = from;
    while (i < n) {
      const c = text[i];
      if (backslash && c === "\\") {
        i += 2;
      } else if (c === quote) {
        if (text[i + 1] === quote) {
          i += 2;
        } else {
          i++;
          return;
        }
      } else {
        i++;
      }
    }
    i = Math.min(i, n);
  };
  const quotedName = (close: string, doubled: boolean): void => {
    i++;
    let name = "";
    while (i < n) {
      const c = text[i];
      if (c === close) {
        if (doubled && text[i + 1] === close) {
          name += close;
          i += 2;
          continue;
        }
        i++;
        break;
      }
      name += c;
      i++;
    }
    addName(name);
    other();
  };
  const tryDollarQuote = (): boolean => {
    let j = i + 1;
    if (j < n && (isLetter(text[j]) || text[j] === "_")) {
      while (j < n && (isLetterOrDigit(text[j]) || text[j] === "_")) j++;
    }
    if (j >= n || text[j] !== "$") return false;
    const tag = text.slice(i, j + 1);
    const end = text.indexOf(tag, j + 1);
    i = end < 0 ? n : end + tag.length;
    return true;
  };
  const colon = (): void => {
    const at = i;
    if (text[at + 1] === ":") {
      while (i < n && text[i] === ":") i++;
      other();
      return;
    }
    i++;
    const before = at > 0 ? text[at - 1] : " ";
    const after = at + 1 < n ? text[at + 1] : "\0";
    if (
      isLetterOrDigit(before) ||
      before === "_" ||
      before === "]" ||
      before === ")" ||
      before === "'" ||
      before === '"' ||
      before === "`" ||
      !(isAsciiLetter(after) || after === "_")
    ) {
      other();
      return;
    }
    let end = at + 1;
    while (end < n && (isAsciiLetter(text[end]) || isAsciiDigit(text[end]) || text[end] === "_")) end++;
    placeholders.push({ name: text.slice(at + 1, end), offset: at, length: end - at });
    i = end;
    other();
  };
  const skipQQuote = (): void => {
    const open = text[i + 1];
    const close = open === "[" ? "]" : open === "{" ? "}" : open === "(" ? ")" : open === "<" ? ">" : open;
    let j = i + 2;
    while (j < n && !(text[j] === close && text[j + 1] === "'")) j++;
    i = Math.min(j + 2, n);
  };
  const word = (): void => {
    const start = i;
    i++;
    while (i < n && isWordPart(text[i])) i++;
    const w = text.slice(start, i);
    if (i < n && text[i] === "'") {
      const upper = upperWord(w);
      if (dialect === "oracle" && (upper === "Q" || upper === "NQ") && i + 1 < n) skipQQuote();
      else skipString(i + 1, "'", dialect === "mysql" || (dialect === "postgresql" && upper === "E"));
      other();
      return;
    }
    addName(w);
    const keyword = upperWord(w);
    if (!started) {
      started = true;
      firstWord = keyword;
      firstDepth = depth;
    } else if (firstWord === "WITH" && !verbFound && depth === firstDepth && STATEMENT_VERBS.has(keyword)) {
      verb = keyword;
      verbFound = true;
    }
    if (depth === 0) {
      if (orderPending && keyword === "BY") orderBy = true;
      if (keyword === "OFFSET" || keyword === "FETCH" || keyword === "TOP") bounded = true;
      orderPending = keyword === "ORDER";
    } else {
      orderPending = false;
    }
  };

  while (i < n) {
    const c = text[i];
    const next = i + 1 < n ? text[i + 1] : "\0";
    const comment = (c === "-" && next === "-") || (c === "#" && dialect === "mysql") || (c === "/" && next === "*");
    if (!comment && !isWhite(c)) semicolon = c === ";" && depth === 0 ? i : -1;
    if (isWhite(c)) {
      i++;
    } else if (c === "-" && next === "-") {
      skipLine();
    } else if (c === "#" && dialect === "mysql") {
      skipLine();
    } else if (c === "/" && next === "*") {
      skipBlockComment(dialect === "postgresql" || dialect === "sqlserver");
    } else if (c === "'") {
      skipString(i + 1, "'", dialect === "mysql");
      other();
    } else if (c === '"') {
      if (dialect === "mysql") {
        skipString(i + 1, '"', true);
        other();
      } else {
        quotedName('"', true);
      }
    } else if (c === "`" && (dialect === "mysql" || dialect === "sqlite")) {
      quotedName("`", true);
    } else if (c === "[" && (dialect === "sqlserver" || dialect === "sqlite")) {
      quotedName("]", dialect === "sqlserver");
    } else if (c === "$" && dialect === "postgresql" && tryDollarQuote()) {
      other();
    } else if (c === ":") {
      colon();
    } else if (c === "@") {
      while (i < n && text[i] === "@") i++;
      while (i < n && isWordPart(text[i])) i++;
      other();
    } else if (c === "(") {
      i++;
      if (started) other();
      depth++;
    } else if (c === ")") {
      i++;
      depth = Math.max(0, depth - 1);
      other();
    } else if (isWordStart(c)) {
      word();
    } else if (isAsciiDigit(c)) {
      while (i < n && (isLetterOrDigit(text[i]) || text[i] === "_")) i++;
      other();
    } else {
      i++;
      other();
    }
  }

  return {
    placeholders,
    firstWord,
    verb: firstWord === null ? null : firstWord === "WITH" ? verb : firstWord,
    names,
    orderBy,
    offsetFetchOrTop: bounded,
    finalSemicolon: semicolon < 0 ? null : semicolon,
  };
}

/** The 1-based line and column of an offset; lines end at `\n`. */
export function sqlTextPosition(text: string, offset: number): { line: number; column: number } {
  let line = 1;
  let lineStart = 0;
  for (let i = 0; i < offset && i < text.length; i++) {
    if (text[i] === "\n") {
      line++;
      lineStart = i + 1;
    }
  }
  return { line, column: offset - lineStart + 1 };
}

/** Whether `name` appears among the scan's names, ignoring case (MQ4098 compares table and view names so). */
export function sqlTextMentions(scan: SqlTextScan, name: string): boolean {
  const lower = name.toLowerCase();
  return scan.names.some((x) => x.toLowerCase() === lower);
}

const DIALECTS: readonly SqlDialect[] = ["postgresql", "sqlserver", "mysql", "sqlite", "oracle"];

/** The dialect a text's key stands for: its own name, or the database's (`dialect`) for `*` or any other key. */
export function sqlDialectOf(key: string, dialect: string): SqlDialect {
  const own = DIALECTS.find((d) => d === key.toLowerCase());
  if (own) return own;
  return DIALECTS.find((d) => d === dialect.toLowerCase()) ?? "postgresql";
}

/** The text with each `:from` placeholder written `:to`. */
export function renameSqlPlaceholder(text: string, dialect: SqlDialect, from: string, to: string): string {
  let out = "";
  let at = 0;
  for (const p of scanSqlText(text, dialect).placeholders) {
    if (p.name !== from) continue;
    out += text.slice(at, p.offset) + ":" + to;
    at = p.offset + p.length;
  }
  return out + text.slice(at);
}

/** MQ4024's message: a placeholder that names no parameter (the editor's hover, the mock's finding; the engine's catalog is the model). */
export const sqlTextNamesNoParameter = (query: string, name: string, line: number, column: number, text: string): string =>
  `Query '${query}': ':${name}' (line ${line}, column ${column} of the ${text} text) names no parameter.`;

/**
 * The text as a derived table or other wrapper needs it: white space trimmed, and one final `;` removed (top level, outside comments
 * and strings, also when comments or white space follow it) together with those trailing comments. Else the trimmed text.
 */
export function unwrapSqlText(text: string, dialect: SqlDialect): string {
  const trimmed = trimSqlWhite(text);
  const at = scanSqlText(trimmed, dialect).finalSemicolon;
  return at === null ? trimmed : trimSqlWhite(trimmed.slice(0, at));
}
