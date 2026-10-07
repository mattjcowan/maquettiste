// The mock's validator: a deterministic subset of the engine's built-in rules (engine-design.md 6),
// enough for the editor's problems panel, inline save diagnostics and the invalid-save path. The
// rule ids, severities and messages follow RuleCatalog; line and column are left null.
import type { Diagnostic } from "@/api/types";
import Ajv2020 from "ajv/dist/2020";
import { schemaValidator } from "../contract";
import { referencesOf, subElementIds } from "./refs";
import { keysOfDocument, referencedKeyMessage, referencedKeyProblem } from "@/model/foreignKeyTarget";

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

/** MQ3001's scope of an element (kind group, package and lower-cased name), or null when the rule does not apply.
 * Diagrams take no part, as in the engine: a process's diagram carries the process's name beside other diagrams. */
export function nameScope(json: Json): string | null {
  const kind = String(json.kind);
  if (typeof json.name !== "string" || json.name === "" || !["entity", "value-object", "enum", "scalar-type", "relation", "package"].includes(kind))
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
  // MQ4057 a column facet its type does not have; MQ4056 a DDL feature the database's dialect lacks (PhysicalRules.cs).
  if (kind === "table") out.push(...tableFacetDiagnostics(json, entry, ctx.lookup));
  if (kind === "view") out.push(...viewDialectDiagnostics(json, entry, ctx.lookup));
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

const DIALECT_NAMES: Record<string, string> = { postgresql: "PostgreSQL", sqlserver: "SQL Server", mysql: "MySQL", sqlite: "SQLite", oracle: "Oracle" };

/** Whether MySQL indexes a column only with a key prefix length: text and blob types (PhysicalRules.NeedsKeyLength). */
function needsKeyLength(column: Json): boolean {
  if (typeof column.nativeType === "string") {
    const lower = column.nativeType.toLowerCase();
    return lower.includes("text") || lower.includes("blob");
  }
  return column.type === "text" || (column.type === "binary" && column.fixedLength !== true);
}

/** MQ4056 on a view file, as PhysicalRules.CheckView reports it: materialized and WITH CHECK OPTION where the dialect lacks them. */
function viewDialectDiagnostics(json: Json, entry: ModelEntry, lookup: ValidationContext["lookup"]): Diagnostic[] {
  const database = typeof json.database === "string" ? lookup?.(json.database)?.json : undefined;
  if (!database) return [];
  const d = typeof database.dialect === "string" ? database.dialect : "postgresql";
  const out: Diagnostic[] = [];
  const add = (what: string, pointer: string) =>
    out.push(
      diag(
        "MQ4056",
        "warning",
        `View '${String(json.name)}' ${what}, which ${DIALECT_NAMES[d] ?? d} (database '${String(database.name)}') does not have; the DDL leaves it out.`,
        entry,
        pointer,
      ),
    );
  const materializes = d === "postgresql" || d === "oracle";
  if (json.materialized === true && !materializes) add("is materialized", "/materialized");
  if (json.withCheckOption === true && d === "sqlite") add("has WITH CHECK OPTION", "/withCheckOption");
  else if (json.withCheckOption === true && json.materialized === true && materializes)
    add("is materialized with WITH CHECK OPTION (a materialized view is not written through)", "/withCheckOption");
  return out;
}

/** MQ4057, MQ4059, MQ4060, MQ4061 and MQ4056 on a table file, as PhysicalRules.CheckColumnValues, CheckReferencedKey and CheckDialectFeatures report them. */
function tableFacetDiagnostics(json: Json, entry: ModelEntry, lookup: ValidationContext["lookup"]): Diagnostic[] {
  const out: Diagnostic[] = [];
  arr(json.columns).forEach((c, i) => {
    const type = typeof c.type === "string" ? c.type : null;
    if (!type) return;
    if (typeof c.unicode === "boolean" && type !== "string" && type !== "text")
      out.push(diag("MQ4057", "error", `Column '${String(c.name)}' sets unicode, which only string and text columns have.`, entry, `/columns/${i}/unicode`));
    if (c.fixedLength === true && type !== "string" && type !== "binary")
      out.push(
        diag("MQ4057", "error", `Column '${String(c.name)}' sets fixedLength, which only string and binary columns have.`, entry, `/columns/${i}/fixedLength`),
      );
  });
  // MQ4060: onDeleteColumns is for set-null and set-default, and names the key's own columns, each once (PhysicalRules.CheckOnDeleteColumns).
  arr(json.foreignKeys).forEach((fk, i) => {
    const listed = Array.isArray(fk.onDeleteColumns) ? (fk.onDeleteColumns as unknown[]).map(String) : [];
    if (!listed.length) return;
    const name = String(fk.name ?? fk.id);
    const pointer = `/foreignKeys/${i}/onDeleteColumns`;
    const action = typeof fk.onDelete === "string" ? fk.onDelete : "no-action";
    if (action !== "set-null" && action !== "set-default") {
      out.push(
        diag(
          "MQ4060",
          "error",
          `Foreign key '${name}' lists onDeleteColumns, but its onDelete is ${action}: only set-null and set-default set columns.`,
          entry,
          pointer,
        ),
      );
      return;
    }
    const columns = Array.isArray(fk.columns) ? (fk.columns as unknown[]).map(String) : [];
    listed.forEach((c, j) => {
      if (!columns.includes(c))
        out.push(
          diag("MQ4060", "error", `Foreign key '${name}' lists '${c}' in onDeleteColumns, which is not one of the key's columns.`, entry, `${pointer}/${j}`),
        );
      else if (listed.indexOf(c) < j)
        out.push(diag("MQ4060", "error", `Foreign key '${name}' lists '${c}' twice in onDeleteColumns.`, entry, `${pointer}/${j}`));
    });
  });
  // MQ4061: what a key's set-null or set-default sets can take the value (the engine's resolver, DatabaseRun.CheckOnDeleteSets; here
  // a designed table's own columns, nullable unless they say otherwise).
  if (json.origin !== "synthesized")
    arr(json.foreignKeys).forEach((fk, i) => {
      const action = fk.onDelete;
      if (action !== "set-null" && action !== "set-default") return;
      const keyColumns = Array.isArray(fk.columns) ? (fk.columns as unknown[]).map(String) : [];
      const listed = Array.isArray(fk.onDeleteColumns) && fk.onDeleteColumns.length ? (fk.onDeleteColumns as unknown[]).map(String) : keyColumns;
      const refused = listed
        .map((id) => arr(json.columns).find((c) => c.id === id))
        .filter((c): c is Json => !!c && c.nullable === false && (action === "set-null" || (c.default === undefined && c.defaultSql === undefined)));
      if (!refused.length) return;
      const target = typeof fk.referencesTable === "string" ? lookup?.(fk.referencesTable)?.json : undefined;
      const fix =
        listed !== keyColumns || keyColumns.length < 2
          ? "make the column nullable or pick another action"
          : "list the columns to set in onDeleteColumns (PostgreSQL), make the column nullable, or pick another action";
      out.push(
        diag(
          "MQ4061",
          "warning",
          `Foreign key '${String(fk.name ?? fk.id)}' of table '${String(json.name)}' ${action === "set-default" ? "sets its columns to their defaults" : "sets its columns to NULL"} on delete, but ${refused.map((c) => String(c.name)).join(", ")} ${refused.length === 1 ? "is" : "are"} ${action === "set-default" ? "not nullable and without a default" : "not nullable"}: deleting a referenced row in '${String(target?.name ?? fk.referencesTable)}' fails; ${fix}.`,
          entry,
          `/foreignKeys/${i}/onDelete`,
        ),
      );
    });
  const database = typeof json.database === "string" ? lookup?.(json.database)?.json : undefined;
  if (!database) return out;
  const d = typeof database.dialect === "string" ? database.dialect : "postgresql";
  // MQ4059: a foreign key's referenced columns are a key of the referenced table file (PhysicalRules.CheckReferencedKey).
  arr(json.foreignKeys).forEach((fk, i) => {
    const refs = Array.isArray(fk.referencesColumns) ? (fk.referencesColumns as unknown[]).map(String) : [];
    const target = typeof fk.referencesTable === "string" ? lookup?.(fk.referencesTable)?.json : undefined;
    if (!refs.length || !target || target.kind !== "table" || target.origin === "synthesized") return;
    const columns = arr(target.columns);
    if (!refs.every((r) => columns.some((c) => c.id === r))) return;
    const problem = referencedKeyProblem(keysOfDocument(target), refs, d);
    if (!problem) return;
    const nameOf = (r: string) => String(columns.find((c) => c.id === r)?.name ?? r);
    out.push(
      diag(
        "MQ4059",
        "error",
        referencedKeyMessage(problem, String(fk.name ?? fk.id), String(target.name), refs, nameOf),
        entry,
        `/foreignKeys/${i}/referencesColumns`,
      ),
    );
  });
  const lacks = (what: string, pointer: string, leaves = "it") =>
    out.push(
      diag(
        "MQ4056",
        "warning",
        `${what}, which ${DIALECT_NAMES[d] ?? d} (database '${String(database.name)}') does not have; the DDL leaves ${leaves} out.`,
        entry,
        pointer,
      ),
    );
  const pk = json.primaryKey as Json | undefined;
  if (pk && typeof pk.clustered === "boolean" && d !== "sqlserver") lacks("The primary key sets clustered", "/primaryKey/clustered");
  arr(json.foreignKeys).forEach((fk, i) => {
    if (typeof fk.deferrable === "string" && fk.deferrable !== "not-deferrable" && (d === "sqlserver" || d === "mysql"))
      lacks(`Foreign key '${String(fk.name ?? fk.id)}' is deferrable`, `/foreignKeys/${i}/deferrable`);
    // Oracle has ON DELETE CASCADE and ON DELETE SET NULL only; MySQL's InnoDB refuses SET DEFAULT.
    const fkName = String(fk.name ?? fk.id);
    const onDelete = typeof fk.onDelete === "string" ? fk.onDelete : "no-action";
    const onUpdate = typeof fk.onUpdate === "string" ? fk.onUpdate : "no-action";
    if (d === "oracle" && onUpdate !== "no-action") lacks(`Foreign key '${fkName}' has ON UPDATE ${onUpdate}`, `/foreignKeys/${i}/onUpdate`);
    if (d === "oracle" && (onDelete === "restrict" || onDelete === "set-default"))
      lacks(`Foreign key '${fkName}' has ON DELETE ${onDelete}`, `/foreignKeys/${i}/onDelete`);
    if (Array.isArray(fk.onDeleteColumns) && fk.onDeleteColumns.length && (onDelete === "set-null" || onDelete === "set-default") && d !== "postgresql")
      out.push(
        diag(
          "MQ4056",
          "warning",
          `Foreign key '${fkName}' sets only some of its columns on delete (onDeleteColumns), which ${DIALECT_NAMES[d] ?? d} (database '${String(database.name)}') does not have; the DDL leaves the column list out, so the action sets every column of the key.`,
          entry,
          `/foreignKeys/${i}/onDeleteColumns`,
        ),
      );
    if (d === "mysql" && onDelete === "set-default") lacks(`Foreign key '${fkName}' has ON DELETE set-default`, `/foreignKeys/${i}/onDelete`);
    if (d === "mysql" && onUpdate === "set-default") lacks(`Foreign key '${fkName}' has ON UPDATE set-default`, `/foreignKeys/${i}/onUpdate`);
  });
  // PostgreSQL alone writes temporal keys, exclusion constraints, partitioning and index operator classes (PhysicalRules).
  if (d !== "postgresql") {
    const pk = json.primaryKey as Json | undefined;
    if (pk?.withoutOverlaps === true) lacks("The primary key is temporal (WITHOUT OVERLAPS)", "/primaryKey/withoutOverlaps");
    arr(json.uniques).forEach((u, i) => {
      if (u.withoutOverlaps === true) lacks(`Unique constraint '${String(u.name ?? u.id)}' is temporal (WITHOUT OVERLAPS)`, `/uniques/${i}/withoutOverlaps`);
    });
    arr(json.foreignKeys).forEach((fk, i) => {
      if (fk.period === true) lacks(`Foreign key '${String(fk.name ?? fk.id)}' has a period (PERIOD)`, `/foreignKeys/${i}/period`);
    });
    arr(json.exclusions).forEach((x, i) => lacks(`Exclusion constraint '${String(x.name ?? x.id)}' (EXCLUDE)`, `/exclusions/${i}`));
    if (json.partitionBy) lacks(`Table '${String(json.name)}' is partitioned (partitionBy)`, "/partitionBy", "the partitioning and the partitions");
    arr(json.indexes).forEach((ix, i) =>
      arr(ix.columns).forEach((c, j) => {
        if (typeof c.operatorClass === "string")
          lacks(`Index '${String(ix.name ?? ix.id)}' sets an operator class`, `/indexes/${i}/columns/${j}/operatorClass`);
      }),
    );
  }
  arr(json.uniques).forEach((u, i) => {
    if (u.nullsNotDistinct === true && d !== "postgresql")
      lacks(`Unique constraint '${String(u.name ?? u.id)}' sets nullsNotDistinct`, `/uniques/${i}/nullsNotDistinct`);
  });
  arr(json.columns).forEach((c, i) => {
    const pointer = `/columns/${i}`;
    if (c.computed !== undefined && c.computedStored === true && d === "oracle")
      lacks(`Column '${String(c.name)}' is a stored computed column (Oracle computes every virtual column on read)`, `${pointer}/computedStored`);
    const identity = c.identity as Json | undefined;
    if (c.generated !== "identity" || !identity || typeof identity !== "object") return;
    if (identity.seed !== undefined && d === "sqlite") lacks(`Column '${String(c.name)}' sets an identity seed`, `${pointer}/identity/seed`);
    if (identity.increment !== undefined && (d === "sqlite" || d === "mysql"))
      lacks(`Column '${String(c.name)}' sets an identity increment`, `${pointer}/identity/increment`);
    if (identity.always === true && (d === "sqlite" || d === "mysql"))
      lacks(`Column '${String(c.name)}' is an identity generated always`, `${pointer}/identity/always`);
  });
  arr(json.indexes).forEach((ix, i) => {
    const name = String(ix.name ?? ix.id);
    arr(ix.columns).forEach((ic, j) => {
      const pointer = `/indexes/${i}/columns/${j}`;
      if (typeof ic.column !== "string" && d === "sqlserver")
        lacks(`Index '${name}' indexes an expression (index a computed column instead)`, `${pointer}/expression`, "the index");
      if (typeof ic.length === "number" && d !== "mysql") lacks(`Index '${name}' sets a key prefix length`, `${pointer}/length`);
      const indexed = typeof ic.column === "string" ? arr(json.columns).find((c) => c.id === ic.column) : undefined;
      if (typeof ic.length !== "number" && d === "mysql" && indexed && needsKeyLength(indexed))
        out.push(
          diag(
            "MQ4056",
            "warning",
            `Index '${name}' indexes the ${String(indexed.type ?? indexed.nativeType)} column '${String(indexed.name)}' without a key prefix length, which ${DIALECT_NAMES[d] ?? d} (database '${String(database.name)}') requires; set the index column's length, or the DDL leaves the index out.`,
            entry,
            `${pointer}/length`,
          ),
        );
    });
    if (arr(ix.include).length && d !== "postgresql" && d !== "sqlserver") lacks(`Index '${name}' has include columns`, `/indexes/${i}/include`);
    if (typeof ix.where === "string" && (d === "mysql" || d === "oracle")) lacks(`Index '${name}' is partial (where)`, `/indexes/${i}/where`);
    const method = typeof ix.method === "string" ? ix.method : "default";
    const ok =
      method === "default" ||
      (method === "clustered"
        ? d === "sqlserver" || d === "postgresql"
        : method === "btree" || method === "hash"
          ? d === "postgresql" || d === "mysql"
          : d === "postgresql");
    if (!ok) lacks(`Index '${name}' uses the method ${method}`, `/indexes/${i}/method`);
  });
  return out;
}
