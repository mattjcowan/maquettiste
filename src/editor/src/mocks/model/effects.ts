// The mock's reading of object effects (object-effects-design.md sections 7 and 10). The mock has no expression engine, so
// this evaluates a small closed subset of the expression language: literals, paths, now(), current_actor(), in_state, the
// comparisons, and/or/not, null tests, arithmetic, concat, coalesce, trim and length. A tree or a text outside the subset is
// UNKNOWN: a set with an unknown value does nothing and the trace says so; a guard with one falls back to its `assume` value.
// The engine evaluates all of it; the mock only has to behave alike on what it can read.
type Json = Record<string, unknown>;

export const UNKNOWN = Symbol("unknown");
export type Value = unknown | typeof UNKNOWN;

export interface EvalEnv {
  /** A path of ids: a subject attribute, a context attribute, or ends then an attribute; UNKNOWN when it is none. */
  /** `old` asks for the value before the verb (a trigger point's `old.` path). */
  path(ids: string[], old?: boolean): Value;
  now(): string;
  actor(): string | null;
  /** True, false, or null when there is no object at the end of the path. */
  inState(states: string[], of: string[] | undefined): boolean | null;
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

/** Text in the subset: `current_actor()`, `now()`, `null`, `true`, `false`, a number, a quoted string. Anything else is UNKNOWN (the engine parses the rest). */
export function treeOfText(text: string): Json | typeof UNKNOWN {
  const t = text.trim();
  if (t === "current_actor()") return { currentActor: true };
  if (t === "now()") return { now: true };
  if (t === "null") return { null: true };
  if (t === "true" || t === "false") return { value: t === "true" };
  if (/^-?\d+$/.test(t)) return { value: Number(t) };
  const q = /^'((?:[^']|'')*)'$/.exec(t);
  if (q) return { value: q[1].replace(/''/g, "'") };
  return UNKNOWN;
}

const num = (v: Value): number | null => (typeof v === "number" ? v : null);

export function evalTree(tree: unknown, env: EvalEnv): Value {
  if (!tree || typeof tree !== "object") return UNKNOWN;
  const t = tree as Json;
  if (typeof t.text === "string") {
    const parsed = treeOfText(t.text);
    return parsed === UNKNOWN ? UNKNOWN : evalTree(parsed, env);
  }
  if ("null" in t) return null;
  if ("value" in t) return t.value;
  if (Array.isArray(t.path)) return env.path(t.path as string[], t.old === true);
  if (t.now === true) return env.now();
  if (t.currentActor === true) return env.actor();
  if (Array.isArray(t.inState)) return env.inState(t.inState as string[], Array.isArray(t.of) ? (t.of as string[]) : undefined);
  const args = arr(t.args).map((a) => evalTree(a, env));
  if (typeof t.op === "string") return evalOp(t.op, args);
  if (typeof t.call === "string") {
    const [a] = args;
    if (a === UNKNOWN) return UNKNOWN;
    if (t.call === "trim") return typeof a === "string" ? a.trim() : a === null ? null : UNKNOWN;
    if (t.call === "length") return typeof a === "string" ? a.length : a === null ? null : UNKNOWN;
  }
  return UNKNOWN;
}

function evalOp(op: string, args: Value[]): Value {
  if (op === "coalesce") {
    for (const a of args) {
      if (a === UNKNOWN) return UNKNOWN;
      if (a !== null) return a;
    }
    return null;
  }
  // Three-valued logic first: false and unknown is false, true or unknown is true.
  if (op === "and" || op === "or") {
    const want = op === "or";
    let unknown = false;
    for (const a of args) {
      if (a === UNKNOWN) return UNKNOWN;
      if (a === null) unknown = true;
      else if ((a === true) === want) return want;
    }
    return unknown ? null : !want;
  }
  if (args.some((a) => a === UNKNOWN)) return UNKNOWN;
  const [a, b] = args;
  switch (op) {
    case "isNull":
      return a === null;
    case "isNotNull":
      return a !== null;
    case "not":
      return a === null ? null : a !== true;
    case "neg":
      return num(a) === null ? (a === null ? null : UNKNOWN) : -(a as number);
  }
  if (a === null || b === null)
    return args.length === 2 && ["eq", "ne", "lt", "le", "gt", "ge", "add", "sub", "mul", "div", "concat"].includes(op) ? null : UNKNOWN;
  switch (op) {
    case "eq":
      return a === b;
    case "ne":
      return a !== b;
    case "lt":
    case "le":
    case "gt":
    case "ge": {
      if (typeof a !== typeof b || (typeof a !== "number" && typeof a !== "string")) return UNKNOWN;
      const x = a as number | string;
      const y = b as number | string;
      return op === "lt" ? x < y : op === "le" ? x <= y : op === "gt" ? x > y : x >= y;
    }
    case "add":
    case "sub":
    case "mul":
    case "div": {
      const x = num(a);
      const y = num(b);
      if (x === null || y === null) return UNKNOWN;
      if (op === "div") return y === 0 ? UNKNOWN : Math.trunc(x / y);
      return op === "add" ? x + y : op === "sub" ? x - y : x * y;
    }
    case "concat":
      return typeof a === "string" && typeof b === "string" ? a + b : UNKNOWN;
  }
  return UNKNOWN;
}

/** The text form of the subset's trees, for a trace note or a preview; "…" for what is outside it. */
export function printTree(tree: unknown, name: (id: string) => string, state: (id: string) => string): string {
  if (!tree || typeof tree !== "object") return "";
  const t = tree as Json;
  const p = (n: unknown) => printTree(n, name, state);
  if (typeof t.text === "string") return t.text;
  if ("null" in t) return "null";
  if ("value" in t) return typeof t.value === "string" ? `'${t.value.replace(/'/g, "''")}'` : String(t.value);
  if (Array.isArray(t.path)) return (t.path as string[]).map(name).join(".");
  if (t.now === true) return "now()";
  if (t.currentActor === true) return "current_actor()";
  if (Array.isArray(t.inState))
    return `in_state(${["subject", ...(Array.isArray(t.of) ? (t.of as string[]).map(name) : [])].join(".")}, ${(t.inState as string[]).map(state).join(", ")})`;
  const args = arr(t.args);
  const infix: Record<string, string> = {
    eq: "=",
    ne: "<>",
    lt: "<",
    le: "<=",
    gt: ">",
    ge: ">=",
    add: "+",
    sub: "-",
    mul: "*",
    div: "/",
    concat: "||",
    and: "and",
    or: "or",
  };
  if (typeof t.op === "string" && infix[t.op] && args.length >= 2) return args.map(p).join(` ${infix[t.op]} `);
  if (typeof t.op === "string") return `${t.op}(${args.map(p).join(", ")})`;
  if (typeof t.call === "string") return `${t.call}(${args.map(p).join(", ")})`;
  return "…";
}
