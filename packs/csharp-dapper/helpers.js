// Helpers for the csharp-dapper pack. They run in the Maquettiste sandbox: no CLR, no files, no clock. Every helper is a pure
// function of its arguments (D12), so output does not depend on the order units render in.

// Joins path segments with "/", skipping empty ones, so an empty folder parameter adds nothing.
maquettiste.helper("join_path", (...parts) =>
  parts.filter((p) => p !== null && p !== undefined && String(p) !== "").map((p) => String(p)).join("/"));

// The folder of an element's package ("Billing/Catalog" for package Catalog inside Billing); "" at the root.
maquettiste.helper("package_folder", (element) =>
  element.package ? element.package.qualifiedName.split(".").join("/") : "");

const keywords = new Set([
  "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
  "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed",
  "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
  "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref",
  "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
  "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
]);

// A C# identifier: keywords get the verbatim "@" prefix ("class" -> "@class").
maquettiste.helper("cs_ident", (name) => (keywords.has(name) ? "@" + name : name));

// A class name from a Pascal-cased name: "Q" in front of one that is empty or starts with a digit ("2024Report" -> "Q2024Report"),
// as the engine names a query's class when it checks that two queries of a database do not collide (MQ4040).
maquettiste.helper("cs_class", (name) => (/^[0-9]/.test(String(name)) || String(name) === "" ? "Q" + String(name) : String(name)));

const valueTypes = new Set([
  "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal", "char",
  "DateOnly", "TimeOnly", "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "JsonElement",
]);

// Whether a C# type name from types/csharp.json is a value type (so it needs no "= default!" initializer).
maquettiste.helper("cs_value_type", (type) => valueTypes.has(String(type)));

// ---- Processes ----

// The UUID text of a ULID (26 characters of Crockford base32, 128 bits): a stable instance identity for generated tests.
const crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
maquettiste.helper("ulid_uuid", (ulid) => {
  let bits = "";
  for (const ch of String(ulid).toUpperCase()) {
    const v = crockford.indexOf(ch);
    if (v < 0) return "";
    bits += v.toString(2).padStart(5, "0");
  }
  if (bits.length !== 130) return "";
  bits = bits.slice(2);
  let hex = "";
  for (let i = 0; i < 128; i += 4) hex += parseInt(bits.slice(i, i + 4), 2).toString(16);
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
});

// Guard and action expressions (phase-3-design.md 7.1) are JavaScript for the engine's sandbox. cs_expression translates the
// documented subset to C#: literals (numbers, strings, true, false, null), context.x, event.payload.x, event.name and
// event.actor, comparison (== === != !== < <= > >=), && || !, arithmetic (+ - * / % and unary minus), ?: and parentheses; an
// action is an object literal of context updates, ({ x: expression, ... }). Anything else returns "": the pack then emits a
// stub for the companion, with the expression as a comment. Specs are "name|C# name|C# type|kind|member=C# member,..." joined
// with ";" (kind: enum, string, bool, int, decimal, double, float or value).

function parseSpec(spec) {
  const map = new Map();
  for (const entry of String(spec || "").split(";")) {
    if (!entry) continue;
    const [name, cs, type, kind, members] = entry.split("|");
    const enumMembers = new Map();
    for (const m of (members || "").split(",")) {
      if (!m) continue;
      const [model, member] = m.split("=");
      enumMembers.set(model, member);
    }
    map.set(name, { cs, type, kind, members: enumMembers });
  }
  return map;
}

function tokenize(text) {
  const tokens = [];
  const punct = ["===", "!==", "==", "!=", "<=", ">=", "&&", "||", "(", ")", "{", "}", ",", ":", ".", "?", "!", "<", ">", "+", "-", "*", "/", "%"];
  let i = 0;
  while (i < text.length) {
    const c = text[i];
    if (/\s/.test(c)) { i++; continue; }
    if (/[0-9]/.test(c)) {
      const m = /^[0-9]+(\.[0-9]+)?/.exec(text.slice(i));
      tokens.push({ t: "num", v: m[0], frac: !!m[1] });
      i += m[0].length;
      continue;
    }
    if (c === "'" || c === '"') {
      let j = i + 1;
      let value = "";
      while (j < text.length && text[j] !== c) {
        if (text[j] === "\\") {
          const n = text[j + 1];
          const esc = { n: "\n", t: "\t", r: "\r", "\\": "\\", "'": "'", '"': '"' }[n];
          if (esc === undefined) return null;
          value += esc;
          j += 2;
          continue;
        }
        value += text[j++];
      }
      if (j >= text.length) return null;
      tokens.push({ t: "str", v: value });
      i = j + 1;
      continue;
    }
    if (/[A-Za-z_$]/.test(c)) {
      const m = /^[A-Za-z_$][A-Za-z0-9_$]*/.exec(text.slice(i));
      tokens.push({ t: "id", v: m[0] });
      i += m[0].length;
      continue;
    }
    const p = punct.find((x) => text.startsWith(x, i));
    if (!p) return null;
    // "?." and "??" are outside the subset.
    if (p === "?" && (text[i + 1] === "." || text[i + 1] === "?")) return null;
    tokens.push({ t: "p", v: p });
    i += p.length;
  }
  return tokens;
}

const binaryLevels = [["||"], ["&&"], ["==", "===", "!=", "!=="], ["<", "<=", ">", ">="], ["+", "-"], ["*", "/", "%"]];

