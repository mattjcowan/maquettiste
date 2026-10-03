// The field map of an entity binding (erratum E43, engine-design.md 7 "Bindings and materialize"), free of React so the
// Storage tab, the bulk actions and the mock backend share it: the attributes a binding can map (own, inherited, virtual,
// value object members, to-one relation keys), the columns of its source with the status of each (as the engine's
// RBindingColumn), Map by name, and the edits a gesture makes to a binding. Column references follow the engine: a key or
// a physical name, found by key, then by name, then loosely by name.

type Json = Record<string, unknown>;

export interface BindingConstant {
  column: string;
  value?: string | number | boolean | null;
}
export interface BindingField {
  attribute: string;
  column: string;
}
export type ListedStatus = "ignored" | "database" | "computed";
export interface BindingColumnEntry {
  column: string;
  status: ListedStatus;
}
export type BindingWrite = "none" | { table: string };
export type BindingDelete = "key" | "none" | { soft: { column: string; value?: string | number | boolean | null } };
export interface Binding {
  id: string;
  database: string;
  source: string;
  constants?: BindingConstant[];
  fields?: BindingField[];
  columns?: BindingColumnEntry[];
  write?: BindingWrite;
  delete?: BindingDelete;
  description?: string;
  tags?: string[];
  properties?: Record<string, unknown>;
}

// ------------------------------------------------------------------ attributes

export type AttributeOrigin = "own" | "inherited" | "virtual" | "member" | "relation";

/** Something a binding field can fill: what `fields[].attribute` holds is `ref`. */
export interface StorageAttribute {
  /** An attribute id, attributeId.memberId, or the id of the relation end a to-one navigation leads to. */
  ref: string;
  /** The name generation uses: the attribute's, attributeMember for a member, the navigation plus Id for an end. */
  name: string;
  origin: AttributeOrigin;
  /** Where it comes from, for display: a base entity's name, a stereotype key, a value object attribute, a relation. */
  from?: string;
  type: string | null;
  isKey: boolean;
  required: boolean;
  readOnly: boolean;
  /** No field mapping it is worth a warning: a stored attribute, or the key of a many-to-one this entity is the many side of. */
  expected: boolean;
}

interface AttributeJson {
  id: string;
  name: string;
  type?: string | { ref: string };
  required?: boolean;
  readOnly?: boolean;
  immutable?: boolean;
  collection?: boolean;
  derived?: { expression: string; stored?: boolean };
}

const capital = (s: string) => (s ? s[0].toUpperCase() + s.slice(1) : s);
const lowerFirst = (s: string) => (s ? s[0].toLowerCase() + s.slice(1) : s);

/** The entity ids from the entity up its base chain (the entity first), stopping at a loop. */
export function hierarchyOf(entityId: string, docs: ReadonlyMap<string, Json>): string[] {
  const out: string[] = [];
  let at: string | undefined = entityId;
  while (at && !out.includes(at)) {
    out.push(at);
    const base: unknown = docs.get(at)?.base;
    at = typeof base === "string" ? base : undefined;
  }
  return out;
}

/**
 * Every attribute a binding of the entity can map, from the documents at hand (`docs` by id: the entity, its bases, the value
 * objects its attributes use, the stereotypes and the relations; what is missing is left out). The order is the resolver's:
 * root first, each level's own attributes then its stereotypes' (virtual) ones, then the to-one relation keys. A value object
 * attribute stands for its members; a collection attribute has no column and is left out.
 */
