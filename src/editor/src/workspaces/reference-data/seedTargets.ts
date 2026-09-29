// Seeds of entities and relations in the Rows grid (the entity editor's Seed data tab, a relation's): the grid's
// columns come from the target's attributes (an entity's bases first) and its to-one relation ends, as the engine
// resolves seed columns; an end cell names a row of the far entity's seeds (or of a subtype's), picked from a list.
// A seed is only ever created on the user's request (New seed), listing every column so its CSV export is a template.
import { useMemo } from "react";
import type { AppServices } from "@/app/context";
import { useServices } from "@/app/context";
import type { ElementSummary, ModelJson, RelationDoc, SeedDoc } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { applySaveResult, elementQuery, indexQuery, useElements, useIndex } from "@/api/queries";
import { baseChain } from "@/editors/related";
import { newId } from "@/lib/ids";
import { clone } from "@/lib/json";
import { attributesOf } from "@/model/model";
import { useEditor } from "@/state/store";
import { endRowOptions, entityGridColumns, entitySeedEnds, newSeedDocument, relationGridColumns, type EndOption, type GridColumn } from "./rowsModel";

/** The kinds whose seeds the entity and relation editors show in the grid. */
export const isSeedTargetKind = (kind: string | undefined): kind is "entity" | "relation" => kind === "entity" || kind === "relation";

interface TargetPlan {
  target?: ElementSummary;
  /** The documents the columns read: the entity, its bases (nearest first) and the relations touching them; or the relation. */
  documents: string[];
  lineage: string[];
  relations: string[];
}

/** Which documents a target's seed columns come from. */
export function seedTargetPlan(rows: readonly ElementSummary[], targetId: string): TargetPlan {
  const byId = new Map(rows.map((r) => [r.id, r]));
  const target = byId.get(targetId);
  if (target?.kind === "relation") return { target, documents: [targetId], lineage: [], relations: [] };
  if (target?.kind !== "entity") return { target, documents: [], lineage: [], relations: [] };
  const lineage = [targetId, ...baseChain(byId, target.base)];
  const inLineage = new Set(lineage);
  const relations = rows.filter((r) => r.kind === "relation" && (r.ends ?? []).some((e) => inLineage.has(e.entity))).map((r) => r.id);
  return { target, documents: [...lineage, ...relations], lineage, relations };
}

/** The grid's columns for a target, or null while a document it needs has not loaded. */
export function seedTargetColumns(rows: readonly ElementSummary[], targetId: string, docOf: (id: string) => ModelJson | undefined): GridColumn[] | null {
  const plan = seedTargetPlan(rows, targetId);
  const names = new Map(rows.map((r) => [r.id, r.displayName || r.name]));
  const nameOf = (id: string) => names.get(id);
  if (plan.target?.kind === "relation") {
    const relation = docOf(targetId) as RelationDoc | undefined;
    return relation ? relationGridColumns(relation, nameOf) : null;
  }
  if (plan.target?.kind !== "entity") return null;
  const docs = plan.documents.map(docOf);
  if (docs.some((d) => !d)) return null;
  const attributes = plan.lineage
    .map((_, i) => docs[i])
    .reverse()
    .flatMap((d) => attributesOf(d));
  const seeded = new Set(rows.filter((r) => r.kind === "seed" && r.target).map((r) => r.target as string));
  const relations = docs.slice(plan.lineage.length) as unknown as RelationDoc[];
  return entityGridColumns(
    attributes,
    entitySeedEnds(new Set(plan.lineage), relations, (id) => seeded.has(id)),
    nameOf,
  );
}

/** The seeds of a target, by name. */
export function seedsOf(rows: readonly ElementSummary[], targetId: string): string[] {
  return rows
    .filter((r) => r.kind === "seed" && r.target === targetId)
    .sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id))
    .map((r) => r.id);
}

