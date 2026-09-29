// Editor wiring of the E5 contract (explorer-redesign.md 6 step 3): batched element reads in chunks
// of 200 with the fallback to single GETs, the conditional index read (E5e), table summaries with
// the last good list (E5c), and index patching from change summaries (E5d).
import { http, HttpResponse } from "msw";
import { QueryClient } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { ApiProblem, ApiUnexpectedResponse } from "@/api/client";
import * as endpoints from "@/api/endpoints";
import { createElementLoader, GET_CONCURRENCY, MAX_READ_IDS } from "@/api/elementLoader";
import { createIndexLoader, type IndexStore } from "@/api/indexLoader";
import { keys, loadElement, loadIndex, mergeTables, patchIndex, shapesTables, tablesQuery, type TablesState } from "@/api/queries";
import type { ChangeSet, DatabaseTablesResult, ElementDocument, ElementReadResult, ElementSummary } from "@/api/types";
import { IDS, useMockApi } from "./harness";

const doc = (id: string): ElementDocument => ({ json: { id }, hash: `h-${id}`, path: `p/${id}.json` }) as unknown as ElementDocument;
const ids = (n: number) => Array.from({ length: n }, (_, i) => `id${String(i).padStart(4, "0")}`);
const row = (id: string, name = id, kind: ElementSummary["kind"] = "entity"): ElementSummary =>
  ({ id, kind, name, package: null, tags: [], category: null, stereotypes: [], hash: `h-${id}`, path: `p/${id}` }) as unknown as ElementSummary;

/** A manual scheduler: the flush runs when the test says so, after every load of the task. */
function manual() {
  let pending: (() => void) | null = null;
  return { schedule: (f: () => void) => (pending = f), flush: () => pending?.() };
}

describe("batched element reads", () => {
  it("reads 450 ids in chunks of 200 and answers each waiter, a repeated id once", async () => {
    const calls: string[][] = [];
    const clock = manual();
    const loader = createElementLoader({
      read: async (chunk) => {
        calls.push(chunk);
        return { elements: chunk.map(doc), missing: [] };
      },
      get: () => Promise.reject(new Error("no GET expected")),
      schedule: clock.schedule,
    });
    const all = ids(450);
    const loads = [...all, all[0]].map((id) => loader.load(id));
    clock.flush();
    const docs = await Promise.all(loads);
    expect(calls.map((c) => c.length)).toEqual([MAX_READ_IDS, MAX_READ_IDS, 50]);
    expect(docs.map((d) => (d.json as { id: string }).id)).toEqual([...all, all[0]]);
    expect(loader.fallback).toBe(false);
  });

  it("rejects missing ids as not-found and reads a sub-element id on its own", async () => {
    const clock = manual();
    const gets: string[] = [];
    const loader = createElementLoader({
      read: async () => ({ elements: [doc("holder")], missing: ["gone"] }) as ElementReadResult,
      get: async (id) => {
        gets.push(id);
        return doc("holder");
      },
      schedule: clock.schedule,
    });
    const gone = loader.load("gone").catch((e: unknown) => e);
    const sub = loader.load("sub");
    const holder = loader.load("holder");
    clock.flush();
    const error = await gone;
    expect(error).toBeInstanceOf(ApiProblem);
    expect((error as ApiProblem).code).toBe("not-found");
    expect((error as ApiProblem).status).toBe(404);
    expect(((await sub).json as { id: string }).id).toBe("holder");
    expect(((await holder).json as { id: string }).id).toBe("holder");
    expect(gets).toEqual(["sub"]);
  });

  it("falls back to single GETs, at most 16 in flight, against a server without the batched read, and remembers it", async () => {
    const clock = manual();
    let reads = 0;
    let inFlight = 0;
    let peak = 0;
    const loader = createElementLoader({
      read: async () => {
        reads++;
        throw new ApiUnexpectedResponse("/api/model/elements/read", 200, "text/html");
      },
      get: async (id) => {
        inFlight++;
        peak = Math.max(peak, inFlight);
        await new Promise((r) => setTimeout(r, 1));
        inFlight--;
        return doc(id);
      },
      schedule: clock.schedule,
    });
    const first = ids(100).map((id) => loader.load(id));
    clock.flush();
    expect((await Promise.all(first)).length).toBe(100);
    expect(peak).toBe(GET_CONCURRENCY);
    expect(loader.fallback).toBe(true);
    const again = loader.load("next");
    clock.flush();
    await again;
    expect(reads).toBe(1);
  });

  it("treats a 404 or 405 without a not-found problem as an older server, and a 500 as an error", async () => {
    for (const [status, fallback] of [
      [404, true],
      [405, true],
      [500, false],
    ] as const) {
      const clock = manual();
      const loader = createElementLoader({
        read: () => Promise.reject(new ApiProblem(status, null)),
        get: async (id) => doc(id),
        schedule: clock.schedule,
      });
      const load = loader.load("a").then(
        () => "ok",
        () => "error",
      );
      clock.flush();
      expect(await load).toBe(fallback ? "ok" : "error");
      expect(loader.fallback).toBe(fallback);
    }
  });
});

