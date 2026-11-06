// The mock's rules for a lifecycle's bindings and state codes (lifecycle-design.md decisions 1 to 4 and 11 to 17), as the
// engine's ProcessRules.cs reports them: MQ9202 to MQ9205, MQ9207 and MQ9208 per binding, MQ9206 and MQ9210 per state.
// Binding findings sit at the binding's pointer under the subjects entry's id (the one-subject form: the process id);
// code findings at `<state pointer>/code` under the state's id.
import type { Diagnostic } from "@/api/types";
import { bindableAttributes, classifyAttribute, type BindingType } from "@/model/binding";
import { subjectsOf } from "@/model/process";
import type { ModelEntry, ValidationContext } from "./validate";

type Json = Record<string, unknown>;
type Code = string | number;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const text = (code: Code) => (typeof code === "string" ? `'${code}'` : String(code));
const names = (list: string[]) => (list.length <= 1 ? (list[0] ?? "") : `${list.slice(0, -1).join(", ")} and ${list[list.length - 1]}`);
const isCode = (v: unknown): v is Code => typeof v === "string" || (typeof v === "number" && Number.isInteger(v));
/** A code's key: 1 and "1" are one code. */
export const codeKey = (code: unknown): string | null => (isCode(code) ? String(code) : null);

type Emit = (rule: string, severity: Diagnostic["severity"], message: string, pointer: string, elementId?: string) => void;

interface Node {
  state: Json;
  id: string;
  path: string;
  pointer: string;
  parent: Node | null;
  codedAncestor: Node | null;
  underParallel: boolean;
}

const restless = (s: Json) => s.type === "history" || s.type === "choice";

function walk(states: unknown, base: string, parent: Node | null, path: string, nearest: Node | null, underParallel: boolean, out: Node[]): void {
  arr(states).forEach((state, i) => {
    const node: Node = {
      state,
      id: String(state.id),
      path: path ? `${path}.${String(state.name)}` : String(state.name),
      pointer: `${base}/states/${i}`,
      parent,
      codedAncestor: nearest,
      underParallel,
    };
    out.push(node);
    walk(
      state.states,
      node.pointer,
      node,
      node.path,
      codeKey(state.code) !== null && !restless(state) ? node : nearest,
      underParallel || state.type === "parallel",
      out,
    );
  });
}

/** The states reachable from the start: the initial path, transition targets, and what entering a state enters. */
function reachable(process: Json, nodes: Node[]): Set<string> {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const seen = new Set<string>();
  const enter = (id: string | undefined) => {
    const node = id ? byId.get(id) : undefined;
    if (!node || seen.has(node.id)) return;
    seen.add(node.id);
    for (let up = node.parent; up; up = up.parent) seen.add(up.id);
    const children = arr(node.state.states);
    if (node.state.type === "parallel") for (const c of children) enter(String(c.id));
    else if (children.length && node.state.type !== "history") enter(typeof node.state.initial === "string" ? node.state.initial : String(children[0].id));
  };
  const roots = arr(process.states);
  enter(typeof process.initial === "string" ? process.initial : roots[0] ? String(roots[0].id) : undefined);
  const holds = (id: string) => {
    const node = byId.get(id);
    return !!node && (seen.has(id) || nodes.some((n) => seen.has(n.id) && n.parent?.id === id));
  };
  for (let changed = true; changed;) {
    changed = false;
    for (const t of arr(process.transitions)) {
      const source = typeof t.source === "string" ? t.source : "";
      if (!holds(source)) continue;
      for (const target of Array.isArray(t.targets) ? (t.targets as string[]) : []) {
        const before = seen.size;
        enter(target);
        if (seen.size !== before) changed = true;
      }
    }
  }
  return seen;
}

interface Bound {
  type: BindingType;
  subject: Json;
  attribute: Json;
}

