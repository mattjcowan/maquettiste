// The Database screen's column editor, pure part: which table file holds a resolved table's columns, and the edit of one
// column's field in that file. A designed or imported table is its own file (its key is the file id); a synthesized table's
// file is an overlay (engine-design.md 7.3), found by what it overrides (the entity, a child table's entity and attribute, or
// a junction's relation), whose column entries name the synthesized column key in `attribute` and hold only what they change.
// A synthesized table without an overlay gets one on its first edit, holding just that column's entry.
import type { ColumnView, ElementSummary, TableView } from "@/api/types";

type Json = Record<string, unknown>;

export type ColumnField = "name" | "type" | "nullable" | "default" | "comment" | "description";

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
function fileValue(column: ColumnView, field: ColumnField, value: string | boolean): unknown {
  if (field === "nullable") return value === true;
  const text = String(value);
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
export function setColumnField(doc: Json, column: ColumnView, field: ColumnField, value: string | boolean, newId: () => string): boolean {
  const overlay = doc.origin === "synthesized";
  const list = entries(doc);
  let entry = list.find((c) => (overlay ? c.attribute === column.key || (c.attribute === undefined && c.id === column.key) : c.id === column.key));
  if (!entry) {
    if (!overlay) return false;
    entry = { id: newId(), attribute: column.key };
    list.push(entry);
  }
  if (field === "description" && entry.description && typeof entry.description === "object") return false;
  const next = fileValue(column, field, value);
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

/** A new overlay for a synthesized table holding one column's edit, or null when the edit changes nothing. */
export function newOverlay(
  target: Extract<TableFileTarget, { kind: "overlay" }>,
  databaseId: string,
  column: ColumnView,
  field: ColumnField,
  value: string | boolean,
  newId: () => string,
): Json | null {
  const doc: Json = {
    kind: "table",
    id: newId(),
    database: databaseId,
    origin: "synthesized",
    ...("relation" in target ? { relation: target.relation } : { entity: target.entity, ...(target.attribute ? { attribute: target.attribute } : {}) }),
  };
  setColumnField(doc, column, field, value, newId);
  return doc.columns ? doc : null;
}
