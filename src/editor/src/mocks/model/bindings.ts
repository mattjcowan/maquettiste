// The mock's entity bindings and materialize (erratum E43, engine-design.md 7 "Bindings and materialize"), enough for the
// editor's Storage tab and its bulk actions and the Database screen's Store as table files: the materialize status of a
// database, the plans of materialize-tables, materialize-entities, materialize-attributes and materialize-columns (the preview, and the batch operations that write them),
// the five statements of a binding for a dialect (simple SQL, not the engine's renderer), and the binding rules the editor
// shows inline (MQ4044 to MQ4048, MQ4050, and the proposed MQ4058). It shares the field map model with the editor (editors/storage/fieldMap.ts).
import type { DatabaseView, Diagnostic, MaterializeChange, MaterializePlan, MaterializeStatus, BindingSqlResult, TableView } from "@/api/types";
import { bindingProblems, columnRows, findColumn, sourceOf, storageAttributes, writes, type Binding } from "@/editors/storage/fieldMap";
import { applyCase, resolveDatabase, type PhysicalInput } from "./physical";

type Json = Record<string, unknown>;
const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T;

export interface BindingInput extends PhysicalInput {
  newId: () => string;
  /** A document's repo-relative path today, for the plan's changes (none for a create). */
  pathOf?: (id: string) => string | null;
}

const bindingsOf = (doc: Json | undefined): Binding[] => (Array.isArray(doc?.bindings) ? (doc!.bindings as Binding[]) : []);
const entities = (docs: ReadonlyMap<string, Json>) => [...docs.values()].filter((d) => d.kind === "entity");
const byName = (a: Json, b: Json) => String(a.name ?? "").localeCompare(String(b.name ?? "")) || String(a.id).localeCompare(String(b.id));

/** The entities bound to a table or view (as source or write table), with their constants. */
function bindersOf(docs: ReadonlyMap<string, Json>, id: string) {
  return entities(docs)
    .sort(byName)
    .flatMap((e) =>
      bindingsOf(e)
        .filter((b) => b.source === id || (typeof b.write === "object" && b.write?.table === id))
        .map((b) => ({
          entity: String(e.id),
          entityName: String(e.name ?? ""),
          binding: b.id,
          constants: (b.constants ?? []).map((c) => (c.value === undefined || c.value === null ? { column: c.column } : { column: c.column, value: c.value })),
        })),
    );
}

/** GET /api/model/databases/{id}/materialize. */
export function materializeStatus(input: PhysicalInput, databaseId: string): MaterializeStatus | null {
  const db = input.docs.get(databaseId);
  if (!db || db.kind !== "database") return null;
  const view = resolveDatabase(input, databaseId);
  return {
    database: databaseId,
    databaseName: String(db.name ?? ""),
    entities: entities(input.docs)
      .filter((e) => !bindingsOf(e).some((b) => b.database === databaseId))
      .sort(byName)
      .map((e) => {
        // The entity's own projected table (not an attribute's or a junction), by name, as the engine says it.
        const table = view?.tables.find((t) => t.origin === "synthesized" && t.key === `${String(e.id)}@${databaseId}`);
        return {
          id: String(e.id),
          name: String(e.name ?? ""),
          package: typeof e.package === "string" ? e.package : null,
          projected: !!table,
          table: table?.name ?? null,
        };
      }),
    sources: [
      ...(view?.tables ?? [])
        .filter((t) => t.origin !== "synthesized")
        .map((t) => ({ id: t.key, kind: "table" as const, name: t.name, schema: t.schema, origin: t.origin, boundBy: bindersOf(input.docs, t.key) })),
      ...(view?.views ?? []).map((v) => ({
        id: v.id,
        kind: "view" as const,
        name: v.name,
        schema: v.schema,
        origin: "designed",
        boundBy: bindersOf(input.docs, v.id),
      })),
    ],
  };
}

// ------------------------------------------------------------------ materialize plans

export interface MaterializeRequest {
  op: string;
  entities?: string[] | null;
  tables?: string[] | null;
  schema?: string | null;
  package?: string | null;
  columns?: string[] | null;
  attributes?: string[] | null;
}

export interface MaterializeWrites {
  creates: Json[];
  updates: Json[];
  deletes: string[];
}

const refusal = (message: string, elementId: string | null): Diagnostic => ({
  rule: "MQ4055",
  severity: "error",
  message,
  elementId,
  filePath: null,
  jsonPointer: null,
  line: null,
  column: null,
});

/** The plan of a materialize operation over a database, and the documents it writes (the batch applies the same). */
export function planMaterialize(input: BindingInput, databaseId: string, request: MaterializeRequest): { plan: MaterializePlan; writes: MaterializeWrites } {
  const out =
    request.op === "materialize-entities"
      ? materializeEntities(input, databaseId, request)
      : request.op === "materialize-attributes"
        ? materializeAttributes(input, databaseId, request)
        : request.op === "materialize-columns"
          ? materializeColumns(input, databaseId, request)
          : materializeTables(input, databaseId, request);
  const pathOf = (id: string) => {
    const path = input.docs.has(id) ? input.pathOf?.(id) : null;
    return path ? { path } : {};
  };
  const change = (json: Json, because: string): MaterializeChange => ({
    id: String(json.id),
    kind: String(json.kind),
    name: String(json.name ?? json.id),
    ...pathOf(String(json.id)),
    because,
    element: json,
  });
  const plan: MaterializePlan = {
    operation: request.op,
    database: databaseId,
    valid: out.diagnostics.length === 0,
    creates: out.writes.creates.map((j) => change(j, out.because.get(String(j.id)) ?? "created")),
    updates: out.writes.updates.map((j) => change(j, out.because.get(String(j.id)) ?? "updated")),
    deletes: out.writes.deletes.map((id) => {
      const doc = input.docs.get(id);
      return { id, kind: String(doc?.kind ?? ""), name: String(doc?.name ?? id), ...pathOf(id), because: out.because.get(id) ?? "deleted" };
    }),
    notes: out.notes,
    diagnostics: out.diagnostics,
  };
  return { plan, writes: out.diagnostics.length ? { creates: [], updates: [], deletes: [] } : out.writes };
}

