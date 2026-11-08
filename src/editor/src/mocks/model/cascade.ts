// The mock's delete plan and cascade (engine-design.md 15.1), close enough to the engine's ChangePlanner for the
// editor: `remove-references` clears optional references and refuses required ones; `delete-dependents` also
// removes the sub-element that holds a required reference (an attribute whose type is deleted, a table index) or,
// when the reference is the element's own (a table's database, a mapping's entity) or a relation end, deletes the
// referrer with its own dependents, recursively. The engine decides by validating each step against the schema;
// the mock follows the `required` flags of refs.ts instead.
import type { DeletePlan, DeleteResolution, ElementKind } from "@/api/types";
import { referencesOf, rowResolverOf, subElementIds, type Ref } from "./refs";

type Json = Record<string, unknown>;

export interface CascadeDoc {
  id: string;
  path: string;
  json: Json;
}

interface Op {
  pointer: string;
  /** The pointer names an array item (spliced) rather than a property (deleted). */
  item: boolean;
  /** A part removed rather than a reference cleared. */
  remove: boolean;
  ref: Ref;
}

export interface Cascade {
  plan: DeletePlan;
  /** Every element the delete removes, the named ones first. */
  deleted: string[];
  /** The documents of surviving elements after their edits. */
  edited: Map<string, Json>;
}

const NOUNS: Record<string, string> = {
  attributes: "attribute",
  ends: "end",
  members: "member",
  columns: "column",
  indexes: "index",
  checks: "check",
  schemas: "schema",
  categories: "category",
  alternateKeys: "key",
  objects: "object",
  links: "link",
  participants: "participant",
  holds: "holding",
  sets: "set",
  states: "state",
  rights: "right",
  triggerPoints: "trigger point",
};

const str = (v: unknown): string | null => (typeof v === "string" && v ? v : null);
const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);

function comparePointers(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true });
}

/** The pointer of the object whose id is `id` inside `json`, if any. */
function pointerOf(json: unknown, id: string, base = ""): string | null {
  if (Array.isArray(json)) {
    for (let i = 0; i < json.length; i++) {
      const found = pointerOf(json[i], id, `${base}/${i}`);
      if (found !== null) return found;
    }
    return null;
  }
  if (!json || typeof json !== "object") return null;
  const obj = json as Json;
  if (base !== "" && obj.id === id) return base;
  for (const [key, value] of Object.entries(obj)) {
    if (value && typeof value === "object") {
      const found = pointerOf(value, id, `${base}/${key}`);
      if (found !== null) return found;
    }
  }
  return null;
}

function at(json: unknown, pointer: string): unknown {
  let node = json;
  for (const part of pointer.split("/").slice(1)) node = node && typeof node === "object" ? (node as Json)[part] : undefined;
  return node;
}

function removeAt(json: Json, pointer: string, item: boolean): void {
  const parts = pointer.split("/").slice(1);
  const last = parts.pop()!;
  let parent: unknown = json;
  for (const p of parts) parent = (parent as Json)[p];
  if (Array.isArray(parent) && item) {
    parent.splice(Number(last), 1);
    // An actor's memberOf is left out when empty, as the engine writes it.
    if (parts.length === 1 && parts[0] === "memberOf" && parent.length === 0) delete json.memberOf;
    // A lifecycle left with no rights is written without the key, and an `except` left empty means no exception.
    if (parts.length === 1 && parts[0] === "rights" && parent.length === 0) delete json.rights;
    // So is a lifecycle left with no trigger points.
    if (parts.length === 1 && parts[0] === "triggerPoints" && parent.length === 0) delete json.triggerPoints;
    // A named trigger point left with no exception or no sets is written without the key.
    if (parts.length === 1 && json.kind === "trigger-point" && parent.length === 0 && ["exceptEntities", "sets"].includes(parts[0])) delete json[parts[0]];
    if (parent.length === 0 && parts.length >= 1 && parts[parts.length - 1] === "except") {
      let holder: unknown = json;
      for (const p of parts.slice(0, -1)) holder = (holder as Json)[p];
      if (holder && typeof holder === "object") delete (holder as Json).except;
    }
    // An action left with no sets, or an object with no states, is written without the key.
    if (parent.length === 0 && parts.length >= 1) {
      const key = parts[parts.length - 1];
      if (key === "sets" || key === "states") {
        let holder: unknown = json;
        for (const p of parts.slice(0, -1)) holder = (holder as Json)[p];
        if (holder && typeof holder === "object" && parts.length > 1) delete (holder as Json)[key];
      }
    }
  } else if (parent && typeof parent === "object") delete (parent as Json)[last];
}

