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

/**
 * Follows a pack rename: the tab keeps its place and its pane under the new name, and a focus or a ticked plan choice
 * on the old name moves too.
 */
export function renamePackTab<T extends Tabs & Pick<GenerationState, "chosenPacks">>(state: T, from: string, to: string): T {
  const swap = (name: string) => (name === from ? to : name);
  const { [from]: pane, ...others } = state.packPane;
  return {
    ...state,
    packTabs: state.packTabs.map(swap),
    packTab: state.packTab === null ? null : swap(state.packTab),
    packPane: pane ? { ...others, [to]: pane } : state.packPane,
    packFocus: state.packFocus?.pack === from ? { ...state.packFocus, pack: to } : state.packFocus,
    chosenPacks: state.chosenPacks ? [...new Set(state.chosenPacks.map(swap))].sort() : null,
  };
}

/** The Generate explorer's expanded rows after a pack rename (`p:<pack>` and the rows under it). */
export function renameExpandedKeys(expanded: ReadonlySet<string>, from: string, to: string): Set<string> {
  const head = `p:${from}`;
  return new Set([...expanded].map((key) => (key === head || key.startsWith(`${head}/`) ? `p:${to}${key.slice(head.length)}` : key)));
}
