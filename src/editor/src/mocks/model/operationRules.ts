// The mock's rules for operations linked to processes (processes-operations-design.md section 8): MQ3032 an operation and a process it is
// linked to (fires, a call, a service step) are about different objects, MQ3044 an operation fires an event its input can't supply,
// MQ9024 an event nothing fires (reported always here; applyRules drops it unless validation.rules gives it a severity), MQ9025 a call
// or service step that can't be read, MQ9026 a call leaves a required input ungiven, and the call case of MQ9020 in a library.
import { subjectsOf } from "@/model/process";
import { Coverage } from "./coverage";

type Json = Record<string, unknown>;
const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string | undefined => (typeof v === "string" && v ? v : undefined);

export interface LinkFinding {
  rule: "MQ3032" | "MQ3044" | "MQ9020" | "MQ9024" | "MQ9025" | "MQ9026";
  severity: "error" | "warning" | "info";
  message: string;
  pointer: string;
  elementId?: string;
}

export interface LinkLookup {
  get(id: string): Json | undefined;
  /** Every document. */
  all(): Iterable<Json>;
  stereotype(key: string): Json | undefined;
}

/** The entity and its bases, nearest first. */
function chain(id: string | undefined, lookup: LinkLookup): string[] {
  const out: string[] = [];
  for (let at = id; at && !out.includes(at); at = str(lookup.get(at)?.base)) out.push(at);
  return out;
}

const coverages = new WeakMap<LinkLookup, Coverage>();
/** Package defaults, implied stereotypes and carriers are read the way the engine does (coverage.ts); built once per lookup. */
function coverageOf(lookup: LinkLookup): Coverage {
  let c = coverages.get(lookup);
  if (!c) {
    c = new Coverage([...lookup.all()].map((json) => ({ id: String(json.id), path: "", json })));
    coverages.set(lookup, c);
  }
  return c;
}

/** Whether a process governs, or governs something derived from, the operation's subject (the matching rule of decision 2, `OperationLinks.LinesUp`). */
export function aboutSameObjects(process: Json, subject: string, lookup: LinkLookup): boolean {
  const coverage = coverageOf(lookup);
  if (coverage.governs(process, subject)) return true;
  return subjectsOf(process).some((s) => {
    if (s.entity) return chain(s.entity, lookup).includes(subject);
    if (s.stereotype === undefined) return false;
    return coverage.carriers(s.stereotype).some((c) => chain(c, lookup).includes(subject));
  });
}

type Member = Json;
/** A member's kind: a typed value, a record key, or a file; with whether it is a collection. */
function typeKey(m: Member, lookup: LinkLookup): string | null {
  if (m.file === true) return null;
  const keyOf = str(m.keyOf);
  if (keyOf) {
    return keyAttributeType(keyOf, lookup);
  }
  const t = m.type;
  return typeof t === "string" ? t : t && typeof t === "object" && typeof (t as Json).ref === "string" ? `ref:${(t as Json).ref as string}` : null;
}

function keyAttributeIds(entity: string, lookup: LinkLookup): string[] {
  for (const id of chain(entity, lookup)) {
    const key = lookup.get(id)?.key as Json | undefined;
    if (Array.isArray(key?.attributes)) return key!.attributes as string[];
  }
  return [];
}

function keyAttributeType(entity: string, lookup: LinkLookup): string | null {
  const ids = keyAttributeIds(entity, lookup);
  if (ids.length !== 1) return null;
  for (const id of chain(entity, lookup)) {
    const a = arr(lookup.get(id)?.attributes).find((x) => x.id === ids[0]);
    if (a)
      return typeof a.type === "string"
        ? a.type
        : a.type && typeof a.type === "object" && typeof (a.type as Json).ref === "string"
          ? `ref:${(a.type as Json).ref as string}`
          : null;
  }
  return null;
}

