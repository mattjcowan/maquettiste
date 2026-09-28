// Realtime event names and payload types, from the OpenAPI `webhooks` (PD7).
import type {
  RealtimeJobCompleted,
  RealtimeJobProgress,
  RealtimeModelChanged,
  RealtimePresenceChanged,
  RealtimeProjectChanged,
  RealtimeSiteDeployed,
  RealtimeValidationCompleted,
} from "@/api/types";

export interface RealtimeEventMap {
  "model.changed": RealtimeModelChanged;
  "validation.completed": RealtimeValidationCompleted;
  "project.changed": RealtimeProjectChanged;
  "job.progress": RealtimeJobProgress;
  "job.completed": RealtimeJobCompleted;
  "presence.changed": RealtimePresenceChanged;
  "site.deployed": RealtimeSiteDeployed;
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
