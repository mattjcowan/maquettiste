import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronUp } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Badge, EmptyState, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { ProblemsPanel, useVisibleProblemGroups } from "@/problems/ProblemsPanel";
import { DiffViewer } from "@/diff/DiffViewer";
import { ReferencesPanel } from "@/references/ReferencesPanel";
import { useEditor, type BottomTab } from "@/state/store";
import { cn } from "@/lib/cn";
import { useServices } from "./context";
import { PANEL_KEYS } from "@/state/layout";
import { USED_TOOLTIP } from "@/model/labels";
import { QUIET_FILES } from "@/workspaces/generate/planModel";

function OutputPanel() {
  const { store } = useServices();
  const output = useEditor(store, (s) => s.output);
  if (!output.length) return <EmptyState title="No output yet">Plans, applies and their outcomes appear here.</EmptyState>;
  return (
    <ol className="flex flex-col py-1 font-mono text-12" aria-label="Output" data-testid="output-list">
      {[...output].reverse().map((o) => (
        <li key={o.id} className="flex gap-2 px-2 py-0.5">
          <time className="shrink-0 text-secondary">{o.time.slice(11, 19)}</time>
          <span className={cn(o.level === "success" && "text-success", o.level === "error" && "text-danger", o.level === "warning" && "text-warning")}>
            {o.text}
          </span>
        </li>
      ))}
    </ol>
  );
}

function DiffPanel() {
  const { store } = useServices();
  const diff = useEditor(store, (s) => s.diff);
  // A file Apply leaves alone (identical, not re-rendered, or yours) has nothing to diff: a kept file is never compared with what
  // its unit renders now (a migration's placeholder, say).
  const quiet = diff?.kind ? QUIET_FILES[diff.kind] : undefined;
  const query = useQuery({
    queryKey: ["planDiff", diff?.planId ?? "", diff?.path ?? ""],
    queryFn: () => endpoints.getPlanDiff(diff!.planId, diff!.path),
    enabled: !!diff && !quiet,
  });
  if (!diff) return <EmptyState title="No file selected">Select a file in a plan (Generate) to see its diff.</EmptyState>;
  if (quiet)
    return (
      <div className="flex h-full flex-col" data-testid="diff-quiet" data-kind={diff.kind}>
        <EmptyState title={quiet.title}>
          <span className="font-mono">{diff.path}</span>: {quiet.text}
        </EmptyState>
      </div>
    );
  if (query.isPending) return <Spinner label="Loading diff" />;
  if (query.error) return <EmptyState title="The diff could not be loaded">{String((query.error as Error).message)}</EmptyState>;
  return <DiffViewer path={diff.path} text={query.data ?? ""} />;
}

export function BottomPanel() {
  const { store } = useServices();
  const tab = useEditor(store, (s) => s.bottomTab);
  const collapsed = useEditor(store, (s) => s.bottomCollapsed);
  // The tab's count follows the panel's severity filter, so hiding the notes also quiets the badge.
  const { groups } = useVisibleProblemGroups();
  const counts = {
    errors: groups.flatMap((g) => g.diagnostics).filter((d) => d.severity === "error").length,
    shown: groups.reduce((n, g) => n + g.diagnostics.length, 0),
  };
  return (
    <section aria-label="Problems, output, diff and references" className="flex h-full min-h-0 flex-col bg-surface" data-testid="bottom-panel">
      <Tabs value={tab} onValueChange={(v) => store.getState().setBottomTab(v as BottomTab)} className="flex min-h-0 flex-1 flex-col">
        <div className="flex items-center border-b border-default">
          <TabsList className="flex-1 border-b-0" aria-label="Bottom panel">
            <TabsTrigger value="problems" data-testid="tab-problems">
              Problems
              <Badge tone={counts.errors ? "danger" : "neutral"} data-testid="problem-count">
                {counts.shown}
              </Badge>
            </TabsTrigger>
            <TabsTrigger value="output" data-testid="tab-output">
              Output
            </TabsTrigger>
            <TabsTrigger value="diff" data-testid="tab-diff">
              Diff
            </TabsTrigger>
            <TabsTrigger value="references" data-testid="tab-references" title={USED_TOOLTIP}>
              References
            </TabsTrigger>
          </TabsList>
          <Button
            variant="ghost"
            size="icon-sm"
            className="mr-2"
            label={collapsed ? "Expand the bottom panel" : "Collapse the bottom panel"}
            title={`${collapsed ? "Show" : "Hide"} the bottom panel (${PANEL_KEYS.bottom.label})`}
            aria-expanded={!collapsed}
            data-testid={collapsed ? "show-bottom" : "hide-bottom"}
            onClick={() => store.getState().toggle("bottom")}
          >
            {collapsed ? <ChevronUp /> : <ChevronDown />}
          </Button>
        </div>
        {collapsed ? null : (
          <>
            <TabsContent value="problems" className="overflow-auto">
              <ProblemsPanel />
            </TabsContent>
            <TabsContent value="output" className="overflow-auto">
              <OutputPanel />
            </TabsContent>
            <TabsContent value="diff" className="flex min-h-0 flex-col">
              <DiffPanel />
            </TabsContent>
            <TabsContent value="references" className="flex min-h-0 flex-col">
              <ReferencesPanel />
            </TabsContent>
          </>
        )}
      </Tabs>
    </section>
  );
}
