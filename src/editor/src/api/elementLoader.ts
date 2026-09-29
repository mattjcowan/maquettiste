// Batched element reads (explorer-redesign.md 4.1 E5b and 6 step 3). Every ["element", id] query
// asks this loader; the ids asked within one task are read together with POST
// /api/model/elements/read in chunks of MAX_READ_IDS, so a diagram of 300 members, or every row
// expanded in one frame, costs two requests instead of 300. Against an older server, which has no
// batched read, the loader falls back to one GET per id with at most GET_CONCURRENCY in flight,
// and remembers that for the rest of the page's life.
import { ApiProblem, ApiUnexpectedResponse } from "./client";
import { MAX_READ_IDS } from "./endpoints";
import type { ElementDocument, ElementReadResult } from "./types";

export { MAX_READ_IDS };
/** The most single GETs in flight when falling back. */
export const GET_CONCURRENCY = 16;
/** The most batched reads in flight at once. */
export const READ_CONCURRENCY = 4;

export interface ElementLoaderDeps {
  read: (ids: string[]) => Promise<ElementReadResult>;
  get: (id: string) => Promise<ElementDocument>;
  /** Runs the flush after the current task; tests pass a synchronous or manual scheduler. */
  schedule?: (flush: () => void) => void;
}

interface Waiter {
  resolve: (doc: ElementDocument) => void;
  reject: (error: unknown) => void;
}

export interface ElementLoader {
  load(id: string): Promise<ElementDocument>;
  /** True once the server has shown it has no batched read. */
  readonly fallback: boolean;
}

/** An answer that means the route does not exist: a server from before E5b. */
export function isMissingRoute(error: unknown): boolean {
  if (error instanceof ApiUnexpectedResponse) return true;
  return error instanceof ApiProblem && (error.status === 404 || error.status === 405 || error.status === 501) && error.code !== "not-found";
}

function notFound(id: string): ApiProblem {
  return new ApiProblem(404, { title: `No element has the id ${id}.`, status: 404, code: "not-found" });
}

/** Runs `work` over `items` with at most `limit` in flight. */
export async function pooled<T>(items: readonly T[], limit: number, work: (item: T) => Promise<void>): Promise<void> {
  let next = 0;
  const lanes = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) await work(items[next++]);
  });
  await Promise.all(lanes);
}

export function createElementLoader(deps: ElementLoaderDeps): ElementLoader {
  const schedule = deps.schedule ?? ((flush) => setTimeout(flush, 0));
  let pending = new Map<string, Waiter[]>();
  let scheduled = false;
  let fallback = false;

  const settle = (waiters: Map<string, Waiter[]>, id: string, outcome: { doc: ElementDocument } | { error: unknown }) => {
    for (const w of waiters.get(id) ?? []) {
      if ("doc" in outcome) w.resolve(outcome.doc);
      else w.reject(outcome.error);
    }
    waiters.delete(id);
  };

  const getEach = (ids: readonly string[], waiters: Map<string, Waiter[]>) =>
    pooled(ids, GET_CONCURRENCY, async (id) => {
      try {
        settle(waiters, id, { doc: await deps.get(id) });
      } catch (error) {
        settle(waiters, id, { error });
      }
    });

  const readChunk = async (ids: string[], waiters: Map<string, Waiter[]>) => {
    if (fallback) return getEach(ids, waiters);
    let result: ElementReadResult;
    try {
      result = await deps.read(ids);
    } catch (error) {
      if (isMissingRoute(error)) fallback = true;
      // An older server, or a chunk the server refused (400): the same ids one at a time.
      if (isMissingRoute(error) || (error instanceof ApiProblem && error.status === 400)) return getEach(ids, waiters);
      for (const id of ids) settle(waiters, id, { error });
      return;
    }
    const byId = new Map<string, ElementDocument>();
    for (const doc of result.elements) byId.set((doc.json as { id?: string }).id ?? "", doc);
    for (const id of result.missing) settle(waiters, id, { error: notFound(id) });
    const rest: string[] = [];
    for (const id of ids) {
      if (!waiters.has(id)) continue;
      const doc = byId.get(id);
      if (doc) settle(waiters, id, { doc });
      else rest.push(id); // a sub-element id answered by its holder: read it on its own
    }
    if (rest.length > 0) await getEach(rest, waiters);
  };

  const flush = () => {
    scheduled = false;
    const waiters = pending;
    pending = new Map();
    const ids = [...waiters.keys()];
    const chunks: string[][] = [];
    for (let i = 0; i < ids.length; i += MAX_READ_IDS) chunks.push(ids.slice(i, i + MAX_READ_IDS));
    if (fallback) void getEach(ids, waiters);
    else void pooled(chunks, READ_CONCURRENCY, (chunk) => readChunk(chunk, waiters));
  };

  return {
    load(id) {
      return new Promise<ElementDocument>((resolve, reject) => {
        const list = pending.get(id);
        if (list) list.push({ resolve, reject });
        else pending.set(id, [{ resolve, reject }]);
        if (!scheduled) {
          scheduled = true;
          schedule(flush);
        }
      });
    },
    get fallback() {
      return fallback;
    },
  };
}
