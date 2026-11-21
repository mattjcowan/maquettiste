// One table can answer twice: its model index row (a table file) and its summary on the database side. Both go to the same place,
// so the palette shows the one that says where it lives (database · schema).
import type { ElementSummary } from "@/api/types";
import { tableKeyOf, type SearchHit } from "./engine";

/**
 * `keyOfFile` gives the resolved key of a table file (a file id, or the key of an overlay: `<entity>@<db>`,
 * `<entity>.<attribute>@<db>`, `<relation>@<db>`), read from the file's document when it is loaded. The index row names
 * only the overlay's entity, so without it a file is matched by its id and by the entity's own table.
 */
export function dropTableDuplicates(
  hits: readonly SearchHit[],
  byId: ReadonlyMap<string, ElementSummary>,
  keyOfFile?: (row: ElementSummary) => string | null | undefined,
): SearchHit[] {
  const dbHits = new Set(hits.filter((h) => h.db).map((h) => h.id));
  if (!dbHits.size) return [...hits];
  return hits.filter((h) => {
    if (h.db || h.kind !== "table") return true;
    const row = byId.get(h.id);
    if (!row?.database) return true;
    const keys = [row.id, keyOfFile?.(row) ?? (row.entity ? `${row.entity}@${row.database}` : null)];
    return !keys.some((key) => key && dbHits.has(tableKeyOf(row.database!, key)));
  });
}