interface Outcome {
  writes: MaterializeWrites;
  because: Map<string, string>;
  notes: string[];
  diagnostics: Diagnostic[];
}

const ANNOTATIONS = ["displayName", "pluralName", "description", "stereotypes", "tags", "category", "properties", "generation"] as const;
/** The column facets an overlay entry may set that the resolved column view does not carry (the DDL facets among them). */
const COLUMN_FACETS = ["unicode", "fixedLength", "defaultName", "sequence"] as const;
const actionKeyword = (text: string): string => text.trim().toLowerCase().replace(/\s+/g, "-") || "no-action";

/**
 * materialize-tables (Materializer.TablesAsync): every picked entity projected into the database, then per entity a designed
 * table with the projected table's exact shape (the overlay, when there is one, becomes the table and keeps its id and column
 * ids), a binding with every column mapped, its mapping to the database deleted, a relation mapping naming each foreign key now
 * in a designed table, and what named the projected table by its key follows it: the database's queries (sources and
 * `alias.<column key>`), other table files' foreign keys. Serves the entity side's Create tables and the database side's Store as
 * table files alike.
 */
function materializeTables(input: BindingInput, databaseId: string, request: MaterializeRequest): Outcome {
  const { docs } = input;
  const db = docs.get(databaseId);
  const out: Outcome = { writes: { creates: [], updates: [], deletes: [] }, because: new Map(), notes: [], diagnostics: [] };
  if (!db || db.kind !== "database") {
    out.diagnostics.push(refusal(`'${databaseId}' is not a database.`, databaseId));
    return out;
  }
  if (request.schema && !arr(db.schemas).some((s) => s.id === request.schema)) {
    out.diagnostics.push(refusal(`Database '${String(db.name)}' has no schema '${request.schema}'.`, databaseId));
    return out;
  }
  const picked = [...new Set(request.entities ?? [])];
  if (!picked.length) {
    out.diagnostics.push(refusal("Name at least one entity to materialize.", databaseId));
    return out;
  }
  const derived = new Set(entities(docs).flatMap((e) => (typeof e.base === "string" ? [e.base] : [])));
  for (const id of picked) {
    const e = docs.get(id);
    if (!e || e.kind !== "entity") out.diagnostics.push(refusal(`'${id}' is not an entity.`, id));
    else if (bindingsOf(e).some((b) => b.database === databaseId))
      out.diagnostics.push(refusal(`${String(e.name)} is already bound to ${String(db.name)}.`, id));
    else if (e.abstract === true) out.diagnostics.push(refusal(`${String(e.name)} is abstract: it has no table of its own.`, id));
    else if (typeof e.base === "string" || derived.has(id))
      out.diagnostics.push(refusal(`${String(e.name)} is in an inheritance hierarchy: bind it by hand on its Storage tab.`, id));
  }
  if (out.diagnostics.length) return out;
  // Every picked entity projected into the database (a mapping added in memory, an ignoring one made placing).
  const all = [...docs.values()];
  const projected = new Map(docs);
  const mappingOf = (entity: string) => all.find((d) => d.kind === "mapping" && d.database === databaseId && d.entity === entity);
  for (const id of picked) {
    const m = mappingOf(id);
    if (m) projected.set(String(m.id), { ...m, ignore: false });
    else projected.set(`tmp-${id}`, { kind: "mapping", id: `tmp-${id}`, name: "tmp", database: databaseId, entity: id });
  }
  const view = resolveDatabase({ ...input, docs: projected }, databaseId);
  const tableOf = new Map<string, TableView>();
  for (const id of picked) {
    const t = view?.tables.find((x) => x.key === `${id}@${databaseId}`);
    if (!t) out.diagnostics.push(refusal(`${String(docs.get(id)?.name)} has no table of its own in ${String(db.name)}.`, id));
    else tableOf.set(id, t);
  }
  if (out.diagnostics.length) return out;
  const schemaId = (name: string | null) => arr(db.schemas).find((s) => s.name === name)?.id as string | undefined;
  const overlayOf = (entity: string) =>
    all.find((d) => d.kind === "table" && d.database === databaseId && d.origin === "synthesized" && d.entity === entity && !d.attribute && !d.relation);
  // Each picked table's new id (its overlay's, else a new one) and its column ids (the overlay entries', else new), drawn
  // before any table is written so the foreign keys between them name each other's files and columns.
  const stored = new Map<string, { id: string; columns: Map<string, string> }>();
  for (const id of picked) {
    const t = tableOf.get(id)!;
    const overlay = overlayOf(id);
    const entries = new Map(arr(overlay?.columns).map((c) => [String(c.attribute ?? ""), c]));
    const columns = new Map(t.columns.map((c) => [c.key, String(entries.get(c.key)?.id ?? input.newId())]));
    stored.set(t.key, { id: String(overlay?.id ?? input.newId()), columns });
  }
  const relationMappings = new Map<string, Json>();
  for (const id of picked) {
    const entity = docs.get(id)!;
    const t = tableOf.get(id)!;
    const overlay = overlayOf(id);
    const { id: tableId, columns: colId } = stored.get(t.key)!;
    const entries = new Map(arr(overlay?.columns).map((c) => [String(c.attribute ?? ""), c]));
    const ids = (keys: readonly string[]) => keys.map((k) => colId.get(k) ?? k);
    const columns = t.columns.map((c) => {
      const entry = entries.get(c.key);
      const col: Json = { id: colId.get(c.key)!, name: c.name, type: c.type };
      if (c.length !== null) col.length = c.length;
      if (c.precision !== null) col.precision = c.precision;
      if (c.scale !== null) col.scale = c.scale;
      if (typeof entry?.nativeType === "string") col.nativeType = entry.nativeType;
      if (!c.nullable) col.nullable = false;
      if (c.default !== null && c.default !== undefined) col.default = clone(c.default);
      if (c.defaultSql) col.defaultSql = { "*": c.defaultSql };
      if (c.identity) col.generated = "identity";
      if (c.computed) col.computed = c.computed;
      if (c.collation) col.collation = c.collation;
      if (c.comment) col.comment = c.comment;
      for (const k of COLUMN_FACETS) if (entry?.[k] !== undefined) col[k] = clone(entry[k]);
      for (const k of ANNOTATIONS) if (entry?.[k] !== undefined) col[k] = clone(entry[k]);
      return col;
    });
    const doc: Json = { kind: "table", id: tableId, name: t.name, database: databaseId };
    for (const k of ANNOTATIONS) if (overlay?.[k] !== undefined) doc[k] = clone(overlay[k]);
    const schema = request.schema ?? (typeof overlay?.schema === "string" ? overlay.schema : schemaId(t.schema));
    if (schema) doc.schema = schema;
    if (t.comment) doc.comment = t.comment;
    doc.columns = columns;
    if (t.primaryKey) doc.primaryKey = { name: t.primaryKey.name, columns: ids(t.primaryKey.columns) };
    if (t.uniques.length) doc.uniques = t.uniques.map((u) => ({ id: input.newId(), name: u.name, columns: ids(u.columns) }));
    const foreignKeys = t.foreignKeys.map((fk) => {
      const target = stored.get(fk.referencedTable);
      // A key to a table another entity still projects names that table's key until it is materialized too.
      if (!target && fk.referencedTable.includes("@"))
        out.notes.push(`Foreign key '${fk.name}' of table '${t.name}' references a table not stored as a file yet: it names that table by its key.`);
      const fid = input.newId();
      const json: Json = {
        id: fid,
        name: fk.name,
        columns: ids(fk.columns),
        referencesTable: target?.id ?? fk.referencedTable,
        referencesColumns: fk.referencedColumns.map((c) => target?.columns.get(c) ?? c),
      };
      if (actionKeyword(fk.onDelete) !== "no-action") json.onDelete = actionKeyword(fk.onDelete);
      if (actionKeyword(fk.onUpdate) !== "no-action") json.onUpdate = actionKeyword(fk.onUpdate);
      if (fk.relationId) relationMappings.set(fk.relationId, { foreignKey: fid });
      return json;
    });
    if (foreignKeys.length) doc.foreignKeys = foreignKeys;
    if (arr(overlay?.checks).length) doc.checks = clone(overlay!.checks);
    if (t.indexes.length)
      doc.indexes = t.indexes.map((x) => {
        const entry: Json = {
          id: input.newId(),
          name: x.name,
          columns: x.columns.map((c) =>
            c.descending ? { column: colId.get(c.column) ?? c.column, descending: true } : { column: colId.get(c.column) ?? c.column },
          ),
        };
        if (x.unique) entry.unique = true;
        if (x.where) entry.where = x.where;
        return entry;
      });
    // The binding: every column mapped, a foreign key's single column to its end, the rest ignored.
    const fields: Json[] = [];
    const listed: Json[] = [];
    for (const c of t.columns) {
      const cid = colId.get(c.key)!;
      if (c.attributePath) fields.push({ attribute: c.attributePath, column: cid });
      else {
        const fk = t.foreignKeys.find((f) => f.columns.length === 1 && f.columns[0] === c.key && f.endId);
        if (fk) fields.push({ attribute: fk.endId!, column: cid });
        else {
          listed.push({ column: cid, status: "ignored" });
          out.notes.push(`${t.name}.${c.name}: no attribute of ${String(entity.name)} holds it, so the binding lists it as ignored.`);
        }
      }
    }
    const binding: Json = { id: input.newId(), database: databaseId, source: tableId, fields };
    if (listed.length) binding.columns = listed;
    const updated = clone(entity);
    updated.bindings = [...bindingsOf(entity), binding];
    out.writes.updates.push(updated);
    out.because.set(id, `bound to ${t.name}`);
    if (overlay) {
      out.writes.updates.push(doc);
      out.because.set(tableId, `the overlay of ${String(entity.name)} becomes the designed table ${t.name}`);
    } else {
      out.writes.creates.push(doc);
      out.because.set(tableId, `the designed table of ${String(entity.name)}`);
    }
    const mapping = mappingOf(id);
    if (mapping) {
      out.writes.deletes.push(String(mapping.id));
      out.because.set(String(mapping.id), `${String(entity.name)} is bound to ${String(db.name)} now`);
    }
  }
  // Relations whose foreign key is now in a designed table name it in their relation mapping.
  for (const [relation, patch] of relationMappings) {
    const existing = all.find((d) => d.kind === "mapping" && d.database === databaseId && d.relation === relation);
    const rel = docs.get(relation);
    if (existing) {
      const next = clone(existing);
      delete next.shape;
      Object.assign(next, patch);
      out.writes.updates.push(next);
      out.because.set(String(next.id), "names the foreign key of the new table");
    } else {
      const json: Json = {
        kind: "mapping",
        id: input.newId(),
        name: `${String(rel?.name ?? "relation")} in ${String(db.name)}`,
        database: databaseId,
        relation,
        ...patch,
      };
      out.writes.creates.push(json);
      out.because.set(String(json.id), "names the foreign key of the new table");
    }
  }
  const written = new Set([...out.writes.creates, ...out.writes.updates].map((d) => String(d.id)));
  // The database's queries read the stored tables by their files, and their columns by the files' column ids.
  const storedOf = (source: unknown) => {
    if (typeof source !== "string") return undefined;
    const direct = stored.get(source);
    if (direct) return direct;
    // A source naming the entity or its overlay names the same projected table.
    const id = picked.find((p) => p === source || String(overlayOf(p)?.id ?? "") === source);
    return id ? stored.get(`${id}@${databaseId}`) : undefined;
  };
  for (const q of all) {
    if (q.kind !== "query" || q.database !== databaseId || written.has(String(q.id))) continue;
    const next = clone(q);
    const aliases = new Map<string, { id: string; columns: Map<string, string> }>();
    let touched = false;
    const sources = (node: unknown): void => {
      if (Array.isArray(node)) return node.forEach(sources);
      if (!node || typeof node !== "object") return;
      const rec = node as Json;
      const hit = storedOf(rec.source);
      if (hit) {
        if (rec.source !== hit.id) {
          rec.source = hit.id;
          touched = true;
        }
        if (typeof rec.alias === "string") aliases.set(rec.alias, hit);
      }
      Object.values(rec).forEach(sources);
    };
    const columns = (node: unknown): void => {
      if (Array.isArray(node)) return node.forEach(columns);
      if (!node || typeof node !== "object") return;
      const rec = node as Json;
      if (typeof rec.column === "string") {
        const dot = rec.column.indexOf(".");
        const hit = dot > 0 ? aliases.get(rec.column.slice(0, dot)) : undefined;
        const cid = hit?.columns.get(rec.column.slice(dot + 1));
        if (hit && cid) {
          rec.column = `${rec.column.slice(0, dot)}.${cid}`;
          touched = true;
        }
      }
      Object.values(rec).forEach(columns);
    };
    sources(next);
    columns(next);
    if (touched) {
      out.writes.updates.push(next);
      written.add(String(q.id));
      out.because.set(String(q.id), `query ${String(q.name)} reads the designed table by its id`);
    }
  }
  // Other table files' foreign keys that named a projected table by its key follow it.
  for (const d of all) {
    if (d.kind !== "table" || d.database !== databaseId || d.origin === "synthesized" || written.has(String(d.id))) continue;
    if (!arr(d.foreignKeys).some((fk) => stored.has(String(fk.referencesTable)))) continue;
    const next = clone(d);
    for (const fk of arr(next.foreignKeys)) {
      const target = stored.get(String(fk.referencesTable));
      if (!target) continue;
      fk.referencesTable = target.id;
      if (Array.isArray(fk.referencesColumns)) fk.referencesColumns = (fk.referencesColumns as string[]).map((c) => target.columns.get(c) ?? c);
    }
    out.writes.updates.push(next);
    out.because.set(String(d.id), "its foreign keys name the new tables");
  }
  return out;
}