export function storageAttributes(entityId: string, docs: ReadonlyMap<string, Json>): StorageAttribute[] {
  const chain = hierarchyOf(entityId, docs).reverse();
  const keyIds = new Set<string>(
    chain.flatMap((id) => {
      const key = docs.get(id)?.key as { attributes?: string[] } | undefined;
      return key?.attributes ?? [];
    }),
  );
  const stereotypes = new Map<string, Json>();
  for (const d of docs.values()) if (d.kind === "stereotype" && typeof d.key === "string") stereotypes.set(d.key, d);
  const seen = new Set<string>();
  const out: StorageAttribute[] = [];
  const add = (a: AttributeJson, origin: AttributeOrigin, from?: string) => {
    if (!a || typeof a.id !== "string" || seen.has(a.id)) return;
    seen.add(a.id);
    if (a.collection) return;
    const stored = !a.derived || a.derived.stored === true;
    const type = a.type;
    const target = typeof type === "object" && type ? docs.get(type.ref) : undefined;
    if (target?.kind === "value-object") {
      for (const m of (target.attributes as AttributeJson[] | undefined) ?? []) {
        if (m.collection) continue;
        out.push({
          ref: `${a.id}.${m.id}`,
          name: `${a.name}${capital(m.name)}`,
          origin: "member",
          from: `${a.name} (${String(target.name ?? "")})`,
          type: typeof m.type === "string" ? m.type : null,
          isKey: false,
          required: a.required === true && m.required === true,
          readOnly: a.readOnly === true || a.immutable === true,
          expected: stored,
        });
      }
      return;
    }
    out.push({
      ref: a.id,
      name: a.name,
      origin,
      from,
      type: typeof type === "string" ? type : type ? String(docs.get(type.ref)?.name ?? "") || null : null,
      isKey: keyIds.has(a.id),
      required: a.required === true,
      readOnly: a.readOnly === true || a.immutable === true,
      expected: stored,
    });
  };
  for (const id of chain) {
    const level = docs.get(id);
    if (!level) continue;
    const inherited = id !== entityId;
    for (const a of (level.attributes as AttributeJson[] | undefined) ?? [])
      add(a, inherited ? "inherited" : "own", inherited ? String(level.name ?? "") : undefined);
    for (const key of (level.stereotypes as string[] | undefined) ?? [])
      for (const a of (stereotypes.get(key)?.attributes as AttributeJson[] | undefined) ?? []) add(a, "virtual", `«${key}»`);
  }
  // To-one navigations: in a two-ended relation, the end opposite this entity's with max 1 is a key the binding can map.
  const relations = [...docs.values()].filter((d) => d.kind === "relation").sort((x, y) => String(x.id).localeCompare(String(y.id)));
  const mine = new Set(chain);
  for (const r of relations) {
    const ends = (r.ends as { id: string; entity: string; role?: string; navigation?: string; max?: 1 | "*" }[] | undefined) ?? [];
    if (ends.length !== 2) continue;
    for (let i = 0; i < 2; i++) {
      const self = ends[i];
      const other = ends[1 - i];
      if (!mine.has(self.entity) || other.max !== 1 || seen.has(other.id)) continue;
      seen.add(other.id);
      const otherName = String(docs.get(other.entity)?.name ?? "");
      const base = other.navigation || other.role || lowerFirst(otherName) || "ref";
      out.push({
        ref: other.id,
        name: `${base}Id`,
        origin: "relation",
        from: `${String(r.name ?? "")} → ${otherName}`,
        type: null,
        isKey: false,
        required: false,
        readOnly: false,
        // The dependent of a many-to-one holds the key; a one-to-one may hold it on either side, so it is not expected.
        expected: self.max !== 1,
      });
    }
  }
  return out;
}

// ------------------------------------------------------------------ source columns

export type SourceKind = "table" | "view" | "query";

export interface SourceColumn {
  /** The column's key (a designed column's id, a projected column's attribute path) or, for a view or query, its name. */
  key: string;
  name: string;
  type: string | null;
  nullable: boolean;
  identity: boolean;
  computed: boolean;
  hasDefault: boolean;
  isPrimaryKey: boolean;
}

export interface BindingSource {
  kind: SourceKind;
  id: string;
  name: string;
  schema: string | null;
  columns: SourceColumn[];
  /** A table that is not a file of its own (projected by the database's conventions). */
  projected?: boolean;
}

interface TableViewLike {
  key: string;
  name: string;
  schema: string | null;
  origin?: string;
  columns: {
    key: string;
    name: string;
    type: string;
    nullable: boolean;
    identity: boolean;
    computed: string | null;
    defaultSql: string | null;
    default: unknown;
    sequenceId?: string | null;
    isPrimaryKey: boolean;
  }[];
}
interface ViewViewLike {
  id: string;
  name: string;
  schema: string | null;
  columns: { name: string; type: string | null; nullable: boolean }[];
}
interface QueryViewLike {
  id: string;
  name: string;
  select: { name: string; type: string | null; nullable: boolean }[];
}
export interface DatabaseViewLike {
  tables: TableViewLike[];
  views: ViewViewLike[];
  queries?: QueryViewLike[];
}

