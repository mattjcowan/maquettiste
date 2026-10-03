// The Database screen's column editor, pure part: which table file holds a resolved table's columns, and the edit of one
// column's field in that file. A designed or imported table is its own file (its key is the file id); a synthesized table's
// file is an overlay (engine-design.md 7.3), found by what it overrides (the entity, a child table's entity and attribute, or
// a junction's relation), whose column entries name the synthesized column key in `attribute` and hold only what they change.
// A synthesized table without an overlay gets one on its first edit, holding just that column's entry.
//
// The physical side is free (the owner: "a database table column could say text, and a mapped entity field could say string
// 255 ... nothing should prevent that"): an overlay entry may carry the column's type, length, precision, scale, native type,
// nullability, default, comment, description and name, whatever the attribute it derives from says. The attribute's length is
// validation, the column's is storage; nothing compares them. A column entry also carries its own marks: tags, stereotypes and
// the property bag (free keys), set the same way.
import type { ColumnView, ElementSummary, TableView } from "@/api/types";
import { applyPropertyEdit, type PropertyEdit } from "@/inspector/propertyBag";

type Json = Record<string, unknown>;

export type ColumnField = "name" | "type" | "length" | "precision" | "scale" | "nativeType" | "nullable" | "default" | "comment" | "description";

/** The column entry's marks, set in the table inspector's column section. */
export type ColumnMarkField = "tags" | "stereotypes" | "properties";

/** Every column field an edit may set. */
export type ColumnEditField = ColumnField | ColumnMarkField;

/** A field's new value: text or a flag for the physical fields, a list for tags and stereotypes, an edit of the property bag. */
export type ColumnValue = string | boolean | readonly string[] | PropertyEdit;

/** The column fields every table may set, in the order the grid and the inspector show them. */
export const COLUMN_FIELDS: readonly ColumnField[] = [
  "name",
  "type",
  "length",
  "precision",
  "scale",
  "nativeType",
  "nullable",
  "default",
  "comment",
  "description",
];

/** The whole-number facets, with their least value (common.json: length and precision from 1, scale from 0). */
const FACETS: Partial<Record<ColumnField, number>> = { length: 1, precision: 1, scale: 0 };

/** Why a typed value cannot be saved in a field, or null: a facet is a whole number (empty clears it). */
export function columnFieldProblem(field: ColumnEditField, raw: string): string | null {
  const least = FACETS[field as ColumnField];
  if (least === undefined) return null;
  const text = raw.trim();
  if (text === "") return null;
  if (!/^\d+$/.test(text) || Number(text) < least) return `${field[0].toUpperCase()}${field.slice(1)} is a whole number from ${least}, or empty.`;
  return null;
}

/** Where a resolved table's columns are written. */
export type TableFileTarget = { kind: "file"; id: string } | { kind: "overlay"; entity: string; attribute?: string } | { kind: "overlay"; relation: string };

/** The table file a resolved table reads its columns from (by the key's shape: synthesized keys end with `@<databaseId>`). */
export function tableFileTarget(table: Pick<TableView, "key" | "entityId" | "relationId">, databaseId: string): TableFileTarget {
  const suffix = `@${databaseId}`;
  if (!table.key.endsWith(suffix)) return { kind: "file", id: table.key };
  const owner = table.key.slice(0, -suffix.length);
  const dot = owner.indexOf(".");
  if (dot > 0) return { kind: "overlay", entity: owner.slice(0, dot), attribute: owner.slice(dot + 1) };
  if (table.relationId && owner === table.relationId) return { kind: "overlay", relation: owner };
  return { kind: "overlay", entity: owner };
}

