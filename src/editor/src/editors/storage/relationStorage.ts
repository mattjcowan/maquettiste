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
  columns?: { id: string; name?: string; type?: unknown }[];
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
  const ends = ((json.ends as { end: string; foreignKey?: string }[] | undefined) ?? []).filter((e) => e.end !== end);
  if (key) ends.push({ end, foreignKey: key });
  if (ends.length) json.ends = ends;
  else delete json.ends;
}

// ------------------------------------------------------------------ several types: Held in and Linked by (several-types-design.md 19.10)
//
// A relation with an end of several types is stored in the team's tables through the entries of its mapping's `ends` list. The Storage tab
// offers two choices, Held in and Linked by, and this section is the table that maps them to entries and back, with the filters the
// pickers use. Below, "A" is a one-type end and "B" an end with several types.

export type HeldIn = "other-table" | "type-tables" | "link-table" | "table-per-type" | "none";
export type LinkedBy = "key" | "column-per-type" | "type-and-id" | "id" | "shared-key";

export const HELD_IN_LABELS: Record<HeldIn, string> = {
  "other-table": "The other end's table",
  "type-tables": "Each type's table",
  "link-table": "A link table",
  "table-per-type": "A table per type",
  none: "Not in this database",
};

export const LINKED_BY_LABELS: Record<LinkedBy, string> = {
  key: "A key",
  "column-per-type": "A column per type",
  "type-and-id": "A type and an id",
  id: "An id unique across types",
  "shared-key": "A key to the shared base table",
};

/** An end as the choices need it: whether it has several types or is any entity, its cardinality, and the entities it lists. */
export interface StorageEnd {
  id: string;
  several: boolean;
  any: boolean;
  toOne: boolean;
  /** The entities the end lists, in order (a `types` item names one of them). */
  entities: string[];
}

export interface StorageEndSource {
  id: string;
  entity?: string | undefined;
  max?: number | "*" | undefined;
  anyEntity?: boolean | undefined;
  types?: readonly { entity?: string | undefined; stereotype?: string | undefined }[] | undefined;
  /** Every covered entity a per-type picker may name (`coveredTargets`); without it, the listed entities. */
  covered?: readonly string[] | undefined;
}

/**
 * Every entity a relation end covers that a per-type picker may name (several-types-design.md 19.10), in the order the engine's targets
 * follow (decision 39): each listed entity, then the carriers of the end's stereotype entries, each followed by the entities derived from it
 * (by name, then id). An end that is any entity covers every entity.
 */
export function coveredTargets(
  listed: readonly string[],
  carriers: readonly string[],
  any: boolean,
  entities: readonly { id: string; name: string; base?: string | undefined }[],
): string[] {
  const byId = new Map(entities.map((e) => [e.id, e]));
  const chainHas = (id: string, root: string) => {
    for (let at = byId.get(id)?.base, guard = 0; at && guard < 64; at = byId.get(at)?.base, guard++) if (at === root) return true;
    return false;
  };
  const byName = (a: { id: string; name: string }, b: { id: string; name: string }) =>
    a.name < b.name ? -1 : a.name > b.name ? 1 : a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  const roots = any
    ? entities
        .filter((e) => !e.base || !byId.has(e.base))
        .sort(byName)
        .map((e) => e.id)
    : [...listed, ...carriers];
  const out: string[] = [];
  const add = (id: string) => void (!out.includes(id) && out.push(id));
  for (const root of roots) {
    add(root);
    entities
      .filter((e) => chainHas(e.id, root))
      .sort(byName)
      .forEach((e) => add(e.id));
  }
  return out;
}

/** The end of a relation document as the choices read it. A `types` list of one entity is a one-type end. */
export function storageEndOf(end: StorageEndSource): StorageEnd {
  const listed = end.entity ? [end.entity] : end.covered ? [...end.covered] : (end.types ?? []).flatMap((t) => t.entity ?? []);
  const several = !end.entity && (end.anyEntity === true || (end.types?.length ?? 0) > 1 || (end.types ?? []).some((t) => t.stereotype !== undefined));
  return { id: end.id, several, any: end.anyEntity === true && !end.entity, toOne: end.max === 1, entities: listed };
}

