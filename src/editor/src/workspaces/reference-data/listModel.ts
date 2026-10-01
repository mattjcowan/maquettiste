// The Reference data screen's type list (reference-types-seeds-localization.md 4.2): the types nested by their
// category path with a count on every group, or flat A to Z, filtered by the explorer's search syntax (`*` contains,
// the default, `^` starts with, `~` like with `%`, `=` equals; case-insensitive). Pure, so it is tested alone and
// filters 800 types well inside a frame.
import type { ElementSummary } from "@/api/types";
import { parseQuery, termMatcher } from "@/search/query";

export interface CategoryInfo {
  value: string;
  label: string;
  parent: string | null;
}

export interface RefTypeItem {
  id: string;
  name: string;
  /** The display name, else the name. */
  label: string;
  category: string | null;
  /** Category names from the root down; empty when the type has no category (or names a missing one). */
  categoryPath: string[];
  rows: number;
  fields: number;
  seeds: string[];
}

export const NO_CATEGORY = "No category";
const NO_CATEGORY_KEY = "cat:none";

/** The index's reference types with their category paths and row counts (the sum over their seeds). */
export function typeItems(index: readonly ElementSummary[] | undefined, categories: readonly CategoryInfo[]): RefTypeItem[] {
  const byId = new Map(categories.map((c) => [c.value, c]));
  const pathOf = (id: string | null): string[] => {
    const out: string[] = [];
    const seen = new Set<string>();
    for (let at = id ? byId.get(id) : undefined; at && !seen.has(at.value); at = at.parent ? byId.get(at.parent) : undefined) {
      seen.add(at.value);
      out.unshift(at.label);
    }
    return out;
  };
  const rows = new Map<string, number>();
  const seeds = new Map<string, string[]>();
  for (const r of index ?? []) {
    if (r.kind !== "seed" || !r.target) continue;
    rows.set(r.target, (rows.get(r.target) ?? 0) + (r.rowCount ?? 0));
    seeds.set(r.target, [...(seeds.get(r.target) ?? []), r.id]);
  }
  return (index ?? [])
    .filter((r) => r.kind === "reference-type")
    .map((r) => ({
      id: r.id,
      name: r.name,
      label: r.displayName || r.name,
      category: r.category ?? null,
      categoryPath: pathOf(r.category ?? null),
      rows: rows.get(r.id) ?? 0,
      fields: r.fieldCount ?? 0,
      seeds: seeds.get(r.id) ?? [],
    }));
}

/** Whether a type matches the search text: its name, display name or category path. */
export function typeMatcher(text: string): (item: RefTypeItem) => boolean {
  const match = termMatcher(parseQuery(text));
  if (!match) return () => true;
  return (item) =>
    match(item.name.toLowerCase()) || match(item.label.toLowerCase()) || (item.categoryPath.length > 0 && match(item.categoryPath.join(" › ").toLowerCase()));
}

export type ListRow =
  | { kind: "group"; key: string; label: string; depth: number; count: number; total: number; expanded: boolean }
  | { kind: "type"; key: string; item: RefTypeItem; depth: number };

export interface ListOptions {
  query: string;
  flat: boolean;
  /** Group keys the user collapsed; groups start expanded. */
  collapsed: ReadonlySet<string>;
}

export interface ListResult {
  rows: ListRow[];
  matched: number;
  total: number;
}

const byLabel = (a: RefTypeItem, b: RefTypeItem) => a.label.localeCompare(b.label, "en", { sensitivity: "base" }) || a.id.localeCompare(b.id);

interface Group {
  key: string;
  label: string;
  children: Map<string, Group>;
  items: RefTypeItem[];
  total: number;
  count: number;
}

const group = (key: string, label: string): Group => ({ key, label, children: new Map(), items: [], total: 0, count: 0 });

/** The visible rows: groups (with "matched of total" counts while searching) and types, or the flat A to Z list. */
export function listRows(items: readonly RefTypeItem[], options: ListOptions): ListResult {
  const matches = typeMatcher(options.query);
  const filtering = options.query.trim() !== "";
  const hits = items.filter(matches);
  if (options.flat)
    return { rows: [...hits].sort(byLabel).map((item) => ({ kind: "type", key: item.id, item, depth: 0 })), matched: hits.length, total: items.length };

  const root = group("", "");
  const hit = new Set(hits.map((h) => h.id));
  for (const item of items) {
    const path = item.categoryPath.length ? item.categoryPath : [NO_CATEGORY];
    let at = root;
    path.forEach((label, i) => {
      const key = item.categoryPath.length ? `cat:${path.slice(0, i + 1).join("/")}` : NO_CATEGORY_KEY;
      let next = at.children.get(label);
      if (!next) at.children.set(label, (next = group(key, label)));
      next.total++;
      if (hit.has(item.id)) next.count++;
      at = next;
    });
    at.items.push(item);
  }
  const rows: ListRow[] = [];
  const walk = (g: Group, depth: number) => {
    const children = [...g.children.values()].sort((a, b) =>
      a.key === NO_CATEGORY_KEY ? 1 : b.key === NO_CATEGORY_KEY ? -1 : a.label.localeCompare(b.label, "en", { sensitivity: "base" }),
    );
    for (const child of children) {
      if (filtering && child.count === 0) continue;
      const expanded = filtering || !options.collapsed.has(child.key);
      rows.push({ kind: "group", key: child.key, label: child.label, depth, count: child.count, total: child.total, expanded });
      if (!expanded) continue;
      walk(child, depth + 1);
      for (const item of [...child.items].sort(byLabel)) if (hit.has(item.id)) rows.push({ kind: "type", key: item.id, item, depth: depth + 1 });
    }
  };
  walk(root, 0);
  return { rows, matched: hits.length, total: items.length };
}

/** A group's count: "12", or "3 of 12" while a search hides some of its types. */
export function countLabel(row: { count: number; total: number }, filtering: boolean): string {
  return filtering && row.count !== row.total ? `${row.count} of ${row.total}` : String(row.total);
}

/**
 * The Used by tab's group of an attribute's owner: a reference type has no domain (RS3), so its group is
 * "Reference type · <category path>", or "Reference types" without a category; any other owner is grouped by its
 * kind and domain ("Entity · Billing", "Entity · Not in a domain").
 */
export function usageGroupLabel(
  owner: { kind: string; kindLabel: string } | undefined,
  domainName: string | null,
  categoryPath: readonly string[],
  notInDomain: string,
): string {
  if (!owner) return `? · ${domainName ?? notInDomain}`;
  if (owner.kind === "reference-type") return categoryPath.length ? `${owner.kindLabel} · ${categoryPath.join(" › ")}` : "Reference types";
  return `${owner.kindLabel} · ${domainName ?? notInDomain}`;
}