const singular = (word: string) =>
  /ies$/i.test(word)
    ? word.slice(0, -3) + "y"
    : /(ss|us)$/i.test(word)
      ? word
      : /(ch|sh|x|z|ses)es$/i.test(word)
        ? word.slice(0, -2)
        : /s$/i.test(word)
          ? word.slice(0, -1)
          : word;

function materializeEntities(input: BindingInput, databaseId: string, request: MaterializeRequest): Outcome {
  const { docs } = input;
  const db = docs.get(databaseId);
  const out: Outcome = { writes: { creates: [], updates: [], deletes: [] }, because: new Map(), notes: [], diagnostics: [] };
  if (!db || db.kind !== "database") {
    out.diagnostics.push(refusal(`No database has the id '${databaseId}'.`, databaseId));
    return out;
  }
  const pkg = request.package ?? null;
  if (!pkg || docs.get(pkg)?.kind !== "package") {
    out.diagnostics.push(refusal("Pick the domain the new entities go to.", databaseId));
    return out;
  }
  const view = resolveDatabase(input, databaseId);
  const taken = new Set(
    entities(docs)
      .filter((e) => e.package === pkg)
      .map((e) => String(e.name)),
  );
  const picked = [...new Set(request.tables ?? [])];
  const made = new Map<string, { entity: Json; columns: Map<string, string> }>();
  for (const id of picked) {
    const table = view?.tables.find((t) => t.key === id);
    const v = view?.views.find((x) => x.id === id);
    if (!table && !v) {
      out.diagnostics.push(refusal(`'${id}' is not a table or view of ${String(db.name)}.`, id));
      continue;
    }
    if (table?.origin === "synthesized") {
      out.diagnostics.push(refusal(`${table.name} is projected from an entity: it already has one.`, id));
      continue;
    }
    if (bindersOf(docs, id).length) {
      out.diagnostics.push(refusal(`${table?.name ?? v?.name} is already bound to ${bindersOf(docs, id)[0].entityName}.`, id));
      continue;
    }
    const sourceName = table?.name ?? v!.name;
    const name = applyCase(singular(sourceName), "pascal");
    if (taken.has(name)) {
      out.diagnostics.push(refusal(`The domain already has an entity named ${name}.`, id));
      continue;
    }
    taken.add(name);
    const columns: {
      key: string;
      name: string;
      type: string | null;
      length?: number | null;
      precision?: number | null;
      scale?: number | null;
      nullable: boolean;
      identity: boolean;
      pk: boolean;
    }[] = table
      ? table.columns.map((c) => ({
          key: c.key,
          name: c.name,
          type: c.type,
          length: c.length,
          precision: c.precision,
          scale: c.scale,
          nullable: c.nullable,
          identity: c.identity,
          pk: c.isPrimaryKey,
        }))
      : v!.columns.map((c) => ({ key: c.name, name: c.name, type: c.type, nullable: c.nullable, identity: false, pk: false }));
    const attrOf = new Map<string, string>();
    const attributes = columns.map((c) => {
      const a: Json = { id: input.newId(), name: applyCase(c.name, "camel"), type: c.type ?? "string" };
      if (c.length) a.length = c.length;
      if (c.precision) a.precision = c.precision;
      if (c.scale !== null && c.scale !== undefined && c.precision) a.scale = c.scale;
      if (!c.nullable) a.required = true;
      attrOf.set(c.key, String(a.id));
      return a;
    });
    let keyColumns = columns.filter((c) => c.pk);
    if (!keyColumns.length && !table) {
      const idColumn = columns.find((c) => c.name.toLowerCase() === "id") ?? columns[0];
      keyColumns = idColumn ? [idColumn] : [];
      if (idColumn) out.notes.push(`${sourceName} is a view: ${name} takes ${idColumn.name} as its key.`);
    }
    const entity: Json = { kind: "entity", id: input.newId(), name, package: pkg };
    if (keyColumns.length) {
      entity.key = { attributes: keyColumns.map((c) => attrOf.get(c.key)!) };
      if (keyColumns.length === 1 && keyColumns[0].identity) (entity.key as Json).strategy = "database-identity";
    }
    entity.attributes = attributes;
    entity.bindings = [{ id: input.newId(), database: databaseId, source: id, fields: columns.map((c) => ({ attribute: attrOf.get(c.key)!, column: c.key })) }];
    out.writes.creates.push(entity);
    out.because.set(String(entity.id), `bound to ${sourceName}`);
    made.set(id, { entity, columns: attrOf });
  }
  // Each foreign key between the picked tables, or towards a table an entity is bound to, becomes a many-to-one relation.
  for (const id of picked) {
    const dependent = made.get(id);
    const tableDoc = docs.get(id);
    if (!dependent || !tableDoc) continue;
    for (const fk of arr(tableDoc.foreignKeys)) {
      const target = String(fk.referencesTable);
      const principalId = made.get(target)?.entity.id ?? bindersOf(docs, target)[0]?.entity;
      if (!principalId) continue;
      const principalName = String(made.get(target)?.entity.name ?? docs.get(String(principalId))?.name ?? "");
      const dependentName = String(dependent.entity.name);
      const columns = (fk.columns as string[] | undefined) ?? [];
      const columnDocs = arr(tableDoc.columns).filter((c) => columns.includes(String(c.id)));
      const required = columnDocs.length > 0 && columnDocs.every((c) => c.nullable === false);
      const principalEnd: Json = {
        id: input.newId(),
        entity: principalId,
        role: applyCase(principalName, "camel"),
        navigation: applyCase(principalName, "camel"),
        max: 1,
      };
      if (required) principalEnd.min = 1;
      const action = { cascade: "cascade", restrict: "restrict", "set-null": "set-null" }[String(fk.onDelete ?? "")];
      if (action) principalEnd.onDelete = action;
      const relation: Json = {
        kind: "relation",
        id: input.newId(),
        name: `${dependentName}${principalName}`,
        package: pkg,
        ends: [{ id: input.newId(), entity: dependent.entity.id, role: applyCase(dependentName, "camel") }, principalEnd],
      };
      out.writes.creates.push(relation);
      out.because.set(String(relation.id), `the foreign key ${String(fk.name ?? fk.id)}`);
      const mapping: Json = {
        kind: "mapping",
        id: input.newId(),
        name: `${String(relation.name)} in ${String(db.name)}`,
        database: databaseId,
        relation: relation.id,
        foreignKey: fk.id,
      };
      out.writes.creates.push(mapping);
      out.because.set(String(mapping.id), `names ${String(fk.name ?? fk.id)}`);
    }
  }
  return out;
}

