// Apply stereotype…, Tag… and Set category… (explorer-redesign.md 1.8, ✱ on a multi-selection) and the bulk
// inspector: one mark applied to many elements, saved as one batch with one undo entry. `applyMark` is pure.
import { useCallback } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { applyBatchResult, keys, loadElement } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { clone } from "@/lib/json";

export type Mark = { kind: "stereotype"; key: string } | { kind: "tag"; key: string } | { kind: "category"; id: string | null };
export type MarkKind = Mark["kind"];

export const MARK_LABELS: Record<MarkKind, { title: string; field: string; action: string }> = {
  stereotype: { title: "Apply stereotype", field: "Stereotype", action: "Apply" },
  tag: { title: "Tag", field: "Tag", action: "Add tag" },
  category: { title: "Set category", field: "Category", action: "Set category" },
};

/** The element with the mark applied: a stereotype or a tag added once (order kept), the category set or removed. */
export function applyMark(json: Record<string, unknown>, mark: Mark): void {
  if (mark.kind === "category") {
    if (mark.id) json.category = mark.id;
    else delete json.category;
    return;
  }
  const field = mark.kind === "stereotype" ? "stereotypes" : "tags";
  const list = Array.isArray(json[field]) ? (json[field] as string[]) : [];
  if (!list.includes(mark.key)) json[field] = [...list, mark.key];
}

/** The undo label and notification text of a mark. */
export function markLabel(mark: Mark, categoryName?: string): string {
  if (mark.kind === "stereotype") return `Apply «${mark.key}»`;
  if (mark.kind === "tag") return `Tag ${mark.key}`;
  return mark.id ? `Set category ${categoryName ?? mark.id}` : "Clear category";
}

/** Edits many elements as one batch (flushing drafts first), with one undo entry; false when nothing saved. */
export function useBatchEdit() {
  const { store, drafts } = useServices();
  const qc = useQueryClient();
  return useCallback(
    async (label: string, ids: readonly string[], mutate: (json: Record<string, unknown>) => void): Promise<boolean> => {
      await drafts.flushAll();
      const docs = await Promise.all(ids.map((id) => qc.fetchQuery({ queryKey: keys.element(id), queryFn: () => loadElement(id) })));
      const after = docs.map((d) => {
        const json = clone(d.json as ModelJson);
        mutate(json as unknown as Record<string, unknown>);
        return json;
      });
      const result = await endpoints.applyBatch({
        operations: docs.map((d, i) => ({ op: "update" as const, id: ids[i], expectedHash: d.hash, element: after[i] as never })),
      });
      if (!endpoints.isBatchResult(result) || result.outcome !== "saved") {
        const why = endpoints.isBatchResult(result)
          ? (result.items.find((i) => i.diagnostics.length)?.diagnostics[0]?.message ?? result.outcome)
          : "invalid batch";
        store.getState().notify(`${label} failed: ${why}`, "error");
        return false;
      }
      applyBatchResult(qc, result);
      store
        .getState()
        .pushUndo({ label, ids: [...ids], before: docs.map((d) => clone(d.json as ModelJson)), after, afterHashes: result.items.map((i) => i.hash) });
      store.getState().notify(ids.length === 1 ? `${label}: saved.` : `${label}: ${ids.length} elements saved in one batch.`);
      return true;
    },
    [qc, drafts, store],
  );
}
