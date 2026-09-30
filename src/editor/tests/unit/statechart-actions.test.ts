// The statechart canvas's writes beyond one draft, over the mock API: a delete of states with their diagram members
// refuses while the process has a draft that could not be saved (it would land on the saved version the editor does
// not show), and a process rename renames its diagram when the diagram carries the old name, in the rename's undo step.
import { describe, expect, it } from "vitest";
import * as endpoints from "@/api/endpoints";
import { indexQuery, keys } from "@/api/queries";
import { changeWithDiagram } from "@/canvas/statechart/actions";
import { deleteStates } from "@/canvas/statechart/edits";
import { CHART_LABELS } from "@/model/labels";
import { PURCHASE_APPROVAL_DIAGRAM } from "@/mocks/model/processDiagramSeed";
import { PURCHASE_APPROVAL } from "@/mocks/model/processSeed";
import { useMockApi } from "./harness";

const S = (n: number) => `01JQSTA0000000000000000${String(n).padStart(3, "0")}`;
const T = (n: number) => `01JQTRN0000000000000000${String(n).padStart(3, "0")}`;
type Named = { name: string; displayName?: string };
type Members = { members: { element: string }[] };

describe("the statechart's writes with its diagram", () => {
  const api = useMockApi();

  async function load(id: string) {
    const doc = await endpoints.getElement(id);
    api.services.queryClient.setQueryData(keys.element(id), doc);
    return doc;
  }

  const deleteRejected = () =>
    changeWithDiagram(api.services, {
      process: PURCHASE_APPROVAL,
      diagram: PURCHASE_APPROVAL_DIAGRAM,
      label: "Delete Rejected",
      removed: new Set([S(113)]),
      apply: (p) => deleteStates(p, [S(113)], [T(22)]),
    });

  it("deletes a state and its diagram member in one batch and one undo step", async () => {
    await load(PURCHASE_APPROVAL);
    await load(PURCHASE_APPROVAL_DIAGRAM);
    expect(await deleteRejected()).toBeNull();
    expect((api.backend.model.get(PURCHASE_APPROVAL_DIAGRAM)!.json as unknown as Members).members.some((m) => m.element === S(113))).toBe(false);
    expect(api.services.store.getState().undo.at(-1)?.ids).toEqual([PURCHASE_APPROVAL, PURCHASE_APPROVAL_DIAGRAM]);
  });

  it("refuses, changing nothing, while the process has a draft that could not be saved", async () => {
    const { drafts, store } = api.services;
    await load(PURCHASE_APPROVAL);
    await load(PURCHASE_APPROVAL_DIAGRAM);
    // Another window edits the process; our draft of it then conflicts and stays.
    api.backend.model.externalEdit(PURCHASE_APPROVAL, (json) => {
      json.description = "edited elsewhere";
    });
    drafts.edit(PURCHASE_APPROVAL, (json) => {
      (json as unknown as Named).displayName = "Mine";
    });
    expect(await deleteRejected()).toBe(CHART_LABELS.unsavedProcess);
    expect(store.getState().drafts[PURCHASE_APPROVAL]?.status).toBe("conflict");
    const process = api.backend.model.get(PURCHASE_APPROVAL)!.json as unknown as { states: { id: string }[] };
    expect(process.states.some((s) => s.id === S(113))).toBe(true);
    expect((api.backend.model.get(PURCHASE_APPROVAL_DIAGRAM)!.json as unknown as Members).members.some((m) => m.element === S(113))).toBe(true);
    expect(store.getState().undo).toHaveLength(0);
  });

  it("renames the process's diagram with the process when it carries the old name; one undo step takes both back", async () => {
    const { drafts, queryClient, undo, store } = api.services;
    await queryClient.fetchQuery(indexQuery);
    await load(PURCHASE_APPROVAL);
    drafts.edit(PURCHASE_APPROVAL, (json) => {
      (json as unknown as Named).name = "PurchaseRequest";
      (json as unknown as Named).displayName = "Purchase request";
    });
    await drafts.flush(PURCHASE_APPROVAL);
    await expect.poll(() => (api.backend.model.get(PURCHASE_APPROVAL_DIAGRAM)!.json as unknown as Named).name).toBe("PurchaseRequest");
    await drafts.whenSettled(PURCHASE_APPROVAL_DIAGRAM);
    expect((api.backend.model.get(PURCHASE_APPROVAL_DIAGRAM)!.json as unknown as Named).displayName).toBe("Purchase request");
    expect(store.getState().undo).toHaveLength(1);
    expect(store.getState().undo[0].ids).toEqual([PURCHASE_APPROVAL, PURCHASE_APPROVAL_DIAGRAM]);
    expect((await undo.undo()).ok).toBe(true);
    expect((api.backend.model.get(PURCHASE_APPROVAL)!.json as unknown as Named).name).toBe("PurchaseApproval");
    expect((api.backend.model.get(PURCHASE_APPROVAL_DIAGRAM)!.json as unknown as Named).name).toBe("PurchaseApproval");
  });

  it("leaves a diagram the user named otherwise", async () => {
    const { drafts, queryClient } = api.services;
    api.backend.model.externalEdit(PURCHASE_APPROVAL_DIAGRAM, (json) => {
      json.name = "ApprovalChart";
    });
    await queryClient.fetchQuery(indexQuery);
    await load(PURCHASE_APPROVAL);
    drafts.edit(PURCHASE_APPROVAL, (json) => {
      (json as unknown as Named).name = "PurchaseRequest";
    });
    await drafts.flush(PURCHASE_APPROVAL);
    await new Promise((r) => setTimeout(r, 50));
    expect((api.backend.model.get(PURCHASE_APPROVAL_DIAGRAM)!.json as unknown as Named).name).toBe("ApprovalChart");
  });
});