/** The attribute a source column becomes (materialize-entities, materialize-attributes): Materializer.AttributeFor. */
function attributeFor(
  input: BindingInput,
  c: { name: string; type: string | null; length?: number | null; precision?: number | null; scale?: number | null; nullable: boolean },
  taken: Set<string>,
): Json {
  let name = applyCase(c.name, "camel");
  if (!/^[A-Za-z_]/.test(name)) name = `c${applyCase(c.name, "pascal")}`;
  let unique = name;
  for (let n = 2; taken.has(unique); n++) unique = `${name}${n}`;
  taken.add(unique);
  const a: Json = { id: input.newId(), name: unique, type: c.type ?? "string" };
  if (c.length) a.length = c.length;
  if (c.precision) a.precision = c.precision;
  if (c.scale !== null && c.scale !== undefined && c.precision) a.scale = c.scale;
  if (!c.nullable) a.required = true;
  return a;
}

/**
 * materialize-attributes (Materializer.AttributesAsync): per entity bound to the database, an attribute per column of its source
 * that nothing in the binding names (or per column named, one entity only), mapped to it; an ignored listing of the column goes.
 */
function materializeAttributes(input: BindingInput, databaseId: string, request: MaterializeRequest): Outcome {
  const { docs } = input;
  const db = docs.get(databaseId);
  const out: Outcome = { writes: { creates: [], updates: [], deletes: [] }, because: new Map(), notes: [], diagnostics: [] };
  if (!db || db.kind !== "database") {
    out.diagnostics.push(refusal(`'${databaseId}' is not a database.`, databaseId));
    return out;
  }
  const picked = [...new Set(request.entities ?? [])];
  const named = request.columns?.length ? [...new Set(request.columns)] : null;
  if (!picked.length) out.diagnostics.push(refusal("Name at least one entity to add attributes to.", databaseId));
  if (named && picked.length > 1) out.diagnostics.push(refusal("Name the columns for one entity at a time.", databaseId));
  if (out.diagnostics.length) return out;
  const view = resolveDatabase(input, databaseId);
  for (const id of picked) {
    const doc = docs.get(id);
    if (doc?.kind !== "entity") {
      out.diagnostics.push(refusal(`'${id}' is not an entity.`, id));
      continue;
    }
    const index = bindingsOf(doc).findIndex((b) => b.database === databaseId);
    if (index < 0) {
      out.diagnostics.push(refusal(`Entity '${String(doc.name)}' has no binding to database '${String(db.name)}': add one first, or create its table.`, id));
      continue;
    }
    const binding = bindingsOf(doc)[index];
    const source = sourceOf(view, binding.source);
    if (!source) {
      out.diagnostics.push(
        refusal(`The source of entity '${String(doc.name)}' in database '${String(db.name)}' does not resolve: pick a table or a view first.`, id),
      );
      continue;
    }
    if (source.kind === "query") {
      out.diagnostics.push(
        refusal(`Entity '${String(doc.name)}' reads query '${source.name}' in database '${String(db.name)}': add attributes for a query's fields by hand.`, id),
      );
      continue;
    }
    const attributes = storageAttributes(id, docs);
    const rows = columnRows(binding, attributes, source.columns);
    let chosen = rows.filter((r) => r.status === "unaccounted" || r.status === "identity" || r.status === "default").map((r) => r.column);
    if (named) {
      chosen = [];
      for (const ref of named) {
        const row = rows.find((r) => r.column.key === ref) ?? rows.find((r) => r.column.name === ref);
        if (!row) {
          out.diagnostics.push(refusal(`'${ref}' is not a column of ${source.kind} '${source.name}'.`, id));
          break;
        }
        if (row.status === "field" || row.status === "constant" || row.status === "soft-delete") {
          const what = row.status === "field" ? "mapped" : row.status === "constant" ? "a constant" : "the soft delete column";
          out.diagnostics.push(
            refusal(`Column '${row.column.name}' of ${source.kind} '${source.name}' is already ${what} in the binding of entity '${String(doc.name)}'.`, id),
          );
          break;
        }
        chosen.push(row.column);
      }
      if (out.diagnostics.length) continue;
    } else if (!chosen.length) {
      out.diagnostics.push(
        refusal(`Every column of ${source.kind} '${source.name}' is accounted for in the binding of entity '${String(doc.name)}': nothing to add.`, id),
      );
      continue;
    }
    const json = clone(doc);
    const own = arr(json.attributes);
    json.attributes = own;
    const taken = new Set(attributes.filter((a) => a.origin !== "member" && a.origin !== "relation").map((a) => a.name));
    const next = (json.bindings as Binding[])[index];
    next.fields = [...(next.fields ?? [])];
    const facets = new Map((source.kind === "table" ? (view?.tables.find((t) => t.key === source.id)?.columns ?? []) : []).map((c) => [c.key, c] as const));
    for (const column of chosen) {
      const facet = facets.get(column.key);
      const attribute = attributeFor(input, { ...column, length: facet?.length, precision: facet?.precision, scale: facet?.scale }, taken);
      own.push(attribute);
      next.fields.push({ attribute: String(attribute.id), column: column.key });
      if (next.columns) {
        next.columns = next.columns.filter((c) => !(c.status === "ignored" && (c.column === column.key || c.column === column.name)));
        if (!next.columns.length) delete next.columns;
      }
    }
    out.writes.updates.push(json);
    out.because.set(
      id,
      `attributes for ${chosen.length} ${chosen.length === 1 ? "column" : "columns"} of ${source.kind} ${source.name}, mapped in its binding`,
    );
  }
  return out;
}

