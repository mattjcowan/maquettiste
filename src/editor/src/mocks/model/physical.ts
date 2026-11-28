// The mock's physical resolution: a simplified resolver (engine-design.md 7) that turns the mock
// model into a DatabaseView (E1) so the Database and Mappings workspaces and the DDL preview
// follow edits without a backend. Every table is a table file (designed or imported); nothing is laid out from entities.
// Conventions (comments, default string length) and the tables' native types are applied; the output is
// plausible, not the engine's exact result, and the mocks' hashes and names are never compared
// with real ones.
import { schemasOf } from "@/model/databaseSchemas";
import type {
  ColumnView,
  DatabaseTypeView,
  DatabaseView,
  Diagnostic,
  RoutineView,
  SchemaView,
  SequenceView,
  SqlObjectView,
  TableView,
  ViewView,
} from "@/api/types";
import { BUILTIN_TYPES } from "@/model/model";
import { resolveQueries } from "./queries";
import { typeStorageRun } from "./typeStorage";

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

/** Renders a native type pattern's {length}, {precision} and {scale} placeholders, missing facets taking the defaults. */
export function renderNativePattern(pattern: string, type: string, facets: { length: number | null; precision: number | null; scale: number | null }): string {
  return pattern
    .replaceAll("{length}", String(facets.length ?? 255))
    .replaceAll("{precision}", String(facets.precision ?? (type === "decimal" ? 18 : 6)))
    .replaceAll("{scale}", String(facets.scale ?? 2));
}

export function nativeType(dialect: string, type: string, facets: { length?: number; precision?: number; scale?: number }): string {
  const map = NATIVE[dialect] ?? NATIVE.postgresql;
  return (map[type] ?? (() => type))(facets);
}

/**
 * The native type of a fixed-length or Unicode string, text or binary column (the engine's type map variant entries, such as
 * string:fixed or string:ansi), or undefined when the column asks for neither or the dialect writes the plain keyword's type.
 */
export function variantNativeType(
  dialect: string,
  type: string,
  length: number | null,
  fixedLength: boolean,
  unicode: boolean | undefined,
): string | undefined {
  const n = length ?? 255;
  if (dialect === "sqlserver") {
    if (type === "string" && fixedLength) return unicode === false ? `char(${n})` : `nchar(${n})`;
    if (type === "string" && unicode === false) return `varchar(${n})`;
    if (type === "text" && unicode === false) return "varchar(max)";
    if (type === "binary" && fixedLength) return `binary(${n})`;
  } else if (dialect === "postgresql") {
    if (type === "string" && fixedLength) return `char(${n})`;
  } else if (dialect === "mysql") {
    const charset = unicode === true ? " character set utf8mb4" : "";
    if (type === "string" && (fixedLength || charset)) return `${fixedLength ? "char" : "varchar"}(${n})${charset}`;
    if (type === "binary" && fixedLength) return `binary(${n})`;
  }
  return undefined;
}

interface ResolvedColumn {
  name: string;
  type: string;
  length: number | null;
  precision: number | null;
  scale: number | null;
  nullable: boolean;
  key: string;
  isPrimaryKey: boolean;
  isForeignKey: boolean;
  unique: boolean;
  indexed: boolean;
  nativeOverride?: string;
  /** The overlay's entry for this column (by attribute key), whose annotations and comment the column takes. */
  entry?: Json;
  /** The description of the attribute the column stores, for the comments convention. */
  attributeDescription?: string | null;
}

/** A description as plain text: an inline description, trimmed; a sidecar one, or none, as null. */
export const descriptionText = (doc: Json | undefined): string | null =>
  typeof doc?.description === "string" && doc.description.trim() ? doc.description.trim() : null;

