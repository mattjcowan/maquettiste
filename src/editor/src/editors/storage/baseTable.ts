// Table per type on the Storage tab (retire-projection-design.md section 4), free of React: the base binding a derived entity's
// binding joins, the foreign keys that fit `baseForeignKey`, the key the join gives, what the base's binding stores (so the
// derived binding's field map leaves it out), and the engine's checks the editor can make on its own (MQ4128 to MQ4133).
import { findColumn, storageAttributes, type Binding, type MapProblem, type StorageAttribute } from "./fieldMap";
import { hostOf } from "./typeColumn";
import { columnIds, isWholePrimaryKey, plainTable, type TableDoc, type TableDocFk } from "./tableDoc";

type Json = Record<string, unknown>;

export interface BoundBase {
  entityId: string;
  name: string;
  binding: Binding;
}

/** The nearest base of the entity with a binding to the database (the engine's bound base), or undefined. */
export function boundBaseOf(entityId: string, database: string, docs: ReadonlyMap<string, Json>): BoundBase | undefined {
  const seen = new Set([entityId]);
  for (let id = docs.get(entityId)?.base as string | undefined; id && !seen.has(id); id = docs.get(id)?.base as string | undefined) {
    seen.add(id);
    const doc = docs.get(id);
    const binding = ((doc?.bindings as Binding[] | undefined) ?? []).find((b) => b.database === database);
    if (binding) return { entityId: id, name: String(doc?.name ?? id), binding };
  }
  return undefined;
}

/**
 * Whether the base's binding writes a table: a named write table does, `none` does not, and by default it writes its source when that is a
 * table file (a base read from a view or a query writes nothing, which leaves the derived binding read only). Undefined while the source
 * is not read.
 */
export function baseWrites(base: Binding, baseSource?: TableDoc): boolean | undefined {
  if (base.write === "none") return false;
  if (typeof base.write === "object") return true;
  return baseSource ? baseSource.kind === undefined || baseSource.kind === "table" : undefined;
}

/**
 * The bound bases whose tables an entity's rows also live in (table per type), nearest first, as the engine reads them: the base a base
 * foreign key reaches, then its own levels; a binding without a key that keeps its rows in its bound base's table shares the base's levels.
 */
export function upperLevels(entityId: string, binding: Binding, docs: ReadonlyMap<string, Json>, depth = 0): BoundBase[] {
  const base = boundBaseOf(entityId, binding.database, docs);
  if (!base || depth > 16) return [];
  const levels: BoundBase[] = binding.baseForeignKey ? [base] : [];
  if (levels.length || hostOf(binding) === hostOf(base.binding))
    for (const l of upperLevels(base.entityId, base.binding, docs, depth + 1)) if (!levels.some((x) => x.entityId === l.entityId)) levels.push(l);
  return levels;
}

/**
 * The foreign keys of the binding's table the editor offers for `baseForeignKey`: those that reference the base binding's table and
 * whose columns are the table's whole primary key.
 */
export function baseKeyChoices(table: TableDoc | undefined, baseTable: string | undefined): TableDocFk[] {
  if (!table || !baseTable) return [];
  return (table.foreignKeys ?? []).filter((k) => k.referencesTable === baseTable && isWholePrimaryKey(table, columnIds(table, k.columns)));
}

export interface JoinedKeyColumn {
  /** The column of the derived binding's table (id) and its name. */
  column: string;
  name: string;
  /** The column of the base's table it references (name). */
  referenced: string;
  /** The key attribute of the base's binding that column carries, when the base's binding maps the referenced column. */
  attribute: string | null;
}

