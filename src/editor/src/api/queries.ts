// TanStack Query keys, hooks and cache patching (phase2-design.md 4.3). Server state lives only
// here; realtime (src/realtime/sync.ts) and successful writes patch it.
import { QueryClient, useQueries, useQuery, type QueryKey } from "@tanstack/react-query";
import * as endpoints from "./endpoints";
import type { ElementDocument, ElementSummary, SaveResult, BatchResult, ChangeSet } from "./types";
import { summaryFromDocument } from "@/model/model";

export const keys = {
  session: ["session"] as const,
  project: ["project"] as const,
  settings: ["settings"] as const,
  index: ["index"] as const,
  element: (id: string) => ["element", id] as const,
  references: (id: string) => ["references", id] as const,
  validation: ["validation"] as const,
  databaseViews: ["databaseView"] as const,
  databaseView: (id: string) => ["databaseView", id] as const,
  previews: ["preview"] as const,
  preview: (pack: string, unit: string, elementId: string | null) => ["preview", pack, unit, elementId] as const,
  plan: (id: string) => ["plan", id] as const,
  job: (id: string) => ["job", id] as const,
  jobs: ["jobs"] as const,
};

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Realtime keeps server state current; nothing refetches on focus or on a timer.
        staleTime: Infinity,
        gcTime: 10 * 60_000,
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
        retry: (count, error) => count < 2 && !(error instanceof Error && "status" in error && (error as { status: number }).status < 500),
      },
    },
  });
}

export const useSession = () => useQuery({ queryKey: keys.session, queryFn: endpoints.getSession });
export const useProject = () => useQuery({ queryKey: keys.project, queryFn: endpoints.getProject });
export const useSettings = () => useQuery({ queryKey: keys.settings, queryFn: endpoints.getSettings });
export const useIndex = () => useQuery({ queryKey: keys.index, queryFn: endpoints.getModelIndex });
export const useValidation = () => useQuery({ queryKey: keys.validation, queryFn: () => endpoints.validate({}) });

export function elementQuery(id: string) {
  return { queryKey: keys.element(id), queryFn: () => endpoints.getElement(id) };
}

export function useElement(id: string | null | undefined) {
  return useQuery({ ...elementQuery(id ?? ""), enabled: !!id });
}

/** Several element documents at once (diagram members, relation lists). */
export function useElements(ids: string[]) {
  return useQueries({
    queries: ids.map((id) => ({ ...elementQuery(id), enabled: !!id })),
    combine: (results) => {
      const byId = new Map<string, ElementDocument>();
      results.forEach((r, i) => {
        if (r.data) byId.set(ids[i], r.data);
      });
      return { byId, pending: results.some((r) => r.isPending), error: results.find((r) => r.error)?.error ?? null };
    },
  });
}

export const useReferences = (id: string | null) =>
  useQuery({ queryKey: keys.references(id ?? ""), queryFn: () => endpoints.getReferences(id!), enabled: !!id });

export const useDatabaseView = (id: string | null) =>
  useQuery({ queryKey: keys.databaseView(id ?? ""), queryFn: () => endpoints.getDatabaseView(id!), enabled: !!id });

export const usePreview = (pack: string, unit: string, elementId: string | null, enabled = true) =>
  useQuery({
    queryKey: keys.preview(pack, unit, elementId),
    queryFn: () => endpoints.previewTemplate({ pack, unit, elementId }),
    enabled,
    placeholderData: (previous) => previous,
  });

export const usePlan = (id: string | null) => useQuery({ queryKey: keys.plan(id ?? ""), queryFn: () => endpoints.getPlan(id!), enabled: !!id });

export const useJob = (id: string | null) => useQuery({ queryKey: keys.job(id ?? ""), queryFn: () => endpoints.getJob(id!), enabled: !!id });

export const useJobs = () => useQuery({ queryKey: keys.jobs, queryFn: endpoints.listJobs });

// ---------------------------------------------------------------- cache patching

/** Replaces or inserts one index row. */
export function upsertIndexRow(qc: QueryClient, row: ElementSummary): void {
  qc.setQueryData<ElementSummary[]>(keys.index, (rows) => {
    if (!rows) return rows;
    const at = rows.findIndex((r) => r.id === row.id);
    if (at < 0) return [...rows, row];
    const next = rows.slice();
    next[at] = row;
    return next;
  });
}

export function removeIndexRows(qc: QueryClient, ids: readonly string[]): void {
  if (ids.length === 0) return;
  const drop = new Set(ids);
  qc.setQueryData<ElementSummary[]>(keys.index, (rows) => rows?.filter((r) => !drop.has(r.id)));
  for (const id of ids) qc.removeQueries({ queryKey: keys.element(id), exact: true });
}

/**
 * A successful save or create: seed ["element", id] from `current` and patch its index row at once
 * (kind, name, package, tags, category and stereotypes from json; hash and path).
 */
export function applySaveResult(qc: QueryClient, result: SaveResult): void {
  if (result.outcome !== "saved") return;
  if (result.current) {
    const id = (result.current.json as { id: string }).id;
    qc.setQueryData(keys.element(id), result.current);
    upsertIndexRow(qc, summaryFromDocument(result.current));
  }
  if (result.changes) removeIndexRows(qc, result.changes.deleted);
}

export function applyBatchResult(qc: QueryClient, result: BatchResult): void {
  if (result.outcome !== "saved") return;
  for (const item of result.items) applySaveResult(qc, item);
  if (result.changes) removeIndexRows(qc, result.changes.deleted);
}

/** Every query whose data depends on resolved tables. */
export function invalidateResolved(qc: QueryClient): void {
  void qc.invalidateQueries({ queryKey: keys.databaseViews });
  void qc.invalidateQueries({ queryKey: keys.previews });
}

/**
 * The part of a model.changed event the index cannot absorb: ids that are unknown or whose hash
 * differs from the cached row (a change from another window, the disk or the CLI). Rows with the
 * same hash are the echo of this window's own write.
 */
export function foreignChanges(rows: ElementSummary[] | undefined, set: ChangeSet): string[] {
  if (!rows) return set.changed.map((c) => c.id);
  const byId = new Map(rows.map((r) => [r.id, r.hash]));
  return set.changed.filter((c) => byId.get(c.id) !== c.hash).map((c) => c.id);
}

export type { QueryKey };
