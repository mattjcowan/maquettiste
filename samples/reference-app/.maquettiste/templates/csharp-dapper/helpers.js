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
//   and or, ! keeps null, and a comparison, arithmetic or function with a null operand is null (written as an explicit test where
//   C#'s own lifting would answer false or skip the null, as for ordering and concatenation);
// - decimals stay decimal, integer arithmetic is done in long, strings compare ordinally, trim removes spaces only, length counts
//   code points and add_months clamps to the month's end, as in PostgreSQL;
// - matches uses .NET's linear-time engine (RegexOptions.NonBacktracking), so a pattern never backtracks, with the portable
//   subset's meaning: ^ and $ anchor the whole string ($ becomes \z, since .NET's $ also matches before a final newline), . matches
//   a newline (Singleline) and case counts. .NET works in UTF-16 code units where the language counts code points, so a pattern
//   that counts characters beyond the basic plane can differ.
// The result is C# of type bool? (or bool when nothing may be null) for a condition, or the value's type for a derived value; a
// check fails on false, a guard holds on true. spec lists what paths read, one entry per value: "path|C# expression|C# type|kind|
// member=C# member,..." joined with ";", path as the expression's text writes it (total, event.amount), kind enum with its members'
// C# names, else value; an entry named now() gives the clock. Anything else returns "" so the pack falls back: division (its
// rounding is PostgreSQL's), the json functions, reference-type codes, a datetime against a datetimeoffset, now() without a clock,
// value object members and a literal it cannot read.

const exprKinds = { bool: "bool", decimal: "decimal", double: "double", string: "string", date: "DateOnly", time: "TimeOnly",
  datetime: "DateTime", datetimeoffset: "DateTimeOffset", uuid: "Guid" };

function exprBase(type) {
  if (type.kind === "int") return type.bits === 16 ? "short" : type.bits === 32 ? "int" : "long";
  const base = exprKinds[type.kind];
  if (!base) throw new Error("type " + type.kind);
  return base;
}

// A rendered node: its C# text, its type without "?", whether it is a value type and whether it may be null.
const csValue = (cs, base, nullable, extra) => ({ ...extra, cs, base, nullable, vt: valueTypes.has(base) || !!(extra && extra.vt) });

// The operand where it is known not to be null: .Value of a nullable value type, ! of a reference type.
const known = (r) => (!r.nullable ? r.cs : r.vt ? r.cs + ".Value" : r.cs + "!");

// A C# result over operands, null when one of them is: the test is written out, so no lifted operator answers false for null.
function lifted(operands, base, core) {
  if (operands.some((o) => o.nullLiteral)) return csValue("(" + base + "?)null", base, true);
  const tests = operands.filter((o) => o.nullable).map((o) => o.cs + " is null");
  const text = core(...operands.map(known));
  return tests.length === 0 ? csValue(text, base, false) : csValue("(" + tests.join(" || ") + " ? (" + base + "?)null : " + text + ")", base, true);
}

const comparisons = { eq: "==", ne: "!=", lt: "<", le: "<=", gt: ">", ge: ">=" };
const temporal = new Set(["DateTime", "DateTimeOffset"]);

function compare(op, l, r) {
  if (temporal.has(l.base) && temporal.has(r.base) && l.base !== r.base) throw new Error("datetime against datetimeoffset");
  return lifted([l, r], "bool", (a, b) => {
    if (l.base === "string" && r.base === "string")
      return op === "eq" || op === "ne"
        ? (op === "ne" ? "!" : "") + "string.Equals(" + a + ", " + b + ", StringComparison.Ordinal)"
        : "(string.CompareOrdinal(" + a + ", " + b + ") " + comparisons[op] + " 0)";
    return "(" + a + " " + comparisons[op] + " " + b + ")";
  });
}

// A boolean operand as bool? for & and |.
const asNullable = (r) => (r.nullable ? r.cs : "(bool?)" + r.cs);

function logic(op, l, r) {
  if (!l.nullable && !r.nullable) return csValue("(" + l.cs + (op === "and" ? " && " : " || ") + r.cs + ")", "bool", false);
  return csValue("(" + asNullable(l) + (op === "and" ? " & " : " | ") + asNullable(r) + ")", "bool", true);
}

const not = (r) => csValue("!" + r.cs, "bool", r.nullable);

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

