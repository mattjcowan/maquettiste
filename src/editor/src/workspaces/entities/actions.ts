// Canvas writes that touch two elements go out as one batch (phase2-design.md 4.6): a new entity
// or relation together with the diagram member that shows it; undo reverses both.
import type { AppServices } from "@/app/context";
import type { BatchResult, DiagramDoc, DiagramMember, ElementDocument, ModelJson } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, elementQuery, keys, loadElement } from "@/api/queries";
import { planMembers, type Point, type RelationLookup } from "@/canvas/model";
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
  const diagramDoc: ElementDocument = cached ?? (await queryClient.fetchQuery({ queryKey: keys.element(diagramId), queryFn: () => loadElement(diagramId) }));
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

/**
 * Adds entities (and relations) to a diagram from the explorer (drop, Add to diagram, Add with related; explorer-redesign
 * step 10), through the diagram's draft so it saves, undoes and conflicts like a card move. Returns the members added.
 */
export async function addToDiagram(
  services: Pick<AppServices, "drafts" | "queryClient" | "store">,
  diagramId: string,
  input: { entities: readonly string[]; relations?: readonly string[]; lookup: RelationLookup; at?: Point },
): Promise<DiagramMember[]> {
  const { drafts, queryClient, store } = services;
  const base = await queryClient.fetchQuery(elementQuery(diagramId));
  const current = (store.getState().drafts[diagramId]?.json ?? base.json) as unknown as DiagramDoc;
  const added = planMembers({ members: current.members ?? [], ...input });
  if (!added.length) return added;
  drafts.edit(
    diagramId,
    (json) => {
      const diagram = json as unknown as DiagramDoc;
      const have = new Set((diagram.members ?? []).map((m) => m.element));
      diagram.members = [...(diagram.members ?? []), ...added.filter((m) => !have.has(m.element))];
    },
    { base },
  );
  await drafts.flush(diagramId);
  return added;
}
