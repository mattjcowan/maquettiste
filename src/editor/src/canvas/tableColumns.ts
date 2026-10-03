// What a table card on the database diagram shows (the canvas toolbar's Display menu, kept per browser): every column, the key
// columns only (primary key, foreign key and single-column unique columns), or the table names only; and where a foreign key's
// edge is anchored: at its first column's row when the card shows it, else at the card's header, on the side facing the other
// table.
import type { TableView } from "@/api/types";
import { local } from "@/lib/storage";

export type TableDisplay = "all" | "keys" | "names";

export interface DatabaseDisplay {
  mode: TableDisplay;
  minimap: boolean;
}

export const DEFAULT_DATABASE_DISPLAY: DatabaseDisplay = { mode: "all", minimap: true };
const KEY = "mq.display.database";

export function loadDatabaseDisplay(): DatabaseDisplay {
  const kept = local.getJson<Partial<DatabaseDisplay>>(KEY) ?? {};
  const mode = kept.mode === "keys" || kept.mode === "names" || kept.mode === "all" ? kept.mode : DEFAULT_DATABASE_DISPLAY.mode;
  return { mode, minimap: typeof kept.minimap === "boolean" ? kept.minimap : DEFAULT_DATABASE_DISPLAY.minimap };
}

export function saveDatabaseDisplay(display: DatabaseDisplay): void {
  local.setJson(KEY, display);
}

/** The column keys of a table that are part of a key: primary, foreign, or a single-column unique constraint or index. */
export function keyColumns(table: TableView): Set<string> {
  const out = new Set<string>();
  for (const c of table.columns) if (c.isPrimaryKey || c.isForeignKey) out.add(c.key);
  for (const u of table.uniques) if (u.columns.length === 1) out.add(u.columns[0]);
  for (const i of table.indexes) if (i.unique && i.columns.length === 1) out.add(i.columns[0].column);
  return out;
}

/** The columns a card shows in a display mode. */
export function shownColumns(table: TableView, mode: TableDisplay): TableView["columns"] {
  if (mode === "names") return [];
  if (mode === "all") return table.columns;
  const keys = keyColumns(table);
  return table.columns.filter((c) => keys.has(c.key));
}

/** A row's handles: `L:<key>` on its left edge, `R:<key>` on its right (the header's: `L:` and `R:`). */
export const handleId = (side: "L" | "R", column: string | null) => `${side}:${column ?? ""}`;

/**
 * The handles a foreign key's edge joins: the side of each card facing the other (left to right when the referenced card is to
 * the right of the referencing one), at the first key column's row when shown, else at the header.
 */
export function edgeHandles(
  fk: { columns: string[]; referencedColumns: string[] },
  shown: { source: ReadonlySet<string>; target: ReadonlySet<string> },
  centers: { source: number; target: number },
  self = false,
): { sourceHandle: string; targetHandle: string } {
  const rightward = self || centers.target >= centers.source;
  const from = fk.columns[0] && shown.source.has(fk.columns[0]) ? fk.columns[0] : null;
  const to = fk.referencedColumns[0] && shown.target.has(fk.referencedColumns[0]) ? fk.referencedColumns[0] : null;
  return { sourceHandle: handleId(rightward ? "R" : "L", from), targetHandle: handleId(self ? "R" : rightward ? "L" : "R", to) };
}