function parse(tokens, ctx, payload) {
  let pos = 0;
  const peek = (v) => pos < tokens.length && tokens[pos].t === "p" && tokens[pos].v === v;
  const expect = (v) => {
    if (!peek(v)) throw new Error("expected " + v);
    pos++;
  };
  function primary() {
    const tok = tokens[pos++];
    if (!tok) throw new Error("end");
    if (tok.t === "num") return { t: "num", v: tok.v, frac: tok.frac, kind: "number", nullable: false };
    if (tok.t === "str") return { t: "str", v: tok.v, kind: "string", nullable: false };
    if (tok.t === "p" && tok.v === "(") {
      const e = conditional();
      expect(")");
      return { t: "group", e, kind: e.kind, type: e.type, nullable: e.nullable };
    }
    if (tok.t === "p" && tok.v === "{") return object();
    if (tok.t !== "id") throw new Error("token");
    if (tok.v === "true" || tok.v === "false") return { t: "lit", v: tok.v, kind: "bool", type: "bool", nullable: false };
    if (tok.v === "null") return { t: "lit", v: "null", kind: "null", nullable: true };
    const path = [tok.v];
    while (peek(".")) {
      pos++;
      const next = tokens[pos++];
      if (!next || next.t !== "id") throw new Error("member");
      path.push(next.v);
    }
    if (path[0] === "context" && path.length === 2 && ctx.has(path[1])) {
      const m = ctx.get(path[1]);
      return { t: "ctx", name: path[1], m, kind: m.kind, type: m.type, nullable: /\?$/.test(m.type) };
    }
    if (path[0] === "event" && path.length === 3 && path[1] === "payload" && payload.has(path[2])) {
      const m = payload.get(path[2]);
      return { t: "payload", name: path[2], m, kind: m.kind, type: m.type, nullable: /\?$/.test(m.type) };
    }
    if (path[0] === "event" && path.length === 2 && (path[1] === "name" || path[1] === "actor"))
      return { t: "event", name: path[1], kind: "string", type: "string?", nullable: true };
    throw new Error("name");
  }
  function object() {
    const props = [];
    while (!peek("}")) {
      const key = tokens[pos++];
      if (!key || (key.t !== "id" && key.t !== "str")) throw new Error("key");
      expect(":");
      props.push([key.v, conditional()]);
      if (!peek(",")) break;
      pos++;
    }
    expect("}");
    return { t: "obj", props, kind: "object" };
  }
  function unary() {
    if (peek("!") || peek("-") || peek("+")) {
      const op = tokens[pos++].v;
      const e = unary();
      if (op !== "!" && (e.nullable || !numeric.has(e.kind))) throw new Error("sign of a value that is not a number");
      return { t: "unary", op, e, kind: op === "!" ? "bool" : e.kind, type: op === "!" ? "bool" : e.type, nullable: false };
    }
    return primary();
  }
  function binary(level) {
    if (level >= binaryLevels.length) return unary();
    let left = binary(level + 1);
    while (pos < tokens.length && tokens[pos].t === "p" && binaryLevels[level].includes(tokens[pos].v)) {
      const op = tokens[pos++].v;
      const right = binary(level + 1);
      const logical = level <= 3;
      check(op, left, right);
      left = { t: "bin", op, l: left, r: right, kind: logical ? "bool" : left.kind === "string" || right.kind === "string" ? "string" : numberKind(left, right), nullable: false };
    }
    return left;
  }
  function conditional() {
    const c = binary(0);
    if (!peek("?")) return c;
    pos++;
    const a = conditional();
    expect(":");
    const b = conditional();
    return { t: "cond", c, a, b, kind: a.kind === "number" ? b.kind : a.kind, nullable: a.nullable || b.nullable };
  }
  const tree = conditional();
  if (pos !== tokens.length) throw new Error("trailing");
  return tree;
}

// Where C# and the engine's JavaScript would disagree, the operator is outside the subset (the caller then emits a stub):
// - "/" unless both operands are decimal or double of the same kind, or one is and the other a number literal (C# divides
//   integers without a fraction, JavaScript does not; float divides with less precision);
// - arithmetic and ordering (< <= > >=) on an operand that may be null (JavaScript reads null as 0, C# lifts to null or false),
//   on operands that are not numbers (JavaScript orders strings and coerces mixed kinds, C# does neither), or on decimal mixed
//   with double or float (no implicit conversion in C#);
// - "+" of two strings is allowed (both concatenate the same way); a string with anything else is not;
// - equality between operands of different kinds (strings, numbers, booleans, enum members), decimal with double or float, or a
//   comparison with null of an operand that cannot be null (always false in JavaScript, a warning in C#).
const numeric = new Set(["int", "decimal", "double", "float", "number"]);

function mixesDecimal(l, r) {
  const kinds = new Set([l.kind, r.kind]);
  return kinds.has("decimal") && (kinds.has("double") || kinds.has("float"));
}

function check(op, l, r) {
  if (op === "&&" || op === "||") return;
  if (op === "==" || op === "===" || op === "!=" || op === "!==") {
    if (l.kind === "null" || r.kind === "null") {
      const other = l.kind === "null" ? r : l;
      if (other.kind !== "null" && !other.nullable) throw new Error("null test on a value that cannot be null");
      return;
    }
    if (l.kind === "enum" && r.t === "str") return;
    if (r.kind === "enum" && l.t === "str") return;
    if (numeric.has(l.kind) && numeric.has(r.kind)) {
      if (mixesDecimal(l, r)) throw new Error("decimal compared with double");
      return;
    }
    if (l.kind !== r.kind || (l.kind === "enum" && l.type.replace(/\?$/, "") !== r.type.replace(/\?$/, ""))) throw new Error("equality of different kinds");
    return;
  }
  if (l.nullable || r.nullable) throw new Error("operand that may be null");
  if (op === "+" && l.kind === "string" && r.kind === "string") return;
  if (!numeric.has(l.kind) || !numeric.has(r.kind)) throw new Error("operand that is not a number");
  if (mixesDecimal(l, r)) throw new Error("decimal mixed with double");
  if (op === "/") {
    const exact = (a, b) => (a.kind === "decimal" || a.kind === "double") && (b.kind === a.kind || b.t === "num");
    if (!exact(l, r) && !exact(r, l)) throw new Error("division that C# would truncate or round differently");
  }
}

