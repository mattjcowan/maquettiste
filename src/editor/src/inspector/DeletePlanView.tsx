// Deleting with a plan (engine-design.md 15.1): the inspector's delete and the explorer's bulk delete read what a
// delete would do (GET /api/model/elements/{id}/delete-plan, POST /api/model/delete-plan) before it happens, list it
// grouped by outcome, and offer the two ways through: clear the references, or delete the dependents too. The
// delete itself is one request (one batch for several elements), and it is one undo step: the documents of every
// element it deletes or changes are read first, so undo re-creates and restores them in one batch.
import { useEffect, useState } from "react";
import type { QueryClient } from "@tanstack/react-query";
import { applyBatchResult, applySaveResult, keys, removeIndexRows } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { DeletePlan, DeleteResolution, ElementDocument, ElementKind, ModelJson, SaveResult } from "@/api/types";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Spinner } from "@/components/ui/misc";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { KindIcon } from "@/app/icons";
import { KIND_LABELS } from "@/model/model";
import { clone } from "@/lib/json";
import type { EditorStore } from "@/state/store";
import type { DraftManager } from "@/state/drafts";

/** The two plans a delete dialog offers. */
export interface DeletePlans {
  dependents: DeletePlan;
  clear: DeletePlan;
}

export async function loadDeletePlans(ids: string[]): Promise<DeletePlans> {
  const read = (resolution: DeleteResolution) =>
    ids.length === 1 ? endpoints.getDeletePlan(ids[0], resolution) : endpoints.getBulkDeletePlan(ids, resolution);
  const [dependents, clear] = await Promise.all([read("delete-dependents"), read("remove-references")]);
  return { dependents, clear };
}

/** Whether the delete touches nothing but the elements named: no dependents, nothing cleared, nothing blocked. */
export function deletesAlone(plans: DeletePlans): boolean {
  const p = plans.dependents;
  return (
    p.outcome === "saved" &&
    p.deletes.length === 0 &&
    p.removes.length === 0 &&
    p.clears.length === 0 &&
    p.settings.length === 0 &&
    plans.clear.outcome === "saved"
  );
}

const isKind = (kind: string | null | undefined): kind is ElementKind => !!kind && kind in KIND_LABELS;

function PlanRow({ kind, name, detail }: { kind: string | null | undefined; name: string; detail: string }) {
  return (
    <li className="flex w-full items-center gap-2 rounded-control px-2 py-1 text-left text-13">
      {isKind(kind) ? <KindIcon kind={kind} /> : null}
      <span className="flex-1 truncate">{name}</span>
      <span className="max-w-[60%] truncate text-11 text-secondary" title={detail}>
        {detail}
      </span>
    </li>
  );
}

function Group({ title, count, testId, children }: { title: string; count: number; testId: string; children: React.ReactNode }) {
  if (count === 0) return null;
  return (
    <section data-testid={testId}>
      <h3 className="px-2 text-11 font-semibold uppercase text-secondary">
        {title} ({count})
      </h3>
      <ul className="flex flex-col">{children}</ul>
    </section>
  );
}

/** A plan grouped by outcome: what is deleted, what loses a part, what loses a reference, what blocks it. */
export function DeletePlanList({ plan }: { plan: DeletePlan }) {
  const empty = !plan.deletes.length && !plan.removes.length && !plan.clears.length && !plan.refused.length && !plan.settings.length && !plan.warnings.length;
  if (empty) return <p className="px-2 text-12 text-secondary">Nothing else refers to it.</p>;
  return (
    <div className="flex max-h-72 flex-col gap-1 overflow-auto" data-testid="delete-plan">
      <Group title="Will be deleted" count={plan.deletes.length} testId="delete-plan-deletes">
        {plan.deletes.map((d) => (
          <PlanRow key={d.id} kind={d.kind} name={d.name} detail={d.because} />
        ))}
      </Group>
      <Group title="Removed from" count={plan.removes.length} testId="delete-plan-removes">
        {plan.removes.map((r) => (
          <PlanRow key={`${r.id}${r.pointer}`} kind={r.kind} name={r.name} detail={`${r.what}: ${r.because}`} />
        ))}
      </Group>
      <Group title="Reference cleared" count={plan.clears.length} testId="delete-plan-clears">
        {plan.clears.map((c) => (
          <PlanRow key={`${c.id}${c.pointer}`} kind={c.kind} name={c.name} detail={`${c.field}: ${c.because}`} />
        ))}
      </Group>
      <Group title="Settings entry removed" count={plan.settings.length} testId="delete-plan-settings">
        {plan.settings.map((s) => (
          <PlanRow key={s.pointer} kind={null} name="maquettiste.json" detail={s.what} />
        ))}
      </Group>
      <Group title="Cannot be resolved" count={plan.refused.length} testId="delete-plan-refused">
        {plan.refused.map((r, i) => (
          <PlanRow key={i} kind={r.kind} name={r.name ?? r.id ?? ""} detail={r.why} />
        ))}
      </Group>
      <Group title="Stops matching" count={plan.warnings.length} testId="delete-plan-warnings">
        {plan.warnings.map((w, i) => (
          <PlanRow key={i} kind={null} name={w.pack ? `${w.pack} ${w.unit ?? ""}` : "Warning"} detail={w.message} />
        ))}
      </Group>
    </div>
  );
}

