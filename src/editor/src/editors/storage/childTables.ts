// Child tables on the Storage tab (retire-projection-design.md section 5), free of React: which attributes a binding can store in a table
// of their own, the item's members a field can map (paths of any depth), the owner foreign keys that fit, the child table's columns with
// what accounts for each, the edits a gesture makes, and the engine's checks the editor can make on its own (MQ4138 to MQ4141, MQ4047,
// MQ4058).
import {
  applyMatches,
  columnRows,
  findColumn,
  hierarchyOf,
  loose,
  setConstants,
  setField,
  valueObjectMembers,
  wholeAbove,
  type Binding,
  type ChildTable,
  type ColumnRow,
  type MapProblem,
  type NameMatch,
  type SourceColumn,
  type StorageAttribute,
} from "./fieldMap";
import { columnIds, isPrimaryOrUnique, type TableDoc, type TableDocFk } from "./tableDoc";
import { isIntegerColumn } from "./typeColumn";

type Json = Record<string, unknown>;

interface AttrJson {
  id: string;
  name: string;
  type?: string | { ref: string };
  collection?: boolean;
  derived?: { stored?: boolean };
}

export interface ChildCandidate {
  id: string;
  name: string;
  /** A list: one row per item, with an optional position. Otherwise a single value object: at most one row. */
  isList: boolean;
  /** The entity it is inherited from, for display. */
  from?: string;
}

const voOf = (a: AttrJson, docs: ReadonlyMap<string, Json>): Json | undefined => {
  const target = typeof a.type === "object" && a.type ? docs.get(a.type.ref) : undefined;
  return target?.kind === "value-object" ? target : undefined;
};

/**
 * The attributes a binding can store in a table of its own: the entity's lists and single value objects, its own and inherited, that no
 * entry stores yet and that the base's binding does not store (when this binding joins its base's table). `baseBinding` is the bound base's.
 */
export function childCandidates(entityId: string, binding: Binding, docs: ReadonlyMap<string, Json>, baseBinding?: Binding): ChildCandidate[] {
  const out: ChildCandidate[] = [];
  const taken = new Set((binding.childTables ?? []).map((c) => c.attribute));
  const baseStores = new Set(binding.baseForeignKey ? (baseBinding?.childTables ?? []).map((c) => c.attribute) : []);
  for (const id of hierarchyOf(entityId, docs).reverse()) {
    const level = docs.get(id);
    for (const a of (level?.attributes as AttrJson[] | undefined) ?? []) {
      if (!a?.id || taken.has(a.id) || baseStores.has(a.id) || (a.derived && a.derived.stored !== true)) continue;
      if (a.collection === true || voOf(a, docs))
        out.push({ id: a.id, name: a.name, isList: a.collection === true, from: id === entityId ? undefined : String(level?.name ?? "") });
    }
  }
  return out;
}

/** The attribute a child table entry stores, from the entity's hierarchy. */
export function childAttribute(entityId: string, attributeId: string, docs: ReadonlyMap<string, Json>): (AttrJson & { valueObject?: Json }) | undefined {
  for (const id of hierarchyOf(entityId, docs)) {
    const a = ((docs.get(id)?.attributes as AttrJson[] | undefined) ?? []).find((x) => x.id === attributeId);
    if (a) return { ...a, valueObject: voOf(a, docs) };
  }
  return undefined;
}

/**
 * What a field of the entry can map: a value object's members, to any depth, or for a list of single values (text, numbers, an enum, a
 * reference type) the attribute itself, one value column.
 */
export function itemAttributes(attribute: (AttrJson & { valueObject?: Json }) | undefined, docs: ReadonlyMap<string, Json>): StorageAttribute[] {
  if (!attribute) return [];
  if (attribute.valueObject) {
    const out: StorageAttribute[] = [];
    valueObjectMembers(
      docs,
      attribute.valueObject,
      attribute.id,
      attribute.name,
      `${attribute.name} (${String(attribute.valueObject.name ?? "")})`,
      { required: false, readOnly: false, expected: true },
      out,
      0,
    );
    return out;
  }
  const type = attribute.type;
  const typeName = typeof type === "string" ? type : type ? String(docs.get(type.ref)?.name ?? "") || null : null;
  return [{ ref: attribute.id, name: attribute.name, origin: "own", type: typeName, isKey: false, required: false, readOnly: false, expected: true }];
}

/**
 * The foreign keys of the child table that can be its owner key: those referencing the owning table on its primary key or a unique key.
 * Without the owning table's document, those that reference the table at all.
 */