/** MQ3032 and MQ3044 on an operation's file. */
export function operationLinkDiagnostics(operation: Json, lookup: LinkLookup): LinkFinding[] {
  const out: LinkFinding[] = [];
  const subject = str(operation.subject);
  const owners = new Map<string, Json>();
  for (const p of lookup.all()) if (p.kind === "process") for (const e of arr(p.events)) if (str(e.id)) owners.set(String(e.id), p);
  const members = arr(operation.input);
  // A repeated id is judged once, at its first entry.
  const judged = new Set<string>();
  (Array.isArray(operation.fires) ? (operation.fires as unknown[]) : []).forEach((raw, i) => {
    const id = String(raw);
    if (judged.has(id)) return;
    judged.add(id);
    const process = owners.get(id);
    const event = arr(process?.events).find((e) => e.id === id);
    if (!process || !event) return;
    const where = `Event '${String(event.name)}' of process '${String(process.name)}'`;
    if (subject && subjectsOf(process).length && !aboutSameObjects(process, subject, lookup))
      out.push({
        rule: "MQ3032",
        severity: "warning",
        message: `${where} is fired by operation '${String(operation.name)}', but the process governs no record of the operation's subject; check that they are about the same objects.`,
        pointer: `/fires/${i}`,
      });
    for (const attribute of arr(event.payload)) {
      const same = members.find((m) => m.name === attribute.name);
      const wanted = typeKey(attribute, lookup);
      const given = same ? typeKey(same, lookup) : null;
      const fits = !!same && given !== null && given === wanted && (same.collection === true) === (attribute.collection === true);
      const optional = attribute.required !== true || attribute.default !== undefined || attribute.defaultExpression !== undefined;
      if (!same && !optional)
        out.push({
          rule: "MQ3044",
          severity: "warning",
          message: `${where} needs '${String(attribute.name)}', which no input of operation '${String(operation.name)}' has; add an input of that name, or let your system fill it.`,
          pointer: `/fires/${i}`,
        });
      else if (same && !fits)
        out.push({
          rule: "MQ3044",
          severity: "warning",
          message: `Input '${String(same.name)}' of operation '${String(operation.name)}' does not fit the payload attribute of the same name of event '${String(event.name)}' (another type or form).`,
          pointer: `/fires/${i}`,
        });
    }
  });
  return out;
}

/** The built-in keywords the expression language has values of (`ExprType.FromBuiltin`); float, binary and duration are not among them. */
const EXPRESSION_BUILTINS = new Set([
  "string",
  "text",
  "bool",
  "int16",
  "int32",
  "int64",
  "decimal",
  "double",
  "date",
  "time",
  "datetime",
  "datetimeoffset",
  "uuid",
  "ulid",
  "json",
]);

/** Whether the expression language has a type (`ExprSites.TypeOf` then `IsAvailable`); a reference that does not resolve counts as available (MQ2001 says it). */
function typeAvailable(type: unknown, lookup: LinkLookup): boolean {
  if (typeof type === "string") return EXPRESSION_BUILTINS.has(type);
  const id = type && typeof type === "object" ? str((type as Json).ref) : undefined;
  if (!id) return true;
  const doc = lookup.get(id);
  if (!doc) return true;
  if (doc.kind === "enum" || doc.kind === "reference-type") return true;
  if (doc.kind === "scalar-type") return typeof doc.base !== "string" || EXPRESSION_BUILTINS.has(doc.base);
  return false;
}

/** Whether a member can be given a value by a call (decision 6, `ExprSites.MemberType`): not a file, a collection, a key of an entity without one key attribute, or a type the language lacks. */
export function givable(m: Member, lookup: LinkLookup): boolean {
  if (m.file === true || m.collection === true) return false;
  const keyOf = str(m.keyOf);
  if (keyOf) {
    if (lookup.get(keyOf)?.kind !== "entity") return true;
    const ids = keyAttributeIds(keyOf, lookup);
    if (ids.length !== 1) return false;
    const attribute = chain(keyOf, lookup)
      .flatMap((id) => arr(lookup.get(id)?.attributes))
      .find((a) => a.id === ids[0]);
    return !!attribute && typeAvailable(attribute.type, lookup);
  }
  return m.type === undefined || typeAvailable(m.type, lookup);
}

