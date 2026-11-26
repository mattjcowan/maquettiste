// The type column of a hierarchy kept in one table (a base's binding names the column; a derived entity's binding to the same table
// inherits it), free of React: what the Storage tab's type column controls write, and what they show. It mirrors the engine's rules
// (DatabaseRun.Bindings.cs): the owner of an entity's type column is the nearest entity, itself included, whose binding to the database
// names one and doesn't also name one a base's binding keeps in the same table (MQ4126); rows are kept in the table a binding writes,
// else its source.
import type { Binding, SourceColumn } from "./fieldMap";

type Json = Record<string, unknown>;

/** Which type column control a binding card shows. */
export type TypeColumnRole =
  /** An entity outside a hierarchy. */
  | { role: "none" }
  /** The entity names the column itself (or no base's binding in its table does, so it may). `column` is the one it names, if any. */
  | { role: "base" }
  /**
   * The column is a base's, kept in the same table: read only, with the base's name. `redundant` when the binding names one as well (the
   * engine refuses it, MQ4126), so the card offers to remove it.
   */
  | { role: "derived"; column: string; base: string; redundant: boolean; canName: boolean };

const baseOf = (docs: ReadonlyMap<string, Json>, id: string): string | undefined => {
  const base = docs.get(id)?.base;
  return typeof base === "string" ? base : undefined;
};

const bindingOf = (docs: ReadonlyMap<string, Json>, id: string, database: string): Binding | undefined =>
  ((docs.get(id)?.bindings as Binding[] | undefined) ?? []).find((b) => b.database === database);

/**
 * Where a binding's rows are kept: the table it writes, else (writing nowhere or writing its source) its source. A projected table's key
 * `key@database` is the same table as `key`, as the engine finds it.
 */
export function hostOf(binding: Binding): string {
  const key = binding.write === "none" ? binding.source : typeof binding.write === "object" && binding.write ? binding.write.table : binding.source;
  const suffix = "@" + binding.database;
  return key.endsWith(suffix) ? key.slice(0, -suffix.length) : key;
}

/**
 * The entity whose binding gives the entity's type column: the nearest of the entity and its bases whose binding to the database names
 * `discriminatorColumn` without a base's binding in the same table naming one too. Null when none does.
 */
export function typeColumnOwner(entityId: string, database: string, docs: ReadonlyMap<string, Json>, binding?: Binding): string | null {
  const seen = new Set<string>();
  for (let e: string | undefined = entityId; e && !seen.has(e); e = baseOf(docs, e)) {
    seen.add(e);
    const b = e === entityId && binding ? binding : bindingOf(docs, e, database);
    if (b?.discriminatorColumn && !namesInheritedColumn(e, b, docs)) return e;
  }
  return null;
}

/** MQ4126: whether the binding names a column while a base's binding to the database, in the same table, names one too. */
function namesInheritedColumn(entityId: string, binding: Binding, docs: ReadonlyMap<string, Json>): boolean {
  const host = hostOf(binding);
  const seen = new Set([entityId]);
  for (let e = baseOf(docs, entityId); e && !seen.has(e); e = baseOf(docs, e)) {
    seen.add(e);
    const b = bindingOf(docs, e, binding.database);
    if (b?.discriminatorColumn && hostOf(b) === host) return true;
  }
  return false;
}

/**
 * The type column an entity reads from a base, by the engine's owner rule (typeColumnOwner): the owner's name and the column its binding
 * names. Undefined when the entity names its own or no entity does. A base in another table is inherited from too, when the entity names none.
 */
export function inheritedTypeColumn(
  entityId: string,
  binding: Binding,
  docs: ReadonlyMap<string, Json>,
): { column: string; base: string; sameTable: boolean } | undefined {
  const owner = typeColumnOwner(entityId, binding.database, docs, binding);
  if (!owner || owner === entityId) return undefined;
  const ownerBinding = bindingOf(docs, owner, binding.database);
  return ownerBinding?.discriminatorColumn
    ? { column: ownerBinding.discriminatorColumn, base: String(docs.get(owner)?.name ?? owner), sameTable: hostOf(ownerBinding) === hostOf(binding) }
    : undefined;
}

