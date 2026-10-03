// What the inspector (the right sidebar) shows: the ACTIVE context, never the last element selected anywhere. An
// element editor tab showing in the centre wins; Settings and Reference data have no inspector (the screen is its own
// panel); the Generate screen shows the open pack and its focused unit; on the Databases side (the Database screen, or
// the Databases explorer active) a picked table shows as the TABLE, by its resolved key, never its entity; elsewhere the
// active explorer's own selection, or an empty state naming that explorer when it has none (after a rail switch).
import { activeTab, type EditorTabsState } from "@/editors/tabs";
import { RAIL_LABELS } from "@/model/labels";
import { EXTENSIONS_TAB } from "@/workspaces/generate/packTabs";
import type { EditorState, InspectedTable, SidebarView } from "@/state/store";
import { isLaidOutKey, type TablePart } from "@/workspaces/database/tableParts";

export type InspectorContext =
  | { mode: "element"; ids: string[]; source: "editor" | "explorer" }
  | { mode: "empty"; place: string; noun: "an element" | "a pack" }
  | { mode: "pack"; pack: string; unit: string | null }
  | { mode: "table"; database: string; key: string; column: string | null; part: TablePart | null }
  | { mode: "none" };

export type ContextInput = Pick<EditorState, "workspace" | "editors" | "generation" | "selectionBy"> & {
  explorer: { active: SidebarView; pinned?: SidebarView | null };
  /** The explorer the last selection was made in (the pinned second explorer keeps its own selection). */
  selectionFrom?: SidebarView | null;
  /** Whether an element still exists (the loaded index); a deleted element never shows, even before the store is pruned. */
  exists?: (id: string) => boolean;
  /** The table picked on the Databases side (the store's `inspectedTable`). */
  inspectedTable?: InspectedTable | null;
};

export function inspectorContext(s: ContextInput): InspectorContext {
  const tab = activeTab(s.editors as EditorTabsState);
  // A table the model lays out has an editor by its key (no file): the inspector shows it as the table, with its pick.
  if (tab?.kind === "table" && isLaidOutKey(tab.id)) {
    const database = tab.id.slice(tab.id.lastIndexOf("@") + 1);
    const t = s.inspectedTable?.database === database && s.inspectedTable.key === tab.id ? s.inspectedTable : null;
    return { mode: "table", database, key: tab.id, column: t?.column ?? null, part: t?.part ?? null };
  }
  if (tab) return { mode: "element", ids: [tab.id], source: "editor" };
  if (s.workspace === "settings" || s.workspace === "reference-data") return { mode: "none" };
  if (s.workspace === "generate") {
    const pack = s.generation.packTab;
    if (!pack) return { mode: "empty", place: RAIL_LABELS.generate.label, noun: "a pack" };
    if (pack === EXTENSIONS_TAB) return { mode: "none" };
    const focus = s.generation.packFocus;
    return { mode: "pack", pack, unit: focus && focus.pack === pack ? (focus.unit ?? null) : null };
  }
  const shown = s.explorer.active;
  // A selection made in the pinned second explorer shows while that explorer is still pinned beside the active one.
  const active = s.selectionFrom && s.selectionFrom !== shown && s.selectionFrom === s.explorer.pinned ? s.selectionFrom : shown;
  // The Databases side inspects the table itself, or one part of it (a column, a key, an index, a check). Selecting an
  // element clears the pick.
  if (s.inspectedTable && (s.workspace === "database" || active === "databases")) {
    const t = s.inspectedTable;
    return { mode: "table", database: t.database, key: t.key, column: t.column, part: t.part ?? null };
  }
  // The Database screen's own picks of a view or a sequence are a selection on the Databases side, whichever explorer shows.
  const side = s.workspace === "database" && s.selectionFrom === "databases" ? "databases" : active;
  const ids = (s.selectionBy[side] ?? []).filter((id) => !s.exists || s.exists(id));
  if (!ids.length) return { mode: "empty", place: RAIL_LABELS[active].label, noun: active === "generate" ? "a pack" : "an element" };
  return { mode: "element", ids, source: "explorer" };
}

/** The inspector's heading for a context without an element (the Playwright walk asserts it). */
export function emptyTitle(c: Extract<InspectorContext, { mode: "empty" }>): string {
  return `Select ${c.noun} in ${c.place}`;
}

/** True when the inspector shows exactly this element (the editor then hides the fields the inspector already shows). */
export function showsElement(c: InspectorContext, id: string): boolean {
  return c.mode === "element" && c.ids.length === 1 && c.ids[0] === id;
}
