// Applies a quick fix (quickFix.ts) as one batch and one undo step: the documents it may change are read before it
// and after it, the caches follow, and the validation report is refreshed so the panel re-validates.
import type { QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, keys } from "@/api/queries";
import type { Diagnostic, ModelJson } from "@/api/types";
import type { DraftManager } from "@/state/drafts";
import type { EditorStore } from "@/state/store";
import { deriveQuickFix, fixDocuments, type QuickFix } from "./quickFix";

/** Flushes pending edits of the documents a fix reads, then derives the fix from their saved versions. */
export async function freshQuickFix(drafts: DraftManager, d: Diagnostic, catalogFix: string | null | undefined): Promise<QuickFix | null> {
  if (!d.elementId) return null;
  await drafts.flush(d.elementId);
  const element = (await endpoints.getElement(d.elementId)).json as ModelJson;
  const docs = new Map<string, ModelJson>([[d.elementId, element]]);
  for (const id of fixDocuments(d, element).slice(1)) {
    await drafts.flush(id);
    docs.set(id, (await endpoints.getElement(id)).json as ModelJson);
  }
  return deriveQuickFix(d, catalogFix, (id) => docs.get(id));
}

/** Applies an operation or update fix; null when saved, else the refusal's message. `target` overrides the fix's own. */
export async function applyQuickFix(
  qc: QueryClient,
  store: EditorStore,
  fix: Exclude<QuickFix, { kind: "sync-enum" }>,
  target?: string,
): Promise<string | null> {
  const ids = fix.kind === "update" ? [fix.id] : fix.ids;
  const before = await Promise.all(ids.map((id) => endpoints.getElement(id)));
  const operation =
    fix.kind === "update"
      ? { op: "update", id: fix.id, expectedHash: before[0].hash, element: fix.json }
      : { op: fix.op, id: fix.id, ...((target ?? fix.target) ? { target: target ?? fix.target } : {}) };
  const result = await endpoints.applyBatch({ operations: [operation] } as never);
  if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? "The batch did not parse.";
  if (result.outcome !== "saved") {
    const refused = result.items.find((i) => i.outcome !== "saved");
    return refused?.diagnostics[0]?.message ?? `The fix was refused (${result.outcome}).`;
  }
  applyBatchResult(qc, result);
  const after = await Promise.all(ids.map((id) => endpoints.getElement(id)));
  after.forEach((doc, i) => qc.setQueryData(keys.element(ids[i]), doc));
  store.getState().pushUndo({
    label: fix.label,
    ids,
    before: before.map((doc) => doc.json as ModelJson),
    after: after.map((doc) => doc.json as ModelJson),
    afterHashes: after.map((doc) => doc.hash),
  });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return null;
}
