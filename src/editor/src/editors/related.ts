// What an element editor's tabs list about one element, from the index rows alone (no documents): the relations
// with the element at an end, the mappings and tables of an entity, its seed data and the entities derived from it.
import type { ElementSummary } from "@/api/types";
import { indexPatchOf } from "@/api/indexPatch";

/** Index members designed but not yet in the contract (seeds' `target` and `rowCount`), read when present. */
type Row = ElementSummary & { target?: string | null; rowCount?: number | null };

export interface RelatedRows {
  relations: ElementSummary[];
  mappings: ElementSummary[];
  tables: ElementSummary[];
  seeds: (ElementSummary & { rowCount?: number | null })[];
  derived: ElementSummary[];
}

const byName = (a: ElementSummary, b: ElementSummary) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id);

/** A reverse index over one index array: target id -> ids of the rows that point at it, and the answers so far. */
interface RelatedState {
  byId: Map<string, Row>;
  links: Map<string, Set<string>>;
  results: Map<string, RelatedRows>;
}

const states = new WeakMap<readonly ElementSummary[], RelatedState | null>();

/** The ids a row points at (relation ends, the entity of a mapping or table, a seed's target, a base entity). */
function targetsOf(r: Row): string[] {
  const out: string[] = [];
  if (r.kind === "relation") for (const e of r.ends ?? []) out.push(e.entity);
  if ((r.kind === "mapping" || r.kind === "table") && r.entity) out.push(r.entity);
  if ((r.kind as string) === "seed" && r.target) out.push(r.target);
  if (r.base) out.push(r.base);
  return out;
}

function link(state: RelatedState, r: Row, on: boolean, affected?: Set<string>) {
  for (const t of targetsOf(r)) {
    affected?.add(t);
    let set = state.links.get(t);
    if (on) {
      if (!set) state.links.set(t, (set = new Set()));
      set.add(r.id);
    } else set?.delete(r.id);
  }
}

/**
 * The state of one index array: for an array patched from one whose state exists (model.changed, a save:
 * api/indexPatch.ts) the reverse index is taken over and patched in O(changes), and only the answers of the ids the
 * patch touches are dropped; otherwise one pass over the rows builds it.
 */
function stateOf(rows: readonly ElementSummary[]): RelatedState {
  const known = states.get(rows);
  if (known) return known;
  const patch = indexPatchOf(rows);
  const previous = patch ? states.get(patch.from) : undefined;
  let state: RelatedState;
  if (patch && previous) {
    states.set(patch.from, null); // the previous array rebuilds its own if it is ever asked again
    state = previous;
    const affected = new Set<string>();
    for (const id of patch.deleted) {
      const old = state.byId.get(id);
      if (old) link(state, old, false, affected);
      state.byId.delete(id);
    }
    for (const row of patch.upserts as Row[]) {
      const old = state.byId.get(row.id);
      if (old) link(state, old, false, affected);
      link(state, row, true, affected);
      state.byId.set(row.id, row);
    }
    for (const id of affected) state.results.delete(id);
  } else {
    state = { byId: new Map(), links: new Map(), results: new Map() };
    for (const r of rows as readonly Row[]) {
      state.byId.set(r.id, r);
      link(state, r, true);
    }
  }
  states.set(rows, state);
  return state;
}

/** The rows related to one element, from a reverse index kept per index array and patched with it. */
export function relatedOf(rows: readonly ElementSummary[] | undefined, id: string): RelatedRows {
  if (!rows) return { relations: [], mappings: [], tables: [], seeds: [], derived: [] };
  const state = stateOf(rows);
  const known = state.results.get(id);
  if (known) return known;
  const out: RelatedRows = { relations: [], mappings: [], tables: [], seeds: [], derived: [] };
  for (const rowId of state.links.get(id) ?? []) {
    const r = state.byId.get(rowId);
    if (!r) continue;
    if (r.kind === "relation" && r.ends?.some((e) => e.entity === id)) out.relations.push(r);
    else if (r.kind === "mapping" && r.entity === id) out.mappings.push(r);
    else if (r.kind === "table" && r.entity === id) out.tables.push(r);
    else if ((r.kind as string) === "seed" && r.target === id) out.seeds.push(r);
    if (r.base === id) out.derived.push(r);
  }
  for (const list of Object.values(out)) (list as ElementSummary[]).sort(byName);
  state.results.set(id, out);
  return out;
}

/** The base chain of an entity, nearest first, from the index's `base` (E5h) when present; stops at a cycle. */
export function baseChain(byId: ReadonlyMap<string, ElementSummary>, base: string | null | undefined): string[] {
  const chain: string[] = [];
  let next = base ?? null;
  while (next && !chain.includes(next)) {
    chain.push(next);
    next = byId.get(next)?.base ?? null;
  }
  return chain;
}
