// The mock's validator: a deterministic subset of the engine's built-in rules (engine-design.md 6),
// enough for the editor's problems panel, inline save diagnostics and the invalid-save path. The
// rule ids, severities and messages follow RuleCatalog; line and column are left null.
import type { Diagnostic } from "@/api/types";
import Ajv2020 from "ajv/dist/2020";
import { schemaValidator } from "../contract";
import { referencesOf, subElementIds } from "./refs";

type Json = Record<string, unknown>;
export interface ModelEntry {
  id: string;
  path: string;
  json: Json;
}
export interface ValidationInput {
  entries: ModelEntry[];
  extensions: { name: string; appliesTo: { kinds: string[]; stereotypes: string[] }; properties: Json; required: string[] }[];
  /** validation.rules: rule id → error | warning | info | off */
  rules?: Record<string, string>;
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

function diag(rule: string, severity: Diagnostic["severity"], message: string, entry: ModelEntry, pointer: string): Diagnostic {
  return { rule, severity, message, elementId: entry.id, filePath: entry.path, jsonPointer: pointer, line: null, column: null };
}

/** MQ1002: the document against schemas/v1/<kind>.json. */
export function schemaDiagnostics(entry: ModelEntry): Diagnostic[] {
  const kind = String(entry.json.kind ?? "");
  let validate;
  try {
    validate = schemaValidator(kind);
  } catch {
    return [diag("MQ1002", "error", `/kind enum: '${kind}' is not a model kind`, entry, "/kind")];
  }
  if (validate(entry.json)) return [];
  const seen = new Set<string>();
  const out: Diagnostic[] = [];
  for (const e of validate.errors ?? []) {
    if (e.keyword === "oneOf" || e.keyword === "anyOf" || e.keyword === "if") continue;
    const pointer = e.instancePath;
    const message = `${pointer || "/"} ${e.keyword}: ${e.message ?? "is not valid"}`;
    if (seen.has(message)) continue;
    seen.add(message);
    out.push(diag("MQ1002", "error", message, entry, pointer));
  }
  return out;
}

const ajvForExtensions = new Ajv2020({ strict: false, allErrors: true });

/** The rule context one entry is checked against: which ids exist, the vocabularies and the name scopes. */
export interface ValidationContext extends ModelGlobals {
  hasId(id: string): boolean;
  extensions: ValidationInput["extensions"];
  /** The entry that uses `scope` first in path order, when that is not `entry` itself (MQ3001). */
  firstInScope(scope: string, entry: ModelEntry): ModelEntry | undefined;
}

/** The model-wide vocabularies: stereotypes by key, the category ids and the tag vocabulary. */
export interface ModelGlobals {
  stereotypes: Map<string, Json>;
  categories: Set<string> | null;
  vocabulary: { keys: Set<string>; strict: boolean } | null;
}

/** Kinds whose change can alter every other entry's diagnostics. */
export const GLOBAL_KINDS: ReadonlySet<string> = new Set(["stereotype", "category-tree", "tag-vocabulary"]);

export function globalsOf(entries: Iterable<ModelEntry>): ModelGlobals {
  const stereotypes = new Map<string, Json>();
  let categories: Set<string> | null = null;
  let vocabulary: { keys: Set<string>; strict: boolean } | null = null;
  for (const e of entries) {
    if (e.json.kind === "stereotype" && typeof e.json.key === "string") stereotypes.set(e.json.key, e.json);
    if (e.json.kind === "category-tree") categories = new Set(arr(e.json.categories).map((c) => String(c.id)));
    if (e.json.kind === "tag-vocabulary") vocabulary = { keys: new Set(arr(e.json.definitions).map((d) => String(d.key))), strict: e.json.strict === true };
  }
  return { stereotypes, categories, vocabulary };
}

/** MQ3001's scope of an element (kind group, package and lower-cased name), or null when the rule does not apply. */
export function nameScope(json: Json): string | null {
  const kind = String(json.kind);
  if (typeof json.name !== "string" || json.name === "" || !["entity", "value-object", "enum", "scalar-type", "relation", "diagram", "package"].includes(kind))
    return null;
  const group = ["entity", "value-object", "enum", "scalar-type"].includes(kind) ? "type" : kind;
  return `${group}|${String(json.package ?? json.parent ?? "")}|${json.name.toLowerCase()}`;
}

/** Every diagnostic of one entry, before validation.rules applies. */
export function entryDiagnostics(entry: ModelEntry, ctx: ValidationContext): Diagnostic[] {
  const { stereotypes, categories, vocabulary } = ctx;
  const out: Diagnostic[] = [];
  const json = entry.json;
  const kind = String(json.kind);
  out.push(...schemaDiagnostics(entry));

  // MQ2001 dangling references
  for (const ref of referencesOf(json)) {
    if (!ctx.hasId(ref.toId) && !(categories?.has(ref.toId) ?? false))
      out.push(diag("MQ2001", "error", `'${ref.field}' names ${ref.toId}, which does not exist.`, entry, ref.pointer));
  }
  // MQ2003 / MQ2004 stereotypes
  ((json.stereotypes as string[] | undefined) ?? []).forEach((key, i) => {
    const s = stereotypes.get(key);
    if (!s) out.push(diag("MQ2003", "error", `Unknown stereotype '${key}'.`, entry, `/stereotypes/${i}`));
    else if (Array.isArray(s.appliesTo) && s.appliesTo.length > 0 && !(s.appliesTo as string[]).includes(kind))
      out.push(diag("MQ2004", "error", `Stereotype '${key}' does not apply to ${kind}.`, entry, `/stereotypes/${i}`));
  });
  // MQ2005 categories
  if (typeof json.category === "string" && categories && !categories.has(json.category))
    out.push(diag("MQ2005", "error", `Unknown category ${json.category}.`, entry, "/category"));
  // MQ2006 tags
  if (vocabulary) {
    ((json.tags as string[] | undefined) ?? []).forEach((tag, i) => {
      if (!vocabulary!.keys.has(tag))
        out.push(diag("MQ2006", vocabulary!.strict ? "error" : "info", `Tag '${tag}' is not in the tag vocabulary.`, entry, `/tags/${i}`));
    });
  }
  // MQ3001 duplicate names in scope (kind group and package)
  const scope = nameScope(json);
  if (scope !== null) {
    const first = ctx.firstInScope(scope, entry);
    if (first) out.push(diag("MQ3001", "error", `The name '${String(json.name)}' is already used by ${first.path}.`, entry, "/name"));
  }
  if (kind === "entity") {
    const attributes = arr(json.attributes);
    const key = json.key as { attributes?: string[] } | undefined;
    // MQ3005 key
    if (!key && json.abstract !== true && !json.base)
      out.push(diag("MQ3005", "error", `Entity '${String(json.name)}' has no key; add 'key', or make it abstract or derived.`, entry, ""));
    // MQ3006 key attribute exists
    const ids = new Set(attributes.map((a) => String(a.id)));
    (key?.attributes ?? []).forEach((id, i) => {
      if (!ids.has(id)) out.push(diag("MQ3006", "error", `The key names ${id}, which is not an attribute of this entity.`, entry, `/key/attributes/${i}`));
    });
    // MQ3007 duplicate attribute names, stereotype (virtual) attributes included
    const seen = new Map<string, string>();
    for (const skey of (json.stereotypes as string[] | undefined) ?? [])
      for (const a of arr(stereotypes.get(skey)?.attributes)) seen.set(String(a.name).toLowerCase(), `stereotype '${skey}'`);
    attributes.forEach((a, i) => {
      const name = String(a.name ?? "").toLowerCase();
      if (!name) return;
      const previous = seen.get(name);
      if (previous)
        out.push(diag("MQ3007", "error", `Attribute name '${String(a.name)}' is used twice (also by ${previous}).`, entry, `/attributes/${i}/name`));
      else seen.set(name, `attribute ${i + 1}`);
    });
  }
  if (kind === "relation") {
    const ends = arr(json.ends);
    if (json.relationKind !== "n-ary" && ends.length !== 2)
      out.push(diag("MQ3008", "error", `A ${String(json.relationKind ?? "association")} has two ends; this one has ${ends.length}.`, entry, "/ends"));
    ends.forEach((e, i) => {
      if (e.onDelete === "set-null" && e.min === 1) out.push(diag("MQ3011", "error", "set-null on a required end.", entry, `/ends/${i}/onDelete`));
    });
  }
  // MQ5001 extension schemas
  for (const ext of ctx.extensions) {
    if (!ext.appliesTo.kinds.includes(kind)) continue;
    const own = (json.stereotypes as string[] | undefined) ?? [];
    if (ext.appliesTo.stereotypes.length > 0 && !ext.appliesTo.stereotypes.some((s) => own.includes(s))) continue;
    const properties = (json.properties as Json | undefined) ?? {};
    for (const [name, schema] of Object.entries(ext.properties)) {
      if (!(name in properties)) continue;
      const check = ajvForExtensions.compile(schema as object);
      if (!check(properties[name]))
        out.push(
          diag(
            "MQ5001",
            "error",
            `properties.${name} fails extension '${ext.name}': ${check.errors?.[0]?.message ?? "invalid"}.`,
            entry,
            `/properties/${name}`,
          ),
        );
    }
  }
  return out;
}

export function validateModel(input: ValidationInput): Diagnostic[] {
  const { entries } = input;
  const owner = new Set<string>();
  for (const e of entries) {
    owner.add(e.id);
    for (const sub of subElementIds(e.json)) owner.add(sub);
  }
  const names = new Map<string, ModelEntry>();
  const ctx: ValidationContext = {
    ...globalsOf(entries),
    hasId: (id) => owner.has(id),
    extensions: input.extensions,
    firstInScope: (scope, entry) => {
      const first = names.get(scope);
      if (!first) names.set(scope, entry);
      return first;
    },
  };
  const out: Diagnostic[] = [];
  const sorted = [...entries].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
  for (const entry of sorted) for (const d of entryDiagnostics(entry, ctx)) out.push(d);
  return applyRules(out, input.rules);
}

/** validation.rules: a rule set to error, warning or info changes its severity; off drops it (MQ1 rules always stay). */
export function applyRules(out: Diagnostic[], rules: Record<string, string> = {}): Diagnostic[] {
  return out
    .map((d) => (rules[d.rule] && rules[d.rule] !== "off" && !d.rule.startsWith("MQ1") ? { ...d, severity: rules[d.rule] as Diagnostic["severity"] } : d))
    .filter((d) => rules[d.rule] !== "off" || d.rule.startsWith("MQ1"));
}

export function diagnosticKey(d: Diagnostic): string {
  return `${d.rule}|${d.elementId}|${d.jsonPointer}|${d.message}`;
}
