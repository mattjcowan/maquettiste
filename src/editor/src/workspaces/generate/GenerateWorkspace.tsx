// The Generate workspace (phase2-design.md 4.8): pack and root selection, then Plan with live
// progress and Cancel; the plan summarized per pack and its changes grouped by unit with the reason each renders
// (PlanExplain.tsx, generation-ui.md 4), filtered by kind, pack, unit and text, hand edits and conflicts flagged; selecting a file opens its
// diff in the bottom panel. Apply queues the plan by id; the result is the apply job's
// applyResult.outcome, never its state. Run history lists GET /api/jobs.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { CircleAlert, CircleCheck, Play, Square, Wand2 } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { keys, useJob, useJobs, usePlan, useProject, useSettings } from "@/api/queries";
import type { JobInfo, RootSelection } from "@/api/types";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";
import { isFinished, jobOutcome } from "@/realtime/jobs";
import { Button, iconLabel } from "@/components/ui/button";
import { Badge, EmptyState, SectionTitle, Spinner, Toolbar } from "@/components/ui/misc";
import { Checkbox } from "@/components/ui/checkbox";
import { Select } from "@/components/ui/input";
import { cn } from "@/lib/cn";
import { X } from "lucide-react";
import { PackEditor } from "./PackEditor";
import { ExplainForm, PlanChanges, type ExplainAsk, PlanSummary, UnchangedUnits, WhyPanel } from "./PlanExplain";
import { closePackTab } from "./packTabs";
import { moreNotesText, nothingToWrite, orderDiagnostics, type PlanNote } from "./planModel";
import { discardDrafts, hasUnsaved } from "./drafts";

/** Kinds an apply leaves alone: an unchanged file, and a companion that is kept as it is on disk. */
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

function PlanScreen() {
  const { store, jobs } = useServices();
  const qc = useQueryClient();
  const project = useProject();
  const settings = useSettings();
  const [ask, setAsk] = useState<ExplainAsk | null>(null);
  const generation = useEditor(store, (s) => s.generation);
  const planJob = useJob(generation.planJob);
  const applyJob = useJob(generation.applyJob);
  const plan = usePlan(generation.planId);
  const history = useJobs();
  const packs = useMemo(() => project.data?.packs ?? [], [project.data]);
  const enabled = useMemo(() => packs.filter((p) => project.data?.settings.packs[p.name]?.enabled !== false).map((p) => p.name), [packs, project.data]);
  // The ticked packs live in the store, so the page state (state/pageState.ts) keeps them across a reload.
  const chosen = generation.chosenPacks;
  const setChosen = (next: string[]) => store.getState().setGeneration({ chosenPacks: next });
  const [roots, setRoots] = useState<RootSelection>("all");
  const [busy, setBusy] = useState(false);
  const selectedPacks = chosen ? chosen.filter((name) => packs.some((p) => p.name === name)) : enabled;
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

  const applied = applyJob.data;
  const applyOutcome = applied && isFinished(applied) ? jobOutcome(applied) : null;
  const handDefault = settings.data?.settings.handEdits;

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="plan-screen">
      <Toolbar label="Generation">
        <Wand2 className="size-4 text-secondary" aria-hidden />
        <span className="text-13 font-semibold">Generate</span>
        <fieldset className="ml-4 flex items-center gap-2">
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
            disabled={busy || !!running || !generation.planId || (plan.data ? nothingToWrite(plan.data) : false)}
            title={plan.data && nothingToWrite(plan.data) ? "Nothing to apply: every planned file already matches the disk" : "Write the planned files"}
            onClick={() => void startApply()}
            data-testid="apply"
          >
            Apply plan
          </Button>
        </div>
      </Toolbar>
      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_340px] gap-0">
        <div className="flex min-h-0 flex-col gap-1.5 overflow-hidden p-2">
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
              <PlanSummary plan={plan.data} />
              {plan.data.diagnostics.length ? <PlanNotes diagnostics={plan.data.diagnostics} /> : null}
              <PlanChanges planId={generation.planId} plan={plan.data} onExplain={(a) => setAsk((prev) => ({ ...a, seq: (prev?.seq ?? 0) + 1 }))} />
            </>
          ) : generation.planJob && !planJob.data ? (
            <Spinner label="Starting" />
          ) : !running ? (
            <EmptyState title="No plan yet">Choose the packs and roots, then Plan. The plan is a dry run: nothing is written until you apply it.</EmptyState>
          ) : null}
        </div>
        <aside className="flex min-h-0 flex-col overflow-auto border-l border-default bg-surface" aria-label="Plan explanation and run history">
          {generation.planId && plan.data ? (
            <>
              <WhyPanel planId={generation.planId} plan={plan.data} />
              <UnchangedUnits planId={generation.planId} plan={plan.data} />
            </>
          ) : null}
          <ExplainForm planId={generation.planId} plan={plan.data ?? null} ask={ask} />
          <h3 className="flex h-6 shrink-0 items-center px-2 text-12 font-semibold">Run history</h3>
          <ol className="shrink-0" data-testid="history">
            {(history.data ?? []).map((job) => (
              <li key={job.id} className="flex h-6 items-center gap-2 border-t border-default px-2 text-12">
                <span className="w-10 font-semibold">{job.kind}</span>
                <OutcomeBadge job={job} />
                <time className="ml-auto text-secondary">{job.queuedUtc.slice(11, 19)}</time>
              </li>
            ))}
            {!history.data?.length ? <li className="px-2 text-12 text-secondary">No runs yet.</li> : null}
          </ol>
        </aside>
      </div>
    </div>
  );
}

