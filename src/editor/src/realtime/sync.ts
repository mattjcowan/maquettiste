// Realtime → TanStack Query cache (phase2-design.md 4.3 and 4.5).
import type { QueryClient } from "@tanstack/react-query";
import type { ElementDocument, ElementSummary, RealtimeModelChanged } from "@/api/types";
import { keys, patchIndex, removeIndexRows, shapesTables } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { EditorStore } from "@/state/store";
import type { DraftManager } from "@/state/drafts";
import type { RealtimeClient } from "./events";
import type { JobTracker } from "./jobs";
import { currentContentLocale } from "@/l10n/contentLocale";
import { applyTranslations, localizationEnabled } from "@/l10n/model";
import { l10nKeys } from "@/l10n/queries";
import type { LocalizationStatus } from "@/api/types";

export interface SyncDeps {
  realtime: RealtimeClient;
  queryClient: QueryClient;
  store: EditorStore;
  drafts: DraftManager;
  jobs: JobTracker;
  reload?: () => void;
  /** Debounces (ms): index refetch 250, preview refetch 400, table summaries 1500. */
  delays?: { index: number; preview: number; tables?: number };
}

export function connectRealtime(deps: SyncDeps): () => void {
  const { realtime, queryClient: qc, store, drafts, jobs } = deps;
  const delays = deps.delays ?? { index: 250, preview: 400 };
  const reload = deps.reload ?? (() => window.location.reload());
  let indexTimer: ReturnType<typeof setTimeout> | null = null;
  let previewTimer: ReturnType<typeof setTimeout> | null = null;
  let tablesTimer: ReturnType<typeof setTimeout> | null = null;

  // ["tables", dbId] on its own timer (explorer-redesign.md 4.1): one whole-model resolve on the
  // server per refetch, so it waits for the edits to settle; the tree keeps the last tables meanwhile.
  const refetchTablesSoon = () => {
    if (tablesTimer) clearTimeout(tablesTimer);
    tablesTimer = setTimeout(() => {
      tablesTimer = null;
      void qc.invalidateQueries({ queryKey: keys.tables });
    }, delays.tables ?? 1500);
  };

  const refetchIndexSoon = () => {
    if (indexTimer) clearTimeout(indexTimer);
    indexTimer = setTimeout(() => {
      indexTimer = null;
      void qc.invalidateQueries({ queryKey: keys.index, exact: true });
    }, delays.index);
  };
  const refreshResolved = () => {
    void qc.invalidateQueries({ queryKey: keys.databaseViews });
    if (previewTimer) clearTimeout(previewTimer);
    previewTimer = setTimeout(() => {
      previewTimer = null;
      void qc.invalidateQueries({ queryKey: keys.previews });
    }, delays.preview);
  };

  const offs: (() => void)[] = [];

  /**
   * RT 3.8: a translation save sends the content locale's display names, patched into the index in place; a cut event
   * refetches the index. Any change moves completeness, so the settings and the open entries are refetched when shown.
   */
  const onTranslations = (set: RealtimeModelChanged, locale: string | null) => {
    if (!localizationEnabled(qc.getQueryData<LocalizationStatus>(l10nKeys.status))) return;
    void qc.invalidateQueries({ queryKey: l10nKeys.status });
    // A changed element's entries change with it (a new seed row, a changed source text): refetch those shown.
    const ids = new Set(set.changed.map((c) => c.id));
    if (ids.size) void qc.invalidateQueries({ queryKey: l10nKeys.all, predicate: (q) => q.queryKey[2] === "owner" && ids.has(String(q.queryKey[3])) });
    if (!set.translations?.length && !set.translationsTruncated) {
      // An element change can change a fallback text in the content locale: refetch its names.
      if (locale && set.changed.length) refetchIndexSoon();
      return;
    }
    void qc.invalidateQueries({ queryKey: l10nKeys.all });
    if (set.translationsTruncated && locale) refetchIndexSoon();
    const rows = qc.getQueryData<readonly ElementSummary[]>(keys.index);
    if (!rows || !locale) return;
    const next = applyTranslations(rows, set.translations, locale);
    if (next !== rows) qc.setQueryData(keys.index, next);
  };

  offs.push(
    realtime.on("model.changed", async (event) => {
      const locale = currentContentLocale();
      const set = locale ? keepLocalizedNames(event, qc.getQueryData<ElementSummary[]>(keys.index)) : event;
      if (shapesTables(set, qc.getQueryData<ElementSummary[]>(keys.index))) refetchTablesSoon();
      onTranslations(set, locale);
      // E5d: every change carries its summary, so the index is patched in place with no request.
      const patched = patchIndex(qc, set);
      if (!patched) {
        removeIndexRows(qc, set.deleted);
        if (set.truncated) refetchIndexSoon();
      }
      for (const change of set.changed) {
        // An event for an id with a save in flight is compared after that save answers.
        if (drafts.isInFlight(change.id)) await drafts.whenSettled(change.id);
        if (patched) {
          // The index is current; only a cached document older than the change is dropped.
          const doc = qc.getQueryData<ElementDocument>(keys.element(change.id));
          if (doc && doc.hash !== change.hash && doc.hash !== change.summary?.hash) {
            void qc.invalidateQueries({ queryKey: keys.element(change.id), exact: true });
          }
          continue;
        }
        const rows = qc.getQueryData<ElementSummary[]>(keys.index);
        const row = rows?.find((r) => r.id === change.id);
        if (row && row.hash === change.hash) continue; // the echo of this window's own write
        refetchIndexSoon();
        void qc.invalidateQueries({ queryKey: keys.element(change.id), exact: true });
      }
      void qc.invalidateQueries({ queryKey: ["references"] });
      refreshResolved();
    }),
  );

  offs.push(
    realtime.on("validation.completed", (report) => {
      if (report.truncated) void qc.fetchQuery({ queryKey: keys.validation, queryFn: () => endpoints.validate({}), staleTime: 0 });
      else qc.setQueryData(keys.validation, report);
    }),
  );

  offs.push(
    realtime.on("project.changed", () => {
      // The localization block may have changed: locales, fallbacks and completeness kinds.
      void qc.invalidateQueries({ queryKey: l10nKeys.status });
      void qc.invalidateQueries({ queryKey: l10nKeys.all });
      void qc.invalidateQueries({ queryKey: keys.project });
      void qc.invalidateQueries({ queryKey: keys.settings });
      refreshResolved();
      refetchTablesSoon();
      // packs.<name> (enabled, output, parameters) lives in maquettiste.json.
      void qc.invalidateQueries({ queryKey: keys.packs });
    }),
    // A pack's files changed (the editor, the CLI, git): its units, files and outputs are read again.
    realtime.on("packs.changed", () => void qc.invalidateQueries({ queryKey: keys.packs })),
    // A template file changed: the open file's disk query refetches, and the Templates tab reloads an unedited
    // buffer or raises its conflict bar.
    realtime.on("templates.changed", (e) => void qc.invalidateQueries({ queryKey: keys.pack(e.pack) })),
  );

  offs.push(
    realtime.on("job.progress", (job) => {
      qc.setQueryData(keys.job(job.id), job);
      jobs.onProgress(job);
    }),
  );

  offs.push(
    realtime.on("job.completed", (summary) => {
      void jobs.onCompleted(summary);
    }),
  );

  offs.push(realtime.on("presence.changed", (payload) => store.getState().setPresence(payload.editors)));

  offs.push(
    realtime.on("site.deployed", (payload) => {
      if (drafts.hasUnsaved()) store.getState().setBanner({ kind: "deployed", text: `A new version of the editor is live (${payload.release}).` });
      else reload();
    }),
  );

  // The selection or workspace changed: report presence, debounced 250 ms (openapi reportPresence).
  let presenceTimer: ReturnType<typeof setTimeout> | undefined;
  let lastPresence = presenceKey(store.getState());
  offs.push(
    store.subscribe((state) => {
      const next = presenceKey(state);
      if (next === lastPresence) return;
      lastPresence = next;
      if (presenceTimer) clearTimeout(presenceTimer);
      presenceTimer = setTimeout(() => {
        presenceTimer = undefined;
        void reportPresence(deps);
      }, PRESENCE_DEBOUNCE_MS);
    }),
  );

  const onConnected = () => {
    store.getState().setConnection("connected");
    void reportPresence(deps);
    void qc.invalidateQueries({ queryKey: keys.index, exact: true });
    void qc.invalidateQueries({ queryKey: keys.validation, exact: true });
    void qc.invalidateQueries({ queryKey: keys.tables });
  };
  offs.push(
    realtime.onStateChange((state) => {
      store.getState().setConnection(state);
      if (state === "connected") onConnected();
    }),
  );

  void realtime
    .join("editors")
    .then(() => {
      if (realtime.state === "connected") {
        store.getState().setConnection("connected");
        void reportPresence(deps);
      }
    })
    .catch((error: unknown) => store.getState().setBanner({ kind: "realtime", text: `Live updates are unavailable: ${(error as Error).message}` }));

  return () => {
    for (const off of offs) off();
    if (indexTimer) clearTimeout(indexTimer);
    if (previewTimer) clearTimeout(previewTimer);
    if (tablesTimer) clearTimeout(tablesTimer);
    if (presenceTimer) clearTimeout(presenceTimer);
  };
}