/** The rows (code, label) of a reference type, from its seeds. */
function rowsOf(typeId: string, all: Iterable<ModelEntry>): { id: string; code: unknown; label: unknown }[] {
  const out: { id: string; code: unknown; label: unknown }[] = [];
  for (const e of all) {
    if (e.json.kind !== "seed" || e.json.target !== typeId || e.json.rowsFrom !== undefined) continue;
    const columns = (e.json.columns as string[] | undefined) ?? [];
    const code = columns.indexOf("code");
    const label = columns.indexOf("label");
    for (const r of arr(e.json.rows)) {
      const values = (r.values as unknown[] | undefined) ?? [];
      out.push({ id: String(r.id ?? ""), code: values[code], label: values[label] });
    }
  }
  return out;
}

/** Whether a code fits a plain status attribute as a default would; the reason when it does not. */
function plainMismatch(code: Code, attribute: Json, scalar: Json | undefined): string | null {
  const keyword = typeof attribute.type === "string" ? attribute.type : String(scalar?.base ?? "string");
  const integer = keyword.startsWith("int");
  if (integer) {
    if (typeof code !== "number") return `an integer type holds no string`;
    const limit = keyword === "int16" ? 32767 : keyword === "int32" ? 2147483647 : Number.MAX_SAFE_INTEGER;
    if (code > limit || code < -limit - 1) return `${code} is outside the range of ${keyword}`;
  } else {
    if (typeof code !== "string") return `a string type holds no integer`;
    const length = (attribute.length ?? scalar?.length) as number | undefined;
    if (typeof length === "number" && code.length > length) return `it is longer than ${length} characters`;
    const pattern = (attribute.validation as Json | undefined)?.pattern ?? (scalar?.validation as Json | undefined)?.pattern;
    if (typeof pattern === "string" && !new RegExp(pattern).test(code)) return `it does not match the pattern ${pattern}`;
  }
  const allowed = allowedOf(attribute, scalar);
  if (allowed.length && !allowed.some((v) => codeKey(v) === String(code))) return `it is not one of the allowed values`;
  return null;
}

const allowedOf = (attribute: Json, scalar: Json | undefined): unknown[] => {
  const own = (attribute.validation as Json | undefined)?.allowedValues;
  if (Array.isArray(own) && own.length) return own;
  const inherited = (scalar?.validation as Json | undefined)?.allowedValues;
  return Array.isArray(inherited) ? inherited : [];
};

/** MQ9206 clauses that hold with or without a binding (decision 1): one finding per state at its code. */
function codeDiagnostics(process: Json, nodes: Node[], emit: Emit): void {
  const coded = nodes.filter((n) => codeKey(n.state.code) !== null);
  if (!coded.length) return;
  const name = String(process.name);
  for (const n of coded.filter((c) => restless(c.state)))
    emit(
      "MQ9206",
      "error",
      `State '${String(n.state.name)}' of process '${name}' is a ${String(n.state.type)} state, which nothing rests in, so it has no code; remove its code.`,
      `${n.pointer}/code`,
      n.id,
    );
  const settled = coded.filter((c) => !restless(c.state));
  const byKey = new Map<string, Node>();
  for (const n of settled) {
    const key = codeKey(n.state.code)!;
    const first = byKey.get(key);
    if (first)
      emit(
        "MQ9206",
        "error",
        `States '${String(first.state.name)}' and '${String(n.state.name)}' of process '${name}' share code ${text(n.state.code as Code)} (1 and "1" are one code); give each state its own code.`,
        `${n.pointer}/code`,
        n.id,
      );
    else byKey.set(key, n);
  }
  const first = settled[0];
  if (!first) return;
  const firstInteger = typeof first.state.code === "number";
  for (const n of settled.filter((c) => (typeof c.state.code === "number") !== firstInteger))
    emit(
      "MQ9206",
      "error",
      `State '${String(n.state.name)}' of process '${name}' has ${typeof n.state.code === "number" ? "an integer" : "a string"} code, but the process's first code is ${firstInteger ? "an integer" : "a string"}; write every code of a process as strings, or every one as integers.`,
      `${n.pointer}/code`,
      n.id,
    );
}

