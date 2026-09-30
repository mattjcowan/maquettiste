// A diagnostic's quick fix in the Problems panel (phase-3-design.md 3): the button names the fix; Set initial asks
// which child (the first one preselected), Sync enum shows its dry run before applying; every other fix applies at
// once. Each is one batch and one undo step, and the report re-validates after it.
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Wrench } from "lucide-react";
import { useElement } from "@/api/queries";
import type { Diagnostic, ModelJson } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Select } from "@/components/ui/input";
import { useServices } from "@/app/context";
import { syncPlanLines } from "@/model/process";
import { applySyncEnum, syncEnum, type SyncEnumResult } from "@/editors/process/api";
import { deriveQuickFix, fixDocuments, type QuickFix } from "./quickFix";
import { applyQuickFix, freshQuickFix } from "./applyQuickFix";

type Pending =
  { kind: "initial"; fix: Extract<QuickFix, { kind: "operation" }>; target: string } | { kind: "sync"; process: string; plan: SyncEnumResult } | null;

export function QuickFixButton({ diagnostic, catalogFix }: { diagnostic: Diagnostic; catalogFix: string | null | undefined }) {
  const { store, drafts } = useServices();
  const qc = useQueryClient();
  const element = useElement(diagnostic.elementId);
  const related = fixDocuments(diagnostic, element.data?.json as ModelJson | undefined)[1];
  const partner = useElement(related);
  const [pending, setPending] = useState<Pending>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const docs = new Map<string, ModelJson>();
  if (element.data && diagnostic.elementId) docs.set(diagnostic.elementId, element.data.json as ModelJson);
  if (partner.data && related) docs.set(related, partner.data.json as ModelJson);
  const shown = deriveQuickFix(diagnostic, catalogFix, (id) => docs.get(id));
  if (!shown) return null;

  const report = (message: string | null, done: string) => {
    if (message) store.getState().notify(`${shown.label}: ${message}`, "error");
    else store.getState().notify(done);
  };
  const run = async (task: () => Promise<void>) => {
    setBusy(true);
    try {
      await task();
    } catch (e) {
      store.getState().notify(`${shown.label}: ${e instanceof Error ? e.message : String(e)}`, "error");
    } finally {
      setBusy(false);
    }
  };
  const start = () =>
    run(async () => {
      setError(null);
      const fix = await freshQuickFix(drafts, diagnostic, catalogFix);
      if (!fix) return report("The problem is no longer there.", "");
      if (fix.kind === "sync-enum") return setPending({ kind: "sync", process: fix.process, plan: await syncEnum(fix.process, true) });
      if (fix.kind === "operation" && fix.choices && fix.choices.length > 1)
        return setPending({ kind: "initial", fix, target: fix.target ?? fix.choices[0].id });
      report(await applyQuickFix(qc, store, fix), `${fix.label}: done.`);
    });
  const confirm = () =>
    run(async () => {
      if (pending?.kind === "initial") {
        const message = await applyQuickFix(qc, store, pending.fix, pending.target);
        if (message) return setError(message);
      } else if (pending?.kind === "sync") {
        if (!pending.plan.enum) return setError("The process has no bound enum.");
        const result = await applySyncEnum(qc, store, pending.process, pending.plan.enum);
        if (!result.applied) return setError(result.diagnostics[0]?.message ?? "The sync was refused.");
      }
      store.getState().notify(`${shown.label}: done.`);
      setPending(null);
    });
  const plan = pending?.kind === "sync" ? pending.plan : null;
  const nothing = plan && !plan.added.length && !plan.removed.length && !plan.reordered;
  return (
    <>
      <Button
        size="sm"
        variant="ghost"
        className="mr-2 shrink-0"
        disabled={busy}
        data-testid={`problem-fix-${diagnostic.rule}`}
        title={`Quick fix: ${shown.label}`}
        onClick={() => void start()}
      >
        <Wrench /> {shown.label}
      </Button>
      <Dialog open={pending !== null} onOpenChange={(o) => !o && setPending(null)}>
        {pending?.kind === "initial" ? (
          <DialogContent title="Set initial" description="The state this one starts in when it is entered.">
            <Field label="Initial state" htmlFor="quick-fix-initial">
              <Select
                id="quick-fix-initial"
                data-testid="quick-fix-initial"
                value={pending.target}
                onChange={(e) => setPending({ ...pending, target: e.target.value })}
              >
                {pending.fix.choices?.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Footer error={error} busy={busy} disabled={false} onCancel={() => setPending(null)} onApply={() => void confirm()} />
          </DialogContent>
        ) : plan ? (
          <DialogContent title="Sync enum" description="The bound enum's members become the lifecycle's states, in order.">
            <ul className="flex flex-col gap-0.5 text-12" data-testid="quick-fix-plan">
              {syncPlanLines(plan).map((l) => (
                <li key={l}>{l}</li>
              ))}
            </ul>
            <Footer
              error={error ?? plan.diagnostics[0]?.message ?? null}
              busy={busy}
              disabled={!!nothing || !!plan.diagnostics.length}
              onCancel={() => setPending(null)}
              onApply={() => void confirm()}
            />
          </DialogContent>
        ) : null}
      </Dialog>
    </>
  );
}

function Footer(props: { error: string | null; busy: boolean; disabled: boolean; onCancel: () => void; onApply: () => void }) {
  return (
    <>
      {props.error ? (
        <p role="alert" className="text-12 text-danger">
          {props.error}
        </p>
      ) : null}
      <div className="flex justify-end gap-2">
        <Button onClick={props.onCancel}>Cancel</Button>
        <Button variant="primary" disabled={props.busy || props.disabled} onClick={props.onApply} data-testid="quick-fix-apply">
          Apply
        </Button>
      </div>
    </>
  );
}
