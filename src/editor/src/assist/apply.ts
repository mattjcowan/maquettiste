// Applies an assistant proposal as one batch and one undo step (erratum E44): the documents it touches are read before and
// after it, the caches follow, and a document that changed since the proposal refuses the apply as a conflict.
import type { QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { ApiProblem } from "@/api/client";
import { applyBatchResult, keys } from "@/api/queries";
import type { AssistProposal } from "@/api/assist";
import type { BatchRequest, ElementDocument, ElementSummary, ModelJson } from "@/api/types";
import type { EditorStore } from "@/state/store";
import { staleElements } from "./model";

export type ApplyOutcome = { ok: true } | { ok: false; conflict: boolean; message: string; changed: string[] };

async function read(id: string): Promise<ElementDocument | null> {
  try {
    return await endpoints.getElement(id);
  } catch (e) {
    if (e instanceof ApiProblem && e.status === 404) return null;
    throw e;
  }
}

/** The ids of the existing elements a proposal changes or deletes, and of the elements it creates. */
export function touchedIds(proposal: AssistProposal): { existing: string[]; created: string[] } {
  const existing = new Set<string>();
  const created = new Set<string>();
  for (const op of proposal.operations as { op?: string; id?: string }[]) if ((op.op === "update" || op.op === "delete") && op.id) existing.add(op.id);
  for (const f of proposal.files) {
    if (!f.id) continue;
    if (f.action === "created") created.add(f.id);
    else existing.add(f.id);
  }
  return { existing: [...existing], created: [...created].filter((id) => !existing.has(id)) };
}

export async function applyProposal(qc: QueryClient, store: EditorStore, proposal: AssistProposal): Promise<ApplyOutcome> {
  const index = new Map((qc.getQueryData<ElementSummary[]>(keys.index) ?? []).map((r) => [r.id, r] as const));
  const stale = staleElements(proposal, index);
  if (stale.length) return { ok: false, conflict: true, message: `${stale.join(", ")} changed since the proposal.`, changed: stale };

  const { existing, created } = touchedIds(proposal);
  const before = await Promise.all(existing.map(read));
  const hashOf = new Map(proposal.files.filter((f) => f.id && f.beforeHash).map((f) => [f.id!, f.beforeHash!] as const));
  const changed = existing.filter((id, i) => {
    const doc = before[i];
    const expected = hashOf.get(id);
    return !doc || (expected !== undefined && doc.hash !== expected);
  });
  if (changed.length) {
    const names = changed.map((id) => index.get(id)?.name ?? proposal.files.find((f) => f.id === id)?.name ?? id);
    return { ok: false, conflict: true, message: `${names.join(", ")} changed since the proposal.`, changed: names };
  }

  const result = await endpoints.applyBatch({ operations: proposal.operations } as unknown as BatchRequest);
  if (!endpoints.isBatchResult(result))
    return { ok: false, conflict: false, message: result.diagnostics[0]?.message ?? "The batch did not parse.", changed: [] };
  if (result.outcome !== "saved") {
    const refused = result.items.find((i) => i.outcome !== "saved");
    if (result.outcome === "conflict") {
      const name = refused?.id ? (index.get(refused.id)?.name ?? refused.id) : "An element";
      return { ok: false, conflict: true, message: `${name} changed since the proposal.`, changed: refused?.id ? [name] : [] };
    }
    return { ok: false, conflict: false, message: refused?.diagnostics[0]?.message ?? `The change was refused (${result.outcome}).`, changed: [] };
  }
  applyBatchResult(qc, result);

  const ids = [...existing, ...created];
  const after = await Promise.all(ids.map(read));
  after.forEach((doc, i) => {
    if (doc) qc.setQueryData(keys.element(ids[i]), doc);
  });
  store.getState().pushUndo({
    label: `Assistant: ${proposal.summary}`,
    ids,
    before: [...before.map((d) => (d ? (d.json as ModelJson) : null)), ...created.map(() => null)],
    after: after.map((d) => (d ? (d.json as ModelJson) : null)),
    afterHashes: after.map((d) => d?.hash ?? null),
  });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return { ok: true };
}