/** The Generate screen: the Plan tab and one centre tab per open pack (generation-ui.md 3). */
export function GenerateWorkspace() {
  const { store } = useServices();
  const tabs = useEditor(store, (s) => s.generation.packTabs);
  const active = useEditor(store, (s) => s.generation.packTab);
  const show = (pack: string | null) => store.getState().setGeneration({ packTab: pack });
  const strip = useRef<HTMLDivElement>(null);
  // Unsaved edits survive tab switches (drafts.ts); a reload or a closed browser tab still loses them, so warn.
  useEffect(() => {
    const onUnload = (e: BeforeUnloadEvent) => {
      if (hasUnsaved()) e.preventDefault();
    };
    window.addEventListener("beforeunload", onUnload);
    return () => window.removeEventListener("beforeunload", onUnload);
  }, []);
  const close = (pack: string) => {
    if (hasUnsaved(pack) && !window.confirm(`Discard the unsaved changes in ${pack}?`)) return;
    discardDrafts(pack);
    store.getState().setGeneration(closePackTab(store.getState().generation, pack));
  };
  const order: (string | null)[] = [null, ...tabs];
  // The tab strip is one Tab stop: arrows, Home and End move between tabs; Delete closes a pack tab.
  const onTabKey = (e: KeyboardEvent, at: number) => {
    const to =
      e.key === "ArrowRight"
        ? (at + 1) % order.length
        : e.key === "ArrowLeft"
          ? (at - 1 + order.length) % order.length
          : e.key === "Home"
            ? 0
            : e.key === "End"
              ? order.length - 1
              : -1;
    if (to >= 0) {
      e.preventDefault();
      show(order[to]);
      requestAnimationFrame(() => strip.current?.querySelectorAll<HTMLElement>('[role="tab"]')[to]?.focus());
    } else if (e.key === "Delete" && order[at]) {
      e.preventDefault();
      close(order[at]!);
    }
  };
  const tab = (selected: boolean) =>
    cn(
      "flex h-full items-center px-2 text-12 hover:bg-accent-subtle",
      selected ? "bg-app font-medium text-primary shadow-[inset_0_-2px_0_var(--mq-accent)]" : "text-secondary",
    );
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="generate-workspace">
      <div
        ref={strip}
        role="tablist"
        aria-label="Generate tabs"
        className="flex h-7 shrink-0 items-stretch border-b border-default bg-surface"
        data-testid="generate-tabs"
      >
        <button
          type="button"
          role="tab"
          aria-selected={active === null}
          tabIndex={active === null ? 0 : -1}
          className={cn(tab(active === null), "border-r border-default")}
          onClick={() => show(null)}
          onKeyDown={(e) => onTabKey(e, 0)}
        >
          Plan
        </button>
        {tabs.map((pack, i) => (
          <div key={pack} className="flex items-stretch border-r border-default">
            <button
              type="button"
              role="tab"
              aria-selected={active === pack}
              aria-keyshortcuts="Delete"
              tabIndex={active === pack ? 0 : -1}
              className={tab(active === pack)}
              onClick={() => show(pack)}
              onKeyDown={(e) => onTabKey(e, i + 1)}
              data-testid={`pack-tab-${pack}`}
            >
              {pack}
            </button>
            <button
              type="button"
              tabIndex={-1}
              {...iconLabel(`Close ${pack}`, "Delete")}
              className="grid size-6 place-items-center self-center rounded-[4px] text-secondary hover:bg-accent-subtle"
              onClick={() => close(pack)}
            >
              <X className="size-3" />
            </button>
          </div>
        ))}
      </div>
      <div className="min-h-0 flex-1">{active ? <PackEditor key={active} pack={active} /> : <PlanScreen />}</div>
    </div>
  );
}

// The plan's diagnostics in their own severity: an error is red, a warning amber, a note (info, such as MQ7204's
// incomplete-locale counts) plain; errors first, five listed, the rest counted.
const NOTE_TONE: Record<string, string> = { error: "text-danger", warning: "text-warning", info: "text-secondary" };
const NOTE_BADGE: Record<string, "danger" | "warning" | "neutral"> = { error: "danger", warning: "warning", info: "neutral" };

function PlanNotes({ diagnostics }: { diagnostics: readonly PlanNote[] }) {
  const { shown, more, counts } = orderDiagnostics(diagnostics);
  return (
    <ul className="text-12" data-testid="plan-notes">
      {shown.map((d, i) => (
        <li key={i} className={NOTE_TONE[d.severity] ?? "text-secondary"} data-severity={d.severity}>
          <Badge tone={NOTE_BADGE[d.severity] ?? "neutral"}>{d.rule}</Badge> {d.message}
        </li>
      ))}
      {more ? <li className="text-secondary">{moreNotesText(more, counts)}</li> : null}
    </ul>
  );
}
