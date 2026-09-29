// The Database screen's table section (explorer-redesign.md 1.3): the table summaries filtered by name, sorted by
// name, capped so the list stays quick on a 10,000-table database. Pure.
import type { TableSummary } from "@/api/types";

export const TABLE_LIST_CAP = 300;

/** Tables whose name (or schema.name) contains every word of the filter, case-insensitively; at most `cap`. */
export function filterTables(tables: readonly TableSummary[], filter: string, cap = TABLE_LIST_CAP): { tables: TableSummary[]; total: number; more: number } {
  const words = filter.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const matching = tables
    .filter((t) => {
      const text = `${t.schema ?? ""}.${t.name}`.toLowerCase();
      return words.every((w) => text.includes(w));
    })
    .sort((a, b) => a.name.localeCompare(b.name) || (a.schema ?? "").localeCompare(b.schema ?? ""));
  return { tables: matching.slice(0, cap), total: matching.length, more: Math.max(0, matching.length - cap) };
}

/**
 * The databases the Database screen offers: the index's database rows (fresh after a create, a rename or a delete),
 * in the project's order, new ones by name after it; the project's list alone until the index has loaded.
 */
export function databaseList<T extends { id: string; kind: string; name: string }>(project: readonly T[] | undefined, index: readonly T[] | undefined): T[] {
  if (!index) return [...(project ?? [])];
  const order = new Map((project ?? []).map((d, i) => [d.id, i]));
  const at = (id: string) => order.get(id) ?? Number.MAX_SAFE_INTEGER;
  return index.filter((r) => r.kind === "database").sort((a, b) => at(a.id) - at(b.id) || a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
}
