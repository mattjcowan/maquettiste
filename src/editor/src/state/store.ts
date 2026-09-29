// Editor state (PD3): everything that is not server state. Server state lives only in TanStack
// Query. A vanilla store, so the draft and undo managers can use it outside React.
import { createStore, type StoreApi } from "zustand/vanilla";
import { useStore } from "zustand";
import type { Diagnostic, ElementDocument, ModelJson, PresenceEntry } from "@/api/types";
import { local } from "@/lib/storage";
import type { ExplorerId } from "@/explorer/tree";
import { emptyHistory, travel, visit, type NavHistory } from "./history";
import { activate, activeTab, emptyEditorTabs, pinElement, type EditorTabsState } from "@/editors/tabs";
import { emptyFilter, normalizeFilter, scopeOf, type ExplorerFilter, type FilterScope } from "@/explorer/filter";
import type { CreateKind } from "@/explorer/create";

export type PaletteCommand = "plan" | "apply" | "new-entity";

export type Workspace = "entities" | "reference-data" | "database" | "mappings" | "generate" | "settings";
export const WORKSPACES: Workspace[] = ["entities", "reference-data", "database", "mappings", "generate", "settings"];
export type ThemeChoice = "system" | "light" | "dark";
export type Density = "compact" | "comfortable";
export type BottomTab = "problems" | "output" | "diff" | "references";

export interface Draft {
  id: string;
  /** "element" saves with PUT /api/model/elements/{id}; "diagram" with PUT /api/diagrams/{id}. */
  channel: "element" | "diagram";
  /** The hash the next save sends in If-Match. */
  baseHash: string;
  /** The document at baseHash: what undo restores. */
  baseJson: ModelJson;
  /** What the editor shows and the next save sends. */
  json: ModelJson;
  status: "dirty" | "saving" | "invalid" | "conflict";
  diagnostics: Diagnostic[];
  /** The disk version after a 409. */
  conflict: ElementDocument | null;
  error: string | null;
}

export interface UndoEntry {
  label: string;
  ids: string[];
  /** Documents before the change (null: the element did not exist). */
  before: (ModelJson | null)[];
  /** Documents after the change (null: the element was deleted). */
  after: (ModelJson | null)[];
  /** Hashes after the change: the expected hashes of the batch that undoes it. */
  afterHashes: (string | null)[];
}

export interface OutputEntry {
  id: number;
  time: string;
  level: "info" | "success" | "warning" | "error";
  text: string;
  jobId?: string;
}

export interface Banner {
  kind: "deployed" | "signed-out" | "realtime";
  text: string;
}

export interface DiffView {
  planId: string;
  path: string;
}

export interface GenerationState {
  planJob: string | null;
  planId: string | null;
  applyJob: string | null;
}

/** What the sidebar shows: one explorer (explorer-redesign.md 1.0), or Generate's packs and targets. */
export type SidebarView = ExplorerId | "generate";
export const SIDEBAR_VIEWS: SidebarView[] = ["domain-model", "reference-data", "databases", "diagrams", "generate"];

/** One explorer's own view state: kept while another explorer shows (1.0), keyed by row key (4.3). */
export interface ExplorerView {
  /** Expanded row keys; mutated in place by the tree (splices), announced with `touchExplorer`. */
  expanded: Set<string>;
  filter: ExplorerFilter;
  /** The pinned filter (3.2): kept across reloads in localStorage until it is unpinned. */
  pinnedFilter: boolean;
  scroll: number;
}

export interface ExplorerSlice {
  active: SidebarView;
  /** The second explorer pinned beside the first, a per-user preference, off by default (1.0). */
  pinned: ExplorerId | null;
  followSelection: boolean;
  highlightRelated: boolean;
  /** The Reference data explorer lists its types A to Z instead of by category (RT 4.2), a per-user preference. */
  referenceFlat: boolean;
  views: Record<ExplorerId, ExplorerView>;
  /** Bumped when a view's expansion or filter changes. */
  version: number;
  /** The user's own scopes (3.2), in localStorage; the team's live in maquettiste.json `explorer.scopes`. */
  scopes: FilterScope[];
  /** Starred elements (3.3), per user, in localStorage. */
  favorites: string[];
}

