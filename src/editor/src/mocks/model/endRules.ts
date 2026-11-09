// The mock's rules for relation ends with several types (several-types-design.md section 6, part 1): MQ3041 an end with several
// types where it can't be, MQ3042 a list of one entity, MQ3043 an entry another entry covers, MQ4115 (info) a database that stores
// nothing for such a relation, and MQ4116 a binding field, query field or query collection that names such an end. The texts are
// the design's, word for word. Each check runs only for a relation that has such an end, so a model without them is untouched.
import { conventionOf, placesEntity } from "@/model/databaseMapping";
import { docEndHasSeveralTypes, docEndSingleEntity, type EndDocLike } from "@/model/endTypes";
import { appliesToEntities, Coverage } from "./coverage";
import { severalEnds } from "./endCover";
import type { ModelEntry } from "./validate";

type Json = Record<string, unknown>;
type Emit = (rule: string, severity: "error" | "warning" | "info", message: string, pointer: string, elementId?: string) => void;

/** What the rules read of the model: other entries by id and every entry. */
export interface EndRuleContext {
  lookup?(id: string): ModelEntry | undefined;
  all?(): Iterable<ModelEntry>;
  /** The entry that owns an id (an element's own or a sub-element's), for the ends a field names. */
  ownerOf?(id: string): ModelEntry | undefined;
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string => (typeof v === "string" ? v : "");

/** Whether any end of a relation document has several types (decision 2): the cheap test every rule starts with. */
export function relationHasSeveralTypes(json: Json): boolean {
  return json.kind === "relation" && arr(json.ends).some((e) => docEndHasSeveralTypes(e as EndDocLike));
}

/** The composition's child end index, as the engine reads it: the owner is the end with max 1 (the first when both are). */
function compositionChild(json: Json, ends: Json[]): number | null {
  if (json.relationKind !== "composition" || ends.length !== 2) return null;
  if (ends[0].max === 1) return 1;
  if (ends[1].max === 1) return 0;
  return null;
}

/**
 * MQ3041, MQ3042, MQ3043, MQ2003 and MQ9211 on a relation file, as the engine reports them (RelationTypeRules.CheckRelation): a finding about
 * one end carries the end's id, one about the relation as a whole (several types on both ends) the relation's.
 */
export function endTypeDiagnostics(json: Json, ctx: EndRuleContext, emit: Emit): void {
  const ends = arr(json.ends);
  const hasTypes = ends.some((e) => e.types !== undefined || e.anyEntity === true);
  if (!hasTypes) return;
  const relation = str(json.name);
  const nameOf = (id: unknown) => str(ctx.lookup?.(str(id))?.json.name) || str(id);
  const nAry = json.relationKind === "n-ary" || ends.length > 2;
  let coverage: Coverage | null = null;
  ends.forEach((e, i) => {
    const endId = str(e.id) || undefined;
    const pointer = `/ends/${i}`;
    const many = docEndHasSeveralTypes(e as EndDocLike);
    if (many) {
      if (nAry)
        emit("MQ3041", "error", `Relation '${relation}' is n-ary, so its end '${str(e.role)}' can't have several types; give it one entity.`, pointer, endId);
    }
    const types = arr(e.types);
    if (e.types === undefined) return;
    // MQ3042: a list that holds one entity means the same as entity.
    if (!many && types.length === 1 && types[0].entity !== undefined)
      emit("MQ3042", "warning", `End '${str(e.role)}' lists only entity '${nameOf(types[0].entity)}' in types; write it as entity.`, `${pointer}/types`, endId);
    // MQ2003 and MQ9211: an entry's stereotype is a known key of a stereotype that applies to entities.
    types.forEach((t, j) => {
      if (typeof t.stereotype !== "string") return;
      const at = `${pointer}/types/${j}/stereotype`;
      const stereotype = [...(ctx.all?.() ?? [])].find((x) => x.json.kind === "stereotype" && x.json.key === t.stereotype)?.json;
      if (!stereotype) emit("MQ2003", "error", `Stereotype '${t.stereotype}' is not defined in model/vocabularies/stereotypes.`, at, endId);
      else if (!appliesToEntities(stereotype as never))
        emit(
          "MQ9211",
          "error",
          `Type ${j + 1} of end '${str(e.role)}' names stereotype '${t.stereotype}', which applies to ${arr(stereotype.appliesTo).join(", ")}, not to entities, so no entity can carry it; name a stereotype entities carry, or an entity.`,
          at,
          endId,
        );
    });
    if (!many) return;
    // MQ3043: an entity entry another entry covers: it derives from a listed entity, carries a listed stereotype, or derives from one that does.
    types.forEach((t, j) => {
      if (typeof t.entity !== "string" || !ctx.lookup?.(t.entity)) return;
      const chain: string[] = [];
      for (let at: string | undefined = t.entity; at && !chain.includes(at); at = str(ctx.lookup?.(at)?.json.base) || undefined) chain.push(at);
      let message: string | null = null;
      for (const other of types)
        if (typeof other.entity === "string" && other.entity !== t.entity && chain.includes(other.entity) && ctx.lookup?.(other.entity)) {
          message = `End '${str(e.role)}' lists entity '${nameOf(t.entity)}', which entity '${nameOf(other.entity)}' already covers, since it derives from it; remove the entry.`;
          break;
        }
      if (message === null && ctx.all) {
        coverage ??= new Coverage(ctx.all());
        const carries = (id: string, key: string) => {
          const doc = ctx.lookup?.(id)?.json;
          return !!doc && coverage!.effective(doc).has(key);
        };
        for (const other of types) {
          if (typeof other.stereotype !== "string") continue;
          if (carries(t.entity, other.stereotype)) {
            message = `End '${str(e.role)}' lists entity '${nameOf(t.entity)}', which carries stereotype '${other.stereotype}', also listed; remove the entry.`;
            break;
          }
          const carrier = chain.slice(1).find((id) => carries(id, other.stereotype as string));
          if (carrier !== undefined) {
            message = `End '${str(e.role)}' lists entity '${nameOf(t.entity)}', which stereotype '${other.stereotype}' already covers, since it derives from entity '${nameOf(carrier)}', which carries it; remove the entry.`;
            break;
          }
        }
      }
      if (message !== null) emit("MQ3043", "warning", message, `${pointer}/types/${j}`, endId);
    });
  });
  // MQ3041: a composition's child end can't be any entity.
  const child = compositionChild(json, ends);
  if (child !== null && ends[child].anyEntity === true)
    emit(
      "MQ3041",
      "error",
      `End '${str(ends[child].role)}' is the part in composition '${relation}', so it can't be any entity; list the types it may be.`,
      `/ends/${child}/anyEntity`,
      str(ends[child].id) || undefined,
    );
}

/** Whether a relation mapping states how the relation is stored: it names a link table or has an `ends` entry (decision 49). */
export function storesRelation(mapping: Json): boolean {
  return typeof mapping.junctionTable === "string" || arr(mapping.ends).length > 0;
}

/**
 * MQ4115 (info): for each database where every end has a covered type placed, "Relation 'AttachedTo' has an end with several types, so
 * database 'main' stores nothing for it." A relation mapping that sets `ignore`, or that stores the relation, silences it.
 */
export function storageDiagnostics(json: Json, ctx: EndRuleContext, emit: Emit): void {
  if (!relationHasSeveralTypes(json) || !ctx.all) return;
  const ends = arr(json.ends);
  const first = ends.findIndex((e) => docEndHasSeveralTypes(e as EndDocLike));
  if (first < 0 || ends.length !== 2) return;
  const entries = [...ctx.all()];
  const databases = entries.filter((e) => e.json.kind === "database");
  if (databases.length === 0) return;
  const entities = entries.filter((e) => e.json.kind === "entity").map((e) => e.json);
  const byId = new Map(entries.map((e) => [e.id, e.json]));
  const chainOf = (entity: Json): string[] => {
    const ids: string[] = [];
    for (let at: Json | undefined = entity; at && !ids.includes(str(at.id)); at = byId.get(str(at.base))) ids.push(str(at.id));
    return ids;
  };
  const coverage = ends.some((e) => arr(e.types).some((t) => t.stereotype !== undefined)) ? new Coverage(entries) : null;
  // What an end covers: a one-type end its entity alone; an end with several types any entity it covers (a listed entity, one derived from it, a carrier).
  const covers = (end: Json, entity: Json): boolean => {
    if (end.anyEntity === true) return true;
    const single = docEndSingleEntity(end as EndDocLike);
    if (single) return str(entity.id) === single;
    return arr(end.types).some((t) =>
      typeof t.entity === "string" ? chainOf(entity).includes(t.entity) : typeof t.stereotype === "string" && !!coverage?.covers(t.stereotype, str(entity.id)),
    );
  };
  const parentOf = (id: string) => str(byId.get(id)?.parent) || null;
  for (const database of [...databases].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))) {
    const mappings = entries.filter((e) => e.json.kind === "mapping" && e.json.database === database.id);
    // A mapping that sets ignore says so already; one that stores the relation (a junctionTable or an ends entry) leaves nothing to say (decision 49).
    if (mappings.some((m) => m.json.relation === json.id && (m.json.ignore === true || storesRelation(m.json)))) continue;
    const convention = conventionOf(database.json as Parameters<typeof conventionOf>[0]);
    // Placed: a binding there, else the mapping and the database's convention.
    const placed = entities.filter(
      (en) =>
        arr(en.bindings).some((b) => b.database === database.id) ||
        placesEntity(convention, str(en.package) || null, mappings.find((m) => m.json.entity === en.id)?.json as { ignore?: boolean } | undefined, parentOf),
    );
    if (!ends.every((end) => placed.some((en) => covers(end, en)))) continue;
    emit(
      "MQ4115",
      "info",
      `Relation '${str(json.name)}' has an end with several types, so database '${str(database.json.name)}' stores nothing for it.`,
      `/ends/${first}`,
      str(ends[first].id) || undefined,
    );
  }
}