/** The seeds whose rows an end naming `entity` can pick: the entity's own and its subtypes'. */
export function pickableSeeds(rows: readonly ElementSummary[], entity: string): string[] {
  const byId = new Map(rows.map((r) => [r.id, r]));
  return rows
    .filter((r) => {
      if (r.kind !== "seed" || !r.target) return false;
      const target = byId.get(r.target);
      return r.target === entity || (target?.kind === "entity" && baseChain(byId, target.base).includes(entity));
    })
    .sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id))
    .map((r) => r.id);
}

export interface SeedTarget {
  target?: ElementSummary;
  columns: GridColumn[] | null;
  seeds: string[];
  endOptions: ReadonlyMap<string, EndOption[]>;
}

/** The grid's inputs for an entity or a relation, following its drafts (an attribute just added shows at once). */
export function useSeedTarget(targetId: string): SeedTarget {
  const { store } = useServices();
  const index = useIndex();
  const rows = useMemo(() => index.data ?? [], [index.data]);
  const plan = useMemo(() => seedTargetPlan(rows, targetId), [rows, targetId]);
  const loaded = useElements(plan.documents);
  const drafts = useEditor(store, (s) => s.drafts);
  const columns = useMemo(
    () => seedTargetColumns(rows, targetId, (id) => drafts[id]?.json ?? loaded.byId.get(id)?.json),
    [rows, targetId, drafts, loaded.byId],
  );
  const ends = useMemo(() => [...new Set((columns ?? []).map((c) => c.end).filter((e): e is string => !!e))], [columns]);
  const endSeeds = useMemo(() => ends.map((e) => [e, pickableSeeds(rows, e)] as const), [ends, rows]);
  const endLoaded = useElements(useMemo(() => [...new Set(endSeeds.flatMap(([, s]) => s))], [endSeeds]));
  const endOptions = useMemo(() => {
    const out = new Map<string, EndOption[]>();
    for (const [entity, seeds] of endSeeds) {
      const docs = seeds.map((id) => (drafts[id]?.json ?? endLoaded.byId.get(id)?.json) as unknown as SeedDoc | undefined).filter((d): d is SeedDoc => !!d);
      out.set(entity, endRowOptions(docs));
    }
    return out;
  }, [endSeeds, drafts, endLoaded.byId]);
  const seeds = useMemo(() => seedsOf(rows, targetId), [rows, targetId]);
  return { target: plan.target, columns, seeds, endOptions };
}

/**
 * New seed (the user's request, never automatic): a seed of the entity or relation named after it, listing every
 * column the grid shows. One undoable create; null (with a notice) when it cannot be made.
 */
export async function createTargetSeed(services: AppServices, targetId: string): Promise<string | null> {
  const { queryClient, store } = services;
  const rows = await queryClient.fetchQuery(indexQuery);
  const plan = seedTargetPlan(rows, targetId);
  if (!plan.target || !isSeedTargetKind(plan.target.kind)) return null;
  const docs = await Promise.all(plan.documents.map((id) => queryClient.fetchQuery(elementQuery(id))));
  const byId = new Map(plan.documents.map((id, i) => [id, store.getState().drafts[id]?.json ?? docs[i].json]));
  const columns = seedTargetColumns(rows, targetId, (id) => byId.get(id)) ?? [];
  if (!columns.length) {
    store.getState().notify(`${plan.target.name} has no attributes yet: add one before its seed data.`, "error");
    return null;
  }
  const seed = newSeedDocument(
    newId(),
    plan.target,
    columns.map((c) => c.key),
  ) as unknown as ModelJson;
  const result = await endpoints.createElement(seed);
  if (result.outcome !== "saved") {
    store.getState().notify(`The seed could not be created: ${result.diagnostics[0]?.message ?? result.outcome}`, "error");
    return null;
  }
  applySaveResult(queryClient, result);
  store
    .getState()
    .pushUndo({ label: `New seed ${plan.target.name}`, ids: [String(seed.id)], before: [null], after: [clone(seed)], afterHashes: [result.hash] });
  return String(seed.id);
}