export function tableSource(t: TableViewLike): BindingSource {
  return {
    kind: "table",
    id: t.key,
    name: t.name,
    schema: t.schema,
    projected: t.origin === "synthesized",
    columns: t.columns.map((c) => ({
      key: c.key,
      name: c.name,
      type: c.type,
      nullable: c.nullable,
      identity: c.identity || (!!c.sequenceId && c.isPrimaryKey),
      computed: !!c.computed,
      hasDefault: c.defaultSql !== null || (c.default !== null && c.default !== undefined),
      isPrimaryKey: c.isPrimaryKey,
    })),
  };
}

/** The tables, views and queries of a database view a binding can read, in that order. */
export function sourcesOf(view: DatabaseViewLike | null | undefined): BindingSource[] {
  if (!view) return [];
  const plain = (c: { name: string; type: string | null; nullable: boolean }): SourceColumn => ({
    key: c.name,
    name: c.name,
    type: c.type,
    nullable: c.nullable,
    identity: false,
    computed: false,
    hasDefault: false,
    isPrimaryKey: false,
  });
  return [
    ...view.tables.map(tableSource),
    ...view.views.map((v) => ({ kind: "view" as const, id: v.id, name: v.name, schema: v.schema, columns: v.columns.map(plain) })),
    ...(view.queries ?? []).map((q) => ({ kind: "query" as const, id: q.id, name: q.name, schema: null, columns: q.select.map(plain) })),
  ];
}

/** The source a binding names (a table key, a view id or a query id), or undefined. */
export function sourceOf(view: DatabaseViewLike | null | undefined, ref: string): BindingSource | undefined {
  return sourcesOf(view).find((s) => s.id === ref);
}

/** A column reference resolved: by key, then name, then loosely by name (case and separators ignored). */
export function findColumn<C extends { key: string; name: string }>(columns: readonly C[], ref: string | undefined | null): C | undefined {
  if (!ref) return undefined;
  return (
    columns.find((c) => c.key === ref) ??
    columns.find((c) => c.name === ref) ??
    columns.find((c) => c.name.toLowerCase() === ref.toLowerCase()) ??
    columns.find((c) => loose(c.name) === loose(ref))
  );
}

/** A name with case and separators dropped: customer_id, customerId and CustomerID read the same. */
export function loose(name: string): string {
  return name.toLowerCase().replace(/[^a-z0-9]/g, "");
}

// ------------------------------------------------------------------ statuses

export type ColumnStatus = "field" | "constant" | "ignored" | "database" | "computed" | "identity" | "default" | "soft-delete" | "unaccounted";

export interface ColumnRow {
  column: SourceColumn;
  status: ColumnStatus;
  /** The attribute that maps it (status field). */
  attribute?: StorageAttribute;
  /** The field's attribute reference when no attribute of the entity has it. */
  fieldRef?: string;
  constant?: BindingConstant;
}

export const COLUMN_STATUS_LABELS: Record<ColumnStatus, string> = {
  field: "mapped",
  constant: "constant",
  ignored: "ignored",
  database: "filled by the database",
  computed: "computed",
  identity: "identity",
  default: "has a default",
  "soft-delete": "soft delete",
  unaccounted: "unaccounted",
};

export type AttributeStatus = "mapped" | "missing-column" | "unmapped" | "optional";

export interface AttributeRow {
  attribute: StorageAttribute;
  field?: BindingField;
  column?: SourceColumn;
  status: AttributeStatus;
}

/** Each attribute with the column its field reads: mapped, a field naming a column the source lacks, or no field. */
export function attributeRows(binding: Binding, attributes: readonly StorageAttribute[], columns: readonly SourceColumn[]): AttributeRow[] {
  return attributes.map((attribute) => {
    const field = (binding.fields ?? []).find((f) => f.attribute === attribute.ref);
    if (!field) return { attribute, status: attribute.expected ? "unmapped" : "optional" };
    const column = findColumn(columns, field.column);
    return { attribute, field, column, status: column ? "mapped" : "missing-column" };
  });
}