export function ownerKeyChoices(child: TableDoc | undefined, ownerTable: string | undefined, owner: TableDoc | undefined): TableDocFk[] {
  if (!child || !ownerTable) return [];
  return (child.foreignKeys ?? []).filter((k) => {
    if (k.referencesTable !== ownerTable) return false;
    if (!owner) return true;
    const referenced = k.referencesColumns?.length ? k.referencesColumns : (owner.primaryKey?.columns ?? []);
    return isPrimaryOrUnique(owner, columnIds(owner, referenced));
  });
}

/** The ids of the columns of the entry's owner foreign key in the child table. */
export function ownerColumnsOf(entry: ChildTable, child: TableDoc | undefined): Set<string> {
  const fk = child?.foreignKeys?.find((k) => k.id === entry.ownerForeignKey);
  return new Set(fk ? columnIds(child, fk.columns) : []);
}

/** The child table's columns with what accounts for each: owner key, item position, field, constant, listed, then the column's own. */
export function childColumnRows(
  entry: ChildTable,
  items: readonly StorageAttribute[],
  columns: readonly SourceColumn[],
  child: TableDoc | undefined,
): ColumnRow[] {
  return columnRows(entry, items, columns, undefined, {
    ownerColumns: ownerColumnsOf(entry, child),
    ...(entry.orderColumn ? { orderColumn: entry.orderColumn } : {}),
  });
}

/** A column that looks like an item position: an integer column named position, order, sort, index or sequence. */
const positionName = (name: string) => /^(position|pos|order|ordinal|sort|sortorder|index|idx|seq|sequence|rank|line|lineno|linenumber)$/.test(loose(name));

/**
 * The order column a list can start with: the integer column named like a position that no field, owner key or constant uses (the pick
 * the conventions make). Undefined when none is.
 */
export function guessOrderColumn(entry: ChildTable, columns: readonly SourceColumn[], owner: ReadonlySet<string>): SourceColumn | undefined {
  const used = new Set((entry.fields ?? []).map((f) => findColumn(columns, f.column)?.key));
  return columns.find((c) => isIntegerColumn(c.type) && positionName(c.name) && !owner.has(c.key) && !used.has(c.key));
}

// ------------------------------------------------------------------ problems the editor can see

/**
 * What the engine's rules report on one child table entry, as far as the editor can tell: the table or owner key that does not fit
 * (MQ4138), the owner's referenced column no field maps (MQ4139), fields naming no member, no column or a column twice (MQ4140), the order
 * column (MQ4141), columns nothing accounts for (MQ4047) and members no field maps (MQ4058). Pointers are relative to the entry.
 * `child` is undefined while the table is not read (or is not a table): then only a missing table is said.
 */
