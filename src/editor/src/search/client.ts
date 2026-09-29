// The main thread's side of the search worker (explorer-redesign.md 3.1). One client per page: the explorers feed it
// the index rows and the table summaries as they arrive (each fed once per object), and the tree filter, quick open
// and the palette ask it questions. Answers come back in the order asked (one worker), so a sequence number pairs
// them. Where there is no Worker (unit tests), the same handler runs in-process, answering on a later task.
import { useEffect, useLayoutEffect, useSyncExternalStore } from "react";
import type { ElementSummary, TableSummary } from "@/api/types";
import { perfStart } from "@/lib/perf";
import { patchesBetween } from "@/api/indexPatch";
import {
  createSearchHandler,
  encodeRows,
  encodeTables,
  idsOf,
  type Category,
  type FilterAnswer,
  type FilterExtra,
  type FromWorker,
  type RankAnswer,
  type RankContext,
  type SearchPlace,
  type ToWorker,
} from "./engine";

type DistributiveOmit<T, K extends PropertyKey> = T extends unknown ? Omit<T, K> : never;
type Pending = { resolve(answer: FromWorker): void };

export class SearchClient {
  private readonly send: (msg: ToWorker) => void;
  private seq = 0;
  private readonly pending = new Map<number, Pending>();
  private rows: readonly ElementSummary[] | null = null;
  private readonly tables = new Map<string, readonly TableSummary[] | null>();
  private categories: readonly Category[] | null = null;
  private version = 0;
  /** Index patches sent as update messages (the scale project waits on it). */
  private updates = 0;
  private updatedAt = 0;
  private readonly listeners = new Set<() => void>();
  private readyEnd: ((detail?: Record<string, unknown>) => unknown) | null = null;
  /** Whether the worker has taken the current rows. */
  ready = false;

  constructor(worker?: Worker | null) {
    if (worker) {
      worker.onmessage = (e: MessageEvent<FromWorker>) => this.receive(e.data);
      this.send = (msg) => worker.postMessage(msg);
    } else {
      const handle = createSearchHandler();
      this.send = (msg) =>
        setTimeout(() => {
          const answer = handle(msg);
          if (answer) this.receive(answer);
        }, 0);
    }
  }

  private receive(answer: FromWorker) {
    if (answer.type === "ready") {
      this.ready = true;
      this.readyEnd?.({ rows: answer.count, workerMs: Math.round(answer.ms * 10) / 10 });
      this.readyEnd = null;
      this.bump();
      return;
    }
    const p = this.pending.get(answer.seq);
    this.pending.delete(answer.seq);
    p?.resolve(answer);
  }

  private bump() {
    this.version++;
    // For the scale project: whether the worker has the rows and every database's tables.
    const holder = globalThis as unknown as { __mqPerf?: Record<string, unknown> };
    holder.__mqPerf ??= {};
    holder.__mqPerf.search = {
      ready: this.ready,
      updates: this.updates,
      updatedAt: this.updatedAt,
      tablesPending: [...this.tables.values()].filter((t) => !t).length,
    };
    for (const l of this.listeners) l();
  }

