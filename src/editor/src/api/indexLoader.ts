// The model index with its ETag (explorer-redesign.md 4.1 E5e and 4.3). The loader remembers the
// last index it received with its ETag and sends that ETag as `If-None-Match`; a 304 answers with
// the remembered rows (the same array, so nothing downstream rebuilds). The ETag is a hash of the
// index's content, so a 304 is correct even after the cache was patched from change events.
//
// Across page loads the last index is kept in the browser's Cache Storage next to its ETag, so a
// warm reopen costs a 304 and no transfer. The HTTP cache would do the same in live mode (the index
// is sent `no-cache`), but not behind the mock's service worker, and a script-set If-None-Match
// turns the HTTP cache off for that request. Every storage access is best effort: private windows,
// blocked site data and tests without Cache Storage just fetch the whole index.
import type { ElementSummary } from "./types";
import type { IndexAnswer } from "./endpoints";
import { perfStart } from "@/lib/perf";
import { rememberIndexText } from "./indexText";

export interface IndexSnapshot {
  etag: string | null;
  rows: ElementSummary[];
}

/** The persisted copy: its ETag without the body, and the body when a 304 needs it. */
export interface IndexStore {
  etag(): Promise<string | null>;
  text(): Promise<string | null>;
  save(etag: string, text: string): void;
}

export interface IndexLoaderDeps {
  fetch: (etag: string | null) => Promise<IndexAnswer>;
  store?: IndexStore | null;
}

export interface IndexLoader {
  load(): Promise<ElementSummary[]>;
  /** The last snapshot received (tests, the search worker's handoff). */
  readonly last: IndexSnapshot | null;
}

const CACHE_NAME = "maquettiste-index-v1";
const CACHE_KEY = "/api/model/index";

/** Cache Storage when the page has it (a secure context), else null. */
export function browserIndexStore(): IndexStore | null {
  if (typeof caches === "undefined") return null;
  const entry = async () => {
    try {
      return (await (await caches.open(CACHE_NAME)).match(CACHE_KEY)) ?? null;
    } catch {
      return null;
    }
  };
  return {
    async etag() {
      return (await entry())?.headers.get("ETag") ?? null;
    },
    async text() {
      try {
        return (await (await entry())?.text()) ?? null;
      } catch {
        return null;
      }
    },
    save(etag, text) {
      void (async () => {
        try {
          const response = new Response(text, { headers: { "Content-Type": "application/json", ETag: etag } });
          await (await caches.open(CACHE_NAME)).put(CACHE_KEY, response);
        } catch {
          /* storage full or blocked: the next open fetches the whole index */
        }
      })();
    },
  };
}

function parse(text: string, source: string): ElementSummary[] {
  const end = perfStart("index:parse");
  const rows = JSON.parse(text) as ElementSummary[];
  end({ bytes: text.length, rows: rows.length, source });
  rememberIndexText(rows, text);
  return rows;
}

export function createIndexLoader(deps: IndexLoaderDeps): IndexLoader {
  let last: IndexSnapshot | null = null;
  const store = deps.store ?? null;

  const load = async (): Promise<ElementSummary[]> => {
    const persisted = last ? null : await store?.etag();
    const etag = last?.etag ?? persisted ?? null;
    const endFetch = perfStart("index:fetch");
    let answer = await deps.fetch(etag);
    endFetch({ status: answer.notModified ? 304 : 200, conditional: etag !== null });
    if (answer.notModified) {
      if (last && last.etag === etag) return last.rows;
      const text = await store?.text();
      if (text !== null && text !== undefined) {
        last = { etag, rows: parse(text, "storage") };
        return last.rows;
      }
      // The stored body is gone: ask again without a condition.
      answer = await deps.fetch(null);
      if (answer.notModified) throw new Error("The server answered 304 to an unconditional index request.");
    }
    const rows = parse(answer.text, "network");
    last = { etag: answer.etag, rows };
    if (answer.etag) store?.save(answer.etag, answer.text);
    return rows;
  };

  return {
    load,
    get last() {
      return last;
    },
  };
}
