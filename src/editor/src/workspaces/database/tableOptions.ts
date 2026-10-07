// A table's options beyond its columns, keys and indexes (the Exclusions and Storage tabs, and the index and stereotype storage
// fields): storage parameters per dialect, partitioning, exclusion constraints. Pure edits of a table (or stereotype, index)
// document; each caller makes one save of them. What PostgreSQL alone writes says so beside the field on another dialect (MQ4056).
import { applyPropertyEdit, type PropertyEdit } from "@/inspector/propertyBag";

type Json = Record<string, unknown>;

/** The dialects a storage map has entries for (SQLite has no storage parameters). */
export const STORAGE_DIALECTS = ["postgresql", "sqlserver", "mysql", "oracle"] as const;

/** The PostgreSQL table storage parameters (each vacuum one also as toast.<name>), as the engine's MQ4064 knows them. */
export const POSTGRES_TABLE_PARAMETERS = [
  "fillfactor",
  "toast_tuple_target",
  "parallel_workers",
  "autovacuum_enabled",
  "vacuum_index_cleanup",
  "vacuum_truncate",
  "autovacuum_vacuum_threshold",
  "autovacuum_vacuum_max_threshold",
  "autovacuum_vacuum_scale_factor",
  "autovacuum_vacuum_insert_threshold",
  "autovacuum_vacuum_insert_scale_factor",
  "autovacuum_analyze_threshold",
  "autovacuum_analyze_scale_factor",
  "autovacuum_vacuum_cost_delay",
  "autovacuum_vacuum_cost_limit",
  "autovacuum_freeze_min_age",
  "autovacuum_freeze_max_age",
  "autovacuum_freeze_table_age",
  "autovacuum_multixact_freeze_min_age",
  "autovacuum_multixact_freeze_max_age",
  "autovacuum_multixact_freeze_table_age",
  "log_autovacuum_min_duration",
  "user_catalog_table",
] as const;

/** The storage parameters a PostgreSQL index method takes (hnsw and ivfflat: the vector extension's). */
export function postgresIndexParameters(method: string): string[] {
  switch (method) {
    case "hash":
    case "spgist":
      return ["fillfactor"];
    case "gist":
      return ["fillfactor", "buffering"];
    case "gin":
      return ["fastupdate", "gin_pending_list_limit"];
    case "brin":
      return ["pages_per_range", "autosummarize"];
    case "hnsw":
      return ["m", "ef_construction"];
    case "ivfflat":
      return ["lists"];
    default:
      return ["fillfactor", "deduplicate_items"];
  }
}

/** A storage parameter name's form problem, or null (the schema's pattern). */
export function storageKeyProblem(key: string): string | null {
  return /^[A-Za-z_][A-Za-z0-9_.]*$/.test(key.trim()) ? null : "A parameter name is letters, digits, _ and . (it starts with a letter or _).";
}

/** The parameters of a storage map for one dialect (an empty object when there are none). */
export function storageOf(owner: Json | undefined, dialect: string): Json {
  const storage = owner?.storage as Record<string, Json> | undefined;
  return storage?.[dialect] ?? {};
}

/** Applies a parameter edit to an owner's storage for one dialect, dropping the dialect and the map when they are left empty. */
export function editStorage(owner: Json, dialect: string, edit: PropertyEdit): void {
  const storage = { ...((owner.storage as Record<string, Json> | undefined) ?? {}) };
  const next = applyPropertyEdit(storage[dialect], edit);
  if (next) storage[dialect] = next;
  else delete storage[dialect];
  if (Object.keys(storage).length) owner.storage = storage;
  else delete owner.storage;
}

/** The parameters a table's stereotypes give it for a dialect (stereotype order, later wins), each with the stereotype it comes from. */
export function inheritedStorage(
  stereotypes: readonly { key: string; storage?: unknown }[],
  dialect: string,
): Record<string, { value: unknown; from: string }> {
  const out: Record<string, { value: unknown; from: string }> = {};
  for (const s of stereotypes) {
    for (const [name, value] of Object.entries(storageOf(s as Json, dialect))) out[name] = { value, from: s.key };
  }
  return out;
}

