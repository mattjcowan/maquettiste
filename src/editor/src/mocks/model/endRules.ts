// The mock's rules for relation ends with several types (several-types-design.md section 6, part 1): MQ3041 an end with several
// types where it can't be, MQ3042 a list of one entity, MQ3043 an entry another entry covers, MQ4115 (info) a database that stores
// nothing for such a relation, and MQ4116 a binding field, query field or query collection that names such an end. The texts are
// the design's, word for word. Each check runs only for a relation that has such an end, so a model without them is untouched.
import { conventionOf, placesEntity } from "@/model/databaseMapping";
import { docEndHasSeveralTypes, docEndSingleEntity, type EndDocLike } from "@/model/endTypes";
import { Coverage } from "./coverage";
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

/** MQ3041, MQ3042 and MQ3043 on a relation file. */
export function endTypeDiagnostics(json: Json, ctx: EndRuleContext, emit: Emit): void {
  const ends = arr(json.ends);
  const hasTypes = ends.some((e) => e.types !== undefined || e.anyEntity === true);
  if (!hasTypes) return;
  const relation = str(json.name);
  const nameOf = (id: unknown) => str(ctx.lookup?.(str(id))?.json.name) || str(id);
  const several = ends.map((e) => docEndHasSeveralTypes(e as EndDocLike));
  if (json.relationKind === "n-ary") {
    ends.forEach((e, i) => {
      if (several[i])
        emit("MQ3041", "error", `Relation '${relation}' is n-ary, so its end '${str(e.role)}' can't have several types; give it one entity.`, `/ends/${i}`);
    });
  } else if (ends.length === 2 && several[0] && several[1]) {
    emit("MQ3041", "error", `Relation '${relation}' has several types on both ends; keep them on one end.`, "/ends");
  }
  const child = compositionChild(json, ends);
  if (child !== null && ends[child].anyEntity === true)
    emit(
      "MQ3041",
      "error",
      `End '${str(ends[child].role)}' is the part in composition '${relation}', so it can't be any entity; list the types it may be.`,
      `/ends/${child}/anyEntity`,
    );
  let coverage: Coverage | null = null;
  ends.forEach((e, i) => {
    const types = arr(e.types);
    if (types.length === 0) return;
    // MQ3042: a list of one entity means the same as `entity`.
    if (types.length === 1 && types[0].entity !== undefined && docEndSingleEntity(e as EndDocLike)) {
      emit("MQ3042", "warning", `End '${str(e.role)}' lists only entity '${nameOf(types[0].entity)}' in types; write it as entity.`, `/ends/${i}/types`);
      return;
    }
    // MQ3043: an entity entry another entity entry covers (it derives from it), or a listed stereotype covers (it carries it).
    const listed = types.flatMap((t) => (typeof t.entity === "string" ? [t.entity] : []));
    const stereotypes = types.flatMap((t) => (typeof t.stereotype === "string" ? [t.stereotype] : []));
    types.forEach((t, j) => {
      if (typeof t.entity !== "string") return;
      const baseOf = (id: string) => str(ctx.lookup?.(id)?.json.base) || undefined;
      const seen = new Set<string>([t.entity]);
      for (let at = baseOf(t.entity); at && !seen.has(at); at = baseOf(at)) {
        seen.add(at);
        if (listed.includes(at)) {
          emit(
            "MQ3043",
            "warning",
            `End '${str(e.role)}' lists entity '${nameOf(t.entity)}', which entity '${nameOf(at)}' already covers, since it derives from it; remove the entry.`,
            `/ends/${i}/types/${j}`,
          );
          return;
        }
      }
      if (stereotypes.length && ctx.all) {
        coverage ??= new Coverage(ctx.all());
        const carried = stereotypes.find((key) => coverage!.covers(key, t.entity as string));
        if (carried !== undefined)
          emit(
            "MQ3043",
            "warning",
            `End '${str(e.role)}' lists entity '${nameOf(t.entity)}', which carries stereotype '${carried}', also listed; remove the entry.`,
            `/ends/${i}/types/${j}`,
          );
      }
    });
  });
}

