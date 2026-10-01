// What the Database screen's DDL preview renders, pure: no pack is assumed by name. The pack is the enabled pack with a
// unit rendered per database (the Storage tab's rule, `databaseUnits`), preferring a unit whose name mentions schema
// or table; with a table selected, that pack's `each table` unit (preferring one whose name mentions table) renders
// the table, else the database unit renders the whole database.
import { databaseUnits } from "@/workspaces/reference-data/storageChoices";

export interface DdlPreviewTarget {
  pack: string;
  unit: string;
  /** The element rendered: the table key, or the database id. */
  elementId: string;
  /** Whether it renders the selected table or the whole database. */
  scope: "table" | "database";
}

type PackLike = { name: string; enabled?: boolean; units: readonly { id: string; for: string }[] };

const PREFERRED = /schema|table/i;

/** The unit the DDL preview renders, or null when no enabled pack has a unit rendered per database. */
export function ddlPreviewTarget(packs: readonly PackLike[], database: string | null, table: string | null): DdlPreviewTarget | null {
  if (!database) return null;
  const units = databaseUnits(packs);
  const chosen = units.find((u) => PREFERRED.test(u.unit)) ?? units[0];
  if (!chosen) return null;
  if (table) {
    const each = (packs.find((p) => p.name === chosen.pack)?.units ?? []).filter((u) => u.for.trim().replace(/\s+/g, " ") === "each table");
    const unit = each.find((u) => /table/i.test(u.id)) ?? each[0];
    if (unit) return { pack: chosen.pack, unit: unit.id, elementId: table, scope: "table" };
  }
  return { pack: chosen.pack, unit: chosen.unit, elementId: database, scope: "database" };
}

/** The pane header's line: the pack and unit previewed, and what it renders. */
export function ddlPreviewCaption(target: DdlPreviewTarget, tableName: string | null): string {
  return `${target.pack}/${target.unit} · ${target.scope === "table" && tableName ? tableName : "whole database"}`;
}

export const NO_DDL_UNIT = "No enabled pack has a unit rendered per database (each database or select databases), so there is no DDL to preview.";