function numberKind(l, r) {
  for (const k of ["decimal", "double", "float"]) if (l.kind === k || r.kind === k) return k;
  return l.kind === "int" || r.kind === "int" ? "int" : "number";
}

const suffixes = { decimal: "m", double: "d", float: "f" };

function emit(node, hint) {
  switch (node.t) {
    case "num":
      return node.frac ? node.v + (suffixes[hint] || "m") : node.v;
    case "str":
      return JSON.stringify(node.v);
    case "lit":
      return node.v;
    case "ctx":
      return "call.Context." + node.m.cs;
    case "payload":
      return "call.Event.Get<" + node.m.type + ">(" + JSON.stringify(node.name) + ")";
    case "event":
      return node.name === "name" ? "call.Event.Name" : "call.Event.Actor";
    case "group":
      return "(" + emit(node.e, hint) + ")";
    case "unary":
      return node.op === "!" ? "!" + truthy(node.e) : node.op === "-" ? "-" + emit(node.e, hint) : emit(node.e, hint);
    case "cond":
      return "(" + truthy(node.c) + " ? " + emit(node.a, hint) + " : " + emit(node.b, hint) + ")";
    case "bin": {
      if (node.op === "&&" || node.op === "||") return truthy(node.l) + " " + node.op + " " + truthy(node.r);
      const op = node.op === "===" ? "==" : node.op === "!==" ? "!=" : node.op;
      const enumSide = node.l.kind === "enum" ? node.l : node.r.kind === "enum" ? node.r : null;
      if (enumSide && (op === "==" || op === "!=")) {
        const other = enumSide === node.l ? node.r : node.l;
        if (other.t === "str") {
          const member = enumSide.m.members.get(other.v);
          if (!member) throw new Error("enum member");
          const text = enumSide.type.replace(/\?$/, "") + "." + member;
          return enumSide === node.l ? emit(node.l) + " " + op + " " + text : text + " " + op + " " + emit(node.r);
        }
      }
      const numeric = numberKind(node.l, node.r);
      return emit(node.l, numeric) + " " + op + " " + emit(node.r, numeric);
    }
    default:
      throw new Error("object");
  }
}

// A boolean operand: a nullable bool member reads as "== true".
function truthy(node) {
  const text = emit(node, "decimal");
  if ((node.t === "ctx" || node.t === "payload") && node.kind === "bool" && /\?$/.test(node.type)) return "(" + text + " == true)";
  if (node.kind === "bool" || node.t === "group" || node.t === "unary" || node.t === "bin") return text;
  throw new Error("not boolean");
}

// The C# value of an action's update of one context attribute, or an error when C# would not store what JavaScript stores: a
// value that may be null into an attribute that may not, a number with a fraction into an integer, decimal and double mixed, or
// a value of another kind (an enum takes one of its members' names).
function assign(m, value) {
  if (value.nullable && value.kind !== "null" && !/\?$/.test(m.type)) throw new Error("null into a value that cannot be null");
  if (value.kind === "null") {
    if (!/\?$/.test(m.type)) throw new Error("null into a value that cannot be null");
    return "null";
  }
  if (m.kind === "enum") {
    if (value.t === "str") {
      const member = m.members.get(value.v);
      if (!member) throw new Error("enum member");
      return m.type.replace(/\?$/, "") + "." + member;
    }
    if (value.kind === "enum" && value.type.replace(/\?$/, "") === m.type.replace(/\?$/, "")) return emit(value, m.kind);
    throw new Error("not a member of the enum");
  }
  if (numeric.has(m.kind)) {
    if (!numeric.has(value.kind)) throw new Error("not a number");
    if (m.kind === "int" && (value.kind === "decimal" || value.kind === "double" || value.kind === "float" || (value.t === "num" && value.frac)))
      throw new Error("a fraction into an integer");
    if (mixesDecimal(m, value)) throw new Error("decimal mixed with double");
    return emit(value, m.kind);
  }
  if (m.kind !== value.kind) throw new Error("another kind");
  return emit(value, m.kind);
}

maquettiste.helper("cs_expression", (expression, kind, contextSpec, payloadSpec) => {
  try {
    const tokens = tokenize(String(expression || ""));
    if (!tokens || tokens.length === 0) return "";
    const ctx = parseSpec(contextSpec);
    const payload = parseSpec(payloadSpec);
    let tree = parse(tokens, ctx, payload);
    if (kind === "action") {
      while (tree.t === "group") tree = tree.e;
      if (tree.t !== "obj") return "";
      const parts = [];
      for (const [name, value] of tree.props) {
        const m = ctx.get(name);
        if (!m) return "";
        parts.push(m.cs + " = " + assign(m, value));
      }
      return parts.length === 0 ? "call.Context" : "call.Context with { " + parts.join(", ") + " }";
    }
    if (tree.t === "obj") return "";
    return truthy(tree);
  } catch {
    return "";
  }
});