export interface DeleteDeps {
  queryClient: QueryClient;
  drafts: DraftManager;
  store: EditorStore;
}

async function readAll(ids: string[]): Promise<Map<string, ElementDocument>> {
  const out = new Map<string, ElementDocument>();
  for (let i = 0; i < ids.length; i += endpoints.MAX_READ_IDS) {
    const result = await endpoints.readElements(ids.slice(i, i + endpoints.MAX_READ_IDS));
    for (const doc of result.elements) out.set(String((doc.json as { id: string }).id), doc);
  }
  return out;
}

const unique = (ids: string[]) => [...new Set(ids)];

/**
 * Deletes `ids` with `resolution` as one change and pushes one undo step that restores every element it deleted
 * or changed. Returns null when deleted, else the reason in words (the engine's readable diagnostic first).
 */
export async function runPlannedDelete(
  deps: DeleteDeps,
  args: { ids: string[]; resolution: DeleteResolution; plan: DeletePlan | null; label: string },
): Promise<string | null> {
  const { queryClient: qc, drafts, store } = deps;
  for (const id of args.ids) await drafts.flush(id);
  const plan = args.plan;
  const touched = unique([
    ...args.ids,
    ...(plan?.deletes.map((d) => d.id) ?? []),
    ...(plan?.removes.map((r) => r.id) ?? []),
    ...(plan?.clears.map((c) => c.id) ?? []),
  ]);
  const before = await readAll(touched);
  const missing = args.ids.filter((id) => !before.has(id));
  if (missing.length) return `${missing.join(", ")} no longer exists.`;

  // The Database screen stops showing a database the plan deletes before the delete is sent: the model.changed event
  // can arrive before the response, and its refetch of the deleted database would answer 404. The database's index
  // row goes too, or the screen would open it again as the first database. Both come back on failure.
  const going = [...args.ids, ...(plan?.deletes.map((d) => d.id) ?? [])];
  const active = store.getState().activeDatabase;
  const leaving = active !== null && going.includes(active);
  if (leaving) {
    removeIndexRows(qc, [active]);
    store.getState().setActiveDatabase(null);
  }
  for (const id of going) qc.removeQueries({ queryKey: keys.databaseView(id) });
  const failed = (reason: string) => {
    if (leaving) {
      store.getState().setActiveDatabase(active);
      void qc.invalidateQueries({ queryKey: keys.index });
    }
    return reason;
  };

  let changes: SaveResult["changes"] = null;
  if (args.ids.length === 1) {
    const result = await endpoints.deleteElement(args.ids[0], before.get(args.ids[0])!.hash, args.resolution);
    if (result.outcome !== "saved") {
      const conflict = result.outcome === "conflict" ? "It changed since it was read." : result.outcome;
      return failed(result.diagnostics[0]?.message ?? (result.referrers.length ? "It is still referenced." : conflict));
    }
    applySaveResult(qc, result);
    changes = result.changes;
  } else {
    const result = await endpoints.applyBatch({
      operations: args.ids.map((id) => ({ op: "delete" as const, id, expectedHash: before.get(id)!.hash, resolution: args.resolution })),
    });
    if (!endpoints.isBatchResult(result)) return failed(result.diagnostics[0]?.message ?? "The delete was refused.");
    if (result.outcome !== "saved") {
      const item = result.items.find((i) => i.outcome !== "saved");
      return failed(item?.diagnostics[0]?.message ?? (item?.referrers.length ? "Still referenced." : result.outcome));
    }
    applyBatchResult(qc, result);
    changes = result.changes;
  }

  const deleted = changes?.deleted ?? args.ids;
  const changed = (changes?.changed ?? []).filter((c) => !deleted.includes(c.id));
  const after = await readAll(changed.map((c) => c.id));
  for (const doc of after.values()) {
    const id = String((doc.json as { id: string }).id);
    applySaveResult(qc, { outcome: "saved", id, hash: doc.hash, current: doc, diagnostics: [], referrers: [], changes: null });
  }
  for (const id of deleted) drafts.discard(id);

  const s = store.getState();
  const ids = [...deleted, ...changed.map((c) => c.id)];
  if (ids.every((id) => before.has(id)))
    s.pushUndo({
      label: args.label,
      ids,
      before: ids.map((id) => clone(before.get(id)!.json as ModelJson)),
      after: [...deleted.map(() => null), ...changed.map((c) => (after.has(c.id) ? clone(after.get(c.id)!.json as ModelJson) : null))],
      afterHashes: [...deleted.map(() => null), ...changed.map((c) => c.hash)],
    });
  else s.notify(`${args.label}: done. Some of what it changed was not read first, so it cannot be undone from the editor.`);
  s.select(s.selection.filter((x) => !deleted.includes(x)));
  return null;
}