// A literal of the language as C#; a ref (an enum member) takes the type of the path it is compared with.
function exprLiteral(node, hint) {
  const text = String(node.value);
  const parts = (re) => {
    const m = re.exec(text);
    if (!m) throw new Error("literal " + text);
    return m.slice(1).map((x) => (x === undefined ? 0 : Number(x)));
  };
  switch (node.type.kind) {
    case "int":
      return csValue(text + (node.type.bits === 64 ? "L" : ""), exprBase(node.type), false);
    case "decimal":
      return csValue(text + "m", "decimal", false);
    case "double":
      return csValue(text + "d", "double", false);
    case "string":
      return csValue(JSON.stringify(text), "string", false);
    case "bool":
      return csValue(node.value ? "true" : "false", "bool", false);
    case "date":
      return csValue("new DateOnly(" + parts(/^(\d{4})-(\d{2})-(\d{2})$/).join(", ") + ")", "DateOnly", false);
    case "time":
      return csValue("new TimeOnly(" + parts(/^(\d{2}):(\d{2})(?::(\d{2}))?$/).join(", ") + ")", "TimeOnly", false);
    case "datetime":
      return csValue("new DateTime(" + parts(/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/).join(", ") + ")", "DateTime", false);
    case "datetimeoffset": {
      const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?(Z|([+-])(\d{2}):(\d{2}))$/.exec(text);
      if (!m) throw new Error("literal " + text);
      const at = m.slice(1, 7).map((x) => (x === undefined ? 0 : Number(x)));
      const offset = m[7] === "Z" ? "TimeSpan.Zero" : "new TimeSpan(" + m[8] + Number(m[9]) + ", " + m[8] + Number(m[10]) + ", 0)";
      return csValue("new DateTimeOffset(" + at.join(", ") + ", " + offset + ")", "DateTimeOffset", false);
    }
    case "uuid":
      return hint && hint.base === "string" ? csValue(JSON.stringify(text), "string", false) : csValue("new Guid(" + JSON.stringify(text) + ")", "Guid", false);
    default:
      throw new Error("literal of " + node.type.kind);
  }
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
  return csValue(path.base + "." + member, path.base, false, { vt: true });
}

function list(items) {
  const out = [];
  for (let i = 0; i < items.length; i++) out.push(items[i]);
  return out;
}

function exprNode(node, spec, hint) {
  switch (node.node) {
    case "value":
      return exprLiteral(node, hint);
    case "null":
      return csValue("null", hint ? hint.base : "object", true, { nullLiteral: true });
    case "ref":
      return exprRef(node, hint);
    case "now": {
      const clock = spec.get("now()");
      if (!clock) throw new Error("now");
      return csValue(clock.cs, clock.type.replace(/\?$/, ""), /\?$/.test(clock.type));
    }
    case "path": {
      const entry = spec.get(node.text);
      if (!entry) throw new Error("path " + node.text);
      const base = entry.type.replace(/\?$/, "");
      return csValue(entry.cs, base, /\?$/.test(entry.type), { vt: entry.kind === "enum", entry });
    }
    case "op":
      return exprOp(node, spec);
    case "call":
      return exprCall(node, spec);
    case "case":
      return exprCase(node, spec);
    default:
      throw new Error("node " + node.node);
  }
}

// Two operands of one type: a literal, a null or a ref beside a path renders in the path's type.
function pair(args, spec) {
  const simple = (n) => n.node === "value" || n.node === "null" || n.node === "ref";
  if (simple(args[0]) && !simple(args[1])) {
    const r = exprNode(args[1], spec);
    return [exprNode(args[0], spec, r), r];
  }
  const l = exprNode(args[0], spec);
  return [l, exprNode(args[1], spec, l)];
}

function exprOp(node, spec) {
  const args = list(node.args);
  const op = node.op;
  if (comparisons[op]) {
    const [l, r] = pair(args, spec);
    return { ...compare(op, l, r), operands: [l, r] };
  }
  switch (op) {
    case "and":
    case "or":
      return logic(op, exprNode(args[0], spec), exprNode(args[1], spec));
    case "implies":
      return logic("or", not(exprNode(args[0], spec)), exprNode(args[1], spec));
    case "not":
      return not(exprNode(args[0], spec));
    case "isNull":
    case "isNotNull": {
      const r = exprNode(args[0], spec);
      if (!r.nullable || r.nullLiteral) return csValue((op === "isNull") === !!r.nullLiteral ? "true" : "false", "bool", false);
      return csValue("(" + r.cs + (op === "isNull" ? " is null)" : " is not null)"), "bool", false);
    }
    case "distinct":
    case "notDistinct": {
      const [l, r] = pair(args, spec);
      const same = l.base === "string" && r.base === "string" ? "string.Equals(" + l.cs + ", " + r.cs + ", StringComparison.Ordinal)" : "(" + l.cs + " == " + r.cs + ")";
      return csValue(op === "distinct" ? "!" + same : same, "bool", false);
    }
    case "between": {
      const x = exprNode(args[0], spec);
      const low = exprNode(args[1], spec, x);
      const high = exprNode(args[2], spec, x);
      return logic("and", compare("le", low, x), compare("le", x, high));
    }
    case "in":
    case "notIn": {
      const x = exprNode(args[0], spec);
      const items = args.slice(1).map((a) => exprNode(a, spec, x));
      const test = lifted([x], "bool", (v) => "(" + items.map((i) => compare("eq", { ...x, cs: v, nullable: false }, i).cs).join(" || ") + ")");
      return op === "in" ? test : not(test);
    }
    case "add":
    case "sub":
    case "mul": {
      const [l, r] = pair(args, spec);
      const base = exprBase(node.type);
      const sign = op === "add" ? " + " : op === "sub" ? " - " : " * ";
      const left = base === "long" ? "(long" + (l.nullable ? "?" : "") + ")" + l.cs : l.cs;
      return csValue("(" + left + sign + r.cs + ")", base, l.nullable || r.nullable);
    }
    case "neg": {
      const r = exprNode(args[0], spec);
      return csValue("(-" + r.cs + ")", r.base, r.nullable);
    }
    case "concat":
      return lifted(args.map((a) => exprNode(a, spec)), "string", (a, b) => "(" + a + " + " + b + ")");
    case "coalesce": {
      const base = exprBase(node.type);
      const parts = [];
      let nullable = true;
      for (const a of args) {
        const r = exprNode(a, spec);
        parts.push(r.base === base || r.nullLiteral ? r.cs : "(" + base + (r.nullable ? "?" : "") + ")" + r.cs);
        if (!r.nullable) {
          nullable = false;
          break;
        }
      }
      return csValue("(" + parts.join(" ?? ") + ")", base, nullable);
    }
    default:
      throw new Error("operator " + op);
  }
}

