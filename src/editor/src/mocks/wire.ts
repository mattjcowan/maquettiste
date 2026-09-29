// Wire rules the host enforces, shared by the mock handlers and the mock backend so the mock
// answers what the host answers (Maquettiste.Functions Api.cs).

/** The largest realtime payload the host publishes: `model.changed` and `validation.completed` are cut to 200 KB (Api.MaxEventBytes). */
export const MAX_EVENT_BYTES = 200 * 1024;

const ULID = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/;

/** Whether the value is an uppercase ULID the contract accepts (first character 0..7, as Api.IsUlid). */
export function isUlid(value: unknown): value is string {
  return typeof value === "string" && ULID.test(value);
}

/**
 * Reads an entity tag the way Api.TryReadTag does: `"<hash>"`, `W/"<hash>"` or the bare hash.
 * Returns null for a missing or empty header.
 */
export function readTag(value: string | null | undefined): string | null {
  let tag = (value ?? "").trim();
  if (tag.startsWith("W/")) tag = tag.slice(2);
  tag = tag.replace(/^"+|"+$/g, "");
  return tag.length > 0 ? tag : null;
}

/** The UTF-8 size of a value's JSON. */
export function jsonBytes(value: unknown): number {
  return new TextEncoder().encode(JSON.stringify(value)).length;
}

/**
 * Cuts an event of changed-then-deleted items to `max` bytes of JSON the way the host's
 * ModelChangedEvent.Create does: the largest prefix that fits, with `truncated` set when it had to cut.
 */
export function truncateChangeEvent<C, E extends { changed: C[]; deleted: string[]; truncated: boolean }>(event: E, max = MAX_EVENT_BYTES): E {
  if (jsonBytes(event) <= max) return event;
  const take = (count: number): E => ({
    ...event,
    changed: event.changed.slice(0, Math.min(count, event.changed.length)),
    deleted: event.deleted.slice(0, Math.max(0, count - event.changed.length)),
    truncated: true,
  });
  let lo = 0;
  let hi = event.changed.length + event.deleted.length;
  while (lo < hi) {
    const mid = Math.floor((lo + hi + 1) / 2);
    if (jsonBytes(take(mid)) <= max) lo = mid;
    else hi = mid - 1;
  }
  return take(lo);
}