/** The rules of a process's own file: calls, service steps, the library case and the events nothing fires. */
export function processLinkDiagnostics(process: Json, lookup: LinkLookup): LinkFinding[] {
  const out: LinkFinding[] = [];
  const library = process.use === "library";
  const processName = String(process.name);
  const hasSubjects = subjectsOf(process).length > 0;
  arr(process.actions).forEach((action, i) => {
    const call = action.call as Json | undefined;
    if (!call) return;
    const at = `/actions/${i}/call`;
    const actionName = String(action.name);
    if (library) {
      out.push({
        rule: "MQ9020",
        severity: "error",
        message: `Action '${actionName}' of library '${processName}' calls an operation, which names the attributes of one process's subjects; move the action to the process that runs it.`,
        pointer: at,
        elementId: str(action.id),
      });
      return;
    }
    const operation = lookup.get(String(call.operation));
    if (!operation || operation.kind !== "operation") return;
    const members = arr(operation.input);
    const seen = new Set<string>();
    arr(call.inputs).forEach((input, j) => {
      const member = members.find((m) => m.id === input.member);
      const pointer = `${at}/inputs/${j}`;
      if (!member)
        out.push({
          rule: "MQ9025",
          severity: "error",
          message: `The call of action '${actionName}' gives an input that is not an input of operation '${String(operation.name)}'; remove it.`,
          pointer: `${pointer}/member`,
          elementId: str(input.id),
        });
      else if (seen.has(member.id as string))
        out.push({
          rule: "MQ9025",
          severity: "error",
          message: `The call of action '${actionName}' gives input '${String(member.name)}' twice; remove one.`,
          pointer: `${pointer}/member`,
          elementId: str(input.id),
        });
      else if (!givable(member, lookup))
        out.push({
          rule: "MQ9025",
          severity: "error",
          message: `Input '${String(member.name)}' of operation '${String(operation.name)}' is a file, a list, a record key without one key attribute, or a type the expression language has no values of, which a call can't give; your system supplies it. Remove it from the call.`,
          pointer: `${pointer}/member`,
          elementId: str(input.id),
        });
      if (member) seen.add(member.id as string);
    });
    for (const m of members)
      if (m.required === true && m.default === undefined && givable(m, lookup) && !seen.has(m.id as string))
        out.push({
          rule: "MQ9026",
          severity: "warning",
          message: `The call of action '${actionName}' leaves input '${String(m.name)}' of operation '${String(operation.name)}' without a value; your system must supply it, or give it a value here.`,
          pointer: at,
          elementId: str(action.id),
        });
    const subject = str(operation.subject);
    if (hasSubjects && subject && (operation.level ?? "record") === "record" && !aboutSameObjects(process, subject, lookup))
      out.push({
        rule: "MQ3032",
        severity: "warning",
        message: `Action '${actionName}' calls operation '${String(operation.name)}', which acts on records the process does not govern; the model does not say which record.`,
        pointer: `${at}/operation`,
        elementId: str(action.id),
      });
  });
  const steps = (list: unknown, base: string) =>
    arr(list).forEach((st, i) => {
      arr(st.invoke).forEach((inv, k) => {
        const operation = str(inv.operation);
        if (!operation) return;
        const pointer = `${base}/${i}/invoke/${k}/operation`;
        if (inv.type !== "service") {
          out.push({
            rule: "MQ9025",
            severity: "error",
            message: `Step '${String(inv.name ?? st.name)}' names an operation but is not a service step; clear the operation, or make it a service step.`,
            pointer,
            elementId: str(inv.id),
          });
          return;
        }
        const op = lookup.get(operation);
        const subject = str(op?.subject);
        if (op && hasSubjects && subject && (op.level ?? "record") === "record" && !aboutSameObjects(process, subject, lookup))
          out.push({
            rule: "MQ3032",
            severity: "warning",
            message: `Service step '${String(inv.name ?? st.name)}' runs operation '${String(op.name)}', which acts on records the process does not govern.`,
            pointer,
            elementId: str(inv.id),
          });
      });
      steps(st.states, `${base}/${i}/states`);
    });
  steps(process.states, "/states");
  // MQ9024: an event that some event transition uses, no action raises and no operation fires.
  if (!library) {
    const raised = new Set(arr(process.actions).flatMap((a) => (Array.isArray(a.raises) ? (a.raises as string[]) : [])));
    const used = new Set(arr(process.transitions).flatMap((t) => (str(t.event) ? [String(t.event)] : [])));
    const fired = new Set<string>();
    for (const o of lookup.all()) if (o.kind === "operation") for (const f of Array.isArray(o.fires) ? (o.fires as string[]) : []) fired.add(f);
    arr(process.events).forEach((e, i) => {
      const id = String(e.id);
      if (used.has(id) && !raised.has(id) && !fired.has(id))
        out.push({
          rule: "MQ9024",
          severity: "info",
          message: `Event '${String(e.name)}' of process '${processName}' is used by a move, but no action raises it and no operation fires it.`,
          pointer: `/events/${i}`,
          elementId: id,
        });
    });
  }
  return out;
}