/** The enum members' own identifiers: key, integer value, name. */
const memberIdentifiers = (m: Json): string[] => [m.code, m.value, m.name].filter((v) => v !== undefined).map(String);

export function bindingDiagnostics(json: Json, entry: ModelEntry, ctx: ValidationContext, emit: Emit): void {
  const name = String(json.name);
  const nodes: Node[] = [];
  walk(json.states, "", null, "", null, false, nodes);
  codeDiagnostics(json, nodes, emit);

  const lookup = (id: string): Json | undefined => ctx.lookup?.(id)?.json;
  const all = (): Iterable<ModelEntry> => ctx.all?.() ?? [];
  const listForm = Array.isArray(json.subjects);
  const entries = subjectsOf(json);
  const pointerOf = (i: number) => (listForm ? `/subjects/${i}/boundAttribute` : "/boundAttribute");
  const idOf = (i: number) => (listForm ? entries[i].id : entry.id);

  if (json.use !== "lifecycle") {
    entries.forEach((e, i) => {
      if (e.boundAttribute === undefined) return;
      emit(
        "MQ9202",
        "error",
        `Process '${name}' is an orchestration but ${listForm ? "its subject" : "it"} binds an attribute; only a lifecycle binds its states to an attribute. Remove boundAttribute or set use to lifecycle.`,
        pointerOf(i),
        idOf(i),
      );
    });
    return;
  }

  const bindings: Bound[] = [];
  const listed = new Set<string>();
  entries.forEach((e, i) => {
    if (listed.has(e.entity)) return; // MQ9201
    listed.add(e.entity);
    if (e.boundAttribute === undefined) return;
    const subject = lookup(e.entity);
    if (!subject || subject.kind !== "entity") return; // MQ2001 or MQ2002
    const pointer = pointerOf(i);
    const at = idOf(i);
    const sname = String(subject.name);
    const candidates = bindableAttributes(subject, lookup, (key) => ctx.stereotypes.get(key));
    const attribute = candidates.find((a) => a.id === e.boundAttribute);
    if (!attribute) {
      emit(
        "MQ9202",
        "error",
        `The bound attribute of process '${name}' is not an attribute of entity '${sname}' (own, inherited or from a stereotype); bind one of the subject's attributes that can hold the state.`,
        pointer,
        at,
      );
      return;
    }
    const type = classifyAttribute(attribute, lookup);
    if (type.kind === "unsupported") {
      emit(
        "MQ9202",
        "error",
        `The bound attribute '${String(attribute.name)}' of process '${name}' ${type.why}; bind a single-valued attribute of '${sname}' typed by an enum, a reference type, or a string or integer type.`,
        pointer,
        at,
      );
      return;
    }
    if (type.kind === "unknown") {
      emit(
        "MQ9202",
        "error",
        `The type of the bound attribute '${String(attribute.name)}' of process '${name}' is not in the model; bind a single-valued attribute of '${sname}' typed by an enum, a reference type, or a string or integer type.`,
        pointer,
        at,
      );
      return;
    }
    // Two lifecycles may not bind one attribute for an entity both list by name; two in the one-subject form already
    // disagree on the entity's lifecycle (MQ9201).
    for (const other of all()) {
      if (other.id === entry.id || other.json.kind !== "process" || other.json.use !== "lifecycle") continue;
      if (!listForm && !Array.isArray(other.json.subjects)) continue;
      if (subjectsOf(other.json).some((o) => o.entity === e.entity && o.boundAttribute === e.boundAttribute))
        emit(
          "MQ9202",
          "error",
          `Processes '${name}' and '${String(other.json.name)}' both bind attribute '${String(attribute.name)}' for entity '${sname}', which both list, and one attribute holds one lifecycle's state; bind another attribute in one of them.`,
          pointer,
          at,
        );
    }
    bindings.push({ type, subject, attribute });
    const sub = { sname, aname: String(attribute.name) };
    if (type.kind === "enum") enumBinding(json, nodes, entry, ctx, lookup, all, type.typeId!, attribute, sub, pointer, at, emit);
    else {
      const scalar = type.typeId && type.kind === "plain" ? lookup(type.typeId) : undefined;
      if (type.kind === "reference") referenceCodes(json, nodes, lookup(type.typeId!)!, type.typeId!, all, sub, emit);
      else plainCodes(json, nodes, attribute, scalar, sub, emit);
      initialCode(json, nodes, attribute, pointer, at, emit);
      uncovered(json, nodes, attribute, scalar, type, lookup, all, sub, pointer, at, emit);
    }
  });
  boundCodes(json, nodes, bindings, emit);
}

