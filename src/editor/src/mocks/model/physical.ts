// The mock's physical resolution: a simplified resolver (engine-design.md 7) that turns the mock
// model into a DatabaseView (E1) so the Database and Mappings workspaces and the DDL preview
// follow edits without a backend. Conventions (case, plural tables, enum storage), mapping
// overrides (storage, prefix, ignore) and designed-table native types are applied; the output is
// plausible, not the engine's exact result, and the mocks' hashes and names are never compared
// with real ones.
import { conventionOf, placesEntity } from "@/model/databaseMapping";
import { schemaForEntity, schemasOf } from "@/model/databaseSchemas";
import type { ColumnView, DatabaseView, ForeignKeyView, SchemaView, SequenceView, TableView, ViewView } from "@/api/types";

type Json = Record<string, unknown>;
const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

type Annotations = Pick<TableView, "displayName" | "pluralName" | "description" | "stereotypes" | "tags" | "category" | "properties" | "generation">;

/**
 * The annotations a database, schema, table, column, view or sequence takes from its own file or entry (none without one,
 * never its entity's or attribute's): the stereotypes' default properties under the file's own, as the engine merges them. A
 * sidecar description reads as null.
 */
export function annotationsOf(doc: Json | undefined, stereotypes: ReadonlyMap<string, Json>): Annotations {
  const keys = (doc?.stereotypes as string[] | undefined) ?? [];
  const properties: Record<string, unknown> = {};
  for (const key of keys) Object.assign(properties, (stereotypes.get(key)?.defaultProperties as Json | undefined) ?? {});
  Object.assign(properties, (doc?.properties as Json | undefined) ?? {});
  const generation = Object.fromEntries(
    Object.entries((doc?.generation as Record<string, Json> | undefined) ?? {}).map(([pack, h]) => [
      pack,
      { skip: h.skip === true, rename: typeof h.rename === "string" ? h.rename : null, variables: (h.variables as Json | undefined) ?? {} },
    ]),
  );
  return {
    displayName: typeof doc?.displayName === "string" ? doc.displayName : null,
    pluralName: typeof doc?.pluralName === "string" ? doc.pluralName : null,
    description: typeof doc?.description === "string" ? doc.description : null,
    stereotypes: [...keys],
    tags: [...((doc?.tags as string[] | undefined) ?? [])],
    category: typeof doc?.category === "string" ? doc.category : null,
    properties: Object.fromEntries(Object.entries(properties).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))),
    generation,
  };
}

export interface PhysicalInput {
  docs: Map<string, Json>;
  conventions: Json;
  databaseConventions: Record<string, Json>;
}

function words(name: string): string[] {
  return name
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean)
    .map((w) => w.toLowerCase());
}

export function applyCase(name: string, style: string): string {
  const w = words(name);
  switch (style) {
    case "pascal":
      return w.map((x) => x[0].toUpperCase() + x.slice(1)).join("");
    case "camel":
      return w.map((x, i) => (i === 0 ? x : x[0].toUpperCase() + x.slice(1))).join("");
    case "kebab":
      return w.join("-");
    case "upper-snake":
      return w.join("_").toUpperCase();
    case "preserve":
      return name;
    default:
      return w.join("_");
  }
}

export function plural(word: string): string {
  if (/(s|x|z|ch|sh)$/i.test(word)) return word + "es";
  if (/[^aeiou]y$/i.test(word)) return word.slice(0, -1) + "ies";
  return word + "s";
}