/** Each source column with what accounts for it, as the engine's statuses say (field, constant, listed, then the column's own). */
export function columnRows(binding: Binding, attributes: readonly StorageAttribute[], columns: readonly SourceColumn[]): ColumnRow[] {
  const byRef = new Map(attributes.map((a) => [a.ref, a]));
  const soft = typeof binding.delete === "object" ? binding.delete.soft.column : null;
  return columns.map((column) => {
    const field = (binding.fields ?? []).find((f) => findColumn(columns, f.column) === column);
    if (field) {
      const attribute = byRef.get(field.attribute);
      return attribute ? { column, status: "field", attribute } : { column, status: "field", fieldRef: field.attribute };
    }
    const constant = (binding.constants ?? []).find((c) => findColumn(columns, c.column) === column);
    if (constant) return { column, status: "constant", constant };
    const listed = (binding.columns ?? []).find((c) => findColumn(columns, c.column) === column);
    if (listed) return { column, status: listed.status };
    if (soft && findColumn(columns, soft) === column) return { column, status: "soft-delete" };
    if (column.identity) return { column, status: "identity" };
    if (column.computed) return { column, status: "computed" };
    if (column.hasDefault) return { column, status: "default" };
    return { column, status: "unaccounted" };
  });
}

/** Whether the binding writes: a write table named, or none set on a table source. */
export function writes(binding: Binding, source: BindingSource | undefined): boolean {
  if (binding.write === "none") return false;
  if (binding.write && typeof binding.write === "object") return true;
  return source ? source.kind === "table" : true;
}

// ------------------------------------------------------------------ problems the editor can see

export interface MapProblem {
  rule: string;
  severity: "error" | "warning";
  message: string;
  /** Under /bindings/<n>. */
  pointer: string;
}

/**
 * What the engine's binding rules report on one binding, as far as the editor can tell from the source's columns: a field naming
 * nothing (MQ4045), a column mapped twice (MQ4045), a missing key field on a writing binding (MQ4046), unaccounted columns
 * (MQ4047, once with the names), a constant the source lacks (MQ4048), and the proposed warning for a stored attribute no field
 * maps (MQ4058, not an engine rule yet).
 */
export function bindingProblems(binding: Binding, attributes: readonly StorageAttribute[], source: BindingSource | undefined): MapProblem[] {
  const out: MapProblem[] = [];
  if (!source) {
    out.push({
      rule: "MQ4044",
      severity: "error",
      message: `The source '${binding.source}' is not a table, view or query of the database.`,
      pointer: "/source",
    });
    return out;
  }
  const refs = new Set(attributes.map((a) => a.ref));
  const usedColumns = new Map<string, number>();
  const seenAttributes = new Set<string>();
  (binding.fields ?? []).forEach((f, i) => {
    if (!refs.has(f.attribute))
      out.push({
        rule: "MQ4045",
        severity: "error",
        message: `The field '${f.attribute}' is no attribute, member or to-one end of the entity.`,
        pointer: `/fields/${i}/attribute`,
      });
    else if (seenAttributes.has(f.attribute))
      out.push({
        rule: "MQ4045",
        severity: "error",
        message: `The attribute ${nameOfRef(attributes, f.attribute)} is mapped twice.`,
        pointer: `/fields/${i}/attribute`,
      });
    seenAttributes.add(f.attribute);
    const column = findColumn(source.columns, f.column);
    if (!column) out.push({ rule: "MQ4045", severity: "error", message: `${source.name} has no column '${f.column}'.`, pointer: `/fields/${i}/column` });
    else if (usedColumns.has(column.key))
      out.push({ rule: "MQ4045", severity: "error", message: `The column ${column.name} is mapped twice.`, pointer: `/fields/${i}/column` });
    else usedColumns.set(column.key, i);
  });
  (binding.constants ?? []).forEach((c, i) => {
    if (!findColumn(source.columns, c.column))
      out.push({
        rule: "MQ4048",
        severity: "error",
        message: `${source.name} has no column '${c.column}' for the constant.`,
        pointer: `/constants/${i}/column`,
      });
  });
  if (writes(binding, source)) {
    const missing = attributes.filter((a) => a.isKey && !(binding.fields ?? []).some((f) => f.attribute === a.ref));
    if (missing.length)
      out.push({
        rule: "MQ4046",
        severity: "error",
        message: `The binding writes but maps no field for the key ${missing.map((a) => a.name).join(", ")}.`,
        pointer: "/fields",
      });
  }
  const unaccounted = columnRows(binding, attributes, source.columns).filter((r) => r.status === "unaccounted");
  if (unaccounted.length)
    out.push({
      rule: "MQ4047",
      severity: "warning",
      message: `Nothing accounts for ${unaccounted.map((r) => r.column.name).join(", ")} of ${source.name}: map a field, set a constant or ignore it.`,
      pointer: "/columns",
    });
  const unmapped = attributeRows(binding, attributes, source.columns).filter((r) => r.status === "unmapped");
  if (unmapped.length)
    out.push({
      rule: UNMAPPED_ATTRIBUTE_RULE,
      severity: "warning",
      message: `No field maps ${unmapped.map((r) => r.attribute.name).join(", ")}: the entity reads ${unmapped.length === 1 ? "it" : "them"} from nowhere.`,
      pointer: "/fields",
    });
  return out;
}