/**
 * materialize-columns (Materializer.ColumnsAsync): per entity bound to the database and reading a table file it writes, a column of
 * that table per attribute the binding leaves unmapped (or per one named, one entity only), shaped as the entity's projection makes
 * it (the entity projected with its binding left out), and a field mapping it. A column name the table has is left out with a note.
 */
function materializeColumns(input: BindingInput, databaseId: string, request: MaterializeRequest): Outcome {
  const { docs } = input;
  const db = docs.get(databaseId);
  const out: Outcome = { writes: { creates: [], updates: [], deletes: [] }, because: new Map(), notes: [], diagnostics: [] };
  if (!db || db.kind !== "database") {
    out.diagnostics.push(refusal(`'${databaseId}' is not a database.`, databaseId));
    return out;
  }
  const picked = [...new Set(request.entities ?? [])];
  const named = request.attributes?.length ? [...new Set(request.attributes)] : null;
  if (!picked.length) out.diagnostics.push(refusal("Name at least one entity to add columns for.", databaseId));
  if (named && picked.length > 1) out.diagnostics.push(refusal("Name the attributes for one entity at a time.", databaseId));
  if (out.diagnostics.length) return out;
  // The entities projected as if never bound to the database: their tables give the columns' shapes.
  const unbound = new Map(docs);
  for (const id of picked) {
    const doc = docs.get(id);
    if (doc?.kind === "entity") unbound.set(id, { ...doc, bindings: bindingsOf(doc).filter((b) => b.database !== databaseId) });
  }
  const projected = resolveDatabase({ ...input, docs: unbound }, databaseId);
  for (const id of picked) {
    const doc = docs.get(id);
    if (doc?.kind !== "entity") {
      out.diagnostics.push(refusal(`'${id}' is not an entity.`, id));
      continue;
    }
    const index = bindingsOf(doc).findIndex((b) => b.database === databaseId);
    if (index < 0) {
      out.diagnostics.push(refusal(`Entity '${String(doc.name)}' has no binding to database '${String(db.name)}': add one first, or create its table.`, id));
      continue;
    }
    const binding = bindingsOf(doc)[index];
    const tableDoc = docs.get(binding.source);
    if (tableDoc?.kind !== "table" || tableDoc.database !== databaseId || tableDoc.origin === "synthesized") {
      out.diagnostics.push(
        refusal(
          `Entity '${String(doc.name)}' does not read a designed or imported table of database '${String(db.name)}': columns are added to a table file.`,
          id,
        ),
      );
      continue;
    }
    if (typeof binding.write === "object" && binding.write?.table && binding.write.table !== binding.source) {
      out.diagnostics.push(refusal(`Entity '${String(doc.name)}' writes another table than it reads: add the columns to the tables by hand.`, id));
      continue;
    }
    const shape = projected?.tables.find((t) => t.origin === "synthesized" && t.entityId === id && !t.isJunction);
    if (!shape) {
      out.diagnostics.push(
        refusal(`Entity '${String(doc.name)}' has no table of its own in database '${String(db.name)}' to take the columns' shapes from.`, id),
      );
      continue;
    }
    const refs = new Set(storageAttributes(id, docs).map((a) => a.ref));
    const targetOf = (path: string | null) => (!path ? null : refs.has(path) ? path : refs.has(path.split(".")[0]) ? path.split(".")[0] : null);
    const candidates = shape.columns.map((c) => ({ column: c, target: targetOf(c.attributePath) })).filter((c) => c.target !== null);
    const mapped = new Set((binding.fields ?? []).map((f) => f.attribute));
    const names = new Set(arr(tableDoc.columns).map((c) => String(c.name).toLowerCase()));
    const chosen: typeof candidates = [];
    if (named) {
      for (const ref of named) {
        const candidate = candidates.find((c) => c.target === ref);
        const problem = mapped.has(ref)
          ? `'${ref}' is already mapped in the binding of entity '${String(doc.name)}'.`
          : !candidate
            ? `'${ref}' is not an attribute of entity '${String(doc.name)}' that a column of table '${String(tableDoc.name)}' can hold.`
            : names.has(candidate.column.name.toLowerCase())
              ? `Table '${String(tableDoc.name)}' already has a column named '${candidate.column.name}': map the attribute to it instead.`
              : null;
        if (problem) {
          out.diagnostics.push(refusal(problem, id));
          break;
        }
        chosen.push(candidate!);
      }
      if (out.diagnostics.length) continue;
    } else {
      for (const c of candidates.filter((x) => !mapped.has(x.target!))) {
        if (names.has(c.column.name.toLowerCase()))
          out.notes.push(
            `Table '${String(tableDoc.name)}' already has a column named '${c.column.name}': attribute '${c.target}' is left for you to map to it.`,
          );
        else chosen.push(c);
      }
      if (!chosen.length) {
        out.diagnostics.push(
          refusal(
            `Every attribute of entity '${String(doc.name)}' a column can hold is mapped, or table '${String(tableDoc.name)}' has its column already: nothing to add.`,
            id,
          ),
        );
        continue;
      }
    }
    const table = clone(tableDoc);
    table.columns = [...arr(table.columns)];
    const entity = clone(doc);
    const next = (entity.bindings as Binding[])[index];
    next.fields = [...(next.fields ?? [])];
    for (const { column, target } of chosen) {
      const json: Json = { id: input.newId(), name: column.name, type: column.type };
      if (column.length) json.length = column.length;
      if (column.precision) json.precision = column.precision;
      if (column.scale !== null && column.scale !== undefined && column.precision) json.scale = column.scale;
      if (!column.nullable) json.nullable = false;
      (table.columns as Json[]).push(json);
      next.fields.push({ attribute: target!, column: String(json.id) });
    }
    out.writes.updates.push(table, entity);
    out.because.set(String(table.id), `columns for ${chosen.length} ${chosen.length === 1 ? "attribute" : "attributes"} of entity ${String(doc.name)}`);
    out.because.set(id, `maps the new ${chosen.length === 1 ? "column" : "columns"} of table ${String(tableDoc.name)}`);
  }
  return out;
}

