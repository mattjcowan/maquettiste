import type { ElementKind, ElementSummary } from "@/api/types";
import { indexPatchOf } from "@/api/indexPatch";

/** Lookups over the model index (every element's summary). */
export interface IndexLookup {
  rows: ElementSummary[];
  byId: Map<string, ElementSummary>;
  ofKind(kind: ElementKind): ElementSummary[];
  nameOf(id: string): string | undefined;
}

const lookups = new WeakMap<readonly ElementSummary[], IndexLookup>();
const EMPTY: ElementSummary[] = [];

/**
 * The lookups over one index array, built once per array and lazily: `byId` on first use, each kind's sorted list on
 * first ask. For an array patched from one whose lookup exists (model.changed, a save: api/indexPatch.ts), `byId` is
 * carried over and patched (O(changes)), and each kind the patch does not touch keeps its sorted list.
 */
export function indexLookup(rows: readonly ElementSummary[] | undefined): IndexLookup {
  const list = rows ?? EMPTY;
  let lookup = lookups.get(list);
  if (!lookup) {
    lookup = createLookup(list);
    lookups.set(list, lookup);
  }
  return lookup;
}

interface LookupState {
  byId: Map<string, ElementSummary> | null;
  byKind: Map<ElementKind, ElementSummary[]>;
}

function createLookup(rows: readonly ElementSummary[]): IndexLookup {
  const state: LookupState = { byId: null, byKind: new Map() };
  // Follow the patch from the previous array's lookup when it has built its maps: take them over.
  const patch = indexPatchOf(rows);
  const previous = patch ? states.get(lookups.get(patch.from) as IndexLookup) : undefined;
  if (patch && previous?.byId) {
    const byId = previous.byId;
    previous.byId = null; // the previous lookup rebuilds its own if it is ever asked again
    const touched = new Set<ElementKind>();
    for (const id of patch.deleted) {
      const old = byId.get(id);
      if (old) touched.add(old.kind);
      byId.delete(id);
    }
    for (const row of patch.upserts) {
      const old = byId.get(row.id);
      if (old) touched.add(old.kind);
      touched.add(row.kind);
      byId.set(row.id, row);
    }
    state.byId = byId;
    for (const [kind, bucket] of previous.byKind) if (!touched.has(kind)) state.byKind.set(kind, bucket);
  }
  const byId = () => {
    if (!state.byId) {
      const map = new Map<string, ElementSummary>();
      for (const r of rows) map.set(r.id, r);
      state.byId = map;
    }
    return state.byId;
  };
  const lookup: IndexLookup = {
    rows: rows as ElementSummary[],
    get byId() {
      return byId();
    },
    ofKind: (kind) => {
      let bucket = state.byKind.get(kind);
      if (!bucket) {
        bucket = rows.filter((r) => r.kind === kind).sort((a, b) => a.name.localeCompare(b.name));
        state.byKind.set(kind, bucket);
      }
      return bucket;
    },
    nameOf: (id) => byId().get(id)?.name,
  };
  states.set(lookup, state);
  return lookup;
}

const states = new WeakMap<IndexLookup, LookupState>();

/** Category colors cycle through the eight categorical tokens in category-tree order. */
export function categoryColorIndex(categoryIds: string[], id: string | null | undefined): number | null {
  if (!id) return null;
  const at = categoryIds.indexOf(id);
  return at < 0 ? null : (at % 8) + 1;
}
