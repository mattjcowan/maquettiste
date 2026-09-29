import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronUp } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Badge, EmptyState, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { ProblemsPanel, useProblemGroups } from "@/problems/ProblemsPanel";
import { countBySeverity } from "@/problems/group";
import { DiffViewer } from "@/diff/DiffViewer";
import { ReferencesPanel } from "@/references/ReferencesPanel";
import { useEditor, type BottomTab } from "@/state/store";
import { cn } from "@/lib/cn";
import { useServices } from "./context";

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
  const query = useQuery({
    queryKey: ["planDiff", diff?.planId ?? "", diff?.path ?? ""],
    queryFn: () => endpoints.getPlanDiff(diff!.planId, diff!.path),
    enabled: !!diff,
  });
  if (!diff) return <EmptyState title="No file selected">Select a file in a plan (Generate) to see its diff.</EmptyState>;
  if (query.isPending) return <Spinner label="Loading diff" />;
  if (query.error) return <EmptyState title="The diff could not be loaded">{String((query.error as Error).message)}</EmptyState>;
  return <DiffViewer path={diff.path} text={query.data ?? ""} />;
}

export function BottomPanel() {
  const { store } = useServices();
  const tab = useEditor(store, (s) => s.bottomTab);
  const collapsed = useEditor(store, (s) => s.bottomCollapsed);
  const { groups } = useProblemGroups();
  const counts = countBySeverity(groups);
  return (
    <section aria-label="Problems, output, diff and references" className="flex h-full min-h-0 flex-col bg-surface" data-testid="bottom-panel">
      <Tabs value={tab} onValueChange={(v) => store.getState().setBottomTab(v as BottomTab)} className="flex min-h-0 flex-1 flex-col">
        <div className="flex items-center border-b border-default">
          <TabsList className="flex-1 border-b-0" aria-label="Bottom panel">
            <TabsTrigger value="problems" data-testid="tab-problems">
              Problems
              <Badge tone={counts.errors ? "danger" : "neutral"} data-testid="problem-count">
                {counts.errors + counts.warnings + counts.infos}
              </Badge>
            </TabsTrigger>
            <TabsTrigger value="output" data-testid="tab-output">
              Output
            </TabsTrigger>
            <TabsTrigger value="diff" data-testid="tab-diff">
              Diff
            </TabsTrigger>
            <TabsTrigger value="references" data-testid="tab-references" title="Where used (Shift+F12)">
              References
            </TabsTrigger>
          </TabsList>
          <Button
            variant="ghost"
            size="icon-sm"
            className="mr-2"
            aria-label={collapsed ? "Expand the bottom panel" : "Collapse the bottom panel"}
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
