// The data the Storage tab and the storage dialogs read (erratum E43): the documents an entity's bindable attributes come
// from, each database's materialize status and source columns, and the batches that apply a storage gesture as one undo
// step (a materialize operation, or updates of several entities).
import { useMemo } from "react";
import { useQueries, useQuery, type QueryClient } from "@tanstack/react-query";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, invalidateResolved, keys, loadElement, tableDetailQuery, useDatabaseView, useElements, useIndex } from "@/api/queries";
import type { BatchRequest, ElementSummary, MaterializeStatus, ModelJson, TableView } from "@/api/types";
import type { AppServices } from "@/app/context";
import { indexLookup } from "@/model/index";
import { clone } from "@/lib/json";
import { baseChain } from "@/editors/related";
import { sourcesOf, storageAttributes, tableSource, type BindingSource, type StorageAttribute } from "./fieldMap";

type Json = Record<string, unknown>;

/**
 * The documents the bindable attributes of some entities come from (the entities, their bases, the value objects their
 * attributes use, the stereotypes and the relations at their ends), with the editor's drafts over them (`overrides`), and
 * each entity's attributes. `pending` while a first read is out.
 */
export function useStorageAttributes(entityIds: readonly string[], overrides?: ReadonlyMap<string, Json>) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const first = useMemo(() => {
    const ids = new Set<string>();
    for (const id of entityIds) {
      ids.add(id);
      for (const b of baseChain(lookup.byId, lookup.byId.get(id)?.base)) ids.add(b);
    }
    const chain = new Set(ids);
    for (const r of lookup.ofKind("relation")) if ((r.ends ?? []).some((e) => chain.has(e.entity))) ids.add(r.id);
    for (const s of lookup.ofKind("stereotype")) ids.add(s.id);
    return [...ids];
  }, [entityIds, lookup]);
  const docs1 = useElements(first);
  const voIds = useMemo(() => {
    const out = new Set<string>();
    for (const doc of docs1.byId.values()) {
      const json = doc.json as Json;
      if (json.kind !== "entity") continue;
      for (const a of (json.attributes as { type?: unknown }[] | undefined) ?? []) {
        const ref = typeof a.type === "object" && a.type ? (a.type as { ref?: string }).ref : undefined;
        if (ref && lookup.byId.get(ref)?.kind === "value-object") out.add(ref);
      }
    }
    return [...out].sort();
  }, [docs1.byId, lookup]);
  const docs2 = useElements(voIds);
  return useMemo(() => {
    const docs = new Map<string, Json>();
    // Names of the entities at the far end of a relation, from the index (their documents are not needed).
    for (const e of lookup.ofKind("entity")) docs.set(e.id, { kind: "entity", id: e.id, name: e.name });
    for (const d of [...docs1.byId.values(), ...docs2.byId.values()]) docs.set(String((d.json as Json).id), d.json as Json);
    for (const [id, json] of overrides ?? []) docs.set(id, json);
    const attributes = new Map<string, StorageAttribute[]>();
    for (const id of entityIds) attributes.set(id, storageAttributes(id, docs));
    return { docs, attributes, pending: docs1.pending || docs2.pending };
  }, [lookup, docs1, docs2, overrides, entityIds]);
}

/** ["tables", db, "materialize"]: under the database's tables key, so a model change that reshapes tables refetches it. */
export function materializeQuery(databaseId: string) {
  return {
    queryKey: [...keys.databaseTables(databaseId), "materialize"] as const,
    queryFn: (): Promise<MaterializeStatus> => endpoints.getMaterializeStatus(databaseId),
    staleTime: 10_000,
  };
}

export function useMaterializeStatus(databaseId: string | null) {
  return useQuery({ ...materializeQuery(databaseId ?? ""), enabled: !!databaseId });
}

const combineStatuses = (results: { data?: MaterializeStatus; isPending: boolean }[]) => ({
  statuses: results.map((r) => r.data).filter((s): s is MaterializeStatus => !!s),
  pending: results.some((r) => r.isPending),
});

/** Every database's materialize status, in the index's database order. */
export function useMaterializeStatuses(databaseIds: readonly string[], enabled = true) {
  return useQueries({ queries: databaseIds.map((id) => ({ ...materializeQuery(id), enabled })), combine: combineStatuses });
}

