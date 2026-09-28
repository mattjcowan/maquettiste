// Editor state (PD3): everything that is not server state. Server state lives only in TanStack
// Query. A vanilla store, so the draft and undo managers can use it outside React.
import { createStore, type StoreApi } from "zustand/vanilla";
import { useStore } from "zustand";
import type { Diagnostic, ElementDocument, ModelJson, PresenceEntry } from "@/api/types";
import { local } from "@/lib/storage";

export type PaletteCommand = "plan" | "apply" | "new-entity";

export type Workspace = "entities" | "database" | "mappings" | "generate" | "settings";
export const WORKSPACES: Workspace[] = ["entities", "database", "mappings", "generate", "settings"];
export type ThemeChoice = "system" | "light" | "dark";
export type Density = "compact" | "comfortable";
export type BottomTab = "problems" | "output" | "diff";

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

export interface EditorState {
  workspace: Workspace;
  selection: string[];
  /** Problem navigation: the element and JSON pointer to reveal in the inspector. */
  focus: { id: string; pointer: string | null } | null;
  activeDiagram: string | null;
  activeDatabase: string | null;
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
  /** A command the palette asked a workspace to run once it is showing (4.8). */
  command: { name: PaletteCommand; nonce: number } | null;
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
  select(ids: string[], focus?: { pointer: string | null }): void;
  setActiveDiagram(id: string | null): void;
  setActiveDatabase(id: string | null): void;
  setBottomTab(tab: BottomTab): void;
  toggle(panel: "explorer" | "inspector" | "bottom", collapsed?: boolean): void;
  setTheme(theme: ThemeChoice): void;
  setDensity(density: Density): void;
  setPaletteOpen(open: boolean): void;
  requestCommand(name: PaletteCommand | null): void;
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

function initialDensity(): Density {
  return local.get("mq.density") === "comfortable" ? "comfortable" : "compact";
}

export function createEditorStore(): EditorStore {
  const layout = local.getJson<{ explorer?: number; inspector?: number; bottom?: number }>("mq.layout") ?? {};
  return createStore<EditorState & EditorActions>()((set, get) => ({
    workspace: "entities",
    selection: [],
    focus: null,
    activeDiagram: null,
    activeDatabase: null,
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
    command: null,
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

    setWorkspace: (workspace) => set({ workspace }),
    select: (ids, focus) => set({ selection: ids, focus: ids.length === 1 && focus ? { id: ids[0], pointer: focus.pointer } : null }),
    setActiveDiagram: (id) => set({ activeDiagram: id }),
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
    setPaletteOpen: (open) => set({ paletteOpen: open }),
    requestCommand: (name) => set({ command: name ? { name, nonce: (get().command?.nonce ?? 0) + 1 } : null }),
    setDraft: (draft) => set({ drafts: { ...get().drafts, [draft.id]: draft } }),
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
