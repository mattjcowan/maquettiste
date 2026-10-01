// The bulk reads of the mock backend, built like the engine's ModelPages (src/Maquettiste.Engine/Editor/ModelPages.cs):
// the index filters, pages ordered by (kind, name, id) ordinal with an opaque cursor that encodes the last
// position, documents trimmed to fields, and the kind counts. GET /api/model/elements, /api/model/kinds and
// the paged /api/model/index answer from them.
import type { ElementSummary, KindCount, ModelKindsResult } from "@/api/types";

export const DEFAULT_LIMIT = 100;
export const MAX_LIMIT = 1000;

export interface Filter {
  kind?: string | null;
  package?: string | null;
  tag?: string | null;
  category?: string | null;
  stereotype?: string | null;
  query?: string | null;
}

/** The filters of a query string. */
export function filterOf(q: URLSearchParams): Filter {
  return {
    kind: q.get("kind"),
    package: q.get("package"),
    tag: q.get("tag"),
    category: q.get("category"),
    stereotype: q.get("stereotype"),
    query: q.get("query"),
  };
}

export function isEmptyFilter(f: Filter): boolean {
  return !f.kind && !f.package && !f.tag && !f.category && !f.stereotype && !f.query;
}

/** The rows that match every filter that is set (AND), as ModelPages.Filter. */
export function filterRows(rows: ElementSummary[], f: Filter): ElementSummary[] {
  let packages: Set<string> | null = null;
  if (f.package) {
    const wanted = f.package;
    packages = new Set([wanted]);
    for (const row of rows) if (row.kind === "package" && row.name.toLowerCase() === wanted.toLowerCase()) packages.add(row.id);
  }
  return rows.filter(
    (r) =>
      (!f.kind || r.kind === f.kind) &&
      (!packages || (r.package !== null && packages.has(r.package))) &&
      (!f.tag || r.tags.includes(f.tag)) &&
      (!f.category || r.category === f.category) &&
      (!f.stereotype || r.stereotypes.includes(f.stereotype)) &&
      (!f.query || r.name.toLowerCase().includes(f.query.toLowerCase())),
  );
}

/** A page size: absent is 100; otherwise a whole number from 1 to 1000, or an error message. */
export function parseLimit(text: string | null): number | string {
  if (text === null || text === "") return DEFAULT_LIMIT;
  const value = /^\d+$/.test(text) ? Number(text) : NaN;
  return value >= 1 && value <= MAX_LIMIT ? value : `limit must be a whole number from 1 to ${MAX_LIMIT}, not '${text}'.`;
}

type Key = [kind: string, name: string, id: string];

const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
const compare = (a: Key, b: Key) => ordinal(a[0], b[0]) || ordinal(a[1], b[1]) || ordinal(a[2], b[2]);

function base64Url(bytes: Uint8Array): string {
  let text = "";
  for (const b of bytes) text += String.fromCharCode(b);
  return btoa(text).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function writeCursor(key: Key): string {
  return base64Url(new TextEncoder().encode(["p1", ...key].join("\0")));
}

/** The position a cursor encodes, or null when it is not one a paged read returned. */
export function readCursor(cursor: string): Key | null {
  try {
    const padded = cursor.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((cursor.length + 3) % 4);
    const bytes = Uint8Array.from(atob(padded), (c) => c.charCodeAt(0));
    const parts = new TextDecoder("utf-8", { fatal: true }).decode(bytes).split("\0");
    return parts.length === 4 && parts[0] === "p1" ? [parts[1], parts[2], parts[3]] : null;
  } catch {
    return null;
  }
}

export const BAD_CURSOR = "cursor is not a cursor a paged read returned; pass next from the previous page as it is.";

/** One page by (kind, name, id) after the cursor's position, as ModelPages.Page; null for a bad cursor. */
export function page<T>(rows: T[], key: (row: T) => Key, cursor: string | null, limit: number): { items: T[]; next: string | null } | null {
  const after = cursor ? readCursor(cursor) : null;
  if (cursor && !after) return null;
  const ordered = rows.map((row) => ({ row, key: key(row) })).sort((a, b) => compare(a.key, b.key));
  const rest = after ? ordered.filter((r) => compare(r.key, after) > 0) : ordered;
  if (rest.length <= limit) return { items: rest.map((r) => r.row), next: null };
  return { items: rest.slice(0, limit).map((r) => r.row), next: writeCursor(rest[limit - 1].key) };
}

/** A document with only the given top-level members (id and kind always kept). */
export function trim(json: Record<string, unknown>, fields: string[] | null): Record<string, unknown> {
  if (!fields) return json;
  const keep = new Set([...fields, "id", "kind"]);
  return Object.fromEntries(Object.entries(json).filter(([k]) => keep.has(k)));
}

function counts(rows: ElementSummary[]): KindCount[] {
  const map = new Map<string, number>();
  for (const row of rows) map.set(row.kind, (map.get(row.kind) ?? 0) + 1);
  return [...map.entries()].sort((a, b) => ordinal(a[0], b[0])).map(([kind, count]) => ({ kind: kind as KindCount["kind"], count }));
}

/** The kinds present with their counts, per package when asked, as ModelPages.Kinds. */
export function kinds(rows: ElementSummary[], byPackage: boolean): ModelKindsResult {
  if (!byPackage) return { total: rows.length, kinds: counts(rows), packages: null };
  const names = new Map(rows.filter((r) => r.kind === "package").map((r) => [r.id, r.name]));
  const groups = new Map<string, ElementSummary[]>();
  for (const row of rows) groups.set(row.package ?? "", [...(groups.get(row.package ?? "") ?? []), row]);
  const packages = [...groups.entries()]
    .map(([id, members]) => ({ package: id || null, name: id ? (names.get(id) ?? id) : null, count: members.length, kinds: counts(members) }))
    .sort((a, b) => (a.package === null ? -1 : b.package === null ? 1 : ordinal(a.name ?? "", b.name ?? "") || ordinal(a.package, b.package)));
  return { total: rows.length, kinds: counts(rows), packages };
}