/**
 * The entities the explorer's storage chip keeps: domain only (no binding to any database) or bound to one. Undefined while the
 * chip is off; empty until every database has answered.
 */
export function useStorageChipIds(chip: "domain-only" | "bound" | null): string[] | undefined {
  const index = useIndex();
  const databases = useMemo(() => databasesOf(index.data).map((d) => d.id), [index.data]);
  const { statuses, pending } = useMaterializeStatuses(databases, !!chip);
  const signature = statuses.map((s) => `${s.database}:${s.entities.length}:${s.entities.map((e) => e.id).join(",")}`).join("|");
  return useMemo(() => {
    if (!chip) return undefined;
    if (pending) return [];
    const unbound = statuses.map((s) => new Set(s.entities.map((e) => e.id)));
    const entities = indexLookup(index.data).ofKind("entity");
    const domainOnly = (id: string) => unbound.every((set) => set.has(id));
    return entities.filter((e) => (chip === "domain-only" ? domainOnly(e.id) : !domainOnly(e.id))).map((e) => e.id);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the statuses are compared by their signature
  }, [chip, pending, signature, index.data]);
}

/**
 * The tables, views and queries of a database a binding can read. The database view answers while the model validates; when it
 * does not (a binding still being built is an error), the last view stays, and a table source is read on its own.
 */
export function useBindingSources(databaseId: string | null, sourceRef?: string | null) {
  const view = useDatabaseView(databaseId);
  const sources = useMemo(() => sourcesOf(view.data?.view), [view.data]);
  const known = !!sourceRef && sources.some((s) => s.id === sourceRef);
  const detail = useQuery({ ...tableDetailQuery(databaseId ?? "", sourceRef ?? ""), enabled: !!databaseId && !!sourceRef && !known && !view.isPending });
  return useMemo(() => {
    const extra: BindingSource[] = !known && detail.data ? [tableSource(detail.data as TableView)] : [];
    return { sources: [...sources, ...extra], pending: view.isPending, stale: view.data?.stale ?? false, dialect: view.data?.view?.dialect ?? null };
  }, [sources, known, detail.data, view.isPending, view.data]);
}

export function databasesOf(rows: readonly ElementSummary[] | undefined): ElementSummary[] {
  return indexLookup(rows)
    .ofKind("database")
    .slice()
    .sort((a, b) => a.name.localeCompare(b.name));
}

/** The entities of a domain and its sub-domains, by name. */
export function entitiesOfDomain(rows: readonly ElementSummary[] | undefined, domain: string): ElementSummary[] {
  const lookup = indexLookup(rows);
  const inside = (pkg: string | null | undefined): boolean => {
    const seen = new Set<string>();
    let at = pkg ?? null;
    while (at && !seen.has(at)) {
      if (at === domain) return true;
      seen.add(at);
      at = lookup.byId.get(at)?.package ?? null;
    }
    return false;
  };
  return lookup
    .ofKind("entity")
    .filter((e) => inside(e.package))
    .sort((a, b) => a.name.localeCompare(b.name));
}

// ------------------------------------------------------------------ committing

async function current(services: AppServices, id: string) {
  await services.drafts.flush(id);
  return services.queryClient.fetchQuery({ queryKey: keys.element(id), queryFn: () => loadElement(id), staleTime: 0 });
}

function refreshPhysical(qc: QueryClient) {
  void qc.invalidateQueries({ queryKey: keys.tables });
  void qc.invalidateQueries({ queryKey: keys.validation });
  invalidateResolved(qc);
}

const reason = (result: Awaited<ReturnType<typeof endpoints.applyBatch>>, label: string): string | null => {
  if (!endpoints.isBatchResult(result)) return `${label} failed: ${result.diagnostics[0]?.message ?? "the batch did not parse"}`;
  if (result.outcome === "saved") return null;
  const failed = result.items.find((i) => i.outcome !== "saved");
  return `${label} failed: ${failed?.diagnostics[0]?.message ?? result.outcome}`;
};

