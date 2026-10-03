// The search index behind the worker (explorer-redesign.md 3.1): one index over the model index rows and the table
// summaries, answering the tree filter (the matching ids of one explorer, and every explorer's match count) and quick
// open (a ranked list). Pure and synchronous: `search/worker.ts` wraps `createSearchHandler` in a Web Worker, and
// `search/client.ts` runs it in-process where there is no Worker (tests).
//
// The rows cross to the worker as one string (fields and records split by control characters): cloning one string
// is a copy, where cloning 26,000 objects walks each of them on the main thread.
import type { ElementSummary, TableSummary } from "@/api/types";
import { KIND_LABELS, placementOf } from "@/model/labels";
import { isEmptyQuery, parseQuery, termMatcher, type SearchQuery } from "./query";
import { kindTest } from "@/explorer/filter";
import { Tier, rankKey, tierFor, wordsOf, type Rankable } from "./rank";

/** Where a match lives: an explorer of the rail, or the Settings screen. */
export type SearchPlace = "domain-model" | "processes" | "reference-data" | "databases" | "diagrams" | "settings";
export const SEARCH_PLACES: readonly SearchPlace[] = ["domain-model", "processes", "reference-data", "databases", "diagrams", "settings"];

const FS = "\u001f";
const RS = "\u001e";
const LS = "\u001d";

/** The tree key of a table summary row (explorer/tree.ts builds the same). */
export const tableKeyOf = (db: string, key: string) => `${db}/t:${key}`;

// ------------------------------------------------------------------ encoding (main thread)

const clean = (s: string | null | undefined) => (s ? s.replaceAll(FS, " ").replaceAll(RS, " ").replaceAll(LS, " ") : "");

/** The index rows, as the worker reads them. */
/** The ids of a filter answer. */
export const idsOf = (joined: string): string[] => (joined ? joined.split(RS) : []);

export function encodeRows(rows: readonly ElementSummary[]): string {
  const out: string[] = new Array(rows.length);
  for (let i = 0; i < rows.length; i++) {
    const r = rows[i];
    out[i] = [
      r.id,
      r.kind,
      clean(r.name),
      clean(r.displayName),
      r.package ?? "",
      r.tags.map(clean).join(LS),
      r.stereotypes.map(clean).join(LS),
      r.category ?? "",
    ].join(FS);
  }
  return out.join(RS);
}

/** One database's table summaries, as the worker reads them. */
export function encodeTables(db: string, tables: readonly TableSummary[]): string {
  const out: string[] = new Array(tables.length);
  for (let i = 0; i < tables.length; i++) {
    const t = tables[i] as TableSummary & { enumId?: string | null };
    out[i] = [tableKeyOf(db, t.key), clean(t.name), clean(t.schema), t.entityId ?? t.relationId ?? t.enumId ?? ""].join(FS);
  }
  return out.join(RS);
}

// ------------------------------------------------------------------ protocol

/** The filter bar's chips (explorer/filter.ts); the ids of elements with errors and of a diagram's members travel with them. */
export interface FilterExtra {
  tags: readonly string[];
  categories: readonly string[];
  stereotypes: readonly string[];
  /** Kind ids, any of. */
  kinds?: readonly string[];
  /** A domain id: the domain and its sub-domains. */
  domain?: string | null;
  /** Has errors: the elements that have them. */
  errorIds?: readonly string[];
  /** On this diagram: its members. */
  members?: readonly string[];
  /** Domain only, or bound to a database: the entities kept. */
  storageIds?: readonly string[];
}

/** Whether the chips narrow at all (an unset list or flag does not). */
export function extraNarrows(extra: FilterExtra | undefined): boolean {
  return (
    !!extra &&
    (extra.tags.length > 0 ||
      extra.categories.length > 0 ||
      extra.stereotypes.length > 0 ||
      !!extra.kinds?.length ||
      !!extra.domain ||
      !!extra.errorIds ||
      !!extra.members ||
      !!extra.storageIds)
  );
}

export interface Category {
  id: string;
  name: string;
  parent: string | null;
}

export interface RankContext {
  /** The domain of the current selection. */
  domain: string | null;
  /** Recently opened element ids, most recent first. */
  recent: readonly string[];
}