export const PRESENCE_DEBOUNCE_MS = 250;

function presenceKey(state: { selection: readonly string[]; workspace: string }): string {
  return `${state.selection[0] ?? ""}|${state.workspace}`;
}

/** PUT /api/presence with this window's selection and workspace. */
export async function reportPresence(deps: Pick<SyncDeps, "realtime" | "store">): Promise<void> {
  const connectionId = deps.realtime.connectionId;
  if (!connectionId) return;
  const { selection, workspace } = deps.store.getState();
  try {
    await endpoints.reportPresence({ connectionId, elementId: selection[0] ?? null, workspace });
  } catch {
    /* presence is best effort */
  }
}

/**
 * In a content locale other than the default, a change's summary carries the default display name: keep the localized
 * one the index shows until the refetch in that locale answers (RT 3.8), so labels do not flicker to the default.
 */
export function keepLocalizedNames(set: RealtimeModelChanged, rows: readonly ElementSummary[] | undefined): RealtimeModelChanged {
  if (!rows || !set.changed.some((c) => c.summary)) return set;
  const byId = new Map(rows.map((r) => [r.id, r]));
  return {
    ...set,
    changed: set.changed.map((c) => {
      const row = c.summary ? byId.get(c.id) : undefined;
      return row && c.summary ? { ...c, summary: { ...c.summary, displayName: row.displayName } } : c;
    }),
  };
}