export interface EditorState {
  workspace: Workspace;
  explorer: ExplorerSlice;
  selection: string[];
  /** Problem navigation: the element and JSON pointer to reveal in the inspector. */
  focus: { id: string; pointer: string | null } | null;
  activeDiagram: string | null;
  activeDatabase: string | null;
  /** A selected explorer row that is not an element: a synthesized table or a column (explorer-redesign.md 1.3), by
   * tree key. Its "mapped by" rows are highlighted; selecting elements clears it. */
  explorerItem: string | null;
  /** The table the Database screen shows focused (opened from the explorer or its table list), by table key. */
  databaseTable: string | null;
  explorerSize: number;
  inspectorSize: number;
  bottomSize: number;
  explorerCollapsed: boolean;
  inspectorCollapsed: boolean;
  bottomCollapsed: boolean;
  bottomTab: BottomTab;
  theme: ThemeChoice;
  density: Density;
  paletteOpen: boolean;
  /** Quick open (Ctrl/Cmd+P, explorer-redesign.md 3.1): the ranked search without the commands. */
  quickOpen: boolean;
  /** Elements opened (not merely selected) from the explorer, quick open, the palette or a go-to, most recent first (a ranking tie-breaker and the
   * explorer's Recent strip, 3.3); the first 20 are kept per user in localStorage. */
  recent: string[];
  /** A command the palette asked a workspace to run once it is showing (4.8). */
  command: { name: PaletteCommand; nonce: number } | null;
  /** The open New element dialog (explorer menus, the explorer's New button, the first-run panel, the palette): the kind
   * and the domain its picker starts on. */
  newElement: { kind: CreateKind; domain: string | null } | null;
  /** Back and forward through selections (explorer-redesign.md 3.3). */
  history: NavHistory;
  /** The element the References tab lists the uses of (Where used, Shift+F12; 3.3). */
  references: string | null;
  /** A request to centre the canvas on an element once it is a card there (Show on canvas, go to definition, 3.5). */
  centerRequest: { id: string; nonce: number } | null;
  /** Element editor tabs in the centre area (explorer-redesign.md 3.6). */
  editors: EditorTabsState;
  drafts: Record<string, Draft>;
  undo: UndoEntry[];
  redo: UndoEntry[];
  presence: PresenceEntry[];
  banner: Banner | null;
  notice: { id: number; text: string; level: "info" | "error" } | null;
  output: OutputEntry[];
  diff: DiffView | null;
  connection: string;
  generation: GenerationState;
}

export interface EditorActions {
  setWorkspace(workspace: Workspace): void;
  setSidebar(view: SidebarView): void;
  pinExplorer(id: ExplorerId | null): void;
  setHighlightRelated(on: boolean): void;
  setReferenceFlat(on: boolean): void;
  setExplorerFilter(id: ExplorerId, filter: ExplorerFilter): void;
  /** Pins or unpins an explorer's filter (3.2). */
  pinExplorerFilter(id: ExplorerId, pinned: boolean): void;
  /** Saves the filter's chips as a personal scope under a name (replacing one of that name), or removes it (null). */
  saveScope(name: string, filter: ExplorerFilter | null): void;
  toggleFavorite(id: string): void;
  /** Announces an in-place change to a view's expansion. */
  touchExplorer(): void;
  select(ids: string[], focus?: { pointer: string | null }): void;
  setExplorerItem(key: string | null): void;
  setDatabaseTable(key: string | null): void;
  setActiveDiagram(id: string | null): void;
  setActiveDatabase(id: string | null): void;
  setBottomTab(tab: BottomTab): void;
  toggle(panel: "explorer" | "inspector" | "bottom", collapsed?: boolean): void;
  setTheme(theme: ThemeChoice): void;
  setDensity(density: Density): void;
  setPaletteOpen(open: boolean): void;
  setQuickOpen(open: boolean): void;
  noteRecent(id: string): void;
  requestCommand(name: PaletteCommand | null): void;
  /** Opens the New element dialog for a kind, its domain picker on `domain`; null closes it. */
  requestNew(request: { kind: CreateKind; domain: string | null } | null): void;
  /** Moves back or forward through the selection history; returns the selection it moved to, or null. */
  travel(direction: "back" | "forward"): string[] | null;
  /** Opens the References tab on an element. */
  showReferences(id: string | null): void;
  requestCenter(id: string): void;
  updateEditors(update: (state: EditorTabsState) => EditorTabsState): void;
  setDraft(draft: Draft): void;
  patchDraft(id: string, patch: Partial<Draft>): void;
  removeDraft(id: string): void;
  pushUndo(entry: UndoEntry): void;
  setStacks(undo: UndoEntry[], redo: UndoEntry[]): void;
  setPresence(entries: PresenceEntry[]): void;
  setBanner(banner: Banner | null): void;
  notify(text: string, level?: "info" | "error"): void;
  log(level: OutputEntry["level"], text: string, jobId?: string): void;
  showDiff(diff: DiffView | null): void;
  setConnection(state: string): void;
  setGeneration(patch: Partial<GenerationState>): void;
}

