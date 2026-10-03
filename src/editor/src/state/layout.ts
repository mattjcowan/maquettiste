// The layout, per browser (the owner's 0.2.0 test pass): which panels are open, the explorer, inspector and bottom
// panel sizes, the Database screen's list and DDL preview widths, the pack editor's panes, and the pinned second explorer. It lives in localStorage (`mq.layout`, `mq.explorer.pinned`) and comes
// back on load; "Reset layout" clears it. The page state (what is open inside the panels) is pageState.ts, per project.
import { local } from "@/lib/storage";
import type { ExplorerId } from "@/explorer/tree";

/** Every panel that collapses and restores: the explorer sidebar, the inspector, the bottom panel, the editor tab strip,
 * the top bar's secondary controls, the Database screen's tables list and DDL preview, and the pack editor's panes
 * (the Templates tab's file list and preview, the Units tab's help). */
export type Panel = "explorer" | "inspector" | "bottom" | "tabs" | "topbar" | "tables" | "ddl" | "packFiles" | "templatePreview" | "unitHelp";
export const PANELS: readonly Panel[] = ["explorer", "inspector", "bottom", "tabs", "topbar", "tables", "ddl", "packFiles", "templatePreview", "unitHelp"];

/** The panels that belong to one screen: their shortcut acts only while that screen shows. */
export const SCREEN_PANELS: Partial<Record<Panel, "database" | "generate">> = {
  tables: "database",
  ddl: "database",
  packFiles: "generate",
  templatePreview: "generate",
  unitHelp: "generate",
};

export const LIMITS = {
  explorer: { min: 240, max: 480 },
  inspector: { min: 320, max: 560 },
  bottom: { min: 120, max: 600 },
  packFiles: { min: 120, max: 400 },
  templatePreview: { min: 240, max: 960 },
  unitHelp: { min: 160, max: 480 },
  tables: { min: 144, max: 400 },
  ddl: { min: 240, max: 800 },
} as const;

export interface Layout {
  collapsed: Record<Panel, boolean>;
  explorerSize: number;
  inspectorSize: number;
  bottomSize: number;
  /** The pack editor's resizable panes. */
  packFilesSize: number;
  templatePreviewSize: number;
  unitHelpSize: number;
  /** The Database screen's tables list and DDL preview widths. */
  tablesSize: number;
  ddlSize: number;
  pinned: ExplorerId | null;
}

/** The defaults. The Database screen opens with its DDL preview hidden (its edge brings it back) and a narrow list, so the
 * diagram has most of the screen: at 1440 px with the explorer and the inspector open, well over half of it. */
export const DEFAULT_LAYOUT: Layout = Object.freeze({
  collapsed: Object.freeze({
    explorer: false,
    inspector: false,
    bottom: false,
    tabs: false,
    topbar: false,
    tables: false,
    ddl: true,
    packFiles: false,
    templatePreview: false,
    unitHelp: false,
  }),
  explorerSize: 280,
  inspectorSize: 360,
  bottomSize: 220,
  packFilesSize: 192,
  templatePreviewSize: 360,
  unitHelpSize: 256,
  tablesSize: 176,
  ddlSize: 400,
  pinned: null,
}) as Layout;

export const LAYOUT_KEY = "mq.layout";
export const PINNED_KEY = "mq.explorer.pinned";
const EXPLORER_IDS: readonly string[] = ["domain-model", "processes", "reference-data", "databases", "diagrams"];

const size = (value: unknown, limits: { min: number; max: number }, fallback: number): number =>
  typeof value === "number" && Number.isFinite(value) ? Math.min(limits.max, Math.max(limits.min, Math.round(value))) : fallback;