interface Names {
  sname: string;
  aname: string;
}

function enumBinding(
  process: Json,
  nodes: Node[],
  entry: ModelEntry,
  _ctx: ValidationContext,
  lookup: (id: string) => Json | undefined,
  all: () => Iterable<ModelEntry>,
  enumId: string,
  attribute: Json,
  n: Names,
  pointer: string,
  at: string,
  emit: Emit,
): void {
  const name = String(process.name);
  const enumDoc = lookup(enumId)!;
  const members = arr(enumDoc.members);
  const states = arr(process.states)
    .filter((s) => !restless(s))
    .map((s) => String(s.name));
  const memberNames = members.map((m) => String(m.name));
  const ename = String(enumDoc.name);
  if (states.join("\u0000") !== memberNames.join("\u0000")) {
    const missing = states.filter((s) => !memberNames.includes(s));
    const extra = memberNames.filter((m) => !states.includes(m));
    const parts = [...(missing.length ? [`missing ${names(missing)}`] : []), ...(extra.length ? [`extra ${names(extra)}`] : [])];
    if (!parts.length) parts.push("out of order");
    emit(
      "MQ9203",
      "error",
      `The members of enum '${ename}' differ from the states of process '${name}' (${names(states)}): ${parts.join("; ")}. Sync the enum from the process.`,
      pointer,
      at,
    );
  }
  // Subjects of one lifecycle may share the enum without warning about each other.
  const own = new Set(subjectsOf(process).map((s) => s.boundAttribute));
  const others: string[] = [];
  for (const e of all()) {
    if (e.id === enumId) continue;
    for (const a of arr(e.json.attributes))
      if ((a.type as Json | undefined)?.ref === enumId && !own.has(a.id as string)) others.push(`${String(e.json.name)}.${String(a.name)}`);
  }
  if (others.length)
    emit(
      "MQ9204",
      "warning",
      `Enum '${ename}', bound by process '${name}', is also used by ${names(others.sort().map((o) => `'${o}'`))}: syncing it from the process changes them too. Give the lifecycle its own enum if they should not follow.`,
      pointer,
      at,
    );
  const roots = arr(process.states);
  const initial = roots.find((s) => s.id === process.initial) ?? roots[0];
  if (initial && !restless(initial) && attribute.default !== initial.name) {
    const current = attribute.default === undefined ? "is not set" : `is ${JSON.stringify(attribute.default)}`;
    emit(
      "MQ9205",
      "warning",
      `The default of bound attribute '${n.aname}' ${current}, but process '${name}' starts in '${String(initial.name)}'; set the default to '${String(initial.name)}'.`,
      pointer,
      at,
    );
  }
  // A code names the same member as the state's name: its key, integer value or name (root states only).
  for (const node of nodes) {
    const code = node.state.code;
    if (codeKey(code) === null || restless(node.state)) continue;
    const c = code as Code;
    if (node.parent) {
      emit(
        "MQ9207",
        "error",
        `State '${node.path}' of process '${name}' has code ${text(c)}, but enum '${ename}', which attribute '${n.aname}' of entity '${n.sname}' binds, holds root states only, by name; move the code to root state '${node.path.split(".")[0]}', or remove it.`,
        `${node.pointer}/code`,
        node.id,
      );
      continue;
    }
    const member = members.find((m) => m.name === node.state.name);
    if (!member || memberIdentifiers(member).includes(String(c))) continue; // MQ9203
    const ids = [
      ...(member.code !== undefined ? [`'${String(member.code)}'`] : []),
      ...(member.value !== undefined ? [String(member.value)] : []),
      `'${String(member.name)}'`,
    ];
    emit(
      "MQ9207",
      "error",
      `State '${node.path}' of process '${name}' has code ${text(c)}, but member '${String(member.name)}' of enum '${ename}', which attribute '${n.aname}' of entity '${n.sname}' binds, is ${names(ids)}; write one of those as the code.`,
      `${node.pointer}/code`,
      node.id,
    );
  }
  void entry;
}