/** The proposed rule for a stored attribute no field of a binding maps (warning; the mock reports it, the engine does not yet). */
export const UNMAPPED_ATTRIBUTE_RULE = "MQ4058";

const nameOfRef = (attributes: readonly StorageAttribute[], ref: string) => attributes.find((a) => a.ref === ref)?.name ?? ref;

// ------------------------------------------------------------------ Map by name

export interface NameMatch {
  attribute: StorageAttribute;
  column: SourceColumn;
}

/**
 * Map by name: every attribute without a field, matched to a free column of the same name (case and separators ignored, so
 * customerId reads customer_id). A column already mapped, constant or listed is not free; each column is proposed once.
 */
export function matchByName(binding: Binding, attributes: readonly StorageAttribute[], columns: readonly SourceColumn[]): NameMatch[] {
  const taken = new Set<string>();
  for (const f of binding.fields ?? []) {
    const c = findColumn(columns, f.column);
    if (c) taken.add(c.key);
  }
  for (const c of [...(binding.constants ?? []), ...(binding.columns ?? [])]) {
    const col = findColumn(columns, c.column);
    if (col) taken.add(col.key);
  }
  const out: NameMatch[] = [];
  for (const attribute of attributes) {
    if ((binding.fields ?? []).some((f) => f.attribute === attribute.ref)) continue;
    const names = [loose(attribute.name), ...(attribute.origin === "relation" ? [loose(attribute.name.replace(/Id$/, ""))] : [])];
    const column = columns.find((c) => !taken.has(c.key) && names.includes(loose(c.name)));
    if (!column) continue;
    taken.add(column.key);
    out.push({ attribute, column });
  }
  return out;
}

// ------------------------------------------------------------------ edits (each one gesture: one save, one undo step)

const prune = (b: Binding) => {
  for (const k of ["constants", "fields", "columns"] as const) if (b[k] && !b[k]!.length) delete b[k];
  return b;
};

/** Points an attribute at a column, or (null) removes its field. A column another field read moves to this one. */
export function setField(b: Binding, ref: string, column: SourceColumn | null, columns: readonly SourceColumn[]): Binding {
  let fields = (b.fields ?? []).filter((f) => f.attribute !== ref);
  if (column) {
    fields = fields.filter((f) => findColumn(columns, f.column) !== column);
    fields.push({ attribute: ref, column: column.key });
    b.columns = (b.columns ?? []).filter((c) => findColumn(columns, c.column) !== column);
    b.constants = (b.constants ?? []).filter((c) => findColumn(columns, c.column) !== column);
  }
  b.fields = fields;
  return prune(b);
}

/** Adds the matches as fields. */
export function applyMatches(b: Binding, matches: readonly NameMatch[], columns: readonly SourceColumn[]): Binding {
  for (const m of matches) setField(b, m.attribute.ref, m.column, columns);
  return b;
}

/** Lists a column as ignored, filled by the database or computed, or (null) drops the listing. */
export function setColumnStatus(b: Binding, column: SourceColumn, status: ListedStatus | null, columns: readonly SourceColumn[]): Binding {
  b.columns = (b.columns ?? []).filter((c) => findColumn(columns, c.column) !== column);
  if (status) b.columns.push({ column: column.key, status });
  return prune(b);
}

/** Replaces the constants (the grid's rows; a row without a column is kept out). */
export function setConstants(b: Binding, constants: readonly BindingConstant[]): Binding {
  b.constants = constants
    .filter((c) => c.column)
    .map((c) => (c.value === undefined || c.value === null || c.value === "" ? { column: c.column } : { column: c.column, value: c.value }));
  return prune(b);
}

