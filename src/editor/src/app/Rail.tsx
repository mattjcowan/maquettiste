import { Box, Code, Database, GitPullRequest, Network, Settings, Users, Wand2, Workflow, ArrowLeftRight } from "lucide-react";
import { useEditor, type Workspace } from "@/state/store";
import { Tooltip } from "@/components/ui/tooltip";
import { cn } from "@/lib/cn";
import { useServices } from "./context";
import { useEditorNavigation } from "./navigation";

const ACTIVE: { id: Workspace; label: string; icon: typeof Box }[] = [
  { id: "entities", label: "Entities", icon: Network },
  { id: "database", label: "Database", icon: Database },
  { id: "mappings", label: "Mappings", icon: ArrowLeftRight },
  { id: "generate", label: "Generate", icon: Wand2 },
  { id: "settings", label: "Settings", icon: Settings },
];

const LATER = [
  { label: "Processes", icon: Workflow, phase: "phase 3" },
  { label: "Templates", icon: Code, phase: "phase 4" },
  { label: "Source control", icon: GitPullRequest, phase: "phase 4" },
  { label: "Team", icon: Users, phase: "phase 4" },
];

export function Rail() {
  const { store } = useServices();
  const workspace = useEditor(store, (s) => s.workspace);
  const { openWorkspace } = useEditorNavigation();
  return (
    <nav
      aria-label="Workspaces"
      data-region="rail"
      tabIndex={-1}
      className="flex w-[var(--mq-rail-w)] shrink-0 flex-col items-center gap-1 border-r border-default bg-surface py-2"
    >
      {ACTIVE.map(({ id, label, icon: Icon }) => (
        <Tooltip key={id} content={label} side="right">
          <button
            type="button"
            aria-label={label}
            aria-current={workspace === id ? "page" : undefined}
            data-testid={`rail-${id}`}
            onClick={() => openWorkspace(id)}
            className={cn(
              "grid size-9 place-items-center rounded-control text-secondary hover:bg-accent-subtle hover:text-primary",
              workspace === id && "bg-accent-subtle text-accent",
            )}
          >
            <Icon className="size-5" aria-hidden />
          </button>
        </Tooltip>
      ))}
      <div className="my-2 h-px w-6 bg-default" aria-hidden />
      {LATER.map(({ label, icon: Icon, phase }) => (
        <Tooltip key={label} content={`${label} (${phase})`} side="right">
          <button
            type="button"
            aria-label={`${label}, available in ${phase}`}
            aria-disabled="true"
            className="grid size-9 cursor-not-allowed place-items-center rounded-control text-secondary opacity-60"
          >
            <Icon className="size-5" aria-hidden />
          </button>
        </Tooltip>
      ))}
    </nav>
  );
}
