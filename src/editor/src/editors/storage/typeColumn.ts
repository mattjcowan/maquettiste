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
  | { role: "derived"; column: string; base: string; redundant: boolean };

const baseOf = (docs: ReadonlyMap<string, Json>, id: string): string | undefined => {
  const base = docs.get(id)?.base;
  return typeof base === "string" ? base : undefined;
};

const bindingOf = (docs: ReadonlyMap<string, Json>, id: string, database: string): Binding | undefined =>
  ((docs.get(id)?.bindings as Binding[] | undefined) ?? []).find((b) => b.database === database);

/** Where a binding's rows are kept: the table it writes, else (writing nowhere or writing its source) its source. */
export function hostOf(binding: Binding): string {
  if (binding.write === "none") return binding.source;
  return typeof binding.write === "object" && binding.write ? binding.write.table : binding.source;
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

/** The nearest base whose binding to the database names a type column in the same table as the binding: its name and the column. */
export function inheritedTypeColumn(entityId: string, binding: Binding, docs: ReadonlyMap<string, Json>): { column: string; base: string } | undefined {
  const host = hostOf(binding);
  const seen = new Set([entityId]);
  for (let e = baseOf(docs, entityId); e && !seen.has(e); e = baseOf(docs, e)) {
    seen.add(e);
    const b = bindingOf(docs, e, binding.database);
    if (b?.discriminatorColumn && hostOf(b) === host) return { column: b.discriminatorColumn, base: String(docs.get(e)?.name ?? e) };
  }
  return undefined;
}

/**
 * Which type column control a binding card shows. `hasDerived`: some entity derives from this one. An entity outside a hierarchy has
 * none; one whose bases name a column in the same table reads it; any other entity in a hierarchy may name its own.
 */
export function typeColumnRole(entityId: string, binding: Binding, docs: ReadonlyMap<string, Json>, hasDerived: boolean): TypeColumnRole {
  const inherited = inheritedTypeColumn(entityId, binding, docs);
  if (inherited) return { role: "derived", ...inherited, redundant: !!binding.discriminatorColumn };
  return hasDerived || baseOf(docs, entityId) ? { role: "base" } : { role: "none" };
}

/** Names the type column (a column of the source, by its key), or (null) drops it; the code stays. */
export function setTypeColumn(b: Binding, column: SourceColumn | null): Binding {
  if (column) b.discriminatorColumn = column.key;
  else delete b.discriminatorColumn;
  return b;
}

/** Whether a source column's type holds whole numbers (a built-in integer keyword or a native integer type). */
export function isIntegerColumn(type: string | null | undefined): boolean {
  return !!type && /^(u?int(8|16|32|64)?|u?(tiny|small|medium|big)?int(eger)?(\(\d+\))?|serial|smallserial|bigserial)(?![a-z0-9_])/i.test(type.trim());
}

/**
 * Sets or clears the entity's type code. The engine reads a JSON number as an integer code and refuses a text code on an integer
 * column (MQ4125), so a numeric text is written as a number when the column holds integers (or when the code already is one), and as
 * text otherwise.
 */
export function setTypeCode(b: Binding, text: string, integerColumn = false): Binding {
  const t = text.trim();
  if (!t) delete b.discriminatorValue;
  else if (/^-?\d+$/.test(t) && Number.isSafeInteger(Number(t)) && (integerColumn || typeof b.discriminatorValue === "number"))
    b.discriminatorValue = Number(t);
  else b.discriminatorValue = t;
  return b;
}

/**
 * The codes an entity's reads keep, as the engine works them out: its own, and those of the entities derived from it (at any depth) whose
 * binding to the database keeps its rows in the same table and shares the entity's type column owner. Each code once. `docs` holds the
 * entity, its bases and the derived entities' documents.
 */
export function readCodes(entityId: string, binding: Binding, derivedIds: readonly string[], docs: ReadonlyMap<string, Json>): string[] {
  const owner = typeColumnOwner(entityId, binding.database, docs, binding);
  if (!owner) return [String(binding.discriminatorValue ?? docs.get(entityId)?.name ?? "")];
  const host = hostOf(binding);
  const codeOf = (id: string, b: Binding) => String(b.discriminatorValue ?? docs.get(id)?.name ?? "");
  const out = [codeOf(entityId, binding)];
  for (const id of derivedIds) {
    const b = bindingOf(docs, id, binding.database);
    if (!b || hostOf(b) !== host || typeColumnOwner(id, binding.database, docs) !== owner) continue;
    const code = codeOf(id, b);
    if (!out.includes(code)) out.push(code);
  }
  return out;
}