function referenceCodes(process: Json, nodes: Node[], type: Json, typeId: string, all: () => Iterable<ModelEntry>, n: Names, emit: Emit): void {
  const rows = rowsOf(typeId, all());
  const live = new Set(rows.map((r) => codeKey(r.code)).filter((k): k is string => k !== null));
  const former = new Map(arr(type.formerCodes).map((f) => [String(f.code), String(f.replacedBy)]));
  for (const node of nodes) {
    if (codeKey(node.state.code) === null || restless(node.state)) continue;
    const key = codeKey(node.state.code)!;
    if (live.has(key)) continue;
    const where = `State '${String(node.state.name)}' of process '${String(process.name)}' has code ${text(node.state.code as Code)}`;
    const binding = `reference type '${String(type.name)}', which attribute '${n.aname}' of entity '${n.sname}' binds`;
    const replacement = former.get(key);
    const current = replacement ? rows.find((r) => r.id === replacement)?.code : undefined;
    emit(
      "MQ9207",
      "error",
      replacement
        ? current !== undefined
          ? `${where}, a former code of ${binding}; write '${String(current)}', the code of the row that replaced it.`
          : `${where}, a former code of ${binding}; write the code of row ${replacement}, which replaced it.`
        : `${where}, which is not a code of ${binding}; write one of its codes, or add a row with it.`,
      `${node.pointer}/code`,
      node.id,
    );
  }
}

function plainCodes(process: Json, nodes: Node[], attribute: Json, scalar: Json | undefined, n: Names, emit: Emit): void {
  for (const node of nodes) {
    if (codeKey(node.state.code) === null || restless(node.state)) continue;
    const reason = plainMismatch(node.state.code as Code, attribute, scalar);
    if (!reason) continue;
    const shown = typeof attribute.type === "string" ? attribute.type : String(scalar?.name);
    emit(
      "MQ9207",
      "error",
      `State '${String(node.state.name)}' of process '${String(process.name)}' has code ${text(node.state.code as Code)}, which does not fit attribute '${n.aname}' of entity '${n.sname}' (${shown}), which the process binds: ${reason}; write a code that fits it.`,
      `${node.pointer}/code`,
      node.id,
    );
  }
}

/** MQ9205 for a binding by code: the default is the code of the configuration the process starts in, when it has one. */
function initialCode(process: Json, nodes: Node[], attribute: Json, pointer: string, at: string, emit: Emit): void {
  const roots = nodes.filter((n) => !n.parent);
  let node: Node | undefined = roots.find((r) => r.id === process.initial) ?? roots[0];
  while (node && node.state.type === "compound" && codeKey(node.state.code) === null) {
    const children: Node[] = nodes.filter((c) => c.parent === node);
    const current: Node = node;
    node = children.find((c) => c.id === current.state.initial) ?? children[0];
  }
  if (!node || restless(node.state) || codeKey(node.state.code) === null) return;
  const code = node.state.code as Code;
  if (codeKey(attribute.default) === String(code)) return;
  const current = attribute.default === undefined ? "is not set" : `is ${JSON.stringify(attribute.default)}`;
  emit(
    "MQ9205",
    "warning",
    `The default of bound attribute '${String(attribute.name)}' ${current}, but process '${String(process.name)}' starts in '${node.path}', whose code is ${text(code)}; set the default to ${text(code)}.`,
    pointer,
    at,
  );
}