/** The resolved key of the table a table document shapes (the inverse of `tableFileTarget`): an overlay's synthesized key, else the file id. */
export function tableKeyOfDoc(doc: Json | undefined): { database: string; key: string } | null {
  if (!doc || doc.kind !== "table" || typeof doc.database !== "string" || typeof doc.id !== "string") return null;
  const database = doc.database;
  if (doc.origin !== "synthesized") return { database, key: doc.id };
  if (typeof doc.relation === "string") return { database, key: `${doc.relation}@${database}` };
  if (typeof doc.entity !== "string") return null;
  return { database, key: `${doc.entity}${typeof doc.attribute === "string" ? `.${doc.attribute}` : ""}@${database}` };
}

/** Whether a table document is the overlay a target names. */
export function isOverlayFor(doc: Json | undefined, target: TableFileTarget, databaseId: string): boolean {
  if (!doc || target.kind !== "overlay" || doc.kind !== "table" || doc.database !== databaseId || doc.origin !== "synthesized") return false;
  if ("relation" in target) return doc.relation === target.relation;
  return doc.entity === target.entity && (doc.attribute ?? undefined) === target.attribute && !doc.relation;
}

/**
 * The index rows that may be the overlay of a synthesized table: the database's table rows on the same entity, or, for a
 * junction (an overlay row carries no entity), the ones without an entity whose name is empty or the table's name (an overlay
 * that renames the table carries the name it gives). Their documents decide (`isOverlayFor`).
 */
export function overlayCandidates(rows: readonly ElementSummary[], table: Pick<TableView, "name">, target: TableFileTarget, databaseId: string): string[] {
  if (target.kind !== "overlay") return [];
  return rows
    .filter((r) => r.kind === "table" && r.database === databaseId)
    .filter((r) => ("relation" in target ? !r.entity && (r.name === "" || r.name === table.name) : r.entity === target.entity))
    .map((r) => r.id);
}

const NUMERIC = new Set(["int16", "int32", "int64", "decimal", "float", "double"]);

/** A literal default typed like its column: numbers for numeric types, booleans for bool, else the text. */
export function parseColumnDefault(type: string, raw: string): unknown {
  if (NUMERIC.has(type) && /^-?\d+(\.\d+)?$/.test(raw)) return Number(raw);
  if (type === "bool" && (raw === "true" || raw === "false")) return raw === "true";
  return raw;
}

/** A column's field as the grid shows it. */
export function columnText(column: ColumnView, field: Exclude<ColumnField, "nullable">): string {
  switch (field) {
    case "length":
    case "precision":
    case "scale":
      return column[field] === null || column[field] === undefined ? "" : String(column[field]);
    case "nativeType":
      return column.nativeType ?? "";
    case "default":
      return column.default === null || column.default === undefined
        ? ""
        : typeof column.default === "string"
          ? column.default
          : JSON.stringify(column.default);
    case "comment":
      return column.comment ?? "";
    case "description":
      return column.description ?? "";
    default:
      return column[field];
  }
}

/** A table document's column entries. */
const entries = (doc: Json): Json[] => (Array.isArray(doc.columns) ? (doc.columns as Json[]) : []);

/** Whether an overlay entry still changes anything (more than its id and the key it overrides). */
const changesSomething = (entry: Json) => Object.keys(entry).some((k) => k !== "id" && k !== "attribute");

/** The value a field takes in the file: undefined removes it (the synthesized or engine value applies again). */
function fileValue(column: ColumnView, field: ColumnEditField, value: ColumnValue, entry: Json): unknown {
  if (field === "tags" || field === "stereotypes") return Array.isArray(value) && value.length ? [...(value as string[])] : undefined;
  if (field === "properties")
    return typeof value === "object" && !Array.isArray(value) ? applyPropertyEdit(entry.properties, value as PropertyEdit) : entry.properties;
  if (field === "nullable") return value === true;
  const text = String(value);
  if (field in FACETS) return text.trim() === "" ? undefined : Number(text.trim());
  if (field === "nativeType") return text.trim() === "" ? undefined : text.trim();
  if (field === "default") return text === "" ? undefined : parseColumnDefault(column.type, text);
  if (field === "comment") return text === "" ? undefined : text;
  if (field === "description" || field === "name" || field === "type") return text.trim() === "" ? undefined : field === "description" ? text : text.trim();
  return text;
}