describe("batched reads against the mock API", () => {
  const mock = useMockApi();

  it("loads documents through POST /api/model/elements/read in one request", async () => {
    let posts = 0;
    let gets = 0;
    mock.server.events.on("request:start", ({ request }) => {
      const path = new URL(request.url).pathname;
      if (request.method === "POST" && path === "/api/model/elements/read") posts++;
      if (request.method === "GET" && path.startsWith("/api/model/elements/")) gets++;
    });
    const docs = await Promise.all([IDS.invoice, IDS.payment, IDS.overview].map(loadElement));
    expect(docs.map((d) => (d.json as { id: string }).id)).toEqual([IDS.invoice, IDS.payment, IDS.overview]);
    expect(posts).toBe(1);
    expect(gets).toBe(0);
    mock.server.events.removeAllListeners();
  });

  it("falls back to GETs when the server answers the batched read with the SPA's HTML", async () => {
    mock.server.use(http.post(mock.url("/api/model/elements/read"), () => new HttpResponse("<!doctype html>", { headers: { "Content-Type": "text/html" } })));
    const docs = await Promise.all([IDS.invoice, IDS.payment].map(loadElement));
    expect(docs.map((d) => (d.json as { id: string }).id)).toEqual([IDS.invoice, IDS.payment]);
  });

  it("sends If-None-Match on the second index read and keeps the same rows on 304", async () => {
    const seen: (string | null)[] = [];
    const statuses: number[] = [];
    mock.server.events.on("request:start", ({ request }) => {
      if (new URL(request.url).pathname === "/api/model/index") seen.push(request.headers.get("If-None-Match"));
    });
    mock.server.events.on("response:mocked", ({ request, response }) => {
      if (new URL(request.url).pathname === "/api/model/index") statuses.push(response.status);
    });
    const first = await loadIndex();
    const second = await loadIndex();
    mock.server.events.removeAllListeners();
    expect(first.length).toBeGreaterThan(0);
    expect(second).toBe(first);
    expect(seen[0]).toBeNull();
    expect(seen[1]).toMatch(/.+/);
    expect(statuses).toEqual([200, 304]);
  });
});

describe("conditional index loader", () => {
  const rows = [row("a"), row("b")];
  const text = JSON.stringify(rows);

  function store(initial: { etag: string; text: string } | null): IndexStore & { saved: string[] } {
    let entry = initial;
    const saved: string[] = [];
    return {
      saved,
      etag: async () => entry?.etag ?? null,
      text: async () => entry?.text ?? null,
      save: (etag, body) => {
        saved.push(etag);
        entry = { etag, text: body };
      },
    };
  }

  it("uses the persisted ETag on open and answers a 304 from the stored body", async () => {
    const asked: (string | null)[] = [];
    const loader = createIndexLoader({
      fetch: async (etag) => {
        asked.push(etag);
        return { notModified: true, etag };
      },
      store: store({ etag: '"t1"', text }),
    });
    expect((await loader.load()).map((r) => r.id)).toEqual(["a", "b"]);
    expect(asked).toEqual(['"t1"']);
    expect(loader.last?.etag).toBe('"t1"');
  });

  it("stores a 200 with its ETag, and asks again without a condition when the stored body is gone", async () => {
    const asked: (string | null)[] = [];
    const persisted = store(null);
    persisted.etag = async () => '"old"';
    const loader = createIndexLoader({
      fetch: async (etag) => {
        asked.push(etag);
        return etag ? { notModified: true, etag } : { notModified: false, etag: '"t2"', text };
      },
      store: persisted,
    });
    expect((await loader.load()).length).toBe(2);
    expect(asked).toEqual(['"old"', null]);
    expect(persisted.saved).toEqual(['"t2"']);
  });

  it("replaces the rows when the ETag changed", async () => {
    let version = 1;
    const loader = createIndexLoader({
      fetch: async (etag) =>
        etag === `"v${version}"` ? { notModified: true, etag } : { notModified: false, etag: `"v${version}"`, text: JSON.stringify([row(`r${version}`)]) },
    });
    const first = await loader.load();
    expect(await loader.load()).toBe(first);
    version = 2;
    expect((await loader.load()).map((r) => r.id)).toEqual(["r2"]);
  });
});

