// What the Database screen's DDL preview renders, pure: no pack is assumed by name. The pack is the enabled pack with a
// unit rendered per database (the Storage tab's rule, `databaseUnits`), preferring a unit whose name mentions schema
// or table; with a table selected, that pack's `each table` unit (preferring one whose name mentions table) renders
// the table; with a view, sequence, routine, database type or SQL object selected, its `each view`, `each sequence`, `each
// routine`, `each database type` or `each sql object` unit renders it; else the database unit renders the whole database. A
// unit may write nothing for an element (the sql-ddl pack writes a view's own script only when its objectScripts parameter is
// on): the screen then shows the whole database, which creates it too. A query creates nothing in the database: its target is
// the engine's own SQL of the query (GET /api/model/queries/{id}/sql), whatever the packs.
import { databaseUnits } from "@/workspaces/reference-data/storageChoices";

export interface DdlPreviewTarget {
  pack: string;
  unit: string;
  /** The element rendered: the table key, or the database id. */
  elementId: string;
  /** Whether it renders the selected table, view, sequence, routine, database type, SQL object or query (its SQL, not a pack's
   * unit: `pack` and `unit` are empty), or the whole database. */
  scope: "table" | DdlObjectKind | "database";
}

/** The kinds the Database screen picks besides tables. */
export type DdlObjectKind = "view" | "sequence" | "routine" | "query" | "database-type" | "sql-object";

/** A view, sequence, routine, database type or SQL object picked on the Database screen. */
export interface DdlObject {
  kind: DdlObjectKind;
  id: string;
}

type PackLike = { name: string; enabled?: boolean; units: readonly { id: string; for: string }[] };

const PREFERRED = /schema|table/i;

/** The unit the DDL preview renders, or null when no enabled pack has a unit rendered per database. */
export function ddlPreviewTarget(
  packs: readonly PackLike[],
  database: string | null,
  table: string | null,
  object: DdlObject | null = null,
): DdlPreviewTarget | null {
  if (!database) return null;
  if (object?.kind === "query") return { pack: "", unit: "", elementId: object.id, scope: "query" };
  const units = databaseUnits(packs);
  const chosen = units.find((u) => PREFERRED.test(u.unit)) ?? units[0];
  if (!chosen) return null;
  // A kind's scope noun: `database-type` is `each database type`; a unit whose id names the kind is preferred.
  const eachOf = (kind: string) => {
    const noun = kind.replace(/-/g, " ");
    const each = (packs.find((p) => p.name === chosen.pack)?.units ?? []).filter((u) => u.for.trim().replace(/\s+/g, " ") === `each ${noun}`);
    return each.find((u) => new RegExp(noun.replace(/ /g, "[- _]?"), "i").test(u.id)) ?? each[0];
  };
  if (object) {
    const unit = eachOf(object.kind);
    if (unit) return { pack: chosen.pack, unit: unit.id, elementId: object.id, scope: object.kind };
  } else if (table) {
    const unit = eachOf("table");
    if (unit) return { pack: chosen.pack, unit: unit.id, elementId: table, scope: "table" };
  }
  return { pack: chosen.pack, unit: chosen.unit, elementId: database, scope: "database" };
}

/** The whole database's target, the fallback of an object's unit that writes nothing. */
export function databaseTargetOf(packs: readonly PackLike[], database: string | null): DdlPreviewTarget | null {
  return ddlPreviewTarget(packs, database, null, null);
}

/** The pane header's line: the pack and unit previewed, and what it renders. */
export function ddlPreviewCaption(target: DdlPreviewTarget, name: string | null): string {
  if (target.scope === "query") return `query SQL · ${name ?? target.elementId}`;
  return `${target.pack}/${target.unit} · ${target.scope !== "database" && name ? name : "whole database"}`;
}

/** The line shown above the whole database when the picked object's unit wrote nothing. */
export function emptyObjectUnitNote(target: DdlPreviewTarget, name: string): string {
  return `${target.pack}/${target.unit} writes no file for ${name} with this project's parameters; the whole database below creates it.`;
}

export const NO_DDL_UNIT = "No enabled pack has a unit rendered per database (each database or select databases), so there is no DDL to preview.";
