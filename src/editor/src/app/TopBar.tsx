import { GitBranch, Monitor, Moon, Redo2, Search, Sparkles, Sun, Undo2 } from "lucide-react";
import { useProject, useSession } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuTrigger } from "@/components/ui/menu";
import { Kbd } from "@/components/ui/misc";
import { Tooltip } from "@/components/ui/tooltip";
import { LOGO_TOOLTIP, PROJECT_TOOLTIP } from "@/model/labels";
import { useEditor, type ThemeChoice } from "@/state/store";
import { cn } from "@/lib/cn";
import { useServices } from "./context";
import { Link } from "react-router";
import { productLine, productTooltip } from "./productLine";
import { useUndoRedo } from "./shortcuts";
import { LocaleSwitcher } from "@/l10n/LocaleSwitcher";
import { LocalizationSync } from "@/l10n/queries";
import { useBrandingView } from "./branding";
import { PanelToggle } from "./panels";
import { SnapshotPicker } from "@/snapshots/SnapshotPicker";
import { AsOfBanner } from "@/snapshots/AsOfBanner";
import { useSnapshotScope } from "@/snapshots/state";
import { withSnapshotParam } from "@/api/snapshotScope";

const THEME_ICON = { system: Monitor, light: Sun, dark: Moon } as const;

export function TopBar() {
  const { store } = useServices();
  const project = useProject();
  const session = useSession();
  const theme = useEditor(store, (s) => s.theme);
  const connection = useEditor(store, (s) => s.connection);
  const canUndo = useEditor(store, (s) => s.undo.length > 0);
  const canRedo = useEditor(store, (s) => s.redo.length > 0);
  const { undo, redo } = useUndoRedo();
  const git = project.data?.git;
  const line = productLine(project.data);
  // The secondary controls (git status, content locale, undo and redo, theme, live status, user) hide together.
  const hidden = useEditor(store, (s) => s.topbarCollapsed);
  const assistantOpen = useEditor(store, (s) => s.assistantOpen);
  const branding = useBrandingView();
  const ThemeIcon = THEME_ICON[theme];
  const asOf = useSnapshotScope();
  const isMac = typeof navigator !== "undefined" && /Mac/.test(navigator.platform);

  return (
    <header className="flex h-[var(--mq-topbar-h)] shrink-0 items-center gap-2 border-b border-default bg-surface px-2" data-region="topbar">
      <div className="flex min-w-28 items-center gap-2">
        <Tooltip content={LOGO_TOOLTIP}>
          <Link
            to={{ pathname: "/", search: withSnapshotParam("", asOf) }}
            aria-label={LOGO_TOOLTIP}
            className="shrink-0 rounded-control"
            data-testid="home-link"
          >
            {branding.iconUrl ? (
              <img src={branding.iconUrl} alt="" aria-hidden className="size-6 rounded-control object-contain" data-testid="logo" />
            ) : (
              <span
                aria-hidden
                className="grid size-6 place-items-center rounded-control bg-accent text-12 font-semibold text-accent-foreground"
                data-testid="logo"
              >
                M
              </span>
            )}
          </Link>
        </Tooltip>
        <div className="flex min-w-0 flex-col">
          {/* The project name opens the snapshot picker: the working model and its snapshots. */}
          <SnapshotPicker tooltip={PROJECT_TOOLTIP}>
            <h1 className={cn("truncate text-14 font-semibold", line && "leading-4")} data-testid="project-name">
              {branding.name}
            </h1>
          </SnapshotPicker>
          {line ? (
            <Tooltip content={productTooltip(project.data)}>
              <span className="truncate text-11 leading-3 text-secondary" data-testid="product-line">
                {line}
              </span>
            </Tooltip>
          ) : null}
        </div>
        {git && !line && !hidden && !asOf ? (
          <span className="flex items-center gap-1 text-12 text-secondary" data-testid="git-status">
            <GitBranch className="size-3.5" aria-hidden />
            <span>{git.branch ?? "detached"}</span>
            <span aria-label={`${git.changedModelFiles} changed model files`}>· {git.changedModelFiles} changed</span>
          </span>
        ) : null}
      </div>

      <AsOfBanner />

      <button
        type="button"
        onClick={() => store.getState().setPaletteOpen(true)}
        className="mx-auto flex h-6 w-full min-w-0 max-w-md items-center gap-2 rounded-control border border-input bg-app px-2 text-13 text-secondary hover:border-accent"
        aria-label="Open the command palette"
      >
        <Search className="size-4" aria-hidden />
        <span className="min-w-0 flex-1 truncate text-left">Go to element or run a command</span>
        <Kbd>{isMac ? "⌘K" : "Ctrl K"}</Kbd>
      </button>

      <div className="flex shrink-0 items-center gap-1" data-testid="topbar-controls">
        <LocalizationSync />
        {hidden ? null : (
          <>
            <LocaleSwitcher />
            {/* A snapshot shown as of is read-only: nothing to undo there (the working model's steps wait for the way back). */}
            <span className="inline-flex" title={asOf ? "Undo is off while a snapshot is shown (read-only)" : undefined}>
              <Button variant="ghost" size="icon" label="Undo" shortcut="Ctrl+Z" disabled={!canUndo || !!asOf} onClick={() => void undo()}>
                <Undo2 />
              </Button>
            </span>
            <span className="inline-flex" title={asOf ? "Redo is off while a snapshot is shown (read-only)" : undefined}>
              <Button variant="ghost" size="icon" label="Redo" shortcut="Ctrl+Shift+Z" disabled={!canRedo || !!asOf} onClick={() => void redo()}>
                <Redo2 />
              </Button>
            </span>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="ghost" size="icon" label={`Theme: ${theme}`} data-testid="theme-menu">
                  <ThemeIcon />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuLabel>Theme</DropdownMenuLabel>
                <DropdownMenuRadioGroup value={theme} onValueChange={(v) => store.getState().setTheme(v as ThemeChoice)}>
                  <DropdownMenuRadioItem value="system">System</DropdownMenuRadioItem>
                  <DropdownMenuRadioItem value="light">Light</DropdownMenuRadioItem>
                  <DropdownMenuRadioItem value="dark">Dark</DropdownMenuRadioItem>
                </DropdownMenuRadioGroup>
              </DropdownMenuContent>
            </DropdownMenu>
            <span className="ml-2 flex items-center gap-2 text-12 text-secondary" data-testid="connection">
              <span
                aria-hidden
                className={cn("size-2 rounded-full", connection === "connected" ? "bg-success" : connection === "disconnected" ? "bg-danger" : "bg-warning")}
              />
              <span className="sr-only">Live updates: </span>
              <span>{connection === "connected" ? "Live" : connection}</span>
            </span>
            <span className="ml-2 text-12 font-medium" data-testid="user">
              {session.data?.user.displayName ?? ""}
            </span>
          </>
        )}
        <Button
          variant="ghost"
          size="icon"
          label={asOf ? "Assistant (not available for a snapshot)" : "Assistant"}
          shortcut="Ctrl+I"
          disabled={!!asOf}
          aria-pressed={assistantOpen}
          aria-controls={assistantOpen ? "mq-assistant" : undefined}
          className={cn(assistantOpen && "bg-accent-subtle")}
          onClick={() => store.getState().setAssistantOpen(!assistantOpen)}
          data-testid="assistant-toggle"
        >
          <Sparkles />
        </Button>
        <PanelToggle panel="topbar" className="ml-1" />
      </div>
    </header>
  );
}
