// The snapshot the editor reads "as of" (docs/engineering/snapshots.md section 4 and 10): null for the working model. One
// place decides it for the whole page, so no read can answer for the working model while the editor shows a snapshot:
// - the HTTP client adds `?snapshot=<id>` to the reads the API serves for a snapshot (AS_OF_READS, the sign-in gate's
//   list), lets the scope-neutral requests through unchanged (the session, the project, presence, the snapshots
//   themselves), and refuses every other request before it leaves the page: a write with `snapshot-read-only`, a read
//   with `snapshot-unsupported`, the codes the gate would answer;
// - the query client hashes every model query key with the scope (`scopedKeyHash`), so a snapshot's documents and the
//   working model's never share a cache entry, and `setQueryData`/`getQueryData` reach the current scope's only;
// - the URL carries it (`?snapshot=<id>`), so a reload or a shared link opens the same snapshot.
import { hashKey, type QueryKey } from "@tanstack/react-query";

export const SNAPSHOT_PARAM = "snapshot";

/** What an edit attempted while a snapshot is shown says (nothing is changed or saved). */
export const SNAPSHOT_READ_ONLY = "This snapshot is read-only. Go back to the working model to edit, or restore the snapshot.";
const ID = /^[a-z0-9]+(-[a-z0-9]+)*$/;

/** A snapshot id the API accepts (`<slug>-<yyyymmdd-hhmmss>`), else null. */
export function snapshotIdOf(value: string | null | undefined): string | null {
  return value && value.length <= 120 && ID.test(value) ? value : null;
}

/** The snapshot a location's query string names. */
export function snapshotFromSearch(search: string): string | null {
  return snapshotIdOf(new URLSearchParams(search).get(SNAPSHOT_PARAM));
}

/** A query string with the snapshot set (or removed, for null), every other parameter kept. */
export function withSnapshotParam(search: string, id: string | null): string {
  const params = new URLSearchParams(search);
  if (id) params.set(SNAPSHOT_PARAM, id);
  else params.delete(SNAPSHOT_PARAM);
  const text = params.toString();
  return text ? `?${text}` : "";
}

let scope: string | null = typeof window === "undefined" ? null : snapshotFromSearch(window.location.search);
const listeners = new Set<() => void>();

/** The snapshot every model read is answered from, or null for the working model. */
export function snapshotScope(): string | null {
  return scope;
}

/** Switches the scope (the caller keeps the URL in step); listeners re-render the shell. */
export function setSnapshotScope(id: string | null): void {
  const next = snapshotIdOf(id);
  if (next === scope) return;
  scope = next;
  for (const listener of [...listeners]) listener();
}

export function subscribeSnapshotScope(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

// ---------------------------------------------------------------- requests

type Route = readonly [method: string, segments: readonly string[]];

/** The reads the API answers "as of" a snapshot (SnapshotEndpoints.AsOfReads); `*` is one path segment. */
export const AS_OF_READS: readonly Route[] = [
  ["GET", ["api", "model", "index"]],
  ["GET", ["api", "model", "elements"]],
  ["POST", ["api", "model", "elements", "read"]],
  ["GET", ["api", "model", "elements", "*"]],
  ["GET", ["api", "model", "kinds"]],
  ["GET", ["api", "model", "resolved"]],
  ["GET", ["api", "model", "references", "*"]],
  ["POST", ["api", "validate"]],
  ["GET", ["api", "diagrams", "*"]],
  ["GET", ["api", "databases", "*", "view"]],
  ["GET", ["api", "databases", "*", "tables"]],
  ["GET", ["api", "databases", "*", "tables", "*"]],
  ["GET", ["api", "project", "settings"]],
  ["POST", ["api", "templates", "preview"]],
];

/**
 * Requests that do not depend on which model is shown and go out unchanged: who is signed in, the project's name and icon,
 * presence, the rule catalog, the job list, the packs (a snapshot's template preview renders with the working model's
 * packs), the assistant's status, and the snapshot operations themselves (compare and restore work from a snapshot).
 */
const NEUTRAL: readonly Route[] = [
  ["*", ["api", "session"]],
  ["GET", ["api", "health"]],
  ["GET", ["api", "project"]],
  ["GET", ["api", "project", "branding", "icon"]],
  ["PUT", ["api", "presence"]],
  ["GET", ["api", "validation", "rules"]],
  ["GET", ["api", "jobs"]],
  ["GET", ["api", "jobs", "*"]],
  ["GET", ["api", "packs"]],
  ["GET", ["api", "packs", "*"]],
  ["GET", ["api", "assist", "status"]],
];

const matches = (routes: readonly Route[], method: string, segments: readonly string[]) =>
  routes.some(([m, route]) => (m === "*" || m === method) && route.length === segments.length && route.every((s, i) => s === "*" || s === segments[i]));

export type RequestScope = "as-of" | "neutral" | "unsupported" | "read-only";

/** What a request is while a snapshot is shown: answered from it, unchanged, or refused (and with which code). */
export function classifyRequest(method: string, pathname: string): RequestScope {
  const segments = pathname.split("/").filter(Boolean);
  const verb = method.toUpperCase();
  if (segments[0] !== "api") return "neutral";
  if (segments[1] === "snapshots") return "neutral";
  if (matches(AS_OF_READS, verb, segments)) return "as-of";
  if (matches(NEUTRAL, verb, segments)) return "neutral";
  return verb === "GET" || verb === "HEAD" ? "unsupported" : "read-only";
}

/** The URL a request goes to in a scope: `?snapshot=<id>` added to an as-of read; null when the request is refused. */
export function scopedUrl(method: string, url: string, id: string | null): { url: string; refused: RequestScope | null } {
  if (!id) return { url, refused: null };
  const parsed = new URL(url, "http://x");
  const kind = classifyRequest(method, parsed.pathname);
  if (kind === "neutral") return { url, refused: null };
  if (kind !== "as-of") return { url, refused: kind };
  parsed.searchParams.set(SNAPSHOT_PARAM, id);
  const absolute = /^[a-z][a-z0-9+.-]*:/i.test(url);
  return { url: absolute ? parsed.toString() : `${parsed.pathname}${parsed.search}${parsed.hash}`, refused: null };
}

// ---------------------------------------------------------------- query keys

/** Query keys whose data is the same for every scope (they are never hashed with a snapshot). */
const NEUTRAL_KEYS = new Set(["session", "project", "snapshots", "validationRules", "jobs", "job", "packs"]);

/** The scope a query key belongs to, as part of its hash: `[{snapshot: id}, ...key]` in a snapshot, the key alone otherwise. */
export function scopedKeyHash(key: QueryKey, id: string | null = scope): string {
  if (!id || NEUTRAL_KEYS.has(String(key[0]))) return hashKey(key);
  return hashKey([{ [SNAPSHOT_PARAM]: id }, ...key]);
}

/** The snapshot a cached query's hash belongs to (null: the working model or a neutral key). */
export function scopeOfHash(queryHash: string): string | null {
  const match = /^\[\{"snapshot":"([a-z0-9-]+)"\}/.exec(queryHash);
  return match ? match[1] : null;
}
