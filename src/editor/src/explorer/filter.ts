// Explorer filtering and grouping, kept pure for tests.
import type { ElementSummary } from "@/api/types";
import { KIND_ORDER, displayName } from "@/model/model";

export interface ExplorerFilter {
  text: string;
  tags: string[];
  categories: string[];
  stereotypes: string[];
}

export const emptyFilter: ExplorerFilter = { text: "", tags: [], categories: [], stereotypes: [] };

export function matches(row: ElementSummary, filter: ExplorerFilter): boolean {
  if (filter.text && !displayName(row).toLowerCase().includes(filter.text.toLowerCase())) return false;
  if (filter.tags.length && !filter.tags.some((t) => row.tags.includes(t))) return false;
  if (filter.categories.length && !(row.category && filter.categories.includes(row.category))) return false;
  if (filter.stereotypes.length && !filter.stereotypes.some((s) => row.stereotypes.includes(s))) return false;
  return true;
}

export type ExplorerRow =
  { type: "group"; key: string; label: string; count: number; collapsed: boolean } | { type: "element"; key: string; summary: ElementSummary };

/** Rows grouped by package (packages by name, then elements without a package), kinds in explorer order. */
export function explorerRows(rows: ElementSummary[], filter: ExplorerFilter, collapsed: Set<string>): ExplorerRow[] {
  const packages = new Map(rows.filter((r) => r.kind === "package").map((p) => [p.id, p]));
  const groups = new Map<string, ElementSummary[]>();
  for (const row of rows) {
    if (!matches(row, filter)) continue;
    const key = row.kind === "package" ? (row.package ?? row.id) : (row.package ?? "");
    const groupKey = row.kind === "package" ? row.id : key;
    const list = groups.get(groupKey) ?? [];
    list.push(row);
    groups.set(groupKey, list);
  }
  const order = [...groups.keys()].sort((a, b) => {
    if (a === "") return 1;
    if (b === "") return -1;
    return (packages.get(a)?.name ?? a).localeCompare(packages.get(b)?.name ?? b);
  });
  const out: ExplorerRow[] = [];
  for (const key of order) {
    const items = groups.get(key)!.sort((a, b) => {
      if (a.kind === "package") return -1;
      if (b.kind === "package") return 1;
      const k = KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind);
      return k !== 0 ? k : displayName(a).localeCompare(displayName(b));
    });
    const label = key === "" ? "Project" : (packages.get(key)?.name ?? "(unknown package)");
    const isCollapsed = collapsed.has(key);
    out.push({ type: "group", key: `group:${key}`, label, count: items.filter((i) => i.kind !== "package").length, collapsed: isCollapsed });
    if (isCollapsed) continue;
    for (const summary of items) if (summary.kind !== "package") out.push({ type: "element", key: summary.id, summary });
  }
  return out;
}
