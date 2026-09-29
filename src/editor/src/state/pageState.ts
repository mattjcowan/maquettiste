// The page state, per project per browser (the owner's 0.2.0 test pass): the active explorer, each explorer's expanded
// rows and selection, the open editor tabs and the active one, Generate's chosen packs and the last Settings tab. It is
// kept in localStorage under `mq.page.<project key>` (`projectKey` of GET /api/project, a hash of the checkout's root, so
// two projects with one name stay apart and a rename keeps it; the name when an older host sends none) and restored on load, before the shell draws, on top of the
// address, which still carries the screen, the diagram or database, the Settings tab and the active selection.
import { local } from "@/lib/storage";
import type { ExplorerId } from "@/explorer/tree";
import { EDITOR_KINDS, type EditorKind, type EditorTab, type EditorTabsState } from "@/editors/tabs";
import { LEGACY_EXPLORER_KEYS, SIDEBAR_VIEWS, type EditorState, type EditorStore, type SidebarView, type Workspace } from "./store";

export interface PageState {
  explorer: SidebarView;
  expanded: Partial<Record<ExplorerId, string[]>>;
  selectionBy: Partial<Record<SidebarView, string[]>>;
  editors: EditorTabsState;
  chosenPacks: string[] | null;
  settingsTab: string | null;
}

const EXPLORER_IDS: readonly ExplorerId[] = ["domain-model", "reference-data", "databases", "diagrams"];
const EXPANDED_LIMIT = 2000;
const TAB_LIMIT = 50;

export const pageKey = (project: string): string => `mq.page.${project}`;

/** The key page state is kept under for a project: its `projectKey`, else its name (an older host). */
export const projectPageId = (project: { name: string; projectKey?: string | null }): string => project.projectKey || project.name;

const strings = (value: unknown, limit = EXPANDED_LIMIT): string[] =>
  Array.isArray(value) ? value.filter((x): x is string => typeof x === "string").slice(0, limit) : [];
const record = (value: unknown): Record<string, unknown> =>
  value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : {};

/** The page state of the editor state (the part this file keeps). */
export function capturePage(s: Pick<EditorState, "explorer" | "selectionBy" | "editors" | "generation" | "settingsTab">): PageState {
  const expanded: PageState["expanded"] = {};
  for (const id of EXPLORER_IDS) {
    const keys = [...s.explorer.views[id].expanded].slice(0, EXPANDED_LIMIT);
    if (keys.length) expanded[id] = keys;
  }
  const selectionBy: PageState["selectionBy"] = {};
  for (const view of SIDEBAR_VIEWS) if (s.selectionBy[view]?.length) selectionBy[view] = s.selectionBy[view];
  return {
    explorer: s.explorer.active,
    expanded,
    selectionBy,
    editors: s.editors,
    chosenPacks: s.generation.chosenPacks,
    settingsTab: s.settingsTab,
  };
}

function readTabs(value: unknown): EditorTabsState {
  const raw = record(value);
  const tabs: EditorTab[] = [];
  for (const t of Array.isArray(raw.tabs) ? raw.tabs.slice(0, TAB_LIMIT) : []) {
    const tab = record(t);
    if (typeof tab.key !== "string" || typeof tab.id !== "string" || !(EDITOR_KINDS as readonly string[]).includes(String(tab.kind))) continue;
    if (tabs.some((x) => x.key === tab.key)) continue;
    tabs.push({ key: tab.key, id: tab.id, kind: tab.kind as EditorKind, pinned: tab.pinned === true, follow: tab.follow === true });
  }
  const active = typeof raw.active === "string" && tabs.some((t) => t.key === raw.active) ? raw.active : null;
  const view: EditorTabsState["view"] = {};
  for (const [kind, v] of Object.entries(record(raw.view)))
    if ((EDITOR_KINDS as readonly string[]).includes(kind) && typeof v === "string") view[kind as EditorKind] = v;
  const next = typeof raw.next === "number" && Number.isInteger(raw.next) && raw.next > 0 ? raw.next : 1;
  return { tabs, active, view, next };
}

/** The saved page state of a project, each part checked, or null when there is none. `formerKey` (the project's name) is
 * read once when `project` has none yet (a state saved before the host sent `projectKey`) and moved under `project`. */