/**
 * The delete dialog: both plans, one shown at a time ("With dependents" or "Clear references only"), and the
 * buttons for each way through. With nothing else involved it is a plain confirmation.
 */
export function DeletePlanDialog({
  ids,
  title,
  preloaded,
  onClose,
  onDelete,
}: {
  ids: string[] | null;
  title: string;
  preloaded?: DeletePlans | null;
  onClose: () => void;
  /** Runs the delete with the chosen resolution; the dialog closes first and the caller reports the result. */
  onDelete: (resolution: DeleteResolution, plan: DeletePlan) => void;
}) {
  const [plans, setPlans] = useState<DeletePlans | null>(preloaded ?? null);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<"dependents" | "clear">("dependents");
  const key = ids?.join(",") ?? "";
  useEffect(() => {
    setView("dependents");
    setError(null);
    if (!key) return setPlans(null);
    if (preloaded) return setPlans(preloaded);
    let live = true;
    setPlans(null);
    loadDeletePlans(key.split(",")).then(
      (p) => live && setPlans(p),
      (e: unknown) => live && setError(e instanceof Error ? e.message : String(e)),
    );
    return () => {
      live = false;
    };
  }, [key, preloaded]);

  const alone = plans ? deletesAlone(plans) : false;
  // Dependents: the elements deleted with it and the parts (attributes, members, keys) removed from others.
  const dependents = (plans?.dependents.deletes.length ?? 0) + (plans?.dependents.removes.length ?? 0);
  // With no dependents both ways through clear the same references: one list, one button.
  const choice = !!plans && !alone && dependents > 0;
  const shown = plans ? (view === "dependents" && choice ? plans.dependents : plans.clear) : null;
  const description = !plans
    ? "Reading what the delete would change…"
    : alone
      ? "Nothing else refers to it. You can undo the delete."
      : "Other elements refer to it. Choose what happens to them; the whole delete is one change you can undo.";
  return (
    <Dialog open={!!ids} onOpenChange={(open) => !open && onClose()}>
      {ids ? (
        <DialogContent title={title} description={description}>
          <div data-testid="delete-plan-dialog" className="flex flex-col gap-2">
            {error ? <p className="text-12 text-danger">{error}</p> : null}
            {!plans && !error ? <Spinner label="Reading the delete plan" /> : null}
            {choice ? (
              <Tabs value={view} onValueChange={(v) => setView(v as "dependents" | "clear")}>
                <TabsList aria-label="What happens to what refers to it">
                  <TabsTrigger value="dependents" data-testid="delete-plan-tab-dependents">
                    With dependents
                  </TabsTrigger>
                  <TabsTrigger value="clear" data-testid="delete-plan-tab-clear">
                    Clear references only
                  </TabsTrigger>
                </TabsList>
              </Tabs>
            ) : null}
            {shown && !alone ? <DeletePlanList plan={shown} /> : null}
            {plans && !alone && (view === "clear" || !choice) && plans.clear.outcome !== "saved" ? (
              <p className="text-12 text-secondary">Clearing references alone cannot work here: the rows under Cannot be resolved need what you delete.</p>
            ) : null}
            <div className="flex flex-wrap justify-end gap-2">
              <Button onClick={onClose}>Keep it</Button>
              {plans && alone ? (
                <Button variant="danger" onClick={() => onDelete("refuse", plans.dependents)} data-testid="delete-plain">
                  Delete
                </Button>
              ) : null}
              {plans && !alone ? (
                <>
                  <Button
                    variant="danger"
                    disabled={plans.clear.outcome !== "saved"}
                    title={plans.clear.outcome !== "saved" ? "Some references are required: see Clear references only" : undefined}
                    onClick={() => onDelete("remove-references", plans.clear)}
                    data-testid="delete-clear-references"
                  >
                    Delete and clear references
                  </Button>
                  {choice ? (
                    <Button
                      variant="danger"
                      disabled={plans.dependents.outcome !== "saved"}
                      onClick={() => onDelete("delete-dependents", plans.dependents)}
                      data-testid="delete-with-dependents"
                    >
                      {dependents === 1 ? "Delete with 1 dependent" : `Delete with ${dependents} dependents`}
                    </Button>
                  ) : null}
                </>
              ) : null}
            </div>
          </div>
        </DialogContent>
      ) : null}
    </Dialog>
  );
}
