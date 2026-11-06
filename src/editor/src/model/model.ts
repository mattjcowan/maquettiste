// Helpers over the model documents the editor edits (canonical JSON, schemas/v1/<kind>.json).
import type {
  AttributeDoc,
  BuiltinType,
  ElementDocument,
  ElementKind,
  ElementSummary,
  EntityDoc,
  ModelJson,
  RelationDoc,
  RelationEndDoc,
  TypeRef,
} from "@/api/types";
import { ACTOR_TYPES, KIND_LABELS, type ActorType } from "./labels";
import { subjectsOf } from "./process";
import { relativeForms, type RelativeDoc } from "./objectFields";

export const BUILTIN_TYPES: BuiltinType[] = [
  "string",
  "text",
  "bool",
  "int16",
  "int32",
  "int64",
  "decimal",
  "float",
  "double",
  "date",
  "time",
  "datetime",
  "datetimeoffset",
  "duration",
  "uuid",
  "ulid",
  "binary",
  "json",
];

/** Kinds whose ids an attribute type may reference. */
export const TYPE_KINDS: ElementKind[] = ["value-object", "enum", "scalar-type", "reference-type"];

/** The kinds' user-facing names live in model/labels.ts (explorer-redesign.md section 2). */
export { KIND_LABELS } from "./labels";

/** Explorer order of kinds inside a package. */
export const KIND_ORDER: ElementKind[] = [
  "diagram",
  "entity",
  "value-object",
  "enum",
  "scalar-type",
  "relation",
  "database",
  "table",
  "view",
  "sequence",
  "routine",
  "database-type",
  "sql-object",
  "query",
  "mapping",
  "package",
  "tag-vocabulary",
  "category-tree",
  "stereotype",
  "reference-type",
  "seed",
];

export function isBuiltin(type: TypeRef | undefined): type is BuiltinType {
  return typeof type === "string";
}

/** "string(32)", "decimal(18,2)", "Money" for a reference, "?" when unknown. */
export function typeLabel(attribute: AttributeDoc, nameOf: (id: string) => string | undefined): string {
  const type = attribute.type;
  if (isBuiltin(type)) {
    if (attribute.length !== undefined) return `${type}(${attribute.length})`;
    if (attribute.precision !== undefined)
      return attribute.scale !== undefined ? `${type}(${attribute.precision},${attribute.scale})` : `${type}(${attribute.precision})`;
    return attribute.collection ? `${type}[]` : type;
  }
  const name = (type && nameOf(type.ref)) ?? "?";
  return attribute.collection ? `${name}[]` : name;
}

export function attributesOf(json: ModelJson | undefined): AttributeDoc[] {
  const list = (json as { attributes?: AttributeDoc[] } | undefined)?.attributes;
  return Array.isArray(list) ? list : [];
}

export function keyAttributeIds(entity: EntityDoc | undefined): Set<string> {
  return new Set(entity?.key?.attributes ?? []);
}

/** UML multiplicity of a relation end: "1", "0..1", "*", "1..*". */
export function multiplicity(end: RelationEndDoc): string {
  const min = end.min ?? 0;
  const max = end.max ?? "*";
  if (max === 1) return min === 1 ? "1" : "0..1";
  return min === 1 ? "1..*" : "*";
}

/** The crow's-foot marker for an end: one of four standard shapes. */
export function crowsFoot(end: RelationEndDoc): "one" | "zero-or-one" | "many" | "one-or-many" {
  const min = end.min ?? 0;
  const max = end.max ?? "*";
  if (max === 1) return min === 1 ? "one" : "zero-or-one";
  return min === 1 ? "one-or-many" : "many";
}

export function relationKind(relation: RelationDoc): NonNullable<RelationDoc["relationKind"]> {
  return relation.relationKind ?? "association";
}

/** The kinds whose index rows carry their database (E5). */
export const DATABASE_MEMBER_KINDS: ReadonlySet<string> = new Set(["table", "view", "sequence", "routine", "database-type", "sql-object", "query", "mapping"]);

/** An index row built from a document the SPA just saved (phase2-design.md 4.3). */
export function summaryFromDocument(doc: ElementDocument, actorIds?: ReadonlySet<string>): ElementSummary {
  const json = doc.json as ModelJson & {
    package?: string;
    parent?: string;
    tags?: string[];
    category?: string;
    stereotypes?: string[];
    name?: string;
  };
  return {
    id: json.id as string,
    kind: json.kind as ElementKind,
    name: typeof json.name === "string" ? json.name : "",
    // A package's row holds its parent, as the engine's index does.
    package: (json.kind === "package" ? json.parent : json.package) ?? null,
    tags: json.tags ?? [],
    category: json.category ?? null,
    stereotypes: json.stereotypes ?? [],
    hash: doc.hash,
    path: doc.path,
    ...indexMembers(json, actorIds),
  };
}

