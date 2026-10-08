// Tags and categories, global and per domain (explorer-redesign.md 1.11). Pure: from the index rows and the loaded
// vocabulary documents. A vocabulary with no `package` is global; one whose `package` is a domain applies to that
// domain and the domains nested under it, at most one of each kind per scope. An element's domain chain is its
// domain, each enclosing domain, then global; the pickers and the filter chips offer the vocabularies on that chain,
// nearest first, and an entry from a domain vocabulary names its domain ("core · Billing").
import type { CategoryTreeDoc, ElementSummary, TagVocabularyDoc } from "@/api/types";

export type VocabularyKind = "tag-vocabulary" | "category-tree";

type Row = Pick<ElementSummary, "id" | "kind" | "name" | "package">;

export interface ScopedVocabulary {
  id: string;
  kind: VocabularyKind;
  /** The domain that declares it; null for a global one. */
  scope: string | null;
  /** That domain's name; null for a global one. */
  domain: string | null;
}

export interface MarkOption {
  /** A tag key, or a category id. */
  value: string;
  /** The key or name, with " · <domain>" for an entry of a domain vocabulary. */
  label: string;
  domain: string | null;
  scope: string | null;
  description?: string;
  color?: string;
}

export interface CategoryOption extends MarkOption {
  parent: string | null;
}

/** " · <domain>" after an entry from a domain vocabulary. */
export const scopedLabel = (text: string, domain: string | null): string => (domain ? `${text} · ${domain}` : text);

/** The domain and its enclosing domains, nearest first; stops at a missing domain or a parent cycle. */
export function domainChain(domain: string | null | undefined, byId: ReadonlyMap<string, Row>): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  for (let d = domain ?? null; d && !seen.has(d);) {
    const row = byId.get(d);
    if (row?.kind !== "package") break;
    seen.add(d);
    out.push(d);
    d = row.package ?? null;
  }
  return out;
}

// Per index array (the rows are replaced, never mutated): the id map, the vocabulary rows by (kind, scope) sorted by id,
// and each chain asked for. An element editor mounts these hooks afresh on every selection in General mode, and the
// explorer's filter bar re-renders with it: without the cache each of them walked every index row (EX 4.5 walk).
interface VocabularyIndex {
  byId: Map<string, Row>;
  byScope: Map<string, Row[]>;
  chains: Map<string, ScopedVocabulary[]>;
}
const vocabularyIndexes = new WeakMap<readonly Row[], VocabularyIndex>();
const scopeKey = (kind: string, scope: string | null) => `${kind}\u0000${scope ?? ""}`;

function vocabularyIndex(rows: readonly Row[]): VocabularyIndex {
  let index = vocabularyIndexes.get(rows);
  if (!index) {
    const byId = new Map<string, Row>();
    const byScope = new Map<string, Row[]>();
    for (const r of rows) {
      byId.set(r.id, r);
      if (r.kind !== "tag-vocabulary" && r.kind !== "category-tree") continue;
      const key = scopeKey(r.kind, r.package ?? null);
      const list = byScope.get(key);
      if (list) list.push(r);
      else byScope.set(key, [r]);
    }
    for (const list of byScope.values()) list.sort((a, b) => a.id.localeCompare(b.id));
    index = { byId, byScope, chains: new Map() };
    vocabularyIndexes.set(rows, index);
  }
  return index;
}

const byIdOf = (rows: readonly Row[]): ReadonlyMap<string, Row> => vocabularyIndex(rows).byId;

/** The vocabulary a scope declares itself (null: the global one), if any. */
export function ownVocabulary(kind: VocabularyKind, scope: string | null, rows: readonly Row[]): Row | undefined {
  return vocabularyIndex(rows).byScope.get(scopeKey(kind, scope))?.[0];
}

/**
 * The vocabularies of a kind an element in `domain` sees: its domain's, each enclosing domain's, then the global ones.
 * The same rows, kind and domain give the same array (callers must not mutate it).
 */
