// Element editor tabs (explorer-redesign.md 3.6): pure state, kept in the editor store. A single click opens an
// element in the preview tab, which the next single click replaces; editing, a double click or Enter pins it. A tab in
// General mode ("Follow selection") follows the selection and keeps its sub-tab, so a user can walk many elements on
// the same sub-tab without reopening anything.

/** The kinds with an element editor in the centre area (a domain: General, Tags, Categories, 1.11). A reference type
 * opens in the Reference data screen. A table file has one when it is a table of its own (designed or imported); the
 * overlay of a projected table is edited in the Database screen. */
export const EDITOR_KINDS = [
  "entity",
  "relation",
  "enum",
  "value-object",
  "scalar-type",
  "package",
  "process",
  "actor",
  "scenario",
  "table",
  "view",
  "sequence",
  "routine",
  "database-type",
  "sql-object",
  "query",
] as const;
export type EditorKind = (typeof EDITOR_KINDS)[number];

/** Whether an element opens in an editor; given its index row, a table overlay (a row naming its entity) does not. */
export function hasEditor(kind: string | null | undefined, row?: { entity?: string | null }): kind is EditorKind {
  if (kind === "table" && row?.entity) return false;
  return !!kind && (EDITOR_KINDS as readonly string[]).includes(kind);
}

export interface EditorTab {
  /** Stable for the tab's life, while `id` changes when a General-mode tab follows the selection. */
  key: string;
  id: string;
  kind: EditorKind;
  /** False: the preview tab (at most one), replaced by the next single click. */
  pinned: boolean;
  /** General mode: the tab follows the selection. At most one tab has it. */
  follow: boolean;
}

export interface EditorTabsState {
  tabs: EditorTab[];
  /** The tab shown in the centre area; null shows the screen (the canvas, the database, ...). */
  active: string | null;
  /** The sub-tab last shown per kind: kept when another element of the kind is shown. */
  view: Partial<Record<EditorKind, string>>;
  next: number;
}

export const emptyEditorTabs = (): EditorTabsState => ({ tabs: [], active: null, view: {}, next: 1 });

export interface EditorTarget {
  id: string;
  kind: EditorKind;
}

export const activeTab = (state: EditorTabsState): EditorTab | null => state.tabs.find((t) => t.key === state.active) ?? null;
export const previewTab = (state: EditorTabsState): EditorTab | null => state.tabs.find((t) => !t.pinned) ?? null;

/** Opens an element: shows its tab when one is open (pinning it when asked), else replaces the preview tab (a
 * preview open) or adds a tab after the active one. A preview open while the General-mode tab shows goes to it. */
export function openTab(state: EditorTabsState, target: EditorTarget, options: { pin: boolean }): EditorTabsState {
  // While the General-mode tab shows, a preview open is the selection moving: that tab shows the element.
  if (!options.pin && activeTab(state)?.follow) return followSelection(state, target);
  // A pinned open never lands in the General-mode tab: that tab leaves the element when the selection moves.
  const existing = state.tabs.find((t) => t.id === target.id && !t.follow) ?? (options.pin ? undefined : state.tabs.find((t) => t.id === target.id));
  if (existing) {
    const tabs = options.pin && !existing.pinned ? state.tabs.map((t) => (t === existing ? { ...t, pinned: true } : t)) : state.tabs;
    return tabs === state.tabs && state.active === existing.key ? state : { ...state, tabs, active: existing.key };
  }
  const preview = previewTab(state);
  if (preview && !preview.follow) {
    const replaced: EditorTab = { ...preview, id: target.id, kind: target.kind, pinned: options.pin };
    return { ...state, tabs: state.tabs.map((t) => (t === preview ? replaced : t)), active: replaced.key };
  }
  const tab: EditorTab = { key: `t${state.next}`, id: target.id, kind: target.kind, pinned: options.pin, follow: false };
  const at = state.tabs.findIndex((t) => t.key === state.active);
  const tabs = [...state.tabs];
  tabs.splice(at < 0 ? tabs.length : at + 1, 0, tab);
  return { ...state, tabs, active: tab.key, next: state.next + 1 };
}

/** Closes a tab; when it was showing, its right neighbour shows (else its left one, else the screen). */
export function closeTab(state: EditorTabsState, key: string): EditorTabsState {
  const at = state.tabs.findIndex((t) => t.key === key);
  if (at < 0) return state;
  const tabs = state.tabs.filter((t) => t.key !== key);
  const active = state.active === key ? (tabs[at]?.key ?? tabs[at - 1]?.key ?? null) : state.active;
  return { ...state, tabs, active };
}

/** A tab the bulk closes leave open: one with unsaved changes. */
export type KeepTab = (tab: EditorTab) => boolean;

