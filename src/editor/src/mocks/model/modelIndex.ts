// The mock model's derived state, kept up to date one change at a time so a 27,000-element model
// (?mock=large, explorer-redesign.md section 5 item 4) answers a save, a read or a validation without
// scanning every entry: the sub-element id → owner map, the reverse reference index, MQ3001's name
// scopes and each entry's own diagnostics. The result is the same as validateModel over every entry
// (tests/unit/mock-model-index.test.ts checks that after random edits).
import type { Diagnostic } from "@/api/types";
import { referencesOf, subElementIds } from "./refs";
import {
  entryDiagnostics,
  GLOBAL_KINDS,
  globalsOf,
  nameScope,
  type ModelEntry,
  type ModelGlobals,
  type ValidationContext,
  type ValidationInput,
} from "./validate";

const byPath = (a: ModelEntry, b: ModelEntry) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0);

export class ModelIndex {
  /** The entries this index describes, by id (the same objects the model holds). */
  private snapshot = new Map<string, ModelEntry>();
  /** How many entries hold each id (own id and sub-element ids); MQ2001 needs only "any". */
  private readonly idCount = new Map<string, number>();
  /** Sub-element id → owning entry id. */
  private readonly subOwner = new Map<string, string>();
  private readonly idsOf = new Map<string, string[]>();
  private readonly targetsOf = new Map<string, string[]>();
  /** Referenced id → the entries that reference it. */
  private readonly referrers = new Map<string, Set<string>>();
  private readonly scopeOf = new Map<string, string>();
  /** MQ3001 scope → its entries in path order. */
  private readonly scopes = new Map<string, ModelEntry[]>();
  private readonly raw = new Map<string, Diagnostic[]>();
  private globals: ModelGlobals = globalsOf([]);
  private sorted: ModelEntry[] | null = null;
  /** Time spent in the last sync, for window.__mqPerf.mock. */
  lastSyncMs = 0;

  constructor(private readonly extensions: ValidationInput["extensions"]) {}

  /** Brings the index to `entries`, re-checking only the entries a difference can affect. Returns the ids re-checked. */
  sync(entries: ReadonlyMap<string, ModelEntry>): Set<string> {
    const started = performance.now();
    const changed: ModelEntry[] = [];
    const removed: ModelEntry[] = [];
    for (const [id, entry] of entries) if (this.snapshot.get(id) !== entry) changed.push(entry);
    for (const [id, entry] of this.snapshot) if (!entries.has(id)) removed.push(entry);
    if (changed.length === 0 && removed.length === 0) return new Set();

    const all = [...changed, ...removed];
    // A package's parent decides the vocabulary chain of every element under it (MQ2006, MQ2008) and the enclosing scopes of
    // its vocabularies (MQ3021), none of which references the package: a move revalidates everything while a domain
    // vocabulary exists. Any package change refreshes the chain map (names appear in messages).
    const isPackage = (e: ModelEntry | undefined) => e?.json.kind === "package";
    const packageChanged = all.some((e) => isPackage(e) || isPackage(this.snapshot.get(e.id)));
    const packageMoved = all.some((e) => {
      const before = this.snapshot.get(e.id);
      const after = entries.get(e.id);
      return (isPackage(before) || isPackage(after)) && (before?.json.parent ?? null) !== (after?.json.parent ?? null);
    });
    const hasDomainVocabulary = () =>
      [...entries.values()].some((e) => (e.json.kind === "tag-vocabulary" || e.json.kind === "category-tree") && e.json.package != null);
    const global =
      all.some((e) => GLOBAL_KINDS.has(String(e.json.kind)) || GLOBAL_KINDS.has(String(this.snapshot.get(e.id)?.json.kind))) ||
      (packageMoved && hasDomainVocabulary());
    const affected = new Set<string>();
    const touch = (entry: ModelEntry | undefined) => {
      if (!entry) return;
      for (const id of this.idsOf.get(entry.id) ?? []) for (const r of this.referrers.get(id) ?? []) affected.add(r);
      const scope = this.scopeOf.get(entry.id);
      if (scope) for (const e of this.scopes.get(scope) ?? []) affected.add(e.id);
    };
    for (const entry of [...changed, ...removed]) {
      touch(this.snapshot.get(entry.id));
      this.remove(entry.id);
    }
    for (const entry of changed) {
      this.add(entry);
      affected.add(entry.id);
      touch(entry);
    }
    this.snapshot = new Map(entries);
    this.sorted = null;
    if (global || packageChanged) this.globals = globalsOf(this.snapshot.values());
    if (global) for (const id of this.snapshot.keys()) affected.add(id);
    const ctx = this.context();
    for (const id of affected) {
      const entry = this.snapshot.get(id);
      if (entry) this.raw.set(id, entryDiagnostics(entry, ctx));
    }
    this.lastSyncMs = performance.now() - started;
    return affected;
  }