export function vocabulariesOnChain(kind: VocabularyKind, domain: string | null | undefined, rows: readonly Row[]): ScopedVocabulary[] {
  const index = vocabularyIndex(rows);
  const key = scopeKey(kind, domain ?? null);
  const known = index.chains.get(key);
  if (known) return known;
  const out: ScopedVocabulary[] = [];
  for (const d of domainChain(domain, index.byId)) {
    const own = ownVocabulary(kind, d, rows);
    if (own) out.push({ id: own.id, kind, scope: d, domain: index.byId.get(d)?.name ?? d });
  }
  for (const r of index.byScope.get(scopeKey(kind, null)) ?? []) out.push({ id: r.id, kind, scope: null, domain: null });
  index.chains.set(key, out);
  return out;
}

/**
 * The vocabularies the explorer's filter offers: with a domain filter, that domain's chain; without one, the global
 * ones and then every domain's, by domain name (a chip still names its domain).
 */
export function filterVocabularies(kind: VocabularyKind, domain: string | null | undefined, rows: readonly Row[]): ScopedVocabulary[] {
  if (domain) return vocabulariesOnChain(kind, domain, rows);
  const byId = byIdOf(rows);
  const global = vocabulariesOnChain(kind, null, rows);
  const scoped = rows
    .filter((r) => r.kind === kind && r.package && byId.get(r.package)?.kind === "package")
    .map((r) => ({ id: r.id, kind, scope: r.package!, domain: byId.get(r.package!)?.name ?? r.package! }))
    .sort((a, b) => a.domain.localeCompare(b.domain) || a.id.localeCompare(b.id));
  return [...global, ...scoped];
}

type Docs = (id: string) => unknown;

/** Tag options along a chain, nearest first; a key declared twice keeps its nearest entry. Strict when any is. */
export function tagOptions(chain: readonly ScopedVocabulary[], docs: Docs): { options: MarkOption[]; strict: boolean } {
  const seen = new Set<string>();
  const options: MarkOption[] = [];
  let strict = false;
  for (const v of chain) {
    const doc = docs(v.id) as TagVocabularyDoc | undefined;
    if (!doc) continue;
    if (doc.strict === true) strict = true;
    for (const d of doc.definitions ?? []) {
      if (seen.has(d.key)) continue;
      seen.add(d.key);
      options.push({ value: d.key, label: scopedLabel(d.key, v.domain), domain: v.domain, scope: v.scope, description: d.description, color: d.color });
    }
  }
  return { options, strict };
}

/** Category options along a chain, nearest first, each tree in its own order. */
export function categoryOptions(chain: readonly ScopedVocabulary[], docs: Docs): CategoryOption[] {
  const seen = new Set<string>();
  const options: CategoryOption[] = [];
  for (const v of chain) {
    const doc = docs(v.id) as CategoryTreeDoc | undefined;
    for (const c of doc?.categories ?? []) {
      if (seen.has(c.id)) continue;
      seen.add(c.id);
      options.push({
        value: c.id,
        label: scopedLabel(c.name, v.domain),
        domain: v.domain,
        scope: v.scope,
        parent: c.parent ?? null,
        description: typeof c.description === "string" ? c.description : undefined,
      });
    }
  }
  return options;
}

/** The chip of a filter value: the option's label, else the bare value (a tag used but declared nowhere). */
export function chipLabel(value: string, options: readonly Pick<MarkOption, "value" | "label">[]): string {
  return options.find((o) => o.value === value)?.label ?? value;
}

/** Where the Problems panel's "Go to" leads for MQ2008 and MQ3021: the element, or the vocabulary's own tab. */
export type VocabularyProblemTarget =
  { type: "element"; id: string } | { type: "domain"; domain: string; tab: "tags" | "categories" } | { type: "settings"; tab: "tags" | "categories" };

export const VOCABULARY_RULES = new Set(["MQ2008", "MQ3021"]);

export function vocabularyProblemTarget(rule: string, elementId: string | null | undefined, rows: readonly Row[]): VocabularyProblemTarget | null {
  if (!VOCABULARY_RULES.has(rule) || !elementId) return null;
  const row = rows.find((r) => r.id === elementId);
  if (!row) return null;
  if (row.kind === "tag-vocabulary" || row.kind === "category-tree") {
    const tab = row.kind === "tag-vocabulary" ? "tags" : "categories";
    return row.package ? { type: "domain", domain: row.package, tab } : { type: "settings", tab };
  }
  return { type: "element", id: row.id };
}

