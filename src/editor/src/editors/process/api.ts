// The process editor's operations (phase-3-design.md 4.4) over the generated client: the Sync enum plan and apply,
// verify one scenario (Replay), and Refresh expectations (the `refresh-scenario` batch operation). Each change is one
// undo step: the documents before and after the operation.
import type { QueryClient } from "@tanstack/react-query";
import { api, ApiProblem } from "@/api/client";
import type { components } from "@/api/schema";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, applySaveResult, keys } from "@/api/queries";
import type { ModelJson } from "@/api/types";
import { recordVerify, type VerifyResult } from "@/explorer/processApi";
import { applyQuickFix } from "@/problems/applyQuickFix";
import type { EditorStore } from "@/state/store";

export type SyncEnumResult = components["schemas"]["SyncEnumResult"];

/** The sync-enum plan (dry run) or its application; a 422 refusal answers with the result and its diagnostics. */
export async function syncEnum(process: string, dryRun: boolean): Promise<SyncEnumResult> {
  const { data, error, response } = await api().POST("/api/processes/{id}/sync-enum", { params: { path: { id: process } }, body: { dryRun } });
  if (data) return data;
  if (error && typeof error === "object" && "added" in error) return error as SyncEnumResult;
  throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
}

/** Verifies one scenario (Replay); the explorer's statuses follow. */
export async function verifyScenario(process: string, scenario: string): Promise<VerifyResult> {
  const { data, response } = await api().POST("/api/processes/{id}/verify", { params: { path: { id: process } }, body: { scenarios: [scenario] } });
  if (!data) throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
  recordVerify(data);
  return data;
}

async function refetch(qc: QueryClient, id: string) {
  const doc = await endpoints.getElement(id);
  qc.setQueryData(keys.element(id), doc);
  return doc;
}

/** Applies the Sync enum plan and records it as one undo step on the enum. */
export async function applySyncEnum(qc: QueryClient, store: EditorStore, process: string, enumId: string): Promise<SyncEnumResult> {
  const before = await endpoints.getElement(enumId);
  const result = await syncEnum(process, false);
  if (!result.applied) return result;
  const after = await refetch(qc, enumId);
  store
    .getState()
    .pushUndo({ label: "Sync enum", ids: [enumId], before: [before.json as ModelJson], after: [after.json as ModelJson], afterHashes: [after.hash] });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return result;
}

/** Rewrites a scenario's expectations from a replay (refresh-scenario); null when saved, else the refusal. */
export async function refreshScenario(qc: QueryClient, store: EditorStore, scenario: string): Promise<string | null> {
  const before = await endpoints.getElement(scenario);
  const result = await endpoints.applyBatch({ operations: [{ op: "refresh-scenario", id: scenario }] } as never);
  if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? "The batch did not parse.";
  if (result.outcome !== "saved") return result.items.find((i) => i.outcome !== "saved")?.diagnostics[0]?.message ?? result.outcome;
  applyBatchResult(qc, result);
  const after = await refetch(qc, scenario);
  store.getState().pushUndo({
    label: "Refresh expectations",
    ids: [scenario],
    before: [before.json as ModelJson],
    after: [after.json as ModelJson],
    afterHashes: [after.hash],
  });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return null;
}

/** Deletes a scenario (nothing refers to one), as one undo step. */
export async function deleteScenario(qc: QueryClient, store: EditorStore, scenario: string, name: string): Promise<string | null> {
  const doc = qc.getQueryData<{ hash: string; json: ModelJson }>(keys.element(scenario)) ?? (await endpoints.getElement(scenario));
  const result = await endpoints.deleteElement(scenario, doc.hash, "refuse");
  if (result.outcome !== "saved") return result.diagnostics[0]?.message ?? `The delete was refused (${result.outcome}).`;
  applySaveResult(qc, result);
  store.getState().pushUndo({ label: `Delete ${name}`, ids: [scenario], before: [doc.json], after: [null], afterHashes: [null] });
  return null;
}

/**
 * Binds a process as an entity's lifecycle, or clears the entity's lifecycle (no `target`), with the `set-lifecycle`
 * batch operation: both sides and the previous partners change in one batch and one undo step (the Use and Subject
 * fields). Pending edits of the documents it reads are saved first. Null when saved, else the refusal's message.
 */
export async function setLifecycle(
  qc: QueryClient,
  store: EditorStore,
  drafts: { flush(id: string): Promise<unknown> },
  change: { process: string; entity: string; target?: string; label: string },
): Promise<string | null> {
  await drafts.flush(change.process);
  await drafts.flush(change.entity);
  const process = (await endpoints.getElement(change.process)).json as Record<string, unknown>;
  const entity = (await endpoints.getElement(change.entity)).json as Record<string, unknown>;
  const str = (v: unknown) => (typeof v === "string" ? v : null);
  const ids = [...new Set([change.process, change.entity, str(entity.lifecycle), str(process.subject)].filter((x): x is string => !!x))];
  for (const id of ids.slice(2)) await drafts.flush(id);
  return applyQuickFix(qc, store, { kind: "operation", op: "set-lifecycle", id: change.entity, target: change.target, label: change.label, ids });
}