/** A constant's text as the value it stores: a number or a boolean when it reads as one, else the text. */
export function constantValue(text: string): string | number | boolean | null {
  const t = text.trim();
  if (t === "") return null;
  if (t === "true" || t === "false") return t === "true";
  if (/^-?\d+(\.\d+)?$/.test(t)) return Number(t);
  return text;
}

export type WriteChoice = "source" | "none" | { table: string };

/** The write choice: the source table (the default, absent), none (read-only) or another table. */
export function setWrite(b: Binding, choice: WriteChoice): Binding {
  if (choice === "source") delete b.write;
  else b.write = choice;
  // A read-only binding deletes nothing: the delete mode goes with it.
  if (choice === "none") delete b.delete;
  return b;
}

export function writeChoiceOf(b: Binding): WriteChoice {
  if (b.write === "none") return "none";
  if (b.write && typeof b.write === "object") return b.write;
  return "source";
}

export function setDelete(b: Binding, mode: BindingDelete | null): Binding {
  if (mode === null || mode === "key") delete b.delete;
  else b.delete = mode;
  return b;
}

/** A new binding to a source; with `byName`, the fields Map by name finds are in already (Auto-map). */
export function newBinding(id: string, database: string, source: BindingSource, attributes: readonly StorageAttribute[], byName = false): Binding {
  const b: Binding = { id, database, source: source.id };
  if (byName) applyMatches(b, matchByName(b, attributes, source.columns), source.columns);
  return prune(b);
}

/** A source changed: the fields, constants and listings whose columns the new source lacks go (Map by name fills it again). */
export function retarget(b: Binding, source: BindingSource): Binding {
  b.source = source.id;
  b.fields = (b.fields ?? []).filter((f) => findColumn(source.columns, f.column));
  b.constants = (b.constants ?? []).filter((c) => findColumn(source.columns, c.column));
  b.columns = (b.columns ?? []).filter((c) => findColumn(source.columns, c.column));
  if (source.kind !== "table" && !(b.write && typeof b.write === "object")) delete b.write;
  return prune(b);
}

// ------------------------------------------------------------------ entities to tables by name (Auto-map)

/** An English plural, as the conventions' plural table names make it. */
export function pluralOf(word: string): string {
  if (/(s|x|z|ch|sh)$/i.test(word)) return word + "es";
  if (/[^aeiou]y$/i.test(word)) return word.slice(0, -1) + "ies";
  return word + "s";
}

/**
 * The table an entity's name points to through the naming conventions: the table case is ignored (invoice_line, InvoiceLine
 * and invoiceline read the same), and the plural form the conventions prefer is tried first, then the other one.
 */
export function tableForEntity<T extends { name: string }>(entityName: string, tables: readonly T[], pluralTables = true): T | undefined {
  const forms = pluralTables ? [pluralOf(entityName), entityName] : [entityName, pluralOf(entityName)];
  for (const form of forms) {
    const hit = tables.find((t) => loose(t.name) === loose(form));
    if (hit) return hit;
  }
  return undefined;
}

export type AutoMapOutcome = "match" | "partial" | "miss";

export interface AutoMapRow {
  entity: string;
  entityName: string;
  source: BindingSource | null;
  matches: NameMatch[];
  /** The expected attributes no column matched. */
  missing: StorageAttribute[];
  outcome: AutoMapOutcome;
}

/** One entity's auto-map: its table by name (or the one picked), its attributes by name, and how complete that is. */
export function autoMapRow(
  entity: { id: string; name: string },
  attributes: readonly StorageAttribute[],
  sources: readonly BindingSource[],
  options: { pluralTables?: boolean; picked?: string | null } = {},
): AutoMapRow {
  // A projected table is not a target: binding to it would end the projection that makes it (Create tables… is for that).
  const tables = sources.filter((s) => (s.kind === "table" && !s.projected) || s.kind === "view");
  const source = options.picked ? (sources.find((s) => s.id === options.picked) ?? null) : (tableForEntity(entity.name, tables, options.pluralTables) ?? null);
  if (!source) return { entity: entity.id, entityName: entity.name, source: null, matches: [], missing: [], outcome: "miss" };
  const matches = matchByName({ id: "", database: "", source: source.id }, attributes, source.columns);
  const missing = attributes.filter((a) => a.expected && !matches.some((m) => m.attribute.ref === a.ref));
  return { entity: entity.id, entityName: entity.name, source, matches, missing, outcome: missing.length ? "partial" : "match" };
}