/**
 * The relation with an end of several types that a field's `attribute` goes through, and the end it names: the first segment of the
 * reference is an end of such a relation, the end with several types or another one (RelationTypeRules.ThroughSeveralTypes).
 */
function through(reference: string, ctx: EndRuleContext): { relation: Json; end: Json } | null {
  const dot = reference.indexOf(".");
  const id = dot > 0 ? reference.slice(0, dot) : reference;
  const owner = ctx.ownerOf?.(id);
  if (!owner || owner.json.kind !== "relation") return null;
  const end = arr(owner.json.ends).find((e) => e.id === id);
  return end && relationHasSeveralTypes(owner.json) ? { relation: owner.json, end } : null;
}

/** "end 'attachedTo', which leads to several types", or for another end of such a relation, the words MQ7105 uses. */
const named = (t: { relation: Json; end: Json }): string =>
  docEndHasSeveralTypes(t.end as EndDocLike)
    ? `end '${str(t.end.role)}', which leads to several types`
    : `end '${str(t.end.role)}' of relation '${str(t.relation.name)}', which has an end with several types`;

/**
 * MQ4116 on an entity (the fields of its bindings) and on a query (its select fields, a collection and its nested select fields): one
 * that names an end of a relation with an end of several types, which leads to more than one table or which no database stores yet.
 * Fields and collections are counted from 0, and a nested field's text names its collection, as the engine says.
 */
