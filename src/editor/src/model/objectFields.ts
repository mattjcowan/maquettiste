// The fields of a process's object, for an actor taken from the object (actors-design.md decisions 8 to 11) and for a
// subject's owner: an entity's attributes (own, inherited and from its stereotypes) and the to-one relation ends that
// lead from it, with the rule that works out whether a path resolves. Pure, so the mock's rules, the pickers and tests share it.
type Json = Record<string, unknown>;

export interface ObjectField {
  id: string;
  name: string;
  kind: "attribute" | "end";
  /** For an end, the entity it leads to. */
  target?: string;
  /** A collection attribute, or a to-many end. */
  many: boolean;
  /** An attribute typed by a value object, which is several fields and holds no single actor. */
  valueObject: boolean;
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

export interface FieldSources {
  doc(id: string): Json | undefined;
  /** Every relation document (two-ended ones lead somewhere). */
  relations: readonly Json[];
  stereotype(key: string): Json | undefined;
  kindOf(id: string): string | undefined;
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

/** An entity's fields: its attributes, its bases' and its stereotypes', then the ends that lead away from it (or a base). */
export function objectFields(entity: string, src: FieldSources): ObjectField[] {
  const chain = entityChain(entity, src.doc);
  const out: ObjectField[] = [];
  const seen = new Set<string>();
  for (const level of chain) {
    const held = [...arr(level.attributes), ...((level.stereotypes as string[] | undefined) ?? []).flatMap((k) => arr(src.stereotype(k)?.attributes))];
    for (const a of held) {
      const id = typeof a.id === "string" ? a.id : "";
      if (!id || seen.has(id)) continue;
      seen.add(id);
      const type = a.type as { ref?: unknown } | string | undefined;
      const ref = type && typeof type === "object" && typeof type.ref === "string" ? type.ref : undefined;
      out.push({ id, name: String(a.name ?? id), kind: "attribute", many: a.collection === true, valueObject: !!ref && src.kindOf(ref) === "value-object" });
    }
  }
  const mine = new Set(chain.map((c) => String(c.id)));
  for (const r of [...src.relations].sort((x, y) => String(x.id).localeCompare(String(y.id)))) {
    const ends = arr(r.ends) as { id?: string; entity?: string; role?: string; navigation?: string; max?: unknown }[];
    if (ends.length !== 2) continue;
    for (let i = 0; i < 2; i++) {
      const self = ends[i];
      const other = ends[1 - i];
      if (!self.entity || !mine.has(self.entity) || !other.id || !other.entity || seen.has(other.id)) continue;
      seen.add(other.id);
      const name = other.navigation || other.role || String(src.doc(other.entity)?.name ?? other.id);
      out.push({ id: other.id, name, kind: "end", target: other.entity, many: other.max !== 1, valueObject: false });
    }
  }
  return out;
}

/** The fields every one of the entities has by id: what an id can mean for all the subjects of a process. */
export function commonFields(lists: readonly (readonly ObjectField[])[]): ObjectField[] {
  if (!lists.length) return [];
  return lists[0].filter((f) => lists.every((l) => l.some((g) => g.id === f.id && g.kind === f.kind && g.target === f.target)));
}

/** Whether a field can be where one actor is named: single-valued, and an attribute that is no value object, or a to-one end. */
export const holdsOneActor = (f: ObjectField): boolean => !f.many && !f.valueObject;

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
  let at = entity;
  for (let i = 0; i < steps.length; i++) {
    const field = fieldsOf(at).find((f) => f.id === steps[i]);
    if (!field) return `'${steps[i]}' is no attribute or end of this entity or what it derives from`;
    const last = i === steps.length - 1;
    if (!last || finish === "end") {
      if (field.kind !== "end") return `'${field.name}' is an attribute, so the path cannot go on through it`;
      if (field.many) return `'${field.name}' leads to many, so the path cannot go on through it`;
      at = field.target!;
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