/** The ids in `known` that a document holds as a string anywhere (the engine counts every reference to an actor), sorted ordinally. */
export function actorIdsIn(value: unknown, known: ReadonlySet<string> | undefined): string[] {
  if (!known || known.size === 0) return [];
  const found = new Set<string>();
  const walk = (v: unknown): void => {
    if (typeof v === "string") {
      if (known.has(v)) found.add(v);
    } else if (Array.isArray(v)) v.forEach(walk);
    else if (v && typeof v === "object") Object.values(v).forEach(walk);
  };
  walk(value);
  return [...found].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/**
 * The E5 members of an index row (explorer-redesign.md 4.1), as the engine's index writes them, so a saved row keeps
 * its display name, its base entity, a relation's ends, a mapping's or table's entity and database, and the seed and
 * reference type counts (RT 2.1) until the next index read.
 */
function indexMembers(json: Record<string, unknown>, actorIds?: ReadonlySet<string>): Partial<ElementSummary> {
  const out: Partial<ElementSummary> = {};
  const kind = json.kind;
  if (typeof json.displayName === "string") out.displayName = json.displayName;
  if (DATABASE_MEMBER_KINDS.has(String(kind)) && typeof json.database === "string") out.database = json.database;
  if ((kind === "table" || kind === "mapping") && typeof json.entity === "string") out.entity = json.entity;
  if (kind === "entity" && typeof json.base === "string") out.base = json.base;
  if (kind === "seed") {
    if (typeof json.target === "string") out.target = json.target;
    out.rowCount = Array.isArray(json.rows) ? json.rows.length : 0;
  }
  // Phase 3 (phase-3-design.md 2.1): a process's use, subject and state count; an actor's type; a scenario's process and steps.
  if (kind === "process") {
    out.use = json.use === "lifecycle" || json.use === "library" ? json.use : "orchestration";
    // One subject, in either form, is `subject`; two or more are `subjects` (lifecycle-design.md decision 23).
    const subjects = subjectsOf(json as Parameters<typeof subjectsOf>[0]);
    if (subjects.length === 1) out.subject = subjects[0].entity;
    else if (subjects.length > 1) out.subjects = subjects.map((s) => s.entity);
    const count = (states: unknown): number =>
      Array.isArray(states) ? (states as Record<string, unknown>[]).reduce((n, st) => n + 1 + count(st.states), 0) : 0;
    out.stateCount = count(json.states);
    // The actors the process names anywhere, as the engine's index lists them: sorted ordinally, left out when there are none.
    const actors = actorIdsIn(json, actorIds);
    if (actors.length) out.actors = actors;
  }
  if (kind === "actor" && typeof json.type === "string" && (ACTOR_TYPES as readonly string[]).includes(json.type)) {
    out.actorType = json.type as ActorType;
    // The index lists memberOf only when non-empty, in file order.
    const memberOf = Array.isArray(json.memberOf) ? json.memberOf.filter((m): m is string => typeof m === "string") : [];
    if (memberOf.length > 0) out.memberOf = memberOf;
    // A relative actor's form, in the order owner, path, members-of; none when the file gives no form (MQ9603).
    if (json.relative !== undefined) out.relative = relativeForms(json.relative as RelativeDoc)[0] ?? "none";
  }
  if (kind === "scenario") {
    if (typeof json.process === "string") out.process = json.process;
    out.stepCount = Array.isArray(json.steps) ? json.steps.length : 0;
  }
  // Section 4.1 of next-phase-2-design.md: an operation's subject and effect.
  if (kind === "operation") {
    if (typeof json.subject === "string") out.subject = json.subject;
    out.effect = json.effect === "change" ? "change" : "read";
  }
  if (kind === "reference-type") out.fieldCount = Array.isArray(json.attributes) ? json.attributes.length : 0;
  if (kind === "diagram") out.memberCount = Array.isArray(json.members) ? json.members.length : 0;
  // A process diagram names its process (phase-3-design.md 2.7), so the explorer and the canvas find it by its row.
  if (kind === "diagram" && typeof json.process === "string") out.process = json.process;
  if (kind === "relation" && Array.isArray(json.ends))
    out.ends = (json.ends as Record<string, unknown>[]).map((end) => ({ entity: String(end.entity ?? ""), role: String(end.role ?? "") }));
  return out;
}

/** Name for display: the summary name, or a placeholder for unnamed table overlays. */
export function displayName(summary: Pick<ElementSummary, "name" | "kind" | "id"> | undefined): string {
  if (!summary) return "(unknown)";
  if (summary.name) return summary.name;
  return `${KIND_LABELS[summary.kind]} ${summary.id.slice(-6)}`;
}

/** Valid identifier (schemas/v1/common.json identifier). */
export const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** What IDENTIFIER allows, in words (no case is enforced), with examples: the hint under every name that must be one. */
export function identifierHint(...examples: string[]): string {
  return `Letters, digits and underscores, not starting with a digit${examples.length ? `, such as ${examples.join(" or ")}` : ""}.`;
}

/** camelCase for a generated attribute or role name. */
export function camel(name: string): string {
  const cleaned = name.replace(/[^A-Za-z0-9]+(.)?/g, (_, c: string | undefined) => (c ? c.toUpperCase() : ""));
  return cleaned.charAt(0).toLowerCase() + cleaned.slice(1);
}

/**
 * The default roles of a new relationship's two ends (SPEC 6: each end's role names its own entity, and its navigation,
 * the property on the opposite entity, repeats the role): the source end is the entity's name, the target end its plural.
 * The canvas dialog and the explorer's New relationship both start from these.
 */
export function defaultEndRoles(sourceName: string, targetName: string): { sourceRole: string; targetRole: string } {
  return { sourceRole: camel(sourceName), targetRole: camel(targetName) + "s" };
}

export function pascal(name: string): string {
  const c = camel(name);
  return c.charAt(0).toUpperCase() + c.slice(1);
}
