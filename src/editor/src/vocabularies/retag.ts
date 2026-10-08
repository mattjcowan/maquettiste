// Tags across the model from the vocabulary editors: the batch operation retag (ModelStore.Tags.cs) removes tags, or renames
// one, in a vocabulary and in every use it governs. It runs as one batch and one undo step: the documents it will change (the
// usage's elements and the vocabulary) are read first, drafts saved, and the batch expects them as read.
import type { BatchRequest, ModelJson } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult } from "@/api/queries";
import type { AppServices } from "@/app/context";
import { clone } from "@/lib/json";

export interface RetagRequest {
  tags: string[];
  /** The new key of the one tag; absent removes the tags. */
  name?: string;
  /** The domain whose vocabulary it is; absent for the global one. */
  package?: string;
}

async function readAll(ids: readonly string[]): Promise<Map<string, { hash: string; json: ModelJson }>> {
  const out = new Map<string, { hash: string; json: ModelJson }>();
  for (let i = 0; i < ids.length; i += endpoints.MAX_READ_IDS) {
    const read = await endpoints.readElements(ids.slice(i, i + endpoints.MAX_READ_IDS) as string[]);
    for (const doc of read.elements) out.set(String((doc.json as { id: string }).id), { hash: doc.hash, json: doc.json as ModelJson });
  }
  return out;
}

/** Applies a retag over the documents it touches (`ids`); resolves to null when saved, else the reason. */
export async function commitRetag(services: AppServices, label: string, request: RetagRequest, ids: readonly string[]): Promise<string | null> {
  const unique = [...new Set(ids)];
  for (const id of unique) await services.drafts.flush(id);
  const before = await readAll(unique);
  const expectedHashes = Object.fromEntries([...before].map(([id, d]) => [id, d.hash]));
  const result = await endpoints.applyBatch({ operations: [{ op: "retag", ...request, expectedHashes }] } as unknown as BatchRequest);
  if (!endpoints.isBatchResult(result)) return `${label} failed: ${result.diagnostics[0]?.message ?? "the batch did not parse"}`;
  if (result.outcome !== "saved") {
    const failed = result.items.find((i) => i.outcome !== "saved");
    return `${label} failed: ${failed?.diagnostics[0]?.message ?? result.outcome}`;
  }
  applyBatchResult(services.queryClient, result);
  const changed = result.items.map((i) => i.id).filter((id): id is string => !!id && before.has(id));
  const after = await readAll(changed);
  services.store.getState().pushUndo({
    label,
    ids: changed,
    before: changed.map((id) => clone(before.get(id)!.json)),
    after: changed.map((id) => clone(after.get(id)?.json ?? before.get(id)!.json)),
    afterHashes: changed.map((id) => after.get(id)?.hash ?? null),
  });
  void services.queryClient.invalidateQueries({ queryKey: ["tag-usage"] });
  return null;
}