/**
 * Sets one field of one column in a table document (mutating it). In an overlay the column's entry is found by the synthesized
 * key it overrides (or, for an extra column, its id) and added when missing; an entry left changing nothing is removed. In a
 * designed table the column is its entry by id, and a required member (name, type) is not cleared. Returns false when the
 * document has no place for the edit (a designed column that is not in the file, a description kept in a sidecar file).
 */
export function setColumnField(doc: Json, column: ColumnView, field: ColumnEditField, value: ColumnValue, newId: () => string): boolean {
  if (typeof value === "string" && columnFieldProblem(field, value)) return false;
  const overlay = doc.origin === "synthesized";
  const list = entries(doc);
  let entry = columnEntryOf(doc, column);
  if (!entry) {
    if (!overlay) return false;
    entry = { id: newId(), attribute: column.key };
    list.push(entry);
  }
  if (field === "description" && entry.description && typeof entry.description === "object") return false;
  const next = fileValue(column, field, value, entry);
  if (next === undefined) {
    if (!overlay && (field === "name" || field === "type")) return false;
    delete entry[field];
  } else entry[field] = next;
  if (field === "default" && next !== undefined) delete entry.defaultSql;
  const kept = overlay ? list.filter((c) => c.attribute === undefined || changesSomething(c)) : list;
  if (kept.length) doc.columns = kept;
  else delete doc.columns;
  return true;
}

/** A column's own entry in a table document (an overlay's by the synthesized key it overrides, else by id), or undefined. */
export function columnEntryOf(doc: Json | undefined, column: Pick<ColumnView, "key">): Json | undefined {
  if (!doc) return undefined;
  const overlay = doc.origin === "synthesized";
  return entries(doc).find((c) => (overlay ? c.attribute === column.key || (c.attribute === undefined && c.id === column.key) : c.id === column.key));
}

/** The members that say what an overlay overrides: an overlay holding nothing else changes nothing. */
const OVERLAY_IDENTITY = new Set(["$schema", "kind", "id", "database", "origin", "entity", "attribute", "relation"]);

/** Whether a table overlay changes anything (a member beyond what names the table it overrides). */
export function overlayChangesSomething(doc: Json): boolean {
  return Object.keys(doc).some((k) => !OVERLAY_IDENTITY.has(k));
}

/** An overlay for a synthesized table that changes nothing yet. */
export function emptyOverlay(target: Extract<TableFileTarget, { kind: "overlay" }>, databaseId: string, id: string): Json {
  return {
    kind: "table",
    id,
    database: databaseId,
    origin: "synthesized",
    ...("relation" in target ? { relation: target.relation } : { entity: target.entity, ...(target.attribute ? { attribute: target.attribute } : {}) }),
  };
}

/** A new overlay for a synthesized table holding one column's edit, or null when the edit changes nothing. */
export function newOverlay(
  target: Extract<TableFileTarget, { kind: "overlay" }>,
  databaseId: string,
  column: ColumnView,
  field: ColumnEditField,
  value: ColumnValue,
  newId: () => string,
): Json | null {
  const doc = emptyOverlay(target, databaseId, newId());
  setColumnField(doc, column, field, value, newId);
  return doc.columns ? doc : null;
}

/** A logical type with its facets, as the hints say it: `string(255)`, `decimal(18,2)`, `int64`. */
export function logicalTypeText(type: string, facets: { length?: number; precision?: number; scale?: number }): string {
  if (facets.length !== undefined) return `${type}(${facets.length})`;
  if (facets.precision !== undefined) return `${type}(${facets.precision}${facets.scale !== undefined ? `,${facets.scale}` : ""})`;
  return type;
}