// ------------------------------------------------------------------ binding SQL (simple statements)

const quote = (name: string) => (/^[a-z_][a-z0-9_]*$/.test(name) ? name : `"${name.replace(/"/g, '""')}"`);
const literal = (v: unknown) => (v === null || v === undefined ? "NULL" : typeof v === "string" ? `'${v.replace(/'/g, "''")}'` : String(v));

/** GET /api/model/entities/{id}/bindings/{bindingId}/sql: the five statements, built simply (select, by key, insert, update, delete). */
export function bindingSql(input: PhysicalInput, entityId: string, bindingId: string, dialect: string | null, placeholder: string): BindingSqlResult | null {
  const entity = input.docs.get(entityId);
  const binding = bindingsOf(entity).find((b) => b.id === bindingId);
  if (!entity || !binding) return null;
  const view = resolveDatabase(input, binding.database);
  const source = sourceOf(view, binding.source);
  const attributes = storageAttributes(entityId, input.docs);
  if (!view || !source)
    return {
      preview: null,
      diagnostics: [
        {
          rule: "MQ4044",
          severity: "error",
          message: `The source '${binding.source}' is not a table, view or query of the database.`,
          elementId: entityId,
          filePath: null,
          jsonPointer: `/bindings/${bindingsOf(entity).indexOf(binding)}/source`,
          line: null,
          column: null,
        },
      ],
    };
  const p = (name: string, i: number) => (placeholder === "$" ? `$${i + 1}` : `${placeholder}${name}`);
  const fields = (binding.fields ?? []).flatMap((f) => {
    const a = attributes.find((x) => x.ref === f.attribute);
    const c = findColumn(source.columns, f.column);
    return a && c ? [{ name: a.name, column: c.name, key: a.isKey, readOnly: a.readOnly }] : [];
  });
  const constants = (binding.constants ?? []).flatMap((k) => {
    const c = findColumn(source.columns, k.column);
    return c ? [{ column: c.name, value: k.value ?? null }] : [];
  });
  const listed = new Set((binding.columns ?? []).filter((c) => c.status !== "ignored").map((c) => findColumn(source.columns, c.column)?.name));
  const from =
    source.kind === "query"
      ? `(${(view.queries ?? []).find((q) => q.id === source.id)?.sql ?? ""}) q`
      : `${source.schema ? `${quote(source.schema)}.` : ""}${quote(source.name)} t`;
  const alias = source.kind === "query" ? "q" : "t";
  const filters = constants.map((c) => `${alias}.${quote(c.column)} ${c.value === null ? "IS NULL" : `= ${literal(c.value)}`}`);
  const soft = typeof binding.delete === "object" ? binding.delete.soft : null;
  if (soft) {
    const col = findColumn(source.columns, soft.column)?.name ?? soft.column;
    filters.push(`(${alias}.${quote(col)} IS NULL OR ${alias}.${quote(col)} <> ${literal(soft.value ?? null)})`);
  }
  const keys = fields.filter((f) => f.key);
  const select = `SELECT ${fields.map((f) => `${alias}.${quote(f.column)} AS ${quote(f.name)}`).join(", ") || "*"}\nFROM ${from}${filters.length ? `\nWHERE ${filters.join(" AND ")}` : ""}`;
  const byKey = keys.map((k, i) => `${alias}.${quote(k.column)} = ${p(k.name, i)}`);
  const statement = (sql: string, parameters: string[]) => ({ sql, parameters });
  const writeTable = typeof binding.write === "object" ? sourceOf(view, binding.write.table) : source.kind === "table" ? source : undefined;
  const target = writeTable ? `${writeTable.schema ? `${quote(writeTable.schema)}.` : ""}${quote(writeTable.name)}` : "";
  const writing = writes(binding, source) && !!writeTable;
  const written = fields.filter((f) => !listed.has(f.column) && !findColumn(source.columns, f.column)?.identity);
  const insertCols = [...written.map((f) => f.column), ...constants.map((c) => c.column)];
  const insertValues = [...written.map((f, i) => p(f.name, i)), ...constants.map((c) => literal(c.value))];
  const updated = written.filter((f) => !f.key && !f.readOnly);
  const where = [
    ...keys.map((k, i) => `${quote(k.column)} = ${p(k.name, updated.length + i)}`),
    ...constants.map((c) => `${quote(c.column)} = ${literal(c.value)}`),
  ];
  const deleteSql =
    binding.delete === "none" || !writing
      ? null
      : soft
        ? `UPDATE ${target} SET ${quote(findColumn(source.columns, soft.column)?.name ?? soft.column)} = ${literal(soft.value ?? null)}\nWHERE ${where.join(" AND ")}`
        : `DELETE FROM ${target}\nWHERE ${where.join(" AND ")}`;
  return {
    preview: {
      entityId,
      bindingId,
      database: binding.database,
      dialect: (dialect ?? view.dialect) as "postgresql",
      select: statement(select, []),
      selectByKey: keys.length
        ? statement(
            `${select}${filters.length ? " AND " : "\nWHERE "}${byKey.join(" AND ")}`,
            keys.map((k) => k.name),
          )
        : null,
      insert: writing
        ? statement(
            `INSERT INTO ${target} (${insertCols.map(quote).join(", ")})\nVALUES (${insertValues.join(", ")})`,
            written.map((f) => f.name),
          )
        : null,
      update:
        writing && keys.length
          ? statement(
              `UPDATE ${target}\nSET ${updated.map((f, i) => `${quote(f.column)} = ${p(f.name, i)}`).join(", ")}\nWHERE ${where.join(" AND ")}`,
              [...updated, ...keys].map((f) => f.name),
            )
          : null,
      delete:
        deleteSql && keys.length
          ? statement(
              deleteSql,
              keys.map((k) => k.name),
            )
          : null,
    },
    diagnostics: [],
  };
}

