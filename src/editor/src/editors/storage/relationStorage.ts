// A relationship's storage in one database (erratum E43, engine-design.md 7.4), free of React: when both ends are bound to
// tables, which foreign key realizes it (the relation mapping's `foreignKey`), or for a many-to-many the junction table and the
// foreign key of it that realizes each end (`junctionTable`, `ends`).

type Json = Record<string, unknown>;

export interface ForeignKeyDoc {
  id: string;
  name?: string;
  columns: string[];
  referencesTable: string;
}

export interface TableDocLike {
  id: string;
  name?: string;
  columns?: { id: string; name?: string }[];
  foreignKeys?: ForeignKeyDoc[];
}

/** The table an entity's binding to the database reads and writes: its write table, else its source; null when unbound. */
export function boundTableOf(entity: Json | undefined, database: string): string | null {
  const binding = ((entity?.bindings as Json[] | undefined) ?? []).find((b) => b.database === database);
  if (!binding) return null;
  const write = binding.write as Json | string | undefined;
  if (write && typeof write === "object" && typeof write.table === "string") return write.table;
  return typeof binding.source === "string" ? binding.source : null;
}

export interface ForeignKeyChoice {
  /** The table holding the key. */
  table: TableDocLike;
  /** The end (0 or 1) whose bound table holds the key: the dependent end the key makes. */
  end: 0 | 1;
  key: ForeignKeyDoc;
  /** "orders.fk_orders_customer (customer_id → customers)". */
  label: string;
}

const columnNames = (t: TableDocLike, ids: readonly string[]) => ids.map((c) => t.columns?.find((x) => x.id === c)?.name ?? c).join(", ");

export interface RelationEndLike {
  id: string;
  min?: number;
  max?: number | "*";
}

/**
 * Which end of a binary relation holds the foreign key (the dependent end), as the engine reads it (MappingRules.DependentEnd):
 * the many end of a one-to-many; of a one-to-one, the optional end when one end is required, else the mapping's
 * `foreignKeyEnd` (default the second end), and then either end may hold it (`tie`). Null for a many-to-many or an n-ary one.
 */
export function dependentEnd(ends: readonly RelationEndLike[], foreignKeyEnd?: string | null): { index: 0 | 1; tie: boolean } | null {
  if (ends.length !== 2) return null;
  const [a, b] = ends;
  const aMany = (a.max ?? "*") === "*";
  const bMany = (b.max ?? "*") === "*";
  if (aMany && !bMany) return { index: 0, tie: false };
  if (bMany && !aMany) return { index: 1, tie: false };
  if (aMany && bMany) return null;
  const aMin = a.min ?? 0;
  const bMin = b.min ?? 0;
  if (aMin !== bMin) return { index: aMin === 1 ? 1 : 0, tie: false };
  return { index: foreignKeyEnd === a.id ? 0 : 1, tie: true };
}

/**
 * The foreign keys that can realize a binary relation whose ends are bound to tables `a` and `b`: the keys of the dependent end's
 * table that reference the other end's table (the engine refuses one held by the other table, MQ4009). A one-to-one tie takes a
 * key either way (picking one names its end as `foreignKeyEnd`); with `dependent` unknown, either way too. A self relation: the
 * table's keys to itself.
 */
export function foreignKeyChoices(
  a: TableDocLike | undefined,
  b: TableDocLike | undefined,
  dependent: { index: 0 | 1; tie: boolean } | null = null,
): ForeignKeyChoice[] {
  if (!a || !b) return [];
  const out: ForeignKeyChoice[] = [];
  const add = (from: TableDocLike, to: TableDocLike, end: 0 | 1) => {
    for (const key of from.foreignKeys ?? [])
      if (key.referencesTable === to.id && !out.some((o) => o.key.id === key.id))
        out.push({ table: from, end, key, label: `${from.name ?? from.id}.${key.name ?? key.id} (${columnNames(from, key.columns)} → ${to.name ?? to.id})` });
  };
  const either = !dependent || dependent.tie;
  if (either || dependent.index === 0) add(a, b, 0);
  if (either || dependent.index === 1) add(b, a, 1);
  return out;
}

/** The keys held the other way (by the principal end's table), which cannot realize the relation: said, not offered. */
export function reverseForeignKeys(a: TableDocLike | undefined, b: TableDocLike | undefined, dependent: { index: 0 | 1; tie: boolean } | null): number {
  if (!dependent || dependent.tie || !a || !b || a.id === b.id) return 0;
  const [from, to] = dependent.index === 0 ? [b, a] : [a, b];
  return (from.foreignKeys ?? []).filter((k) => k.referencesTable === to.id).length;
}

/**
 * Names the foreign key that realizes the relation (null: none named); a named key replaces any shape. For a one-to-one tie,
 * `end` names the end whose table holds it (`foreignKeyEnd`).
 */
export function setRelationForeignKey(json: Json, key: string | null, end?: string | null): void {
  if (end !== undefined) {
    if (end) json.foreignKeyEnd = end;
    else delete json.foreignKeyEnd;
  }
  if (key) {
    json.foreignKey = key;
    delete json.shape;
    delete json.junctionTable;
    delete json.ends;
  } else delete json.foreignKey;
}

/** Names the junction table of a many-to-many (null: none); its end keys go when the table changes. */
export function setJunctionTable(json: Json, table: string | null): void {
  if (json.junctionTable !== table) delete json.ends;
  if (table) {
    json.junctionTable = table;
    json.shape = "junction";
    delete json.foreignKey;
  } else {
    delete json.junctionTable;
    if (json.shape === "junction") delete json.shape;
  }
}

/** Names the foreign key of the junction table that realizes one end (null: none). */
export function setEndForeignKey(json: Json, end: string, key: string | null): void {
  const ends = ((json.ends as { end: string; foreignKey: string }[] | undefined) ?? []).filter((e) => e.end !== end);
  if (key) ends.push({ end, foreignKey: key });
  if (ends.length) json.ends = ends;
  else delete json.ends;
}
