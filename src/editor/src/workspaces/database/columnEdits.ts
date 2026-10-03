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

/**
 * The DDL facets of a column entry (schemas/v1 table.json): `unicode` (string and text: true Unicode, false single-byte, absent
 * the dialect's default), `fixedLength` (string and binary) and `defaultName` (the default constraint's name, where the dialect
 * names defaults). Read from the entry, as the resolved column does not carry them.
 */
export type ColumnFacetField = "unicode" | "fixedLength" | "defaultName";

/**
 * How a column gets its values beyond a default (schemas/v1 table.json): `generated` (identity, or sequence with `sequence` the
 * sequence's id), `computed` (an SQL expression) with `computedStored`, `collation`, and `defaultSql` (a default SQL expression
 * per dialect, `*` for any; a value is `{ dialect, text }`, an empty text removes that dialect's), and an identity's `identity`
 * object: `identitySeed` (its first value), `identityIncrement` (its step, not 0) and `identityAlways` (GENERATED ALWAYS instead of
 * BY DEFAULT); an identity left with none of them loses the object, and a column that stops being an identity loses it too.
 */
export type ColumnGenerationField =
  "generated" | "sequence" | "computed" | "computedStored" | "collation" | "defaultSql" | "identitySeed" | "identityIncrement" | "identityAlways";

/** The identity fields and the member of `identity` each sets. */
export const IDENTITY_MEMBERS = { identitySeed: "seed", identityIncrement: "increment", identityAlways: "always" } as const;
export type ColumnIdentityField = keyof typeof IDENTITY_MEMBERS;
const isIdentityField = (field: string): field is ColumnIdentityField => field in IDENTITY_MEMBERS;

/** Every column field an edit may set. */
export type ColumnEditField = ColumnField | ColumnMarkField | ColumnFacetField | ColumnGenerationField;

/** The integer types an identity column takes (and decimal with scale 0 where a dialect allows it; MQ rules say the rest). */
export const IDENTITY_TYPES: ReadonlySet<string> = new Set(["int16", "int32", "int64", "decimal"]);

/** What a column entry says about how it gets values, for the inspector: the file's entry, else the resolved column. */
export interface ColumnGeneration {
  generated: "" | "identity" | "sequence";
  sequence: string;
  computed: string;
  computedStored: boolean;
  collation: string;
  defaultSql: Record<string, string>;
  /** The identity's seed and increment as typed text ("" when the file says none), and whether it is generated always. */
  identitySeed: string;
  identityIncrement: string;
  identityAlways: boolean;
}

/** The generation fields of a column: its file entry's when there is one, else what the resolved column says (for `dialect`). */
export function columnGeneration(
  entry: Json | null | undefined,
  column: Pick<ColumnView, "identity" | "sequenceId" | "computed" | "computedStored" | "collation" | "defaultSql">,
  dialect: string,
): ColumnGeneration {
  if (entry)
    return {
      generated: entry.generated === "identity" || entry.generated === "sequence" ? entry.generated : "",
      sequence: typeof entry.sequence === "string" ? entry.sequence : "",
      computed: typeof entry.computed === "string" ? entry.computed : "",
      computedStored: entry.computedStored === true,
      collation: typeof entry.collation === "string" ? entry.collation : "",
      defaultSql:
        entry.defaultSql && typeof entry.defaultSql === "object"
          ? Object.fromEntries(Object.entries(entry.defaultSql as Json).filter((e): e is [string, string] => typeof e[1] === "string"))
          : {},
      ...identityOf(entry),
    };
  return {
    generated: column.identity ? "identity" : column.sequenceId ? "sequence" : "",
    sequence: column.sequenceId ?? "",
    computed: column.computed ?? "",
    computedStored: column.computedStored,
    collation: column.collation ?? "",
    defaultSql: column.defaultSql ? { [dialect]: column.defaultSql } : {},
    ...identityOf(undefined),
  };
}

/** A column entry's identity options (schemas/v1 table.json `identity`). */
function identityOf(entry: Json | undefined): Pick<ColumnGeneration, "identitySeed" | "identityIncrement" | "identityAlways"> {
  const identity = entry?.identity && typeof entry.identity === "object" ? (entry.identity as Json) : {};
  const text = (v: unknown) => (typeof v === "number" ? String(v) : "");
  return { identitySeed: text(identity.seed), identityIncrement: text(identity.increment), identityAlways: identity.always === true };
}

/**
 * What an identity option does on a dialect (the sql-ddl pack's DDL and MQ4056), as a sentence under the fields; null where the
 * dialect writes all three.
 */
export function identityNote(dialect: string): string | null {
  if (dialect === "sqlserver") return "SQL Server writes IDENTITY(seed, increment) and always generates the value.";
  if (dialect === "mysql") return "MySQL writes the seed as the table's AUTO_INCREMENT; it has no increment and no generated always (MQ4056).";
  if (dialect === "sqlite") return "SQLite numbers rows itself: seed, increment and generated always are left out (MQ4056).";
  return null;
}

