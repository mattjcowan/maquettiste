// Promote to entity (explorer-redesign.md 1.8): a value object or a custom type becomes an entity, and its uses are
// rewritten. Pure: the caller loads the documents and sends the plan as one batch (creates, updates, then the delete).
//
// - The entity keeps the name, domain, display names, description, tags and category; a value object's attributes
//   carry over, a custom type becomes one required `value` attribute of its base type (length, precision, scale).
//   It gets a uuid-v7 `id` key (`<name>Id` when an attribute is already called id).
// - Each entity attribute typed as the promoted element becomes a relationship from that entity to the new one: the
//   attribute's name is the navigation on the owner; a single value is many-to-one (min 1 when the attribute was
//   required), a collection one-to-many. The attribute leaves the owner.
// - What still points at a removed attribute is rewritten too (`dependents`: the owners' mapping elements and seeds): a
//   mapping's attribute row for it is dropped (the new relationship's foreign key maps by convention), a seed's column for
//   it is dropped with its cells. A seed left with no column blocks.
// - Other elements that refer to the promoted element without an attribute: a diagram shows the new entity in its
//   place; any other kind blocks, since the delete would leave it dangling. `rewritten` lists every rewrite.
// - A use the rewrite cannot express (an attribute of a value object or a relationship, or an attribute in a key)
//   blocks the promotion; `blocked` names each one.

export interface PromoteAttribute {
  id: string;
  name: string;
  type?: unknown;
  required?: boolean;
  collection?: boolean;
  [field: string]: unknown;
}

export interface PromoteSource {
  kind: string;
  id: string;
  name: string;
  package?: string;
  displayName?: string;
  pluralName?: string;
  description?: unknown;
  tags?: string[];
  category?: string;
  attributes?: PromoteAttribute[];
  base?: string;
  length?: number;
  precision?: number;
  scale?: number;
}

export interface PromoteUser {
  kind: string;
  id: string;
  name: string;
  package?: string;
  attributes?: PromoteAttribute[];
  key?: { attributes?: string[] };
  alternateKeys?: { attributes?: string[] }[];
  /** A seed's columns and rows, a diagram's members (a mapping's attribute rows arrive as `attributes`). */
  columns?: string[];
  rows?: { id: string; values?: unknown[] }[];
  members?: { element: string; [field: string]: unknown }[];
  [field: string]: unknown;
}

export interface PromotePlan {
  entity: Record<string, unknown>;
  relations: Record<string, unknown>[];
  /** The rewritten users (the attribute removed) and dependents (mapping rows, seed columns, diagram members). */
  updates: { id: string; json: Record<string, unknown> }[];
  /** What the rewrite changes besides the new relationships, one line each, for the preview. */
  rewritten: string[];
  /** Uses the rewrite cannot express, as "Owner.attribute" with the reason; the promotion is refused when any. */
  blocked: string[];
}

export const PROMOTABLE_KINDS: ReadonlySet<string> = new Set(["value-object", "scalar-type"]);

const refersTo = (type: unknown, id: string) => !!type && typeof type === "object" && (type as { ref?: unknown }).ref === id;
const cap = (s: string) => (s ? s[0].toUpperCase() + s.slice(1) : s);
const camel = (s: string) => (s ? s[0].toLowerCase() + s.slice(1) : s);

/**
 * Plans the promotion. `users` are the elements that refer to the source; `dependents` the elements that refer to the
 * owner entities (their mapping elements and seeds), rewritten where they point at an attribute the plan removes.
 */
