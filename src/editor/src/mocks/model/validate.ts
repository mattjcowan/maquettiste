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
  /** Another entry by id (MQ9203 reads the subject entity and its bound enum). */
  lookup?(id: string): ModelEntry | undefined;
}

/** MQ9203: a lifecycle's root-level states (not history or choice) against its bound enum's members, in order. */
export function enumDrift(json: Json, lookup: (id: string) => ModelEntry | undefined): { states: string[]; members: string[]; enumId: string } | null {
  if (json.kind !== "process" || json.use !== "lifecycle" || typeof json.subject !== "string" || typeof json.boundAttribute !== "string") return null;
  const subject = lookup(json.subject)?.json;
  const attribute = arr(subject?.attributes).find((a) => a.id === json.boundAttribute);
  const ref = (attribute?.type as Json | undefined)?.ref;
  const enumEntry = typeof ref === "string" ? lookup(ref) : undefined;
  if (!enumEntry || enumEntry.json.kind !== "enum") return null;
  const states = arr(json.states)
    .filter((st) => st.type !== "history" && st.type !== "choice")
    .map((st) => String(st.name));
  const members = arr(enumEntry.json.members).map((m) => String(m.name));
  return states.join("\u0000") === members.join("\u0000") ? null : { states, members, enumId: enumEntry.id };
}

/** One tag vocabulary or category tree of a scope ("" is global, else the domain's package id). */
interface ScopedVocabulary {
  keys: Set<string>;
  strict: boolean;
}

/**
 * The model-wide vocabularies: stereotypes by key, every category id, the global tag vocabulary,
 * and (explorer-redesign.md section 1.11) one tag vocabulary and one category tree per scope with
 * the package parents that make up each element's chain: its domain, every enclosing one, global.
 */
export interface ModelGlobals {
  stereotypes: Map<string, Json>;
  categories: Set<string> | null;
  vocabulary: { keys: Set<string>; strict: boolean } | null;
  tagVocabularies: Map<string, ScopedVocabulary>;
  categoryTrees: Map<string, ScopedVocabulary>;
  categoryScope: Map<string, string>;
  packages: Map<string, { parent: string | null; name: string }>;
}

/** Kinds whose change can alter every other entry's diagnostics. */
export const GLOBAL_KINDS: ReadonlySet<string> = new Set(["stereotype", "category-tree", "tag-vocabulary"]);

export function globalsOf(entries: Iterable<ModelEntry>): ModelGlobals {
  const stereotypes = new Map<string, Json>();
  let categories: Set<string> | null = null;
  const tagVocabularies = new Map<string, ScopedVocabulary>();
  const categoryTrees = new Map<string, ScopedVocabulary>();
  const categoryScope = new Map<string, string>();
  const packages = new Map<string, { parent: string | null; name: string }>();
  for (const e of entries) {
    const kind = e.json.kind;
    const scope = typeof e.json.package === "string" ? e.json.package : "";
    if (kind === "stereotype" && typeof e.json.key === "string") stereotypes.set(e.json.key, e.json);
    if (kind === "package") packages.set(e.id, { parent: typeof e.json.parent === "string" ? e.json.parent : null, name: String(e.json.name ?? e.id) });
    if (kind === "category-tree" && !categoryTrees.has(scope)) {
      // The first tree of a scope counts; a second one is MQ1009 and ignored.
      const items = arr(e.json.categories);
      categories ??= new Set();
      for (const c of items) {
        categories.add(String(c.id));
        categoryScope.set(String(c.id), scope);
      }
      categoryTrees.set(scope, { keys: new Set(items.map((c) => String(c.name))), strict: false });
    }
    if (kind === "tag-vocabulary" && !tagVocabularies.has(scope))
      tagVocabularies.set(scope, { keys: new Set(arr(e.json.definitions).map((d) => String(d.key))), strict: e.json.strict === true });
  }
  return { stereotypes, categories, vocabulary: tagVocabularies.get("") ?? null, tagVocabularies, categoryTrees, categoryScope, packages };
}

/** The scopes an element sees, nearest first: its domain (a package is its own), every enclosing one, then global (""). */
export function vocabularyChain(json: Json, globals: ModelGlobals): string[] {
  const start = json.kind === "package" ? (typeof json.id === "string" ? json.id : null) : typeof json.package === "string" ? json.package : null;
  const chain: string[] = [];
  for (let at = start; at !== null && !chain.includes(at); at = globals.packages.get(at)?.parent ?? null) chain.push(at);
  chain.push("");
  return chain;
}