/** The domain whose chain an element's marks resolve along: a domain's own, else the element's `package`. */
export function markDomainOf(json: { kind?: unknown; id?: unknown; package?: unknown } | undefined): string | null {
  if (!json) return null;
  if (json.kind === "package") return typeof json.id === "string" ? json.id : null;
  return typeof json.package === "string" ? json.package : null;
}

/** The nearest domain every one of several elements is in (their chains' first common domain): bulk tagging offers
 * only what each element may use. Null when they share none. */
export function commonDomain(domains: readonly (string | null | undefined)[], rows: readonly Row[]): string | null {
  if (!domains.length) return null;
  const byId = byIdOf(rows);
  const chains = domains.map((d) => domainChain(d, byId));
  return chains[0].find((d) => chains.every((c) => c.includes(d))) ?? null;
}

/** An element's marks that would leave scope on a move: declared only by domain vocabularies off the new chain. */
export interface MarksLeavingScope {
  name: string;
  tags: string[];
  category: string | null;
}

/**
 * The tags and categories of the moved elements that a domain vocabulary declares and the target domain's chain does
 * not offer (explorer-redesign.md 1.11: Move to domain… warns before they fall out of scope). A mark no vocabulary
 * declares, or one the target's chain offers, is not reported.
 */
export function marksLeavingScope(
  elements: readonly { name: string; tags?: readonly string[] | null; category?: string | null }[],
  target: string | null,
  rows: readonly Row[],
  docs: Docs,
): MarksLeavingScope[] {
  const scoped = (kind: VocabularyKind) => rows.filter((r) => r.kind === kind && r.package).map((r) => ({ id: r.id, kind, scope: r.package!, domain: null }));
  const scopedTags = new Set(tagOptions(scoped("tag-vocabulary"), docs).options.map((o) => o.value));
  const scopedCategories = new Set(categoryOptions(scoped("category-tree"), docs).map((o) => o.value));
  const offeredTags = new Set(tagOptions(vocabulariesOnChain("tag-vocabulary", target, rows), docs).options.map((o) => o.value));
  const offeredCategories = new Set(categoryOptions(vocabulariesOnChain("category-tree", target, rows), docs).map((o) => o.value));
  const out: MarksLeavingScope[] = [];
  for (const e of elements) {
    const tags = (e.tags ?? []).filter((t) => scopedTags.has(t) && !offeredTags.has(t));
    const category = e.category && scopedCategories.has(e.category) && !offeredCategories.has(e.category) ? e.category : null;
    if (tags.length || category) out.push({ name: e.name, tags, category });
  }
  return out;
}

export interface CategoryMaps {
  parents: Map<string, string | null>;
  names: Map<string, string>;
  list: { id: string; name: string; parent: string | null }[];
}

/**
 * The category nodes of every category tree, the global one and each domain's (§1.11), as the maps the explorer's
 * category filter and project-defined folders read: node → parent, node → name. A node id declared twice keeps its
 * first entry (the global tree comes first). Undefined when no tree has a category.
 */
export function mergeCategoryTrees(docs: readonly (CategoryTreeDoc | undefined)[]): CategoryMaps | undefined {
  const parents = new Map<string, string | null>();
  const names = new Map<string, string>();
  const list: CategoryMaps["list"] = [];
  for (const doc of docs)
    for (const c of doc?.categories ?? []) {
      if (names.has(c.id)) continue;
      const parent = (c as { parent?: string | null }).parent ?? null;
      parents.set(c.id, parent);
      names.set(c.id, c.name);
      list.push({ id: c.id, name: c.name, parent });
    }
  return list.length ? { parents, names, list } : undefined;
}

/** Whether a text is a tag key (common.json tagLabel): 1 to 64 characters, no whitespace and no control characters. */
export function isTagKey(text: string): boolean {
  const chars = [...text];
  return chars.length >= 1 && chars.length <= 64 && chars.every((c) => !/\s/.test(c) && c.charCodeAt(0) > 0x1f && c.charCodeAt(0) !== 0x7f);
}