export interface SearchHit {
  /** The element id, or for a table summary its tree key. */
  id: string;
  kind: string;
  name: string;
  /** The display name, when it differs from the name. */
  display?: string;
  /** Where it lives: the domain path, or "database · schema" for a table. */
  path: string;
  place: SearchPlace;
  /** For a table summary: its database. */
  db?: string;
  /** The ranking tier (search/rank.ts), so the palette can merge its own items in. */
  tier: number;
}

export type ToWorker =
  /** The rows encoded by `encodeRows`, or the index's JSON text (an `ElementSummary[]`) to parse and encode here. */
  | { type: "rows"; version: number; data: string; json?: undefined; bytes?: undefined }
  | { type: "rows"; version: number; json: string; data?: undefined; bytes?: undefined; first?: boolean; more?: boolean }
  /** The index body's UTF-8 bytes, transferred: decoded and parsed here. */
  | { type: "rows"; version: number; bytes: ArrayBuffer; data?: undefined; json?: undefined }
  /** An index patch (api/indexPatch.ts): encoded upserts, removed ids, and the rows that took a new position. */
  | { type: "update"; data: string; deleted: readonly string[]; moves: readonly (readonly [string, string | null])[] }
  | { type: "tables"; db: string; data: string | null }
  | { type: "categories"; data: readonly Category[] }
  | { type: "filter"; seq: number; text: string; extra?: FilterExtra; place: SearchPlace }
  | { type: "rank"; seq: number; text: string; limit: number; context?: RankContext };

export interface FilterAnswer {
  type: "filter";
  seq: number;
  /**
   * The matching ids in the asked place (element ids, and tree keys for table summaries), joined by a record
   * separator: one string crosses from the worker as a copy, an array of 20,000 strings one by one.
   */
  ids: string;
  /** Matches per place, every place. */
  counts: Record<SearchPlace, number>;
  /** Some database's table summaries have not arrived: table names are still loading. */
  tablesLoading: boolean;
  /** The worker's own time. */
  ms: number;
}

export interface RankAnswer {
  type: "rank";
  seq: number;
  hits: SearchHit[];
  total: number;
  /** The worker's own time. */
  ms: number;
}

export type FromWorker = { type: "ready"; version: number; count: number; ms: number } | FilterAnswer | RankAnswer;

// ------------------------------------------------------------------ the index

interface Doc {
  id: string;
  kind: string;
  name: string;
  lname: string;
  display: string;
  ldisplay: string;
  pkg: string;
  tags: string[];
  st: string[];
  cat: string;
  place: SearchPlace;
  db?: string;
  schema?: string;
  owner?: string;
  /** The name's words for word-start matching (search/rank.ts), computed on first need. */
  words?: string[];
  dwords?: string[];
}

/** The place (explorer or Settings) that lists a kind. */
export const placeOf = (kind: string): SearchPlace => {
  const p = placementOf(kind);
  return p === "databases" || p === "diagrams" || p === "reference-data" || p === "settings" || p === "processes" ? p : "domain-model";
};

/** "Value objects", "value-object", "valueobject" → "valueobject"; a trailing plural s is dropped. */
const kindWord = (s: string) =>
  s
    .toLowerCase()
    .replace(/[^a-z]/g, "")
    .replace(/(ies)$/, "y")
    .replace(/s$/, "");

const zeroCounts = (): Record<SearchPlace, number> => ({ "domain-model": 0, processes: 0, "reference-data": 0, databases: 0, diagrams: 0, settings: 0 });

/** One encoded index row (`encodeRows`) as a document. */
function rowDoc(rec: string): Doc {
  const f = rec.split(FS);
  const kind = f[1];
  return {
    id: f[0],
    kind,
    name: f[2],
    lname: f[2].toLowerCase(),
    display: f[3],
    ldisplay: f[3].toLowerCase(),
    pkg: f[4],
    tags: f[5] ? f[5].toLowerCase().split(LS) : [],
    st: f[6] ? f[6].toLowerCase().split(LS) : [],
    cat: f[7],
    place: placeOf(kind),
  };
}

export class SearchIndex {
  version = -1;
  private rows: Doc[] = [];
  private byId = new Map<string, Doc>();
  private tables = new Map<string, Doc[] | null>();
  private databases: string[] = [];
  private categories: Category[] = [];

  get size() {
    let n = this.rows.length;
    for (const t of this.tables.values()) n += t?.length ?? 0;
    return n;
  }