/** What keeps a column's generation from making sense (said under the fields): each a sentence. */
export function columnGenerationProblems(g: ColumnGeneration, column: Pick<ColumnView, "type" | "default">): string[] {
  const out: string[] = [];
  const hasDefault = (column.default !== null && column.default !== undefined) || Object.keys(g.defaultSql).length > 0;
  if (g.generated === "sequence" && !g.sequence) out.push("Pick the sequence that supplies the values.");
  if (g.generated === "identity" && !IDENTITY_TYPES.has(column.type)) out.push(`An identity column takes an integer type; this one is ${column.type}.`);
  if (g.computed && g.generated) out.push("A computed column is neither an identity nor sequence-generated: choose one.");
  if (g.computed && hasDefault) out.push("A computed column has no default: its expression gives its value.");
  if (g.generated && hasDefault) out.push(`A${g.generated === "identity" ? "n identity" : " sequence-generated"} column takes no other default.`);
  return out;
}

/** The types that have the Unicode facet, and the ones that have a fixed length (MQ4057 otherwise). */
export const UNICODE_TYPES: ReadonlySet<string> = new Set(["string", "text"]);
export const FIXED_LENGTH_TYPES: ReadonlySet<string> = new Set(["string", "binary"]);

/** The Unicode choice of a column entry as a select value: "" (the dialect's default), "true" or "false". */
export const unicodeChoice = (entry: Json | undefined): "" | "true" | "false" =>
  typeof entry?.unicode === "boolean" ? (entry.unicode ? "true" : "false") : "";

/** A field's new value: text or a flag for the physical fields, a list for tags and stereotypes, an edit of the property bag, a
 * dialect's text for `defaultSql`. */
export type ColumnValue = string | boolean | readonly string[] | PropertyEdit | { dialect: string; text: string };

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

/** Why a typed value cannot be saved in a field, or null: a facet is a whole number, an identity's seed an integer and its
 * increment an integer other than 0 (empty clears each). */
export function columnFieldProblem(field: ColumnEditField, raw: string): string | null {
  if (field === "identitySeed" || field === "identityIncrement") {
    const text = raw.trim();
    if (text === "") return null;
    const what = field === "identitySeed" ? "The identity seed" : "The identity increment";
    if (!/^-?\d+$/.test(text) || !Number.isSafeInteger(Number(text))) return `${what} is a whole number, or empty.`;
    if (field === "identityIncrement" && Number(text) === 0) return "The identity increment cannot be 0.";
    return null;
  }
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
  if (field === "fixedLength" || field === "computedStored") return value === true ? true : undefined;
  if (field === "defaultSql") {
    const { dialect, text } = value as unknown as { dialect: string; text: string };
    const map = { ...((entry.defaultSql as Record<string, string> | undefined) ?? {}) };
    if (text.trim()) map[dialect] = text.trim();
    else delete map[dialect];
    return Object.keys(map).length ? map : undefined;
  }
  if (field === "generated") return value === "identity" || value === "sequence" ? value : undefined;
  if (isIdentityField(field)) {
    const identity = { ...((entry.identity as Json | undefined) ?? {}) };
    const member = IDENTITY_MEMBERS[field];
    const text = typeof value === "string" ? value.trim() : "";
    const next = field === "identityAlways" ? (value === true ? true : undefined) : text === "" ? undefined : Number(text);
    if (next === undefined) delete identity[member];
    else identity[member] = next;
    // The schema's order: seed, increment, always.
    const ordered = Object.fromEntries((["seed", "increment", "always"] as const).filter((k) => k in identity).map((k) => [k, identity[k]]));
    return Object.keys(ordered).length ? ordered : undefined;
  }
  if (field === "unicode") return value === "true" || value === true ? true : value === "false" ? false : undefined;
  const text = String(value);
  if (field === "defaultName" || field === "sequence" || field === "collation" || field === "computed") return text.trim() === "" ? undefined : text.trim();
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
  // An identity option sets the column's `identity` object (and makes the column an identity).
  const member = isIdentityField(field) ? "identity" : field;
  if (next === undefined) {
    if (!overlay && (field === "name" || field === "type")) return false;
    delete entry[member];
  } else entry[member] = next;
  if (isIdentityField(field) && next !== undefined) {
    entry.generated = "identity";
    delete entry.sequence;
  }
  if (field === "default" && next !== undefined) delete entry.defaultSql;
  if (field === "defaultSql" && next !== undefined) delete entry.default;
  // How the column gets its values: a sequence is named with "sequence"; another way leaves no sequence; no computed
  // expression, nothing stored.
  if (field === "sequence") {
    if (next !== undefined) {
      entry.generated = "sequence";
      delete entry.identity;
    } else if (entry.generated === "sequence") delete entry.generated;
  }
  if (field === "generated" && next !== "sequence") delete entry.sequence;
  if (field === "generated" && next !== "identity") delete entry.identity;
  if (field === "computed" && next === undefined) delete entry.computedStored;
  // A type without the facet drops it (else MQ4057).
  if (field === "type") {
    const type = typeof next === "string" ? next : column.type;
    if (!UNICODE_TYPES.has(type)) delete entry.unicode;
    if (!FIXED_LENGTH_TYPES.has(type)) delete entry.fixedLength;
  }
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
