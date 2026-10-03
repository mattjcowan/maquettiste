// What a foreign key may reference (MQ4059, PhysicalRules.CheckReferencedKey): the referenced table's primary key or one of its
// unique keys (a unique constraint, or a unique index without a filter; Oracle takes a constraint only), the columns in any order,
// each named once (as many as the key has); on MySQL (and MariaDB) some primary key, unique constraint or index must also start
// with the referenced columns in the order the foreign key names them. Shared by the mock's validation (table documents) and the
// foreign key dialog (the resolved view).

/** A table's keys: its primary key's columns and its unique constraints' and unique indexes' (each index with whether it filters). */
export interface TableKeys {
  primaryKey: readonly string[] | null;
  uniques: readonly (readonly string[])[];
  uniqueIndexes: readonly { columns: readonly string[]; partial: boolean }[];
  /** Every index's leading plain columns (up to its first expression), unique or not: what MySQL's order rule reads. */
  indexes?: readonly (readonly string[])[];
}

/** The same columns, each once, as many on both sides (["a", "a"] is not the key ["a"]). */
const sameSet = (a: readonly string[], b: readonly string[]) => {
  const x = new Set(a);
  const y = new Set(b);
  return a.length === b.length && x.size === a.length && y.size === b.length && [...x].every((c) => y.has(c));
};

/** Why a foreign key may not reference `columns` (MQ4059), or null: a column named twice, no key, or (MySQL) no index in that order. */
export type ReferencedKeyProblem = { kind: "twice"; column: string } | { kind: "not-key" } | { kind: "order" };

export function referencedKeyProblem(keys: TableKeys, columns: readonly string[], dialect: string): ReferencedKeyProblem | null {
  if (!columns.length) return null;
  const seen = new Set<string>();
  for (const c of columns) {
    if (seen.has(c)) return { kind: "twice", column: c };
    seen.add(c);
  }
  if (!isKeyOf(keys, columns, dialect)) return { kind: "not-key" };
  if (dialect !== "mysql") return null;
  const leads = (list: readonly string[]) => list.length >= columns.length && columns.every((c, i) => list[i] === c);
  if ((keys.primaryKey && leads(keys.primaryKey)) || keys.uniques.some(leads) || (keys.indexes ?? []).some(leads)) return null;
  return { kind: "order" };
}

/** The engine's MQ4059 message for a problem, the columns named by `nameOf`. */
export function referencedKeyMessage(
  problem: ReferencedKeyProblem,
  fk: string,
  table: string,
  columns: readonly string[],
  nameOf: (id: string) => string,
): string {
  const names = columns.map(nameOf).join(", ");
  if (problem.kind === "twice")
    return `Foreign key '${fk}' references (${names}) of table '${table}', naming column '${nameOf(problem.column)}' more than once; reference each column of the key once.`;
  if (problem.kind === "order")
    return `Foreign key '${fk}' references (${names}) of table '${table}' in an order no index of it starts with; MySQL needs an index whose first columns are the referenced ones in the same order: reference them in the key's order, or add such an index.`;
  return `Foreign key '${fk}' references (${names}) of table '${table}', which is neither its primary key nor one of its unique keys; reference its key, or add a unique constraint on those columns.`;
}

/** Whether `columns` are a key of the table: its primary key, a unique constraint, or (not on Oracle) a unique index without a filter. */
export function isKeyOf(keys: TableKeys, columns: readonly string[], dialect: string): boolean {
  if (!columns.length) return false;
  if (keys.primaryKey && sameSet(keys.primaryKey, columns)) return true;
  if (keys.uniques.some((u) => sameSet(u, columns))) return true;
  return dialect !== "oracle" && keys.uniqueIndexes.some((i) => !i.partial && sameSet(i.columns, columns));
}

/** The keys of a table document (schemas/v1/table.json). */
export function keysOfDocument(doc: Record<string, unknown>): TableKeys {
  const list = (v: unknown) => (Array.isArray(v) ? (v as Record<string, unknown>[]) : []);
  const ids = (v: unknown) => (Array.isArray(v) ? (v as unknown[]).map(String) : []);
  const pk = doc.primaryKey as Record<string, unknown> | undefined;
  return {
    primaryKey: pk ? ids(pk.columns) : null,
    uniques: list(doc.uniques).map((u) => ids(u.columns)),
    uniqueIndexes: list(doc.indexes)
      // An index on an expression is no key a foreign key can reference.
      .filter((i) => i.unique === true && list(i.columns).every((c) => typeof c.column === "string"))
      .map((i) => ({ columns: list(i.columns).map((c) => String(c.column)), partial: typeof i.where === "string" })),
    indexes: list(doc.indexes).map((i) => leading(list(i.columns).map((c) => c.column))),
  };
}

/** An index's columns up to its first expression. */
function leading(columns: readonly unknown[]): string[] {
  const out: string[] = [];
  for (const c of columns) {
    if (typeof c !== "string" || !c) break;
    out.push(c);
  }
  return out;
}

/** The keys of a resolved table (the database view's TableView). */
export function keysOfView(table: {
  primaryKey: { columns: string[] } | null;
  uniques: { columns: string[] }[];
  indexes: { unique: boolean; where?: string | null; columns: { column: string | null }[] }[];
}): TableKeys {
  return {
    primaryKey: table.primaryKey?.columns ?? null,
    uniques: table.uniques.map((u) => u.columns),
    uniqueIndexes: table.indexes
      .filter((i) => i.unique && i.columns.every((c) => !!c.column))
      .map((i) => ({ columns: i.columns.map((c) => String(c.column)), partial: !!i.where })),
    indexes: table.indexes.map((i) => leading(i.columns.map((c) => c.column))),
  };
}

/** The key a column belongs to that a foreign key would reference whole: the primary key when it holds the column, else the first
 * unique constraint (then unique index) that does; null when none does. */
export function keyHolding(keys: TableKeys, column: string, dialect: string): readonly string[] | null {
  if (keys.primaryKey?.includes(column)) return keys.primaryKey;
  const unique = keys.uniques.find((u) => u.includes(column));
  if (unique) return unique;
  if (dialect === "oracle") return null;
  return keys.uniqueIndexes.find((i) => !i.partial && i.columns.includes(column))?.columns ?? null;
}
