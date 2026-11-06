// What a process's subject entries cover (stereotype-sets-design.md decisions 3 and 4), for the mock: a stereotype entry
// covers each entity that carries the stereotype (package defaults, implied stereotypes, its own list, less what it
// excludes) and every entity derived from one; an entry naming the entity or a base wins, nearest first, then stereotype
// entries in entry order. Does no work when no entry names a stereotype.
import { stereotypeAppliesToEntities as appliesToEntities, subjectsOf, type SubjectDoc } from "@/model/process";
import type { ModelEntry } from "./validate";

type Json = Record<string, unknown>;

const keys = (v: unknown): string[] => (Array.isArray(v) ? v.filter((k): k is string => typeof k === "string") : []);

export { stereotypeAppliesToEntities as appliesToEntities } from "@/model/process";

export class Coverage {
  private readonly docs = new Map<string, Json>();
  private readonly byKey = new Map<string, Json>();
  private readonly entities: Json[] = [];
  private readonly carriersMemo = new Map<string, string[]>();
  private readonly coveredMemo = new Map<string, Set<string>>();
  /** Whether some process has an entry that names a stereotype. */
  readonly any: boolean;

  constructor(entries: Iterable<ModelEntry>) {
    let any = false;
    for (const e of entries) {
      this.docs.set(e.id, e.json);
      if (e.json.kind === "stereotype" && typeof e.json.key === "string") this.byKey.set(e.json.key, e.json);
      else if (e.json.kind === "entity") this.entities.push(e.json);
      else if (!any && e.json.kind === "process" && subjectsOf(e.json).some((s) => s.stereotype !== undefined)) any = true;
    }
    this.any = any;
  }

  /** An entity's effective stereotype keys: package defaults (outermost first), then its own, each with what it implies. */
  effective(entity: Json): Set<string> {
    const withImplied = (key: string) => [key, ...keys(this.byKey.get(key)?.implies)];
    const chain: Json[] = [];
    for (let id = entity.package, hops = 0; typeof id === "string" && hops < 32; hops++) {
      const p = this.docs.get(id);
      if (!p || chain.includes(p)) break;
      chain.unshift(p);
      id = p.parent;
    }
    const excluded = new Set(keys(entity.excludeStereotypes).flatMap(withImplied));
    const out = new Set<string>();
    for (const p of chain)
      for (const k of keys(p.defaultStereotypes)) {
        const st = this.byKey.get(k);
        if (st && !appliesToEntities(st)) continue;
        for (const x of withImplied(k)) if (!excluded.has(x)) out.add(x);
      }
    for (const k of keys(entity.stereotypes)) for (const x of withImplied(k)) out.add(x);
    return out;
  }

  private chain(entity: Json): string[] {
    const ids: string[] = [];
    for (let at: Json | undefined = entity; at && !ids.includes(String(at.id)); at = typeof at.base === "string" ? this.docs.get(at.base) : undefined)
      ids.push(String(at.id));
    return ids;
  }

  /** The entities that carry the stereotype themselves, by name then id. */
  carriers(key: string): string[] {
    let list = this.carriersMemo.get(key);
    if (!list) {
      list = this.entities
        .filter((e) => this.effective(e).has(key))
        .sort((a, b) => String(a.name).localeCompare(String(b.name)) || String(a.id).localeCompare(String(b.id)))
        .map((e) => String(e.id));
      this.carriersMemo.set(key, list);
    }
    return list;
  }

  /** Whether the stereotype covers the entity: it or a base carries it. */
  covers(key: string, entityId: string): boolean {
    let set = this.coveredMemo.get(key);
    if (!set) {
      const carriers = new Set(this.carriers(key));
      set = new Set(this.entities.filter((e) => this.chain(e).some((id) => carriers.has(id))).map((e) => String(e.id)));
      this.coveredMemo.set(key, set);
    }
    return set.has(entityId);
  }

  /** The index (in `subjectsOf`) of the entry that governs an entity: the nearest entity entry, else the first covering stereotype entry; -1 for none. */
  entryIndex(process: Json, entityId: string): number {
    const entity = this.docs.get(entityId);
    if (!entity) return -1;
    const chain = this.chain(entity);
    const entries = subjectsOf(process);
    let best = -1;
    let bestAt = Infinity;
    entries.forEach((e, i) => {
      const at = e.entity ? chain.indexOf(e.entity) : -1;
      if (at >= 0 && at < bestAt) [best, bestAt] = [i, at];
    });
    if (best >= 0) return best;
    return entries.findIndex((e) => e.stereotype !== undefined && this.covers(e.stereotype, entityId));
  }

  /** The entry of a process that governs an entity. */
  entryOf(process: Json, entityId: string): SubjectDoc | undefined {
    return subjectsOf(process)[this.entryIndex(process, entityId)];
  }

  /** The carriers the stereotype entry at `index` governs: those no other entry of the process wins. */
  governed(process: Json, index: number): string[] {
    const entry = subjectsOf(process)[index];
    if (entry?.stereotype === undefined) return [];
    return this.carriers(entry.stereotype).filter((id) => this.entryIndex(process, id) === index);
  }

  /** Whether the process governs the entity: an entry covers it, directly, by a base or by a stereotype. */
  governs(process: Json, entityId: string): boolean {
    return this.entryIndex(process, entityId) >= 0;
  }
}