  loadRows(version: number, data: string) {
    this.version = version;
    const rows: Doc[] = [];
    const byId = new Map<string, Doc>();
    const databases: string[] = [];
    if (data)
      for (const rec of data.split(RS)) {
        const doc = rowDoc(rec);
        rows.push(doc);
        byId.set(doc.id, doc);
        if (doc.kind === "database") databases.push(doc.id);
      }
    // Word-start matching needs each name's words: split once here, off the keystroke path.
    for (const d of rows) d.words = wordsOf(d.name);
    this.rows = rows;
    this.byId = byId;
    this.databases = databases;
  }

  /**
   * Applies an index patch (explorer-redesign.md 4.4): changed rows are replaced where they stand, removed rows
   * dropped, and rows with a new position (added, or a new path) inserted before their follower, last first, so the
   * rows end in the index's order, as `loadRows` of the patched index would leave them.
   */
  updateRows(data: string, deleted: readonly string[], moves: readonly (readonly [string, string | null])[]) {
    const incoming = new Map<string, Doc>();
    if (data)
      for (const rec of data.split(RS)) {
        const doc = rowDoc(rec);
        doc.words = wordsOf(doc.name);
        incoming.set(doc.id, doc);
      }
    const gone = new Set(deleted);
    for (const [id] of moves) gone.add(id);
    const rows: Doc[] = [];
    for (const d of this.rows) if (!gone.has(d.id)) rows.push(incoming.get(d.id) ?? d);
    for (const [id, before] of moves) {
      const doc = incoming.get(id);
      if (!doc) continue;
      const at = before === null ? -1 : rows.findIndex((d) => d.id === before);
      if (at < 0) rows.push(doc);
      else rows.splice(at, 0, doc);
    }
    for (const id of deleted) this.byId.delete(id);
    for (const [id, doc] of incoming) this.byId.set(id, doc);
    this.rows = rows;
    if ([...incoming.values()].some((d) => d.kind === "database") || deleted.length)
      this.databases = rows.filter((d) => d.kind === "database").map((d) => d.id);
  }

  loadTables(db: string, data: string | null) {
    if (data === null) {
      this.tables.set(db, null);
      return;
    }
    const docs: Doc[] = [];
    if (data)
      for (const rec of data.split(RS)) {
        const f = rec.split(FS);
        docs.push({
          id: f[0],
          kind: "table",
          name: f[1],
          lname: f[1].toLowerCase(),
          display: "",
          ldisplay: "",
          pkg: "",
          tags: [],
          st: [],
          cat: "",
          place: "databases",
          db,
          schema: f[2],
          owner: f[3] || undefined,
        });
      }
    for (const d of docs) d.words = wordsOf(d.name);
    this.tables.set(db, docs);
  }

  loadCategories(categories: readonly Category[]) {
    this.categories = [...categories];
  }

  get tablesLoading(): boolean {
    return this.databases.some((db) => !this.tables.get(db));
  }

  /** Every document: the index rows, then each database's tables. */
  private *docs(): Iterable<Doc> {
    yield* this.rows;
    for (const db of this.databases) {
      const list = this.tables.get(db);
      if (list) yield* list;
    }
  }

  /** The predicate of a query's qualifiers and the filter menu's picks; undefined when neither narrows. */
  private narrowing(query: SearchQuery, extra?: FilterExtra): ((d: Doc) => boolean) | undefined {
    const q = query.q;
    const tags = [...q.tag, ...(extra?.tags ?? []).map((t) => t.toLowerCase())];
    const sts = [...q.st, ...(extra?.stereotypes ?? []).map((t) => t.toLowerCase())];
    const kinds = q.kind.map(kindWord);
    const cats = this.categoryScope([...q.cat], extra?.categories ?? []);
    const scope = q.in.length ? this.domainScope(q.in) : undefined;
    const chipKinds = kindTest(extra?.kinds ?? []);
    const chipDomain = extra?.domain ? this.domainScope([extra.domain.toLowerCase()]) : undefined;
    const errors = extra?.errorIds ? new Set(extra.errorIds) : undefined;
    const members = extra?.members ? new Set(extra.members) : undefined;
    const storage = extra?.storageIds ? new Set(extra.storageIds) : undefined;
    if (!kinds.length && !tags.length && !sts.length && !cats && !scope && !chipKinds && !chipDomain && !errors && !members && !storage) return undefined;
    const kindOk = (kind: string) => kinds.some((k) => k === kindWord(kind) || k === kindWord(KIND_LABELS[kind as keyof typeof KIND_LABELS] ?? ""));
    return (d) => {
      if (kinds.length && !kindOk(d.kind)) return false;
      if (chipKinds && !chipKinds(d.kind)) return false;
      if (chipDomain && !chipDomain(d)) return false;
      if (errors && !errors.has(d.id)) return false;
      if (members && !members.has(d.id)) return false;
      if (storage && !storage.has(d.id)) return false;
      if (tags.length && !tags.some((t) => d.tags.includes(t))) return false;
      if (sts.length && !sts.some((t) => d.st.includes(t))) return false;
      if (cats && !(d.cat && cats.has(d.cat.toLowerCase()))) return false;
      if (scope && !scope(d)) return false;
      return true;
    };
  }

