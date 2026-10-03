// TanStack Query keys, hooks and cache patching (phase2-design.md 4.3). Server state lives only
// here; realtime (src/realtime/sync.ts) and successful writes patch it.
import { currentContentLocale } from "@/l10n/contentLocale";
import { QueryClient, useQueries, useQuery, useQueryClient, type QueryKey } from "@tanstack/react-query";
import * as endpoints from "./endpoints";
import type {
  DatabaseTablesResult,
  DatabaseViewResult,
  TableView,
  ElementDocument,
  ElementKind,
  ElementSummary,
  SaveResult,
  BatchResult,
  ChangeSet,
} from "./types";
import { summaryFromDocument } from "@/model/model";
import { createElementLoader, type ElementLoader } from "./elementLoader";
import { browserIndexStore, createIndexLoader, type IndexLoader } from "./indexLoader";
import { perfStart } from "@/lib/perf";
import { indexChangesOf } from "@/realtime/events";
import { applyIndexPatch } from "./indexPatch";

export { applyIndexPatch, indexPatchOf, patchesBetween, type IndexPatch } from "./indexPatch";

export const keys = {
  session: ["session"] as const,
  project: ["project"] as const,
  settings: ["settings"] as const,
  index: ["index"] as const,
  element: (id: string) => ["element", id] as const,
  references: (id: string) => ["references", id] as const,
  validation: ["validation"] as const,
  validationRules: ["validationRules"] as const,
  databaseViews: ["databaseView"] as const,
  databaseView: (id: string) => ["databaseView", id] as const,
  /** Under the previews' key, so a model change renders the query again once the edits settle, as the DDL preview does. */
  querySql: (id: string, dialect: string | null) => ["preview", "query-sql", id, dialect ?? ""] as const,
  tables: ["tables"] as const,
  databaseTables: (id: string) => ["tables", id] as const,
  previews: ["preview"] as const,
  preview: (pack: string, unit: string, elementId: string | null) => ["preview", pack, unit, elementId] as const,
  plan: (id: string) => ["plan", id] as const,
  job: (id: string) => ["job", id] as const,
  jobs: ["jobs"] as const,
  packs: ["packs"] as const,
  pack: (name: string) => ["packs", name] as const,
  packOutputs: (name: string) => ["packs", name, "outputs"] as const,
  extensions: ["extensions"] as const,
  extensionFile: (path: string) => ["extensions", "file", path] as const,
};