export function childProblems(
  entry: ChildTable,
  attribute: (AttrJson & { valueObject?: Json }) | undefined,
  items: readonly StorageAttribute[],
  columns: readonly SourceColumn[],
  child: TableDoc | undefined,
  owner: TableDoc | undefined,
  binding: Binding,
  tableKnown: boolean,
  names: { entity: string; table: string },
  baseOwnsKey = false,
): MapProblem[] {
  const out: MapProblem[] = [];
  const say = (rule: string, severity: "error" | "warning", message: string, pointer: string) => out.push({ rule, severity, message, pointer });
  if (!attribute) {
    say("MQ4137", "error", `The attribute '${entry.attribute}' is not an attribute of the entity.`, "/attribute");
    return out;
  }
  const isList = attribute.collection === true;
  if (!isList && !attribute.valueObject) say("MQ4137", "error", `${attribute.name} is neither a list nor a value object.`, "/attribute");
  if (!tableKnown || !child) {
    if (!entry.table || (tableKnown && !child)) say("MQ4138", "error", `The table '${entry.table || "(none)"}' is not a table of the database.`, "/table");
    return out;
  }
  const fk = child.foreignKeys?.find((k) => k.id === entry.ownerForeignKey);
  if (!fk)
    say(
      "MQ4138",
      "error",
      entry.ownerForeignKey
        ? `'${entry.ownerForeignKey}' is not a foreign key of ${names.table}.`
        : `Name the foreign key of ${names.table} that ties a row to its ${names.entity}.`,
      "/ownerForeignKey",
    );
  else if (owner && fk.referencesTable !== owner.id)
    say("MQ4138", "error", `${fk.name ?? "The owner foreign key"} does not reference ${owner.name ?? "the owning table"}.`, "/ownerForeignKey");
  else if (owner) {
    const referenced = fk.referencesColumns?.length ? fk.referencesColumns : (owner.primaryKey?.columns ?? []);
    if (!isPrimaryOrUnique(owner, columnIds(owner, referenced)))
      say(
        "MQ4138",
        "error",
        `${fk.name ?? "The owner foreign key"} references neither the primary key nor a unique key of ${owner.name ?? "the owning table"}.`,
        "/ownerForeignKey",
      );
    else if (!baseOwnsKey) {
      const mapped = new Set((binding.fields ?? []).map((f) => findColumn(tableDocColumnsOf(owner), f.column)?.key));
      const missing = columnIds(owner, referenced).filter((c) => !mapped.has(c));
      if (missing.length)
        say(
          "MQ4139",
          "error",
          `No field of the binding maps ${missing.map((c) => owner.columns?.find((x) => x.id === c)?.name ?? c).join(", ")}, which the owner foreign key references.`,
          "/ownerForeignKey",
        );
    }
  }
  const refs = new Set(items.map((a) => a.ref));
  const owned = ownerColumnsOf(entry, child);
  const used = new Map<string, number>();
  const seen = new Set<string>();
  (entry.fields ?? []).forEach((f, i) => {
    if (!refs.has(f.attribute)) say("MQ4140", "error", `The field '${f.attribute}' names no member of ${attribute.name}.`, `/fields/${i}/attribute`);
    else if (seen.has(f.attribute)) say("MQ4140", "error", `${f.attribute} is mapped twice.`, `/fields/${i}/attribute`);
    else if (wholeAbove(f.attribute, entry.fields ?? []))
      say("MQ4140", "error", `${f.attribute} is mapped as well as the value object that holds it whole.`, `/fields/${i}/attribute`);
    seen.add(f.attribute);
    const column = findColumn(columns, f.column);
    if (!column) say("MQ4140", "error", `${names.table} has no column '${f.column}'.`, `/fields/${i}/column`);
    else if (owned.has(column.key))
      say("MQ4139", "error", `The column ${column.name} is a column of the owner foreign key, so no field maps it.`, `/fields/${i}/column`);
    else if (used.has(column.key)) say("MQ4140", "error", `The column ${column.name} is mapped twice.`, `/fields/${i}/column`);
    else used.set(column.key, i);
  });
  (entry.constants ?? []).forEach((c, i) => {
    if (!findColumn(columns, c.column)) say("MQ4140", "error", `${names.table} has no column '${c.column}' for the constant.`, `/constants/${i}/column`);
  });
  if (entry.orderColumn) {
    const column = findColumn(columns, entry.orderColumn);
    if (!isList) say("MQ4141", "error", "A single value object has at most one row: it has no order column.", "/orderColumn");
    else if (!column) say("MQ4141", "error", `${names.table} has no column '${entry.orderColumn}' for the order.`, "/orderColumn");
    else if (!isIntegerColumn(column.type)) say("MQ4141", "error", `The order column ${column.name} is not an integer column.`, "/orderColumn");
    else if (owned.has(column.key)) say("MQ4141", "error", `The order column ${column.name} is part of the owner foreign key.`, "/orderColumn");
  }
  const unaccounted = childColumnRows(entry, items, columns, child).filter((r) => r.status === "unaccounted");
  if (unaccounted.length)
    say(
      "MQ4047",
      "warning",
      `Nothing accounts for ${unaccounted.map((r) => r.column.name).join(", ")} of ${names.table}: map a field, set a constant or ignore it.`,
      "",
    );
  const mappedRefs = new Set((entry.fields ?? []).map((f) => f.attribute));
  const unmapped = items.filter((a) => a.expected && !a.json && !mappedRefs.has(a.ref) && !wholeAbove(a.ref, entry.fields ?? []));
  if (unmapped.length)
    say(
      "MQ4058",
      "warning",
      `No field of ${names.table} maps ${unmapped.map((a) => a.name).join(", ")}: the entity reads ${unmapped.length === 1 ? "it" : "them"} from nowhere.`,
      "/fields",
    );
  return out;
}

const tableDocColumnsOf = (t: TableDoc) => (t.columns ?? []).map((c) => ({ key: c.id, name: c.name ?? c.id }));

// ------------------------------------------------------------------ edits (each one gesture: one save, one undo step)