/**
 * Which type column control a binding card shows, from the same owner rule as the engine. An entity outside a hierarchy has none. When a
 * base owns the column, the card reads it from there (and offers a column of its own unless the binding names one already, which the
 * engine refuses when the base keeps its rows in the same table: `redundant`). Otherwise the entity names its own.
 * `hasDerived`: some entity derives from this one.
 */
export function typeColumnRole(entityId: string, binding: Binding, docs: ReadonlyMap<string, Json>, hasDerived: boolean): TypeColumnRole {
  const inherited = inheritedTypeColumn(entityId, binding, docs);
  // Naming a column of its own is refused when the base that owns the column keeps its rows in the same table (MQ4126).
  if (inherited)
    return { role: "derived", column: inherited.column, base: inherited.base, redundant: !!binding.discriminatorColumn, canName: !inherited.sameTable };
  return hasDerived || baseOf(docs, entityId) ? { role: "base" } : { role: "none" };
}

/** Names the type column (a column of the source, by its key), or (null) drops it; the code stays. */
export function setTypeColumn(b: Binding, column: SourceColumn | null): Binding {
  if (column) b.discriminatorColumn = column.key;
  else delete b.discriminatorColumn;
  return b;
}

/** Whether a source column's type, a model type keyword, holds whole numbers (int16, int32 or int64). */
export function isIntegerColumn(type: string | null | undefined): boolean {
  return type === "int16" || type === "int32" || type === "int64";
}

/**
 * A type code's value for its text. The engine reads a JSON number as an integer code and refuses a text code on an integer column (and the
 * other way round), so the value follows the column: a number for an integer column (when the text is a whole number), else text.
 * `integer` null: the column is not known, so the code keeps the kind it has.
 */
export function codeValue(text: string, integer: boolean | null, existing?: string | number): string | number | undefined {
  const t = text.trim();
  if (!t) return undefined;
  const whole = /^-?\d+$/.test(t) && Number.isSafeInteger(Number(t));
  return whole && (integer === true || (integer === null && typeof existing === "number")) ? Number(t) : t;
}

/** Whether two codes are the same value of the same JSON type (1 and "1" are different codes to the engine). */
export const sameCode = (a: string | number | undefined, b: string | number | undefined): boolean => typeof a === typeof b && a === b;

/** Sets or clears the entity's type code, typed as the column is. */
export function setTypeCode(b: Binding, text: string, integer: boolean | null = null): Binding {
  const value = codeValue(text, integer, b.discriminatorValue);
  if (value === undefined) delete b.discriminatorValue;
  else b.discriminatorValue = value;
  return b;
}

/** Gives the code the JSON type of the column it is written for, after the column or the code was changed. */
export function retypeCode(b: Binding, integer: boolean | null): Binding {
  if (b.discriminatorValue !== undefined) setTypeCode(b, String(b.discriminatorValue), integer);
  return b;
}

/**
 * The codes an entity's reads keep, as the engine works them out: its own, and those of the entities derived from it (at any depth) whose
 * binding to the database keeps its rows in the same table and shares the entity's type column owner. Each code once. `docs` holds the
 * entity, its bases and the derived entities' documents.
 */
export function readCodes(entityId: string, binding: Binding, derivedIds: readonly string[], docs: ReadonlyMap<string, Json>): (string | number)[] {
  const owner = typeColumnOwner(entityId, binding.database, docs, binding);
  if (!owner) return [binding.discriminatorValue ?? String(docs.get(entityId)?.name ?? "")];
  const host = hostOf(binding);
  const codeOf = (id: string, b: Binding): string | number => b.discriminatorValue ?? String(docs.get(id)?.name ?? "");
  const out: (string | number)[] = [codeOf(entityId, binding)];
  for (const id of derivedIds) {
    const b = bindingOf(docs, id, binding.database);
    if (!b || hostOf(b) !== host || typeColumnOwner(id, binding.database, docs) !== owner) continue;
    const code = codeOf(id, b);
    if (!out.some((c) => sameCode(c, code))) out.push(code);
  }
  return out;
}
