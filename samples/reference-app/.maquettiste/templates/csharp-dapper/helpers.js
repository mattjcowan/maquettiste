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

// Text for a one-line comment or an XML documentation line: every line break (\r\n, \r, \n) becomes a space, so a multi-line
// expression or description cannot end the comment early. Every comment context of the process units goes through it.
maquettiste.helper("cs_line", (text) => String(text ?? "").replace(/\r\n|\r|\n/g, " "));