/** Adds an entry for the attribute (its table comes next); a field of the binding that mapped the attribute or its members goes. */
export function addChildTable(b: Binding, id: string, attribute: string): ChildTable {
  const entry: ChildTable = { id, attribute, table: "", ownerForeignKey: "" };
  b.fields = (b.fields ?? []).filter((f) => f.attribute !== attribute && !f.attribute.startsWith(`${attribute}.`));
  if (!b.fields.length) delete b.fields;
  b.childTables = [...(b.childTables ?? []), entry];
  return entry;
}

/** Removes an entry. */
export function removeChildTable(b: Binding, id: string): Binding {
  b.childTables = (b.childTables ?? []).filter((c) => c.id !== id);
  if (!b.childTables.length) delete b.childTables;
  return b;
}

/**
 * Points an entry at a table: what named the old table's columns goes, the owner key is the one that fits when exactly one does, the
 * members are mapped by name, and a list takes the position column the names point to.
 */
export function pickChildTable(
  entry: ChildTable,
  child: TableDoc,
  columns: readonly SourceColumn[],
  fits: readonly TableDocFk[],
  items: readonly StorageAttribute[],
  isList: boolean,
  attributeName = "",
): ChildTable {
  entry.table = child.id;
  entry.ownerForeignKey = fits.length === 1 ? fits[0].id : "";
  delete entry.fields;
  delete entry.orderColumn;
  delete entry.constants;
  delete entry.columns;
  const owner = new Set(fits.length === 1 ? columnIds(child, fits[0].columns) : []);
  const free = columns.filter((c) => !owner.has(c.key));
  applyMatches(entry, matchChildByName(items, attributeName, free), columns);
  if (isList) {
    const order = guessOrderColumn(entry, free, owner);
    if (order) entry.orderColumn = order.key;
  }
  return entry;
}

/**
 * The columns the item's members are named after: a member `shipToGeoLatitude` of the attribute `shipTo` reads `geoLatitude` (a column
 * of the child table), a list of single values takes a column named like the attribute, its singular, or `value`.
 */
export function matchChildByName(items: readonly StorageAttribute[], attributeName: string, columns: readonly SourceColumn[]): NameMatch[] {
  const taken = new Set<string>();
  const out: NameMatch[] = [];
  for (const attribute of items) {
    if (attribute.json) continue;
    const member = !!attributeName && attribute.name.startsWith(attributeName) && attribute.name.length > attributeName.length;
    const rest = member ? attribute.name.slice(attributeName.length) : attribute.name;
    const names = member ? [loose(rest)] : [loose(rest), loose(rest.replace(/ies$/, "y").replace(/s$/, "")), "value"];
    const column = columns.find((c) => !taken.has(c.key) && names.includes(loose(c.name)));
    if (!column) continue;
    taken.add(column.key);
    out.push({ attribute, column });
  }
  return out;
}

/** Names the owner key; its columns stop being fields, constants or listed columns. */
export function setOwnerKey(entry: ChildTable, fk: TableDocFk | null, child: TableDoc | undefined, columns: readonly SourceColumn[]): ChildTable {
  entry.ownerForeignKey = fk?.id ?? "";
  const owned = new Set(fk ? columnIds(child, fk.columns) : []);
  const keep = <T extends { column: string }>(list: T[] | undefined) => (list ?? []).filter((x) => !owned.has(findColumn(columns, x.column)?.key ?? ""));
  for (const k of ["fields", "constants", "columns"] as const) {
    const kept = keep(entry[k] as { column: string }[] | undefined);
    if (kept.length) (entry as unknown as Record<string, unknown>)[k] = kept;
    else delete entry[k];
  }
  if (entry.orderColumn && owned.has(findColumn(columns, entry.orderColumn)?.key ?? "")) delete entry.orderColumn;
  return entry;
}

/** Names the order column (a column that a field, constant or listing used is freed first), or (null) removes it. */
export function setOrderColumn(entry: ChildTable, column: SourceColumn | null, columns: readonly SourceColumn[]): ChildTable {
  if (!column) {
    delete entry.orderColumn;
    return entry;
  }
  entry.orderColumn = column.key;
  entry.fields = (entry.fields ?? []).filter((f) => findColumn(columns, f.column) !== column);
  entry.constants = (entry.constants ?? []).filter((c) => findColumn(columns, c.column) !== column);
  entry.columns = (entry.columns ?? []).filter((c) => findColumn(columns, c.column) !== column);
  for (const k of ["fields", "constants", "columns"] as const) if (!entry[k]?.length) delete entry[k];
  return entry;
}

export { setConstants as setChildConstants, setField as setChildField };