export interface EndEntry {
  end: string;
  foreignKey?: string | undefined;
  foreignKeys?: string[] | undefined;
  types?: { entity: string; foreignKey?: string | undefined; referencesColumn?: string | undefined }[] | undefined;
  idColumn?: string | undefined;
  typeColumn?: string | undefined;
  referencesColumn?: string | undefined;
}

export interface MappingConstantDoc {
  column: string;
  value?: string | number | boolean | null | undefined;
}

export interface StorageMapping {
  ignore?: boolean | null | undefined;
  shape?: string | null | undefined;
  foreignKey?: string | undefined;
  foreignKeyEnd?: string | null | undefined;
  promotedName?: string | null | undefined;
  junctionTable?: string | undefined;
  ends?: EndEntry[] | undefined;
  constants?: MappingConstantDoc[] | undefined;
  attributes?: { attribute: string; column?: string | undefined; storage?: unknown; prefix?: unknown }[] | undefined;
}

/** The ends of a two-ended relation as the choices read them, with the one-type end A and the end B when exactly one end has several types. */
export function pairOfEnds(ends: readonly StorageEnd[]): { a: StorageEnd | null; b: StorageEnd | null; both: boolean; oneType: boolean } | null {
  if (ends.length !== 2) return null;
  const several = ends.filter((e) => e.several);
  if (several.length === 2) return { a: null, b: null, both: true, oneType: false };
  if (several.length === 0) return { a: null, b: null, both: false, oneType: true };
  const b = several[0];
  return { a: ends.find((e) => e !== b) ?? null, b, both: false, oneType: false };
}

/** What Held in offers (the table of 19.10). A many-to-many offers only A link table, A table per type and Not in this database. */
export function offeredHeldIn(ends: readonly StorageEnd[]): HeldIn[] {
  const pair = pairOfEnds(ends);
  if (!pair || pair.oneType) return [];
  if (pair.both) return ["link-table", "none"];
  const { a, b } = pair;
  const out: HeldIn[] = [];
  if (b?.toOne) out.push("other-table");
  if (a?.toOne && !b?.any) out.push("type-tables");
  out.push("link-table");
  if (!b?.any) out.push("table-per-type");
  out.push("none");
  return out;
}

/** What Linked by offers for an end under a Held in choice. Empty where the choice is fixed (each type's table, a table per type). */
export function offeredLinkedBy(heldIn: HeldIn | null, end: StorageEnd): LinkedBy[] {
  if (heldIn !== "other-table" && heldIn !== "link-table") return [];
  if (heldIn === "other-table" && !end.several) return [];
  if (!end.several) return ["key"];
  // An any-entity end never offers a column per type: every new entity would need a column (decision 43).
  return end.any ? ["shared-key", "type-and-id", "id"] : ["shared-key", "column-per-type", "type-and-id", "id"];
}

/** The ends that need a Linked by choice under a Held in choice: B for the other end's table, each end for a link table. */
export function linkedByEnds(heldIn: HeldIn | null, ends: readonly StorageEnd[]): StorageEnd[] {
  if (heldIn === "other-table") return ends.filter((e) => e.several);
  if (heldIn === "link-table") return [...ends];
  return [];
}

/** What an entry says about how it points at its end: a Linked by choice, "keys" (foreignKeys), "" (no way yet) or "mixed". */
export function entryKind(entry: EndEntry, end: StorageEnd): LinkedBy | "keys" | "" | "mixed" {
  const kinds: (LinkedBy | "keys")[] = [];
  if (entry.foreignKey) kinds.push(end.several ? "shared-key" : "key");
  if (entry.foreignKeys?.length) kinds.push("keys");
  if (entry.types?.some((t) => t.foreignKey)) kinds.push("column-per-type");
  if (entry.idColumn) kinds.push(entry.typeColumn ? "type-and-id" : "id");
  if (kinds.length > 1) return "mixed";
  return kinds[0] ?? "";
}

