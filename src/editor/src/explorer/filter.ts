// The explorer's filter state and its predicates (explorer-redesign.md 3.2). The search worker (search/engine.ts)
// answers which rows match; the tree (explorer/tree.ts) walks the matches and their ancestors (`filteredRows`).
// `rowPredicate` is the same test on one index row for code that has no worker at hand; the worker builds its chip
// tests from the builders below. Scopes are named sets of chips (per user in localStorage, or for the team in
// maquettiste.json `explorer.scopes`); the pinned filter keeps one explorer's filter across reloads.
import type { ElementSummary } from "@/api/types";
import { displayName } from "@/model/model";
import { parseQuery, termMatcher } from "@/search/query";

export interface ExplorerFilter {
  text: string;
  /** Element kinds, any of. */
  kinds: string[];
  /** A domain id: the domain and its sub-domains. */
  domain: string | null;
  tags: string[];
  /** Category-tree node ids, any of; each includes its descendants. */
  categories: string[];
  stereotypes: string[];
  /** Only elements with validation errors. */
  errors: boolean;
  /** A diagram id: only its members. */
  diagram: string | null;
  /** Entities by storage (erratum E43): domain only (no binding) or bound to a database; null does not filter. */
  storage: StorageChip | null;
}

export type StorageChip = "domain-only" | "bound";

export const emptyFilter: ExplorerFilter = {
  text: "",
  kinds: [],
  domain: null,
  tags: [],
  categories: [],
  stereotypes: [],
  errors: false,
  diagram: null,
  storage: null,
};

/** The number of active chips (the count on the filter button). */
export function chipCount(f: ExplorerFilter): number {
  return (
    f.kinds.length +
    (f.domain ? 1 : 0) +
    f.tags.length +
    f.categories.length +
    f.stereotypes.length +
    (f.errors ? 1 : 0) +
    (f.diagram ? 1 : 0) +
    (f.storage ? 1 : 0)
  );
}

/** Whether any condition is set. */
export function isFiltering(filter: ExplorerFilter): boolean {
  return !!filter.text.trim() || chipCount(filter) > 0;
}

/** A filter read from storage or settings, with every member present and typed. */
export function normalizeFilter(value: unknown): ExplorerFilter {
  const v = (value && typeof value === "object" ? value : {}) as Record<string, unknown>;
  const list = (x: unknown) => (Array.isArray(x) ? x.filter((s): s is string => typeof s === "string" && s.length > 0) : []);
  const id = (x: unknown) => (typeof x === "string" && x ? x : null);
  return {
    text: typeof v.text === "string" ? v.text : "",
    kinds: list(v.kinds),
    domain: id(v.domain),
    tags: list(v.tags),
    categories: list(v.categories),
    stereotypes: list(v.stereotypes),
    errors: v.errors === true,
    diagram: id(v.diagram),
    storage: v.storage === "domain-only" || v.storage === "bound" ? v.storage : null,
  };
}

// ------------------------------------------------------------------ scopes

/** A named set of chips: the shape of maquettiste.json `explorer.scopes` items (text is not part of a scope). */
export interface FilterScope {
  name: string;
  kinds: string[];
  domain: string | null;
  tags: string[];
  stereotypes: string[];
  categories: string[];
  errors: boolean;
  diagram: string | null;
}

/** The chips of a filter saved under a name. */
export function scopeOf(name: string, f: ExplorerFilter): FilterScope {
  return {
    name,
    kinds: [...f.kinds],
    domain: f.domain,
    tags: [...f.tags],
    stereotypes: [...f.stereotypes],
    categories: [...f.categories],
    errors: f.errors,
    diagram: f.diagram,
  };
}

/** A scope applied: its chips replace the filter's, the search text stays. */
export function applyScope(f: ExplorerFilter, scope: Partial<FilterScope>): ExplorerFilter {
  const n = normalizeFilter(scope);
  return { ...n, text: f.text };
}