export function createQueryClient(): QueryClient {
  const qc = new QueryClient({
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
  // Every write to ["index"] keeps its array: patches are registered by array identity (api/indexPatch.ts), which
  // structural sharing would replace with a copy.
  qc.setQueryDefaults(keys.index, { structuralSharing: false });
  return qc;
}

export const useSession = () => useQuery({ queryKey: keys.session, queryFn: endpoints.getSession });
export const useProject = () => useQuery({ queryKey: keys.project, queryFn: endpoints.getProject });
export const useSettings = () => useQuery({ queryKey: keys.settings, queryFn: endpoints.getSettings });
export const usePacks = () => useQuery({ queryKey: keys.packs, queryFn: endpoints.listPacks });
export const usePack = (name: string | null) => useQuery({ queryKey: keys.pack(name ?? ""), queryFn: () => endpoints.getPack(name!), enabled: !!name });
/** The model's extension files (custom property schemas and script rules), with what is wrong with each one alone. */
export const useExtensionFiles = () => useQuery({ queryKey: keys.extensions, queryFn: endpoints.listExtensionFiles });
export const usePackOutputs = (name: string | null, enabled = true) =>
  useQuery({ queryKey: keys.packOutputs(name ?? ""), queryFn: () => endpoints.getPackOutputs(name!), enabled: !!name && enabled });

// The loaders are per page; tests replace them with setLoaders.
let elementLoader: ElementLoader = createElementLoader({ read: endpoints.readElements, get: endpoints.getElement });
// The index in the content locale in effect (reference-types-seeds-localization.md 3.8 and 3.10): `?locale=` fills
// displayName from that locale's fallback chain; its ETag covers the locale, so switching never answers 304.
const fetchIndex = (etag: string | null) => endpoints.getModelIndexText(etag, currentContentLocale());
let indexLoader: IndexLoader = createIndexLoader({ fetch: fetchIndex, store: browserIndexStore() });

/** Replaces the element and index loaders (tests; a new API client). */
export function resetLoaders(options: { indexStore?: boolean } = {}): void {
  elementLoader = createElementLoader({ read: endpoints.readElements, get: endpoints.getElement });
  indexLoader = createIndexLoader({ fetch: fetchIndex, store: options.indexStore === false ? null : browserIndexStore() });
}

/** One element document, read in a batch with every other element asked for in the same task. */
export const loadElement = (id: string): Promise<ElementDocument> => elementLoader.load(id);

/** The index, conditional on the last ETag (E5e). */
export const loadIndex = (): Promise<ElementSummary[]> => indexLoader.load();

/**
 * ["index"]: sent with If-None-Match, and without structural sharing, which would walk every row
 * on each refetch for nothing (explorer-redesign.md 4.3).
 */
export const indexQuery = { queryKey: keys.index, queryFn: loadIndex, structuralSharing: false } as const;
export const useIndex = () => useQuery(indexQuery);
export const useValidation = () => useQuery({ queryKey: keys.validation, queryFn: () => endpoints.validate({}) });
/** The built-in rule catalog: fixed for a running engine, so it is fetched once. */
export const useValidationRules = () => useQuery({ queryKey: keys.validationRules, queryFn: endpoints.listValidationRules, staleTime: Infinity });

/** Element documents leave the cache 5 minutes after their last observer (EX 4.2); the rest keeps the default. */
export const ELEMENT_GC_TIME = 5 * 60_000;

export function elementQuery(id: string) {
  return { queryKey: keys.element(id), queryFn: () => loadElement(id), gcTime: ELEMENT_GC_TIME };
}

/** How long a pointer or the tree's focus rests on a row before its document is read ahead (EX 3.5). */
export const PREFETCH_DELAY_MS = 300;

/** Reads an element document ahead (no-op when it is cached); the batched loader groups concurrent reads. */
export function prefetchElement(qc: QueryClient, id: string): void {
  void qc.prefetchQuery(elementQuery(id));
}

export function useElement(id: string | null | undefined) {
  return useQuery({ ...elementQuery(id ?? ""), enabled: !!id });
}

/** Several element documents at once (diagram members, relation lists), read in batches of 200. */
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

/** A database view as the Database screen shows it: `stale` when the model has errors and `view` is the last one resolved. */
export type DatabaseViewState = DatabaseViewResult & { stale: boolean };

/**
 * The next view from the previous one and a new answer: an answer without a view (the model has errors, such as a foreign
 * key column pinned to another type than the column it references, MQ4005) keeps the last resolved tables, marked stale,
 * with the new diagnostics, so the screen, its grid and the table inspector stay up to fix it in place.
 */
export function mergeDatabaseView(previous: DatabaseViewState | undefined, next: DatabaseViewResult): DatabaseViewState {
  if (!next.view && previous?.view) return { view: previous.view, diagnostics: next.diagnostics, stale: true };
  return { ...next, stale: false };
}

/** One database's resolved view, kept stale over a model with errors (`mergeDatabaseView`). */
export function databaseViewQuery(qc: QueryClient, id: string) {
  return {
    queryKey: keys.databaseView(id),
    queryFn: async (): Promise<DatabaseViewState> =>
      mergeDatabaseView(qc.getQueryData<DatabaseViewState>(keys.databaseView(id)), await endpoints.getDatabaseView(id)),
  };
}

export function useDatabaseView(id: string | null) {
  const qc = useQueryClient();
  return useQuery({ ...databaseViewQuery(qc, id ?? ""), enabled: !!id });
}

/**
 * A query's SQL for a dialect (null: its database's), rendered again after every model change, the last answer for the same query
 * and dialect showing meanwhile. Another query or dialect shows nothing until its own answer comes (never the previous one's SQL
 * under the new label). Only while the index has the query: a query just deleted is not asked for (it would answer 404).
 */
export function useQuerySql(id: string | null, dialect: string | null) {
  const index = useIndex();
  const known = !!id && !!index.data?.some((r) => r.id === id);
  return useQuery({
    queryKey: keys.querySql(id ?? "", dialect),
    queryFn: () => endpoints.getQuerySql(id!, dialect),
    enabled: known,
  });
}

// ---------------------------------------------------------------- table summaries (E5c)

/** A database's tables as the tree shows them: the last complete list while the model has errors. */
export interface TablesState extends DatabaseTablesResult {
  /** True when `tables` is an earlier complete list because the latest answer was partial. */
  stale: boolean;
}

/**
 * The next state from the previous one and a new answer: a complete answer replaces everything; a
 * partial one keeps the previous complete tables (marked stale) with the new diagnostics, or shows
 * what resolved when nothing complete was ever seen.
 */
export function mergeTables(previous: TablesState | undefined, next: DatabaseTablesResult): TablesState {
  if (!next.partial) return { ...next, stale: false };
  if (previous && (!previous.partial || previous.stale)) return { tables: previous.tables, diagnostics: next.diagnostics, partial: true, stale: true };
  return { ...next, stale: false };
}

export function tablesQuery(qc: QueryClient, id: string) {
  return {
    queryKey: keys.databaseTables(id),
    queryFn: async (): Promise<TablesState> => {
      const end = perfStart("tables:fetch");
      const next = await endpoints.getDatabaseTables(id);
      end({ database: id, tables: next.tables.length, partial: next.partial });
      return mergeTables(qc.getQueryData<TablesState>(keys.databaseTables(id)), next);
    },
    structuralSharing: false,
  } as const;
}

/**
 * ["tables", dbId, "detail", key]: one table's detail (E5f). Under the database's key, so the debounced invalidation
 * of ["tables", dbId] refetches it too. A server without E5f (404 or 405) answers from the database read (`/view`).
 */
export function tableDetailQuery(dbId: string, key: string) {
  return {
    queryKey: [...keys.databaseTables(dbId), "detail", key] as const,
    queryFn: async (): Promise<TableView | null> => {
      const end = perfStart("table:fetch");
      try {
        const result = await endpoints.getDatabaseTable(dbId, key);
        end({ database: dbId, key, columns: result.table?.columns.length ?? 0 });
        return result.table;
      } catch (e) {
        const status = (e as { status?: number }).status;
        if (status !== 404 && status !== 405) throw e;
        const view = await endpoints.getDatabaseView(dbId);
        end({ database: dbId, key, fallback: true });
        return view.view?.tables.find((t) => t.key === key) ?? null;
      }
    },
    staleTime: 30_000,
  };
}

/** ["tables", dbId]: the table summaries of one database, kept on screen while a refetch runs. */
export function useDatabaseTables(id: string | null, options: { fetch?: boolean } = {}) {
  const qc = useQueryClient();
  return useQuery({ ...tablesQuery(qc, id ?? ""), enabled: !!id && options.fetch !== false, placeholderData: (previous) => previous });
}

/** Loads every database's table summaries in the background, one at a time, at low priority. */
export function prefetchAllTables(qc: QueryClient, rows: readonly ElementSummary[]): void {
  const ids = rows.filter((r) => r.kind === "database").map((r) => r.id);
  const idle = (run: () => void) => (typeof requestIdleCallback === "function" ? requestIdleCallback(run, { timeout: 1000 }) : setTimeout(run, 50));
  const next = (i: number) => {
    if (i >= ids.length) return;
    idle(() => {
      void qc.prefetchQuery(tablesQuery(qc, ids[i])).finally(() => next(i + 1));
    });
  };
  next(0);
}

/** The kinds whose changes can change a resolved table (explorer-redesign.md 4.1). */
export const TABLE_SHAPING_KINDS: ReadonlySet<ElementKind> = new Set<ElementKind>([
  "package",
  "entity",
  "relation",
  "enum",
  "value-object",
  "scalar-type",
  "database",
  "table",
  "view",
  "sequence",
  "routine",
  "database-type",
  "sql-object",
  "mapping",
]);

/** True when a change set touches a kind that shapes tables (deleted ids are looked up in the index). */
export function shapesTables(set: ChangeSet, rows: readonly ElementSummary[] | undefined): boolean {
  if (set.truncated) return true;
  if (set.changed.some((c) => TABLE_SHAPING_KINDS.has(c.kind))) return true;
  if (set.deleted.length === 0) return false;
  if (!rows) return true;
  const deleted = new Set(set.deleted);
  return rows.some((r) => deleted.has(r.id) && TABLE_SHAPING_KINDS.has(r.kind));
}

export const usePreview = (pack: string, unit: string, elementId: string | null, enabled = true) =>
  useQuery({
    queryKey: keys.preview(pack, unit, elementId),
    queryFn: () => endpoints.previewTemplate({ pack, unit, elementId }),
    enabled,
    placeholderData: (previous) => previous,
  });

export const usePlan = (id: string | null) => useQuery({ queryKey: keys.plan(id ?? ""), queryFn: () => endpoints.getPlan(id!, true), enabled: !!id });

export const useJob = (id: string | null) => useQuery({ queryKey: keys.job(id ?? ""), queryFn: () => endpoints.getJob(id!), enabled: !!id });

export const useJobs = () => useQuery({ queryKey: keys.jobs, queryFn: endpoints.listJobs });

// ---------------------------------------------------------------- cache patching

/** Replaces or inserts one index row (a registered patch: the tree and the search worker follow it). */
export function upsertIndexRow(qc: QueryClient, row: ElementSummary): void {
  qc.setQueryData<readonly ElementSummary[]>(keys.index, (rows) => (rows ? applyIndexPatch(rows, [row], []) : rows));
}

/**
 * Applies a model.changed event's summaries (E5d) and deletions to the cached index in one pass,
 * without a request: add, change, rename and move upsert the row at its path's position, as a
 * refetch would answer. Returns false when the event cannot be applied in place (a change without
 * a summary, or a truncated set), and the caller refetches the index with its ETag instead.
 */
export function patchIndex(qc: QueryClient, set: ChangeSet): boolean {
  const changes = indexChangesOf(set);
  if (!changes) return false;
  const rows = qc.getQueryData<readonly ElementSummary[]>(keys.index);
  if (!rows) return true;
  const next = applyIndexPatch(rows, changes.upserts, changes.deleted);
  if (next !== rows) qc.setQueryData(keys.index, next);
  for (const id of changes.deleted) qc.removeQueries({ queryKey: keys.element(id), exact: true });
  return true;
}

export function removeIndexRows(qc: QueryClient, ids: readonly string[]): void {
  if (ids.length === 0) return;
  qc.setQueryData<readonly ElementSummary[]>(keys.index, (rows) => (rows ? applyIndexPatch(rows, [], ids) : rows));
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