describe("table summaries and index patching", () => {
  const result = (names: string[], partial: boolean): DatabaseTablesResult =>
    ({ tables: names.map((name) => ({ key: name, name })), diagnostics: partial ? [{ id: "MQ2001" }] : [], partial }) as unknown as DatabaseTablesResult;

  it("keeps the last complete tables, marked stale, while answers are partial", () => {
    const complete = mergeTables(undefined, result(["a", "b"], false));
    expect(complete.stale).toBe(false);
    const stale = mergeTables(complete, result(["a"], true));
    expect(stale.stale).toBe(true);
    expect(stale.tables.map((t) => t.name)).toEqual(["a", "b"]);
    expect(stale.diagnostics.length).toBe(1);
    const still: TablesState = mergeTables(stale, result([], true));
    expect(still.tables.map((t) => t.name)).toEqual(["a", "b"]);
    expect(mergeTables(still, result(["c"], false)).tables.map((t) => t.name)).toEqual(["c"]);
    const firstPartial = mergeTables(undefined, result(["x"], true));
    expect(firstPartial).toMatchObject({ stale: false, partial: true });
  });

  it("refetches tables only for kinds that shape tables", () => {
    const set = (kind: ElementSummary["kind"], deleted: string[] = []): ChangeSet =>
      ({ changed: kind ? [{ id: "x", kind, path: "p", hash: "h" }] : [], deleted, source: "disk", truncated: false, isEmpty: false }) as ChangeSet;
    expect(shapesTables(set("entity"), [])).toBe(true);
    expect(shapesTables(set("diagram"), [])).toBe(false);
    expect(shapesTables(set("tag-vocabulary"), [])).toBe(false);
    const none = { ...set("diagram"), changed: [] } as ChangeSet;
    expect(shapesTables({ ...none, deleted: ["d"] }, [row("d", "d", "diagram")])).toBe(false);
    expect(shapesTables({ ...none, deleted: ["e"] }, [row("e")])).toBe(true);
    expect(shapesTables({ ...none, truncated: true }, [])).toBe(true);
  });

  it("patches the index in one pass from change summaries and deletions", () => {
    const qc = new QueryClient();
    qc.setQueryData(keys.index, [row("a"), row("b"), row("c")]);
    qc.setQueryData(keys.element("c"), doc("c"));
    const applied = patchIndex(qc, {
      changed: [
        { id: "b", kind: "entity", path: "p/b", hash: "h2", summary: row("b", "Bee") },
        { id: "n", kind: "entity", path: "p/n", hash: "h3", summary: row("n", "New") },
      ],
      deleted: ["c"],
      source: "disk",
      truncated: false,
      isEmpty: false,
    } as ChangeSet);
    expect(applied).toBe(true);
    expect(qc.getQueryData<ElementSummary[]>(keys.index)?.map((r) => r.name)).toEqual(["a", "Bee", "New"]);
    expect(qc.getQueryData(keys.element("c"))).toBeUndefined();
    const older = patchIndex(qc, {
      changed: [{ id: "a", kind: "entity", path: "p", hash: "h" }],
      deleted: [],
      source: "disk",
      truncated: false,
      isEmpty: false,
    } as ChangeSet);
    expect(older).toBe(false);
  });
});

describe("table summaries against the mock API", () => {
  useMockApi();

  it('loads a database\'s tables into ["tables", id]', async () => {
    const qc = new QueryClient();
    const database = (await endpoints.getModelIndex()).find((r) => r.kind === "database");
    expect(database).toBeDefined();
    const state = await qc.fetchQuery(tablesQuery(qc, database!.id));
    expect(state.tables.length).toBeGreaterThan(0);
    expect(state.stale).toBe(false);
    expect(qc.getQueryData(keys.databaseTables(database!.id))).toBe(state);
  });
});
