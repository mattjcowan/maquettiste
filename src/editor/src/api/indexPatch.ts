// Incremental index patching (explorer-redesign.md 4.4, step 12). A pure module: the query cache
// (api/queries.ts), the tree (explorer/tree.ts) and the search client (search/client.ts) share it.
//
// Every patched rows array is registered with the patch that produced it, so each consumer that
// holds an earlier array can follow the chain of patches from its array to the new one and apply
// only the difference, instead of rebuilding from every row. An array that was fetched (not
// patched) has no patch: consumers then rebuild.
import type { ElementSummary } from "./types";

export interface IndexPatch {
  /** The rows the patch was applied to. */
  from: readonly ElementSummary[];
  /** New and changed rows, in the order of the new array. */
  upserts: ElementSummary[];
  /** Ids removed (only ids that were present). */
  deleted: string[];
  /**
   * Rows that took a new position (added, or their path changed), from the last position to the
   * first, each with the id of the row that follows it in the new array (null: the end). Inserting
   * them in this order before their follower rebuilds the new order from the old one.
   */
  moves: [id: string, before: string | null][];
}

const patches = new WeakMap<readonly ElementSummary[], IndexPatch>();

/** The patch that produced these rows, if they were patched. */
export function indexPatchOf(rows: readonly ElementSummary[]): IndexPatch | undefined {
  return patches.get(rows);
}

/**
 * The patches that lead from `from` to `to`, oldest first; null when `to` was not reached from
 * `from` by patches (a refetch in between, or more than `limit` steps). Same array: no patches.
 */
export function patchesBetween(from: readonly ElementSummary[] | null | undefined, to: readonly ElementSummary[], limit = 64): IndexPatch[] | null {
  if (!from) return null;
  const chain: IndexPatch[] = [];
  for (let at = to; at !== from;) {
    const patch = patches.get(at);
    if (!patch || chain.length >= limit) return null;
    chain.push(patch);
    at = patch.from;
  }
  return chain.reverse();
}

// The engine's and the mock's index order: by path, ordinal.
const byPath = (a: ElementSummary, b: ElementSummary) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0);

/**
 * Applies upserts and deletions to the rows the way a refetch would answer: a changed row whose
 * path is the same keeps its position; a new row, or one whose path changed, is inserted at its
 * path's position (binary search over rows that are in path order). Returns `rows` itself when
 * nothing applies, and registers the patch otherwise.
 */
export function applyIndexPatch(rows: readonly ElementSummary[], upserts: readonly ElementSummary[], deleted: readonly string[]): readonly ElementSummary[] {
  const updates = new Map<string, ElementSummary>();
  for (const u of upserts) updates.set(u.id, u);
  const drop = new Set(deleted);
  for (const id of drop) updates.delete(id);
  if (updates.size === 0 && drop.size === 0) return rows;

  const next: ElementSummary[] = [];
  const removed: string[] = [];
  const kept = new Set<string>();
  let replaced = false;
  for (const row of rows) {
    if (drop.has(row.id)) {
      removed.push(row.id);
      continue;
    }
    const update = updates.get(row.id);
    if (!update) next.push(row);
    else if (update.path === row.path) {
      next.push(update);
      kept.add(row.id);
      if (update !== row) replaced = true;
    }
    // A row whose path changed is taken out here and inserted below.
  }
  const placed = [...updates.values()].filter((u) => !kept.has(u.id)).sort(byPath);
  for (const u of placed) {
    let lo = 0;
    let hi = next.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (byPath(next[mid], u) <= 0) lo = mid + 1;
      else hi = mid;
    }
    next.splice(lo, 0, u);
  }
  if (removed.length === 0 && placed.length === 0 && !replaced) return rows;

  const moved = new Set(placed.map((u) => u.id));
  const moves: [string, string | null][] = [];
  const ordered: ElementSummary[] = [];
  for (let i = next.length - 1; i >= 0; i--) if (moved.has(next[i].id)) moves.push([next[i].id, next[i + 1]?.id ?? null]);
  for (const row of next) if (updates.has(row.id)) ordered.push(row);
  patches.set(next, { from: rows, upserts: ordered, deleted: removed, moves });
  return next;
}