/** Closes the tabs `closes` picks, except those `keep` holds on to. When the shown tab closes, `fallback` shows when it
 * is still open, else the nearest tab left at or after the shown one's place, else the one before it, else the screen. */
function closeWhere(state: EditorTabsState, closes: (tab: EditorTab, at: number) => boolean, keep: KeepTab, fallback?: string): EditorTabsState {
  const tabs = state.tabs.filter((t, i) => !closes(t, i) || keep(t));
  if (tabs.length === state.tabs.length) return state;
  if (tabs.some((t) => t.key === state.active)) return { ...state, tabs };
  const at = state.tabs.findIndex((t) => t.key === state.active);
  const left = new Set(tabs.map((t) => t.key));
  const after = state.tabs.slice(Math.max(at, 0)).find((t) => left.has(t.key));
  const before = state.tabs
    .slice(0, Math.max(at, 0))
    .reverse()
    .find((t) => left.has(t.key));
  const active = state.active === null ? null : fallback && left.has(fallback) ? fallback : (after?.key ?? before?.key ?? null);
  return { ...state, tabs, active };
}

const keepNone: KeepTab = () => false;

/** Closes every tab but `key`, keeping the ones `keep` holds (unsaved changes). */
export const closeOthers = (state: EditorTabsState, key: string, keep: KeepTab = keepNone): EditorTabsState =>
  state.tabs.some((t) => t.key === key) ? closeWhere(state, (t) => t.key !== key, keep, key) : state;

/** Closes the tabs to the right of `key`, keeping the ones `keep` holds. */
export function closeToRight(state: EditorTabsState, key: string, keep: KeepTab = keepNone): EditorTabsState {
  const from = state.tabs.findIndex((t) => t.key === key);
  return from < 0 ? state : closeWhere(state, (_, i) => i > from, keep, key);
}

/** Closes every tab, keeping the ones `keep` holds. */
export const closeAll = (state: EditorTabsState, keep: KeepTab = keepNone): EditorTabsState => closeWhere(state, () => true, keep);

/** Closes every tab without unsaved changes: `dirty` names the ones with them. */
export const closeSaved = (state: EditorTabsState, dirty: KeepTab): EditorTabsState => closeWhere(state, () => true, dirty);

/** Closes every tab showing an element (after a delete). */
export function closeElement(state: EditorTabsState, id: string): EditorTabsState {
  let next = state;
  for (const t of state.tabs) if (t.id === id) next = closeTab(next, t.key);
  return next;
}

export function pinTab(state: EditorTabsState, key: string): EditorTabsState {
  const tab = state.tabs.find((t) => t.key === key);
  if (!tab || tab.pinned) return state;
  return { ...state, tabs: state.tabs.map((t) => (t === tab ? { ...t, pinned: true } : t)) };
}

/** Turns a tab back into the preview tab, which the next single click replaces; the earlier preview tab stays open,
 * pinned. The General-mode tab stays pinned. */
export function unpinTab(state: EditorTabsState, key: string): EditorTabsState {
  const tab = state.tabs.find((t) => t.key === key);
  if (!tab || !tab.pinned || tab.follow) return state;
  return { ...state, tabs: state.tabs.map((t) => (t === tab ? { ...t, pinned: false } : t.pinned ? t : { ...t, pinned: true })) };
}

/** Pins the tab showing an element (an edit made in it). */
export function pinElement(state: EditorTabsState, id: string): EditorTabsState {
  const tab = state.tabs.find((t) => t.id === id && !t.pinned);
  return tab ? pinTab(state, tab.key) : state;
}

export function activate(state: EditorTabsState, key: string | null): EditorTabsState {
  if (key !== null && !state.tabs.some((t) => t.key === key)) return state;
  return state.active === key ? state : { ...state, active: key };
}

/** Turns General mode on or off for a tab; on pins it and turns it off on every other tab. */
export function setFollow(state: EditorTabsState, key: string, on: boolean): EditorTabsState {
  if (!state.tabs.some((t) => t.key === key)) return state;
  const tabs = state.tabs.map((t) => (t.key === key ? { ...t, follow: on, pinned: t.pinned || on } : on && t.follow ? { ...t, follow: false } : t));
  return { ...state, tabs };
}

/** The selection moved to an element: the General-mode tab shows it, keeping the sub-tab of its kind. */
export function followSelection(state: EditorTabsState, target: EditorTarget): EditorTabsState {
  const tab = state.tabs.find((t) => t.follow);
  if (!tab || tab.id === target.id) return state;
  return { ...state, tabs: state.tabs.map((t) => (t === tab ? { ...t, id: target.id, kind: target.kind } : t)) };
}

export function setView(state: EditorTabsState, kind: EditorKind, view: string): EditorTabsState {
  return state.view[kind] === view ? state : { ...state, view: { ...state.view, [kind]: view } };
}