function scopeName(scope: string, globals: ModelGlobals): string {
  return scope === "" ? "the model (global)" : `domain '${globals.packages.get(scope)?.name ?? scope}'`;
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
  const chain = vocabularyChain(json, ctx);
  // MQ2005 categories; MQ2008 a category of a tree outside the element's chain
  if (typeof json.category === "string" && categories && !categories.has(json.category))
    out.push(diag("MQ2005", "error", `Unknown category ${json.category}.`, entry, "/category"));
  else if (typeof json.category === "string" && categories) {
    const scope = ctx.categoryScope.get(json.category) ?? "";
    if (!chain.includes(scope))
      out.push(
        diag(
          "MQ2008",
          "error",
          `Category '${json.category}' is declared in the category tree of ${scopeName(scope, ctx)}, outside this element's domain chain.`,
          entry,
          "/category",
        ),
      );
  }
  // MQ2006 tags along the chain; MQ2008 a tag declared only outside it
  if (vocabulary || ctx.tagVocabularies.size > 0) {
    const seen = chain.map((scope) => ctx.tagVocabularies.get(scope)).filter((v): v is ScopedVocabulary => !!v);
    const strict = seen.some((v) => v.strict);
    ((json.tags as string[] | undefined) ?? []).forEach((tag, i) => {
      if (seen.some((v) => v.keys.has(tag))) return;
      const outside = [...ctx.tagVocabularies].find(([, v]) => v.keys.has(tag));
      if (outside)
        out.push(
          diag(
            "MQ2008",
            "error",
            `Tag '${tag}' is declared only in the tag vocabulary of ${scopeName(outside[0], ctx)}, outside this element's domain chain.`,
            entry,
            `/tags/${i}`,
          ),
        );
      else if (seen.length > 0)
        out.push(
          diag(
            "MQ2006",
            strict ? "error" : "info",
            `Tag '${tag}' is not declared in the tag vocabulary${seen.length > 1 ? " of any domain on its chain" : ""}${strict ? ", which is strict" : ""}.`,
            entry,
            `/tags/${i}`,
          ),
        );
    });
  }
  // MQ3021 a domain vocabulary redeclaring a tag key or category name of the global vocabulary or an enclosing domain's
  if ((kind === "tag-vocabulary" || kind === "category-tree") && typeof json.package === "string") {
    const enclosing = chain.slice(1);
    const tags = kind === "tag-vocabulary";
    const items = arr(tags ? json.definitions : json.categories);
    const declared = tags ? ctx.tagVocabularies : ctx.categoryTrees;
    items.forEach((item, i) => {
      const value = String(tags ? item.key : item.name);
      const scope = enclosing.find((sc) => declared.get(sc)?.keys.has(value));
      if (scope !== undefined)
        out.push(
          diag(
            "MQ3021",
            "error",
            `${tags ? "Tag" : "Category"} '${value}' is already declared by ${scopeName(scope, ctx)}; a domain vocabulary cannot redeclare it.`,
            entry,
            tags ? `/definitions/${i}/key` : `/categories/${i}/name`,
          ),
        );
    });
  }
  // MQ3001 duplicate names in scope (kind group and package)
  const scope = nameScope(json);
  if (scope !== null) {
    const first = ctx.firstInScope(scope, entry);
    if (first) out.push(diag("MQ3001", "error", `The name '${String(json.name)}' is already used by ${first.path}.`, entry, "/name"));
  }
  // MQ3022 a diagram that follows a package (membership "package") but names none
  if (kind === "diagram" && json.membership === "package" && !json.package)
    out.push(
      diag(
        "MQ3022",
        "error",
        `Diagram '${String(json.name)}' follows a package (membership 'package') but names none; set its package, or set membership to 'explicit'.`,
        entry,
        "/membership",
      ),
    );
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
  // MQ9203 enum drift
  if (kind === "process" && ctx.lookup) {
    const drift = enumDrift(json, ctx.lookup);
    if (drift)
      out.push(
        diag(
          "MQ9203",
          "error",
          `The bound enum's members (${drift.members.join(", ")}) differ from the lifecycle's states (${drift.states.join(", ")}); sync the enum from the process.`,
          entry,
          "/boundAttribute",
        ),
      );
  }
  // MQ9001 initial, MQ9201 (process side) and MQ9205, as ProcessRules.cs reports them
  if (kind === "process") out.push(...lifecycleDiagnostics(json, entry, ctx.lookup));
  // MQ9501 guard and action expressions: one JavaScript expression each (parsed, never run)
  if (kind === "process")
    for (const field of ["guards", "actions"] as const)
      arr(json[field]).forEach((g, i) => {
        const source = typeof g.expression === "string" ? g.expression.trim() : "";
        if (!source) return;
        try {
          new Function("context", "event", `"use strict"; return (\n${source}\n);`);
        } catch (e) {
          const what = field === "guards" ? "guard" : "action";
          out.push(
            diag(
              "MQ9501",
              "error",
              `The ${what} '${String(g.name)}' does not parse: ${e instanceof Error ? e.message : String(e)}.`,
              entry,
              `/${field}/${i}/expression`,
            ),
          );
        }
      });
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
  const byId = new Map([...entries].map((e) => [e.id, e]));
  const ctx: ValidationContext = {
    ...globalsOf(entries),
    hasId: (id) => owner.has(id),
    extensions: input.extensions,
    lookup: (id) => byId.get(id),
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

/** MQ9001 (an initial that is not a direct child, or an initial on a state that is not compound), MQ9201 (a lifecycle
 * without a subject, or whose subject's lifecycle does not name it) and MQ9205 (the bound attribute's default is not
 * the initial root state's name), as the engine's ProcessRules.cs reports them. */
function lifecycleDiagnostics(json: Json, entry: ModelEntry, lookup: ValidationContext["lookup"]): Diagnostic[] {
  const out: Diagnostic[] = [];
  const name = String(json.name);
  const checkInitial = (owner: Json, pointer: string, label: string) => {
    const children = arr(owner.states);
    if (typeof owner.initial === "string" && !children.some((c) => c.id === owner.initial))
      out.push(
        diag(
          "MQ9001",
          "error",
          `${label} has initial '${owner.initial}', which is not one of its direct children. Set initial to a direct child, or remove it to use the first child.`,
          entry,
          `${pointer}/initial`,
        ),
      );
  };
  checkInitial(json, "", `Process '${name}'`);
  const walk = (states: Json[], pointer: string) =>
    states.forEach((st, i) => {
      const at = `${pointer}/states/${i}`;
      const compound = st.type === "compound" || (st.type === undefined && arr(st.states).length > 0);
      if (compound) checkInitial(st, at, `State '${String(st.name)}' of process '${name}'`);
      else if (typeof st.initial === "string" && st.type !== "parallel")
        out.push(diag("MQ9001", "error", `State '${String(st.name)}' is not compound but sets initial; remove initial.`, entry, `${at}/initial`));
      walk(arr(st.states), at);
    });
  walk(arr(json.states), "");
  if (json.use !== "lifecycle") return out;
  if (typeof json.subject !== "string") {
    out.push(diag("MQ9201", "error", `Process '${name}' is a lifecycle without a subject; set subject to the entity it describes.`, entry, "/use"));
    return out;
  }
  const subject = lookup?.(json.subject)?.json;
  if (!subject || subject.kind !== "entity") return out;
  if (subject.lifecycle !== entry.id)
    out.push(
      diag(
        "MQ9201",
        "error",
        `Process '${name}' is the lifecycle of entity '${String(subject.name)}', but the entity's lifecycle is not this process; set the entity's lifecycle to this process.`,
        entry,
        "/subject",
      ),
    );
  const attribute = arr(subject.attributes).find((a) => a.id === json.boundAttribute);
  const roots = arr(json.states);
  const initial = roots.find((st) => st.id === json.initial) ?? roots[0];
  if (attribute && initial && initial.type !== "history" && initial.type !== "choice" && attribute.default !== initial.name) {
    const current = attribute.default === undefined ? "is not set" : `is ${JSON.stringify(attribute.default)}`;
    out.push(
      diag(
        "MQ9205",
        "warning",
        `The default of bound attribute '${String(attribute.name)}' ${current}, but process '${name}' starts in '${String(initial.name)}'; set the default to '${String(initial.name)}'.`,
        entry,
        "/boundAttribute",
      ),
    );
  }
  return out;
}