/** Ids held under `pointer` in `json` (the removed sub-element and what it nests). */
function idsUnder(json: Json, pointer: string): string[] {
  const out: string[] = [];
  const walk = (node: unknown) => {
    if (Array.isArray(node)) node.forEach(walk);
    else if (node && typeof node === "object") {
      if (str((node as Json).id)) out.push((node as Json).id as string);
      Object.values(node as Json).forEach(walk);
    }
  };
  walk(at(json, pointer));
  return out;
}

/**
 * Scenario objects dropped for a deleted entity (decision 22): the links to them go too, and a subject left pointing at
 * nothing refuses the delete (the scenario cannot run without it).
 */
function dropObjects(docs: ReadonlyMap<string, CascadeDoc>, ops: Map<string, Op[]>, plan: DeletePlan): void {
  for (const [id, list] of ops) {
    const doc = docs.get(id);
    if (doc?.json.kind !== "scenario") continue;
    const dropped = new Map<string, number>();
    for (const o of list) {
      const m = /^\/objects\/(\d+)$/.exec(o.pointer);
      if (o.remove && m) dropped.set(String((at(doc.json, o.pointer) as Json).id), Number(m[1]));
    }
    if (!dropped.size) continue;
    const subject = str(doc.json.subject);
    if (subject && dropped.has(subject))
      plan.refused.push({
        id,
        kind: "scenario",
        name: str(doc.json.name),
        pointer: "/subject",
        why: "its subject object would be dropped with the entity it is an instance of, and the subject is required.",
        rule: "MQ2001",
      });
    const gone = new Set(dropped.values());
    arr(doc.json.objects).forEach((o, i) => {
      if (gone.has(i)) return;
      arr(o.links).forEach((l, j) => {
        const to = str(l.object);
        if (!to || !dropped.has(to)) return;
        const pointer = `/objects/${i}/links/${j}`;
        const ref: Ref = { fromId: String(o.id), pointer: `${pointer}/object`, field: "object", toId: to, required: false };
        if (!list.some((x) => x.pointer === pointer)) list.push({ pointer, item: true, remove: true, ref });
      });
    });
  }
}

/**
 * Plans deleting `ids`. Elements in `skip` (the other elements a batch names) are left to their own operation, as the
 * engine leaves them.
 */
