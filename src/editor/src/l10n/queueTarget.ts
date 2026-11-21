// A finding about a translation shard (MQ7204, MQ7205, MQ7206) names a file under `.maquettiste/model/locales/<locale>/`, not an element:
// it opens Settings, Locales, on that locale's translation queue for the shard.
import type { Diagnostic } from "@/api/types";

export interface QueueTarget {
  locale: string;
  shard: string;
}

export function shardTargetOf(d: Pick<Diagnostic, "filePath">): QueueTarget | null {
  const m = /\/locales\/([^/]+)\/[^/]+(?:\/[^/]+)*\.json$/.exec(d.filePath ?? "");
  return m && d.filePath ? { locale: m[1]!, shard: d.filePath } : null;
}

/** A request the Locales screen does not take within this long is dropped, so it never fires on a later unrelated visit. */
export const QUEUE_REQUEST_TTL_MS = 3000;

let pending: { target: QueueTarget; at: number } | null = null;
const listeners = new Set<() => void>();

/** Asks the Locales screen to open a queue: now when it shows, else when it mounts. */
export function requestQueue(target: QueueTarget): void {
  pending = { target, at: Date.now() };
  for (const l of [...listeners]) l();
}

/** The live request, or null (and cleared) when it is older than the time to live. */
function live(): QueueTarget | null {
  if (pending && Date.now() - pending.at > QUEUE_REQUEST_TTL_MS) pending = null;
  return pending?.target ?? null;
}

export const peekQueueRequest = (): QueueTarget | null => live();

/** The queue asked for, once. */
export function takeQueueRequest(): QueueTarget | null {
  const t = live();
  pending = null;
  return t;
}

export function onQueueRequest(listener: () => void): () => void {
  listeners.add(listener);
  return () => void listeners.delete(listener);
}
