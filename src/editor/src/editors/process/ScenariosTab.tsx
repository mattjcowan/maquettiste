// The Scenarios tab (phase-3-design.md 6.2): the process's scenarios with their steps and last status (passed,
// failed at step n, not run), and per row Replay (verify this one), Open (its editor), Refresh expectations (the
// refresh-scenario operation) and Delete.
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { ExternalLink, Play, RefreshCw } from "lucide-react";
import { useIndex, useValidation } from "@/api/queries";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/misc";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { useScenarioStatuses } from "@/explorer/processApi";
import { indexLookup } from "@/model/index";
import { PROCESS_LABELS } from "@/model/labels";
import { scenarioRows, type ScenarioRow } from "@/model/process";
import { Grid, type GridColumn } from "./Grid";
import { useGridTargets } from "./shared";
import { deleteScenario, refreshScenario, verifyScenario } from "./api";

export function ScenarioStatusBadge({ status, failedAt }: { status: ScenarioRow["status"]; failedAt: number | null }) {
  return status === "passed" ? (
    <Badge tone="success" data-testid="scenario-status" data-status="passed">
      {PROCESS_LABELS.passed}
    </Badge>
  ) : status === "failed" ? (
    <Badge tone="danger" data-testid="scenario-status" data-status="failed">
      {PROCESS_LABELS.failedAt(failedAt ?? 0)}
    </Badge>
  ) : (
    <Badge data-testid="scenario-status" data-status="not-run">
      {PROCESS_LABELS.notRun}
    </Badge>
  );
}

export function ScenariosTab({ process, name, focus }: { process: string; name: string; focus: string | null }) {
  const { store } = useServices();
  const qc = useQueryClient();
  const index = useIndex();
  const go = useGridTargets(process);
  const nav = useEditorNavigation();
  const statuses = useScenarioStatuses();
  const report = useValidation().data?.diagnostics ?? [];
  const [busy, setBusy] = useState<string | null>(null);
  const lookup = indexLookup(index.data);
  const summaries = lookup.ofKind("scenario").filter((s) => s.process === process);
  const rows = scenarioRows(summaries, statuses);
  const run = async (row: ScenarioRow, what: string, action: () => Promise<string | null>) => {
    setBusy(row.id);
    try {
      const error = await action();
      if (error) store.getState().notify(`${what} ${row.name}: ${error}`, "error");
    } catch (e) {
      store.getState().notify(`${what} ${row.name}: ${e instanceof Error ? e.message : String(e)}`, "error");
    } finally {
      setBusy(null);
    }
  };
  const columns: GridColumn<ScenarioRow>[] = [
    { key: "name", label: "Name", kind: "readonly", width: "min-w-40", value: (r) => r.name, definition: (r) => go.element(r.id) },
    { key: "steps", label: "Steps", kind: "readonly", width: "w-14", value: (r) => String(r.steps) },
    {
      key: "status",
      label: "Status",
      kind: "readonly",
      width: "w-32",
      value: (r) => r.status,
      render: (r) => <ScenarioStatusBadge status={r.status} failedAt={r.failedAt} />,
    },
  ];
  const open = (row: ScenarioRow) => {
    const summary = lookup.byId.get(row.id);
    if (summary) nav.openEditor(summary, true);
  };
  return (
    <Grid
      label={`Scenarios of ${name}`}
      testid="scenarios-grid"
      noun="scenario"
      rows={rows}
      columns={columns}
      rowName={(r) => r.name}
      selected={focus}
      problems={(r) => report.filter((d) => d.elementId === r.id)}
      empty="No scenarios yet: add one from the explorer (New scenario…)."
      onKey={(e, row) => {
        if (e.ctrlKey || e.metaKey || e.altKey) return false;
        // Enter opens, R replays, Shift+R refreshes the expectations (the row's buttons, from the keyboard).
        const key = e.key === "Enter" ? "open" : e.key.toLowerCase() === "r" ? (e.shiftKey ? "refresh" : "replay") : null;
        if (!key) return false;
        e.preventDefault();
        if (key === "open") open(row);
        else if (busy === row.id) return true;
        else if (key === "replay") void run(row, "Replay", async () => (await verifyScenario(process, row.id), null));
        else void run(row, "Refresh expectations of", () => refreshScenario(qc, store, row.id));
        return true;
      }}
      onRemove={(row) => void run(row, "Delete", () => deleteScenario(qc, store, row.id, row.name))}
      actions={(row) => (
        <>
          <Button
            variant="ghost"
            size="icon-row"
            tabIndex={-1}
            label={`Replay ${row.name}`}
            shortcut="R"
            disabled={busy === row.id}
            data-testid="scenario-replay"
            onClick={() => void run(row, "Replay", async () => (await verifyScenario(process, row.id), null))}
          >
            <Play />
          </Button>
          <Button variant="ghost" size="icon-row" tabIndex={-1} label={`Open ${row.name}`} data-testid="scenario-open" onClick={() => open(row)}>
            <ExternalLink />
          </Button>
          <Button
            variant="ghost"
            size="icon-row"
            tabIndex={-1}
            label={`Refresh the expectations of ${row.name}`}
            shortcut="Shift+R"
            disabled={busy === row.id}
            data-testid="scenario-refresh"
            onClick={() => void run(row, "Refresh expectations of", () => refreshScenario(qc, store, row.id))}
          >
            <RefreshCw />
          </Button>
        </>
      )}
      hint="Enter opens · R replays · Shift+R refreshes the expectations · Ctrl+Delete deletes"
    />
  );
}