const NATIVE: Record<string, Record<string, (a: { length?: number; precision?: number; scale?: number }) => string>> = {
  postgresql: {
    string: (a) => `varchar(${a.length ?? 255})`,
    text: () => "text",
    bool: () => "boolean",
    int16: () => "smallint",
    int32: () => "integer",
    int64: () => "bigint",
    decimal: (a) => `numeric(${a.precision ?? 18},${a.scale ?? 2})`,
    float: () => "real",
    double: () => "double precision",
    date: () => "date",
    time: () => "time(6)",
    datetime: () => "timestamp(6)",
    datetimeoffset: () => "timestamptz(6)",
    duration: () => "interval",
    uuid: () => "uuid",
    ulid: () => "char(26)",
    binary: () => "bytea",
    json: () => "jsonb",
  },
  sqlserver: {
    string: (a) => `nvarchar(${a.length ?? 255})`,
    text: () => "nvarchar(max)",
    bool: () => "bit",
    int16: () => "smallint",
    int32: () => "int",
    int64: () => "bigint",
    decimal: (a) => `decimal(${a.precision ?? 18},${a.scale ?? 2})`,
    float: () => "real",
    double: () => "float",
    date: () => "date",
    time: () => "time(7)",
    datetime: () => "datetime2(7)",
    datetimeoffset: () => "datetimeoffset(7)",
    duration: () => "bigint",
    uuid: () => "uniqueidentifier",
    ulid: () => "char(26)",
    binary: () => "varbinary(max)",
    json: () => "nvarchar(max)",
  },
  sqlite: {
    string: () => "TEXT",
    text: () => "TEXT",
    bool: () => "INTEGER",
    int16: () => "INTEGER",
    int32: () => "INTEGER",
    int64: () => "INTEGER",
    decimal: () => "NUMERIC",
    float: () => "REAL",
    double: () => "REAL",
    date: () => "TEXT",
    time: () => "TEXT",
    datetime: () => "TEXT",
    datetimeoffset: () => "TEXT",
    duration: () => "INTEGER",
    uuid: () => "TEXT",
    ulid: () => "TEXT",
    binary: () => "BLOB",
    json: () => "TEXT",
  },
  mysql: {
    string: (a) => `varchar(${a.length ?? 255})`,
    text: () => "text",
    bool: () => "tinyint(1)",
    int16: () => "smallint",
    int32: () => "int",
    int64: () => "bigint",
    decimal: (a) => `decimal(${a.precision ?? 18},${a.scale ?? 2})`,
    float: () => "float",
    double: () => "double",
    date: () => "date",
    time: () => "time(6)",
    datetime: () => "datetime(6)",
    datetimeoffset: () => "datetime(6)",
    duration: () => "bigint",
    uuid: () => "char(36)",
    ulid: () => "char(26)",
    binary: () => "longblob",
    json: () => "json",
  },
  oracle: {
    string: (a) => `varchar2(${a.length ?? 255})`,
    text: () => "clob",
    bool: () => "number(1)",
    int16: () => "number(5)",
    int32: () => "number(10)",
    int64: () => "number(19)",
    decimal: (a) => `number(${a.precision ?? 18},${a.scale ?? 2})`,
    float: () => "binary_float",
    double: () => "binary_double",
    date: () => "date",
    time: () => "interval day to second",
    datetime: () => "timestamp(6)",
    datetimeoffset: () => "timestamp(6) with time zone",
    duration: () => "interval day to second",
    uuid: () => "raw(16)",
    ulid: () => "char(26)",
    binary: () => "blob",
    json: () => "clob",
  },
};

export function nativeType(dialect: string, type: string, facets: { length?: number; precision?: number; scale?: number }): string {
  const map = NATIVE[dialect] ?? NATIVE.postgresql;
  return (map[type] ?? (() => type))(facets);
}

interface ResolvedColumn {
  name: string;
  type: string;
  length: number | null;
  precision: number | null;
  scale: number | null;
  nullable: boolean;
  attributeId: string | null;
  attributePath: string | null;
  key: string;
  isPrimaryKey: boolean;
  isForeignKey: boolean;
  unique: boolean;
  indexed: boolean;
  nativeOverride?: string;
  /** The overlay's entry for this column (by attribute key), whose annotations and comment the column takes. */
  entry?: Json;
}