/** A column's own members the mock does not resolve from conventions: no default, collation or sequence, no annotations. */
export const plainColumn = (entry: Json | undefined, stereotypes: ReadonlyMap<string, Json>) => ({
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
  const defaultStringLength = typeof conventions.defaultStringLength === "number" ? conventions.defaultStringLength : 255;
  const defaultSchema = typeof db.defaultSchema === "string" ? db.defaultSchema : null;
  /** A per-dialect text map's text for the database's dialect, else its "*" one, else null. */
  const forDialectText = (map: unknown): string | null => {
    const texts = map && typeof map === "object" ? (map as Record<string, unknown>) : {};
    const text = texts[dialect] ?? texts["*"];
    return typeof text === "string" ? text : null;
  };
  // The comments convention (engine-design.md 2.4): with "descriptions" (the default) a table or column without an explicit comment
  // takes its own description, else the description of what it stores.
  const commentsFromDescriptions = conventions.comments !== "none";
  const commentOf = (entry: Json | undefined, ...fallbacks: (string | null | undefined)[]): string | null =>
    typeof entry?.comment === "string"
      ? entry.comment
      : commentsFromDescriptions
        ? (descriptionText(entry) ?? fallbacks.find((f): f is string => typeof f === "string" && f.length > 0) ?? null)
        : null;
  const all = [...input.docs.values()];
  const stereotypes = new Map(all.filter((d) => d.kind === "stereotype").map((s) => [String(s.key), s]));
  const mappings = all.filter((d) => d.kind === "mapping" && d.database === databaseId);
  const tableDocs = all.filter((d) => d.kind === "table" && d.database === databaseId);
  const declared = new Map(schemasOf(db).map((x) => [x.id, x.name]));

  const tables: TableView[] = [];
  const num = (v: unknown): number | null => (typeof v === "number" ? v : null);

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
    isPrimaryKey: c.isPrimaryKey,
    isForeignKey: c.isForeignKey,
    position,
    ...plainColumn(c.entry, stereotypes),
    comment: commentOf(c.entry),
  });

  // Designed and imported tables (DatabaseRun.BuildDesignedTable): their own columns by id, their keys, uniques, indexes and
  // foreign keys as the file says them (a foreign key names a table file id or a synthesized key; no referenced columns: its
  // primary key).
  const designedSchema = (doc: Json) => (typeof doc.schema === "string" ? (declared.get(doc.schema) ?? defaultSchema) : defaultSchema);
  const designed = tableDocs.sort((a, b) => String(a.id).localeCompare(String(b.id)));
  const junctionRelation = new Map(
    mappings.filter((m) => typeof m.relation === "string" && typeof m.junctionTable === "string").map((m) => [String(m.junctionTable), String(m.relation)]),
  );
  for (const doc of designed) {
    const name = String(doc.name ?? "");
    const pkColumns = ((doc.primaryKey as Json | undefined)?.columns as string[] | undefined) ?? [];
    const fkColumns = new Set(arr(doc.foreignKeys).flatMap((fk) => (fk.columns as string[] | undefined) ?? []));
    const columns = arr(doc.columns)
      .filter((c) => typeof c.id === "string")
      .map((c, i) => {
        const type = typeof c.type === "string" ? c.type : "string";
        return {
          ...toView(
            {
              name: String(c.name ?? ""),
              type,
              length: num(c.length) ?? (type === "string" ? defaultStringLength : null),
              precision: num(c.precision) ?? (type === "decimal" ? 18 : null),
              scale: num(c.scale) ?? (type === "decimal" ? 2 : null),
              nullable: c.nullable !== false,
              key: String(c.id),
              isPrimaryKey: pkColumns.includes(String(c.id)),
              isForeignKey: fkColumns.has(String(c.id)),
              unique: false,
              indexed: false,
              nativeOverride:
                typeof c.nativeType === "string"
                  ? c.nativeType
                  : variantNativeType(
                      dialect,
                      type,
                      num(c.length) ?? (type === "string" ? defaultStringLength : null),
                      c.fixedLength === true,
                      typeof c.unicode === "boolean" ? c.unicode : undefined,
                    ),
              entry: c,
            },
            i + 1,
          ),
          identity: c.generated === "identity",
          // The dialect's default SQL (or the "*" one) and the computed expression, as the file says them.
          defaultSql: (c.defaultSql as Record<string, string> | undefined)?.[dialect] ?? (c.defaultSql as Record<string, string> | undefined)?.["*"] ?? null,
          computed: typeof c.computed === "string" ? c.computed : null,
        };
      });
    const nameOf = (key: string) => columns.find((c) => c.key === key)?.name ?? key;
    const pk = doc.primaryKey as Json | undefined;
    tables.push({
      key: String(doc.id),
      name,
      schema: designedSchema(doc),
      // A relation mapping's junction table is that relation's junction (the engine marks it so).
      relationId: junctionRelation.get(String(doc.id)) ?? null,
      comment: commentOf(doc),
      columns,
      primaryKey: pkColumns.length ? { name: typeof pk?.name === "string" ? pk.name : `pk_${name}`, columns: [...pkColumns] } : null,
      uniques: arr(doc.uniques).map((u) => {
        const cols = (u.columns as string[] | undefined) ?? [];
        return { name: typeof u.name === "string" ? u.name : `uq_${name}_${cols.map(nameOf).join("_")}`, columns: [...cols] };
      }),
      foreignKeys: arr(doc.foreignKeys).map((fk) => {
        const cols = (fk.columns as string[] | undefined) ?? [];
        return {
          name: typeof fk.name === "string" ? fk.name : `fk_${name}_${cols.map(nameOf).join("_")}`,
          columns: [...cols],
          referencedTable: String(fk.referencesTable ?? ""),
          referencedColumns: [...((fk.referencesColumns as string[] | undefined) ?? [])],
          onDelete: String(fk.onDelete ?? "no-action").replace("-", " "),
          onUpdate: String(fk.onUpdate ?? "no-action").replace("-", " "),
          relationId: null,
          endId: null,
        };
      }),
      // An index column is a column, or an expression for the dialect ("*" for any) shown in parentheses; an expression without
      // a text for the dialect leaves the index out, and an expression is "expr" in a conventional name (DatabaseRun).
      indexes: arr(doc.indexes).flatMap((ix) => {
        const cols = arr(ix.columns).map((c) => {
          if (typeof c.column === "string") return { column: c.column, descending: c.descending === true, expr: false };
          const text = forDialectText(c.expression);
          return text === null ? null : { column: `(${text})`, descending: c.descending === true, expr: true };
        });
        if (!cols.length || cols.some((c) => c === null)) return [];
        const kept = cols as { column: string; descending: boolean; expr: boolean }[];
        return [
          {
            name: typeof ix.name === "string" ? ix.name : `ix_${name}_${kept.map((c) => (c.expr ? "expr" : nameOf(c.column))).join("_")}`,
            columns: kept.map((c) => ({ column: c.column, descending: c.descending })),
            unique: ix.unique === true,
            where: typeof ix.where === "string" ? ix.where : null,
          },
        ];
      }),
      ...annotationsOf(doc, stereotypes),
    });
  }
  // A relation mapping names the foreign key that stores its relation: the foreign key then carries the relation and the
  // end, as the engine derives them.
  for (const m of mappings) {
    if (typeof m.relation !== "string" || typeof m.foreignKey !== "string") continue;
    for (const t of tables) {
      const fk = t.foreignKeys.find((f) => f.name === m.foreignKey);
      if (fk) {
        fk.relationId = m.relation;
        fk.endId = typeof m.foreignKeyEnd === "string" ? m.foreignKeyEnd : null;
      }
    }
  }

  // A designed foreign key without referenced columns references the referenced table's primary key.
  for (const t of tables)
    for (const fk of t.foreignKeys)
      if (!fk.referencedColumns.length) fk.referencedColumns = [...(tables.find((x) => x.key === fk.referencedTable)?.primaryKey?.columns ?? [])];

  tables.sort((x, y) => x.name.localeCompare(y.name));
  const schemaOf = designedSchema;
  const byName = <T extends { name: string }>(x: T, y: T) => x.name.localeCompare(y.name);
  const views: ViewView[] = all
    .filter((d) => d.kind === "view" && d.database === databaseId)
    .map((d) => {
      const body = (d.body as Record<string, string> | undefined) ?? {};
      return {
        id: String(d.id),
        name: String(d.name ?? ""),
        schema: schemaOf(d),
        body: body[dialect] ?? body["*"] ?? null,
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
  // Routines, database types and SQL objects (schemas/v1/routine.json, database-type.json, sql-object.json), as the engine's
  // DatabaseViews projects them: the dialect's body (or the "*" one), a type slot as a built-in keyword or a database type's id.
  const own = (kind: string) => all.filter((d) => d.kind === kind && d.database === databaseId);
  const typeDocs = new Map(own("database-type").map((d) => [String(d.id), d]));
  const typeNativeName = (d: Json) => (typeof d.nativeName === "string" ? d.nativeName : qualifiedName(schemaOf(d), String(d.name ?? "")));
  const slot = (row: Json) => {
    const type = typeof row.type === "string" ? row.type : null;
    const builtin = type && (BUILTIN_TYPES as readonly string[]).includes(type) ? type : null;
    const dbType = type && !builtin ? typeDocs.get(type) : undefined;
    const facets = { length: num(row.length), precision: num(row.precision), scale: num(row.scale) };
    const native =
      typeof row.nativeType === "string"
        ? row.nativeType
        : builtin
          ? nativeType(dialect, builtin, { length: facets.length ?? undefined, precision: facets.precision ?? undefined, scale: facets.scale ?? undefined })
          : dbType
            ? typeNativeName(dbType)
            : "";
    return { type: builtin, dbTypeId: dbType ? String(dbType.id) : null, ...facets, nativeType: native };
  };
  const forDialect = (map: unknown): string | null => {
    const texts = (map as Record<string, string> | undefined) ?? {};
    return texts[dialect] ?? texts["*"] ?? null;
  };
  const routines: RoutineView[] = own("routine")
    .map((d) => {
      const returns = d.returns as Json | undefined;
      const body = forDialect(d.body);
      return {
        id: String(d.id),
        name: String(d.name ?? ""),
        schema: schemaOf(d),
        routineKind: d.routineKind === "procedure" ? ("procedure" as const) : ("function" as const),
        parameters: arr(d.parameters).map((p) => ({
          name: String(p.name ?? ""),
          ...slot(p),
          mode: (p.mode === "out" || p.mode === "inout" ? p.mode : "in") as RoutineView["parameters"][number]["mode"],
          default: typeof p.default === "string" ? p.default : null,
        })),
        returns: returns
          ? Array.isArray(returns.table)
            ? {
                type: null,
                dbTypeId: null,
                length: null,
                precision: null,
                scale: null,
                nativeType: null,
                table: arr(returns.table).map((c) => ({ name: String(c.name ?? ""), ...slot(c), nullable: c.nullable !== false })),
              }
            : { ...slot(returns), nativeType: slot(returns).nativeType || null, table: null }
          : null,
        language: typeof d.language === "string" ? d.language : dialect === "postgresql" ? "plpgsql" : dialect === "sqlserver" ? "tsql" : "sql",
        body,
        hasBody: body !== null,
        deterministic: d.deterministic === true,
        security: (d.security === "definer" ? "definer" : "invoker") as RoutineView["security"],
        dependsOn: ((d.dependsOn as string[] | undefined) ?? []).filter((x) => typeof x === "string"),
        comment: typeof d.comment === "string" ? d.comment : null,
        ...annotationsOf(d, stereotypes),
      };
    })
    .sort(byName);
  const types: DatabaseTypeView[] = [...typeDocs.values()]
    .map((d) => {
      const typeKind = (["domain", "composite", "enum", "range"].includes(String(d.typeKind)) ? d.typeKind : "domain") as DatabaseTypeView["typeKind"];
      const base = typeof d.base === "string" ? d.base : null;
      const subtype = typeof d.subtype === "string" ? d.subtype : null;
      const fields = arr(d.fields).map((f) => ({ name: String(f.name ?? ""), ...slot(f) }));
      // A dialect creates what it has: every kind on postgresql, an alias type (a domain) on sqlserver, nothing elsewhere.
      const isCreated = dialect === "postgresql" || (dialect === "sqlserver" && typeKind === "domain");
      return {
        id: String(d.id),
        name: String(d.name ?? ""),
        schema: schemaOf(d),
        typeKind,
        base,
        baseNativeType: base
          ? nativeType(dialect, base, { length: num(d.length) ?? undefined, precision: num(d.precision) ?? undefined, scale: num(d.scale) ?? undefined })
          : null,
        length: num(d.length),
        precision: num(d.precision),
        scale: num(d.scale),
        check: typeof d.check === "string" ? d.check : null,
        members: ((d.members as string[] | undefined) ?? []).map(String),
        fields,
        subtype,
        subtypeNativeType: subtype ? nativeType(dialect, subtype, {}) : null,
        definition: forDialect(d.definition),
        isCreated,
        nativeName: typeNativeName(d),
        dependsOn: [...new Set(fields.map((f) => f.dbTypeId).filter((x): x is string => !!x))],
        comment: typeof d.comment === "string" ? d.comment : null,
        ...annotationsOf(d, stereotypes),
      };
    })
    .sort(byName);
  const objects: SqlObjectView[] = own("sql-object")
    .map((d) => {
      const body = forDialect(d.body);
      return {
        id: String(d.id),
        name: String(d.name ?? ""),
        schema: schemaOf(d),
        objectKind: String(d.objectKind ?? ""),
        phase: (d.phase === "before" ? "before" : "after") as SqlObjectView["phase"],
        dependsOn: ((d.dependsOn as string[] | undefined) ?? []).filter((x) => typeof x === "string"),
        body,
        hasBody: body !== null,
        ...annotationsOf(d, stereotypes),
      };
    })
    .sort(byName);
  // Every schema the file declares or an object uses, by name; a declared one carries its entry's annotations.
  const entries = arr(db.schemas).filter((x) => typeof x.id === "string");
  const schemaNames = new Set<string>(entries.map((x) => String(x.name ?? "")));
  for (const o of [...tables, ...views, ...sequences, ...routines, ...types, ...objects]) if (o.schema) schemaNames.add(o.schema);
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
  const view: DatabaseView = {
    id: databaseId,
    name: dbName,
    dialect,
    version: typeof db.version === "string" ? db.version : null,
    defaultSchema,
    tables,
    views,
    sequences,
    routines,
    types,
    objects,
    schemas,
    quoting: (typeof db.quoting === "string" ? db.quoting : "reserved") as DatabaseView["quoting"],
    maxIdentifierLength: typeof db.maxIdentifierLength === "number" ? db.maxIdentifierLength : (limits[dialect] ?? null),
    ...annotationsOf(db, stereotypes),
  };
  // The queries over its tables and views (queries.ts), bound against the view just built.
  view.queries = resolveQueries(view, input.docs).queries;
  // The storage of relations with an end of several types: the view's relationStorage, and the run's findings kept for the resolver findings.
  const run = typeStorageRun(input.docs, view, conventions);
  if (run.storage.length) view.relationStorage = run.storage;
  runDiagnostics.set(view, run.diagnostics);
  return view;
}

const runDiagnostics = new WeakMap<DatabaseView, Diagnostic[]>();
/** The storage run's findings for relations with an end of several types (typeStorage.ts), for a view `resolveDatabase` built. */
export function typeStorageDiagnostics(view: DatabaseView): Diagnostic[] {
  return runDiagnostics.get(view) ?? [];
}

const qualifiedName = (schema: string | null, name: string) => (schema ? `${schema}.${name}` : name);

function stripNull(o: Json): Json {
  return Object.fromEntries(Object.entries(o).filter(([, v]) => v !== null && v !== undefined));
}

/** A column's physical type for a message, as the engine says it: `string(26) (varchar(26))`. */
function describeColumn(c: ColumnView): string {
  const facets =
    ["string", "text", "binary"].includes(c.type) && c.length !== null
      ? `(${c.length})`
      : c.type === "decimal" && c.precision !== null
        ? `(${c.precision}${c.scale !== null ? `,${c.scale}` : ""})`
        : ["time", "datetime", "datetimeoffset"].includes(c.type) && c.precision !== null
          ? `(${c.precision})`
          : "";
  return `${c.type}${facets} (${c.nativeType})`;
}

function samePhysicalType(a: ColumnView, b: ColumnView): boolean {
  if (a.type !== b.type || a.nativeType.toLowerCase() !== b.nativeType.toLowerCase()) return false;
  return (
    (!["string", "text", "binary"].includes(a.type) || a.length === b.length) &&
    (!["decimal", "time", "datetime", "datetimeoffset"].includes(a.type) || a.precision === b.precision) &&
    (a.type !== "decimal" || a.scale === b.scale)
  );
}

/**
 * The resolved MQ4005 (DatabaseRun.Finish.cs): a foreign key column whose resolved type, facets or native type differ from
 * the referenced column's, reported on the overlay entry that pins it (else the table's overlay, else no file). The
 * attribute's own type and length are never compared with the column's.
 */
export function foreignKeyMismatches(view: DatabaseView, docs: ReadonlyMap<string, Json>): Diagnostic[] {
  const out: Diagnostic[] = [];
  for (const t of view.tables) {
    for (const fk of t.foreignKeys) {
      const target = view.tables.find((x) => x.key === fk.referencedTable);
      if (!target) continue;
      fk.columns.forEach((key, i) => {
        const column = t.columns.find((c) => c.key === key);
        const referenced = target.columns.find((c) => c.key === fk.referencedColumns[i]);
        if (!column || !referenced || samePhysicalType(column, referenced)) return;
        const file = docs.get(t.key);
        const at = arr(file?.columns).findIndex((c) => c.id === key);
        out.push({
          rule: "MQ4005",
          severity: "error",
          message: `Foreign key column '${t.name}.${column.name}' is ${describeColumn(column)} but references '${target.name}.${referenced.name}', which is ${describeColumn(referenced)} (database '${view.name}').`,
          elementId: file ? String(file.id) : null,
          filePath: null,
          jsonPointer: file && at >= 0 ? `/columns/${at}` : null,
          line: null,
          column: null,
        });
      });
    }
  }
  return out;
}