export function fieldDiagnostics(json: Json, ctx: EndRuleContext, emit: Emit): void {
  if (!ctx.ownerOf) return;
  if (json.kind !== "query") return;
  const fields = (list: Json[], at: string, within: string) =>
    list.forEach((f, fi) => {
      const found = typeof f.attribute === "string" ? through(f.attribute, ctx) : null;
      if (found)
        emit("MQ4116", "error", `Field ${fi}${within} of query '${str(json.name)}' names ${named(found)}; a field can't map it.`, `${at}/${fi}/attribute`);
    });
  fields(arr(json.select), "/select", "");
  arr(json.collections).forEach((c, ci) => {
    const found = typeof c.attribute === "string" ? through(c.attribute, ctx) : null;
    if (found)
      emit(
        "MQ4116",
        "error",
        `Collection ${ci} of query '${str(json.name)}' names ${named(found)}; a collection can't fill it.`,
        `/collections/${ci}/attribute`,
      );
    fields(arr((c.query as Json | undefined)?.select), `/collections/${ci}/query/select`, ` of collection ${ci}`);
  });
}

/** The ways an `ends` entry can point at its end (decision 38): `types` counts as a way only when its items carry `foreignKey`. */
export function entryWays(entry: Json): string[] {
  const ways: string[] = [];
  if (entry.foreignKey !== undefined) ways.push("foreignKey");
  if (arr(entry.foreignKeys).length > 0) ways.push("foreignKeys");
  if (arr(entry.types).some((t) => t.foreignKey !== undefined)) ways.push("types");
  if (entry.idColumn !== undefined) ways.push("idColumn");
  return ways;
}

