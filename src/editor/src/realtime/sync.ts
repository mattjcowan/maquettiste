// Realtime → TanStack Query cache (phase2-design.md 4.3 and 4.5).
import type { QueryClient } from "@tanstack/react-query";
import type { ElementSummary } from "@/api/types";
import { keys, removeIndexRows } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { EditorStore } from "@/state/store";
import type { DraftManager } from "@/state/drafts";
import type { RealtimeClient } from "./events";
import type { JobTracker } from "./jobs";

export interface SyncDeps {
  realtime: RealtimeClient;
  queryClient: QueryClient;
  store: EditorStore;
  drafts: DraftManager;
  jobs: JobTracker;
  reload?: () => void;
  /** Debounces (ms): index refetch 250, preview refetch 400. */
  delays?: { index: number; preview: number };
}

export function connectRealtime(deps: SyncDeps): () => void {
  const { realtime, queryClient: qc, store, drafts, jobs } = deps;
  const delays = deps.delays ?? { index: 250, preview: 400 };
  const reload = deps.reload ?? (() => window.location.reload());
  let indexTimer: ReturnType<typeof setTimeout> | null = null;
  let previewTimer: ReturnType<typeof setTimeout> | null = null;

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

  offs.push(
    realtime.on("model.changed", async (set) => {
      removeIndexRows(qc, set.deleted);
      if (set.truncated) refetchIndexSoon();
      for (const change of set.changed) {
        // An event for an id with a save in flight is compared after that save answers.
        if (drafts.isInFlight(change.id)) await drafts.whenSettled(change.id);
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
      void qc.invalidateQueries({ queryKey: keys.project });
      void qc.invalidateQueries({ queryKey: keys.settings });
      refreshResolved();
    }),
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