export type EditorStore = StoreApi<EditorState & EditorActions>;

const UNDO_LIMIT = 200;
let outputId = 0;
let noticeId = 0;

function initialTheme(): ThemeChoice {
  const saved = local.get("mq.theme");
  return saved === "light" || saved === "dark" || saved === "system" ? saved : "system";
}

const EXPLORER_IDS: ExplorerId[] = ["domain-model", "reference-data", "databases", "diagrams"];

/** An explorer's expanded rows survive a reload (explorer-redesign.md 3.3), per browser, the first 2,000 keys. */
const EXPANDED_LIMIT = 2000;
const expandedKey = (id: ExplorerId) => `mq.explorer.expanded.${id}`;
export function saveExpanded(id: ExplorerId, expanded: ReadonlySet<string>): void {
  local.setJson(expandedKey(id), [...expanded].slice(0, EXPANDED_LIMIT));
}

function initialExplorer(): ExplorerSlice {
  const active = local.get("mq.explorer.active");
  const pinned = local.get("mq.explorer.pinned");
  const view = (id: ExplorerId): ExplorerView => {
    const pinnedFilter = local.getJson<unknown>(`mq.explorer.filter.${id}`);
    const expanded = local.getJson<unknown[]>(expandedKey(id));
    return {
      expanded: new Set(Array.isArray(expanded) ? expanded.filter((k): k is string => typeof k === "string").slice(0, EXPANDED_LIMIT) : []),
      filter: pinnedFilter ? normalizeFilter(pinnedFilter) : emptyFilter, pinnedFilter: !!pinnedFilter, scroll: 0 };
  };
  const scopes = local.getJson<unknown[]>("mq.explorer.scopes");
  const favorites = local.getJson<unknown[]>("mq.favorites");
  return {
    active: (SIDEBAR_VIEWS as string[]).includes(active ?? "") ? (active as SidebarView) : "domain-model",
    pinned: (EXPLORER_IDS as string[]).includes(pinned ?? "") ? (pinned as ExplorerId) : null,
    followSelection: true,
    // On by default (1.9): only an explicit "0" turns it off.
    highlightRelated: local.get("mq.explorer.highlight") !== "0",
    referenceFlat: local.get("mq.explorer.referenceFlat") === "1",
    views: { "domain-model": view("domain-model"), "reference-data": view("reference-data"), databases: view("databases"), diagrams: view("diagrams") },
    version: 0,
    scopes: Array.isArray(scopes)
      ? scopes.flatMap((x) => {
          const name = x && typeof x === "object" ? (x as { name?: unknown }).name : undefined;
          return typeof name === "string" && name ? [scopeOf(name, normalizeFilter(x))] : [];
        })
      : [],
    favorites: Array.isArray(favorites) ? favorites.filter((x): x is string => typeof x === "string") : [],
  };
}

/** The recent list with `id` first (the same array when it already is), the first 20 kept in localStorage. */
function withRecent(recent: string[], id: string): string[] {
  if (recent[0] === id) return recent;
  const next = [id, ...recent.filter((x) => x !== id)].slice(0, 50);
  local.setJson("mq.recent", next.slice(0, 20));
  return next;
}

function initialRecent(): string[] {
  const saved = local.getJson<unknown[]>("mq.recent");
  return Array.isArray(saved) ? saved.filter((x): x is string => typeof x === "string").slice(0, 20) : [];
}