  /** The owning entry id of an element or sub-element id. */
  ownerOf(id: string): string | undefined {
    return this.snapshot.has(id) ? id : this.subOwner.get(id);
  }

  /** The ids an entry holds: its own and its sub-elements'. */
  idsHeldBy(entryId: string): readonly string[] {
    return this.idsOf.get(entryId) ?? [];
  }

  /** The entries that reference any of `ids`. */
  referrersOf(ids: Iterable<string>): Set<string> {
    const out = new Set<string>();
    for (const id of ids) for (const r of this.referrers.get(id) ?? []) out.add(r);
    return out;
  }

  /** Every entry's diagnostics in path order, before validation.rules applies. */
  diagnostics(): Diagnostic[] {
    this.sorted ??= [...this.snapshot.values()].sort(byPath);
    const out: Diagnostic[] = [];
    for (const entry of this.sorted) for (const d of this.raw.get(entry.id) ?? []) out.push(d);
    return out;
  }

  /** The diagnostics of some entries, in path order, before validation.rules applies. */
  diagnosticsFor(ids: Iterable<string>): Diagnostic[] {
    const entries = [...ids].flatMap((id) => this.snapshot.get(id) ?? []).sort(byPath);
    return entries.flatMap((e) => this.raw.get(e.id) ?? []);
  }

  private context(): ValidationContext {
    return {
      ...this.globals,
      extensions: this.extensions,
      hasId: (id) => (this.idCount.get(id) ?? 0) > 0,
      firstInScope: (scope, entry) => {
        const first = this.scopes.get(scope)?.[0];
        return first && first.id !== entry.id ? first : undefined;
      },
    };
  }

  private add(entry: ModelEntry): void {
    const ids = [entry.id, ...subElementIds(entry.json)];
    this.idsOf.set(entry.id, ids);
    for (const id of ids) this.idCount.set(id, (this.idCount.get(id) ?? 0) + 1);
    for (const id of ids.slice(1)) this.subOwner.set(id, entry.id);
    const targets = [...new Set(referencesOf(entry.json).map((r) => r.toId))];
    this.targetsOf.set(entry.id, targets);
    for (const t of targets) {
      let set = this.referrers.get(t);
      if (!set) this.referrers.set(t, (set = new Set()));
      set.add(entry.id);
    }
    const scope = nameScope(entry.json);
    if (scope !== null) {
      this.scopeOf.set(entry.id, scope);
      const list = this.scopes.get(scope) ?? [];
      list.push(entry);
      list.sort(byPath);
      this.scopes.set(scope, list);
    }
  }

  private remove(id: string): void {
    const old = this.snapshot.get(id);
    if (!old) return;
    for (const held of this.idsOf.get(id) ?? []) {
      const n = (this.idCount.get(held) ?? 1) - 1;
      if (n > 0) this.idCount.set(held, n);
      else this.idCount.delete(held);
      if (this.subOwner.get(held) === id) this.subOwner.delete(held);
    }
    this.idsOf.delete(id);
    for (const t of this.targetsOf.get(id) ?? []) {
      const set = this.referrers.get(t);
      set?.delete(id);
      if (set?.size === 0) this.referrers.delete(t);
    }
    this.targetsOf.delete(id);
    const scope = this.scopeOf.get(id);
    if (scope !== undefined) {
      const list = (this.scopes.get(scope) ?? []).filter((e) => e.id !== id);
      if (list.length) this.scopes.set(scope, list);
      else this.scopes.delete(scope);
      this.scopeOf.delete(id);
    }
    this.raw.delete(id);
  }
}
