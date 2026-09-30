// What the inspector (the right sidebar) shows: the ACTIVE context, never the last element selected anywhere. An
// element editor tab showing in the centre wins; Settings and Reference data have no inspector (the screen is its own
// panel); the Generate screen shows the open pack and its focused unit; elsewhere the active explorer's own
// selection, or an empty state naming that explorer when it has none (after a rail switch).
import { activeTab, type EditorTabsState } from "@/editors/tabs";
import { RAIL_LABELS } from "@/model/labels";
import type { EditorState, SidebarView } from "@/state/store";

export type InspectorContext =
  | { mode: "element"; ids: string[]; source: "editor" | "explorer" }
  | { mode: "empty"; place: string; noun: "an element" | "a pack" }
  | { mode: "pack"; pack: string; unit: string | null }
  | { mode: "none" };

export type ContextInput = Pick<EditorState, "workspace" | "editors" | "generation" | "selectionBy"> & {
  explorer: { active: SidebarView; pinned?: SidebarView | null };
  /** The explorer the last selection was made in (the pinned second explorer keeps its own selection). */
  selectionFrom?: SidebarView | null;
  /** Whether an element still exists (the loaded index); a deleted element never shows, even before the store is pruned. */
  exists?: (id: string) => boolean;
};

export function inspectorContext(s: ContextInput): InspectorContext {
  const tab = activeTab(s.editors as EditorTabsState);
  if (tab) return { mode: "element", ids: [tab.id], source: "editor" };
  if (s.workspace === "settings" || s.workspace === "reference-data") return { mode: "none" };
  if (s.workspace === "generate") {
    const pack = s.generation.packTab;
    if (!pack) return { mode: "empty", place: RAIL_LABELS.generate.label, noun: "a pack" };
    const focus = s.generation.packFocus;
    return { mode: "pack", pack, unit: focus && focus.pack === pack ? (focus.unit ?? null) : null };
  }
  const shown = s.explorer.active;
  // A selection made in the pinned second explorer shows while that explorer is still pinned beside the active one.
  const active = s.selectionFrom && s.selectionFrom !== shown && s.selectionFrom === s.explorer.pinned ? s.selectionFrom : shown;
  const ids = (s.selectionBy[active] ?? []).filter((id) => !s.exists || s.exists(id));
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