export function readPage(project: string, formerKey?: string): PageState | null {
  let saved = local.getJson<unknown>(pageKey(project));
  if (!saved && formerKey && formerKey !== project) {
    saved = local.getJson<unknown>(pageKey(formerKey));
    if (saved) {
      local.setJson(pageKey(project), saved);
      local.remove(pageKey(formerKey));
    }
  }
  if (!saved || typeof saved !== "object") return null;
  const raw = record(saved);
  const expanded: PageState["expanded"] = {};
  for (const [id, keys] of Object.entries(record(raw.expanded)))
    if ((EXPLORER_IDS as readonly string[]).includes(id)) expanded[id as ExplorerId] = strings(keys);
  const selectionBy: PageState["selectionBy"] = {};
  for (const [view, ids] of Object.entries(record(raw.selectionBy)))
    if ((SIDEBAR_VIEWS as readonly string[]).includes(view)) selectionBy[view as SidebarView] = strings(ids, 200);
  return {
    explorer: (SIDEBAR_VIEWS as readonly unknown[]).includes(raw.explorer) ? (raw.explorer as SidebarView) : "domain-model",
    expanded,
    selectionBy,
    editors: readTabs(raw.editors),
    chosenPacks: Array.isArray(raw.chosenPacks) ? strings(raw.chosenPacks, 200) : null,
    settingsTab: typeof raw.settingsTab === "string" && raw.settingsTab ? raw.settingsTab : null,
  };
}

export function writePage(project: string, page: PageState): void {
  local.setJson(pageKey(project), page);
  for (const key of LEGACY_EXPLORER_KEYS) local.remove(key);
}

/** Forgets a project's page state (palette: Reset layout and page state): the entry goes, and the explorer, the
 * expansions, the selections, the editor tabs, the pack choice and the Settings tab go back to their defaults. */
export function forgetPage(store: EditorStore, project: string | null): void {
  if (project) local.remove(pageKey(project));
  const s = store.getState();
  const views = { ...s.explorer.views };
  for (const id of EXPLORER_IDS) views[id] = { ...views[id], expanded: new Set<string>(), scroll: 0 };
  store.setState({
    explorer: { ...s.explorer, active: "domain-model", pinned: null, views, version: s.explorer.version + 1 },
    selectionBy: {},
    editors: { tabs: [], active: null, view: {}, next: 1 },
    generation: { ...s.generation, chosenPacks: null },
    settingsTab: null,
  });
}

/** Puts a saved page state into the store, before the shell draws. The address wins where it speaks: the screen comes
 * from it (so the restored active tab is not put behind a screen change), and the active explorer's selection is the
 * address's `sel`. */
export function restorePage(store: EditorStore, page: PageState, workspace: Workspace): void {
  const s = store.getState();
  const views = { ...s.explorer.views };
  for (const id of EXPLORER_IDS) {
    const keys = page.expanded[id];
    if (keys) views[id] = { ...views[id], expanded: new Set(keys) };
  }
  const pinned = s.explorer.pinned === page.explorer ? null : s.explorer.pinned;
  const selectionBy = { ...page.selectionBy };
  delete selectionBy[page.explorer];
  store.setState({
    workspace,
    explorer: { ...s.explorer, active: page.explorer, pinned, views, version: s.explorer.version + 1 },
    selectionBy,
    editors: page.editors,
    generation: { ...s.generation, chosenPacks: page.chosenPacks },
    settingsTab: page.settingsTab,
  });
}

let changed: (() => void) | null = null;

/** Announces a change the store does not see (the tree expands rows in place): the watcher saves after its delay. */
export function pageChanged(): void {
  changed?.();
}

/** Saves the page state `delay` ms after the last change to it, and at once when the page is hidden or unloads (a
 * reload). Returns the unsubscribe. */
export function watchPage(store: EditorStore, project: string, delay = 300): () => void {
  let timer: ReturnType<typeof setTimeout> | null = null;
  const flush = () => {
    if (timer) clearTimeout(timer);
    timer = null;
    writePage(project, capturePage(store.getState()));
  };
  const schedule = () => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(flush, delay);
  };
  changed = schedule;
  const unsubscribe = store.subscribe((state, previous) => {
    if (
      state.explorer.active === previous.explorer.active &&
      state.explorer.version === previous.explorer.version &&
      state.selectionBy === previous.selectionBy &&
      state.editors === previous.editors &&
      state.generation.chosenPacks === previous.generation.chosenPacks &&
      state.settingsTab === previous.settingsTab
    )
      return;
    schedule();
  });
  window.addEventListener("pagehide", flush);
  return () => {
    if (timer) flush();
    if (changed === schedule) changed = null;
    window.removeEventListener("pagehide", flush);
    unsubscribe();
  };
}