  /** Changes whenever the searchable data changes: a filter re-runs on it (table names landing, say). */
  getVersion = () => this.version;
  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => void this.listeners.delete(listener);
  };

  /** Whether these rows were patched from the rows the worker holds, so `setRows` sends only the difference. */
  canPatch(rows: readonly ElementSummary[]): boolean {
    return rows !== this.rows && patchesBetween(this.rows, rows) !== null;
  }

  /**
   * The index rows. The same array twice is a no-op. Rows patched from the ones the worker holds (model.changed,
   * a save) go as update messages with only the changed rows; any other array is handed over whole.
   */
  setRows(rows: readonly ElementSummary[] | undefined) {
    if (!rows || rows === this.rows) return;
    const chain = patchesBetween(this.rows, rows);
    if (chain) {
      const end = perfStart("search:update");
      for (const p of chain) this.send({ type: "update", data: encodeRows(p.upserts), deleted: p.deleted, moves: p.moves });
      this.rows = rows;
      this.updates++;
      this.updatedAt = performance.now();
      end({ patches: chain.length, rows: chain.reduce((n, p) => n + p.upserts.length + p.deleted.length, 0) });
      this.bump();
      return;
    }
    this.rows = rows;
    this.ready = false;
    this.readyEnd = perfStart("search:ready");
    const handoff = perfStart("search:handoff");
    this.send({ type: "rows", version: this.version + 1, data: encodeRows(rows) });
    handoff({ rows: rows.length });
    this.bump();
  }

  /** One database's table summaries; undefined while they have not arrived. */
  setTables(db: string, tables: readonly TableSummary[] | undefined) {
    const next = tables ?? null;
    if (this.tables.has(db) && this.tables.get(db) === next) return;
    this.tables.set(db, next);
    this.send({ type: "tables", db, data: next ? encodeTables(db, next) : null });
    this.bump();
  }

  /** The category tree's nodes, for `cat:` and the Category chips (descendants match too). */
  setCategories(categories: readonly Category[] | undefined) {
    if (!categories || categories === this.categories) return;
    this.categories = categories;
    this.send({ type: "categories", data: categories });
    this.bump();
  }

  private ask<T extends FromWorker>(msg: DistributiveOmit<Extract<ToWorker, { seq: number }>, "seq">): Promise<T> {
    const seq = ++this.seq;
    return new Promise<T>((resolve) => {
      this.pending.set(seq, { resolve: resolve as (a: FromWorker) => void });
      this.send({ ...msg, seq } as ToWorker);
    });
  }

  /** The tree filter: the matching ids of one place, and every place's count. */
  filter(text: string, place: SearchPlace, extra?: FilterExtra): Promise<Omit<FilterAnswer, "ids"> & { ids: string[] }> {
    const end = perfStart("search:filter");
    return this.ask<FilterAnswer>({ type: "filter", text, place, extra }).then((a) => {
      const ids = idsOf(a.ids);
      end({ ids: ids.length, workerMs: Math.round(a.ms * 10) / 10 });
      return { ...a, ids };
    });
  }

  /** Quick open and the palette: the first `limit` ranked hits, and how many there are. */
  rank(text: string, limit = 200, context?: RankContext): Promise<RankAnswer> {
    const end = perfStart("search:rank");
    return this.ask<RankAnswer>({ type: "rank", text, limit, context }).then((a) => {
      end({ hits: a.hits.length, total: a.total, workerMs: Math.round(a.ms * 10) / 10 });
      return a;
    });
  }
}

let client: SearchClient | null = null;

/** The page's search client, with a worker when the browser has them. */
export function searchClient(): SearchClient {
  if (!client) {
    let worker: Worker | null = null;
    if (typeof Worker !== "undefined" && !import.meta.env?.VITEST) {
      try {
        worker = new Worker(new URL("./worker.ts", import.meta.url), { type: "module", name: "search" });
      } catch {
        worker = null;
      }
    }
    client = new SearchClient(worker);
  }
  return client;
}

/** Replaces the page's client (tests). */
export function setSearchClient(next: SearchClient | null): void {
  client = next;
}

/** The searchable data's version, re-rendering on change. */
export function useSearchVersion(): number {
  const c = searchClient();
  return useSyncExternalStore(c.subscribe, c.getVersion, c.getVersion);
}

/** Feeds the index rows to the search client. */
export function useSearchRows(rows: readonly ElementSummary[] | undefined): void {
  // A patch (model.changed, a save) is a few rows: sent with the tree's commit, before paint, so search is current
  // with the tree (explorer-redesign.md 4.4).
  useLayoutEffect(() => {
    if (rows && searchClient().canPatch(rows)) searchClient().setRows(rows);
  }, [rows]);
  useEffect(() => {
    // After the first paint: the handoff is not on the explorer's critical path.
    if (!rows) return;
    const t = setTimeout(() => searchClient().setRows(rows), 0);
    return () => clearTimeout(t);
  }, [rows]);
}