// ---- The expression language (next-phase-3-design.md section 3.1) ----

// expr_cs(condition, spec) renders a typed expression tree (check.condition, guard.condition, attribute.derived.value) as C#. It is
// an example renderer, a starting point to copy and adapt, not a contract: the engine ships no emitter, and a pack renders the tree
// in its own language. It keeps the language's answers:
// - three-valued logic through C#'s lifted operators: a value that may be null is a nullable type, & and | on bool? are Kleene's and
//   and or, ! keeps null, and arithmetic with a null operand is null. Where C#'s lifting would answer otherwise (ordering answers
//   false for null, equality true or false, concatenation skips it) and for functions, the operands are bound once by a pattern,
//   (A, B) switch { ({ } v1, { } v2) => (bool?)(v1 <= v2), _ => null }, so no operand is written or evaluated twice and C# knows the
//   bound values are not null;
// - integer arithmetic is done in System.Numerics.BigInteger, which never overflows, as PostgreSQL's numeric and the runtime's BigInt
//   do: long would wrap to a wrong answer, and decimal would throw past 28 digits. Decimals stay decimal, literals are written from
//   their exact text (valueText; value, a 64-bit integer, is a double in a script), strings compare equal ordinally and order by code
//   point through the spec's compare() entry (string.CompareOrdinal orders UTF-16 units, which puts U+E000 to U+FFFF above the
//   characters beyond the basic plane), trim removes spaces only, length counts code points and add_months clamps to the month's
//   end, as in PostgreSQL;
// - matches uses .NET's linear-time engine (RegexOptions.NonBacktracking), so a pattern never backtracks, with the portable
//   subset's meaning: ^ and $ anchor the whole string ($ becomes \z, since .NET's $ also matches before a final newline), . matches
//   a newline (Singleline) and case counts. .NET works in UTF-16 code units where the language counts code points, so a pattern
//   that counts characters beyond the basic plane can differ.
// The result is C# of type bool? (or bool when nothing may be null) for a condition, or the value's type for a derived value (an
// integer computed by arithmetic is a BigInteger); a check fails on false, a guard holds on true. Pattern variables are v1, v2...,
// scoped to their switch arm. spec lists what paths read, one entry per value: "path|C# expression|C# type|kind|member=C# member,..."
// joined with ";", path as the expression's text writes it (total, event.amount), kind enum with its members' C# names, else value;
// an entry named now() gives the clock and one named compare() the function that orders two strings by code point. Anything else
// returns "" so the pack falls back: division (its rounding is PostgreSQL's), the json functions, reference-type codes, a datetime
// against a datetimeoffset, now() without a clock, string order without compare(), value object members and a literal C# cannot
// hold exactly (a decimal of more than 28 digits).

const exprKinds = { bool: "bool", decimal: "decimal", double: "double", string: "string", date: "DateOnly", time: "TimeOnly",
  datetime: "DateTime", datetimeoffset: "DateTimeOffset", uuid: "Guid" };

const bigInteger = "System.Numerics.BigInteger";

function exprBase(type) {
  if (type.kind === "int") return type.bits === 16 ? "short" : type.bits === 32 ? "int" : "long";
  const base = exprKinds[type.kind];
  if (!base) throw new Error("type " + type.kind);
  return base;
}

// A rendered node: its C# text, its type without "?", whether it is a value type and whether it may be null. Its text is a primary
// expression (a name, a literal, a call, anything in parentheses), a cast, or ! or - before one.
const csValue = (cs, base, nullable, extra) =>
  ({ ...extra, cs, base, nullable, vt: valueTypes.has(base) || base === bigInteger || !!(extra && extra.vt) });

// The null of a type, which every operator but is null, is distinct from, coalesce and nonnull_count passes on.
const nullOf = (base) => csValue("(" + base + "?)null", base, true, { nullLiteral: true });

const constant = (value) => csValue(value ? "true" : "false", "bool", false, { literal: true });

// The index after the C# string or char literal that starts at i.
function skipLiteral(text, i) {
  const quote = text[i];
  for (i++; i < text.length && text[i] !== quote; i++) {
    if (text[i] === "\\") i++;
  }
  return i + 1;
}

// Whether C# text is wrapped whole in one pair of parentheses.
function enclosed(text) {
  if (text[0] !== "(") return false;
  let depth = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"' || c === "'") {
      i = skipLiteral(text, i) - 1;
    } else if (c === "(") {
      depth++;
    } else if (c === ")" && --depth === 0) {
      return i === text.length - 1;
    }
  }
  return false;
}

