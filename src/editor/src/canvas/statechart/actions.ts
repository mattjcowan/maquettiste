// The statechart canvas's writes that go beyond one draft: creating the process diagram the first time the chart is
// arranged (phase-3-design.md 2.7: one diagram per process, named after it, in its domain, one undoable step), and a
// process change that must drop the diagram members of the states it deletes, sent as one batch and one undo step
// (a diagram member naming a deleted state would be a dangling reference, which refuses the save); and a process
// rename that the process's diagram follows when it carries the process's old name.
import type { AppServices } from "@/app/context";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, applySaveResult, elementQuery, keys } from "@/api/queries";
import type { DiagramDoc, ElementSummary, ModelJson } from "@/api/types";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import { CHART_LABELS } from "@/model/labels";
import type { ProcessDoc } from "@/model/process";
import { newProcessDiagram, processDiagramId, removeMembers, type Placements } from "./diagram";

type Services = Pick<AppServices, "drafts" | "queryClient" | "store">;

/** Creates a process's diagram with every state placed; returns its id, or null (the reason is notified). */
export async function createProcessDiagram(
  services: Services,
  input: {
    process: Pick<ProcessDoc, "id" | "name" | "package"> & { displayName?: string };
    order: readonly string[];
    placements: Placements;
    collapsed: ReadonlySet<string>;
    viewport: { x: number; y: number; zoom: number } | null;
  },
): Promise<string | null> {
  const { queryClient, store } = services;
  const json = newProcessDiagram({ id: newId(), ...input }) as unknown as ModelJson;
  const result = await endpoints.createElement(json);
  if (result.outcome !== "saved") {
    store.getState().notify(CHART_LABELS.diagramNotSaved(result.diagnostics[0]?.message ?? result.outcome), "error");
    return null;
  }
  applySaveResult(queryClient, result);
  store.getState().pushUndo({
    label: CHART_LABELS.newDiagramUndo(String(json.name)),
    ids: [String(json.id)],
    before: [null],
    after: [result.current!.json as ModelJson],
    afterHashes: [result.hash],
  });
  return String(json.id);
}

/**
 * Deletes states (with their subtrees) from a process and their members from its diagram in one batch and one undo
 * step. Pending edits of both are saved first; when one cannot be saved (invalid, in conflict), nothing changes: the
 * change would land on the saved version while the editor shows the draft, whose next save would then conflict.
 * `apply` makes the change on the saved process and returns a refusal (nothing is sent) or null. Returns null when
 * saved, else the reason.
 */
export async function changeWithDiagram(
  services: Services,
  input: { process: string; diagram: string; label: string; removed: ReadonlySet<string>; apply: (process: ProcessDoc) => string | null },
): Promise<string | null> {
  const { drafts, queryClient, store } = services;
  if (!(await drafts.flushSaved(input.process))) return CHART_LABELS.unsavedProcess;
  if (!(await drafts.flushSaved(input.diagram))) return CHART_LABELS.unsavedDiagram;
  const [processDoc, diagramDoc] = await Promise.all([
    queryClient.fetchQuery({ ...elementQuery(input.process), staleTime: 0 }),
    queryClient.fetchQuery({ ...elementQuery(input.diagram), staleTime: 0 }),
  ]);
  const process = clone(processDoc.json as ModelJson) as unknown as ProcessDoc;
  const refusal = input.apply(process);
  if (refusal) return refusal;
  const diagram = clone(diagramDoc.json as ModelJson) as unknown as DiagramDoc;
  // A diagram that shows none of the removed states is left alone.
  const withDiagram = removeMembers(diagram, input.removed) > 0;
  const result = await endpoints.applyBatch({
    operations: [
      { op: "update", id: input.process, expectedHash: processDoc.hash, element: process as never },
      ...(withDiagram ? [{ op: "update" as const, id: input.diagram, expectedHash: diagramDoc.hash, element: diagram as never }] : []),
    ],
  });
  if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? CHART_LABELS.batchUnreadable;
  if (result.outcome !== "saved") {
    const failed = result.items.find((i) => i.outcome !== "saved");
    return failed?.diagnostics[0]?.message ?? CHART_LABELS.changeRefused(failed?.outcome ?? result.outcome);
  }
  applyBatchResult(queryClient, result);
  const ids = withDiagram ? [input.process, input.diagram] : [input.process];
  store.getState().pushUndo({
    label: input.label,
    ids,
    before: [clone(processDoc.json as ModelJson), clone(diagramDoc.json as ModelJson)].slice(0, ids.length),
    after: [clone(process as unknown as ModelJson), clone(diagram as unknown as ModelJson)].slice(0, ids.length),
    afterHashes: result.items.map((i) => i.hash),
  });
  return null;
}

/**
 * After a process save that renamed it (the explorer's Rename, the editor's or the inspector's name field): its diagram
 * takes the new name when it still carries the old one (and the new display name when it carried the old display
 * name), as a follow-up edit of the diagram's draft saved at once, so it joins the rename's undo step. A diagram named
 * otherwise by the user keeps its name.
 */
export async function followProcessRename(services: Services, id: string, before: ModelJson | null, after: ModelJson): Promise<void> {
  type Named = { kind?: string; name?: string; displayName?: string };
  const was = before as Named | null;
  const now = after as Named;
  if (now.kind !== "process" || !was || (was.name === now.name && was.displayName === now.displayName)) return;
  const { drafts, queryClient } = services;
  const diagramId = processDiagramId(queryClient.getQueryData<ElementSummary[]>(keys.index) ?? [], id);
  if (!diagramId) return;
  const doc = await queryClient.fetchQuery(elementQuery(diagramId));
  const current = (drafts.current(diagramId) ?? doc.json) as Named;
  const name = current.name === was.name && was.name !== now.name;
  const displayName = !!was.displayName && current.displayName === was.displayName && was.displayName !== now.displayName;
  if (!name && !displayName) return;
  drafts.edit(
    diagramId,
    (json) => {
      const d = json as Named;
      if (name && now.name) d.name = now.name;
      if (displayName) {
        if (now.displayName) d.displayName = now.displayName;
        else delete d.displayName;
      }
    },
    { followUp: true, base: doc },
  );
  await drafts.flush(diagramId);
}