export interface StorageChoice {
  /** The Held in choice the mapping matches; "as-written" when it matches no pair; null when it states nothing. */
  heldIn: HeldIn | "as-written" | null;
  /** The Linked by choice per end id; "" while the entry names no way. */
  linkedBy: Record<string, LinkedBy | "">;
}

/** Keys of a mapping that the choices never write: their presence means the mapping is shown as written. */
const FOREIGN_KEYS_OF_OTHER_SHAPES = ["foreignKey", "foreignKeyEnd", "promotedName"] as const;

/** Whether a half-written entry fits a Linked by draft: every way it states belongs to that choice (nothing written fits any). */
export function entryAgrees(kind: LinkedBy, entry: EndEntry | undefined): boolean {
  if (!entry) return true;
  const hasKey = !!entry.foreignKey;
  const keysList = !!entry.foreignKeys?.length;
  const typeKeys = !!entry.types?.some((t) => t.foreignKey);
  const refers = !!entry.types?.some((t) => t.referencesColumn) || !!entry.referencesColumn;
  const pointer = !!entry.idColumn || !!entry.typeColumn;
  if (keysList) return false;
  switch (kind) {
    case "key":
    case "shared-key":
      return !typeKeys && !refers && !pointer;
    case "column-per-type":
      return !hasKey && !refers && !pointer;
    case "type-and-id":
      return !hasKey && !typeKeys;
    case "id":
      return !hasKey && !typeKeys && !entry.typeColumn;
  }
}

/** Whether the mapping, as far as it is written, fits a Held in draft: what is there belongs to that choice, so the draft is not a guess. */
export function draftAgrees(draft: HeldIn, mapping: StorageMapping | null | undefined, ends: readonly StorageEnd[]): boolean {
  if (!mapping) return true;
  if (draft === "none") return mapping.ignore === true;
  if (mapping.ignore === true) return false;
  if (FOREIGN_KEYS_OF_OTHER_SHAPES.some((k) => mapping[k]) || mapping.shape === "promoted") return false;
  const entries = mapping.ends ?? [];
  if (!entries.every((e) => ends.some((x) => x.id === e.end))) return false;
  if (draft === "link-table") return true;
  if (mapping.junctionTable) return false;
  const pair = pairOfEnds(ends);
  if (!pair?.a || !pair.b) return false;
  const [a, b] = [pair.a.id, pair.b.id];
  const onlyKeys = (e: EndEntry) => !e.foreignKey && !e.types && !e.idColumn && !e.typeColumn && !e.referencesColumn;
  const onlyTypeKeys = (e: EndEntry) =>
    !e.foreignKey && !e.foreignKeys && !e.idColumn && !e.typeColumn && !e.referencesColumn && !e.types?.some((t) => !t.foreignKey);
  if (draft === "other-table") return entries.every((e) => e.end === b);
  if (draft === "type-tables") return entries.every((e) => e.end === a && onlyKeys(e));
  return entries.every((e) => (e.end === a && onlyKeys(e)) || (e.end === b && onlyTypeKeys(e)));
}

/**
 * Reads a mapping back into a pair of choices, the table of 19.10 read the other way. A mapping that matches no pair is "As written".
 * `draft` is the choice the person made on the card and may not have written yet: while the mapping is only half-written, a draft that
 * agrees with it wins. The choice is never one that Held in doesn't offer.
 */
export function readChoice(mapping: StorageMapping | null | undefined, ends: readonly StorageEnd[], draft?: HeldIn | null): StorageChoice {
  const offered = offeredHeldIn(ends);
  if (draft && draft !== "none" && offered.includes(draft) && draftAgrees(draft, mapping, ends)) {
    const out: Record<string, LinkedBy | ""> = {};
    const byEnd = new Map((mapping?.ends ?? []).map((e) => [e.end, e]));
    for (const end of linkedByEnds(draft, ends)) {
      const entry = byEnd.get(end.id);
      const kind = entry ? entryKind(entry, end) : "";
      out[end.id] = kind !== "" && kind !== "mixed" && kind !== "keys" && offeredLinkedBy(draft, end).includes(kind) ? kind : "";
    }
    return { heldIn: draft, linkedBy: out };
  }
  const read = readFileChoice(mapping, ends);
  if (read.heldIn === null || read.heldIn === "as-written" || read.heldIn === "none" || offered.includes(read.heldIn)) return read;
  // A half-written table per type on a many-to-many reads as the one thing that fits; never a choice that isn't offered.
  if (offered.includes("table-per-type") && draftAgrees("table-per-type", mapping, ends)) return { heldIn: "table-per-type", linkedBy: {} };
  return { heldIn: "as-written", linkedBy: {} };
}

