// The verb quick fixes that reach the project's verb vocabulary (verbs-design.md section 9): MQ9701 adds an unknown verb as an own
// verb, MQ9702 and MQ9706 use the built-in verb (rename-verb), MQ9704 shows a hidden verb again. Each is one undo step.
import type { QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { applySaveResult, keys } from "@/api/queries";
import type { ElementSummary, ModelJson } from "@/api/types";
import type { AppServices } from "@/app/context";
import { clone } from "@/lib/json";
import { newId } from "@/lib/ids";
import { labelOfKey } from "@/model/verbList";
import { applyQuickFix } from "@/problems/applyQuickFix";
import { commitOperation } from "@/vocabularies/retag";

export type VerbFix =
  | { kind: "verb"; action: "add"; key: string; label: string }
  | { kind: "verb"; action: "use-built-in"; key: string; to: string; label: string }
  | { kind: "verb"; action: "show"; key: string; label: string };

const vocabularyId = (qc: QueryClient) => qc.getQueryData<ElementSummary[]>(keys.index)?.find((r) => r.kind === "verb-vocabulary")?.id ?? null;

/** Applies a verb fix; resolves to null when saved, else the reason. */
export async function applyVerbFix({ queryClient: qc, store, drafts }: AppServices, fix: VerbFix): Promise<string | null> {
  const id = vocabularyId(qc);
  if (id) await drafts.flush(id);
  if (fix.action === "use-built-in") {
    if (!id) return "The project has no verbs file.";
    const doc = (await endpoints.getElement(id)).json as { verbs?: { id: string; key: string }[] };
    const own = doc.verbs?.find((v) => v.key === fix.key);
    const referrers = own ? await endpoints.getReferences(own.id) : [];
    return commitOperation({ queryClient: qc, store, drafts } as AppServices, fix.label, { op: "rename-verb", verb: fix.key, to: fix.to }, [
      id,
      ...referrers.map((r) => r.fromElementId),
    ]);
  }
  if (!id) {
    if (fix.action === "show") return null;
    const json = {
      kind: "verb-vocabulary",
      id: newId(),
      name: "verbs",
      verbs: [{ id: newId(), key: fix.key, displayName: labelOfKey(fix.key) }],
    } as unknown as ModelJson;
    const result = await endpoints.createElement(json);
    if (result.outcome !== "saved") return result.diagnostics[0]?.message ?? result.outcome;
    applySaveResult(qc, result);
    const created = (await endpoints.getElement(String(result.id))).json as ModelJson;
    store.getState().pushUndo({ label: fix.label, ids: [String(result.id)], before: [null], after: [created], afterHashes: [result.hash] });
    return null;
  }
  const json = clone((await endpoints.getElement(id)).json) as { verbs?: unknown[]; hidden?: string[] };
  if (fix.action === "add") json.verbs = [...(json.verbs ?? []), { id: newId(), key: fix.key }];
  else {
    json.hidden = (json.hidden ?? []).filter((k) => k !== fix.key);
    if (!json.hidden.length) delete json.hidden;
  }
  return applyQuickFix(qc, store, { kind: "update", id, json: json as unknown as ModelJson, label: fix.label });
}
