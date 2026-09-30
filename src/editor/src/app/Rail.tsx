// The rail of explorers (explorer-redesign.md 1.0, the owner's order): Domain model, Reference data, Databases,
// Diagrams and Generate at the top; Settings and the account menu at the bottom. An explorer icon shows that one
// explorer in the sidebar and brings its home screen to the centre (the diagram canvas, or the Database screen);
// the selection outlives the switch. Generate shows its packs in the sidebar and its screen in the centre.
import { Boxes, CircleUserRound, Database, LogOut, Network, Route, Settings, Wand2, Workflow, type LucideIcon } from "lucide-react";
import * as queries from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import { useEditor, type SidebarView, type Workspace } from "@/state/store";
import { Tooltip } from "@/components/ui/tooltip";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger } from "@/components/ui/menu";
import { cn } from "@/lib/cn";
import { useServices } from "./context";
import { useEditorNavigation } from "./navigation";
import { RAIL_LABELS, RAIL_NAME } from "@/model/labels";

const item = (view: SidebarView, icon: LucideIcon, home: Workspace) => ({ view, ...RAIL_LABELS[view], icon, home });

export const RAIL: { view: SidebarView; label: string; tooltip: string; icon: LucideIcon; home: Workspace }[] = [
  item("domain-model", Network, "entities"),
  item("processes", Route, "entities"),
  item("reference-data", Boxes, "reference-data"),
  item("databases", Database, "database"),
  item("diagrams", Workflow, "entities"),
  item("generate", Wand2, "generate"),
];

const buttonClass = "grid size-7 place-items-center rounded-control text-secondary hover:bg-accent-subtle hover:text-primary";

export function Rail() {
  const { store } = useServices();
  const workspace = useEditor(store, (s) => s.workspace);
  const sidebar = useEditor(store, (s) => s.explorer.active);
  const session = queries.useSession();
  const { openWorkspace } = useEditorNavigation();
  const show = (view: SidebarView, home: Workspace) => {
    store.getState().setSidebar(view);
    // A hidden explorer comes back when its rail icon is clicked (the rail is the explorer's edge).
    if (store.getState().explorerCollapsed) store.getState().toggle("explorer", false);
    if (store.getState().workspace !== home) openWorkspace(home);
  };
  return (
    <nav
      aria-label={RAIL_NAME}
      data-region="rail"
      tabIndex={-1}
      className="flex w-[var(--mq-rail-w)] shrink-0 flex-col items-center gap-1 border-r border-default bg-surface py-1"
    >
      {RAIL.map(({ view, label, tooltip, icon: Icon, home }) => {
        const current = sidebar === view && workspace !== "settings";
        return (
          <Tooltip key={view} content={tooltip} side="right">
            <button
              type="button"
              aria-label={label}
              aria-current={current ? "page" : undefined}
              data-testid={`rail-${view}`}
              onClick={() => show(view, home)}
              className={cn(buttonClass, current && "bg-accent-subtle text-accent")}
            >
              <Icon className="size-4" aria-hidden />
            </button>
          </Tooltip>
        );
      })}
      <div className="flex-1" />
      <Tooltip content={RAIL_LABELS.settings.tooltip} side="right">
        <button
          type="button"
          aria-label={RAIL_LABELS.settings.label}
          aria-current={workspace === "settings" ? "page" : undefined}
          data-testid="rail-settings"
          onClick={() => openWorkspace("settings")}
          className={cn(buttonClass, workspace === "settings" && "bg-accent-subtle text-accent")}
        >
          <Settings className="size-5" aria-hidden />
        </button>
      </Tooltip>
      <DropdownMenu>
        <Tooltip content={RAIL_LABELS.account.tooltip} side="right">
          <DropdownMenuTrigger asChild>
            <button type="button" aria-label={RAIL_LABELS.account.label} data-testid="rail-account" className={buttonClass}>
              <CircleUserRound className="size-5" aria-hidden />
            </button>
          </DropdownMenuTrigger>
        </Tooltip>
        <DropdownMenuContent side="right" align="end">
          <DropdownMenuLabel>{session.data?.user.displayName ?? "Signed in"}</DropdownMenuLabel>
          <DropdownMenuItem
            onSelect={() => {
              void endpoints.signOut().finally(() => window.location.reload());
            }}
          >
            <LogOut aria-hidden />
            Sign out
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </nav>
  );
}
