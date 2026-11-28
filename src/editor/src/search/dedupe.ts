// One table can answer twice: its model index row (a table file) and its summary on the database side. Both go to the same place,
// so the palette shows the one that says where it lives (database · schema).
import type { ElementSummary } from "@/api/types";
import { tableKeyOf, type SearchHit } from "./engine";

/**
 * A table file's key is its id, so a table file and its summary on the database side are matched by it.
 */
export function dropTableDuplicates(hits: readonly SearchHit[], byId: ReadonlyMap<string, ElementSummary>): SearchHit[] {
  const dbHits = new Set(hits.filter((h) => h.db).map((h) => h.id));
  if (!dbHits.size) return [...hits];
  return hits.filter((h) => {
    if (h.db || h.kind !== "table") return true;
    const row = byId.get(h.id);
    if (!row?.database) return true;
    return !dbHits.has(tableKeyOf(row.database, row.id));
  });
}