function readFileChoice(mapping: StorageMapping | null | undefined, ends: readonly StorageEnd[]): StorageChoice {
  const none: StorageChoice = { heldIn: null, linkedBy: {} };
  if (!mapping) return none;
  if (mapping.ignore === true) return { heldIn: "none", linkedBy: {} };
  const entries = mapping.ends ?? [];
  const junction = mapping.junctionTable;
  const asWritten: StorageChoice = { heldIn: "as-written", linkedBy: {} };
  if (FOREIGN_KEYS_OF_OTHER_SHAPES.some((k) => mapping[k]) || mapping.shape === "promoted") return asWritten;
  if (!junction && entries.length === 0) return none;
  const pair = pairOfEnds(ends);
  if (!pair || pair.oneType) return asWritten;
  const byEnd = new Map<string, EndEntry>();
  for (const e of entries) {
    if (byEnd.has(e.end) || !ends.some((x) => x.id === e.end)) return asWritten;
    byEnd.set(e.end, e);
  }
  const kindOf = (end: StorageEnd) => {
    const entry = byEnd.get(end.id);
    return entry ? entryKind(entry, end) : "";
  };
  const linked = (heldIn: HeldIn): StorageChoice => {
    const out: Record<string, LinkedBy | ""> = {};
    for (const end of linkedByEnds(heldIn, ends)) {
      const kind = kindOf(end);
      if (kind === "mixed" || kind === "keys") return asWritten;
      if (kind !== "" && !offeredLinkedBy(heldIn, end).includes(kind)) return asWritten;
      out[end.id] = kind;
    }
    return { heldIn, linkedBy: out };
  };
  if (junction) return linked("link-table");
  const { a, b } = pair;
  if (!a || !b) return asWritten;
  const hasA = byEnd.has(a.id);
  const hasB = byEnd.has(b.id);
  const keysOrNone = (end: StorageEnd) => ["keys", ""].includes(kindOf(end));
  if (hasB && !hasA) return linked("other-table");
  if (hasA && !hasB) return keysOrNone(a) ? { heldIn: "type-tables", linkedBy: {} } : asWritten;
  if (hasA && hasB && keysOrNone(a) && ["column-per-type", ""].includes(kindOf(b))) return { heldIn: "table-per-type", linkedBy: {} };
  return asWritten;
}

// ------------------------------------------------------------------ writing a choice

const ENTRY_WAYS = ["foreignKey", "foreignKeys", "types", "idColumn", "typeColumn", "referencesColumn"] as const;

/** Whether an entry is only its `end`: nothing yet says how it points. */
const isBare = (e: EndEntry) => ENTRY_WAYS.every((k) => e[k] === undefined);

function entriesOf(json: StorageMapping): EndEntry[] {
  return (json.ends ??= []);
}

function entryOf(json: StorageMapping, end: string): EndEntry {
  const list = entriesOf(json);
  let entry = list.find((e) => e.end === end);
  if (!entry) {
    entry = { end };
    list.push(entry);
  }
  return entry;
}

function tidy(json: StorageMapping): void {
  if (json.ends && json.ends.length === 0) delete json.ends;
  if (json.constants && json.constants.length === 0) delete json.constants;
}

function clearAttributeColumns(json: StorageMapping): void {
  if (!json.attributes) return;
  for (const a of json.attributes) delete a.column;
  json.attributes = json.attributes.filter((a) => Object.keys(a).some((k) => k !== "attribute"));
  if (json.attributes.length === 0) delete json.attributes;
}

