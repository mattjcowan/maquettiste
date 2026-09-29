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

const byIdOf = (rows: readonly Row[]) => new Map(rows.map((r) => [r.id, r] as const));

/** The vocabulary a scope declares itself (null: the global one), if any. */
export function ownVocabulary(kind: VocabularyKind, scope: string | null, rows: readonly Row[]): Row | undefined {
  return rows.filter((r) => r.kind === kind && (r.package ?? null) === scope).sort((a, b) => a.id.localeCompare(b.id))[0];
}

/** The vocabularies of a kind an element in `domain` sees: its domain's, each enclosing domain's, then the global ones. */
export function vocabulariesOnChain(kind: VocabularyKind, domain: string | null | undefined, rows: readonly Row[]): ScopedVocabulary[] {
  const byId = byIdOf(rows);
  const out: ScopedVocabulary[] = [];
  for (const d of domainChain(domain, byId)) {
    const own = ownVocabulary(kind, d, rows);
    if (own) out.push({ id: own.id, kind, scope: d, domain: byId.get(d)?.name ?? d });
  }
  for (const r of rows.filter((x) => x.kind === kind && !x.package).sort((a, b) => a.id.localeCompare(b.id)))
    out.push({ id: r.id, kind, scope: null, domain: null });
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