export function planDelete(docs: ReadonlyMap<string, CascadeDoc>, ids: string[], resolution: DeleteResolution, skip: ReadonlySet<string> = new Set()): Cascade {
  const refsTo = new Map<string, { doc: CascadeDoc; ref: Ref }[]>();
  const owners = new Map<string, CascadeDoc>();
  const rows = rowResolverOf(() => [...docs.values()].map((d) => d.json));
  for (const doc of docs.values()) {
    for (const sub of subElementIds(doc.json)) owners.set(sub, doc);
    for (const ref of referencesOf(doc.json, rows)) {
      const list = refsTo.get(ref.toId) ?? [];
      list.push({ doc, ref });
      refsTo.set(ref.toId, list);
    }
  }
  const nameOf = (doc: CascadeDoc): string => {
    const json = doc.json;
    const entity = str(json.entity) ? docs.get(json.entity as string) : undefined;
    return str(json.name) ?? str(json.displayName) ?? (json.kind === "table" && entity ? `${String(entity.json.name)} table` : doc.id);
  };
  const describe = (id: string): string => {
    const doc = docs.get(id);
    if (doc) return `${String(doc.json.kind)} ${nameOf(doc)}`;
    const owner = owners.get(id);
    if (!owner) return id;
    const pointer = pointerOf(owner.json, id) ?? "";
    const parts = pointer.split("/");
    const noun = NOUNS[parts[parts.length - 2] ?? ""] ?? "part";
    const sub = at(owner.json, pointer) as Json | undefined;
    return `${noun} ${str(sub?.name) ?? str(sub?.role) ?? str(sub?.code) ?? id} of ${String(owner.json.kind)} ${nameOf(owner)}`;
  };
  const shortName = (id: string): string => {
    const doc = docs.get(id);
    if (doc) return nameOf(doc);
    const owner = owners.get(id);
    const sub = owner ? (at(owner.json, pointerOf(owner.json, id) ?? "") as Json | undefined) : undefined;
    return str(sub?.name) ?? str(sub?.role) ?? id;
  };

  const plan: DeletePlan = {
    ids: [...new Set(ids)],
    resolution,
    outcome: "saved",
    deletes: [],
    clears: [],
    removes: [],
    refused: [],
    settings: [],
    warnings: [],
  };
  const known = plan.ids.filter((id) => docs.has(id));
  for (const id of plan.ids)
    if (!docs.has(id)) plan.refused.push({ id, kind: null, name: null, pointer: null, why: "no element has this id.", rule: "not-found" });

  const deleted: string[] = [];
  const deletedSet = new Set<string>();
  const ops = new Map<string, Op[]>();
  const queue: string[] = [];
  const markDeleted = (id: string, because: string | null) => {
    const doc = docs.get(id);
    if (!doc || deletedSet.has(id)) return;
    deletedSet.add(id);
    deleted.push(id);
    ops.delete(id);
    if (because !== null) plan.deletes.push({ id, kind: doc.json.kind as ElementKind, name: nameOf(doc), path: doc.path, because });
    queue.push(id, ...subElementIds(doc.json));
  };
  for (const id of known) markDeleted(id, null);

  if (resolution === "refuse") {
    for (const target of [...queue]) {
      for (const { doc, ref } of refsTo.get(target) ?? []) {
        if (deletedSet.has(doc.id)) continue;
        // Deleting a stereotype removes the process entries that name it, under either choice (stereotype-sets-design.md decision 11).
        if (doc.json.kind === "process" && ref.field === "stereotype" && ref.removeItem !== undefined) {
          const mine = ops.get(doc.id) ?? [];
          if (!mine.some((o) => o.pointer === ref.removeItem)) mine.push({ pointer: ref.removeItem, item: true, remove: false, ref });
          ops.set(doc.id, mine);
          continue;
        }
        plan.refused.push({
          id: doc.id,
          kind: String(doc.json.kind),
          name: nameOf(doc),
          pointer: ref.pointer,
          why: `still references ${describe(ref.toId)}.`,
          rule: "referenced",
        });
      }
    }
    queue.length = 0;
  }

  while (queue.length) {
    const target = queue.shift()!;
    for (const { doc, ref } of refsTo.get(target) ?? []) {
      if (deletedSet.has(doc.id) || skip.has(doc.id)) continue;
      const mine = ops.get(doc.id) ?? [];
      if (mine.some((o) => o.remove && (ref.pointer === o.pointer || ref.pointer.startsWith(`${o.pointer}/`)))) continue;
      if (mine.some((o) => !o.remove && o.ref.pointer === ref.pointer)) continue;
      if (ref.deletesHolder) {
        markDeleted(
          doc.id,
          doc.json.kind === "trigger-point"
            ? `covers no other type than ${describe(ref.toId)}, and a trigger point with none would run everywhere`
            : `names no other actor or end than ${describe(ref.toId)}`,
        );
        continue;
      }
      if (!ref.required || ref.drops) {
        mine.push({ pointer: ref.removeItem ?? ref.pointer, item: ref.removeItem !== undefined, remove: !!ref.drops, ref });
        ops.set(doc.id, mine);
        continue;
      }
      if (resolution !== "delete-dependents") {
        plan.refused.push({
          id: doc.id,
          kind: String(doc.json.kind),
          name: nameOf(doc),
          pointer: ref.pointer,
          why: `its reference to ${describe(ref.toId)} at ${ref.pointer}: the reference is required, so it cannot be cleared.`,
          rule: "MQ2001",
        });
        continue;
      }
      const holder = ref.fromId !== doc.id ? pointerOf(doc.json, ref.fromId) : null;
      if (holder && !(doc.json.kind === "relation" && holder.startsWith("/ends/"))) {
        const kept = mine.filter((o) => o.pointer !== holder && !o.pointer.startsWith(`${holder}/`));
        kept.push({ pointer: holder, item: true, remove: true, ref });
        ops.set(doc.id, kept);
        queue.push(...idsUnder(doc.json, holder));
        continue;
      }
      markDeleted(doc.id, `needs ${describe(ref.toId)}`);
    }
  }

  dropObjects(docs, ops, plan);
  const edited = new Map<string, Json>();
  for (const [id, list] of [...ops].sort(([a], [b]) => a.localeCompare(b))) {
    const doc = docs.get(id)!;
    const json = structuredClone(doc.json);
    for (const op of [...list].sort((a, b) => comparePointers(b.pointer, a.pointer))) removeAt(json, op.pointer, op.item);
    edited.set(id, json);
    for (const op of [...list].sort((a, b) => comparePointers(a.pointer, b.pointer))) {
      const base = { id, kind: doc.json.kind as ElementKind, name: nameOf(doc) };
      if (op.remove) {
        const sub = at(doc.json, op.pointer) as Json | undefined;
        const parts = op.pointer.split("/");
        const noun = NOUNS[parts[parts.length - 2] ?? ""] ?? "part";
        const subId = str(sub?.id);
        plan.removes.push({
          ...base,
          pointer: op.pointer,
          what: `${noun} ${str(sub?.name) ?? str(sub?.role) ?? shortName(op.ref.toId)}`,
          ...(subId ? { subId } : {}),
          subKind: noun,
          because:
            noun === "right" || noun === "trigger point"
              ? `${describe(op.ref.toId)} was the only ${op.ref.field.replace(/s$/, "")} it names; with none it would apply to everything, so it goes too`
              : `needs ${describe(op.ref.toId)}`,
        });
      } else if (doc.json.kind === "diagram" && op.ref.field === "element") {
        plan.removes.push({
          ...base,
          pointer: op.pointer,
          what: `member ${shortName(op.ref.toId)}`,
          subKind: "member",
          because: `shows ${describe(op.ref.toId)}`,
        });
      } else {
        // A list of a right names the right, so the plan says which right changes.
        const inRight = /^\/rights\/(\d+)\/(actors|except|ends|states)\/\d+$/.exec(op.ref.pointer);
        const rightName = inRight ? str((arr(doc.json.rights)[Number(inRight[1])] as Json | undefined)?.name) : undefined;
        const inPoint = /^\/triggerPoints\/(\d+)\/(entities|attributes|ends|states|targets|transitions)\/\d+$/.exec(op.ref.pointer);
        const pointName = inPoint ? str((arr(doc.json.triggerPoints)[Number(inPoint[1])] as Json | undefined)?.name) : undefined;
        plan.clears.push({
          ...base,
          pointer: op.ref.pointer,
          field: rightName ? `right ${rightName}, ${inRight![2]}` : pointName ? `trigger point ${pointName}, ${inPoint![2]}` : op.ref.field,
          target: op.ref.toId,
          because: `pointed to ${describe(op.ref.toId)}`,
        });
      }
    }
  }

  if (plan.refused.some((r) => r.rule === "not-found")) plan.outcome = "not-found";
  else if (plan.refused.some((r) => r.rule === "referenced")) plan.outcome = "referenced";
  else if (plan.refused.length) plan.outcome = "invalid";
  return { plan, deleted, edited };
}
