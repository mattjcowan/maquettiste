// Panel toggles (the owner's 0.2.0 test pass): each panel has a header button that hides it, a palette command and a
// shortcut (layout.ts PANEL_KEYS); a hidden panel leaves a slim edge that brings it back. Escape never closes a panel.
import type { ReactNode } from "react";
import {
  ChevronDown,
  ChevronsLeft,
  ChevronsRight,
  PanelBottomClose,
  PanelBottomOpen,
  PanelLeftClose,
  PanelLeftOpen,
  PanelRightClose,
  PanelRightOpen,
  PanelTopClose,
  type LucideIcon,
} from "lucide-react";
import { Button, iconLabel } from "@/components/ui/button";
import { cn } from "@/lib/cn";
import { PANEL_KEYS, PANEL_NAMES, SCREEN_PANELS, type Panel } from "@/state/layout";
import { useEditor } from "@/state/store";
import { useServices } from "./context";

const ICONS: Record<Panel, { hide: LucideIcon; show: LucideIcon }> = {
  explorer: { hide: PanelLeftClose, show: PanelLeftOpen },
  inspector: { hide: PanelRightClose, show: PanelRightOpen },
  bottom: { hide: PanelBottomClose, show: PanelBottomOpen },
  tabs: { hide: PanelTopClose, show: ChevronDown },
  topbar: { hide: ChevronsRight, show: ChevronsLeft },
  tables: { hide: PanelLeftClose, show: PanelLeftOpen },
  ddl: { hide: PanelRightClose, show: PanelRightOpen },
};

/** The screen panels (the Database screen's) are named without an article: "Hide tables list". */
export const panelLabel = (panel: Panel, collapsed: boolean): string =>
  `${collapsed ? "Show" : "Hide"} ${SCREEN_PANELS[panel] ? "" : "the "}${PANEL_NAMES[panel]}`;

/** The header button that hides a panel (or, for the top bar, shows its controls again). */
export function PanelToggle({ panel, className }: { panel: Panel; className?: string }) {
  const { store } = useServices();
  const collapsed = useEditor(store, (s) => s[`${panel}Collapsed`]);
  const Icon = collapsed ? ICONS[panel].show : ICONS[panel].hide;
  const label = panelLabel(panel, collapsed);
  return (
    <Button
      variant="ghost"
      size="icon"
      className={cn("size-5 shrink-0", className)}
      label={label}
      shortcut={PANEL_KEYS[panel].label}
      aria-expanded={!collapsed}
      data-testid={`${collapsed ? "show" : "hide"}-${panel}`}
      onClick={() => store.getState().toggle(panel)}
    >
      <Icon />
    </Button>
  );
}

/** The slim edge a hidden panel leaves: a thin strip along the side it went to; a click brings the panel back. */
export function EdgeToggle({ panel, side, children }: { panel: Panel; side: "left" | "right" | "top"; children?: ReactNode }) {
  const { store } = useServices();
  const Icon = ICONS[panel].show;
  const label = panelLabel(panel, true);
  const vertical = side !== "top";
  return (
    <button
      type="button"
      {...iconLabel(label, PANEL_KEYS[panel].label)}
      aria-expanded={false}
      data-testid={`show-${panel}`}
      onClick={() => store.getState().toggle(panel, false)}
      className={cn(
        "group flex shrink-0 items-center justify-center bg-surface text-secondary hover:bg-accent-subtle hover:text-primary",
        vertical ? "w-3 flex-col border-default" : "h-3 w-full gap-1 border-b border-default text-11",
        side === "left" && "border-r",
        side === "right" && "border-l",
      )}
    >
      <Icon className={vertical ? "size-3" : "size-2.5"} aria-hidden />
      {children}
    </button>
  );
}

/** The inspector's header row (the explorer's has its own): the panel name and its hide button. */
export function PanelHeader({ panel, title }: { panel: Panel; title: string }) {
  return (
    <div className="flex h-6 shrink-0 items-center gap-2 border-b border-default px-2" data-testid={`${panel}-panel-header`}>
      <span className="min-w-0 flex-1 truncate text-11 font-semibold uppercase tracking-wide text-secondary">{title}</span>
      <PanelToggle panel={panel} />
    </div>
  );
}
