// The mock's sync-codes (lifecycle-design.md decision 19), as the engine plans it: the states a binding needs a code on keep
// theirs, take the one `stateCodes` gives, else the one code of the attribute's type equal to their name (case, `_` and `-`
// ignored), else the one row or member whose label equals their display name or name. An ambiguous match, or a code another
// state keeps or takes, takes none. The binding is written in the form the file has.
import { bindableAttributes, classifyAttribute } from "@/model/binding";
import { subjectsOf } from "@/model/process";
import { codeKey } from "./lifecycleRules";

type Json = Record<string, unknown>;
type Code = string | number;

export interface SyncCodesPlan {
  process: string;
  entity: string;
  attribute: string;
  binds: boolean;
  codes: { state: string; path: string; code: Code; source: "kept" | "given" | "name" | "label" }[];
  unmatched: { state: string; path: string; candidates: Code[] }[];
}

export type SyncCodesOutcome = { problem: string } | { plan: SyncCodesPlan; next: Json };

interface Node {
  state: Json;
  path: string;
  parent: Node | null;
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const restless = (s: Json) => s.type === "history" || s.type === "choice";
const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
const loose = (text: string) => text.replace(/[_-]/g, "").toUpperCase();

function nodesOf(states: unknown, parent: Node | null, path: string, out: Node[]): Node[] {
  for (const state of arr(states)) {
    const node: Node = { state, path: path ? `${path}.${String(state.name)}` : String(state.name), parent };
    out.push(node);
    nodesOf(state.states, node, node.path, out);
  }
  return out;
}

interface Candidate {
  code: Code;
  key: string;
  names: string[];
  label?: string;
}

function candidatesOf(
  kind: string,
  typeId: string | undefined,
  attribute: Json,
  scalar: Json | undefined,
  all: Iterable<Json>,
  lookup: (id: string) => Json | undefined,
  integerCodes: boolean,
): Candidate[] {
  const list: Candidate[] = [];
  const seen = new Set<string>();
  const add = (code: unknown, names: string[], label?: string) => {
    const key = codeKey(code);
    if (key !== null && !seen.has(key)) {
      seen.add(key);
      list.push({ code: code as Code, key, names, label });
    }
  };
  if (kind === "reference" && typeId) {
    const seeds = [...all]
      .filter((d) => d.kind === "seed" && d.target === typeId && d.rowsFrom === undefined)
      .sort((a, b) => ordinal(String(a.name), String(b.name)) || ordinal(String(a.id), String(b.id)));
    for (const seed of seeds) {
      const columns = (seed.columns as string[] | undefined) ?? [];
      const at = columns.indexOf("code");
      const labelAt = columns.indexOf("label");
      if (at < 0) continue;
      for (const row of arr(seed.rows)) {
        const values = (row.values as unknown[] | undefined) ?? [];
        const key = codeKey(values[at]);
        if (key === null) continue;
        add(values[at], [key], typeof values[labelAt] === "string" ? (values[labelAt] as string) : undefined);
      }
    }
  } else if (kind === "enum" && typeId) {
    for (const member of arr(lookup(typeId)?.members)) {
      const names = [
        String(member.name),
        ...(member.code !== undefined ? [String(member.code)] : []),
        ...(member.value !== undefined ? [String(member.value)] : []),
      ];
      const label = typeof member.displayName === "string" ? member.displayName : undefined;
      if (integerCodes) {
        if (member.value !== undefined) add(member.value, names, label);
      } else add(member.code ?? member.name, names, label);
    }
  } else {
    const own = (attribute.validation as Json | undefined)?.allowedValues;
    const allowed = Array.isArray(own) && own.length ? own : (((scalar?.validation as Json | undefined)?.allowedValues as unknown[] | undefined) ?? []);
    for (const value of allowed) {
      const key = codeKey(value);
      if (key !== null) add(value, [key]);
    }
  }
  return list;
}

/** Plans sync-codes over a model's documents (`entries` by id); `newId` mints a subjects entry's id. */
export function syncCodesOf(
  model: { entries: ReadonlyMap<string, { json: Json }> },
  process: Json,
  entity: string | undefined,
  attribute: string | undefined,
  stateCodes: Json | undefined,
  newId: () => string,
): SyncCodesOutcome {
  const all = [...model.entries.values()].map((e) => e.json);
  const stereotype = (key: string) => all.find((d) => d.kind === "stereotype" && d.key === key);
  return planSyncCodes(process, entity, attribute, stateCodes, (id) => model.entries.get(id)?.json, all, stereotype, newId);
}

/** Plans sync-codes on a process document; `all` is every document (a reference type's seeds), `newId` mints a subjects entry id. */
export function planSyncCodes(
  process: Json,
  entityId: string | undefined,
  attributeId: string | undefined,
  given: Record<string, unknown> | undefined,
  lookup: (id: string) => Json | undefined,
  all: Iterable<Json>,
  stereotype: (key: string) => Json | undefined,
  newId: () => string,
): SyncCodesOutcome {
  const name = String(process.name);
  const id = String(process.id);
  if (process.use !== "lifecycle")
    return { problem: `Process '${name}' is an orchestration, which binds no attribute; set use to lifecycle first, then sync its codes.` };
  const entity = entityId ? lookup(entityId) : undefined;
  if (!entity || entity.kind !== "entity")
    return { problem: `'${String(entityId)}' is not an entity; pass the subject whose attribute holds the state as entity.` };
  if (process.boundAttribute !== undefined && typeof process.subject !== "string")
    return { problem: `Process '${name}' binds an attribute but names no subject; set its subject (or list its subjects) first, then sync its codes.` };
  const oneSubject = typeof process.subject === "string" && !Array.isArray(process.subjects);
  if (oneSubject && process.subject !== entityId)
    return {
      problem: `Process '${name}' names ${lookup(String(process.subject)) ? `entity '${String(lookup(String(process.subject))!.name)}'` : "no entity"} as its one subject, so sync-codes binds that subject's attribute only; pass it as entity, or list the subjects first (use-subjects).`,
    };
  const attribute = bindableAttributes(entity, lookup, stereotype).find((a) => a.id === attributeId);
  if (!attributeId || !attribute)
    return {
      problem: `'${String(attributeId)}' is not an attribute of entity '${String(entity.name)}' (own, inherited or from a stereotype); pass the one that holds the state.`,
    };
  const type = classifyAttribute(attribute, lookup);
  if (type.kind === "unsupported")
    return {
      problem: `Attribute '${String(attribute.name)}' of entity '${String(entity.name)}' ${type.why}; pass an attribute typed by an enum, a reference type, or a string or integer type.`,
    };
  if (type.kind === "unknown") return { problem: `The type of attribute '${String(attribute.name)}' is not in the model.` };

  const nodes = nodesOf(process.states, null, "", []);
  const byId = new Map(nodes.map((n) => [String(n.state.id), n]));
  const stateCodes = new Map<string, Code>();
  for (const [stateId, code] of Object.entries(given ?? {}).sort(([a], [b]) => (a < b ? -1 : 1))) {
    const node = byId.get(stateId);
    if (!node) return { problem: `'${stateId}' (a key of stateCodes) is not a state of process '${name}'.` };
    if (restless(node.state))
      return {
        problem: `State '${node.path}' of process '${name}' is a ${String(node.state.type)} state, which nothing rests in, so it takes no code; leave it out of stateCodes.`,
      };
    if (!(typeof code === "string" ? code.length > 0 : typeof code === "number" && Number.isInteger(code)))
      return { problem: `The code stateCodes gives state '${node.path}' is ${JSON.stringify(code)}; a code is a non-empty string or an integer.` };
    stateCodes.set(stateId, code as Code);
  }

  const coded = nodes.filter((n) => codeKey(n.state.code) !== null);
  // States a binding needs a code on: walking down, one with no code and no coded state below it.
  const above = new Set<Node>();
  for (const n of coded.filter((c) => !restless(c.state))) for (let p = n.parent; p && !above.has(p); p = p.parent) above.add(p);
  const needing = new Set<Node>();
  const visit = (parent: Node | null) => {
    for (const n of nodes.filter((c) => c.parent === parent)) {
      if (restless(n.state) || codeKey(n.state.code) !== null) continue;
      if (above.has(n)) visit(n);
      else needing.add(n);
    }
  };
  visit(null);

  const taken = new Set<string>();
  for (const n of coded) if (!restless(n.state) && !stateCodes.has(String(n.state.id))) taken.add(codeKey(n.state.code)!);
  for (const code of stateCodes.values()) taken.add(String(code));

  const resting = coded.filter((n) => !restless(n.state));
  const integerCodes = resting.length > 0 && resting.every((n) => Number.isInteger(n.state.code));
  const scalar = type.kind === "plain" && type.typeId ? lookup(type.typeId) : undefined;
  const candidates = candidatesOf(type.kind, "typeId" in type ? type.typeId : undefined, attribute, scalar, all, lookup, integerCodes);

  const matched = new Map<Node, { candidate: Candidate; source: "name" | "label" }>();
  const unmatched = new Map<Node, Code[]>();
  for (const n of nodes) {
    if (!needing.has(n) || stateCodes.has(String(n.state.id))) continue;
    const key = loose(String(n.state.name));
    const byName = candidates.filter((c) => c.names.some((x) => loose(x) === key));
    const display = typeof n.state.displayName === "string" ? n.state.displayName.toLowerCase() : undefined;
    const byLabel = byName.length
      ? []
      : candidates.filter((c) => c.label !== undefined && (c.label.toLowerCase() === display || c.label.toLowerCase() === String(n.state.name).toLowerCase()));
    const found = byName.length ? byName : byLabel;
    if (found.length === 1 && !taken.has(found[0].key)) matched.set(n, { candidate: found[0], source: byName.length ? "name" : "label" });
    else
      unmatched.set(
        n,
        found.map((c) => c.code),
      );
  }
  // A code two states match goes to neither.
  const groups = new Map<string, Node[]>();
  for (const [n, m] of matched) groups.set(m.candidate.key, [...(groups.get(m.candidate.key) ?? []), n]);
  for (const [, group] of groups)
    if (group.length > 1)
      for (const n of group) {
        unmatched.set(n, [matched.get(n)!.candidate.code]);
        matched.delete(n);
      }

  const next = structuredClone(process) as Json;
  const nextNodes = new Map(nodesOf(next.states, null, "", []).map((n) => [String(n.state.id), n]));
  const plan: SyncCodesPlan = { process: id, entity: String(entityId), attribute: attributeId, binds: false, codes: [], unmatched: [] };
  for (const n of nodes) {
    const stateId = String(n.state.id);
    const write = (code: Code) => void (nextNodes.get(stateId)!.state.code = code);
    const wanted = stateCodes.get(stateId);
    const match = matched.get(n);
    if (wanted !== undefined) {
      plan.codes.push({ state: stateId, path: n.path, code: wanted, source: "given" });
      write(wanted);
    } else if (codeKey(n.state.code) !== null && !restless(n.state)) {
      plan.codes.push({ state: stateId, path: n.path, code: n.state.code as Code, source: "kept" });
    } else if (match) {
      plan.codes.push({ state: stateId, path: n.path, code: match.candidate.code, source: match.source });
      write(match.candidate.code);
    } else if (unmatched.has(n)) plan.unmatched.push({ state: stateId, path: n.path, candidates: unmatched.get(n)! });
  }

  const current = subjectsOf(process).find((e) => e.entity === entityId)?.boundAttribute;
  plan.binds = current !== attributeId;
  if (plan.binds) {
    if (oneSubject) next.boundAttribute = attributeId;
    else if (Array.isArray(next.subjects)) {
      const entries = next.subjects as Json[];
      const entry = entries.find((e) => e.entity === entityId);
      if (entry) entry.boundAttribute = attributeId;
      else entries.push({ id: newId(), entity: entityId, boundAttribute: attributeId });
    } else next.subjects = [{ id: newId(), entity: entityId, boundAttribute: attributeId }];
  }
  return { plan, next };
}
