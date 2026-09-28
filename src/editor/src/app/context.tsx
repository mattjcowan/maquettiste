// The app's long-lived services, created once at startup and shared through React context.
import { createContext, useContext, type ReactNode } from "react";
import type { QueryClient } from "@tanstack/react-query";
import { createQueryClient } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import { createEditorStore, type EditorStore } from "@/state/store";
import { DraftManager } from "@/state/drafts";
import { UndoManager } from "@/state/undo";
import type { RealtimeClient } from "@/realtime/events";
import { JobTracker } from "@/realtime/jobs";

export interface AppServices {
  queryClient: QueryClient;
  store: EditorStore;
  drafts: DraftManager;
  undo: UndoManager;
  realtime: RealtimeClient;
  jobs: JobTracker;
  mock: boolean;
}

export function createServices(realtime: RealtimeClient, mock: boolean, queryClient: QueryClient = createQueryClient()): AppServices {
  const store = createEditorStore();
  const drafts = new DraftManager({ store, queryClient, saveElement: endpoints.saveElement, saveDiagram: endpoints.saveDiagram });
  const undo = new UndoManager({ store, queryClient, drafts, applyBatch: endpoints.applyBatch });
  const jobs = new JobTracker({ realtime, queryClient, store });
  return { queryClient, store, drafts, undo, realtime, jobs, mock };
}

const Context = createContext<AppServices | null>(null);

export function ServicesProvider({ services, children }: { services: AppServices; children: ReactNode }) {
  return <Context.Provider value={services}>{children}</Context.Provider>;
}

export function useServices(): AppServices {
  const services = useContext(Context);
  if (!services) throw new Error("useServices outside ServicesProvider");
  return services;
}
