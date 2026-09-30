// Edits the element editors make to other elements (a mapping from the Inheritance or Mappings tab): through the
// draft manager like the inspector's edits, so they save, undo and conflict the same way; a new element is created
// with one undo entry.
import { useCallback } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { applySaveResult, elementQuery } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { clone } from "@/lib/json";

export function useElementEdits() {
  const qc = useQueryClient();
  const { drafts, store } = useServices();
  const update = useCallback(
    async (id: string, mutate: (json: Record<string, unknown>) => void) => {
      const base = await qc.fetchQuery(elementQuery(id));
      drafts.edit(id, (json) => mutate(json as unknown as Record<string, unknown>), { base });
      await drafts.flush(id);
    },
    [qc, drafts],
  );
  const create = useCallback(
    async (json: Record<string, unknown>, label: string): Promise<boolean> => {
      const result = await endpoints.createElement(json as unknown as ModelJson);
      if (result.outcome !== "saved") {
        store.getState().notify(`${label} failed: ${result.diagnostics[0]?.message ?? result.outcome}`, "error");
        return false;
      }
      applySaveResult(qc, result);
      store.getState().pushUndo({
        label,
        ids: [String(json.id)],
        before: [null],
        after: [clone(result.current?.json ?? (json as unknown as ModelJson))],
        afterHashes: [result.hash],
      });
      return true;
    },
    [qc, store],
  );
  return { update, create };
}
