// The Generate screen's centre tabs, pure: Plan (always there) and one tab per open pack (generation-ui.md 3).
import type { GenerationState, PackPane } from "@/state/store";

type Tabs = Pick<GenerationState, "packTabs" | "packTab" | "packPane" | "packFocus">;

/** Opens (or shows) a pack's editor, on a tab and optionally focused on a unit or a parameter. */
export function openPackTab(state: Tabs, pack: string, pane?: PackPane, focus?: { unit?: string; parameter?: string; file?: string }): Tabs {
  const packTabs = state.packTabs.includes(pack) ? state.packTabs : [...state.packTabs, pack];
  const packPane = pane ? { ...state.packPane, [pack]: pane } : state.packPane;
  return { packTabs, packTab: pack, packPane, packFocus: focus ? { pack, ...focus } : null };
}

/** Closes a pack tab; when it was showing, its right neighbour shows, else its left one, else the Plan screen. */
export function closePackTab(state: Tabs, pack: string): Tabs {
  const at = state.packTabs.indexOf(pack);
  if (at < 0) return state;
  const packTabs = state.packTabs.filter((p) => p !== pack);
  const packTab = state.packTab === pack ? (packTabs[at] ?? packTabs[at - 1] ?? null) : state.packTab;
  const { [pack]: _closed, ...packPane } = state.packPane;
  void _closed;
  return { packTabs, packTab, packPane, packFocus: null };
}