/**
 * Applies one materialize operation as one batch and one undo step: the documents it will update or delete are read first (their
 * drafts saved), so undo restores them (the batch expects them as read), and what it creates is deleted; the new elements' ids
 * are the batch result's, never the preview's. Resolves to null when saved, else the reason.
 */
export async function commitMaterialize(
  services: AppServices,
  label: string,
  operation: Json,
  touched: { updates: readonly string[]; deletes: readonly string[] },
): Promise<string | null> {
  const before = new Map<string, ModelJson>();
  // The batch expects the documents as read here: one saved meanwhile is a conflict, so undo never restores a stale version.
  const expectedHashes: Record<string, string> = {};
  for (const id of [...touched.updates, ...touched.deletes]) {
    const doc = await current(services, id);
    before.set(id, clone(doc.json as ModelJson));
    expectedHashes[id] = doc.hash;
  }
  const result = await endpoints.applyBatch({ operations: [{ ...operation, expectedHashes }] } as unknown as BatchRequest);
  const failed = reason(result, label);
  if (failed || !endpoints.isBatchResult(result)) return failed;
  applyBatchResult(services.queryClient, result);
  // Undo restores the updated documents first (an entity drops its binding), then deletes what was created (the table the
  // binding named) and recreates what was deleted, so no delete is refused for a reference the same undo removes.
  // Created documents go referrer first: a relation mapping before its relation, a relation before its entities, a binding's
  // entity before its table.
  const KIND_ORDER = ["mapping", "relation", "entity", "table"];
  const kindRank = (i: (typeof result.items)[number]) => KIND_ORDER.indexOf(String((i.current?.json as Json | undefined)?.kind ?? ""));
  const rank = (i: (typeof result.items)[number]) => (before.has(i.id!) && i.current ? 0 : !before.has(i.id!) ? 1 : 2);
  const items = result.items.filter((i) => i.id).sort((x, y) => rank(x) - rank(y) || (rank(x) === 1 ? kindRank(x) - kindRank(y) : 0));
  services.store.getState().pushUndo({
    label,
    ids: items.map((i) => i.id!),
    before: items.map((i) => (before.has(i.id!) ? clone(before.get(i.id!)!) : null)),
    after: items.map((i) => (i.current ? clone(i.current.json as ModelJson) : null)),
    afterHashes: items.map((i) => i.hash),
  });
  refreshPhysical(services.queryClient);
  return null;
}

/** Rewrites several entities in one batch and one undo step (`change` returns the new document, or null to leave one alone). */
export async function commitEntities(
  services: AppServices,
  label: string,
  ids: readonly string[],
  change: (json: Json) => Json | null,
): Promise<string | null> {
  const ops: { op: "update"; id: string; expectedHash: string; element: ModelJson }[] = [];
  const before: ModelJson[] = [];
  for (const id of ids) {
    const doc = await current(services, id);
    const next = change(clone(doc.json as Json));
    if (!next) continue;
    ops.push({ op: "update", id, expectedHash: doc.hash, element: next as ModelJson });
    before.push(clone(doc.json as ModelJson));
  }
  if (!ops.length) return null;
  const result = await endpoints.applyBatch({ operations: ops } as unknown as BatchRequest);
  const failed = reason(result, label);
  if (failed || !endpoints.isBatchResult(result)) return failed;
  applyBatchResult(services.queryClient, result);
  services.store.getState().pushUndo({
    label,
    ids: ops.map((o) => o.id),
    before,
    after: ops.map((o) => clone(o.element)),
    afterHashes: ops.map((_, i) => result.items[i]?.hash ?? null),
  });
  refreshPhysical(services.queryClient);
  return null;
}

/** Removes the given entities' bindings to one database (every binding when `database` is null), in one batch. */
export function removeBindings(services: AppServices, ids: readonly string[], database: string | null): Promise<string | null> {
  return commitEntities(services, "Remove bindings", ids, (json) => {
    const list = (json.bindings as { database: string }[] | undefined) ?? [];
    const kept = list.filter((b) => database !== null && b.database !== database);
    if (kept.length === list.length) return null;
    if (kept.length) json.bindings = kept;
    else delete json.bindings;
    return json;
  });
}