/**
 * Writes a Held in choice. The entries of 19.10's table are written as the pickers name keys and columns, never bare (an entry with no
 * way is MQ4009 and a save that introduces it is refused), so a choice that names nothing yet is the tab's own state until then. Leaving
 * A link table drops its table, its constants and its attribute columns. "Not in this database" sets `ignore`; every other choice
 * clears it, and the keys of the other shapes (a foreign key, `shape`, `foreignKeyEnd`, the entries) with it.
 */
export function setHeldIn(json: StorageMapping, heldIn: HeldIn, ends: readonly StorageEnd[]): void {
  void ends;
  const keepLink = heldIn === "link-table";
  delete json.ignore;
  delete json.foreignKey;
  delete json.foreignKeyEnd;
  // Without a link table yet, the entries of the old choice would only read back as that choice.
  if (!keepLink || !json.junctionTable) delete json.ends;
  if (!keepLink) {
    delete json.junctionTable;
    delete json.shape;
  }
  if (!keepLink || !json.junctionTable) {
    delete json.constants;
    clearAttributeColumns(json);
  }
  if (heldIn === "none") json.ignore = true;
  tidy(json);
}

/** Names the link table (null: none). Its end entries, constants and attribute columns go when the table changes. */
export function setLinkTable(json: StorageMapping, table: string | null): void {
  if (json.junctionTable !== table) {
    delete json.ends;
    delete json.constants;
    clearAttributeColumns(json);
  }
  delete json.ignore;
  if (table) {
    json.junctionTable = table;
    json.shape = "junction";
  } else {
    delete json.junctionTable;
    delete json.shape;
  }
  tidy(json);
}

/** Changes how one end is pointed at: the entry keeps its `end` and loses every way, since a way of another kind says something else. */
export function clearEntryWays(json: StorageMapping, end: string): void {
  const entry = entryOf(json, end);
  for (const k of ENTRY_WAYS) delete entry[k];
  dropBareEntries(json);
}

/** Names the one foreign key of an end entry (A key, or A key to the shared base table); null clears it. */
export function setEntryKey(json: StorageMapping, end: string, key: string | null): void {
  const entry = entryOf(json, end);
  if (key) entry.foreignKey = key;
  else delete entry.foreignKey;
  dropBareEntries(json);
}

/** Names the foreign keys of an entry, one per table (Each type's table, A table per type). An empty list leaves the entry bare. */
export function setEntryKeys(json: StorageMapping, end: string, keys: readonly string[]): void {
  const entry = entryOf(json, end);
  if (keys.length) entry.foreignKeys = [...new Set(keys)];
  else delete entry.foreignKeys;
  dropBareEntries(json);
}

/** Names the key of one type under A column per type; null removes the type's item. Items follow the end's type order. */
export function setTypeKey(json: StorageMapping, end: string, order: readonly string[], entity: string, key: string | null): void {
  const entry = entryOf(json, end);
  const items = (entry.types ?? []).filter((t) => t.entity !== entity);
  if (key) items.push({ entity, foreignKey: key });
  const rank = (e: string) => (order.includes(e) ? order.indexOf(e) : order.length);
  items.sort((x, y) => rank(x.entity) - rank(y.entity));
  if (items.length) entry.types = items;
  else delete entry.types;
  dropBareEntries(json);
}

/** Names the type and id columns (A type and an id; An id unique across types has no type column). `undefined` leaves a column as it is, null clears it. */
export function setEntryColumns(json: StorageMapping, end: string, columns: { idColumn?: string | null; typeColumn?: string | null }): void {
  const entry = entryOf(json, end);
  for (const k of ["idColumn", "typeColumn"] as const) {
    const v = columns[k];
    if (v === undefined) continue;
    if (v) entry[k] = v;
    else delete entry[k];
  }
  dropBareEntries(json);
}

