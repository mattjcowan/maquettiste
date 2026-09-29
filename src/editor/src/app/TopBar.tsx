import { GitBranch, Monitor, Moon, Redo2, Search, Sun, Undo2 } from "lucide-react";
import { useProject, useSession } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuTrigger } from "@/components/ui/menu";
import { Kbd } from "@/components/ui/misc";
import { Tooltip } from "@/components/ui/tooltip";
import { LOGO_TOOLTIP, PROJECT_TOOLTIP } from "@/model/labels";
import { useEditor, type ThemeChoice } from "@/state/store";
import { cn } from "@/lib/cn";
import { useServices } from "./context";
import { useUndoRedo } from "./shortcuts";
import { LocaleSwitcher } from "@/l10n/LocaleSwitcher";
import { LocalizationSync } from "@/l10n/queries";

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
  const ThemeIcon = THEME_ICON[theme];
  const isMac = typeof navigator !== "undefined" && /Mac/.test(navigator.platform);

  return (
    <header className="flex h-[var(--mq-topbar-h)] shrink-0 items-center gap-2 border-b border-default bg-surface px-2" data-region="topbar">
      <div className="flex min-w-0 items-center gap-2">
        <Tooltip content={LOGO_TOOLTIP}>
          <span
            aria-hidden
            className="grid size-6 place-items-center rounded-control bg-accent text-12 font-semibold text-accent-foreground"
            data-testid="logo"
          >
            M
          </span>
        </Tooltip>
        <Tooltip content={PROJECT_TOOLTIP}>
          <h1 className="truncate text-14 font-semibold" data-testid="project-name">
            {project.data?.name ?? "Maquettiste"}
          </h1>
        </Tooltip>
        {git ? (
          <span className="flex items-center gap-1 text-12 text-secondary" data-testid="git-status">
            <GitBranch className="size-3.5" aria-hidden />
            <span>{git.branch ?? "detached"}</span>
            <span aria-label={`${git.changedModelFiles} changed model files`}>· {git.changedModelFiles} changed</span>
          </span>
        ) : null}
      </div>

      <button
        type="button"
        onClick={() => store.getState().setPaletteOpen(true)}
        className="mx-auto flex h-6 w-full max-w-md items-center gap-2 rounded-control border border-input bg-app px-2 text-13 text-secondary hover:border-accent"
        aria-label="Open the command palette"
      >
        <Search className="size-4" aria-hidden />
        <span className="flex-1 text-left">Go to element or run a command</span>
        <Kbd>{isMac ? "⌘K" : "Ctrl K"}</Kbd>
      </button>

      <div className="flex items-center gap-1">
        <LocalizationSync />
        <LocaleSwitcher />
        <Tooltip content="Undo (Ctrl+Z)">
          <Button variant="ghost" size="icon" aria-label="Undo" disabled={!canUndo} onClick={() => void undo()}>
            <Undo2 />
          </Button>
        </Tooltip>
        <Tooltip content="Redo (Ctrl+Shift+Z)">
          <Button variant="ghost" size="icon" aria-label="Redo" disabled={!canRedo} onClick={() => void redo()}>
            <Redo2 />
          </Button>
        </Tooltip>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={`Theme: ${theme}`} data-testid="theme-menu">
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
      </div>
    </header>
  );
}
