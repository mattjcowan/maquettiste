// Where a model document references other elements, as the engine's ReferenceWalker reports it:
// the holder (element or sub-element id), the JSON pointer, the field and the referenced id.
// `required` marks references that `remove-references` cannot clear (a delete becomes invalid).
import { BUILTIN_TYPES } from "@/model/model";
import { subjectsOf } from "@/model/process";
import { verbUses } from "./verbs";

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
  /** Removing it deletes the document that holds it, in either resolution (the last actor or end of a named right). */
  deletesHolder?: boolean;
  /**
   * A required reference whose holder is a part with no id of its own (an `ends` entry of a relation mapping, a constant): delete-dependents
   * removes the item at this pointer, remove-references refuses (several-types-design.md 19.6).
   */
  dependentItem?: string;
}

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
/** The built-in type keywords (schemas/v1/common.json builtinType): a type slot holding one names no element. */
const BUILTIN: ReadonlySet<string> = new Set(BUILTIN_TYPES);
const str = (v: unknown): string | null => (typeof v === "string" ? v : null);
const strings = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);

function attributeRefs(owner: string, base: string, attributes: unknown, out: Ref[]): void {
  arr(attributes).forEach((a, i) => {
    const holder = str(a.id) ?? owner;
    const type = a.type as Json | string | undefined;
    if (type && typeof type === "object" && str(type.ref))
      out.push({ fromId: holder, pointer: `${base}/${i}/type/ref`, field: "ref", toId: type.ref as string, required: true });
    if (str(a.category)) out.push({ fromId: holder, pointer: `${base}/${i}/category`, field: "category", toId: a.category as string, required: false });
  });
}

/**
 * The references of one `ends` entry of a relation mapping (several-types-design.md 19.6). What makes the entry point at its end (a key, an id
 * column, a type column, a referenced column) is required: losing it would leave the entry with no way or change what the id means, so
 * delete-dependents removes the whole entry and remove-references refuses. An item of `foreignKeys` or `types` follows the generic rule for
 * a list item, and the last one leaves the entry with no way, so the entry rule applies. An entity in a `types` item takes the item with it.
 */
function mappingEntryRefs(out: Ref[], id: string, own: ReadonlySet<string>, entry: Json, at: string): void {
  const add = (pointer: string, field: string, value: unknown, extra: Partial<Ref>) => {
    const to = str(value);
    if (to && !own.has(to) && to !== id) out.push({ fromId: id, pointer, field, toId: to, required: true, ...extra });
  };
  const whole = { dependentItem: at };
  add(`${at}/foreignKey`, "foreignKey", entry.foreignKey, whole);
  add(`${at}/idColumn`, "idColumn", entry.idColumn, whole);
  add(`${at}/typeColumn`, "typeColumn", entry.typeColumn, whole);
  add(`${at}/referencesColumn`, "referencesColumn", entry.referencesColumn, whole);
  const keys = strings(entry.foreignKeys);
  keys.forEach((k, j) => {
    const item = `${at}/foreignKeys/${j}`;
    if (keys.length > 1) add(item, "foreignKeys", k, { required: false, removeItem: item });
    else add(item, "foreignKeys", k, whole);
  });
  const types = arr(entry.types);
  types.forEach((t, j) => {
    const item = `${at}/types/${j}`;
    add(`${item}/entity`, "entity", t.entity, { required: false, removeItem: item });
    for (const field of ["foreignKey", "referencesColumn"] as const)
      add(`${item}/${field}`, field, t[field], types.length > 1 ? { required: false, removeItem: item } : whole);
  });
}

/** Finds the seed row a code of a bound attribute's reference type names (lifecycle-design.md decision 18). */
export interface RowResolver {
  rowOf(entityId: string, attributeId: string, code: unknown): string | null;
  /** The id of the stereotype with a key (a subject entry names one by key; stereotype-sets-design.md decision 2). */
  stereotypeIdOf?(key: string): string | null;
  /** The id of the project's own verb with a key (a use names it by key; verbs-design.md decision 7). Built-in keys give null. */
  verbIdOf?(key: string): string | null;
}

