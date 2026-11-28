// The mock's table designer for "Create tables" (materialize-tables, -columns): the shape a table takes for an entity, as the
// engine's table designer lays it out (hierarchy and list choices aside, which bindings.ts applies). It only shapes the
// entities it is asked about, and never writes or places anything: nothing in a model is laid out from entities.
import type { ColumnView, DatabaseView, ForeignKeyView, TableView } from "@/api/types";
import { docEndSingleEntity, type EndDocLike } from "@/model/endTypes";
import { schemasOf } from "@/model/databaseSchemas";
import {
  annotationsOf,
  applyCase,
  descriptionText,
  nativeType,
  plainColumn,
  plural,
  renderNativePattern,
  resolveDatabase,
  type PhysicalInput,
} from "./physical";

type Json = Record<string, unknown>;
const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

/** A shaped column: the resolved column and the attribute path it stores. */
export type ShapeColumn = ColumnView & { attributeId: string | null; attributePath: string | null };
/** A shaped table: the entity (or relation) it is the shape of. */
export type ShapeTable = Omit<TableView, "columns"> & { columns: ShapeColumn[]; entityId: string | null; isJunction: boolean };

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
  entry?: Json;
  attributeDescription?: string | null;
}

/** The tables the picked entities would take in a database: one table per entity, a foreign key per relation, a junction table per many-to-many. */
export function shapeTables(input: PhysicalInput, databaseId: string, picked: readonly string[]): { tables: ShapeTable[] } {
  const db = input.docs.get(databaseId);
  if (!db || db.kind !== "database") return { tables: [] };
  const existing: DatabaseView | null = resolveDatabase(input, databaseId);
  const dialect = String(db.dialect ?? "postgresql");
  const dbName = String(db.name ?? "");
  const conventions = {
    ...input.conventions,
    ...Object.fromEntries(Object.entries(input.databaseConventions[dbName] ?? {}).filter(([, v]) => v !== null && v !== undefined)),
  };
  const tableCase = String(conventions.tableCase ?? "snake");
  const columnCase = String(conventions.columnCase ?? "snake");
  const pluralTables = conventions.pluralTables !== false;
  const enumStorage = String(conventions.enumStorage ?? "int");
  const defaultStringLength = typeof conventions.defaultStringLength === "number" ? conventions.defaultStringLength : 255;
  const defaultSchema = typeof db.defaultSchema === "string" ? db.defaultSchema : null;
  const commentsFromDescriptions = conventions.comments !== "none";
  const commentOf = (entry: Json | undefined, ...fallbacks: (string | null | undefined)[]): string | null =>
    typeof entry?.comment === "string"
      ? entry.comment
      : commentsFromDescriptions
        ? (descriptionText(entry) ?? fallbacks.find((f): f is string => typeof f === "string" && f.length > 0) ?? null)
        : null;
  const all = [...input.docs.values()];
  const declared = new Map(schemasOf(db).map((x) => [x.id, x.name]));
  void declared;
  const entities = all.filter((d) => d.kind === "entity" && picked.includes(String(d.id))).sort((a, b) => String(a.name).localeCompare(String(b.name)));
  const entityIds = new Set(entities.map((e) => String(e.id)));
  const stereotypes = new Map(all.filter((d) => d.kind === "stereotype").map((s) => [String(s.key), s]));
  const mappings = all.filter((d) => d.kind === "mapping" && d.database === databaseId);
  const colName = (n: string) => applyCase(n, columnCase);
  const tableName = (entityName: string) => applyCase(pluralTables ? plural(entityName) : entityName, tableCase);

  const tables: ShapeTable[] = [];
  const pkColumnsByEntity = new Map<string, { table: ShapeTable; columns: ShapeColumn[] }>();

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
          attributeDescription: descriptionText(attr),
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
          attributeDescription: descriptionText(attr),
        },
      ];
    }
    if (target.kind === "scalar-type") {
      const base = String(target.base);
      const length = (target.length as number | undefined) ?? (base === "string" ? defaultStringLength : null);
      const precision = (target.precision as number | undefined) ?? null;
      const scale = (target.scale as number | undefined) ?? null;
      // A custom type's own native type for the dialect replaces the base's type map entry (as the engine's resolver does).
      const own = (target.nativeTypes as Record<string, string> | undefined)?.[dialect];
      return [
        {
          name: colName(prefix + String(attr.name)),
          type: base,
          length,
          precision,
          scale,
          nativeOverride: own === undefined ? undefined : renderNativePattern(own, base, { length, precision, scale }),
          nullable: !required,
          attributeId: attrId,
          attributePath: path,
          key: path,
          unique: attr.unique === true,
          indexed: attr.indexed === true,
          attributeDescription: descriptionText(attr),
        },
      ];
    }
    return [];
  };

  const toView = (c: ResolvedColumn, position: number): ShapeColumn => ({
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
    position,
    ...plainColumn(c.entry, stereotypes),
    comment: commentOf(c.entry, c.attributeDescription),
  });

  for (const entity of entities) {
    const id = String(entity.id);
    const keyIds = new Set(((entity.key as Json | undefined)?.attributes as string[] | undefined) ?? []);
    const identity = (entity.key as Json | undefined)?.strategy === "database-identity";
    const attributes = [...arr(entity.attributes)];
    for (const skey of (entity.stereotypes as string[] | undefined) ?? []) attributes.push(...arr(stereotypes.get(skey)?.attributes));
    const columns: ResolvedColumn[] = [];
    for (const attr of attributes) {
      if (attr.collection === true) continue;
      const produced = scalarColumns(attr, "", String(attr.id), String(attr.id), attr.required === true, {});
      for (const c of produced) columns.push({ ...c, isPrimaryKey: keyIds.has(String(attr.id)), isForeignKey: false });
    }
    const name = tableName(String(entity.name));
    const table: ShapeTable = {
      key: `${id}@${databaseId}`,
      name,
      schema: defaultSchema,
      entityId: id,
      relationId: null,
      isJunction: false,
      comment: commentOf(undefined, descriptionText(entity)),
      columns: [],
      primaryKey: null,
      uniques: [],
      foreignKeys: [],
      indexes: [],
      ...annotationsOf(undefined, stereotypes),
    };
    table.columns = columns.map((c, i) => ({ ...toView(c, i + 1), identity: identity && c.isPrimaryKey }));
    const pk = table.columns.filter((c) => c.isPrimaryKey);
    table.primaryKey = pk.length ? { name: `pk_${name}`, columns: pk.map((c) => c.key) } : null;
    table.uniques = columns.filter((c) => c.unique).map((c) => ({ name: `uq_${name}_${c.name}`, columns: [c.key] }));
    table.indexes = columns
      .filter((c) => c.indexed)
      .map((c) => ({ name: `ix_${name}_${c.name}`, columns: [{ column: c.key, descending: false }], unique: false, where: null }));
    tables.push(table);
    pkColumnsByEntity.set(id, { table, columns: table.columns.filter((c) => c.isPrimaryKey) });
  }

  // An entity bound to one of the database's table files (erratum E43) has no table laid out for it, but the foreign keys of
  // the tables still laid out reference its file's primary key, as the engine's bindings resolve them.
  const boundTo = new Map<string, string>();
  // A bound key column's attribute (the binding's field map), so a foreign key column naming it keeps its key.
  const boundAttribute = new Map<string, string>();
  for (const d of all)
    if (d.kind === "entity")
      for (const b of arr(d.bindings))
        if (b.database === databaseId && typeof b.source === "string") {
          // Its rows live in the write table, else the source (DatabaseRun.BindingHost); a read-only binding has no host.
          const write = b.write as Json | string | undefined;
          if (write && typeof write === "object" && write.none === true) continue;
          boundTo.set(String(d.id), write && typeof write === "object" && typeof write.table === "string" ? write.table : b.source);
          for (const f of arr(b.fields)) if (typeof f.column === "string" && typeof f.attribute === "string") boundAttribute.set(f.column, f.attribute);
        }
  const stableKey = (column: string) => boundAttribute.get(column) ?? column;
  for (const [entity, source] of boundTo) {
    const stored = existing?.tables.find((t) => t.key === source);
    if (!stored) continue;
    // A stored table's columns are shaped like the rest; only its primary key is read.
    const file: ShapeTable = {
      ...stored,
      entityId: null,
      isJunction: false,
      columns: stored.columns.map((c) => ({ ...c, attributeId: null, attributePath: null })),
    };
    pkColumnsByEntity.set(entity, { table: file, columns: file.columns.filter((c) => c.isPrimaryKey) });
  }

  // Relations: a foreign key on the "many" side, or a junction table for many-to-many.
  const relations = all.filter((d) => d.kind === "relation").sort((a, b) => String(a.id).localeCompare(String(b.id)));
  for (const relation of relations) {
    // A `types` list of one entity is a one-type end (the same meaning as `entity`); an end with several types is stored nowhere.
    const ends = arr(relation.ends).map((e): Json => ({ ...e, entity: docEndSingleEntity(e as EndDocLike) ?? undefined }));
    if (ends.length !== 2) continue;
    const [a, b] = ends;
    const known = (id: unknown) => entityIds.has(String(id)) || pkColumnsByEntity.has(String(id));
    if (!known(a.entity) || !known(b.entity)) continue;
    // Only a relation with an end at a picked entity gets storage here.
    if (!entityIds.has(String(a.entity)) && !entityIds.has(String(b.entity))) continue;
    const relationMapping = mappings.find((m) => m.relation === relation.id);
    // A mapping that names a foreign key or a junction table of its own leaves nothing to lay out.
    if (typeof relationMapping?.junctionTable === "string" || typeof relationMapping?.foreignKey === "string") continue;
    const manyA = (a.max ?? "*") === "*";
    const manyB = (b.max ?? "*") === "*";
    const onDelete = (end: Json) => ({ cascade: "cascade", restrict: "restrict", "set-null": "set null" })[String(end.onDelete)] ?? "no action";
    const addForeignKey = (holder: Json, referenced: Json, end: Json, nullable: boolean, ordered: boolean) => {
      // A bound holder's foreign key is in its table file already.
      if (boundTo.has(String(holder.entity))) return;
      const from = pkColumnsByEntity.get(String(holder.entity));
      const to = pkColumnsByEntity.get(String(referenced.entity));
      if (!from || !to) return;
      const role = String(referenced.role ?? referenced.navigation ?? "ref");
      // A foreign key column copies the referenced key column's type, facets and native type.
      const fkColumns: ShapeColumn[] = to.columns.map((pk) => {
        const key = `${String(referenced.id)}.${stableKey(pk.key)}`;
        const c = { ...pk, key, name: colName(`${role}_${pk.name}`), nullable };
        return {
          ...c,
          nativeType: pk.nativeType,
          identity: false,
          isPrimaryKey: false,
          isForeignKey: true,
          attributeId: null,
          attributePath: null,
          position: from.table.columns.length + 1,
          ...plainColumn(undefined, stereotypes),
          comment: commentOf(undefined),
        };
      });
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
      const junction: ShapeTable = {
        key,
        name,
        schema: defaultSchema,
        entityId: null,
        relationId: String(relation.id),
        isJunction: true,
        comment: commentOf(undefined, descriptionText(relation)),
        columns: [],
        primaryKey: null,
        uniques: [],
        foreignKeys: [],
        indexes: [],
        ...annotationsOf(undefined, stereotypes),
      };
      const sides = [
        { end: a, target: left, label: leftName },
        { end: b, target: right, label: rightName },
      ];
      for (const side of sides) {
        const cols = side.target.columns.map((pk) => ({
          ...pk,
          key: `${String(side.end.id)}.${stableKey(pk.key)}`,
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

  return { tables };
}