/**
 * MQ4009 on a relation mapping, the file cases of several-types-design.md section 19.7 that need no resolved tables: an `ends` entry with
 * no way or two, the keys that go with `idColumn`, two entries for one end, the keys a one-type relation doesn't take, and `constants` and
 * attribute columns against the designed `junctionTable`. A mapping that states no storage keeps part 1's words for the keys it can't
 * set (the engine's text until its storage rules land).
 */
export function mappingDiagnostics(json: Json, ctx: EndRuleContext, emit: Emit): void {
  if (json.kind !== "mapping" || typeof json.relation !== "string") return;
  const relation = ctx.lookup?.(json.relation)?.json;
  if (!relation || relation.kind !== "relation") return;
  const several = relationHasSeveralTypes(relation);
  const name = str(relation.name);
  const relationEnds = arr(relation.ends);
  const entries = arr(json.ends);
  const junction = typeof json.junctionTable === "string" ? ctx.lookup?.(json.junctionTable)?.json : undefined;
  // The mapping's junction when it is a designed or imported table of the mapping's database (MappingRules.DesignedJunction).
  const designed = junction !== undefined && junction.kind === "table" && junction.origin !== "synthesized" && junction.database === json.database;
  const stores = storesRelation(json);
  const roleOf = (endId: unknown) => str(relationEnds.find((e) => e.id === endId)?.role) || str(endId);
  const bad = (pointer: string, message: string) => emit("MQ4009", "error", message, pointer);

  // The keys that part 1 refused on such a relation and part 2 still refuses (decision 50), in part 1's words while nothing is stored.
  if (several) {
    const refused: [string, boolean][] = [
      ["/foreignKeyEnd", json.foreignKeyEnd !== undefined],
      ["/foreignKey", json.foreignKey !== undefined],
      ["/promotedName", json.promotedName !== undefined],
      ["/shape", json.shape === "promoted" || (json.shape !== undefined && !stores)],
      ["/attributes", !stores && arr(json.attributes).length > 0],
      ["/constants", !stores && arr(json.constants).length > 0],
    ];
    for (const [pointer, isSet] of refused)
      if (isSet)
        bad(
          pointer,
          `Relation '${name}' has an end with several types, so a mapping of it doesn't take '${pointer.slice(1)}'; its 'ends' entries say how it is stored. Remove it.`,
        );
    if (stores) {
      const shape = json.shape === "junction" || json.shape === "foreign-key" ? json.shape : null;
      const inLinkTables = typeof json.junctionTable === "string" || entries.some((e) => arr(e.types).some((t) => t.foreignKey !== undefined));
      if (shape && (shape === "junction") !== inLinkTables)
        bad(
          "/shape",
          shape === "junction"
            ? `Relation '${name}' keeps its links in the tables of its ends, so shape 'junction' disagrees with its entries; remove 'shape'.`
            : `Relation '${name}' keeps its links in link tables, so shape 'foreign-key' disagrees with its entries; remove 'shape'.`,
        );
      arr(json.attributes).forEach((a, i) => {
        for (const key of ["storage", "prefix"])
          if (a[key] !== undefined)
            bad(
              `/attributes/${i}/${key}`,
              `Relation '${name}' has an end with several types, so '${key}' of attribute ${i} can't shape engine-made columns; remove it.`,
            );
      });
    }
  }

  const seenEnds = new Set<string>();
  entries.forEach((entry, i) => {
    const at = `/ends/${i}`;
    const role = roleOf(entry.end);
    const ways = entryWays(entry);
    if (several && typeof entry.end === "string") {
      if (seenEnds.has(entry.end)) bad(`${at}/end`, `End '${role}' of relation '${name}' has two entries; keep one.`);
      seenEnds.add(entry.end);
    }
    if (!several) {
      // A one-type relation keeps the words of a missing key and refuses the keys of an end with several types.
      if (ways.length === 0 && designed)
        bad(
          at,
          `End entry ${i} names no 'foreignKey'; junction table '${str(junction.name)}' is designed, so the entry needs the foreign key that references its end's table.`,
        );
      for (const key of ["foreignKeys", "types", "idColumn", "typeColumn", "referencesColumn"])
        if (Array.isArray(entry[key]) ? (entry[key] as unknown[]).length > 0 : entry[key] !== undefined)
          bad(`${at}/${key}`, `End entry ${i} of relation '${name}' sets '${key}', which needs a relation with an end of several types.`);
      return;
    }
    if (ways.length === 0)
      bad(
        at,
        `End entry ${i} of relation '${name}' names no way to point at end '${role}'; give it a foreignKey, foreignKeys, types with foreignKey items or an idColumn.`,
      );
    else if (ways.length > 1) bad(at, `End entry ${i} of relation '${name}' names more than one way to point at end '${role}' (${ways.join(", ")}); keep one.`);
    for (const key of ["typeColumn", "referencesColumn"])
      if (entry[key] !== undefined && entry.idColumn === undefined)
        bad(`${at}/${key}`, `End entry ${i} of relation '${name}' sets '${key}' with no 'idColumn'; it describes the id column.`);
    const types = arr(entry.types);
    const keyed = types.filter((t) => t.foreignKey !== undefined);
    const refers = types.filter((t) => t.referencesColumn !== undefined);
    if (keyed.length && refers.length)
      bad(`${at}/types`, `End entry ${i} of relation '${name}' mixes types items with foreignKey and with referencesColumn; use one kind.`);
    else if (keyed.length && entry.idColumn !== undefined)
      bad(`${at}/types`, `End entry ${i} of relation '${name}' has types items with foreignKey beside 'idColumn'; use one way.`);
    else if (refers.length && entry.idColumn === undefined)
      bad(`${at}/types`, `End entry ${i} of relation '${name}' has types items with referencesColumn but no 'idColumn'.`);
    if (entry.referencesColumn !== undefined && refers.length)
      bad(`${at}/referencesColumn`, `End entry ${i} of relation '${name}' sets 'referencesColumn' on the entry and in types items; keep one.`);
    if (entry.typeColumn !== undefined && entry.typeColumn === entry.idColumn)
      bad(`${at}/typeColumn`, `End entry ${i} of relation '${name}' uses one column for 'typeColumn' and 'idColumn'.`);
    if (arr(entry.foreignKeys).length > 0 && json.junctionTable !== undefined)
      bad(
        `${at}/foreignKeys`,
        `End entry ${i} of relation '${name}' sets 'foreignKeys' beside 'junctionTable'; in a link table an end is pointed at by one key, a key per type or an id.`,
      );
    // Each type is listed once (decision 38): a second item for it would give its rows a second pointer.
    const listed = new Set<string>();
    types.forEach((t, k) => {
      if (typeof t.entity !== "string") return;
      if (listed.has(t.entity))
        bad(
          `${at}/types/${k}/entity`,
          `End entry ${i} of relation '${name}' lists entity '${str(ctx.lookup?.(t.entity)?.json.name) || t.entity}' in two types items; keep one.`,
        );
      listed.add(t.entity);
    });
  });

  // constants and attribute columns serve every relation mapping with a designed link table (answer 9).
  const columns = new Set(arr(junction?.columns).map((c) => str(c.id)));
  // A link table of a relation with an end of several types needs an entry for every end, as a designed junction does (MQ4011).
  if (several && designed)
    for (const end of relationEnds)
      if (!entries.some((e) => e.end === end.id))
        emit(
          "MQ4011",
          "error",
          `Link table '${str(junction.name)}' holds relation '${name}', so end '${str(end.role)}' needs an 'ends' entry that says how the table points at it.`,
          "/junctionTable",
        );
  // The columns of every key an entry names count as pointer columns, beside `idColumn` and `typeColumn`.
  const keyColumns = (id: unknown): string[] => {
    const owner = typeof id === "string" ? ctx.ownerOf?.(id) : undefined;
    const fk = arr(owner?.json.foreignKeys).find((k) => k.id === id);
    return Array.isArray(fk?.columns) ? (fk.columns as string[]) : [];
  };
  const pointerColumns = new Set(
    entries.flatMap((e) => [
      ...[e.idColumn, e.typeColumn].filter((c): c is string => typeof c === "string"),
      ...keyColumns(e.foreignKey),
      ...(Array.isArray(e.foreignKeys) ? (e.foreignKeys as unknown[]).flatMap(keyColumns) : []),
      ...arr(e.types).flatMap((t) => keyColumns(t.foreignKey)),
    ]),
  );
  // A relation with an end of several types that stores nothing has its constants refused above, once, as the engine does.
  arr(json.constants).forEach((c, i) => {
    if (several && !stores) return;
    const at = `/constants/${i}`;
    if (!designed) return bad(at, `Constant ${i} needs a designed 'junctionTable' that holds its column.`);
    if (typeof c.column === "string" && !columns.has(c.column)) bad(`${at}/column`, `'${c.column}' is not a column of link table '${str(junction.name)}'.`);
    else if (typeof c.column === "string" && pointerColumns.has(c.column))
      bad(`${at}/column`, `Constant ${i} names a column that an end entry already uses as a pointer; a constant needs a column of its own.`);
  });
  arr(json.attributes).forEach((a, i) => {
    if (typeof a.column !== "string") return;
    if (!designed) {
      // Without a link table the column is checked against the end's table by the run; a collection or value object can't map one column.
      const attribute = arr(relation.attributes).find((x) => x.id === a.attribute);
      if (several && stores && attribute && isCompound(attribute, ctx))
        bad(`/attributes/${i}/column`, `Attribute ${i} is a collection or value object, so it can't map one column.`);
      return;
    }
    if (!columns.has(a.column)) bad(`/attributes/${i}/column`, `'${a.column}' is not a column of link table '${str(junction.name)}'.`);
    const attribute = arr(relation.attributes).find((x) => x.id === a.attribute);
    if (attribute && isCompound(attribute, ctx)) bad(`/attributes/${i}/column`, `Attribute ${i} is a collection or value object, so it can't map one column.`);
  });
}

