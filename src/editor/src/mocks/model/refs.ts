// Where a model document references other elements, as the engine's ReferenceWalker reports it:
// the holder (element or sub-element id), the JSON pointer, the field and the referenced id.
// `required` marks references that `remove-references` cannot clear (a delete becomes invalid).
import { BUILTIN_TYPES } from "@/model/model";
import { subjectsOf } from "@/model/process";

type Json = Record<string, unknown>;

export interface Ref {
  fromId: string;
  pointer: string;
  field: string;
  toId: string;
  required: boolean;
  /** How remove-references clears it: drop the array item at `removeItem`, or delete the field. */
  removeItem?: string;
  /** A required reference that remove-references answers by dropping the item at `removeItem` (a scenario object whose entity goes). */
  drops?: boolean;
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
/** The built-in type keywords (schemas/v1/common.json builtinType): a type slot holding one names no element. */
const BUILTIN: ReadonlySet<string> = new Set(BUILTIN_TYPES);
const str = (v: unknown): string | null => (typeof v === "string" ? v : null);

function attributeRefs(owner: string, base: string, attributes: unknown, out: Ref[]): void {
  arr(attributes).forEach((a, i) => {
    const holder = str(a.id) ?? owner;
    const type = a.type as Json | string | undefined;
    if (type && typeof type === "object" && str(type.ref))
      out.push({ fromId: holder, pointer: `${base}/${i}/type/ref`, field: "ref", toId: type.ref as string, required: true });
    if (str(a.category)) out.push({ fromId: holder, pointer: `${base}/${i}/category`, field: "category", toId: a.category as string, required: false });
  });
}

/** Finds the seed row a code of a bound attribute's reference type names (lifecycle-design.md decision 18). */
export interface RowResolver {
  rowOf(entityId: string, attributeId: string, code: unknown): string | null;
}

/**
 * A row resolver over a model's documents, built on first use: the bound attribute (own, a base's or a stereotype's) gives
 * the reference type, whose seeds give the row of a code (1 and "1" are one code).
 */
export function rowResolverOf(docs: () => Iterable<Json>): RowResolver {
  let built: { byId: Map<string, Json>; rows: Map<string, Map<string, string>>; stereotypes: Map<string, Json> } | null = null;
  const build = () => {
    if (built) return built;
    const byId = new Map<string, Json>();
    const rows = new Map<string, Map<string, string>>();
    const stereotypes = new Map<string, Json>();
    for (const d of docs()) {
      byId.set(String(d.id), d);
      if (d.kind === "stereotype") stereotypes.set(String(d.key), d);
      if (d.kind !== "seed" || typeof d.target !== "string" || d.rowsFrom !== undefined) continue;
      const at = ((d.columns as string[] | undefined) ?? []).indexOf("code");
      if (at < 0) continue;
      const map = rows.get(d.target) ?? new Map<string, string>();
      for (const r of arr(d.rows)) {
        const code = ((r.values as unknown[] | undefined) ?? [])[at];
        if ((typeof code === "string" || typeof code === "number") && !map.has(String(code))) map.set(String(code), String(r.id));
      }
      rows.set(d.target, map);
    }
    return (built = { byId, rows, stereotypes });
  };
  return {
    rowOf(entityId, attributeId, code) {
      if (typeof code !== "string" && typeof code !== "number") return null;
      const { byId, rows, stereotypes } = build();
      const seen = new Set<string>();
      for (let at = byId.get(entityId); at && !seen.has(String(at.id)); at = typeof at.base === "string" ? byId.get(at.base) : undefined) {
        seen.add(String(at.id));
        const held = [...arr(at.attributes), ...((at.stereotypes as string[] | undefined) ?? []).flatMap((k) => arr(stereotypes.get(k)?.attributes))];
        const attribute = held.find((a) => a.id === attributeId);
        if (!attribute) continue;
        const ref = (attribute.type as Json | undefined)?.ref;
        return typeof ref === "string" && byId.get(ref)?.kind === "reference-type" ? (rows.get(ref)?.get(String(code)) ?? null) : null;
      }
      return null;
    },
  };
}

/**
 * Every outgoing reference of one document. Intra-document references (a key naming its own attribute) are left out. With
 * `rows`, a lifecycle's state codes under a reference binding are references to the rows, field `code`, held by the state.
 */
export function referencesOf(doc: Json, rows?: RowResolver): Ref[] {
  const id = str(doc.id) ?? "";
  const out: Ref[] = [];
  const own = new Set(subElementIds(doc));
  const push = (pointer: string, field: string, value: unknown, required: boolean, removeItem?: string) => {
    const to = str(value);
    if (to && !own.has(to) && to !== id) out.push({ fromId: id, pointer, field, toId: to, required, removeItem });
  };
  push("/package", "package", doc.package, false);
  push("/category", "category", doc.category, false);
  switch (doc.kind) {
    case "package":
      push("/parent", "parent", doc.parent, false);
      break;
    case "entity":
      push("/base", "base", doc.base, false);
      attributeRefs(id, "/attributes", doc.attributes, out);
      // A binding's database, source and write table (erratum E43): removing what it names drops the binding, never the entity.
      arr(doc.bindings).forEach((b, i) => {
        const at = `/bindings/${i}`;
        push(`${at}/database`, "database", b.database, false, at);
        // A synthesized table key (<entityId>@<databaseId>) is no element id.
        if (str(b.source) && !(b.source as string).includes("@")) push(`${at}/source`, "source", b.source, false, at);
        const write = b.write as Json | string | undefined;
        if (write && typeof write === "object" && str(write.table) && !(write.table as string).includes("@"))
          push(`${at}/write/table`, "table", write.table, false, at);
      });
      break;
    case "actor":
      // A deleted role, group or organization leaves its members' memberOf lists (actors-design.md decision 3).
      arr(doc.memberOf).forEach((_, i) => push(`/memberOf/${i}`, "memberOf", (doc.memberOf as unknown[])[i], false, `/memberOf/${i}`));
      // An actor from the object needs the fields and the role it names (decision 9): the delete is refused under
      // remove-references unless the actor goes too, and delete-dependents deletes the actor.
      if (doc.relative && typeof doc.relative === "object") {
        const relative = doc.relative as Json;
        (Array.isArray(relative.path) ? (relative.path as unknown[]) : []).forEach((p, i) => push(`/relative/path/${i}`, "path", p, true));
        push("/relative/membersOf", "membersOf", relative.membersOf, true);
        (Array.isArray(relative.within) ? (relative.within as unknown[]) : []).forEach((p, i) => push(`/relative/within/${i}`, "within", p, true));
      }
      break;
    case "value-object":
    case "stereotype":
      attributeRefs(id, "/attributes", doc.attributes, out);
      break;
    case "reference-type":
      attributeRefs(id, "/attributes", doc.attributes, out);
      // A binding's database and table, and the columns of its fields and constants (2026-10-11): removing the table drops the
      // binding, removing a column drops the entry that names it; the type stays.
      arr(doc.bindings).forEach((b, i) => {
        const at = `/bindings/${i}`;
        push(`${at}/database`, "database", b.database, false, at);
        push(`${at}/table`, "table", b.table, false, at);
        for (const part of ["fields", "constants"] as const)
          arr(b[part]).forEach((x, j) => {
            if (ULID.test(str(x.column) ?? "")) push(`${at}/${part}/${j}/column`, "column", x.column, false, `${at}/${part}/${j}`);
          });
      });
      break;
    case "relation":
      arr(doc.ends).forEach((e, i) => {
        if (str(e.entity)) out.push({ fromId: str(e.id) ?? id, pointer: `/ends/${i}/entity`, field: "entity", toId: e.entity as string, required: true });
      });
      attributeRefs(id, "/attributes", doc.attributes, out);
      break;
    case "diagram":
      arr(doc.members).forEach((m, i) => push(`/members/${i}/element`, "element", m.element, false, `/members/${i}`));
      break;
    case "database":
      // An entry is a package id or { package, schema } (erratum E26).
      (Array.isArray(doc.packages) ? (doc.packages as unknown[]) : []).forEach((p, i) =>
        typeof p === "string"
          ? push(`/packages/${i}`, "packages", p, false, `/packages/${i}`)
          : push(`/packages/${i}/package`, "packages", (p as { package?: unknown }).package, false, `/packages/${i}`),
      );
      break;
    case "mapping":
      push("/database", "database", doc.database, true);
      push("/entity", "entity", doc.entity, true);
      push("/relation", "relation", doc.relation, true);
      push("/table", "table", doc.table, false);
      push("/foreignKeyEnd", "foreignKeyEnd", doc.foreignKeyEnd, false);
      arr(doc.attributes).forEach((a, i) => {
        push(`/attributes/${i}/attribute`, "attribute", a.attribute, false, `/attributes/${i}`);
        push(`/attributes/${i}/column`, "column", a.column, false);
      });
      arr(doc.ends).forEach((e, i) => push(`/ends/${i}/end`, "end", e.end, false, `/ends/${i}`));
      break;
    case "table":
      push("/database", "database", doc.database, true);
      // An overlay table names its entity or relation (exactly one of them): it cannot lose it.
      push("/entity", "entity", doc.entity, true);
      push("/relation", "relation", doc.relation, true);
      push("/schema", "schema", doc.schema, false);
      arr(doc.columns).forEach((c, i) => {
        // A column key is an attribute path, or a foreign key column's `<endId>.<keyAttributeId>`: each segment is a reference.
        for (const to of (str(c.attribute) ?? "").split(".").filter(Boolean))
          if (!own.has(to)) out.push({ fromId: str(c.id) ?? id, pointer: `/columns/${i}/attribute`, field: "attribute", toId: to, required: false });
      });
      arr(doc.indexes).forEach((ix, i) =>
        arr(ix.columns).forEach((c, j) => {
          const to = str(c.column);
          if (to && !own.has(to))
            out.push({ fromId: str(ix.id) ?? id, pointer: `/indexes/${i}/columns/${j}/column`, field: "column", toId: to, required: true });
        }),
      );
      break;
    case "process": {
      push("/subject", "subject", doc.subject, false);
      push("/boundAttribute", "boundAttribute", doc.boundAttribute, false);
      // A deleted entity takes its entry with it, under either delete choice (lifecycle-design.md decision 5).
      arr(doc.subjects).forEach((e, i) => {
        push(`/subjects/${i}/entity`, "entity", e.entity, false, `/subjects/${i}`);
        push(`/subjects/${i}/boundAttribute`, "boundAttribute", e.boundAttribute, false);
        // The owner field is optional: a delete clears the key (decision 10).
        push(`/subjects/${i}/owner`, "owner", e.owner, false);
      });
      arr(doc.events).forEach((e, i) =>
        arr(e.actors).forEach((_, j) => push(`/events/${i}/actors/${j}`, "actors", (e.actors as unknown[])[j], false, `/events/${i}/actors/${j}`)),
      );
      const bound = rows && doc.use === "lifecycle" ? subjectsOf(doc).filter((e) => e.boundAttribute) : [];
      const states = (list: unknown, base: string) =>
        arr(list).forEach((st, i) => {
          // A code under a reference binding is a use of the row it names (decision 18): one reference per binding it fits.
          const seen = new Set<string>();
          for (const e of bound) {
            const row = rows!.rowOf(e.entity, e.boundAttribute!, st.code);
            if (!row || !str(st.id) || seen.has(row)) continue;
            seen.add(row);
            out.push({ fromId: st.id as string, pointer: `${base}/${i}/code`, field: "code", toId: row, required: true });
          }
          arr(st.invoke).forEach((inv, k) => {
            push(`${base}/${i}/invoke/${k}/process`, "process", inv.process, false);
            arr(inv.actors).forEach((a, j) => push(`${base}/${i}/invoke/${k}/actors/${j}`, "actors", a, false, `${base}/${i}/invoke/${k}/actors/${j}`));
          });
          states(st.states, `${base}/${i}/states`);
        });
      states(doc.states, "/states");
      arr(doc.transitions).forEach((t, i) => {
        const gate = t.gate as Json | undefined;
        if (!gate) return;
        arr(gate.signers).forEach((a, j) => push(`/transitions/${i}/gate/signers/${j}`, "signers", a, true));
        arr(gate.requiredActors).forEach((a, j) =>
          push(`/transitions/${i}/gate/requiredActors/${j}`, "requiredActors", a, false, `/transitions/${i}/gate/requiredActors/${j}`),
        );
      });
      break;
    }
    case "scenario":
      push("/process", "process", doc.process, true);
      // Objects (lifecycle-design.md decision 22): the entity an object is an instance of, the attributes its values name and
      // the relation ends its links navigate. The subject and a link's object name objects of the same file (no reference leaves it).
      arr(doc.objects).forEach((o, i) => {
        const at = `/objects/${i}`;
        const holder = str(o.id) ?? id;
        const add = (pointer: string, field: string, to: unknown, required: boolean, removeItem?: string, drops?: boolean) => {
          const t = str(to);
          if (t) out.push({ fromId: holder, pointer, field, toId: t, required, removeItem, ...(drops ? { drops } : {}) });
        };
        add(`${at}/entity`, "entity", o.entity, true, at, true);
        if (o.values && typeof o.values === "object") for (const key of Object.keys(o.values as Json)) add(`${at}/values/${key}`, "values", key, false);
        arr(o.links).forEach((l, j) => add(`${at}/links/${j}/end`, "end", l.end, false, `${at}/links/${j}`));
      });
      break;
    case "sequence":
    case "view":
      push("/database", "database", doc.database, true);
      push("/schema", "schema", doc.schema, false);
      break;
    case "routine":
    case "database-type":
    case "sql-object": {
      push("/database", "database", doc.database, true);
      push("/schema", "schema", doc.schema, false);
      arr(doc.dependsOn).forEach((_, i) => push(`/dependsOn/${i}`, "dependsOn", (doc.dependsOn as unknown[])[i], false, `/dependsOn/${i}`));
      // A type slot naming a database type (not a built-in keyword): the routine or type cannot do without it.
      const typeRef = (pointer: string, row: unknown) => {
        const type = (row as Json | undefined)?.type;
        if (typeof type === "string" && !BUILTIN.has(type)) push(pointer, "type", type, true);
      };
      arr(doc.parameters).forEach((p, i) => typeRef(`/parameters/${i}/type`, p));
      typeRef("/returns/type", doc.returns);
      arr((doc.returns as Json | undefined)?.table).forEach((c, i) => typeRef(`/returns/table/${i}/type`, c));
      arr(doc.fields).forEach((f, i) => typeRef(`/fields/${i}/type`, f));
      break;
    }
    case "query": {
      push("/database", "database", doc.database, true);
      push("/entity", "entity", doc.entity, false);
      // Keyed references (a source's table key, a column's alias.key, a call's routine, a parameter's database type, a collection's
      // attribute or end): every segment that is an id; a field's attribute and a collection's entity: the id itself.
      const keyed = new Set(["source", "column", "call", "type", "attribute"]);
      const walk = (node: unknown, pointer: string): void => {
        if (Array.isArray(node)) return node.forEach((x, i) => walk(x, `${pointer}/${i}`));
        if (!node || typeof node !== "object") return;
        for (const [key, value] of Object.entries(node as Json)) {
          const at = `${pointer}/${key}`;
          if (typeof value === "string" && keyed.has(key)) {
            for (const to of value.split(/[@.]/).filter((x) => ULID.test(x)))
              if (!own.has(to) && to !== id) out.push({ fromId: id, pointer: at, field: key, toId: to, required: true });
          } else if (typeof value === "string" && key === "entity" && pointer !== "") push(at, key, value, false);
          else walk(value, at);
        }
      };
      for (const key of ["parameters", "from", "joins", "select", "where", "groupBy", "having", "orderBy", "collections"]) walk(doc[key], `/${key}`);
      break;
    }
  }
  return out;
}

const ULID = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/;

/** Ids of the sub-elements a document holds (attributes, ends, keys, members, columns…). */
export function subElementIds(doc: Json): string[] {
  const ids: string[] = [];
  const collect = (list: unknown) => arr(list).forEach((x) => str(x.id) && ids.push(x.id as string));
  collect(doc.attributes);
  collect(doc.ends);
  collect(doc.alternateKeys);
  collect(doc.categories);
  collect(doc.schemas);
  collect(doc.columns);
  collect(doc.indexes);
  collect(doc.checks);
  if (doc.kind === "entity" || doc.kind === "reference-type") collect(doc.bindings);
  if (doc.kind === "enum") collect(doc.members);
  if (doc.kind === "process") {
    collect(doc.subjects);
    collect(doc.context);
    collect(doc.events);
    collect(doc.guards);
    collect(doc.actions);
    const states = (list: unknown) =>
      arr(list).forEach((st) => {
        if (str(st.id)) ids.push(st.id as string);
        collect(st.invoke);
        states(st.states);
      });
    states(doc.states);
    arr(doc.transitions).forEach((t) => {
      if (str(t.id)) ids.push(t.id as string);
      const gate = t.gate as Json | undefined;
      if (gate && str(gate.id)) ids.push(gate.id as string);
      if (gate) collect(gate.meanings);
    });
  }
  if (doc.kind === "scenario") {
    collect(doc.steps);
    collect(doc.objects);
  }
  // A seed's rows hold ids: a lifecycle's state code under a reference binding is a use of its row.
  if (doc.kind === "seed") collect(doc.rows);
  return ids;
}
