// The layout, per browser (the owner's 0.2.0 test pass): which panels are open, the explorer, inspector and bottom
// panel sizes, and the pinned second explorer. It lives in localStorage (`mq.layout`, `mq.explorer.pinned`) and comes
// back on load; "Reset layout" clears it. The page state (what is open inside the panels) is pageState.ts, per project.
import { local } from "@/lib/storage";
import type { ExplorerId } from "@/explorer/tree";

/** Every panel that collapses and restores: the explorer sidebar, the inspector, the bottom panel, the editor tab strip,
 * the top bar's secondary controls, and the Database screen's tables list and DDL preview. */
export type Panel = "explorer" | "inspector" | "bottom" | "tabs" | "topbar" | "tables" | "ddl";
export const PANELS: readonly Panel[] = ["explorer", "inspector", "bottom", "tabs", "topbar", "tables", "ddl"];

/** The panels that belong to one screen: their shortcut acts only while that screen shows. */
export const SCREEN_PANELS: Partial<Record<Panel, "database">> = { tables: "database", ddl: "database" };

export const LIMITS = {
  explorer: { min: 240, max: 480 },
  inspector: { min: 320, max: 560 },
  bottom: { min: 120, max: 600 },
} as const;

export interface Layout {
  collapsed: Record<Panel, boolean>;
  explorerSize: number;
  inspectorSize: number;
  bottomSize: number;
  pinned: ExplorerId | null;
}

export const DEFAULT_LAYOUT: Layout = Object.freeze({
  collapsed: Object.freeze({ explorer: false, inspector: false, bottom: false, tabs: false, topbar: false, tables: false, ddl: false }),
  explorerSize: 280,
  inspectorSize: 360,
  bottomSize: 220,
  pinned: null,
}) as Layout;

export const LAYOUT_KEY = "mq.layout";
export const PINNED_KEY = "mq.explorer.pinned";
const EXPLORER_IDS: readonly string[] = ["domain-model", "reference-data", "databases", "diagrams"];

const size = (value: unknown, limits: { min: number; max: number }, fallback: number): number =>
  typeof value === "number" && Number.isFinite(value) ? Math.min(limits.max, Math.max(limits.min, Math.round(value))) : fallback;

/** The saved layout, each part checked (a bad or missing value falls back to the default). */
export function readLayout(): Layout {
  const saved = local.getJson<{ explorer?: unknown; inspector?: unknown; bottom?: unknown; collapsed?: unknown }>(LAYOUT_KEY);
  const raw = saved && typeof saved === "object" ? saved : {};
  const collapsedList = Array.isArray(raw.collapsed) ? raw.collapsed : [];
  const collapsed = Object.fromEntries(PANELS.map((p) => [p, collapsedList.includes(p)])) as Record<Panel, boolean>;
  const pinned = local.get(PINNED_KEY);
  return {
    collapsed,
    explorerSize: size(raw.explorer, LIMITS.explorer, DEFAULT_LAYOUT.explorerSize),
    inspectorSize: size(raw.inspector, LIMITS.inspector, DEFAULT_LAYOUT.inspectorSize),
    bottomSize: size(raw.bottom, LIMITS.bottom, DEFAULT_LAYOUT.bottomSize),
    pinned: pinned && EXPLORER_IDS.includes(pinned) ? (pinned as ExplorerId) : null,
  };
}

/** Writes the layout: sizes and the collapsed panels in `mq.layout` (the panel order is fixed), the pinned explorer apart. */
export function writeLayout(layout: Layout): void {
  local.setJson(LAYOUT_KEY, {
    explorer: layout.explorerSize,
    inspector: layout.inspectorSize,
    bottom: layout.bottomSize,
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
 * Chrome keeps Alt+Shift+T for its toolbar. */
export const PANEL_KEYS: Record<Panel, { code: string; label: string }> = {
  explorer: { code: "KeyE", label: "Alt+Shift+E" },
  inspector: { code: "KeyP", label: "Alt+Shift+P" },
  bottom: { code: "KeyJ", label: "Alt+Shift+J" },
  tabs: { code: "KeyO", label: "Alt+Shift+O" },
  topbar: { code: "KeyH", label: "Alt+Shift+H" },
  tables: { code: "KeyL", label: "Alt+Shift+L" },
  ddl: { code: "KeyD", label: "Alt+Shift+D" },
};

export const PANEL_NAMES: Record<Panel, string> = {
  explorer: "explorer",
  inspector: "inspector",
  bottom: "bottom panel",
  tabs: "editor tabs",
  topbar: "top bar controls",
  tables: "tables list",
  ddl: "DDL preview",
};

/** The panel a key event toggles, or null. */
export function panelForKey(e: { code: string; altKey: boolean; shiftKey: boolean; ctrlKey: boolean; metaKey: boolean }): Panel | null {
  if (!e.altKey || !e.shiftKey || e.ctrlKey || e.metaKey) return null;
  return PANELS.find((p) => PANEL_KEYS[p].code === e.code) ?? null;
}
