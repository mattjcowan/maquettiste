// Realtime event names and payload types, from the OpenAPI `webhooks` (PD7).
import type {
  ChangeSet,
  ElementSummary,
  RealtimeJobCompleted,
  RealtimeJobProgress,
  RealtimeModelChanged,
  RealtimePresenceChanged,
  RealtimeProjectChanged,
  RealtimeSiteDeployed,
  RealtimeValidationCompleted,
} from "@/api/types";
import type { components } from "@/api/schema";

export interface RealtimeEventMap {
  "model.changed": RealtimeModelChanged;
  "validation.completed": RealtimeValidationCompleted;
  "project.changed": RealtimeProjectChanged;
  "job.progress": RealtimeJobProgress;
  "job.completed": RealtimeJobCompleted;
  "presence.changed": RealtimePresenceChanged;
  "site.deployed": RealtimeSiteDeployed;
  "templates.changed": components["schemas"]["RealtimeTemplatesChanged"];
  "packs.changed": components["schemas"]["RealtimePacksChanged"];
}

export type RealtimeEventName = keyof RealtimeEventMap;
export const REALTIME_EVENTS: RealtimeEventName[] = [
  "model.changed",
  "validation.completed",
  "project.changed",
  "job.progress",
  "job.completed",
  "presence.changed",
  "site.deployed",
  "templates.changed",
  "packs.changed",
];

export type RealtimeState = "disconnected" | "connecting" | "connected" | "reconnecting";
export interface EventDetails {
  event: string;
  group: string | null;
}
export type Handler<E extends RealtimeEventName> = (payload: RealtimeEventMap[E], details: EventDetails) => void;

/** What the SPA needs from site.realtime (phase2-design.md 4.5). */
export interface RealtimeClient {
  on<E extends RealtimeEventName>(event: E, handler: Handler<E>): () => void;
  off<E extends RealtimeEventName>(event: E, handler?: Handler<E>): void;
  join(group: string): Promise<void>;
  leave(group: string): Promise<void>;
  connect(): Promise<void>;
  readonly state: RealtimeState;
  readonly connectionId: string | null;
  onStateChange(handler: (state: RealtimeState, previous: RealtimeState) => void): () => void;
}

/** What a model.changed event does to the index: rows to upsert and ids to remove (E5d). */
export interface IndexChanges {
  upserts: ElementSummary[];
  deleted: string[];
}

/**
 * The index changes a model.changed event carries, from each change's summary (built server-side,
 * E5d) and the deleted ids: add, change, rename and move are all an upsert of the row as it is now.
 * Null when the event cannot be applied in place: a truncated event (it lists only a prefix of the
 * changes) or a change without a summary (an older server); the caller then refetches the index
 * with its ETag (explorer-redesign.md 4.4).
 */
export function indexChangesOf(set: ChangeSet): IndexChanges | null {
  if (set.truncated) return null;
  const upserts: ElementSummary[] = [];
  for (const c of set.changed) {
    if (!c.summary) return null;
    upserts.push(c.summary);
  }
  return { upserts, deleted: set.deleted };
}
