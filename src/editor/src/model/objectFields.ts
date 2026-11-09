// The fields of a process's object, for an actor taken from the object (actors-design.md decisions 8 to 11) and for a
// subject's owner: an entity's attributes (own, inherited and from its stereotypes) and the to-one relation ends that
// lead from it, with the rule that works out whether a path resolves. Pure, so the mock's rules, the pickers and tests share it.
import { docEndEntities, docEndHasSeveralTypes, docEndSingleEntity, endLabel, type EndDocLike } from "@/model/endTypes";

type Json = Record<string, unknown>;

export interface ObjectField {
  id: string;
  name: string;
  kind: "attribute" | "end";
  /** For an end, the entity it leads to. An end with several types has no one entity: it has `targets` instead. */
  target?: string;
  /**
   * For an end with several types (several-types-design.md decisions 2 and 13): the entities it covers (its listed entities and the carriers
   * of its stereotypes, each with the entities derived from them), and what a path through it reads: the fields every one of them has by id.
   * An end written with `anyEntity` has none listed and `anyEntity` true, and offers nothing beyond itself.
   */
  targets?: string[];
  anyEntity?: boolean;
  /** For an end with several types, its types in plain words ("Part, Assembly or approvable (stereotype)", "any entity"). */
  typesLabel?: string;
  /** A collection attribute, or a to-many end. */
  many: boolean;
  /** An attribute typed by a value object, which is several fields and holds no single actor. */
  valueObject: boolean;
  /** An attribute's built-in type, directly or through a scalar type; unset for an end, or when it does not resolve. */
  scalar?: string;
  /** An attribute that comes from a stereotype. */
  fromStereotype?: boolean;
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

export interface FieldSources {
  doc(id: string): Json | undefined;
  /** Every relation document (two-ended ones lead somewhere). */
  relations: readonly Json[];
  stereotype(key: string): Json | undefined;
  kindOf(id: string): string | undefined;
  /**
   * The entities that carry the stereotypes a relation's end names (the union over its stereotype entries); the end's index row holds them
   * (`covers`). Without it an end with several types covers its listed entities only.
   */
  carriers?(relation: Json, endIndex: number): readonly string[] | undefined;
  /** An element's name from the index, for entities whose documents are not loaded (an end's listed types). */
  nameOf?(id: string): string | undefined;
}

/** The entity and its bases, nearest first. */
export function entityChain(entity: string, doc: (id: string) => Json | undefined): Json[] {
  const out: Json[] = [];
  const seen = new Set<string>();
  for (let at = doc(entity); at && !seen.has(String(at.id)); at = typeof at.base === "string" ? doc(at.base) : undefined) {
    seen.add(String(at.id));
    out.push(at);
  }
  return out;
}

/** One attribute as a field. */
function attributeField(a: Json, fromStereotype: boolean, src: Pick<FieldSources, "doc" | "kindOf">): ObjectField {
  const id = typeof a.id === "string" ? a.id : "";
  const type = a.type as { ref?: unknown } | string | undefined;
  const ref = type && typeof type === "object" && typeof type.ref === "string" ? type.ref : undefined;
  let scalar = typeof type === "string" ? type : undefined;
  for (let at = ref, hops = 0; at && hops < 8; hops++) {
    const d = src.doc(at);
    if (d?.kind !== "scalar-type") break;
    const base = d.base;
    if (typeof base === "string" && !src.doc(base)) {
      scalar = base;
      break;
    }
    at = typeof base === "string" ? base : undefined;
  }
  return {
    id,
    name: String(a.name ?? id),
    kind: "attribute",
    many: a.collection === true,
    valueObject: !!ref && src.kindOf(ref) === "value-object",
    ...(scalar ? { scalar } : {}),
    ...(fromStereotype ? { fromStereotype } : {}),
  };
}

/** A stereotype entry's fields: the stereotype's own attributes and no ends (stereotype-sets-design.md decision 7). */
export function stereotypeFields(stereotype: Json | undefined, src: Pick<FieldSources, "doc" | "kindOf">): ObjectField[] {
  return arr(stereotype?.attributes)
    .filter((a) => typeof a.id === "string" && a.id)
    .map((a) => attributeField(a, false, src));
}

/** An entity's fields: its attributes, its bases' and its stereotypes', then the ends that lead away from it (or a base). */
export function objectFields(entity: string, src: FieldSources): ObjectField[] {
  const chain = entityChain(entity, src.doc);
  const out: ObjectField[] = [];
  const seen = new Set<string>();
  for (const level of chain) {
    const own = arr(level.attributes).map((a) => ({ a, fromStereotype: false }));
    const stereo = ((level.stereotypes as string[] | undefined) ?? [])
      .flatMap((k) => arr(src.stereotype(k)?.attributes))
      .map((a) => ({ a, fromStereotype: true }));
    for (const { a, fromStereotype } of [...own, ...stereo]) {
      const id = typeof a.id === "string" ? a.id : "";
      if (!id || seen.has(id)) continue;
      seen.add(id);
      out.push(attributeField(a, fromStereotype, src));
    }
  }
  const mine = new Set(chain.map((c) => String(c.id)));
  for (const r of [...src.relations].sort((x, y) => String(x.id).localeCompare(String(y.id)))) {
    const ends = arr(r.ends) as RelationEndJson[];
    if (ends.length !== 2) continue;
    for (let i = 0; i < 2; i++) {
      const self = ends[i];
      const other = ends[1 - i];
      if (!other.id || seen.has(other.id)) continue;
      // The entity holds this end when its own end covers it: a one-type end that is it or a base, or an end with several types that covers one of them.
      const selfCovers = docEndHasSeveralTypes(self)
        ? severalCovers(self, mine, src.carriers?.(r, i))
        : (docEndSingleEntity(self) ?? "") !== "" && mine.has(docEndSingleEntity(self)!);
      if (!selfCovers) continue;
      if (docEndHasSeveralTypes(other)) {
        // Both ends with several types is refused (MQ3041); the end leads to every type it covers.
        if (docEndHasSeveralTypes(self)) continue;
        seen.add(other.id);
        const name = other.navigation || other.role || other.id;
        const covered = [...new Set([...docEndEntities(other), ...(src.carriers?.(r, 1 - i) ?? [])])];
        out.push({
          id: other.id,
          name,
          kind: "end",
          targets: covered,
          ...(other.anyEntity ? { anyEntity: true } : {}),
          typesLabel: endLabel(
            {
              entity: undefined,
              anyEntity: other.anyEntity,
              entities: docEndEntities(other),
              stereotypes: arr(other.types).flatMap((t) => (typeof t.stereotype === "string" ? [t.stereotype] : [])),
            },
            (id) => src.nameOf?.(id) ?? (typeof src.doc(id)?.name === "string" ? String(src.doc(id)!.name) : undefined),
            (key) => key,
          ),
          many: other.max !== 1,
          valueObject: false,
        });
        continue;
      }
      const target = docEndSingleEntity(other);
      if (!target) continue;
      seen.add(other.id);
      const name = other.navigation || other.role || String(src.nameOf?.(target) ?? src.doc(target)?.name ?? other.id);
      out.push({ id: other.id, name, kind: "end", target, many: other.max !== 1, valueObject: false });
    }
  }
  return out;
}

type RelationEndJson = EndDocLike & { id?: string; role?: string; navigation?: string; max?: unknown };

/** Whether an end with several types covers one of `mine` (an entity and its bases): `anyEntity`, a listed entity, or a carrier. */
function severalCovers(end: RelationEndJson, mine: ReadonlySet<string>, carriers: readonly string[] | undefined): boolean {
  if (end.anyEntity) return true;
  return [...docEndEntities(end), ...(carriers ?? [])].some((id) => mine.has(id));
}

/** The fields every covered entity has by id, for a path that goes on through an end with several types. */
export function targetFields(field: ObjectField, fieldsOf: (entity: string) => readonly ObjectField[]): ObjectField[] {
  if (field.target) return [...fieldsOf(field.target)];
  if (field.anyEntity || !field.targets?.length) return [];
  return commonFields(field.targets.map(fieldsOf));
}

/** An end's text in a picker: its name, and for an end with several types the types it leads to, "attachedTo (Part, Assembly or any entity)". */
export function fieldOptionLabel(f: ObjectField): string {
  return f.typesLabel ? `${f.name} (${f.typesLabel})` : f.name;
}

/** The fields every one of the entities has by id: what an id can mean for all the subjects of a process. */
export function commonFields(lists: readonly (readonly ObjectField[])[]): ObjectField[] {
  if (!lists.length) return [];
  return lists[0].filter((f) => lists.every((l) => l.some((g) => g.id === f.id && g.kind === f.kind && g.target === f.target)));
}

/** Whether a field can be where one actor is named: single-valued, and an attribute that is no value object, or a to-one end. */
export const holdsOneActor = (f: ObjectField): boolean => !f.many && !f.valueObject;

const PERSON_TYPES = new Set(["string", "text", "guid", "uuid", "int16", "int32", "int64"]);

/** Whether a field can name a person: a to-one end, or a single-valued attribute typed string, text, guid, uuid or a whole number. Guidance only. */
export const canNamePerson = (f: ObjectField): boolean => holdsOneActor(f) && (f.kind === "end" || (!!f.scalar && PERSON_TYPES.has(f.scalar.toLowerCase())));

/** The fields an Owner picker offers: those that can name a person (a stereotype's attributes too, stereotype-sets-design.md decision 7). */
export const canBeOwner = (f: ObjectField): boolean => canNamePerson(f);

/**
 * Why `steps` cannot be worked out from `entity`, or null. A path follows to-one ends and may finish at a single-valued
 * attribute or a to-one end (`finish: "field"`); a context (`within`) is to-one ends only (`finish: "end"`).
 */
export function pathProblem(
  entity: string,
  steps: readonly string[],
  fieldsOf: (entity: string) => readonly ObjectField[],
  finish: "field" | "end",
): string | null {
  // `at` is the one entity the path stands on; through an end with several types it stands on what every covered entity has (`current`).
  let at: string | null = entity;
  let current: readonly ObjectField[] = [];
  for (let i = 0; i < steps.length; i++) {
    const field: ObjectField | undefined = (at === null ? current : fieldsOf(at)).find((f) => f.id === steps[i]);
    if (!field) return `'${steps[i]}' is no attribute or end of this entity or what it derives from`;
    const last = i === steps.length - 1;
    if (!last || finish === "end") {
      if (field.kind !== "end") return `'${field.name}' is an attribute, so the path cannot go on through it`;
      if (field.many) return `'${field.name}' leads to many, so the path cannot go on through it`;
      if (field.target) at = field.target;
      else {
        current = targetFields(field, fieldsOf);
        at = null;
      }
    } else if (field.many) return `'${field.name}' holds many values, not one actor`;
    else if (field.valueObject) return `'${field.name}' is a value object, which is several fields and not one actor`;
  }
  return null;
}

/** The entity a path of to-one ends reaches, or undefined when it does not resolve. */
export function entityAt(entity: string, ends: readonly string[], fieldsOf: (entity: string) => readonly ObjectField[]): string | undefined {
  let at: string | undefined = entity;
  for (const id of ends) {
    const f: ObjectField | undefined = at ? fieldsOf(at).find((x) => x.id === id) : undefined;
    if (!f || f.kind !== "end" || f.many) return undefined;
    at = f.target;
  }
  return at;
}

export type RelativeDoc = { owner?: true; path?: string[]; membersOf?: string; within?: string[] };
export type RelativeForm = "owner" | "path" | "members-of";

/** The forms a relative gives, in the order owner, path, members-of. */
export function relativeForms(r: RelativeDoc | null | undefined): RelativeForm[] {
  if (!r || typeof r !== "object") return [];
  const out: RelativeForm[] = [];
  if (r.owner === true) out.push("owner");
  if (Array.isArray(r.path)) out.push("path");
  if (typeof r.membersOf === "string") out.push("members-of");
  return out;
}

/** "accountManager" as "account manager". */
export function spaced(name: string): string {
  return name.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase();
}

/** A relative in plain words: "the order's account manager". `subject` names the object's entity when it is known. */
export function relativeMeaning(r: RelativeDoc | null | undefined, names: { subject?: string; field(id: string): string; actor(id: string): string }): string {
  const object = names.subject ? `the ${spaced(names.subject)}'s` : "the object's";
  const form = relativeForms(r)[0];
  const words = (ids: readonly string[]) => ids.map((id) => spaced(names.field(id)));
  if (form === "owner") return `${object} owner`;
  if (form === "path") return `${object} ${words(r!.path ?? []).join("'s ")}`;
  if (form === "members-of") {
    const within = words(r!.within ?? []);
    const itself = names.subject ? `the ${spaced(names.subject)} itself` : "the object itself";
    return `the ${names.actor(r!.membersOf!)} members ${within.length ? `of ${object} ${within.join("'s ")}` : `of ${itself}`}`;
  }
  return "";
}

/** The text after the type in an actor row: how it is worked out, from the index row's `relative`. */
export function relativeRowText(form: string | undefined): string {
  switch (form) {
    case "owner":
      return "owner";
    case "path":
      return "from a field";
    case "members-of":
      return "within a context";
    default:
      return form ? "from the object" : "";
  }
}