/** Names what an id refers to for one type (Refers to): a unique column of the type's table. Null removes the item. */
export function setRefersTo(json: StorageMapping, end: string, order: readonly string[], entity: string, column: string | null): void {
  const entry = entryOf(json, end);
  const items = (entry.types ?? []).filter((t) => t.entity !== entity);
  if (column) items.push({ entity, referencesColumn: column });
  const rank = (e: string) => (order.includes(e) ? order.indexOf(e) : order.length);
  items.sort((x, y) => rank(x.entity) - rank(y.entity));
  if (items.length) entry.types = items;
  else delete entry.types;
  dropBareEntries(json);
}

/** Names the constants (Rows of this relation) of a mapping with a link table. */
export function setMappingConstants(json: StorageMapping, constants: readonly MappingConstantDoc[]): void {
  const next = constants
    .filter((c) => c.column)
    .map((c) => (c.value === undefined || c.value === null ? { column: c.column } : { column: c.column, value: c.value }));
  if (next.length) json.constants = next;
  else delete json.constants;
}

/** Names the column of a relation attribute (one table holds every link); null clears it. An attribute entry left with no column is dropped. */
export function setAttributeColumn(json: StorageMapping, attribute: string, column: string | null): void {
  const list = json.attributes ?? [];
  const at = list.find((a) => a.attribute === attribute);
  if (column) {
    if (at) at.column = column;
    else list.push({ attribute, column });
    json.attributes = list;
  } else if (at) {
    delete at.column;
    json.attributes = list.filter((a) => Object.keys(a).some((k) => k !== "attribute"));
    if (json.attributes.length === 0) delete json.attributes;
  }
}

/**
 * Whether a link-table mapping is complete enough for the engine to take: it names the link table, and every end has an entry that names a
 * way (a link table with no entry for an end is MQ4011, an entry with no way MQ4009). Until then the card holds the edit and writes nothing.
 */
export function linkTableComplete(mapping: StorageMapping, ends: readonly StorageEnd[]): boolean {
  if (!mapping.junctionTable) return false;
  return ends.every((end) => {
    const entry = mapping.ends?.find((e) => e.end === end.id);
    return !!entry && !isBare(entry);
  });
}

/** The keys of a mapping the choices write; a held edit replaces exactly these. */
const STORAGE_KEYS = ["ignore", "shape", "foreignKey", "foreignKeyEnd", "promotedName", "junctionTable", "ends", "constants", "attributes"] as const;

/** Copies what the choices write from one mapping to another (a key the source lacks is removed), so a held edit lands as one change. */
export function copyStorageKeys(target: Record<string, unknown>, source: StorageMapping): void {
  for (const k of STORAGE_KEYS) {
    const v = (source as Record<string, unknown>)[k];
    if (v === undefined) delete target[k];
    else target[k] = structuredClone(v);
  }
}

/** Removes every entry that is only its `end`: it says nothing, and an entry with no way is MQ4009. */
export function dropBareEntries(json: StorageMapping): void {
  if (json.ends) json.ends = json.ends.filter((e) => !isBare(e));
  tidy(json);
}

// ------------------------------------------------------------------ the pickers' filters

export interface ColumnDocLike {
  id: string;
  name?: string;
  type?: unknown;
}

export interface TableDocWithKeys extends TableDocLike {
  columns?: ColumnDocLike[];
  primaryKey?: { columns: string[] };
  uniques?: { columns: string[] }[];
  indexes?: { columns: string[]; unique?: boolean; where?: string }[];
}

/** The foreign keys of a holding table that refer to one of the tables: what a key picker offers. */
export function keysReferring(holder: TableDocLike | undefined, targets: ReadonlySet<string>): ForeignKeyDoc[] {
  return (holder?.foreignKeys ?? []).filter((k) => targets.has(k.referencesTable));
}

/** Whether a key lists a column of the table (by id or name): the columns of a key name columns either way. */
const lists = (columns: readonly string[], c: ColumnDocLike) => columns.includes(c.id) || (!!c.name && columns.includes(c.name));

