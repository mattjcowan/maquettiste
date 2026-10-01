// The mock's delete plan and cascade (engine-design.md 15.1), close enough to the engine's ChangePlanner for the
// editor: `remove-references` clears optional references and refuses required ones; `delete-dependents` also
// removes the sub-element that holds a required reference (an attribute whose type is deleted, a table index) or,
// when the reference is the element's own (a table's database, a mapping's entity) or a relation end, deletes the
// referrer with its own dependents, recursively. The engine decides by validating each step against the schema;
// the mock follows the `required` flags of refs.ts instead.
import type { DeletePlan, DeleteResolution, ElementKind } from "@/api/types";
import { referencesOf, subElementIds, type Ref } from "./refs";

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
};

const str = (v: unknown): string | null => (typeof v === "string" && v ? v : null);

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
  if (Array.isArray(parent) && item) parent.splice(Number(last), 1);
  else if (parent && typeof parent === "object") delete (parent as Json)[last];
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
 * Plans deleting `ids`. Elements in `skip` (the other elements a batch names) are left to their own operation, as the
 * engine leaves them.
 */
export function planDelete(docs: ReadonlyMap<string, CascadeDoc>, ids: string[], resolution: DeleteResolution, skip: ReadonlySet<string> = new Set()): Cascade {
  const refsTo = new Map<string, { doc: CascadeDoc; ref: Ref }[]>();
  const owners = new Map<string, CascadeDoc>();
  for (const doc of docs.values()) {
    for (const sub of subElementIds(doc.json)) owners.set(sub, doc);
    for (const ref of referencesOf(doc.json)) {
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
      if (!ref.required) {
        mine.push({ pointer: ref.removeItem ?? ref.pointer, item: ref.removeItem !== undefined, remove: false, ref });
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
          because: `needs ${describe(op.ref.toId)}`,
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
        plan.clears.push({ ...base, pointer: op.ref.pointer, field: op.ref.field, target: op.ref.toId, because: `pointed to ${describe(op.ref.toId)}` });
      }
    }
  }

  if (plan.refused.some((r) => r.rule === "not-found")) plan.outcome = "not-found";
  else if (plan.refused.some((r) => r.rule === "referenced")) plan.outcome = "referenced";
  else if (plan.refused.length) plan.outcome = "invalid";
  return { plan, deleted, edited };
}
