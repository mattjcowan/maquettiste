// Database schemas (erratum E26): a database declares its schemas ({ id, name }) and names its default by name
// (`defaultSchema`); tables, views, sequences, routines, database types and SQL objects name a schema by id. The four
// schema operations of a batch (add-schema, rename-schema, remove-schema, set-default-schema) run on the server; the
// mock server runs `applySchemaOperation` here. Free of React.

type Json = Record<string, unknown>;

export interface DbSchema {
  id: string;
  name: string;
}

export type SchemaOp =
  | { op: "add-schema"; id: string; name: string; schema?: string }
  | { op: "rename-schema"; id: string; schema: string; name: string }
  | { op: "remove-schema"; id: string; schema: string; target?: string; default?: string }
  | { op: "set-default-schema"; id: string; schema: string };

export const SCHEMA_OPS: ReadonlySet<string> = new Set(["add-schema", "rename-schema", "remove-schema", "set-default-schema"]);

/** The schemas a database document declares. */
export function schemasOf(db: Json | null | undefined): DbSchema[] {
  const list = Array.isArray(db?.schemas) ? (db.schemas as Json[]) : [];
  return list.filter((s) => typeof s?.id === "string").map((s) => ({ id: String(s.id), name: String(s.name ?? "") }));
}

/** The dialect's default schema name, when it has one. */
export function dialectDefault(dialect: unknown): string | null {
  return dialect === "postgresql" ? "public" : dialect === "sqlserver" ? "dbo" : null;
}

/** The default schema's name: `defaultSchema`, else the dialect's. */
export function defaultSchemaName(db: Json | null | undefined): string | null {
  return typeof db?.defaultSchema === "string" ? db.defaultSchema : dialectDefault(db?.dialect);
}

/** The declared schema that is the default, if any. */
export function defaultSchemaId(db: Json | null | undefined): string | null {
  const name = defaultSchemaName(db);
  return schemasOf(db).find((s) => s.name === name)?.id ?? null;
}

/** The schema name a schema id stands for; null (the default) when it is not declared. */
export function schemaNameOf(db: Json, schemaId: string | null | undefined): string | null {
  return (schemaId && schemasOf(db).find((s) => s.id === schemaId)?.name) || defaultSchemaName(db);
}

const SCHEMA_KINDS: ReadonlySet<string> = new Set(["table", "view", "sequence", "routine", "database-type", "sql-object"]);

/** What lives in a schema: tables, views, sequences, routines, database types and SQL objects of the database. */
export function occupantsOf(docs: Iterable<Json>, db: Json, schemaId: string): { id: string; label: string }[] {
  const out: { id: string; label: string }[] = [];
  const dbId = String(db.id);
  for (const d of docs) {
    if (!SCHEMA_KINDS.has(String(d.kind))) continue;
    if (d.database === dbId && d.schema === schemaId) out.push({ id: String(d.id), label: `${String(d.kind)} ${String(d.name ?? d.id)}` });
  }
  return out;
}

/**
 * Runs one schema operation on documents (the mock server's handler; the engine's is ModelStore.Schemas.cs): returns the
 * changed documents, or why it is refused (MQ4015).
 */
export function applySchemaOperation(docs: ReadonlyMap<string, Json>, o: SchemaOp, newId: () => string): { changed: Map<string, Json> } | { error: string } {
  const source = docs.get(o.id);
  if (!source || source.kind !== "database") return { error: `'${o.id}' is not a database.` };
  const changed = new Map<string, Json>();
  const db = structuredClone(source);
  changed.set(o.id, db);
  const schemas = (Array.isArray(db.schemas) ? db.schemas : (db.schemas = [])) as Json[];
  const dbName = String(db.name ?? o.id);
  const find = (id: string | undefined) => (id ? schemas.find((s) => s.id === id) : undefined);
  const taken = (name: string, except?: string) => schemas.some((s) => s.id !== except && String(s.name).toLowerCase() === name.toLowerCase());
  switch (o.op) {
    case "add-schema": {
      if (!o.name?.trim()) return { error: "A new schema needs a name." };
      if (taken(o.name)) return { error: `Database '${dbName}' already has a schema named '${o.name}'.` };
      schemas.push({ id: o.schema ?? newId(), name: o.name });
      return { changed };
    }
    case "rename-schema": {
      const s = find(o.schema);
      if (!s) return { error: `Database '${dbName}' has no schema '${o.schema}'.` };
      if (!o.name?.trim()) return { error: "A schema needs a name." };
      if (taken(o.name, o.schema)) return { error: `Database '${dbName}' already has a schema named '${o.name}'.` };
      if (db.defaultSchema === s.name) db.defaultSchema = o.name;
      s.name = o.name;
      return { changed };
    }
    case "set-default-schema": {
      const s = find(o.schema);
      if (!s) return { error: `Database '${dbName}' has no schema '${o.schema}'.` };
      db.defaultSchema = s.name;
      return { changed };
    }
    case "remove-schema": {
      const s = find(o.schema);
      if (!s) return { error: `Database '${dbName}' has no schema '${o.schema}'.` };
      let newDefault: Json | undefined;
      if (db.defaultSchema === s.name) {
        newDefault = find(o.default);
        if (!newDefault || newDefault === s)
          return { error: `Schema '${String(s.name)}' is the default of database '${dbName}'; name another schema as the default to remove it.` };
      }
      const target = o.target ? find(o.target) : undefined;
      if (o.target && (!target || target === s)) return { error: `The target schema '${o.target}' is not another schema of database '${dbName}'.` };
      const occupants = occupantsOf(docs.values(), db, o.schema);
      if (occupants.length && !target)
        return {
          error: `Schema '${String(s.name)}' of database '${dbName}' still holds ${occupants.map((x) => x.label).join(", ")}; move them or name a target schema.`,
        };
      for (const x of occupants) {
        if (docs.get(x.id)?.database === o.id) {
          const doc = structuredClone(docs.get(x.id)!);
          doc.schema = o.target;
          changed.set(x.id, doc);
        }
      }
      if (newDefault) db.defaultSchema = newDefault.name;
      db.schemas = schemas.filter((x) => x !== s);
      return { changed };
    }
  }
}