/** Applies a setting edit to a routine's settings (PostgreSQL SET name = value), dropping the map when it is left empty. */
export function editSettings(routine: Json, edit: PropertyEdit): void {
  const next = applyPropertyEdit(routine.settings, edit);
  if (next) routine.settings = next;
  else delete routine.settings;
}

/** What a dialect does with a PostgreSQL feature: null on PostgreSQL, else the note (MQ4056). */
export function postgresOnlyNote(dialect: string, what: string): string | null {
  return dialect === "postgresql" ? null : `Only PostgreSQL has ${what}; this dialect leaves it out (MQ4056).`;
}

// ------------------------------------------------------------------ partitioning

export const PARTITION_STRATEGIES = ["range", "list", "hash"] as const;

/** Partitions the table by a strategy (keeping its columns), or, with none, makes it a plain table again (its partitions go too). */
export function setPartitionStrategy(doc: Json, strategy: string | undefined, firstColumn: string | undefined): void {
  if (!strategy) {
    delete doc.partitionBy;
    delete doc.partitions;
    return;
  }
  const current = doc.partitionBy as Json | undefined;
  const columns = (current?.columns as string[] | undefined) ?? (firstColumn ? [firstColumn] : []);
  doc.partitionBy = { strategy, columns };
  // A hash-partitioned table has no default partition.
  if (strategy === "hash") for (const p of (doc.partitions as Json[] | undefined) ?? []) delete p.default;
}

/** Ticks or unticks a partition column; the last one stays (the partitioning needs one). */
export function togglePartitionColumn(doc: Json, column: string): boolean {
  const by = doc.partitionBy as Json | undefined;
  if (!by) return false;
  const columns = (by.columns as string[] | undefined) ?? [];
  const next = columns.includes(column) ? columns.filter((c) => c !== column) : [...columns, column];
  if (!next.length) return false;
  by.columns = next;
  return true;
}

/** Adds a partition named after the table (<table>_p<n>), with bounds to fill in, or the default partition. */
export function addPartition(doc: Json, id: string, table: string, isDefault = false): Json {
  const partitions = [...((doc.partitions as Json[] | undefined) ?? [])];
  const names = new Set(partitions.map((p) => String(p.name)));
  let n = partitions.length + 1;
  let name = isDefault ? `${table}_default` : `${table}_p${n}`;
  while (names.has(name)) name = `${table}_p${++n}`;
  const strategy = String((doc.partitionBy as Json | undefined)?.strategy ?? "range");
  const bounds =
    strategy === "list" ? "IN ()" : strategy === "hash" ? `WITH (MODULUS ${partitions.length + 1}, REMAINDER ${partitions.length})` : "FROM () TO ()";
  const partition: Json = isDefault ? { id, name, default: true } : { id, name, bounds };
  partitions.push(partition);
  doc.partitions = partitions;
  return partition;
}

/** Sets a partition's name or bounds (a default partition takes no bounds); false when the edit is refused. */
export function setPartition(doc: Json, at: number, member: "name" | "bounds", value: string): boolean {
  const partitions = (doc.partitions as Json[] | undefined) ?? [];
  const p = partitions[at];
  const text = value.trim();
  if (!p || !text) return false;
  if (member === "name" && partitions.some((x, i) => i !== at && x.name === text)) return false;
  if (member === "bounds" && p.default === true) return false;
  p[member] = text;
  return true;
}

/** Removes a partition (the partitions member goes with the last one). */
export function removePartition(doc: Json, at: number): void {
  const partitions = ((doc.partitions as Json[] | undefined) ?? []).filter((_, i) => i !== at);
  if (partitions.length) doc.partitions = partitions;
  else delete doc.partitions;
}