/**
 * MQ4115 (info): for each database where every end has a covered type placed, "Relation 'AttachedTo' has an end with several types, so
 * database 'main' stores nothing for it." A relation mapping that sets `ignore` says so already and silences it.
 */
export function storageDiagnostics(json: Json, ctx: EndRuleContext, emit: Emit): void {
  if (!relationHasSeveralTypes(json) || !ctx.all) return;
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
  const ends = arr(json.ends);
  const coverage = ends.some((e) => arr(e.types).some((t) => t.stereotype !== undefined)) ? new Coverage(entries) : null;
  const covers = (end: Json, entity: Json): boolean => {
    if (end.anyEntity === true) return true;
    if (typeof end.entity === "string") return chainOf(entity).includes(end.entity);
    return arr(end.types).some((t) =>
      typeof t.entity === "string" ? chainOf(entity).includes(t.entity) : typeof t.stereotype === "string" && !!coverage?.covers(t.stereotype, str(entity.id)),
    );
  };
  const parentOf = (id: string) => str(byId.get(id)?.parent) || null;
  for (const database of [...databases].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))) {
    const mappings = entries.filter((e) => e.json.kind === "mapping" && e.json.database === database.id);
    if (mappings.some((m) => m.json.relation === json.id && m.json.ignore === true)) continue;
    const convention = conventionOf(database.json as Parameters<typeof conventionOf>[0]);
    const placed = entities.filter((en) =>
      placesEntity(convention, str(en.package) || null, mappings.find((m) => m.json.entity === en.id)?.json as { ignore?: boolean } | undefined, parentOf),
    );
    if (ends.length === 0 || !ends.every((end) => placed.some((en) => covers(end, en)))) continue;
    emit("MQ4115", "info", `Relation '${str(json.name)}' has an end with several types, so database '${str(database.json.name)}' stores nothing for it.`, "");
  }
}

/** The end an id names, with its relation, when it is an end of a relation that has an end with several types. */
function severalTypesEnd(id: string, ctx: EndRuleContext): { end: Json; role: string } | null {
  const owner = ctx.ownerOf?.(id);
  if (!owner || owner.json.kind !== "relation") return null;
  const found = arr(owner.json.ends).find((e) => e.id === id);
  return found && docEndHasSeveralTypes(found as EndDocLike) ? { end: found, role: str(found.role) } : null;
}

/**
 * MQ4116 on an entity (the fields of its bindings) and on a query (its select fields and collections): one that names a relation end
 * with several types leads to more than one table, so a field can't map it.
 */
export function fieldDiagnostics(json: Json, ctx: EndRuleContext, emit: Emit): void {
  if (!ctx.ownerOf) return;
  if (json.kind === "entity") {
    arr(json.bindings).forEach((b, bi) =>
      arr(b.fields).forEach((f, fi) => {
        const found = typeof f.attribute === "string" ? severalTypesEnd(f.attribute, ctx) : null;
        if (found)
          emit(
            "MQ4116",
            "error",
            `Field ${fi + 1} of the binding of '${str(json.name)}' names end '${found.role}', which leads to several types; a field can't map it.`,
            `/bindings/${bi}/fields/${fi}/attribute`,
          );
      }),
    );
  }
  if (json.kind === "query") {
    arr(json.select).forEach((f, fi) => {
      const found = typeof f.attribute === "string" ? severalTypesEnd(f.attribute, ctx) : null;
      if (found)
        emit(
          "MQ4116",
          "error",
          `Field ${fi + 1} of query '${str(json.name)}' names end '${found.role}', which leads to several types; a field can't map it.`,
          `/select/${fi}/attribute`,
        );
    });
    arr(json.collections).forEach((c, ci) => {
      const found = typeof c.attribute === "string" ? severalTypesEnd(c.attribute, ctx) : null;
      if (found)
        emit(
          "MQ4116",
          "error",
          `Collection ${ci + 1} of query '${str(json.name)}' names end '${found.role}', which leads to several types; a collection can't fill it.`,
          `/collections/${ci}/attribute`,
        );
    });
  }
}