/** The columns of a table that are unique alone: a one-column primary key or unique constraint, a unique index with no filter. */
export function uniqueColumns(table: TableDocWithKeys | undefined): ColumnDocLike[] {
  if (!table) return [];
  const single: string[][] = [];
  if (table.primaryKey?.columns.length === 1) single.push(table.primaryKey.columns);
  for (const u of table.uniques ?? []) if (u.columns.length === 1) single.push(u.columns);
  for (const i of table.indexes ?? []) if (i.unique === true && !i.where && i.columns.length === 1) single.push(i.columns);
  return (table.columns ?? []).filter((c) => single.some((cols) => lists(cols, c)));
}

/** The single column of a table's primary key, null when it has none or several (then Refers to is needed). */
export function primaryKeyColumn(table: TableDocWithKeys | undefined): ColumnDocLike | null {
  const pk = table?.primaryKey?.columns;
  if (!table || pk?.length !== 1) return null;
  return (table.columns ?? []).find((c) => lists(pk, c)) ?? null;
}

const sameType = (a: unknown, b: unknown) => a === undefined || b === undefined || JSON.stringify(a) === JSON.stringify(b);

/**
 * The columns of the holding table an id column may be: those whose type matches the column the id refers to (a column of a type table: its
 * `referencesColumn` or its primary key). Without a known type, every column but the type column.
 */
export function idColumnChoices(
  holder: TableDocWithKeys | undefined,
  referenced: ColumnDocLike | null | undefined,
  typeColumn?: string | null,
): ColumnDocLike[] {
  return (holder?.columns ?? []).filter((c) => c.id !== typeColumn && sameType(c.type, referenced?.type));
}

/** The columns a type column may be: text or integer columns (a code is a string or a number), never the id column. */
export function typeColumnChoices(holder: TableDocWithKeys | undefined, idColumn?: string | null): ColumnDocLike[] {
  return (holder?.columns ?? []).filter(
    (c) => c.id !== idColumn && (c.type === undefined || typeof c.type !== "string" || /^(string|text|int|integer|short|long|byte)/i.test(c.type)),
  );
}

/** The tables a key to a shared base table may lead to: those bound to a base (or the entity itself) of every covered entity. Empty when unknown. */
export function sharedTables(boundByEntity: readonly ReadonlySet<string>[]): Set<string> {
  if (boundByEntity.length === 0) return new Set();
  const [first, ...rest] = boundByEntity;
  return new Set([...first].filter((t) => rest.every((s) => s.has(t))));
}

/**
 * The columns of a link table that point: the id and type columns of the entries and the columns of the foreign keys they name. A constant or an
 * attribute column on one of these is refused (MQ4009).
 */
export function pointerColumnIds(junction: TableDocWithKeys | undefined, mapping: StorageMapping): Set<string> {
  const keys = new Set<string>();
  for (const e of mapping.ends ?? []) {
    if (e.foreignKey) keys.add(e.foreignKey);
    for (const k of e.foreignKeys ?? []) keys.add(k);
    for (const t of e.types ?? []) if (t.foreignKey) keys.add(t.foreignKey);
  }
  const out = new Set<string>();
  for (const e of mapping.ends ?? []) for (const c of [e.idColumn, e.typeColumn]) if (c) out.add(c);
  const idOf = (ref: string) => junction?.columns?.find((c) => c.id === ref || c.name === ref)?.id ?? ref;
  for (const k of junction?.foreignKeys ?? []) if (keys.has(k.id)) for (const c of k.columns) out.add(idOf(c));
  return out;
}

/**
 * Whether a relation attribute can take one column of a link table: not a collection and not typed by a value object (as the engine's
 * `IsCompound`). A reference to anything else (an enum, a reference type) is one column. `kindOf` answers an element's kind.
 */
export function attributeTakesColumn(a: { type?: unknown; collection?: boolean | undefined }, kindOf: (id: string) => string | undefined): boolean {
  if (a.collection === true) return false;
  if (typeof a.type !== "object" || a.type === null) return true;
  const ref = (a.type as { ref?: unknown }).ref;
  return typeof ref === "string" && kindOf(ref) !== "value-object";
}
