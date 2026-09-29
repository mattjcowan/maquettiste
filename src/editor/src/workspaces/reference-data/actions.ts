// The Reference data screen's writes: a new reference type with its first seed in one batch, a new seed for a type
// that has none, and the CSV import applied as one save of the seed (RT 2.3) that undo reverses.
import type { AppServices } from "@/app/context";
import type { ElementDocument, ModelJson, ReferenceTypeDoc, SeedDoc } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, applySaveResult, keys, loadElement } from "@/api/queries";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";

export interface NewReferenceTypeInput {
  name: string;
  displayName: string;
  category: string | null;
}

const failure = (items: { outcome: string; diagnostics: { message: string }[] }[], outcome: string) => {
  const failed = items.find((i) => i.outcome !== "saved");
  return `${failed?.outcome ?? outcome}: ${failed?.diagnostics[0]?.message ?? ""}`.trim();
};

/** Creates the type and one seed named after it (RT 1.2), in one batch; undo removes both. */
export async function createReferenceType(
  services: AppServices,
  input: NewReferenceTypeInput,
): Promise<{ ok: true; id: string } | { ok: false; reason: string }> {
  const { queryClient, store } = services;
  const type = {
    kind: "reference-type",
    id: newId(),
    name: input.name,
    ...(input.displayName.trim() ? { displayName: input.displayName.trim() } : {}),
    ...(input.category ? { category: input.category } : {}),
    code: { id: newId() },
    label: { id: newId() },
  } as unknown as ModelJson;
  const seed = { kind: "seed", id: newId(), name: input.name, target: type.id, columns: ["code", "label"], rows: [] } as unknown as ModelJson;
  const result = await endpoints.applyBatch({
    operations: [
      { op: "create", element: type as never },
      { op: "create", element: seed as never },
    ],
  });
  if (!endpoints.isBatchResult(result)) return { ok: false, reason: result.diagnostics[0]?.message ?? "The batch did not parse." };
  if (result.outcome !== "saved") return { ok: false, reason: failure(result.items, result.outcome) };
  applyBatchResult(queryClient, result);
  store.getState().pushUndo({
    label: `New reference type ${input.name}`,
    ids: [String(type.id), String(seed.id)],
    before: [null, null],
    after: [clone(type), clone(seed)],
    afterHashes: result.items.map((i) => i.hash),
  });
  return { ok: true, id: String(type.id) };
}

/** A seed for a type that has none yet (rows added in the grid need one). */
export async function createSeed(services: AppServices, type: Pick<ReferenceTypeDoc, "id" | "name">): Promise<string | null> {
  const seed = { kind: "seed", id: newId(), name: type.name, target: type.id, columns: ["code", "label"], rows: [] } as unknown as ModelJson;
  const result = await endpoints.createElement(seed);
  if (result.outcome !== "saved") {
    services.store.getState().notify(`The rows could not be created: ${result.diagnostics[0]?.message ?? result.outcome}`, "error");
    return null;
  }
  applySaveResult(services.queryClient, result);
  return String(seed.id);
}

/** The seed as the server has it, after any pending draft of it is saved. */
export async function currentSeed(services: AppServices, id: string): Promise<ElementDocument> {
  await services.drafts.flush(id);
  return services.queryClient.fetchQuery({ queryKey: keys.element(id), queryFn: () => loadElement(id), staleTime: 0 });
}

/** Applies a previewed CSV import with the hash the preview read; the seed's new document replaces the cached one. */
export async function applyCsvImport(
  services: AppServices,
  seedId: string,
  content: string,
  mode: "merge" | "replace",
  before: ElementDocument,
): Promise<{ ok: true } | { ok: false; reason: string }> {
  const { queryClient, store } = services;
  const answer = await endpoints.importSeedCsv(seedId, content, { mode, dryRun: false, hash: before.hash });
  if (answer.status === 409) return { ok: false, reason: "The rows changed since the preview; preview the file again." };
  if (!answer.applied) return { ok: false, reason: answer.diagnostics[0]?.message ?? "The import was not applied." };
  const after = await queryClient.fetchQuery({ queryKey: keys.element(seedId), queryFn: () => loadElement(seedId), staleTime: 0 });
  queryClient.setQueryData(keys.element(seedId), after);
  void queryClient.invalidateQueries({ queryKey: keys.index });
  store.getState().pushUndo({
    label: "Import CSV",
    ids: [seedId],
    before: [clone(before.json as ModelJson)],
    after: [clone(after.json as ModelJson)],
    afterHashes: [after.hash],
  });
  return { ok: true };
}

/** Downloads a seed's rows as a CSV file named after the seed. */
export async function exportCsv(seed: Pick<SeedDoc, "id" | "name">): Promise<void> {
  const text = await endpoints.exportSeedCsv(seed.id);
  const url = URL.createObjectURL(new Blob([text], { type: "text/csv;charset=utf-8" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = `${seed.name}.csv`;
  a.hidden = true;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