/**
 * A row resolver over a model's documents, built on first use: the bound attribute (own, a base's or a stereotype's) gives
 * the reference type, whose seeds give the row of a code (1 and "1" are one code).
 */
export function rowResolverOf(docs: () => Iterable<Json>): RowResolver {
  let built: { byId: Map<string, Json>; rows: Map<string, Map<string, string>>; stereotypes: Map<string, Json>; verbs: Map<string, string> } | null = null;
  const build = () => {
    if (built) return built;
    const byId = new Map<string, Json>();
    const rows = new Map<string, Map<string, string>>();
    const stereotypes = new Map<string, Json>();
    const verbs = new Map<string, string>();
    for (const d of docs()) {
      byId.set(String(d.id), d);
      if (d.kind === "stereotype") stereotypes.set(String(d.key), d);
      if (d.kind === "verb-vocabulary" && verbs.size === 0) for (const v of arr(d.verbs)) verbs.set(String(v.key), String(v.id));
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
    return (built = { byId, rows, stereotypes, verbs });
  };
  return {
    verbIdOf: (key) => build().verbs.get(key) ?? null,
    stereotypeIdOf(key) {
      const found = build().stereotypes.get(key);
      return found ? String(found.id) : null;
    },
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
  // A use of an own verb is a reference to its id; a built-in use is none, and a delete of the vocabulary clears the field.
  if (rows?.verbIdOf)
    for (const use of verbUses(doc)) {
      const verb = rows.verbIdOf(use.key);
      if (!verb) continue;
      const base = { fromId: use.holder ?? id, pointer: use.pointer, field: "verb", toId: verb };
      // A right's verbs are a list: the entry leaves it, and the right goes with its last verb (rights-design.md section 8).
      const at = use.owner === "right" || use.owner === "trigger-point" ? rightVerbAt(doc, use.pointer) : null;
      // A project verb named in a signature's notBy: the entry goes, and dropping it widens nothing that could occur (signoff-design.md section 9).
      if (use.owner === "not-by") out.push({ ...base, required: false, removeItem: use.pointer.slice(0, use.pointer.lastIndexOf("/")) });
      else if (!at) out.push({ ...base, required: false });
      else if (!at.last) out.push({ ...base, required: false, removeItem: use.pointer });
      else if (at.whole) out.push({ ...base, required: false, removeItem: at.right, drops: true });
      else out.push({ ...base, required: true });
    }
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
    case "trigger-point": {
      // A named trigger point (trigger-points-design.md decision 33): its subjects, the entities it skips, and what an inline point names.
      const subjects = arr(doc.subjects);
      subjects.forEach((e, i) => {
        const at = `/subjects/${i}`;
        const alone = subjects.length === 1;
        const to = str(e.entity) ?? (typeof e.stereotype === "string" ? rows?.stereotypeIdOf?.(e.stereotype) : null);
        const field = str(e.entity) ? "entity" : "stereotype";
        // The last subject takes the point with it: an empty list would run it everywhere.
        if (to && !own.has(to) && to !== id)
          out.push({ fromId: id, pointer: `${at}/${field}`, field, toId: to, required: false, ...(alone ? { deletesHolder: true } : { removeItem: at }) });
      });
      arr(doc.exceptEntities as unknown).forEach((_, j) =>
        push(`/exceptEntities/${j}`, "exceptEntities", (doc.exceptEntities as unknown[])[j], false, `/exceptEntities/${j}`),
      );
      triggerRefs(out, doc, "", id, own);
      break;
    }
    case "right":
      // A named right (rights-design.md decision 7). It cannot drop itself from the cascade, so its last actor, end or verb is a
      // required reference, which the delete refuses unless the right goes with it.
      rightRefs(out, doc, "", id, false, own);
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
        // An entry of an end's types (several-types-design.md decision 21): a deleted entity or stereotype takes its entry with it, under either
        // delete choice. The entries that remain are checked as a group in the cascade: an end left with none loses its relation.
        const types = arr(e.types);
        types.forEach((t, j) => {
          const at = `/ends/${i}/types/${j}`;
          const to = str(t.entity) ?? (typeof t.stereotype === "string" ? rows?.stereotypeIdOf?.(t.stereotype) : null);
          const field = str(t.entity) ? "entity" : "stereotype";
          if (to && !own.has(to) && to !== id)
            out.push({ fromId: str(e.id) ?? id, pointer: `${at}/${field}`, field, toId: to, required: false, removeItem: at });
        });
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
      push("/junctionTable", "junctionTable", doc.junctionTable, false);
      push("/foreignKey", "foreignKey", doc.foreignKey, false);
      arr(doc.ends).forEach((e, i) => {
        const at = `/ends/${i}`;
        push(`${at}/end`, "end", e.end, false, at);
        mappingEntryRefs(out, id, own, e, at);
      });
      arr(doc.constants).forEach((c, i) => {
        const to = str(c.column);
        if (to && !own.has(to) && to !== id)
          out.push({ fromId: id, pointer: `/constants/${i}/column`, field: "column", toId: to, required: true, dependentItem: `/constants/${i}` });
      });
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
        // A stereotype entry names its stereotype by key; the reference is to the stereotype's id, and a delete takes the entry with it.
        const stereotypeId = typeof e.stereotype === "string" ? rows?.stereotypeIdOf?.(e.stereotype) : null;
        if (stereotypeId) push(`/subjects/${i}/stereotype`, "stereotype", stereotypeId, false, `/subjects/${i}`);
        push(`/subjects/${i}/boundAttribute`, "boundAttribute", e.boundAttribute, false);
        // The owner field is optional: a delete clears the key (decision 10).
        push(`/subjects/${i}/owner`, "owner", e.owner, false);
      });
      // An action's sets (object-effects-design.md section 8): the attribute and every id in the value mean something to the set,
      // so a delete drops the whole set and the action keeps its other sets.
      arr(doc.actions).forEach((a, i) =>
        arr(a.sets).forEach((set, j) => {
          const at = `/actions/${i}/sets/${j}`;
          const holder = str(set.id) ?? str(a.id) ?? id;
          const drop = (pointer: string, field: string, to: unknown) => {
            const t = str(to);
            if (t) out.push({ fromId: holder, pointer, field, toId: t, required: false, removeItem: at, drops: true });
          };
          drop(`${at}/attribute`, "attribute", set.attribute);
          treeIds(set.value, `${at}/value`, drop);
        }),
      );
      arr(doc.events).forEach((e, i) =>
        arr(e.actors).forEach((_, j) => push(`/events/${i}/actors/${j}`, "actors", (e.actors as unknown[])[j], false, `/events/${i}/actors/${j}`)),
      );
      // The lifecycle's rights: a use of a named right, and what an inline right names (rights-design.md section 8).
      arr(doc.rights).forEach((r, i) => {
        const at = `/rights/${i}`;
        const to = str(r.right);
        if (to) out.push({ fromId: str(r.id) ?? id, pointer: `${at}/right`, field: "right", toId: to, required: false, removeItem: at, drops: true });
        else rightRefs(out, r, at, str(r.id) ?? id, true, own);
      });
      // The lifecycle's trigger points (trigger-points-design.md section 9): what they name, and the ids in their expressions and sets.
      arr(doc.triggerPoints).forEach((t, i) => triggerRefs(out, t, `/triggerPoints/${i}`, str(t.id) ?? id, own));
      const bound = rows && doc.use === "lifecycle" ? subjectsOf(doc).filter((e) => e.boundAttribute) : [];
      const states = (list: unknown, base: string) =>
        arr(list).forEach((st, i) => {
          // A code under a reference binding is a use of the row it names (decision 18): one reference per binding it fits.
          const seen = new Set<string>();
          for (const e of bound) {
            const row = e.entity ? rows!.rowOf(e.entity, e.boundAttribute!, st.code) : null;
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
        // An approval's named signatures (signoff-design.md section 9): signers follow what a gate's signers do, required actors leave
        // their list, and the ids a `when` reads are required (a delete is refused, or takes the signature, and with its last one the gate).
        arr(gate.stages).forEach((stage, s) =>
          arr(stage.signatures).forEach((sig, j) => {
            const at = `/transitions/${i}/gate/stages/${s}/signatures/${j}`;
            const holder = str(sig.id) ?? id;
            const from = (pointer: string, field: string, to: unknown, required: boolean, removeItem?: string) => {
              const toId = str(to);
              if (toId && !own.has(toId) && toId !== id) out.push({ fromId: holder, pointer, field, toId, required, removeItem });
            };
            arr(sig.signers).forEach((a, k) => from(`${at}/signers/${k}`, "signers", a, true));
            arr(sig.requiredActors).forEach((a, k) => from(`${at}/requiredActors/${k}`, "requiredActors", a, false, `${at}/requiredActors/${k}`));
            treeIds(sig.when, `${at}/when`, (pointer, field, to) => from(pointer, field, to, true));
            // Part 2: the actors a signature may be delegated to, and those an escalation tells. Both leave their list as an actor goes;
            // a list left empty drops its key (and an escalateTo takes escalateAfter with it).
            arr(sig.delegateTo).forEach((a, k) => from(`${at}/delegateTo/${k}`, "delegateTo", a, false, `${at}/delegateTo/${k}`));
            arr(sig.escalateTo).forEach((a, k) => from(`${at}/escalateTo/${k}`, "escalateTo", a, false, `${at}/escalateTo/${k}`));
          }),
        );
      });
      break;
    }
    case "scenario":
      push("/process", "process", doc.process, true);
      // Objects (lifecycle-design.md decision 22): the entity an object is an instance of, the attributes its values name and
      // the relation ends its links navigate. The subject and a link's object name objects of the same file (no reference leaves it).
      // Participants (actors-design.md decision 14) are parts: the actor one stands for (cleared when it goes) and each holding's actor
      // (the holding goes with it). Their objects and `within` name objects of the same file, as a link's object does.
      arr(doc.participants).forEach((p, i) => {
        const holder = str(p.id) ?? id;
        const actor = str(p.actor);
        if (actor) out.push({ fromId: holder, pointer: `/participants/${i}/actor`, field: "actor", toId: actor, required: false });
        arr(p.holds).forEach((h, j) => {
          const held = str(h.actor);
          if (held)
            out.push({
              fromId: str(h.id) ?? holder,
              pointer: `/participants/${i}/holds/${j}/actor`,
              field: "actor",
              toId: held,
              required: false,
              removeItem: `/participants/${i}/holds/${j}`,
            });
        });
      });
      // The named signature a step answers is optional (a delete clears the key, and the engine picks again); the keys of its expected
      // signature counts are signature ids (a delete drops the entry, and an empty map goes).
      arr(doc.steps).forEach((st, i) => {
        push(`/steps/${i}/signature`, "signature", st.signature, false);
        const signatures = (st.expect as Json | undefined)?.signatures;
        if (signatures && typeof signatures === "object")
          for (const key of Object.keys(signatures as Json))
            out.push({ fromId: str(st.id) ?? id, pointer: `/steps/${i}/expect/signatures/${key}`, field: "signatures", toId: key, required: false });
      });
      // A step's delegateTo and onBehalfOf name participants of the same file (part 2): a delete of the participant clears the key. The
      // reminders and escalations a step expects are listed by signature id: a delete drops the item.
      arr(doc.steps).forEach((st, i) => {
        for (const key of ["delegateTo", "onBehalfOf"] as const) {
          const to = str(st[key]);
          if (to) out.push({ fromId: str(st.id) ?? id, pointer: `/steps/${i}/${key}`, field: key, toId: to, required: false });
        }
        arr((st.expect as Json | undefined)?.notices).forEach((n, j) => {
          const to = str(n.signature);
          const pointer = `/steps/${i}/expect/notices/${j}`;
          if (to) out.push({ fromId: str(st.id) ?? id, pointer: `${pointer}/signature`, field: "notices", toId: to, required: false, removeItem: pointer });
        });
      });
      // A step's assume words are keyed by the id of a check (trigger-points-design.md decision 26): a delete clears the entry. Boolean
      // entries are guards' and stay as they are.
      arr(doc.steps).forEach((st, i) => {
        const assume = st.assume;
        if (assume && typeof assume === "object")
          for (const [key, value] of Object.entries(assume as Json))
            if (typeof value === "string")
              out.push({ fromId: str(st.id) ?? id, pointer: `/steps/${i}/assume/${key}`, field: "assume", toId: key, required: false });
      });
      // The checks a step expects to have warned, and the trigger points it expects to have run, are listed by id: a delete clears the item.
      arr(doc.steps).forEach((st, i) => {
        const expect = st.expect as Json | undefined;
        for (const list of ["checkWarnings", "triggerPoints"] as const)
          strings(expect?.[list]).forEach((to, j) => {
            const pointer = `/steps/${i}/expect/${list}/${j}`;
            out.push({ fromId: str(st.id) ?? id, pointer, field: list, toId: to, required: false, removeItem: pointer });
          });
      });
      // A step's expected values are keyed by attribute id: a delete clears the key.
      arr(doc.steps).forEach((st, i) => {
        const values = (st.expect as Json | undefined)?.values;
        if (values && typeof values === "object")
          for (const key of Object.keys(values as Json))
            out.push({ fromId: str(st.id) ?? id, pointer: `/steps/${i}/expect/values/${key}`, field: "values", toId: key, required: false });
      });
      arr(doc.objects).forEach((o, i) => {
        const at = `/objects/${i}`;
        const holder = str(o.id) ?? id;
        // The states an object is in: a state or its lifecycle going takes the entry with it.
        arr(o.states as unknown).forEach((_, j) => {
          const to = str((o.states as unknown[])[j]);
          if (to) out.push({ fromId: holder, pointer: `${at}/states/${j}`, field: "states", toId: to, required: false, removeItem: `${at}/states/${j}` });
        });
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

/**
 * Where a right's or a trigger point's verb sits: whether it is the last of its list, the entry's pointer and whether the entry
 * is an entry of a lifecycle (a named right is a document of its own).
 */
function rightVerbAt(doc: Json, pointer: string): { last: boolean; right: string; whole: boolean } | null {
  const m = /^((?:\/rights|\/triggerPoints)\/\d+|)\/verbs\/\d+$/.exec(pointer);
  if (!m) return null;
  const list = m[1].startsWith("/triggerPoints") ? doc.triggerPoints : doc.rights;
  const holder = m[1] === "" ? doc : arr(list)[Number(m[1].split("/")[2])];
  return { last: arr(holder?.verbs).length === 1, right: m[1], whole: m[1] !== "" };
}

/**
 * What a trigger point names besides verbs and states: its entities, attributes and ends, the ids in its `when` and condition, and
 * its sets. An item leaves its list; the last one takes the trigger point with it, since an empty list would widen it to
 * everywhere. A deleted id in an expression takes the point too (remove-references refuses; the cascade takes it by `required`).
 * A set's attribute, or an id in its value, takes the set, and the point keeps its other sets.
 */
function triggerRefs(out: Ref[], point: Json, base: string, holder: string, own: ReadonlySet<string>): void {
  const add = (pointer: string, field: string, to: unknown, item: string | null, alone: boolean, required = false) => {
    const toId = str(to);
    if (!toId || own.has(toId) || toId === holder) return;
    if (required) out.push({ fromId: holder, pointer, field, toId, required: true });
    // A named trigger point (no base) cannot drop itself from a lifecycle: it is deleted, as the last subject does.
    else if (alone)
      out.push({ fromId: holder, pointer, field, toId, required: false, ...(base === "" ? { deletesHolder: true } : { removeItem: base, drops: true }) });
    else out.push({ fromId: holder, pointer, field, toId, required: false, ...(item ? { removeItem: item } : {}) });
  };
  for (const field of ["entities", "attributes", "ends"] as const) {
    const list = arr(point[field]) as unknown as string[];
    list.forEach((to, j) => add(`${base}/${field}/${j}`, field, to, `${base}/${field}/${j}`, list.length === 1));
  }
  for (const field of ["when", "condition"] as const) treeIds(point[field], `${base}/${field}`, (pointer, f, to) => add(pointer, f, to, null, true, true));
  arr(point.sets).forEach((set, j) => {
    const at = `${base}/sets/${j}`;
    const setHolder = str(set.id) ?? holder;
    const drop = (pointer: string, field: string, to: unknown) => {
      const t = str(to);
      if (t && !own.has(t)) out.push({ fromId: setHolder, pointer, field, toId: t, required: false, removeItem: at, drops: true });
    };
    drop(`${at}/attribute`, "attribute", set.attribute);
    treeIds(set.value, `${at}/value`, drop);
  });
}

/**
 * What a right names besides verbs and states: its actors, except, ends and the ids in its condition. `base` is the entry's
 * pointer ("" for a named right). An item leaves its list; the last actor or end takes the right with it, since an empty list
 * would widen it (`whole`: an entry of a lifecycle can drop itself). A deleted id in the condition takes the right too.
 */
function rightRefs(out: Ref[], right: Json, base: string, holder: string, whole: boolean, own: ReadonlySet<string>): void {
  const add = (pointer: string, field: string, to: unknown, item: string | null, alone: boolean, condition = false) => {
    const toId = str(to);
    if (!toId || own.has(toId) || toId === holder) return;
    // An id in a condition is never rewritten: remove-references refuses, and delete-dependents takes the right (the cascade does it by `required`).
    if (condition) out.push({ fromId: holder, pointer, field, toId, required: true });
    // The last actor or end takes the right with it: an inline right drops from its lifecycle, a named right is deleted, in either resolution.
    else if (alone)
      out.push({ fromId: holder, pointer, field, toId, required: false, ...(whole ? { removeItem: base, drops: true } : { deletesHolder: true }) });
    else out.push({ fromId: holder, pointer, field, toId, required: false, ...(item ? { removeItem: item } : {}) });
  };
  for (const field of ["actors", "except", "ends"] as const) {
    const list = arr(right[field]) as unknown as string[];
    list.forEach((to, j) => add(`${base}/${field}/${j}`, field, to, `${base}/${field}/${j}`, field !== "except" && list.length === 1));
  }
  treeIds(right.condition, `${base}/condition`, (pointer, field, to) => add(pointer, field, to, null, true, true));
}

/** The ids a value tree names: path ids, a ref, the states of `inState` and the path `of`. */
function treeIds(node: unknown, pointer: string, found: (pointer: string, field: string, id: string) => void): void {
  if (Array.isArray(node)) return node.forEach((x, i) => treeIds(x, `${pointer}/${i}`, found));
  if (!node || typeof node !== "object") return;
  const t = node as Json;
  for (const key of ["path", "inState", "of"])
    arr(t[key] as unknown).forEach((_, i) => found(`${pointer}/${key}/${i}`, key, (t[key] as unknown[])[i] as string));
  if (typeof t.ref === "string") found(`${pointer}/ref`, "ref", t.ref);
  for (const key of ["args", "case", "when", "then", "else"]) if (t[key]) treeIds(t[key], `${pointer}/${key}`, found);
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
  // A table's foreign keys are sub-elements a relation mapping names (the entry rule of several-types-design.md 19.6).
  collect(doc.foreignKeys);
  if (doc.kind === "entity" || doc.kind === "reference-type") collect(doc.bindings);
  if (doc.kind === "trigger-point") collect(doc.sets);
  if (doc.kind === "enum") collect(doc.members);
  if (doc.kind === "verb-vocabulary") collect(doc.verbs);
  if (doc.kind === "process") {
    collect(doc.subjects);
    collect(doc.context);
    collect(doc.events);
    collect(doc.guards);
    collect(doc.actions);
    collect(doc.rights);
    collect(doc.triggerPoints);
    arr(doc.triggerPoints).forEach((t) => collect(t.sets));
    arr(doc.actions).forEach((a) => collect(a.sets));
    // The stages and named signatures of every approval, inline or in a library.
    const stagesOf = (gate: Json | undefined) =>
      arr(gate?.stages).forEach((stage) => {
        if (str(stage.id)) ids.push(stage.id as string);
        collect(stage.signatures);
      });
    arr(doc.gates).forEach(stagesOf);
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
      stagesOf(gate);
    });
  }
  if (doc.kind === "scenario") {
    collect(doc.steps);
    collect(doc.objects);
    collect(doc.participants);
    arr(doc.participants).forEach((p) => collect(p.holds));
  }
  // A seed's rows hold ids: a lifecycle's state code under a reference binding is a use of its row.
  if (doc.kind === "seed") collect(doc.rows);
  return ids;
}