// Whether C# text is one primary expression, which a cast, a member access, a switch or a unary operator takes as it is: anything in
// parentheses, or a name, literal, member access or call with no space outside its parentheses and literals.
function primary(text) {
  if (enclosed(text)) return true;
  if (!/^[A-Za-z_@"]/.test(text)) return false;
  let depth = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"' || c === "'") i = skipLiteral(text, i) - 1;
    else if (c === "(") depth++;
    else if (c === ")") depth--;
    else if (depth === 0 && /\s/.test(c)) return false;
  }
  return true;
}

const atom = (text) => (primary(text) ? text : "(" + text + ")");

// Whether a node may be written more than once: a literal, or a name or member chain (a property, call.Context.Total), which reads a
// value without computing it.
const plain = (r) => !!r.literal || /^@?[A-Za-z_][A-Za-z0-9_]*(\.@?[A-Za-z_][A-Za-z0-9_]*)*$/.test(r.cs);

// A node's text as another numeric type where C# does not convert on its own: an integer computed in BigInteger meeting a decimal or a
// double (int widens to both in the language; the conversion throws past decimal's range rather than answer wrong). nullable says
// whether the text may be null here.
function widen(r, base, text, nullable) {
  return r.base === bigInteger && (base === "decimal" || base === "double") ? "(" + base + (nullable ? "?" : "") + ")" + atom(text) : text;
}

// A node's text in a wider type of the same kind (an int as long, a long as BigInteger, an int as decimal), for coalesce and case.
const convert = (r, base) => (r.base === base ? r.cs : "(" + base + (r.nullable ? "?" : "") + ")" + atom(r.cs));

// The C# type of values of one type that some operands hold as BigInteger: an integer is BigInteger when any of them is.
const commonBase = (type, rs) => (type.kind === "int" && rs.some((r) => r.base === bigInteger) ? bigInteger : exprBase(type));

// A C# result over operands, null when one of them is. core receives each operand's text: a non-null one as it is (unless the core
// reads it more than once, many, and it is not plain), every other one bound once to a pattern variable, { } vN when it may be null,
// so the core reads it as not null, else var vN. Nothing is written or evaluated twice, however deep the nesting.
function lifted(ctx, operands, base, core, many) {
  if (operands.some((o) => o.nullLiteral)) return nullOf(base);
  const subjects = [];
  const patterns = [];
  const texts = operands.map((o) => {
    if (!o.nullable && (!many || plain(o))) return o.cs;
    const name = "v" + ++ctx.next;
    subjects.push(o.cs);
    patterns.push((o.nullable ? "{ } " : "var ") + name);
    return name;
  });
  const text = core(...texts);
  if (subjects.length === 0) return csValue(text, base, false);
  const one = subjects.length === 1;
  const subject = one ? atom(subjects[0]) : "(" + subjects.join(", ") + ")";
  const pattern = one ? patterns[0] : "(" + patterns.join(", ") + ")";
  if (!operands.some((o) => o.nullable)) return csValue("(" + subject + " switch { " + pattern + " => " + text + " })", base, false);
  return csValue("(" + subject + " switch { " + pattern + " => (" + base + "?)" + atom(text) + ", _ => null })", base, true);
}

const comparisons = { eq: "==", ne: "!=", lt: "<", le: "<=", gt: ">", ge: ">=" };
const temporal = new Set(["DateTime", "DateTimeOffset"]);

function compare(ctx, op, l, r) {
  if (temporal.has(l.base) && temporal.has(r.base) && l.base !== r.base) throw new Error("datetime against datetimeoffset");
  const strings = l.base === "string" && r.base === "string";
  // A value against itself (issuedOn <= issuedOn) is known, and C# warns of the comparison (CS1718): equal, as in PostgreSQL, where NaN
  // equals NaN too.
  if (!strings && !l.nullable && !r.nullable && plain(l) && !l.literal && l.cs === r.cs) return constant(op === "eq" || op === "le" || op === "ge");
  const order = strings && op !== "eq" && op !== "ne" ? ctx.entries.get("compare()") : null;
  if (strings && op !== "eq" && op !== "ne" && !order) throw new Error("string order without compare()");
  return lifted(ctx, [l, r], "bool", (a, b) => {
    if (order) return "(" + order.cs + "(" + a + ", " + b + ") " + comparisons[op] + " 0)";
    if (strings) return (op === "ne" ? "!" : "") + "string.Equals(" + a + ", " + b + ", StringComparison.Ordinal)";
    return "(" + widen(l, r.base, a, false) + " " + comparisons[op] + " " + widen(r, l.base, b, false) + ")";
  });
}

// A boolean operand as bool? for & and |.
const asNullable = (r) => (r.nullable ? r.cs : "(bool?)" + atom(r.cs));

function logic(op, l, r) {
  if (l.nullLiteral && r.nullLiteral) return l;
  if (!l.nullable && !r.nullable) return csValue("(" + l.cs + (op === "and" ? " && " : " || ") + r.cs + ")", "bool", false);
  return csValue("(" + asNullable(l) + (op === "and" ? " & " : " | ") + asNullable(r) + ")", "bool", true);
}

const not = (r) => (r.nullLiteral ? r : csValue("!" + atom(r.cs), "bool", r.nullable));

// x is null (or, negated, is not null): a constant when x cannot be null or is the null.
function isNull(r, negated) {
  if (r.nullLiteral || !r.nullable) return constant(!!r.nullLiteral !== negated);
  return csValue("(" + atom(r.cs) + (negated ? " is not null)" : " is null)"), "bool", false);
}

// The pattern for .NET: an unescaped $ outside a bracket class anchors the end of the string (\z).
function dotnetPattern(pattern) {
  let out = "";
  let inClass = false;
  for (let i = 0; i < pattern.length; i++) {
    const c = pattern[i];
    if (c === "\\") {
      out += c + (pattern[i + 1] ?? "");
      i++;
    } else if (inClass) {
      if (c === "]") inClass = false;
      out += c;
    } else if (c === "[") {
      inClass = true;
      out += c;
      if (pattern[i + 1] === "^") out += pattern[++i];
      if (pattern[i + 1] === "]") out += pattern[++i];
    } else {
      out += c === "$" ? "\\z" : c;
    }
  }
  return out;
}

const regexOptions = "System.Text.RegularExpressions.RegexOptions.NonBacktracking | System.Text.RegularExpressions.RegexOptions.Singleline";

// A literal's text exactly as the tree holds it: valueText, else value (a script holds an integer above 2^53 inexactly in value).
const literalText = (node) => (node.valueText !== null && node.valueText !== undefined ? String(node.valueText) : String(node.value));

// An integer literal as C#: int when it fits, else long with L; the digits are the tree's own.
function intLiteral(digits) {
  if (!/^-?[0-9]+$/.test(digits)) throw new Error("literal " + digits);
  const n = BigInt(digits);
  if (n >= -2147483648n && n <= 2147483647n) return csValue(n.toString(), "int", false, { literal: true });
  if (n >= -9223372036854775808n && n <= 9223372036854775807n) return csValue(n.toString() + "L", "long", false, { literal: true });
  throw new Error("literal " + digits);
}

// A decimal literal as C#, which holds 28 significant digits at a scale of at most 28; more would be rounded silently, so it is refused.
function decimalLiteral(text) {
  const m = /^(-?)([0-9]*)(?:\.([0-9]+))?$/.exec(text);
  if (!m || (m[2] + (m[3] ?? "")).length === 0) throw new Error("literal " + text);
  const digits = (m[2] + (m[3] ?? "")).replace(/^0+/, "");
  if (digits.length > 28 || (m[3] ?? "").length > 28) throw new Error("decimal literal " + text);
  return csValue(text + "m", "decimal", false, { literal: true });
}

// A literal of the language as C#; a ref (an enum member) takes the type of the path it is compared with.
function exprLiteral(node, hint) {
  const text = literalText(node);
  const parts = (re) => {
    const m = re.exec(text);
    if (!m) throw new Error("literal " + text);
    return m.slice(1).map((x) => (x === undefined ? 0 : Number(x)));
  };
  switch (node.type.kind) {
    case "int": {
      const r = intLiteral(text);
      return node.type.bits === 64 && r.base === "int" ? csValue(r.cs + "L", "long", false, { literal: true }) : r;
    }
    case "decimal":
      return decimalLiteral(text);
    case "double":
      if (!/^-?([0-9]+(\.[0-9]+)?|\.[0-9]+)([eE][-+]?[0-9]+)?$/.test(text)) throw new Error("literal " + text);
      return csValue(text + "d", "double", false, { literal: true });
    case "string":
      return csValue(JSON.stringify(text), "string", false, { literal: true });
    case "bool":
      return constant(node.value === true || text === "true");
    case "date":
      return csValue("new DateOnly(" + parts(/^(\d{4})-(\d{2})-(\d{2})$/).join(", ") + ")", "DateOnly", false, { literal: true });
    case "time":
      return csValue("new TimeOnly(" + parts(/^(\d{2}):(\d{2})(?::(\d{2}))?$/).join(", ") + ")", "TimeOnly", false, { literal: true });
    case "datetime":
      return csValue("new DateTime(" + parts(/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/).join(", ") + ")", "DateTime", false, { literal: true });
    case "datetimeoffset": {
      const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?(Z|([+-])(\d{2}):(\d{2}))$/.exec(text);
      if (!m) throw new Error("literal " + text);
      const at = m.slice(1, 7).map((x) => (x === undefined ? 0 : Number(x)));
      const offset = m[7] === "Z" ? "TimeSpan.Zero" : "new TimeSpan(" + m[8] + Number(m[9]) + ", " + m[8] + Number(m[10]) + ", 0)";
      return csValue("new DateTimeOffset(" + at.join(", ") + ", " + offset + ")", "DateTimeOffset", false, { literal: true });
    }
    case "uuid":
      return hint && hint.base === "string"
        ? csValue(JSON.stringify(text), "string", false, { literal: true })
        : csValue("new Guid(" + JSON.stringify(text) + ")", "Guid", false, { literal: true });
    default:
      throw new Error("literal of " + node.type.kind);
  }
}

// -x of a literal, as a literal of its kind.
function negatedLiteral(node) {
  const text = literalText(node);
  const negated = text.startsWith("-") ? text.slice(1) : "-" + text;
  return exprLiteral({ node: "value", value: negated, valueText: negated, type: node.type });
}

// The path a node reads, for a ref beside it: the node itself or the first path among its operands.
function pathIn(r) {
  if (r.entry) return r;
  for (const o of r.operands || []) {
    const found = pathIn(o);
    if (found) return found;
  }
  return null;
}

function exprRef(node, hint) {
  const path = hint && pathIn(hint);
  if (!path || path.entry.kind !== "enum" || node.type.kind !== "enum") throw new Error("ref");
  const member = path.entry.members.get(String(node.value));
  if (!member) throw new Error("enum member " + node.value);
  return csValue(path.base + "." + member, path.base, false, { vt: true, literal: true });
}

function list(items) {
  const out = [];
  for (let i = 0; i < items.length; i++) out.push(items[i]);
  return out;
}

function exprNode(node, ctx, hint) {
  switch (node.node) {
    case "value":
      return exprLiteral(node, hint);
    case "null":
      return csValue("null", hint ? hint.base : "object", true, { nullLiteral: true });
    case "ref":
      return exprRef(node, hint);
    case "now": {
      const clock = ctx.entries.get("now()");
      if (!clock) throw new Error("now");
      return csValue(clock.cs, clock.type.replace(/\?$/, ""), /\?$/.test(clock.type));
    }
    case "path": {
      const entry = ctx.entries.get(node.text);
      if (!entry) throw new Error("path " + node.text);
      const base = entry.type.replace(/\?$/, "");
      return csValue(entry.cs, base, /\?$/.test(entry.type), { vt: entry.kind === "enum", entry });
    }
    case "op":
      return exprOp(node, ctx);
    case "call":
      return exprCall(node, ctx);
    case "case":
      return exprCase(node, ctx);
    default:
      throw new Error("node " + node.node);
  }
}

// Two operands of one type: a literal, a null or a ref beside a path renders in the path's type.
function pair(args, ctx) {
  const simple = (n) => n.node === "value" || n.node === "null" || n.node === "ref";
  if (simple(args[0]) && !simple(args[1])) {
    const r = exprNode(args[1], ctx);
    return [exprNode(args[0], ctx, r), r];
  }
  const l = exprNode(args[0], ctx);
  return [l, exprNode(args[1], ctx, l)];
}

function exprOp(node, ctx) {
  const args = list(node.args);
  const op = node.op;
  if (comparisons[op]) {
    const [l, r] = pair(args, ctx);
    return { ...compare(ctx, op, l, r), operands: [l, r] };
  }
  switch (op) {
    case "and":
    case "or":
      return logic(op, exprNode(args[0], ctx), exprNode(args[1], ctx));
    case "implies":
      return logic("or", not(exprNode(args[0], ctx)), exprNode(args[1], ctx));
    case "not":
      return not(exprNode(args[0], ctx));
    case "isNull":
    case "isNotNull":
      return isNull(exprNode(args[0], ctx), op === "isNotNull");
    case "distinct":
    case "notDistinct": {
      const [l, r] = pair(args, ctx);
      if (l.nullLiteral || r.nullLiteral) return isNull(l.nullLiteral ? r : l, op === "distinct");
      if (plain(l) && !l.literal && l.cs === r.cs) return constant(op === "notDistinct");
      const same = l.base === "string" && r.base === "string"
        ? "string.Equals(" + l.cs + ", " + r.cs + ", StringComparison.Ordinal)"
        : "(" + widen(l, r.base, l.cs, l.nullable) + " == " + widen(r, l.base, r.cs, r.nullable) + ")";
      return csValue(op === "distinct" ? "!" + same : same, "bool", false);
    }
    case "between": {
      // low <= x and x <= high under Kleene's and, with x bound once: when x is null both comparisons are, and so is the answer.
      const x = exprNode(args[0], ctx);
      const low = exprNode(args[1], ctx, x);
      const high = exprNode(args[2], ctx, x);
      if (x.nullLiteral) return nullOf("bool");
      if (!x.nullable && plain(x)) return logic("and", compare(ctx, "le", low, x), compare(ctx, "le", x, high));
      const name = "v" + ++ctx.next;
      const bound = { ...x, cs: name, nullable: false };
      const test = logic("and", compare(ctx, "le", low, bound), compare(ctx, "le", bound, high));
      return x.nullable
        ? csValue("(" + atom(x.cs) + " switch { { } " + name + " => " + asNullable(test) + ", _ => null })", "bool", true)
        : csValue("(" + atom(x.cs) + " switch { var " + name + " => " + test.cs + " })", "bool", test.nullable);
    }
    case "in":
    case "notIn": {
      const x = exprNode(args[0], ctx);
      const items = args.slice(1).map((a) => exprNode(a, ctx, x));
      const test = lifted(ctx, [x], "bool", (v) => "(" + items.map((i) => compare(ctx, "eq", { ...x, cs: v, nullable: false }, i).cs).join(" || ") + ")", true);
      return op === "in" ? test : not(test);
    }
    case "add":
    case "sub":
    case "mul": {
      const [l, r] = pair(args, ctx);
      const sign = op === "add" ? " + " : op === "sub" ? " - " : " * ";
      if (node.type.kind === "int") {
        // In BigInteger, so nothing overflows: a left operand that is not one yet is cast, and the right one converts on its own.
        if (l.nullLiteral || r.nullLiteral) return nullOf(bigInteger);
        const left = l.base === bigInteger || r.base === bigInteger ? l.cs : "(" + bigInteger + (l.nullable ? "?" : "") + ")" + atom(l.cs);
        return csValue("(" + left + sign + r.cs + ")", bigInteger, l.nullable || r.nullable);
      }
      const base = exprBase(node.type);
      if (l.nullLiteral || r.nullLiteral) return nullOf(base);
      return csValue("(" + widen(l, base, l.cs, l.nullable) + sign + widen(r, base, r.cs, r.nullable) + ")", base, l.nullable || r.nullable);
    }
    case "neg": {
      if (args[0].node === "value") return negatedLiteral(args[0]);
      const r = exprNode(args[0], ctx);
      if (node.type.kind === "int") {
        if (r.nullLiteral) return nullOf(bigInteger);
        return csValue("(-" + (r.base === bigInteger ? atom(r.cs) : "(" + bigInteger + (r.nullable ? "?" : "") + ")" + atom(r.cs)) + ")", bigInteger, r.nullable);
      }
      return r.nullLiteral ? nullOf(r.base) : csValue("(-" + atom(r.cs) + ")", r.base, r.nullable);
    }
    case "concat":
      return lifted(ctx, args.map((a) => exprNode(a, ctx)), "string", (...xs) => "(" + xs.join(" + ") + ")");
    case "coalesce": {
      const rs = [];
      for (const a of args) {
        const r = exprNode(a, ctx);
        rs.push(r);
        if (!r.nullable) break;
      }
      const base = commonBase(node.type, rs);
      const nullable = rs[rs.length - 1].nullable;
      return csValue("(" + rs.map((r) => (r.nullLiteral ? r.cs : convert(r, base))).join(" ?? ") + ")", base, nullable);
    }
    default:
      throw new Error("operator " + op);
  }
}

// A day or month count as the int AddDays and AddMonths take: a long or a BigInteger converts checked, so a count past int's range
// throws rather than wraps.
const count = (r, text) => (r.base === "int" || r.base === "short" ? text : r.base === bigInteger ? "(int)" + atom(text) : "checked((int)" + text + ")");

function exprCall(node, ctx) {
  const args = list(node.args);
  const one = () => exprNode(args[0], ctx);
  switch (node.call) {
    case "trim":
      return lifted(ctx, [one()], "string", (s) => atom(s) + ".Trim(' ')");
    case "length":
      return lifted(ctx, [one()], "int", (s) => "System.Linq.Enumerable.Count(" + atom(s) + ".EnumerateRunes())");
    case "matches":
      return lifted(ctx, [one()], "bool",
        (s) => "System.Text.RegularExpressions.Regex.IsMatch(" + s + ", " + JSON.stringify(dotnetPattern(literalText(args[1]))) + ", " + regexOptions + ")");
    case "nonnull_count": {
      const terms = args.map((a) => exprNode(a, ctx)).map((r) => (r.nullLiteral ? "0" : r.nullable ? "(" + atom(r.cs) + " is null ? 0 : 1)" : "1"));
      return csValue(terms.length === 1 ? terms[0] : "(" + terms.join(" + ") + ")", "int", false);
    }
    case "add_days":
    case "add_months": {
      const d = one();
      const n = exprNode(args[1], ctx);
      const utc = d.base === "DateTimeOffset" && node.call === "add_months" ? ".ToUniversalTime()" : "";
      return lifted(ctx, [d, n], d.base, (x, y) => atom(x) + utc + (node.call === "add_days" ? ".AddDays(" : ".AddMonths(") + count(n, y) + ")");
    }
    case "date_part": {
      const part = literalText(args[0]);
      const d = exprNode(args[1], ctx);
      const utc = d.base === "DateTimeOffset" ? ".UtcDateTime" : "";
      const field = { year: ".Year", month: ".Month", day: ".Day", hour: ".Hour", minute: ".Minute" }[part];
      if (part !== "isodow" && !field) throw new Error("date part " + part);
      return lifted(ctx, [d], "int", (x) => (part === "isodow" ? "(((int)" + atom(x) + utc + ".DayOfWeek + 6) % 7 + 1)" : atom(x) + utc + field));
    }
    case "date_of": {
      const t = one();
      return lifted(ctx, [t], "DateOnly", (x) => "DateOnly.FromDateTime(" + atom(x) + (t.base === "DateTimeOffset" ? ".UtcDateTime" : "") + ")");
    }
    case "round": {
      const digits = literalText(args[1]);
      if (!/^[0-9]+$/.test(digits)) throw new Error("round digits " + digits);
      return lifted(ctx, [one()], "decimal", (x) => "Math.Round((decimal)" + atom(x) + ", " + digits + ", MidpointRounding.AwayFromZero)");
    }
    default:
      throw new Error("function " + node.call);
  }
}

// case when ... then ... else ... end: a branch is taken only when its condition is true; no branch and no else is null.
function exprCase(node, ctx) {
  const args = list(node.args);
  const branches = [];
  for (let i = 0; i + 1 < args.length; i += 2) branches.push([exprNode(args[i], ctx), exprNode(args[i + 1], ctx)]);
  const given = args.length % 2 === 1 ? exprNode(args[args.length - 1], ctx) : null;
  const base = commonBase(node.type, [...branches.map(([, then]) => then), ...(given ? [given] : [])]);
  const fallback = given ?? nullOf(base);
  const nullable = fallback.nullable || branches.some(([, then]) => then.nullable);
  const cast = (r) => (r.nullLiteral || (r.nullable && r.base === base) ? r.cs : nullable ? "(" + base + "?)" + atom(r.cs) : convert(r, base));
  let text = cast(fallback);
  let taken = false;
  for (let i = branches.length - 1; i >= 0; i--) {
    const [when, then] = branches[i];
    if (when.nullLiteral) continue; // never true, so never taken
    taken = true;
    text = "(" + (when.nullable ? atom(when.cs) + " == true" : when.cs) + " ? " + cast(then) + " : " + text + ")";
  }
  return !taken && fallback.nullLiteral ? nullOf(base) : csValue(text, base, nullable);
}

maquettiste.helper("expr_cs", (condition, spec) => {
  try {
    if (!condition) return "";
    return exprNode(condition, { entries: parseSpec(spec), next: 0 }).cs;
  } catch {
    return "";
  }
});

// expr_cs_holds(condition, spec): C# that is true exactly when a condition holds, as a guard needs it (only true holds): the condition
// == true, or false for one that is always null, which C# refuses to compare with true (CS0472).
maquettiste.helper("expr_cs_holds", (condition, spec) => {
  try {
    if (!condition) return "";
    const r = exprNode(condition, { entries: parseSpec(spec), next: 0 });
    return r.nullLiteral ? "false" : atom(r.cs) + " == true";
  } catch {
    return "";
  }
});

// C# text without one pair of parentheses around the whole of it, for an expression that stands alone (the right side of an
// assignment): "(Total >= 0)" -> "Total >= 0".
maquettiste.helper("cs_unwrap", (text) => {
  const s = String(text ?? "");
  return enclosed(s) ? s.slice(1, -1) : s;
});

// Text for a one-line comment or an XML documentation line: every line break (\r\n, \r, \n) becomes a space, so a multi-line
// expression or description cannot end the comment early. Every comment context of the process units goes through it.
maquettiste.helper("cs_line", (text) => String(text ?? "").replace(/\r\n|\r|\n/g, " "));