function initialDensity(): Density {
  return local.get("mq.density") === "comfortable" ? "comfortable" : "compact";
}

export function createEditorStore(): EditorStore {
  const layout = local.getJson<{ explorer?: number; inspector?: number; bottom?: number }>("mq.layout") ?? {};
  return createStore<EditorState & EditorActions>()((set, get) => ({
    workspace: "entities",
    explorer: initialExplorer(),
    selection: [],
    focus: null,
    activeDiagram: null,
    activeDatabase: null,
    explorerItem: null,
    databaseTable: null,
    explorerSize: layout.explorer ?? 280,
    inspectorSize: layout.inspector ?? 360,
    bottomSize: layout.bottom ?? 220,
    explorerCollapsed: false,
    inspectorCollapsed: false,
    bottomCollapsed: false,
    bottomTab: "problems",
    theme: initialTheme(),
    density: initialDensity(),
    paletteOpen: false,
    quickOpen: false,
    recent: initialRecent(),
    command: null,
    newElement: null,
    history: emptyHistory,
    references: null,
    centerRequest: null,
    editors: emptyEditorTabs(),
    drafts: {},
    undo: [],
    redo: [],
    presence: [],
    banner: null,
    notice: null,
    output: [],
    diff: null,
    connection: "disconnected",
    generation: { planJob: null, planId: null, applyJob: null },

    // Another screen shows in the centre area: the editor tabs stay open behind it.
    setWorkspace: (workspace) => set((s) => ({ workspace, editors: s.workspace === workspace ? s.editors : activate(s.editors, null) })),
    setSidebar: (active) => {
      local.set("mq.explorer.active", active);
      const pinned = get().explorer.pinned === active ? null : get().explorer.pinned;
      set({ explorer: { ...get().explorer, active, pinned } });
    },
    pinExplorer: (pinned) => {
      local.set("mq.explorer.pinned", pinned ?? "");
      set({ explorer: { ...get().explorer, pinned } });
    },
    setHighlightRelated: (on) => {
      local.set("mq.explorer.highlight", on ? "1" : "0");
      set({ explorer: { ...get().explorer, highlightRelated: on } });
    },
    setReferenceFlat: (on) => {
      local.set("mq.explorer.referenceFlat", on ? "1" : "0");
      set({ explorer: { ...get().explorer, referenceFlat: on } });
    },
    setExplorerFilter: (id, filter) => {
      const e = get().explorer;
      if (e.views[id].pinnedFilter) local.setJson(`mq.explorer.filter.${id}`, filter);
      set({ explorer: { ...e, views: { ...e.views, [id]: { ...e.views[id], filter } }, version: e.version + 1 } });
    },
    pinExplorerFilter: (id, pinned) => {
      const e = get().explorer;
      if (pinned) local.setJson(`mq.explorer.filter.${id}`, e.views[id].filter);
      else local.remove(`mq.explorer.filter.${id}`);
      set({ explorer: { ...e, views: { ...e.views, [id]: { ...e.views[id], pinnedFilter: pinned } }, version: e.version + 1 } });
    },
    saveScope: (name, filter) => {
      const e = get().explorer;
      const rest = e.scopes.filter((s) => s.name !== name);
      const scopes = filter ? [...rest, scopeOf(name, filter)].sort((a, b) => a.name.localeCompare(b.name)) : rest;
      local.setJson("mq.explorer.scopes", scopes);
      set({ explorer: { ...e, scopes } });
    },
    toggleFavorite: (id) => {
      const e = get().explorer;
      const favorites = e.favorites.includes(id) ? e.favorites.filter((x) => x !== id) : [...e.favorites, id];
      local.setJson("mq.favorites", favorites);
      set({ explorer: { ...e, favorites } });
    },
    touchExplorer: () => set({ explorer: { ...get().explorer, version: get().explorer.version + 1 } }),
    select: (ids, focus) => {
      // A selection is not an open: the Recent strip (3.3) changes only when an element is opened, so a click on a
      // tree row never grows the strip and shifts the tree under the pointer between the two clicks of a double click.
      set({
        selection: ids,
        explorerItem: null,
        focus: ids.length === 1 && focus ? { id: ids[0], pointer: focus.pointer } : null,
        history: visit(get().history, ids),
      });
    },
    setActiveDiagram: (id) => set({ activeDiagram: id }),
    setExplorerItem: (key) => set({ explorerItem: key }),
    setDatabaseTable: (key) => set({ databaseTable: key }),
    setActiveDatabase: (id) => set({ activeDatabase: id }),
    setBottomTab: (tab) => set({ bottomTab: tab, bottomCollapsed: false }),
    toggle: (panel, collapsed) => {
      const key = `${panel}Collapsed` as const;
      set({ [key]: collapsed ?? !get()[key] } as Partial<EditorState>);
    },
    setTheme: (theme) => {
      local.set("mq.theme", theme);
      set({ theme });
    },
    setDensity: (density) => {
      local.set("mq.density", density);
      set({ density });
    },
    setPaletteOpen: (open) => set(open ? { paletteOpen: true, quickOpen: false } : { paletteOpen: false }),
    setQuickOpen: (open) => set(open ? { quickOpen: true, paletteOpen: false } : { quickOpen: false }),
    noteRecent: (id) => set({ recent: withRecent(get().recent, id) }),
    travel: (direction) => {
      const next = travel(get().history, direction);
      if (!next?.current) return null;
      set({ history: next, selection: next.current, focus: null });
      return next.current;
    },
    showReferences: (id) => set({ references: id, ...(id ? { bottomTab: "references" as const, bottomCollapsed: false } : {}) }),
    // A go-to (a definition, a where-used row, a breadcrumb, back and forward) opens the element: it is a recent one.
    requestCenter: (id) => set({ centerRequest: { id, nonce: (get().centerRequest?.nonce ?? 0) + 1 }, recent: withRecent(get().recent, id) }),
    requestNew: (request) => set({ newElement: request }),
    requestCommand: (name) => set({ command: name ? { name, nonce: (get().command?.nonce ?? 0) + 1 } : null }),
    updateEditors: (update) => {
      const before = get().editors;
      const next = update(before);
      if (next === before) return;
      // A pinned open (Enter, a double click, the menu's Open, a new element) is a recent one (3.3); a preview open,
      // a tab switch and the General-mode tab following the selection are not.
      const shown = activeTab(next);
      const opened = shown && shown.pinned && !shown.follow && !before.tabs.some((t) => t.key === shown.key && t.id === shown.id && t.pinned);
      set(opened ? { editors: next, recent: withRecent(get().recent, shown.id) } : { editors: next });
    },
    // An edit pins the element's preview tab (3.6).
    setDraft: (draft) => set((s) => ({ drafts: { ...s.drafts, [draft.id]: draft }, editors: pinElement(s.editors, draft.id) })),
    patchDraft: (id, patch) => {
      const current = get().drafts[id];
      if (current) set({ drafts: { ...get().drafts, [id]: { ...current, ...patch } } });
    },
    removeDraft: (id) => {
      const next = { ...get().drafts };
      delete next[id];
      set({ drafts: next });
    },
    pushUndo: (entry) => set({ undo: [...get().undo, entry].slice(-UNDO_LIMIT), redo: [] }),
    setStacks: (undo, redo) => set({ undo, redo }),
    setPresence: (entries) => set({ presence: entries }),
    setBanner: (banner) => set({ banner }),
    notify: (text, level = "info") => set({ notice: { id: ++noticeId, text, level } }),
    log: (level, text, jobId) =>
      set({
        output: [...get().output, { id: ++outputId, time: new Date().toISOString(), level, text, jobId }].slice(-500),
      }),
    showDiff: (diff) => set({ diff, ...(diff ? { bottomTab: "diff" as const, bottomCollapsed: false } : {}) }),
    setConnection: (connection) => set({ connection }),
    setGeneration: (patch) => set({ generation: { ...get().generation, ...patch } }),
  }));
}

/** Persists panel sizes per browser. */
export function saveLayout(state: Pick<EditorState, "explorerSize" | "inspectorSize" | "bottomSize">): void {
  local.setJson("mq.layout", { explorer: state.explorerSize, inspector: state.inspectorSize, bottom: state.bottomSize });
}

export function useEditor<T>(store: EditorStore, selector: (state: EditorState & EditorActions) => T): T {
  return useStore(store, selector);
}