/** Whether an attribute is a collection or typed by a value object: one column can't hold it (MappingRules.IsCompound). */
function isCompound(attribute: Json, ctx: EndRuleContext): boolean {
  if (attribute.collection === true) return true;
  const type = attribute.type as Json | string | undefined;
  const ref = type && typeof type === "object" ? type.ref : undefined;
  return typeof ref === "string" && ctx.lookup?.(ref)?.json.kind === "value-object";
}

// Whether an end with several types covers an entity (given as its base chain): a listed entity, a carrier or any entity.
function coversEntity(end: EndDocLike, chain: string[], ctx: EndRuleContext): boolean {
  if (end.anyEntity === true) return true;
  const coverage = ctx.all ? new Coverage(ctx.all()) : null;
  return (end.types ?? []).some((t) =>
    t.entity !== undefined ? chain.includes(t.entity) : !!coverage && t.stereotype !== undefined && chain.some((id) => coverage.covers(t.stereotype!, id)),
  );
}

/**
 * MQ7105 on a seed (ReferenceDataRules): seeds can't hold the links of a relation with an end of several types yet, neither the rows of such
 * a relation nor a column of an entity seed that names one of its ends.
 */
export function seedDiagnostics(json: Json, ctx: EndRuleContext, emit: Emit): void {
  if (json.kind !== "seed" || typeof json.target !== "string") return;
  const target = ctx.lookup?.(json.target)?.json;
  if (!target) return;
  const name = str(json.name);
  if (target.kind === "relation") {
    if (relationHasSeveralTypes(target))
      emit(
        "MQ7105",
        "error",
        `Seed '${name}' holds rows of relation '${str(target.name)}', which has an end with several types; seeds can't hold such links yet.`,
        "/target",
      );
    return;
  }
  if (target.kind !== "entity" || !ctx.ownerOf) return;
  const chain: string[] = [];
  for (let at: Json | undefined = target; at && !chain.includes(str(at.id)); at = ctx.lookup?.(str(at.base))?.json) chain.push(str(at.id));
  const attributes = new Set(arr(target.attributes).map((a) => str(a.id)));
  (Array.isArray(json.columns) ? json.columns : []).forEach((column, k) => {
    if (typeof column !== "string" || attributes.has(column)) return;
    const owner = ctx.ownerOf?.(column);
    if (!owner || owner.json.kind !== "relation") return;
    const ends = arr(owner.json.ends);
    const index = ends.findIndex((e) => e.id === column);
    if (index < 0 || ends.length !== 2) return;
    // The entity holds the end when the other end covers it.
    const other = ends[1 - index] as EndDocLike;
    const near = docEndSingleEntity(other);
    const holds = docEndHasSeveralTypes(other) ? coversEntity(other, chain, ctx) : !!near && chain.includes(near);
    if (!holds || !relationHasSeveralTypes(owner.json)) return;
    const end = ends[index];
    emit(
      "MQ7105",
      "error",
      docEndHasSeveralTypes(end as EndDocLike)
        ? `Seed '${name}' has a column for end '${str(end.role)}', which leads to several types; seeds can't hold such links yet.`
        : `Seed '${name}' has a column for end '${str(end.role)}' of relation '${str(owner.json.name)}', which has an end with several types; seeds can't hold such links yet.`,
      `/columns/${k}`,
    );
  });
}