/** The saved layout, each part checked (a bad or missing value falls back to the default). */
export function readLayout(): Layout {
  const saved = local.getJson<{
    explorer?: unknown;
    inspector?: unknown;
    bottom?: unknown;
    packFiles?: unknown;
    templatePreview?: unknown;
    unitHelp?: unknown;
    tables?: unknown;
    ddl?: unknown;
    collapsed?: unknown;
  }>(LAYOUT_KEY);
  const raw = saved && typeof saved === "object" ? saved : {};
  // A saved list says every panel's state; without one (nothing saved, or the sizes-only format of 0.2.0) the defaults apply.
  const collapsedList = Array.isArray(raw.collapsed) ? (raw.collapsed as unknown[]) : null;
  const collapsed = (collapsedList ? Object.fromEntries(PANELS.map((p) => [p, collapsedList.includes(p)])) : { ...DEFAULT_LAYOUT.collapsed }) as Record<
    Panel,
    boolean
  >;
  const pinned = local.get(PINNED_KEY);
  return {
    collapsed,
    explorerSize: size(raw.explorer, LIMITS.explorer, DEFAULT_LAYOUT.explorerSize),
    inspectorSize: size(raw.inspector, LIMITS.inspector, DEFAULT_LAYOUT.inspectorSize),
    bottomSize: size(raw.bottom, LIMITS.bottom, DEFAULT_LAYOUT.bottomSize),
    packFilesSize: size(raw.packFiles, LIMITS.packFiles, DEFAULT_LAYOUT.packFilesSize),
    templatePreviewSize: size(raw.templatePreview, LIMITS.templatePreview, DEFAULT_LAYOUT.templatePreviewSize),
    unitHelpSize: size(raw.unitHelp, LIMITS.unitHelp, DEFAULT_LAYOUT.unitHelpSize),
    tablesSize: size(raw.tables, LIMITS.tables, DEFAULT_LAYOUT.tablesSize),
    ddlSize: size(raw.ddl, LIMITS.ddl, DEFAULT_LAYOUT.ddlSize),
    pinned: pinned && EXPLORER_IDS.includes(pinned) ? (pinned as ExplorerId) : null,
  };
}

/** Writes the layout: sizes and the collapsed panels in `mq.layout` (the panel order is fixed), the pinned explorer apart. */
export function writeLayout(layout: Layout): void {
  local.setJson(LAYOUT_KEY, {
    explorer: layout.explorerSize,
    inspector: layout.inspectorSize,
    bottom: layout.bottomSize,
    packFiles: layout.packFilesSize,
    templatePreview: layout.templatePreviewSize,
    unitHelp: layout.unitHelpSize,
    tables: layout.tablesSize,
    ddl: layout.ddlSize,
    collapsed: PANELS.filter((p) => layout.collapsed[p]),
  });
  local.set(PINNED_KEY, layout.pinned ?? "");
}

export function clearLayout(): void {
  local.remove(LAYOUT_KEY);
  local.remove(PINNED_KEY);
}

/** The keyboard shortcut per panel: Alt+Shift with a letter, by physical key (Option+Shift types a symbol on a Mac).
 * Chosen clear of the browsers' own (Alt+Shift+I, Alt+Shift+T and Alt+Shift+A in Chrome) and the editor's (Alt+M and
 * Alt+R in the type picker, Alt+1 to Alt+4 in the pack editor, Alt+arrows). The tables list takes L (for list) because
 * Chrome keeps Alt+Shift+T for its toolbar; the pack editor's panes take F (files), V (view: the preview) and U (unit
 * help). */
export const PANEL_KEYS: Record<Panel, { code: string; label: string }> = {
  explorer: { code: "KeyE", label: "Alt+Shift+E" },
  inspector: { code: "KeyP", label: "Alt+Shift+P" },
  bottom: { code: "KeyJ", label: "Alt+Shift+J" },
  tabs: { code: "KeyO", label: "Alt+Shift+O" },
  topbar: { code: "KeyH", label: "Alt+Shift+H" },
  tables: { code: "KeyL", label: "Alt+Shift+L" },
  ddl: { code: "KeyD", label: "Alt+Shift+D" },
  packFiles: { code: "KeyF", label: "Alt+Shift+F" },
  templatePreview: { code: "KeyV", label: "Alt+Shift+V" },
  unitHelp: { code: "KeyU", label: "Alt+Shift+U" },
};

export const PANEL_NAMES: Record<Panel, string> = {
  explorer: "explorer",
  inspector: "inspector",
  bottom: "bottom panel",
  tabs: "editor tabs",
  topbar: "top bar controls",
  tables: "tables list",
  ddl: "DDL preview",
  packFiles: "pack files",
  templatePreview: "template preview",
  unitHelp: "unit help",
};

/** The panel a key event toggles, or null. */
export function panelForKey(e: { code: string; altKey: boolean; shiftKey: boolean; ctrlKey: boolean; metaKey: boolean }): Panel | null {
  if (!e.altKey || !e.shiftKey || e.ctrlKey || e.metaKey) return null;
  return PANELS.find((p) => PANEL_KEYS[p].code === e.code) ?? null;
}
