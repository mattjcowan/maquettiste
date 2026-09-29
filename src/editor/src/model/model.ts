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
import { KIND_LABELS } from "./labels";

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

/** An index row built from a document the SPA just saved (phase2-design.md 4.3). */
export function summaryFromDocument(doc: ElementDocument): ElementSummary {
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
    ...referenceDataMembers(json),
  };
}

/** The index members of seed and reference type rows (RT 2.1), so a saved row keeps its place in the Reference data screen. */
function referenceDataMembers(json: Record<string, unknown>): Partial<ElementSummary> {
  const out: Partial<ElementSummary> = {};
  if (json.kind !== "seed" && json.kind !== "reference-type") return out;
  if (typeof json.displayName === "string") out.displayName = json.displayName;
  if (json.kind === "seed") {
    if (typeof json.target === "string") out.target = json.target;
    out.rowCount = Array.isArray(json.rows) ? json.rows.length : 0;
  } else out.fieldCount = Array.isArray(json.attributes) ? json.attributes.length : 0;
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