function exprCall(node, spec) {
  const args = list(node.args);
  const one = () => exprNode(args[0], spec);
  switch (node.call) {
    case "trim":
      return lifted([one()], "string", (s) => s + ".Trim(' ')");
    case "length":
      return lifted([one()], "int", (s) => "System.Linq.Enumerable.Count(" + s + ".EnumerateRunes())");
    case "matches":
      return lifted([one()], "bool", (s) => "System.Text.RegularExpressions.Regex.IsMatch(" + s + ", " + JSON.stringify(dotnetPattern(String(args[1].value))) + ", " + regexOptions + ")");
    case "nonnull_count":
      return csValue("(" + args.map((a) => exprNode(a, spec)).map((r) => (r.nullLiteral ? "0" : r.nullable ? "(" + r.cs + " is null ? 0 : 1)" : "1")).join(" + ") + ")", "int", false);
    case "add_days":
    case "add_months": {
      const d = one();
      const n = exprNode(args[1], spec);
      const utc = d.base === "DateTimeOffset" && node.call === "add_months" ? ".ToUniversalTime()" : "";
      return lifted([d, n], d.base, (x, y) => x + utc + (node.call === "add_days" ? ".AddDays(" : ".AddMonths(") + "(int)" + y + ")");
    }
    case "date_part": {
      const part = String(args[0].value);
      const d = exprNode(args[1], spec);
      const utc = d.base === "DateTimeOffset" ? ".UtcDateTime" : "";
      const field = { year: ".Year", month: ".Month", day: ".Day", hour: ".Hour", minute: ".Minute" }[part];
      return lifted([d], "int", (x) => (part === "isodow" ? "(((int)" + x + utc + ".DayOfWeek + 6) % 7 + 1)" : x + utc + field));
    }
    case "date_of": {
      const t = one();
      return lifted([t], "DateOnly", (x) => "DateOnly.FromDateTime(" + x + (t.base === "DateTimeOffset" ? ".UtcDateTime" : "") + ")");
    }
    case "round":
      return lifted([one()], "decimal", (x) => "Math.Round((decimal)" + x + ", " + String(args[1].value) + ", MidpointRounding.AwayFromZero)");
    default:
      throw new Error("function " + node.call);
  }
}

// case when ... then ... else ... end: a branch is taken only when its condition is true; no branch and no else is null.
function exprCase(node, spec) {
  const args = list(node.args);
  const base = exprBase(node.type);
  const branches = [];
  let fallback = csValue("(" + base + "?)null", base, true);
  for (let i = 0; i + 1 < args.length; i += 2) branches.push([exprNode(args[i], spec), exprNode(args[i + 1], spec)]);
  if (args.length % 2 === 1) fallback = exprNode(args[args.length - 1], spec);
  const nullable = fallback.nullable || branches.some(([, then]) => then.nullable);
  const cast = (r) => (r.nullLiteral || !nullable ? r.cs : "(" + base + "?)" + r.cs);
  let text = cast(fallback);
  for (let i = branches.length - 1; i >= 0; i--) {
    const [when, then] = branches[i];
    text = "(" + (when.nullable ? when.cs + " == true" : when.cs) + " ? " + cast(then) + " : " + text + ")";
  }
  return csValue(text, base, nullable);
}

maquettiste.helper("expr_cs", (condition, spec) => {
  try {
    if (!condition) return "";
    return exprNode(condition, parseSpec(spec)).cs;
  } catch {
    return "";
  }
});

// Text for a one-line comment or an XML documentation line: every line break (\r\n, \r, \n) becomes a space, so a multi-line
// expression or description cannot end the comment early. Every comment context of the process units goes through it.
maquettiste.helper("cs_line", (text) => String(text ?? "").replace(/\r\n|\r|\n/g, " "));
