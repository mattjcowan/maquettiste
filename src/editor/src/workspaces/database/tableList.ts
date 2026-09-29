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
