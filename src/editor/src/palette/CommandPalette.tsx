// The command palette (Ctrl/Cmd+K): go to any element in the index, switch workspace, and run the
// shell's commands (theme, density, undo, redo, panels). cmdk on a Radix dialog.
import { Command } from "cmdk";
import { useIndex } from "@/api/queries";
import { useServices } from "@/app/context";
import { KindIcon } from "@/app/icons";
import { useEditorNavigation } from "@/app/navigation";
import { useUndoRedo } from "@/app/shortcuts";
import { useEditor, WORKSPACES, type Workspace } from "@/state/store";

const WORKSPACE_LABELS: Record<Workspace, string> = {
  entities: "Entities",
  database: "Database",
  mappings: "Mappings",
  generate: "Generate",
  settings: "Settings",
};

const itemClass =
  "flex h-[var(--mq-row-h)] cursor-pointer items-center gap-2 rounded-[6px] px-2 text-[13px] text-primary data-[selected=true]:bg-accent-subtle";
const groupClass =
  "px-1 py-1 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1 [&_[cmdk-group-heading]]:text-[11px] [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:text-secondary";

export function CommandPalette() {
  const { store } = useServices();
  const open = useEditor(store, (s) => s.paletteOpen);
  const theme = useEditor(store, (s) => s.theme);
  const density = useEditor(store, (s) => s.density);
  const canApply = useEditor(store, (s) => s.generation.planId !== null && s.generation.applyJob === null);
  const index = useIndex();
  const { reveal, openWorkspace } = useEditorNavigation();
  const { undo, redo } = useUndoRedo();

  const close = () => store.getState().setPaletteOpen(false);
  const run = (action: () => void) => () => {
    close();
    action();
  };

  return (
    <Command.Dialog
      open={open}
      onOpenChange={(value) => store.getState().setPaletteOpen(value)}
      label="Command palette"
      overlayClassName="fixed inset-0 z-40 bg-[color-mix(in_srgb,var(--mq-bg-app)_60%,transparent)]"
      contentClassName="fixed left-1/2 top-[15vh] z-50 w-[min(560px,calc(100vw-32px))] -translate-x-1/2 overflow-hidden rounded-[8px] border border-default bg-raised shadow-lg"
      data-testid="command-palette"
    >
      <Command.Input
        placeholder="Go to an element or run a command…"
        className="h-11 w-full border-b border-default bg-transparent px-3 text-[14px] text-primary outline-none placeholder:text-secondary"
      />
      <Command.List className="max-h-[50vh] overflow-y-auto">
        <Command.Empty className="px-3 py-4 text-[13px] text-secondary">Nothing matches.</Command.Empty>
        <Command.Group heading="Commands" className={groupClass}>
          <Command.Item className={itemClass} onSelect={run(() => store.getState().setTheme(theme === "dark" ? "light" : "dark"))}>
            Toggle theme
          </Command.Item>
          <Command.Item className={itemClass} onSelect={run(() => store.getState().setDensity(density === "compact" ? "comfortable" : "compact"))}>
            Toggle density
          </Command.Item>
          <Command.Item className={itemClass} onSelect={run(() => void undo())}>
            Undo
          </Command.Item>
          <Command.Item className={itemClass} onSelect={run(() => void redo())}>
            Redo
          </Command.Item>
          <Command.Item
            className={itemClass}
            onSelect={run(() => {
              openWorkspace("generate");
              store.getState().requestCommand("plan");
            })}
          >
            Plan generation
          </Command.Item>
          {canApply ? (
            <Command.Item
              className={itemClass}
              onSelect={run(() => {
                openWorkspace("generate");
                store.getState().requestCommand("apply");
              })}
            >
              Apply plan
            </Command.Item>
          ) : null}
          <Command.Item
            className={itemClass}
            onSelect={run(() => {
              openWorkspace("entities");
              store.getState().requestCommand("new-entity");
            })}
          >
            New entity
          </Command.Item>
          <Command.Item className={itemClass} onSelect={run(() => store.getState().toggle("explorer"))}>
            Toggle explorer
          </Command.Item>
          <Command.Item className={itemClass} onSelect={run(() => store.getState().toggle("inspector"))}>
            Toggle inspector
          </Command.Item>
          <Command.Item className={itemClass} onSelect={run(() => store.getState().toggle("bottom"))}>
            Toggle bottom panel
          </Command.Item>
        </Command.Group>
        <Command.Group heading="Workspaces" className={groupClass}>
          {WORKSPACES.map((w) => (
            <Command.Item key={w} value={`workspace ${WORKSPACE_LABELS[w]}`} className={itemClass} onSelect={run(() => openWorkspace(w))}>
              {WORKSPACE_LABELS[w]}
            </Command.Item>
          ))}
        </Command.Group>
        {index.data?.length ? (
          <Command.Group heading="Elements" className={groupClass}>
            {index.data.map((e) => (
              <Command.Item key={e.id} value={`${e.name || e.id} ${e.kind} ${e.id}`} className={itemClass} onSelect={run(() => reveal(e))}>
                <KindIcon kind={e.kind} className="size-4 text-secondary" />
                <span className="truncate">{e.name || e.id}</span>
                <span className="ml-auto text-[11px] text-secondary">{e.kind}</span>
              </Command.Item>
            ))}
          </Command.Group>
        ) : null}
      </Command.List>
    </Command.Dialog>
  );
}
