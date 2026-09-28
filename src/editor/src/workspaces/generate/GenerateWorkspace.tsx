// The Generate workspace (phase2-design.md 4.8): pack and root selection, then Plan with live
// progress and Cancel; the plan counted by FileChangeKind and pack, its changes in a virtualized
// table filtered by kind and pack, hand edits and conflicts flagged; selecting a file opens its
// diff in the bottom panel. Apply queues the plan by id; the result is the apply job's
// applyResult.outcome, never its state. Run history lists GET /api/jobs.
import { useEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { createColumnHelper, flexRender, getCoreRowModel, getFilteredRowModel, useReactTable } from "@tanstack/react-table";
import { CircleAlert, CircleCheck, Play, Square, TriangleAlert, Wand2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { keys, useJob, useJobs, usePlan, useProject, useSettings } from "@/api/queries";
import type { FileChange, FileChangeKind, JobInfo, RootSelection } from "@/api/types";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";
import { isFinished, jobOutcome } from "@/realtime/jobs";
import { Button } from "@/components/ui/button";
import { Badge, EmptyState, SectionTitle, Spinner, Toolbar } from "@/components/ui/misc";
import { Checkbox } from "@/components/ui/checkbox";
import { Select } from "@/components/ui/input";
import { cn } from "@/lib/cn";

const KIND_TONE: Partial<Record<FileChangeKind, "success" | "danger" | "warning" | "accent" | "neutral">> = {
  added: "success",
  modified: "accent",
  deleted: "danger",
  "hand-edited": "warning",
  conflict: "danger",
  "orphaned-owned": "warning",
};

/** Kinds an apply leaves alone: an unchanged file, and a companion that is kept as it is on disk. */
const NOTHING_TO_WRITE = new Set<FileChangeKind>(["unchanged", "kept"]);

function Progress({ job }: { job: JobInfo }) {
  const p = job.progress;
  const pct = p && p.total ? Math.round((p.done / p.total) * 100) : 0;
  return (
    <div className="flex flex-col gap-1" data-testid="job-progress">
      <div className="flex items-center justify-between text-12">
        <span>
          {job.kind === "plan" ? "Planning" : "Applying"}: <span className="font-semibold">{p?.stage ?? job.state}</span>
        </span>
        <span className="font-mono text-secondary">{p ? `${p.done} / ${p.total}` : ""}</span>
      </div>
      <div
        className="h-1.5 overflow-hidden rounded-full bg-app"
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={pct}
        aria-label={`${job.kind} progress`}
      >
        <div className="h-full bg-accent mq-transition transition-[width]" style={{ width: `${pct}%` }} />
      </div>
      {p?.currentPath ? <span className="truncate font-mono text-11 text-secondary">{p.currentPath}</span> : null}
    </div>
  );
}

function OutcomeBadge({ job }: { job: JobInfo }) {
  const outcome = jobOutcome(job);
  if (!isFinished(job)) return <Badge>{job.state}</Badge>;
  if (outcome === "succeeded")
    return (
      <Badge tone="success" data-testid="outcome">
        <CircleCheck className="size-3" aria-hidden /> succeeded
      </Badge>
    );
  return (
    <Badge tone={outcome === "cancelled" ? "warning" : "danger"} data-testid="outcome">
      <CircleAlert className="size-3" aria-hidden /> {outcome ?? job.state}
    </Badge>
  );
}

const helper = createColumnHelper<FileChange>();

function ChangesTable({ planId, changes }: { planId: string; changes: FileChange[] }) {
  const { store } = useServices();
  const diff = useEditor(store, (s) => s.diff);
  const [kind, setKind] = useState<string>("changed");
  const [pack, setPack] = useState<string>("");
  const columns = useMemo(
    () => [
      helper.accessor("kind", {
        header: "Change",
        filterFn: (row, id, value: string) => (value === "changed" ? row.getValue(id) !== "unchanged" : !value || row.getValue(id) === value),
      }),
      helper.accessor("path", { header: "File" }),
      helper.accessor("pack", { header: "Pack", filterFn: (row, id, value: string) => !value || row.getValue(id) === value }),
    ],
    [],
  );
  const columnFilters = useMemo(
    () => [
      { id: "kind", value: kind },
      { id: "pack", value: pack },
    ],
    [kind, pack],
  );
  const table = useReactTable({
    data: changes,
    columns,
    state: { columnFilters },
    getCoreRowModel: getCoreRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
  });
  const rows = table.getRowModel().rows;
  const scrollRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => 30, overscan: 20 });
  const kinds = [...new Set(changes.map((c) => c.kind))].sort();
  const packs = [...new Set(changes.map((c) => c.pack))].sort();
  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2">
      <div className="flex items-center gap-2">
        <label htmlFor="filter-kind" className="text-12 text-secondary">
          Show
        </label>
        <Select id="filter-kind" className="h-7 w-40 text-12" value={kind} onChange={(e) => setKind(e.target.value)}>
          <option value="changed">All but unchanged</option>
          <option value="">Everything</option>
          {kinds.map((k) => (
            <option key={k} value={k}>
              {k}
            </option>
          ))}
        </Select>
        <label htmlFor="filter-pack" className="text-12 text-secondary">
          Pack
        </label>
        <Select id="filter-pack" className="h-7 w-40 text-12" value={pack} onChange={(e) => setPack(e.target.value)}>
          <option value="">All packs</option>
          {packs.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </Select>
        <span className="ml-auto text-12 text-secondary">{rows.length} files</span>
      </div>
      <div className="min-h-0 flex-1 overflow-hidden rounded-control border border-default">
        <div role="table" aria-label="Planned file changes" className="flex h-full flex-col text-12">
          <div role="rowgroup">
            {table.getHeaderGroups().map((g) => (
              <div role="row" key={g.id} className="grid grid-cols-[120px_1fr_120px] bg-app px-2">
                {g.headers.map((h) => (
                  <div role="columnheader" key={h.id} className="py-1 text-11 font-semibold text-secondary">
                    {flexRender(h.column.columnDef.header, h.getContext())}
                  </div>
                ))}
              </div>
            ))}
          </div>
          <div ref={scrollRef} role="rowgroup" className="min-h-0 flex-1 overflow-auto" data-testid="changes">
            <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
              {virtualizer.getVirtualItems().map((item) => {
                const row = rows[item.index];
                const c = row.original;
                const active = diff?.planId === planId && diff.path === c.path;
                return (
                  <button
                    type="button"
                    role="row"
                    key={row.id}
                    style={{ position: "absolute", top: 0, left: 0, right: 0, height: item.size, transform: `translateY(${item.start}px)` }}
                    className={cn("grid grid-cols-[120px_1fr_120px] items-center px-2 text-left hover:bg-accent-subtle", active && "bg-accent-subtle")}
                    onClick={() => store.getState().showDiff({ planId, path: c.path })}
                    data-testid={`change-${c.path}`}
                  >
                    <span role="cell" className="flex items-center gap-1">
                      {c.kind === "hand-edited" || c.kind === "conflict" ? (
                        <TriangleAlert className="size-3.5 text-warning" aria-label="needs attention" />
                      ) : null}
                      <Badge tone={KIND_TONE[c.kind] ?? "neutral"}>{c.kind}</Badge>
                    </span>
                    <span role="cell" className="truncate font-mono">
                      {c.path}
                    </span>
                    <span role="cell" className="text-secondary">
                      {c.pack}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

export function GenerateWorkspace() {
  const { store, jobs } = useServices();
  const qc = useQueryClient();
  const project = useProject();
  const settings = useSettings();
  const generation = useEditor(store, (s) => s.generation);
  const planJob = useJob(generation.planJob);
  const applyJob = useJob(generation.applyJob);
  const plan = usePlan(generation.planId);
  const history = useJobs();
  const packs = useMemo(() => project.data?.packs ?? [], [project.data]);
  const enabled = useMemo(() => packs.filter((p) => project.data?.settings.packs[p.name]?.enabled !== false).map((p) => p.name), [packs, project.data]);
  const [chosen, setChosen] = useState<string[] | null>(null);
  const [roots, setRoots] = useState<RootSelection>("all");
  const [busy, setBusy] = useState(false);
  const selectedPacks = chosen ?? enabled;
  const running = [planJob.data, applyJob.data].find((j) => j && !isFinished(j)) ?? null;

  useEffect(() => {
    const off = jobs.onFinished((job) => {
      if (job.kind === "plan" && job.id === store.getState().generation.planJob) {
        const planId = job.planResult?.plan?.id ?? null;
        store.getState().setGeneration({ planId });
      }
      void qc.invalidateQueries({ queryKey: keys.jobs });
    });
    // A view that comes back to a running job follows it again.
    const { planJob: p, applyJob: a } = store.getState().generation;
    for (const id of [p, a]) if (id) void jobs.watch(id);
    return () => {
      off();
      const g = store.getState().generation;
      for (const id of [g.planJob, g.applyJob]) if (id) void jobs.release(id);
    };
  }, [jobs, store, qc]);

  const startPlan = async () => {
    setBusy(true);
    try {
      store.getState().setGeneration({ planJob: null, planId: null, applyJob: null });
      store.getState().showDiff(null);
      const job = await jobs.startPlan({ packs: selectedPacks, roots });
      store.getState().setGeneration({ planJob: job.id, planId: isFinished(job) ? (job.planResult?.plan?.id ?? null) : null });
    } catch (error) {
      store.getState().notify(`The plan could not start: ${(error as Error).message}`, "error");
    } finally {
      setBusy(false);
    }
  };

  const startApply = async () => {
    if (!generation.planId) return;
    setBusy(true);
    try {
      const job = await jobs.startApply(generation.planId);
      store.getState().setGeneration({ applyJob: job.id });
    } catch (error) {
      store.getState().notify(`The apply could not start: ${(error as Error).message}`, "error");
    } finally {
      setBusy(false);
    }
  };

  // Palette commands (4.8): Plan and Apply run here with this workspace's current pack and root choice.
  const command = useEditor(store, (s) => s.command);
  const commandActions = useRef({ startPlan, startApply });
  commandActions.current = { startPlan, startApply };
  useEffect(() => {
    if (!command || (command.name !== "plan" && command.name !== "apply")) return;
    store.getState().requestCommand(null);
    void (command.name === "plan" ? commandActions.current.startPlan() : commandActions.current.startApply());
  }, [command, store]);

  const cancel = async () => {
    if (!running) return;
    try {
      await endpoints.cancelJob(running.id);
    } catch (error) {
      store.getState().notify(`Cancel failed: ${(error as Error).message}`, "error");
    }
  };

  const counts = useMemo(() => {
    const byKind = new Map<string, number>();
    const byPack = new Map<string, number>();
    for (const c of plan.data?.changes ?? []) {
      byKind.set(c.kind, (byKind.get(c.kind) ?? 0) + 1);
      if (!NOTHING_TO_WRITE.has(c.kind)) byPack.set(c.pack, (byPack.get(c.pack) ?? 0) + 1);
    }
    return { byKind, byPack };
  }, [plan.data]);
  const applied = applyJob.data;
  const applyOutcome = applied && isFinished(applied) ? jobOutcome(applied) : null;
  const handDefault = settings.data?.settings.handEdits;

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="generate-workspace">
      <Toolbar label="Generation">
        <Wand2 className="size-4 text-secondary" aria-hidden />
        <span className="text-13 font-semibold">Generate</span>
        <fieldset className="ml-4 flex items-center gap-3">
          <legend className="sr-only">Packs</legend>
          {packs.map((p) => (
            <label key={p.name} className="flex items-center gap-1.5 text-13">
              <Checkbox
                checked={selectedPacks.includes(p.name)}
                aria-label={`Pack ${p.name}`}
                onCheckedChange={(v) => setChosen(v === true ? [...selectedPacks, p.name] : selectedPacks.filter((x) => x !== p.name))}
              />
              {p.name}
            </label>
          ))}
        </fieldset>
        <label htmlFor="roots" className="ml-2 text-12 text-secondary">
          Roots
        </label>
        <Select id="roots" className="h-7 w-32 text-12" value={roots} onChange={(e) => setRoots(e.target.value as RootSelection)}>
          <option value="all">all</option>
          <option value="committed">committed</option>
          <option value="built">built</option>
        </Select>
        <div className="ml-auto flex items-center gap-2">
          {running ? (
            <Button size="sm" onClick={() => void cancel()} data-testid="cancel-job">
              <Square /> Cancel
            </Button>
          ) : null}
          <Button size="sm" variant="primary" disabled={busy || !!running || selectedPacks.length === 0} onClick={() => void startPlan()} data-testid="plan">
            <Play /> Plan
          </Button>
          <Button
            size="sm"
            disabled={busy || !!running || !generation.planId || plan.data?.changes.every((c) => NOTHING_TO_WRITE.has(c.kind))}
            onClick={() => void startApply()}
            data-testid="apply"
          >
            Apply plan
          </Button>
        </div>
      </Toolbar>
      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_320px] gap-0">
        <div className="flex min-h-0 flex-col gap-3 overflow-hidden p-3">
          {running ? <Progress job={running} /> : null}
          {planJob.data && isFinished(planJob.data) ? (
            <div className="flex items-center gap-2 text-13" data-testid="plan-result">
              <span>Plan</span>
              <OutcomeBadge job={planJob.data} />
              {handDefault ? <span className="text-12 text-secondary">hand edits: {handDefault}</span> : null}
            </div>
          ) : null}
          {applied && isFinished(applied) ? (
            <div className="flex flex-col gap-1 rounded-control border border-default p-2 text-13" data-testid="apply-result" role="status">
              <div className="flex items-center gap-2">
                <span>Apply</span>
                <OutcomeBadge job={applied} />
                {applied.applyResult?.result ? (
                  <span className="text-12 text-secondary">
                    {applied.applyResult.result.filesWritten} written, {applied.applyResult.result.filesDeleted} deleted
                  </span>
                ) : null}
              </div>
              {applyOutcome === "stale" ? (
                <div className="text-12">
                  <p>The model or a planned file changed since planning; nothing was written.</p>
                  <ul className="font-mono text-11 text-secondary">
                    {applied.applyResult?.staleUnits.slice(0, 10).map((u) => (
                      <li key={u}>unit {u}</li>
                    ))}
                    {applied.applyResult?.stalePaths.slice(0, 10).map((p) => (
                      <li key={p}>path {p}</li>
                    ))}
                  </ul>
                  <Button size="sm" className="mt-1" onClick={() => void startPlan()}>
                    Re-plan
                  </Button>
                </div>
              ) : null}
              {applyOutcome === "invalid" ? (
                <ul className="text-12 text-danger">
                  {applied.applyResult?.result?.diagnostics.slice(0, 10).map((d, i) => (
                    <li key={i}>
                      {d.rule} {d.message}
                    </li>
                  ))}
                </ul>
              ) : null}
              {applyOutcome === "conflicts" ? (
                <p className="text-12">Some files were edited by hand; resolve them or plan with hand edits set to overwrite or skip.</p>
              ) : null}
            </div>
          ) : null}
          {generation.planId && plan.data ? (
            <>
              <SectionTitle>Plan summary</SectionTitle>
              <div className="flex flex-wrap gap-2" data-testid="plan-summary">
                {[...counts.byKind.entries()].map(([k, n]) => (
                  <Badge key={k} tone={KIND_TONE[k as FileChangeKind] ?? "neutral"}>
                    {k} {n}
                  </Badge>
                ))}
                {[...counts.byPack.entries()].map(([p, n]) => (
                  <Badge key={p}>
                    {p}: {n} changed
                  </Badge>
                ))}
              </div>
              {plan.data.diagnostics.length ? (
                <ul className="text-12 text-warning">
                  {plan.data.diagnostics.slice(0, 5).map((d, i) => (
                    <li key={i}>
                      {d.rule} {d.message}
                    </li>
                  ))}
                </ul>
              ) : null}
              <ChangesTable planId={generation.planId} changes={plan.data.changes} />
            </>
          ) : generation.planJob && !planJob.data ? (
            <Spinner label="Starting" />
          ) : !running ? (
            <EmptyState title="No plan yet">Choose the packs and roots, then Plan. The plan is a dry run: nothing is written until you apply it.</EmptyState>
          ) : null}
        </div>
        <aside className="flex min-h-0 flex-col border-l border-default bg-surface" aria-label="Run history">
          <div className="flex h-9 items-center px-3 text-12 font-semibold">Run history</div>
          <ol className="min-h-0 flex-1 overflow-auto" data-testid="history">
            {(history.data ?? []).map((job) => (
              <li key={job.id} className="flex items-center gap-2 border-t border-default px-3 py-1.5 text-12">
                <span className="w-10 font-semibold">{job.kind}</span>
                <OutcomeBadge job={job} />
                <time className="ml-auto text-secondary">{job.queuedUtc.slice(11, 19)}</time>
              </li>
            ))}
            {!history.data?.length ? <li className="px-3 text-12 text-secondary">No runs yet.</li> : null}
          </ol>
        </aside>
      </div>
    </div>
  );
}
