// Undo and redo (phase2-design.md 4.6). Each successful save pushed { ids, before, after,
// afterHashes }. Undo sends the `before` documents as one POST /api/model/batch whose expected
// hashes are the `after` hashes (creates become deletes and deletes become creates); a conflict
// leaves the stacks unchanged and says which element changed since. Redo is the same with the
// inverse entry.
import type { QueryClient } from "@tanstack/react-query";
import type { BatchOperationRequest, BatchParseResult, BatchRequest, BatchResult, ModelJson } from "@/api/types";
import { applyBatchResult } from "@/api/queries";
import { isBatchResult } from "@/api/endpoints";
import { clone } from "@/lib/json";
import type { EditorStore, UndoEntry } from "./store";
import type { DraftManager } from "./drafts";
import { snapshotScope } from "@/api/snapshotScope";

export interface UndoDeps {
  store: EditorStore;
  queryClient: QueryClient;
  drafts: DraftManager;
  applyBatch: (batch: BatchRequest) => Promise<BatchResult | BatchParseResult>;
}

export type UndoOutcome = { ok: true; label: string } | { ok: false; reason: string };

/** The batch that restores `entry.before`. */
export function batchFor(entry: UndoEntry): BatchRequest {
  const operations: BatchOperationRequest[] = entry.ids.map((id, i) => {
    const before = entry.before[i];
    const after = entry.after[i];
    const hash = entry.afterHashes[i];
    if (before === null) return { op: "delete", id, expectedHash: hash! };
    if (after === null) return { op: "create", element: clone(before) as unknown as BatchOperationRequest["element"] };
    return { op: "update", id, expectedHash: hash!, element: clone(before) as unknown as BatchOperationRequest["element"] };
  });
  return { operations };
}

export class UndoManager {
  constructor(private readonly deps: UndoDeps) {}

  canUndo(): boolean {
    return this.deps.store.getState().undo.length > 0;
  }

  canRedo(): boolean {
    return this.deps.store.getState().redo.length > 0;
  }

  undo(): Promise<UndoOutcome> {
    return this.step("undo");
  }

  redo(): Promise<UndoOutcome> {
    return this.step("redo");
  }

  private async step(direction: "undo" | "redo"): Promise<UndoOutcome> {
    // The stacks hold the working model's edits; a snapshot shown read-only has none of its own.
    if (snapshotScope()) return { ok: false, reason: `Cannot ${direction} while a snapshot is shown: it is read-only.` };
    await this.deps.drafts.flushAll();
    const state = this.deps.store.getState();
    const from = direction === "undo" ? state.undo : state.redo;
    const entry = from[from.length - 1];
    if (!entry) return { ok: false, reason: `Nothing to ${direction}.` };
    // flushAll waits for saves re-queued behind an in-flight one; if one still has not settled, the
    // batch would race it and the user's own save would come back as a conflict (4.6).
    if (this.deps.drafts.hasPending(entry.ids)) return { ok: false, reason: `Cannot ${direction} yet: a save is still in progress.` };

    const result = await this.deps.applyBatch(batchFor(entry));
    if (!isBatchResult(result)) {
      return { ok: false, reason: `The ${direction} batch was refused: ${result.diagnostics[0]?.message ?? "invalid batch"}` };
    }
    if (result.outcome !== "saved") {
      const failed = result.items.findIndex((item) => item.outcome !== "saved");
      const id = entry.ids[failed >= 0 ? failed : 0];
      const name = (entry.after[failed] ?? entry.before[failed]) as { name?: string } | null;
      const what = name?.name ? `${name.name} (${id})` : id;
      const reason = result.outcome === "conflict" ? `Cannot ${direction}: ${what} changed since.` : `Cannot ${direction}: ${what} is ${result.outcome}.`;
      return { ok: false, reason };
    }

    applyBatchResult(this.deps.queryClient, result);
    // Drafts left for these elements (invalid ones) were based on the hashes the batch replaced.
    for (const id of entry.ids) if (this.deps.store.getState().drafts[id]) this.deps.drafts.discard(id);
    const inverse: UndoEntry = {
      label: entry.label,
      ids: entry.ids,
      before: entry.after.map((j) => (j === null ? null : (clone(j) as ModelJson))),
      after: entry.before.map((j) => (j === null ? null : (clone(j) as ModelJson))),
      afterHashes: result.items.map((item) => item.hash),
    };
    const rest = from.slice(0, -1);
    if (direction === "undo") state.setStacks(rest, [...state.redo, inverse]);
    else state.setStacks([...state.undo, inverse], rest);
    return { ok: true, label: entry.label };
  }
}