/** Whether two filters carry the same chips (the scope picker marks the scope in use). */
export function sameChips(a: ExplorerFilter, b: ExplorerFilter): boolean {
  const same = (x: readonly string[], y: readonly string[]) => x.length === y.length && [...x].sort().join("\u0000") === [...y].sort().join("\u0000");
  return (
    same(a.kinds, b.kinds) &&
    a.domain === b.domain &&
    same(a.tags, b.tags) &&
    same(a.categories, b.categories) &&
    same(a.stereotypes, b.stereotypes) &&
    a.errors === b.errors &&
    a.diagram === b.diagram
  );
}

// ------------------------------------------------------------------ predicate builders

/** Kind: any of the kind ids; undefined when no kind is asked. */
export function kindTest(kinds: readonly string[]): ((kind: string) => boolean) | undefined {
  if (!kinds.length) return undefined;
  const set = new Set(kinds);
  return (kind) => set.has(kind);
}

/**
 * Domain scope: a package id is in scope when it is the domain or nested under it. `parentOf` gives a package's
 * parent package. The walk is memoized and guarded against cycles.
 */
export function domainTest(domain: string, parentOf: (id: string) => string | null | undefined): (pkg: string | null | undefined) => boolean {
  const memo = new Map<string, boolean>([[domain, true]]);
  const inScope = (pkg: string | null | undefined): boolean => {
    if (!pkg) return false;
    const known = memo.get(pkg);
    if (known !== undefined) return known;
    memo.set(pkg, false);
    const hit = inScope(parentOf(pkg));
    memo.set(pkg, hit);
    return hit;
  };
  return inScope;
}

/** Category subtree: the chosen nodes and every descendant, from a node → parent map. */
export function categoryScope(ids: readonly string[], parents: ReadonlyMap<string, string | null>): Set<string> {
  const wanted = new Set(ids);
  const out = new Set(ids);
  for (const node of parents.keys()) {
    for (let p: string | null | undefined = node, guard = 0; p && guard < 64; p = parents.get(p), guard++) {
      if (wanted.has(p)) {
        out.add(node);
        break;
      }
    }
  }
  return out;
}

/** What the chip predicates need besides the rows: none of it is on an index row. */
export interface FilterContext {
  byId?: ReadonlyMap<string, ElementSummary>;
  /** Category node → parent node. */
  categoryParents?: ReadonlyMap<string, string | null>;
  /** Elements with validation errors. */
  errorIds?: ReadonlySet<string>;
  /** The chosen diagram's members. */
  diagramMembers?: ReadonlySet<string>;
  /** The entities the storage chip keeps. */
  storageIds?: ReadonlySet<string>;
}

/** The row test of a filter: the search term and every chip. */
export function rowPredicate(filter: ExplorerFilter, ctx: FilterContext = {}): (row: ElementSummary) => boolean {
  const match = termMatcher(parseQuery(filter.text));
  const kind = kindTest(filter.kinds);
  const domain = filter.domain ? domainTest(filter.domain, (id) => ctx.byId?.get(id)?.package) : undefined;
  const categories = filter.categories.length ? categoryScope(filter.categories, ctx.categoryParents ?? new Map()) : undefined;
  return (row) => {
    if (match && !match(displayName(row).toLowerCase()) && !(row.displayName && match(row.displayName.toLowerCase()))) return false;
    if (kind && !kind(row.kind)) return false;
    if (domain && !domain(row.kind === "package" ? row.id : row.package)) return false;
    if (filter.tags.length && !filter.tags.some((t) => row.tags.includes(t))) return false;
    if (categories && !(row.category && categories.has(row.category))) return false;
    if (filter.stereotypes.length && !filter.stereotypes.some((s) => row.stereotypes.includes(s))) return false;
    if (filter.errors && !ctx.errorIds?.has(row.id)) return false;
    if (filter.diagram && !ctx.diagramMembers?.has(row.id)) return false;
    if (filter.storage && !ctx.storageIds?.has(row.id)) return false;
    return true;
  };
}

export function matches(row: ElementSummary, filter: ExplorerFilter, ctx?: FilterContext): boolean {
  return rowPredicate(filter, ctx)(row);
}

/** The ids of the rows that match. */
export function matchingIds(rows: Iterable<ElementSummary>, filter: ExplorerFilter, ctx?: FilterContext): string[] {
  const test = rowPredicate(filter, ctx);
  const out: string[] = [];
  for (const row of rows) if (test(row)) out.push(row.id);
  return out;
}