/** MQ9208: the values the binding can hold that no state's code covers, once per binding. */
function uncovered(
  process: Json,
  nodes: Node[],
  attribute: Json,
  scalar: Json | undefined,
  type: BindingType,
  lookup: (id: string) => Json | undefined,
  all: () => Iterable<ModelEntry>,
  n: Names,
  pointer: string,
  at: string,
  emit: Emit,
): void {
  const allowed = allowedOf(attribute, scalar);
  const values: string[] = [];
  if (allowed.length) {
    for (const v of allowed) {
      const key = codeKey(v);
      if (key !== null && !values.includes(key)) values.push(key);
    }
  } else if (type.kind === "reference") {
    values.push(
      ...rowsOf(type.typeId!, all())
        .map((r) => codeKey(r.code))
        .filter((k): k is string => k !== null)
        .sort(),
    );
  }
  const covered = new Set(nodes.filter((c) => !restless(c.state)).map((c) => codeKey(c.state.code)));
  const missing = values.filter((v) => !covered.has(v));
  if (!missing.length) return;
  const shown = names(missing.slice(0, 10).map((v) => `'${v}'`)) + (missing.length > 10 ? ` and ${missing.length - 10} more` : "");
  const from = allowed.length
    ? `allowedValues of attribute '${n.aname}'`
    : `the rows of reference type '${String(lookup("typeId" in type ? String(type.typeId) : "")?.name)}'`;
  emit(
    "MQ9208",
    "warning",
    `No state of process '${String(process.name)}' has ${missing.length === 1 ? "the value" : "the values"} ${shown} from ${from} of entity '${n.sname}', which the process binds; add a state with each code, or narrow the attribute's allowedValues.`,
    pointer,
    at,
  );
}

/** What every binding of a lifecycle asks of its codes together: a map (MQ9206) and a code on each resting state (MQ9210). */
function boundCodes(process: Json, nodes: Node[], bindings: Bound[], emit: Emit): void {
  if (!bindings.length) return;
  const name = String(process.name);
  const coded = nodes.filter((c) => codeKey(c.state.code) !== null && !restless(c.state));
  if (coded.length && !coded.every((c) => !c.codedAncestor && !c.underParallel)) {
    for (const c of coded) {
      if (c.codedAncestor)
        emit(
          "MQ9206",
          "error",
          `State '${c.path}' of process '${name}' has a code, and so does '${c.codedAncestor.path}' above it, but the process binds an attribute, which holds one code per configuration; remove one of the two codes.`,
          `${c.pointer}/code`,
          c.id,
        );
      else if (c.underParallel)
        emit(
          "MQ9206",
          "error",
          `State '${c.path}' of process '${name}' has a code inside a region of a parallel state, but the process binds an attribute, which holds one code per configuration; give the parallel state, or a state above it, the code instead.`,
          `${c.pointer}/code`,
          c.id,
        );
    }
  }
  let needing = bindings.find((b) => b.type.kind !== "enum");
  if (!needing && !coded.length) return;
  needing ??= bindings[0];
  const above = new Set<string>();
  for (const c of coded) for (let p = c.parent; p && !above.has(p.id); p = p.parent) above.add(p.id);
  const live = reachable(process, nodes);
  const children = (parent: Node | null) => nodes.filter((n) => n.parent === parent);
  const visit = (parent: Node | null) => {
    for (const child of children(parent)) {
      if (restless(child.state) || codeKey(child.state.code) !== null) continue;
      if (above.has(child.id)) visit(child);
      else if (live.has(child.id))
        emit(
          "MQ9210",
          "warning",
          needing.type.kind === "enum"
            ? `State '${child.path}' has no code, though other states of process '${name}' have one; give it the code its member stands for, or remove the others.`
            : `State '${child.path}' of process '${name}' has no code, so attribute '${String(needing.attribute.name)}' of entity '${String(needing.subject.name)}', which the process binds, has no value for it; give the state a code.`,
          child.pointer,
          child.id,
        );
    }
  };
  visit(null);
}