/** The columns the base foreign key joins, each with the key attribute of the base's binding it carries. */
export function joinedKeyColumns(
  fk: TableDocFk,
  table: TableDoc | undefined,
  baseTable: TableDoc | undefined,
  baseBinding: Binding | undefined,
  baseAttributes: readonly StorageAttribute[],
): JoinedKeyColumn[] {
  const baseColumns = (baseTable?.columns ?? []).map((c) => ({ key: c.id, name: c.name ?? c.id }));
  return fk.columns.map((ref, i) => {
    const id = columnIds(table, [ref])[0];
    const referencedRef = fk.referencesColumns?.[i] ?? baseTable?.primaryKey?.columns[i];
    const referenced = referencedRef ? findColumn(baseColumns, referencedRef) : undefined;
    const field = referenced ? (baseBinding?.fields ?? []).find((f) => findColumn(baseColumns, f.column) === referenced) : undefined;
    return {
      column: id,
      name: table?.columns?.find((c) => c.id === id)?.name ?? ref,
      referenced: referenced?.name ?? referencedRef ?? "",
      attribute: field ? (baseAttributes.find((a) => a.ref === field.attribute)?.name ?? null) : null,
    };
  });
}

/** What the base's binding stores for a derived binding with a base foreign key: the refs of every attribute and to-one end of the base. */
export function baseStoredRefs(base: BoundBase | undefined, docs: ReadonlyMap<string, Json>): Set<string> {
  return new Set(base ? storageAttributes(base.entityId, docs).map((a) => a.ref) : []);
}

/**
 * The engine's checks on `baseForeignKey` that the editor can make from the tables: MQ4130 (no bound base), MQ4128 (not a key of the table,
 * or it does not reference the base's table), MQ4129 (not the whole primary key), MQ4133 (the binding keeps its rows in the base's table)
 * and MQ4131 (the binding writes while the base writes nothing). `table` is the binding's table document, undefined while unknown (then
 * nothing is said about the keys).
 */
export function baseProblems(binding: Binding, base: BoundBase | undefined, table: TableDoc | undefined, baseSource?: TableDoc): MapProblem[] {
  const key = binding.baseForeignKey;
  if (!key) return [];
  const out: MapProblem[] = [];
  const at = "/baseForeignKey";
  if (!base) {
    out.push({
      rule: "MQ4130",
      severity: "error",
      message: "The binding names a base foreign key, but no base of the entity has a binding to this database.",
      pointer: at,
    });
    return out;
  }
  const baseHost = hostOf(base.binding);
  if (hostOf(binding) === baseHost) {
    out.push({
      rule: "MQ4133",
      severity: "error",
      message: `The binding names a base foreign key, but it keeps its rows in the table of ${base.name}'s binding: remove the key.`,
      pointer: at,
    });
    return out;
  }
  const writes = baseWrites(base.binding, baseSource);
  const fk = table?.foreignKeys?.find((k) => k.id === key);
  // A base read from a view or query has no table for the key to reference: the engine pairs the columns by name there.
  if (writes !== false && table && (!fk || fk.referencesTable !== plainTable(baseHost)))
    out.push({
      rule: "MQ4128",
      severity: "error",
      message: !fk
        ? `The base foreign key '${key}' is not a foreign key of ${table.name ?? "the binding's table"}.`
        : `The base foreign key does not reference the table of ${base.name}'s binding.`,
      pointer: at,
    });
  else if (writes !== false && table && fk && !isWholePrimaryKey(table, columnIds(table, fk.columns)))
    out.push({
      rule: "MQ4129",
      severity: "error",
      message: `The columns of the base foreign key are not the whole primary key of ${table.name ?? "the table"}.`,
      pointer: at,
    });
  if (binding.write !== "none" && writes === false)
    out.push({
      rule: "MQ4131",
      severity: "error",
      message: `The binding writes, but ${base.name}'s binding writes no table: make the binding read only.`,
      pointer: "/write",
    });
  return out;
}

/**
 * Names the base foreign key, or (null) removes it. Naming it drops the fields that map what the base's binding stores (the key among
 * them: the foreign key gives it), which the engine refuses beside a base foreign key (MQ4132).
 */
export function setBaseForeignKey(b: Binding, key: string | null, baseStored: ReadonlySet<string>): Binding {
  if (!key) {
    delete b.baseForeignKey;
    return b;
  }
  b.baseForeignKey = key;
  b.fields = (b.fields ?? []).filter((f) => !baseStored.has(f.attribute));
  if (!b.fields.length) delete b.fields;
  return b;
}
