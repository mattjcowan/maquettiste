// The type column of a hierarchy kept in one table (a base's binding names the column; every derived entity's binding to the
// same database inherits it), free of React: what the Storage tab's type column controls write, and what they show.
import type { Binding, SourceColumn } from "./fieldMap";

type Json = Record<string, unknown>;

/** The role an entity's binding plays in a type column: the base names it, a derived entity inherits it, else none. */
export type TypeColumnRole =
  | { role: "none" }
  /** An entity with derived entities, no base naming a column for it: it can name the column. */
  | { role: "base" }
  /** A derived entity: the column and the name of the base whose binding names it (undefined while no base names one). */
  | { role: "derived"; column?: string; base?: string };

const baseOf = (docs: ReadonlyMap<string, Json>, id: string): string | undefined => {
  const base = docs.get(id)?.base;
  return typeof base === "string" ? base : undefined;
};

/** The nearest base whose binding to the database names a type column: its name and the column. */
export function inheritedTypeColumn(entityId: string, database: string, docs: ReadonlyMap<string, Json>): { column: string; base: string } | undefined {
  const seen = new Set([entityId]);
  for (let id = baseOf(docs, entityId); id && !seen.has(id); id = baseOf(docs, id)) {
    seen.add(id);
    const bound = ((docs.get(id)?.bindings as Binding[] | undefined) ?? []).find((b) => b.database === database);
    if (bound?.discriminatorColumn) return { column: bound.discriminatorColumn, base: String(docs.get(id)?.name ?? id) };
  }
  return undefined;
}

/**
 * Which type column control a binding card shows. `hasDerived`: some entity derives from this one. An entity outside a hierarchy
 * (no base, nothing derived) has none; a derived entity reads the column from its bases; an entity with derived entities and no
 * base naming a column names it itself.
 */
export function typeColumnRole(entityId: string, binding: Binding, docs: ReadonlyMap<string, Json>, hasDerived: boolean): TypeColumnRole {
  const inherited = inheritedTypeColumn(entityId, binding.database, docs);
  if (inherited) return { role: "derived", ...inherited };
  if (baseOf(docs, entityId)) return { role: "derived" };
  return hasDerived ? { role: "base" } : { role: "none" };
}

/** Names the type column (a column of the source, by its key), or (null) drops it; the code stays. */
export function setTypeColumn(b: Binding, column: SourceColumn | null): Binding {
  if (column) b.discriminatorColumn = column.key;
  else delete b.discriminatorColumn;
  return b;
}

/** Sets or clears the entity's type code; a numeric text stays text (the column's type decides). */
export function setTypeCode(b: Binding, text: string): Binding {
  if (text.trim()) b.discriminatorValue = text.trim();
  else delete b.discriminatorValue;
  return b;
}

/**
 * The codes an entity's reads keep: its own, and those of the entities derived from it (at any depth) whose binding to the same
 * database reads the same source. `derived` holds the documents of those entities.
 */
export function readCodes(entity: Json, binding: Binding, derived: readonly Json[]): string[] {
  const codeOf = (e: Json, b: Binding) => String(b.discriminatorValue ?? e.name ?? "");
  const out = [codeOf(entity, binding)];
  for (const d of derived) {
    const b = ((d.bindings as Binding[] | undefined) ?? []).find((x) => x.database === binding.database && x.source === binding.source);
    if (b) out.push(codeOf(d, b));
  }
  return out;
}