  /** The categories in scope (lower-cased ids and names), with their descendants; undefined when none is asked. */
  private categoryScope(names: string[], ids: readonly string[]): Set<string> | undefined {
    if (!names.length && !ids.length) return undefined;
    const wanted = new Set([...names, ...ids.map((i) => i.toLowerCase())]);
    const out = new Set(wanted);
    if (!this.categories.length) return out;
    const byId = new Map(this.categories.map((c) => [c.id, c]));
    for (const c of this.categories) {
      for (let p: Category | undefined = c, guard = 0; p && guard < 64; p = p.parent ? byId.get(p.parent) : undefined, guard++) {
        if (wanted.has(p.id.toLowerCase()) || wanted.has(p.name.toLowerCase())) {
          out.add(c.id.toLowerCase());
          out.add(c.name.toLowerCase());
          break;
        }
      }
    }
    return out;
  }

  /** `in:` a domain (by name or id) and its sub-domains; a table is in the domain of the element that owns it. */
  private domainScope(values: string[]): (d: Doc) => boolean {
    const memo = new Map<string, boolean>();
    const inScope = (pkg: string): boolean => {
      if (!pkg) return false;
      const known = memo.get(pkg);
      if (known !== undefined) return known;
      memo.set(pkg, false);
      const p = this.byId.get(pkg);
      const hit = !!p && (values.includes(p.lname) || values.includes(p.id.toLowerCase()) || inScope(p.pkg));
      memo.set(pkg, hit);
      return hit;
    };
    return (d) => {
      if (d.kind === "package") return inScope(d.id);
      if (d.owner) return inScope(this.byId.get(d.owner)?.pkg ?? "");
      return inScope(d.pkg);
    };
  }

  filter(text: string, place: SearchPlace, extra?: FilterExtra): Omit<FilterAnswer, "type" | "seq" | "ms"> {
    const query = parseQuery(text);
    const counts = zeroCounts();
    const ids: string[] = [];
    const empty = isEmptyQuery(query) && !extraNarrows(extra);
    if (!empty) {
      const match = termMatcher(query);
      const narrow = this.narrowing(query, extra);
      for (const d of this.docs()) {
        if (match && !match(d.lname) && !(d.ldisplay && match(d.ldisplay))) continue;
        if (narrow && !narrow(d)) continue;
        counts[d.place]++;
        if (d.place === place) ids.push(d.id);
      }
    }
    return { ids: ids.join(RS), counts, tablesLoading: this.tablesLoading };
  }

