// Canvas writes that touch two elements go out as one batch (phase2-design.md 4.6): a new entity
// or relation together with the diagram member that shows it; undo reverses both.
import type { AppServices } from "@/app/context";
import type { BatchResult, DiagramDoc, DiagramMember, ElementDocument, ModelJson } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, keys } from "@/api/queries";
import { clone } from "@/lib/json";

export async function createWithMember(
  services: AppServices,
  element: ModelJson,
  diagramId: string,
  member: DiagramMember,
  label: string,
): Promise<{ ok: true; result: BatchResult } | { ok: false; reason: string }> {
  const { drafts, queryClient, store } = services;
  await drafts.flush(diagramId);
  const cached = queryClient.getQueryData<ElementDocument>(keys.element(diagramId));
  const diagramDoc: ElementDocument =
    cached ?? (await queryClient.fetchQuery({ queryKey: keys.element(diagramId), queryFn: () => endpoints.getElement(diagramId) }));
  const before = clone(diagramDoc.json as ModelJson);
  const diagram = clone(before) as unknown as DiagramDoc;
  diagram.members = [...(diagram.members ?? []), member];
  const after = diagram as unknown as ModelJson;
  const result = await endpoints.applyBatch({
    operations: [
      { op: "create", element: element as never },
      { op: "update", id: diagramId, expectedHash: diagramDoc.hash, element: after as never },
    ],
  });
  if (!endpoints.isBatchResult(result)) return { ok: false, reason: result.diagnostics[0]?.message ?? "The batch did not parse." };
  if (result.outcome !== "saved") {
    const failed = result.items.find((i) => i.outcome !== "saved");
    return { ok: false, reason: `${failed?.outcome ?? result.outcome}: ${failed?.diagnostics[0]?.message ?? ""}`.trim() };
  }
  applyBatchResult(queryClient, result);
  drafts.discard(diagramId);
  store.getState().pushUndo({
    label,
    ids: [String(element.id), diagramId],
    before: [null, before],
    after: [clone(element), clone(after)],
    afterHashes: result.items.map((i) => i.hash),
  });
  return { ok: true, result };
}
