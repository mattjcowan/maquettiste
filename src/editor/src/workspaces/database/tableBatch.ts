// Writing several documents of a database at once (a delete plan, a rename that rewrites what names the renamed thing): the
// documents are read as they are now (their unsaved drafts saved first), each change made on a copy, and everything sent as one
// batch with the hashes read (all or nothing; a document changed meanwhile is a conflict, nothing written), then one undo step,
// or, with `join`, part of the step on top of the stack (the store of a table that the same gesture made first). A plan shown
// before it is written (a delete's) passes the hashes it was read with (`expected`): a document changed since is a conflict
// too, so a plan never overwrites a newer change. `retractStep` takes back a step the gesture made and then did not use (a
// store before a delete that was cancelled or blocked), leaving no trace in the history.
import type { QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, elementQuery, indexQuery, invalidateResolved, keys } from "@/api/queries";
import { batchFor } from "@/state/undo";
import type { ElementSummary, ModelJson } from "@/api/types";
import type { AppServices as Services } from "@/app/context";
import { clone, jsonEqual } from "@/lib/json";
import type { UndoEntry } from "@/state/store";

type Json = Record<string, unknown>;

/** Merges a step into the one on top (an element already in it keeps its first `before`). */
export function joinUndo(top: UndoEntry, next: UndoEntry): UndoEntry {
  const ids = [...top.ids];
  const before = [...top.before];
  const after = [...top.after];
  const afterHashes = [...top.afterHashes];
  next.ids.forEach((id, i) => {
    const at = ids.indexOf(id);
    if (at < 0) {
      ids.push(id);
      before.push(next.before[i]);
      after.push(next.after[i]);
      afterHashes.push(next.afterHashes[i]);
    } else {
      after[at] = next.after[i];
      afterHashes[at] = next.afterHashes[i];
    }
  });
  return { ...top, label: next.label, ids, before, after, afterHashes };
}

/**
 * Changes documents in one batch and one undo step labelled `label`. Each edit changes a copy of its document as read now; a
 * document left as it was is not sent. Resolves to null when saved (or nothing changed), else why not (said too).
 */
export async function commitDocuments(
  services: Pick<Services, "store" | "drafts">,
  qc: QueryClient,
  label: string,
  edits: ReadonlyMap<string, (doc: Json) => void>,
  options: { join?: boolean; expected?: ReadonlyMap<string, string> } = {},
): Promise<string | null> {
  const { store, drafts } = services;
  const notify = (why: string) => {
    store.getState().notify(`${label}: ${why}`, "error");
    return why;
  };
  for (const id of edits.keys()) {
    if (store.getState().drafts[id] && !(await drafts.flushSaved(id))) return notify(`${id} has changes that are not saved; save or discard them first.`);
  }
  const read = await Promise.all([...edits.keys()].map((id) => qc.fetchQuery({ ...elementQuery(id), staleTime: 0 })));
  const nameOf = (id: string) => qc.getQueryData<ElementSummary[]>(indexQuery.queryKey)?.find((r) => r.id === id)?.name || id;
  const stale = [...edits.keys()].find((id, i) => options.expected?.has(id) && options.expected.get(id) !== read[i].hash);
  if (stale) return notify(`${nameOf(stale)} changed since the plan was made; nothing was changed. Ask again to see the new plan.`);
  const ops: { op: "update"; id: string; expectedHash: string; element: ModelJson }[] = [];
  const befores: ModelJson[] = [];
  [...edits.entries()].forEach(([id, edit], i) => {
    const before = read[i].json as ModelJson;
    const next = clone(before) as unknown as Json;
    edit(next);
    if (jsonEqual(next as unknown as ModelJson, before)) return;
    ops.push({ op: "update", id, expectedHash: read[i].hash, element: next as unknown as ModelJson });
    befores.push(clone(before));
  });
  if (!ops.length) return null;
  const result = await endpoints.applyBatch({ operations: ops } as never);
  if (!endpoints.isBatchResult(result)) return notify(result.diagnostics[0]?.message ?? "the batch did not parse.");
  if (result.outcome !== "saved") {
    const failed = result.items.find((i) => i.outcome !== "saved");
    const name = failed?.id ? nameOf(failed.id) : "";
    return notify(
      result.outcome === "conflict"
        ? `${name} changed meanwhile; nothing was changed, try again.`
        : `${failed?.diagnostics.find((d) => d.severity === "error")?.message ?? result.outcome} (nothing was changed)`,
    );
  }
  applyBatchResult(qc, result);
  const entry: UndoEntry = {
    label,
    ids: ops.map((o) => o.id),
    before: befores,
    after: ops.map((o) => clone(o.element)),
    afterHashes: ops.map((o) => result.items.find((i) => i.id === o.id)?.hash ?? null),
  };
  const s = store.getState();
  const top = s.undo[s.undo.length - 1];
  if (options.join && top) s.setStacks([...s.undo.slice(0, -1), joinUndo(top, entry)], s.redo);
  else s.pushUndo(entry);
  invalidateResolved(qc);
  void qc.invalidateQueries({ queryKey: keys.tables });
  return null;
}

/**
 * Reads documents in chunks of the batched read's limit (unknown ids are left out); `read`, when given, gets each one's hash and
 * path (a plan's expected hashes, the files a dialog lists).
 */
export async function readDocuments(ids: readonly string[], read?: Map<string, { hash: string; path: string }>): Promise<Json[]> {
  const out: Json[] = [];
  const unique = [...new Set(ids)];
  for (let i = 0; i < unique.length; i += endpoints.MAX_READ_IDS) {
    const chunk = await endpoints.readElements(unique.slice(i, i + endpoints.MAX_READ_IDS));
    for (const d of chunk.elements) {
      out.push(d.json as unknown as Json);
      read?.set(String((d.json as unknown as Json).id), { hash: d.hash, path: d.path });
    }
  }
  return out;
}

/**
 * Takes back the step on top of the undo stack when it is the one labelled `label` (a store the gesture made first and then did
 * not use): its documents go back as they were, and the step leaves the history (no redo either). Resolves to whether it did;
 * a document changed since keeps the step where it is (said).
 */
export async function retractStep(services: Pick<Services, "store" | "drafts">, qc: QueryClient, label: string): Promise<boolean> {
  const { store, drafts } = services;
  await drafts.flushAll();
  const top = store.getState().undo.at(-1);
  if (!top || top.label !== label) return false;
  const result = await endpoints.applyBatch(batchFor(top));
  if (!endpoints.isBatchResult(result) || result.outcome !== "saved") {
    store.getState().notify(`${label}: the table stays stored as a file (it changed meanwhile); undo takes it back.`, "error");
    return false;
  }
  applyBatchResult(qc, result);
  const s = store.getState();
  s.setStacks(
    s.undo.filter((e) => e !== top),
    s.redo,
  );
  invalidateResolved(qc);
  void qc.invalidateQueries({ queryKey: keys.tables });
  return true;
}