  rank(text: string, limit: number, context?: RankContext): Omit<RankAnswer, "type" | "seq" | "ms"> {
    const query = parseQuery(text);
    if (isEmptyQuery(query)) return { hits: [], total: 0 };
    const match = query.explicit ? termMatcher(query) : undefined;
    const narrow = this.narrowing(query, undefined);
    const term = query.term;
    const recent = new Map((context?.recent ?? []).map((id, i) => [id, i]));
    const near = this.nearness(context?.domain ?? null);
    const tierOfDoc = tierFor(term);
    type Found = Rankable & { doc: Doc; key: number };
    let found: Found[] = [];
    for (const d of this.docs()) {
      let tier: Tier;
      if (!term) tier = Tier.Exact;
      else if (match) tier = match(d.lname) || (!!d.ldisplay && match(d.ldisplay)) ? Tier.Exact : Tier.None;
      else {
        tier = tierOfDoc(d.lname, () => (d.words ??= wordsOf(d.name)));
        if (tier > Tier.Exact && d.display) {
          const t = tierOfDoc(d.ldisplay, () => (d.dwords ??= wordsOf(d.display)));
          if (t < tier) tier = t;
        }
      }
      if (tier === Tier.None) continue;
      if (narrow && !narrow(d)) continue;
      found.push({ doc: d, tier, kind: d.kind, name: d.name, near: 2, recent: recent.get(d.id) ?? -1, key: 0 });
    }
    const total = found.length;
    for (const f of found) {
      if (near) f.near = near(f.doc);
      f.key = rankKey(f);
    }
    // Only the first `limit` are sorted in full: the rest are cut at the limit-th key first.
    if (found.length > limit * 2) {
      const keys = Float64Array.from(found, (f) => f.key).sort();
      const cut = keys[limit - 1];
      found = found.filter((f) => f.key <= cut);
    }
    found.sort((a, b) => a.key - b.key || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
    const hits = found.slice(0, limit).map(({ doc, tier }) => this.hitOf(doc, tier));
    return { hits, total };
  }

  /** 0 in the selection's domain, 1 in an enclosing or enclosed domain, 2 elsewhere. */
  private nearness(domain: string | null): ((d: Doc) => number) | undefined {
    if (!domain) return undefined;
    const up = new Set<string>();
    for (let p = this.byId.get(domain), guard = 0; p && guard < 64; p = this.byId.get(p.pkg), guard++) up.add(p.id);
    return (d) => {
      const pkg = d.owner ? (this.byId.get(d.owner)?.pkg ?? "") : d.kind === "package" ? d.id : d.pkg;
      if (!pkg) return 2;
      if (pkg === domain) return 0;
      if (up.has(pkg)) return 1;
      for (let p = this.byId.get(pkg), guard = 0; p && guard < 64; p = this.byId.get(p.pkg), guard++) if (p.id === domain) return 1;
      return 2;
    };
  }

  private hitOf(d: Doc, tier: number): SearchHit {
    const hit: SearchHit = { id: d.id, kind: d.kind, name: d.name, path: "", place: d.place, tier };
    if (d.display && d.display !== d.name) hit.display = d.display;
    if (d.db) {
      hit.db = d.db;
      hit.path = [this.byId.get(d.db)?.name ?? "", d.schema].filter(Boolean).join(" · ");
    } else {
      const names: string[] = [];
      for (let p = this.byId.get(d.pkg), guard = 0; p && guard < 64; p = this.byId.get(p.pkg), guard++) names.unshift(p.name);
      hit.path = names.join(" / ");
    }
    return hit;
  }
}

/** The worker's message handler: one index, one answer per question. */
export function createSearchHandler(index = new SearchIndex()): (msg: ToWorker) => FromWorker | undefined {
  // The index text arrives in slices (`more` on all but the last; `first` starts a new text).
  let parts: string[] = [];
  return (msg) => {
    switch (msg.type) {
      case "rows": {
        if (msg.bytes !== undefined) {
          const start = performance.now();
          const rows = JSON.parse(new TextDecoder().decode(msg.bytes)) as ElementSummary[];
          index.loadRows(msg.version, encodeRows(rows));
          return { type: "ready", version: msg.version, count: index.size, ms: performance.now() - start };
        }
        if (msg.json !== undefined) {
          if (msg.first) parts = [];
          parts.push(msg.json);
          if (msg.more) return undefined;
          msg = { type: "rows", version: msg.version, json: parts.join("") };
          parts = [];
        }
        const start = performance.now();
        index.loadRows(msg.version, msg.data ?? encodeRows(JSON.parse(msg.json) as ElementSummary[]));
        return { type: "ready", version: msg.version, count: index.size, ms: performance.now() - start };
      }
      case "update":
        index.updateRows(msg.data, msg.deleted, msg.moves);
        return undefined;
      case "tables":
        index.loadTables(msg.db, msg.data);
        return undefined;
      case "categories":
        index.loadCategories(msg.data);
        return undefined;
      case "filter": {
        const start = performance.now();
        const answer = index.filter(msg.text, msg.place, msg.extra);
        return { type: "filter", seq: msg.seq, ...answer, ms: performance.now() - start };
      }
      case "rank": {
        const start = performance.now();
        const answer = index.rank(msg.text, msg.limit, msg.context);
        return { type: "rank", seq: msg.seq, ...answer, ms: performance.now() - start };
      }
    }
  };
}