/**
 * MQ3116 on a lifecycle's guard whose in_state reaches the object through a relation end with several types (ExpressionRules.CannotGovern):
 * valid when the state's lifecycle can govern one of the end's types, as a base passes when a derived entity is governed.
 */
export function guardDiagnostics(json: Json, ctx: EndRuleContext, emit: Emit): void {
  if (json.kind !== "process" || !ctx.all || !arr(json.guards).length) return;
  const entries = [...ctx.all()];
  const ends = severalEnds(entries);
  if (!ends.size) return;
  const coverage = new Coverage(entries);
  const lifecycleOf = (stateId: string) =>
    entries.find((e) => e.json.kind === "process" && JSON.stringify(e.json.states ?? []).includes(`"id":"${stateId}"`))?.json;
  const canGovern = (lifecycle: Json, entityId: string): boolean => {
    if (coverage.governs(lifecycle, entityId)) return true;
    return entries.some((e) => e.json.kind === "entity" && e.id !== entityId && coverage.chain(e.json).includes(entityId) && coverage.governs(lifecycle, e.id));
  };
  const walk = (node: unknown, pointer: string, guardId: string | undefined) => {
    if (Array.isArray(node)) return node.forEach((x, i) => walk(x, `${pointer}/${i}`, guardId));
    if (!node || typeof node !== "object") return;
    const t = node as Json;
    if (Array.isArray(t.inState) && Array.isArray(t.of) && t.of.length) {
      const end = ends.get(String(t.of[t.of.length - 1]));
      const lifecycle = lifecycleOf(String(t.inState[0]));
      if (end && lifecycle && !end.types.some((id) => canGovern(lifecycle, id)))
        emit("MQ3116", "error", `Lifecycle ${str(lifecycle.name)} can't govern any type the object in_state reaches: ${end.leadsTo}.`, pointer, guardId);
    }
    for (const [k, v] of Object.entries(t)) walk(v, `${pointer}/${k}`, guardId);
  };
  arr(json.guards).forEach((g, i) => walk(g.condition, `/guards/${i}/condition`, str(g.id) || undefined));
}