export function planPromotion(source: PromoteSource, users: readonly PromoteUser[], newId: () => string, dependents: readonly PromoteUser[] = []): PromotePlan {
  const id = newId();
  const carried: PromoteAttribute[] =
    source.kind === "scalar-type"
      ? [
          {
            id: newId(),
            name: "value",
            type: source.base ?? "string",
            ...(source.length !== undefined ? { length: source.length } : {}),
            ...(source.precision !== undefined ? { precision: source.precision } : {}),
            ...(source.scale !== undefined ? { scale: source.scale } : {}),
            required: true,
          },
        ]
      : (source.attributes ?? []).map((a) => structuredClone(a));
  const keyId = newId();
  const keyName = carried.some((a) => a.name === "id") ? `${camel(source.name)}Id` : "id";
  const entity: Record<string, unknown> = {
    kind: "entity",
    id,
    name: source.name,
    ...(source.displayName ? { displayName: source.displayName } : {}),
    ...(source.pluralName ? { pluralName: source.pluralName } : {}),
    ...(source.package ? { package: source.package } : {}),
    ...(source.description !== undefined ? { description: source.description } : {}),
    ...(source.tags?.length ? { tags: [...source.tags] } : {}),
    ...(source.category ? { category: source.category } : {}),
    key: { attributes: [keyId], strategy: "uuid-v7" },
    attributes: [{ id: keyId, name: keyName, type: "uuid", required: true }, ...carried],
  };

  const relations: Record<string, unknown>[] = [];
  const edited = new Map<string, PromoteUser>();
  const blocked: string[] = [];
  const rewritten: string[] = [];
  const names = new Set<string>();
  const removed = new Map<string, string>(); // attribute id -> "Owner.attribute"
  const byName = (a: PromoteUser, b: PromoteUser) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id);
  const edit = (user: PromoteUser) => {
    let json = edited.get(user.id);
    if (!json) edited.set(user.id, (json = structuredClone(user)));
    return json;
  };
  for (const user of [...users].sort(byName)) {
    const uses = (user.attributes ?? []).filter((a) => refersTo(a.type, source.id));
    if (!uses.length) {
      if (user.kind === "diagram" && user.members?.some((m) => m.element === source.id)) {
        const json = edit(user);
        json.members = (json.members ?? []).map((m) => (m.element === source.id ? { ...m, element: id } : m));
        rewritten.push(`Diagram ${user.name} shows the new entity in place of ${source.name}`);
      } else if (user.kind === "mapping" || user.kind === "seed") {
        blocked.push(`${user.name} (a ${user.kind} that refers to ${source.name}; remove the reference first)`);
      } else {
        blocked.push(`${user.name} (a ${user.kind} that refers to ${source.name} without an attribute; remove the reference first)`);
      }
      continue;
    }
    if (user.kind !== "entity") {
      for (const a of uses) blocked.push(`${user.name}.${a.name} (a ${user.kind === "relation" ? "relationship" : "value object"} cannot hold a relationship)`);
      continue;
    }
    const keyed = new Set([...(user.key?.attributes ?? []), ...(user.alternateKeys ?? []).flatMap((k) => k.attributes ?? [])]);
    const inKey = uses.filter((a) => keyed.has(a.id));
    if (inKey.length) {
      for (const a of inKey) blocked.push(`${user.name}.${a.name} (part of a key)`);
      continue;
    }
    const json = edit(user);
    json.attributes = (json.attributes ?? []).filter((a) => !refersTo(a.type, source.id));
    for (const a of uses) {
      removed.set(a.id, `${user.name}.${a.name}`);
      let name = `${user.name}${cap(a.name)}`;
      for (let n = 2; names.has(name); n++) name = `${user.name}${cap(a.name)}${n}`;
      names.add(name);
      const many = a.collection === true;
      relations.push({
        kind: "relation",
        id: newId(),
        name,
        ...(user.package ? { package: user.package } : {}),
        ends: [
          { id: newId(), entity: user.id, role: camel(user.name), navigation: "", min: many ? 1 : 0, max: many ? 1 : "*" },
          { id: newId(), entity: id, role: a.name, navigation: a.name, min: !many && a.required ? 1 : 0, max: many ? "*" : 1 },
        ],
      });
    }
  }
  for (const dep of [...dependents].sort(byName)) {
    if (dep.kind === "mapping") {
      const rows = (dep.attributes ?? []) as unknown as { attribute?: string }[];
      const gone = rows.filter((r) => r.attribute && removed.has(r.attribute));
      if (!gone.length) continue;
      const json = edit(dep);
      json.attributes = rows.filter((r) => !(r.attribute && removed.has(r.attribute))) as unknown as PromoteAttribute[];
      for (const r of gone) rewritten.push(`Mapping ${dep.name} drops its row for ${removed.get(r.attribute!)} (the relationship maps by convention)`);
    } else if (dep.kind === "seed") {
      const columns = dep.columns ?? [];
      const drop = columns.map((c, i) => (removed.has(c) ? i : -1)).filter((i) => i >= 0);
      if (!drop.length) continue;
      if (drop.length === columns.length) {
        blocked.push(`${dep.name} (a seed whose only columns are the promoted attributes)`);
        continue;
      }
      const json = edit(dep);
      const keep = (_: unknown, i: number) => !drop.includes(i);
      json.columns = columns.filter(keep);
      json.rows = (dep.rows ?? []).map((r) => {
        const values = (r.values ?? []).filter(keep);
        while (values.length && values[values.length - 1] === null) values.pop();
        return { ...r, values };
      });
      for (const i of drop) rewritten.push(`Seed ${dep.name} drops its column for ${removed.get(columns[i])}`);
    }
  }
  const updates = [...edited.values()].map((json) => ({ id: json.id, json: json as Record<string, unknown> }));
  return { entity, relations, updates, blocked, rewritten };
}