/** A column's own members the mock does not resolve from conventions: no default, collation or sequence, no annotations. */
const plainColumn = (entry: Json | undefined, stereotypes: ReadonlyMap<string, Json>) => ({
  default: entry?.default ?? null,
  computedStored: false,
  sequenceId: null,
  collation: typeof entry?.collation === "string" ? entry.collation : null,
  comment: typeof entry?.comment === "string" ? entry.comment : null,
  ...annotationsOf(entry, stereotypes),
});

/** Resolves one database of the mock model; null when the id is not a database. */
export function resolveDatabase(input: PhysicalInput, databaseId: string): DatabaseView | null {
  const db = input.docs.get(databaseId);
  if (!db || db.kind !== "database") return null;
  const dialect = String(db.dialect ?? "postgresql");
  const dbName = String(db.name ?? "");
  const conventions = { ...input.conventions, ...stripNull(input.databaseConventions[dbName] ?? {}) };
  const tableCase = String(conventions.tableCase ?? "snake");
  const columnCase = String(conventions.columnCase ?? "snake");
  const pluralTables = conventions.pluralTables !== false;
  const enumStorage = String(conventions.enumStorage ?? "int");
  const defaultStringLength = typeof conventions.defaultStringLength === "number" ? conventions.defaultStringLength : 255;
  const defaultSchema = typeof db.defaultSchema === "string" ? db.defaultSchema : null;
  // What the database holds (D46): its byConvention (a file without it: every entity, or its packages') plus the
  // entities a mapping names, less the ignored ones.
  const convention = conventionOf(db as Parameters<typeof conventionOf>[0]);
  const parentOf = (id: string) => {
    const parent = input.docs.get(id)?.parent;
    return typeof parent === "string" ? parent : null;
  };

  const all = [...input.docs.values()];
  const entityMapping = new Map(all.filter((d) => d.kind === "mapping" && d.database === databaseId && d.entity).map((d) => [String(d.entity), d]));
  // A conventional table's schema: the entity's mapping, else the nearest convention entry, else the default (erratum E26).
  const declared = new Map(schemasOf(db).map((x) => [x.id, x.name]));
  const schemaOfEntity = (entity: Json) => {
    const sid = schemaForEntity(db, typeof entity.package === "string" ? entity.package : null, entityMapping.get(String(entity.id)), parentOf);
    return (sid ? declared.get(sid) : undefined) ?? defaultSchema;
  };
  const entities = all
    .filter(
      (d) =>
        d.kind === "entity" &&
        d.abstract !== true &&
        placesEntity(
          convention,
          typeof d.package === "string" ? d.package : null,
          entityMapping.get(String(d.id)) as { ignore?: boolean } | undefined,
          parentOf,
        ),
    )
    .sort((a, b) => String(a.name).localeCompare(String(b.name)));
  const entityIds = new Set(entities.map((e) => String(e.id)));
  const stereotypes = new Map(all.filter((d) => d.kind === "stereotype").map((s) => [String(s.key), s]));
  const mappings = all.filter((d) => d.kind === "mapping" && d.database === databaseId);
  const overlays = all.filter((d) => d.kind === "table" && d.database === databaseId);
  const colName = (n: string) => applyCase(n, columnCase);
  const tableName = (entityName: string) => applyCase(pluralTables ? plural(entityName) : entityName, tableCase);

  const tables: TableView[] = [];
  const pkColumnsByEntity = new Map<string, { table: TableView; columns: ColumnView[] }>();

  type Override = { storage?: string; prefix?: string };
  const scalarColumns = (
    attr: Json,
    prefix: string,
    path: string,
    attrId: string,
    required: boolean,
    override: Override,
  ): Omit<ResolvedColumn, "isPrimaryKey" | "isForeignKey">[] => {
    const type = attr.type as string | { ref: string };
    if (typeof type === "string") {
      return [
        {
          name: colName(prefix + String(attr.name)),
          type,
          length: type === "string" ? ((attr.length as number | undefined) ?? defaultStringLength) : null,
          precision: type === "decimal" ? ((attr.precision as number | undefined) ?? 18) : null,
          scale: type === "decimal" ? ((attr.scale as number | undefined) ?? 2) : null,
          nullable: !required,
          attributeId: attrId,
          attributePath: path,
          key: path,
          unique: attr.unique === true,
          indexed: attr.indexed === true,
        },
      ];
    }
    const target = input.docs.get(type.ref);
    if (!target) return [];
    if (target.kind === "value-object") {
      const voPrefix = override.prefix ?? `${String(attr.name)}_`;
      return arr(target.attributes).flatMap((inner) =>
        scalarColumns(inner, prefix + voPrefix, `${path}.${String(inner.id)}`, attrId, required && inner.required === true, {}),
      );
    }
    if (target.kind === "enum") {
      const asString = (override.storage ?? enumStorage) === "string";
      const longest = Math.max(1, ...arr(target.members).map((m) => String(m.name).length));
      return [
        {
          name: colName(prefix + String(attr.name)),
          type: asString ? "string" : "int32",
          length: asString ? longest : null,
          precision: null,
          scale: null,
          nullable: !required,
          attributeId: attrId,
          attributePath: path,
          key: path,
          unique: attr.unique === true,
          indexed: attr.indexed === true,
        },
      ];
    }
    if (target.kind === "scalar-type") {
      const base = String(target.base);
      return [
        {
          name: colName(prefix + String(attr.name)),
          type: base,
          length: base === "string" ? ((target.length as number | undefined) ?? defaultStringLength) : null,
          precision: (target.precision as number | undefined) ?? null,
          scale: (target.scale as number | undefined) ?? null,
          nullable: !required,
          attributeId: attrId,
          attributePath: path,
          key: path,
          unique: attr.unique === true,
          indexed: attr.indexed === true,
        },
      ];
    }
    return [];
  };

  const toView = (c: ResolvedColumn, position: number): ColumnView => ({
    key: c.key,
    name: c.name,
    type: c.type,
    nativeType:
      c.nativeOverride ?? nativeType(dialect, c.type, { length: c.length ?? undefined, precision: c.precision ?? undefined, scale: c.scale ?? undefined }),
    length: c.length,
    precision: c.precision,
    scale: c.scale,
    nullable: c.nullable,
    defaultSql: null,
    identity: false,
    computed: null,
    attributeId: c.attributeId,
    attributePath: c.attributePath,
    isPrimaryKey: c.isPrimaryKey,
    isForeignKey: c.isForeignKey,
    isDiscriminator: false,
    position,
    ...plainColumn(c.entry, stereotypes),
  });

  for (const entity of entities) {
    const id = String(entity.id);
    const mapping = mappings.find((m) => m.entity === id);
    if (mapping?.ignore === true) continue;
    const overrides = new Map(arr(mapping?.attributes).map((a) => [String(a.attribute), a]));
    const overlay = overlays.find((t) => t.entity === id);
    const nativeByAttr = new Map(
      arr(overlay?.columns)
        .filter((c) => c.attribute && c.nativeType)
        .map((c) => [String(c.attribute), String(c.nativeType)]),
    );
    const entryByAttr = new Map(
      arr(overlay?.columns)
        .filter((c) => c.attribute)
        .map((c) => [String(c.attribute), c]),
    );
    const keyIds = new Set(((entity.key as Json | undefined)?.attributes as string[] | undefined) ?? []);
    const identity = (entity.key as Json | undefined)?.strategy === "database-identity";
    const attributes = [...arr(entity.attributes)];
    for (const skey of (entity.stereotypes as string[] | undefined) ?? []) attributes.push(...arr(stereotypes.get(skey)?.attributes));
    const columns: ResolvedColumn[] = [];
    for (const attr of attributes) {
      if (attr.collection === true) continue;
      const override = overrides.get(String(attr.id));
      if (override?.ignore === true) continue;
      const produced = scalarColumns(attr, "", String(attr.id), String(attr.id), attr.required === true, {
        storage: override?.storage as string | undefined,
        prefix: override?.prefix as string | undefined,
      });
      for (const c of produced)
        columns.push({
          ...c,
          isPrimaryKey: keyIds.has(String(attr.id)),
          isForeignKey: false,
          nativeOverride: nativeByAttr.get(String(c.key)),
          entry: entryByAttr.get(String(c.key)),
        });
    }
    const name = tableName(String(entity.name));
    const table: TableView = {
      key: `${id}@${databaseId}`,
      name,
      schema: schemaOfEntity(entity),
      origin: "synthesized",
      entityId: id,
      relationId: null,
      isJunction: false,
      isLookup: false,
      comment: typeof overlay?.comment === "string" ? overlay.comment : null,
      columns: [],
      primaryKey: null,
      uniques: [],
      foreignKeys: [],
      indexes: [],
      ...annotationsOf(overlay, stereotypes),
    };
    table.columns = columns.map((c, i) => ({ ...toView(c, i + 1), identity: identity && c.isPrimaryKey }));
    const pk = table.columns.filter((c) => c.isPrimaryKey);
    table.primaryKey = pk.length ? { name: `pk_${name}`, columns: pk.map((c) => c.key) } : null;
    table.uniques = columns.filter((c) => c.unique).map((c) => ({ name: `uq_${name}_${c.name}`, columns: [c.key] }));
    table.indexes = columns
      .filter((c) => c.indexed)
      .map((c) => ({ name: `ix_${name}_${c.name}`, columns: [{ column: c.key, descending: false }], unique: false, where: null }));
    if (overlay) {
      table.origin = "designed";
      table.key = `${id}@${databaseId}`;
      for (const ix of arr(overlay.indexes)) {
        const cols = arr(ix.columns).map((c) => ({ column: String(c.column), descending: c.descending === true }));
        const names = cols.map((c) => table.columns.find((col) => col.key === c.column)?.name ?? c.column);
        if (!table.indexes.some((x) => x.columns.length === cols.length && x.columns.every((c, i) => c.column === cols[i].column)))
          table.indexes.push({ name: `ix_${name}_${names.join("_")}`, columns: cols, unique: false, where: null });
        else table.indexes = table.indexes.map((x) => (x.columns.length === 1 && x.columns[0].column === cols[0]?.column ? { ...x, columns: cols } : x));
      }
    }
    tables.push(table);
    pkColumnsByEntity.set(id, { table, columns: table.columns.filter((c) => c.isPrimaryKey) });
  }

  // Relations: a foreign key on the "many" side, or a junction table for many-to-many.
  const relations = all.filter((d) => d.kind === "relation").sort((a, b) => String(a.id).localeCompare(String(b.id)));
  for (const relation of relations) {
    const ends = arr(relation.ends);
    if (ends.length !== 2) continue;
    const [a, b] = ends;
    if (!entityIds.has(String(a.entity)) || !entityIds.has(String(b.entity))) continue;
    const relationMapping = mappings.find((m) => m.relation === relation.id);
    if (relationMapping?.ignore === true) continue;
    const manyA = (a.max ?? "*") === "*";
    const manyB = (b.max ?? "*") === "*";
    const onDelete = (end: Json) => ({ cascade: "cascade", restrict: "restrict", "set-null": "set null" })[String(end.onDelete)] ?? "no action";
    const addForeignKey = (holder: Json, referenced: Json, end: Json, nullable: boolean, ordered: boolean) => {
      const from = pkColumnsByEntity.get(String(holder.entity));
      const to = pkColumnsByEntity.get(String(referenced.entity));
      if (!from || !to) return;
      const role = String(referenced.role ?? referenced.navigation ?? "ref");
      const fkColumns: ColumnView[] = to.columns.map((pk) => ({
        ...pk,
        key: `${String(referenced.id)}.${pk.key}`,
        name: colName(`${role}_${pk.name}`),
        nullable,
        identity: false,
        isPrimaryKey: false,
        isForeignKey: true,
        attributeId: null,
        attributePath: null,
        position: from.table.columns.length + 1,
        ...plainColumn(undefined, stereotypes),
      }));
      from.table.columns.push(...fkColumns);
      if (ordered)
        from.table.columns.push({
          key: `${String(end.id)}.position`,
          name: colName("position"),
          type: "int32",
          nativeType: nativeType(dialect, "int32", {}),
          length: null,
          precision: null,
          scale: null,
          nullable: false,
          defaultSql: null,
          identity: false,
          computed: null,
          attributeId: null,
          attributePath: null,
          isPrimaryKey: false,
          isForeignKey: false,
          isDiscriminator: false,
          position: from.table.columns.length + 1,
          ...plainColumn(undefined, stereotypes),
        });
      const fk: ForeignKeyView = {
        name: `fk_${from.table.name}_${fkColumns.map((c) => c.name).join("_")}`,
        columns: fkColumns.map((c) => c.key),
        referencedTable: to.table.key,
        referencedColumns: to.columns.map((c) => c.key),
        onDelete: onDelete(referenced),
        onUpdate: "no action",
        relationId: String(relation.id),
        endId: String(referenced.id),
      };
      from.table.foreignKeys.push(fk);
      from.table.columns.forEach((c, i) => (c.position = i + 1));
    };

    if (manyA && manyB) {
      const left = pkColumnsByEntity.get(String(a.entity));
      const right = pkColumnsByEntity.get(String(b.entity));
      if (!left || !right) continue;
      const leftName = applyCase(String(input.docs.get(String(a.entity))?.name), tableCase);
      const rightName = applyCase(String(input.docs.get(String(b.entity))?.name), tableCase);
      const name = `${leftName}_${rightName}`;
      const key = `${String(relation.id)}@${databaseId}`;
      const junction: TableView = {
        key,
        name,
        schema: defaultSchema,
        origin: "synthesized",
        entityId: null,
        relationId: String(relation.id),
        isJunction: true,
        isLookup: false,
        comment: null,
        columns: [],
        primaryKey: null,
        uniques: [],
        foreignKeys: [],
        indexes: [],
        ...annotationsOf(
          overlays.find((t) => t.relation === relation.id),
          stereotypes,
        ),
      };
      const sides = [
        { end: a, target: left, label: leftName },
        { end: b, target: right, label: rightName },
      ];
      for (const side of sides) {
        const cols = side.target.columns.map((pk) => ({
          ...pk,
          key: `${String(side.end.id)}.${pk.key}`,
          name: colName(`${side.label}_${pk.name}`),
          nullable: false,
          identity: false,
          isPrimaryKey: true,
          isForeignKey: true,
          attributeId: null,
          attributePath: null,
          position: 0,
          ...plainColumn(undefined, stereotypes),
        }));
        junction.columns.push(...cols);
        junction.foreignKeys.push({
          name: `fk_${name}_${cols.map((c) => c.name).join("_")}`,
          columns: cols.map((c) => c.key),
          referencedTable: side.target.table.key,
          referencedColumns: side.target.columns.map((c) => c.key),
          onDelete: "cascade",
          onUpdate: "no action",
          relationId: String(relation.id),
          endId: String(side.end.id),
        });
      }
      for (const attr of arr(relation.attributes)) {
        for (const c of scalarColumns(attr, "", String(attr.id), String(attr.id), attr.required === true, {}))
          junction.columns.push(toView({ ...c, isPrimaryKey: false, isForeignKey: false }, 0));
      }
      junction.columns.forEach((c, i) => (c.position = i + 1));
      junction.primaryKey = { name: `pk_${name}`, columns: junction.columns.filter((c) => c.isPrimaryKey).map((c) => c.key) };
      tables.push(junction);
    } else if (manyA && !manyB) {
      addForeignKey(a, b, a, (b.min ?? 0) === 0, a.ordered === true);
    } else if (manyB && !manyA) {
      addForeignKey(b, a, b, (a.min ?? 0) === 0, b.ordered === true);
    } else {
      addForeignKey(b, a, b, (a.min ?? 0) === 0, false);
    }
  }

  tables.sort((x, y) => x.name.localeCompare(y.name));
  const schemaOf = (doc: Json) => (typeof doc.schema === "string" ? (declared.get(doc.schema) ?? defaultSchema) : defaultSchema);
  const byName = <T extends { name: string }>(x: T, y: T) => x.name.localeCompare(y.name);
  const views: ViewView[] = all
    .filter((d) => d.kind === "view" && d.database === databaseId)
    .map((d) => {
      const body = (d.body as Record<string, string> | undefined) ?? {};
      return {
        id: String(d.id),
        name: String(d.name ?? ""),
        schema: schemaOf(d),
        body: body[dialect] ?? body["*"] ?? "",
        columns: arr(d.columns).map((c) => ({
          name: String(c.name),
          type: typeof c.type === "string" ? c.type : null,
          nativeType: typeof c.type === "string" ? nativeType(dialect, c.type, {}) : null,
          nullable: c.nullable !== false,
        })),
        comment: typeof d.comment === "string" ? d.comment : null,
        ...annotationsOf(d, stereotypes),
      };
    })
    .sort(byName);
  const sequences: SequenceView[] = all
    .filter((d) => d.kind === "sequence" && d.database === databaseId)
    .map((d) => {
      const type = typeof d.type === "string" ? d.type : "int64";
      return {
        id: String(d.id),
        name: String(d.name ?? ""),
        schema: schemaOf(d),
        type,
        nativeType: nativeType(dialect, type, {}),
        start: typeof d.start === "number" ? d.start : 1,
        increment: typeof d.increment === "number" ? d.increment : 1,
        min: typeof d.min === "number" ? d.min : null,
        max: typeof d.max === "number" ? d.max : null,
        cycle: d.cycle === true,
        cache: typeof d.cache === "number" ? d.cache : null,
        ...annotationsOf(d, stereotypes),
      };
    })
    .sort(byName);
  // Every schema the file declares or a table, view or sequence uses, by name; a declared one carries its entry's annotations.
  const entries = arr(db.schemas).filter((x) => typeof x.id === "string");
  const schemaNames = new Set<string>(entries.map((x) => String(x.name ?? "")));
  for (const o of [...tables, ...views, ...sequences]) if (o.schema) schemaNames.add(o.schema);
  const schemas: SchemaView[] = [...schemaNames]
    .sort((x, y) => (x < y ? -1 : x > y ? 1 : 0))
    .map((name) => {
      const entry = entries.find((x) => x.name === name);
      return {
        id: entry ? String(entry.id) : `${databaseId}/${name}`,
        name,
        isDefault: name === defaultSchema,
        isDeclared: entry !== undefined,
        ...annotationsOf(entry, stereotypes),
      };
    });
  const limits: Record<string, number> = { postgresql: 63, sqlserver: 128, mysql: 64, oracle: 128 };
  const packageEntries = Array.isArray(db.packages) ? (db.packages as unknown[]) : [];
  return {
    id: databaseId,
    name: dbName,
    dialect,
    version: typeof db.version === "string" ? db.version : null,
    defaultSchema,
    tables,
    views,
    sequences,
    schemas,
    quoting: (typeof db.quoting === "string" ? db.quoting : "reserved") as DatabaseView["quoting"],
    maxIdentifierLength: typeof db.maxIdentifierLength === "number" ? db.maxIdentifierLength : (limits[dialect] ?? null),
    byConvention: (typeof db.byConvention === "string" ? db.byConvention : packageEntries.length > 0 ? "packages" : "all") as DatabaseView["byConvention"],
    packages: packageEntries.map((p) =>
      typeof p === "string"
        ? { packageId: p, schema: null }
        : { packageId: String((p as Json).package), schema: declared.get(String((p as Json).schema)) ?? null },
    ),
    ...annotationsOf(db, stereotypes),
  };
}

function stripNull(o: Json): Json {
  return Object.fromEntries(Object.entries(o).filter(([, v]) => v !== null && v !== undefined));
}