/** What a column derives from, for its hints and the Attribute column: `Entity.attribute` and the attribute's logical type. */
export interface Derivation {
  entityId: string;
  /** `Invoice.number`. */
  label: string;
  /** `string(255)`, or null when the attribute's document is not at hand. */
  logical: string | null;
}

/** The hint of a physical cell (type, length, precision, scale, native type): the column's storage, the attribute's own rule. */
export function physicalHint(column: Pick<ColumnView, "isForeignKey">, derivation: Derivation | null): string {
  if (column.isForeignKey) return "Follows the referenced column; set it here and a different type is reported (MQ4005).";
  if (derivation?.logical) return `Physical type of the column; the attribute keeps its own (${derivation.logical}) for validation.`;
  return "Physical type of the column.";
}

type AttributeLike = { id?: unknown; name?: unknown; type?: unknown; length?: unknown; precision?: unknown; scale?: unknown; collection?: unknown };
type DocLike = { id?: unknown; key?: unknown; name?: unknown; base?: unknown; stereotypes?: unknown; attributes?: unknown };

const attributesOf = (doc: DocLike | undefined): AttributeLike[] => (Array.isArray(doc?.attributes) ? (doc.attributes as AttributeLike[]) : []);

/**
 * What each column of a table derives from, by column key: the attribute it stores (`attributeId`), as `Entity.attribute`
 * with the attribute's logical type. The attribute is the entity's own, else a base entity's (named, and the way goes to the
 * base), else a stereotype's applied to the entity or a base (named «key»); one found nowhere names the entity only. A
 * foreign key or an extra column derives from no attribute.
 */
export function columnDerivations(
  table: Pick<TableView, "columns" | "entityId">,
  entity: DocLike | undefined,
  typeName: (ref: string) => string | undefined,
  more: { bases?: readonly DocLike[]; stereotypes?: readonly DocLike[] } = {},
): Map<string, Derivation> {
  const out = new Map<string, Derivation>();
  if (!table.entityId || !entity) return out;
  const entityName = String(entity.name ?? "");
  const lineage = [entity, ...(more.bases ?? [])];
  const applied = new Set(lineage.flatMap((d) => (Array.isArray(d.stereotypes) ? (d.stereotypes as unknown[]).map(String) : [])));
  const num = (v: unknown) => (typeof v === "number" ? v : undefined);
  const logicalOf = (attr: AttributeLike) => {
    const type = attr.type;
    const base = typeof type === "string" ? type : (typeName(String((type as { ref?: unknown } | undefined)?.ref ?? "")) ?? "?");
    return logicalTypeText(base, { length: num(attr.length), precision: num(attr.precision), scale: num(attr.scale) }) + (attr.collection ? "[]" : "");
  };
  for (const column of table.columns) {
    if (!column.attributeId) continue;
    let found: Derivation | null = null;
    for (const [i, doc] of lineage.entries()) {
      const attr = attributesOf(doc).find((a) => a.id === column.attributeId);
      if (!attr) continue;
      const from = i === 0 ? "" : ` (from ${String(doc.name ?? "")})`;
      found = {
        entityId: i === 0 ? table.entityId : String(doc.id ?? table.entityId),
        label: `${entityName}.${String(attr.name ?? "")}${from}`,
        logical: logicalOf(attr),
      };
      break;
    }
    if (!found)
      for (const st of more.stereotypes ?? []) {
        const attr = attributesOf(st).find((a) => a.id === column.attributeId);
        if (!attr || !applied.has(String(st.key ?? ""))) continue;
        found = { entityId: table.entityId, label: `${entityName}.${String(attr.name ?? "")} «${String(st.key ?? "")}»`, logical: logicalOf(attr) };
        break;
      }
    out.set(column.key, found ?? { entityId: table.entityId, label: `${entityName} (attribute from a parent or a stereotype)`, logical: null });
  }
  return out;
}