// ------------------------------------------------------------------ the binding rules

/** The binding rules over every entity bound to the database (resolver findings, as the engine adds them to every validate). */
export function bindingFindings(view: DatabaseView, docs: ReadonlyMap<string, Json>, pathOf: (id: string) => string | null): Diagnostic[] {
  const out: Diagnostic[] = [];
  // Only the entities bound to this database are checked: on a large model most entities have no binding, and their storage
  // attributes (inheritance and stereotypes walked) are not needed.
  for (const e of entities(docs)
    .filter((x) => bindingsOf(x).some((b) => b.database === view.id))
    .sort(byName)) {
    const all = bindingsOf(e);
    const attributes = storageAttributes(String(e.id), docs);
    all.forEach((b, i) => {
      if (b.database !== view.id) return;
      const at = (pointer: string) => `/bindings/${i}${pointer}`;
      const push = (rule: string, severity: Diagnostic["severity"], message: string, pointer: string) =>
        out.push({ rule, severity, message, elementId: String(e.id), filePath: pathOf(String(e.id)), jsonPointer: at(pointer), line: null, column: null });
      if (all.findIndex((x) => x.database === b.database) !== i) push("MQ4050", "error", `${String(e.name)} has a second binding to ${view.name}.`, "");
      for (const p of bindingProblems(b, attributes, sourceOf(view, b.source))) push(p.rule, p.severity, p.message, p.pointer);
    });
  }
  // MQ4011: a relation of the foreign-key shape whose ends are both bound to tables of the database names no foreign key there
  // (MappingRules.cs: a many-to-many keeps the junction table the database lays out for it, so it needs nothing named).
  // Indexed once: the tables of the database, and the relation mappings to it (a lookup per relation over every document is
  // quadratic on a large model).
  const tableKeys = new Set(view.tables.map((t) => t.key));
  const boundTable = (entity: unknown) => {
    const b = bindingsOf(docs.get(String(entity))).find((x) => x.database === view.id);
    if (!b) return null;
    const table = typeof b.write === "object" ? b.write.table : b.source;
    return tableKeys.has(String(table)) ? table : null;
  };
  const relationMappings = new Map<string, Json>();
  const relations: Json[] = [];
  for (const d of docs.values()) {
    if (d.kind === "relation") relations.push(d);
    else if (d.kind === "mapping" && d.database === view.id && d.relation && !relationMappings.has(String(d.relation)))
      relationMappings.set(String(d.relation), d);
  }
  for (const r of relations.sort(byName)) {
    const ends = arr(r.ends);
    if (ends.length !== 2 || !ends.every((e) => boundTable(e.entity))) continue;
    const mapping = relationMappings.get(String(r.id));
    const manyToMany = ends.every((e) => (e.max ?? "*") === "*");
    if (manyToMany || mapping?.ignore === true || mapping?.foreignKey) continue;
    out.push({
      rule: "MQ4011",
      severity: "error",
      message: `${String(r.name)}: both ends are bound to tables of ${view.name}, and no foreign key is named for it there.`,
      elementId: String(r.id),
      filePath: pathOf(String(r.id)),
      jsonPointer: "",
      line: null,
      column: null,
    });
  }
  return out;
}