/** Why a partitioned table's keys need more columns (MQ4066), or null: every primary or unique key includes the partition columns. */
export function partitionKeyProblem(doc: Json, nameOf: (id: string) => string): string | null {
  const by = (doc.partitionBy as Json | undefined)?.columns as string[] | undefined;
  if (!by?.length) return null;
  const pk = (doc.primaryKey as Json | undefined)?.columns as string[] | undefined;
  const missing = pk ? by.filter((c) => !pk.includes(c)) : [];
  if (missing.length)
    return `The primary key lacks ${missing.map(nameOf).join(", ")}: every key of a partitioned table includes the partition columns (MQ4066).`;
  for (const u of (doc.uniques as Json[] | undefined) ?? []) {
    const lacks = by.filter((c) => !((u.columns as string[] | undefined) ?? []).includes(c));
    if (lacks.length) return `Unique constraint ${String(u.name ?? u.id)} lacks ${lacks.map(nameOf).join(", ")} (MQ4066).`;
  }
  return null;
}

// ------------------------------------------------------------------ exclusion constraints

export const EXCLUSION_METHODS = ["gist", "spgist", "btree", "hash"] as const;

/** Adds an exclusion constraint over a column compared with =, to change from there; its name is left to the convention. */
export function addExclusion(doc: Json, id: string, column: string): Json {
  const exclusion: Json = { id, elements: [{ column, operator: "=" }] };
  doc.exclusions = [...((doc.exclusions as Json[] | undefined) ?? []), exclusion];
  return exclusion;
}

export function removeExclusion(doc: Json, at: number): void {
  const list = ((doc.exclusions as Json[] | undefined) ?? []).filter((_, i) => i !== at);
  if (list.length) doc.exclusions = list;
  else delete doc.exclusions;
}

/** Sets an exclusion constraint's name, method, where or deferrable; an empty text or the default removes it. */
export function setExclusion(doc: Json, at: number, member: "name" | "method" | "where" | "deferrable", value: string): boolean {
  const x = ((doc.exclusions as Json[] | undefined) ?? [])[at];
  if (!x) return false;
  const text = value.trim();
  const isDefault = !text || (member === "method" && text === "gist") || (member === "deferrable" && text === "not-deferrable");
  if (isDefault) delete x[member];
  else x[member] = text;
  return true;
}

/** Adds an element (a column compared with =). */
export function addExclusionElement(doc: Json, at: number, column: string): boolean {
  const x = ((doc.exclusions as Json[] | undefined) ?? [])[at];
  if (!x) return false;
  x.elements = [...((x.elements as Json[] | undefined) ?? []), { column, operator: "=" }];
  return true;
}

/**
 * Sets an element's column, expression, operator or operator class: a column replaces the expression and the other way round;
 * the operator is required; an empty operator class removes it.
 */
export function setExclusionElement(
  doc: Json,
  at: number,
  element: number,
  member: "column" | "expression" | "operator" | "operatorClass",
  value: string,
): boolean {
  const e = (((doc.exclusions as Json[] | undefined) ?? [])[at]?.elements as Json[] | undefined)?.[element];
  if (!e) return false;
  const text = value.trim();
  if (member === "operator" && !text) return false;
  if ((member === "column" || member === "expression") && !text) return false;
  if (!text) delete e[member];
  else e[member] = text;
  if (member === "column") delete e.expression;
  if (member === "expression") delete e.column;
  return true;
}

/** Removes an element; the last one stays (an exclusion constraint compares one at least). */
export function removeExclusionElement(doc: Json, at: number, element: number): boolean {
  const x = ((doc.exclusions as Json[] | undefined) ?? [])[at];
  const elements = (x?.elements as Json[] | undefined) ?? [];
  if (!x || elements.length < 2) return false;
  x.elements = elements.filter((_, i) => i !== element);
  return true;
}

/** The text of an exclusion constraint as the DDL writes it (EXCLUDE USING gist (room_id WITH =, during WITH &&)). */
export function exclusionText(x: Json, nameOf: (id: string) => string): string {
  const parts = ((x.elements as Json[] | undefined) ?? []).map(
    (e) =>
      `${typeof e.column === "string" ? nameOf(e.column) : `(${String(e.expression ?? "")})`}${e.operatorClass ? ` ${String(e.operatorClass)}` : ""} WITH ${String(e.operator ?? "")}`,
  );
  return `EXCLUDE USING ${String(x.method ?? "gist")} (${parts.join(", ")})${x.where ? ` WHERE (${String(x.where)})` : ""}`;
}
