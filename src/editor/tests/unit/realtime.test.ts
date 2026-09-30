// The mock realtime transport and the cache patching it drives (phase2-design.md 4.3, 4.5).
// Every wait is on the state a step needs (joined and connected, the event delivered, the query invalidated), never on a pause
// and never on a poll: each wait subscribes to the thing that changes (the editor store, the query cache, the bus) and checks
// its condition on every notification, so a loaded run only makes the wait longer (bounded by the test timeout), where a
// 5 s poll could time out before the connection was up or the invalidation had landed (the flake seen under two agents' load).
import { describe, expect, it } from "vitest";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";
import { MockRealtime } from "@/realtime/mock";
import { connectRealtime } from "@/realtime/sync";
import { newId } from "@/lib/ids";
import { MAX_EVENT_BYTES } from "@/mocks/wire";
import { IDS, useMockApi } from "./harness";

/** Resolves the first time `check` holds: now, or on a later notification of `subscribe`. */
function until(subscribe: (listener: () => void) => () => void, check: () => boolean): Promise<void> {
  return new Promise((resolve) => {
    if (check()) return resolve();
    const off = subscribe(() => {
      if (!check()) return;
      off();
      resolve();
    });
  });
}

describe("MockRealtime", () => {
  it("shares one connection between concurrent callers", async () => {
    const rt = new MockRealtime();
    const off = rt.on("model.changed", () => undefined);
    await rt.join("editors");
    expect(rt.connectionId).toBe("mock-connection-1");
    off();
  });

  it("delivers group events only to joined groups", async () => {
    const rt = new MockRealtime();
    const seen: string[] = [];
    let arrived: () => void = () => undefined;
    const delivered = new Promise<void>((resolve) => (arrived = resolve));
    rt.on("job.progress", (job) => {
      seen.push(job.id);
      if (job.id === "A") arrived();
    });
    await rt.join("job:A");
    // B first: deliveries run in publish order, so by the time A arrives B has been dropped (or, wrongly, delivered).
    rt.publish("job.progress", { id: "B" } as never, "job:B");
    rt.publish("job.progress", { id: "A" } as never, "job:A");
    expect(seen).toEqual([]); // delivered asynchronously, as over the wire
    await delivered;
    expect(seen).toEqual(["A"]);
  });
});

describe("realtime cache patching", () => {
  const api = useMockApi();

  /** Resolves once this window has joined "editors", is connected and has reported its presence (the connect-time work is done). */
  // The last step of connecting is this window's presence reaching the backend, which publishes presence.changed into the store.
  async function connected(services: typeof api.services) {
    await until(
      (l) => services.store.subscribe(l),
      () =>
        api.backend.realtime.joins.includes("editors") &&
        services.store.getState().connection === "connected" &&
        [...api.backend.presence.keys()].some((id) => id !== "mock-colleague"),
    );
  }

  /** Resolves when the query under this key is invalidated (the query cache notifies every state change). */
  function invalidated(queryClient: typeof api.services.queryClient, queryKey: readonly unknown[]) {
    return until(
      (l) => queryClient.getQueryCache().subscribe(l),
      () => queryClient.getQueryState(queryKey)?.isInvalidated === true,
    );
  }

  /** Resolves when the next event of this name has reached every handler registered before this call. */
  function delivered(event: "model.changed" | "site.deployed") {
    return new Promise<void>((resolve) => {
      const off = api.backend.realtime.on(event, () => {
        off();
        resolve();
      });
    });
  }

  async function connect() {
    const services = api.services;
    const stop = connectRealtime({ ...services, reload: () => undefined, delays: { index: 0, preview: 0 } });
    await connected(services);
    return { ...services, stop };
  }

  it("joins editors and reports presence on connect", async () => {
    const { stop } = await connect();
    expect(api.backend.realtime.joins).toContain("editors");
    expect(api.backend.presence.size).toBe(1);
    stop();
  });

  it("reports presence again when the selection or workspace changes, debounced", async () => {
    const { stop, store } = await connect();
    const mine = () => [...api.backend.presence.values()].find((p) => p.connectionId !== "mock-colleague");
    store.getState().select([IDS.invoice]);
    store.getState().setWorkspace("database");
    expect(mine()?.elementId ?? null).toBeNull();
    await until(
      (l) => store.subscribe(l),
      () => mine()?.elementId === IDS.invoice && mine()?.workspace === "database",
    );
    expect(mine()).toMatchObject({ elementId: IDS.invoice, workspace: "database" });
    stop();
  });

  it("patches the index from the change's summary (E5d) and invalidates the element on a change made elsewhere", async () => {
    const { queryClient, stop } = await connect();
    await queryClient.fetchQuery({ queryKey: keys.index, queryFn: endpoints.getModelIndex });
    await queryClient.fetchQuery({ queryKey: keys.element(IDS.invoice), queryFn: () => endpoints.getElement(IDS.invoice) });
    const arrived = delivered("model.changed");
    api.backend.model.externalEdit(IDS.invoice, (json) => {
      json.name = "Bill";
    });
    await arrived;
    await invalidated(queryClient, keys.element(IDS.invoice));
    expect(queryClient.getQueryState(keys.index)?.isInvalidated).toBe(false);
    const rows = queryClient.getQueryData<{ id: string; name: string }[]>(keys.index);
    expect(rows?.find((r) => r.id === IDS.invoice)?.name).toBe("Bill");
    expect(queryClient.getQueryState(keys.element(IDS.invoice))?.isInvalidated).toBe(true);
    stop();
  });

  it("invalidates the index when a change carries no summary (an older server)", async () => {
    const { queryClient, stop } = await connect();
    await queryClient.fetchQuery({ queryKey: keys.index, queryFn: endpoints.getModelIndex });
    api.backend.realtime.publish("model.changed", {
      changed: [{ id: IDS.invoice, kind: "entity", path: ".maquettiste/model/entities/invoice.json", hash: "0".repeat(64) }],
      deleted: [],
      source: "disk",
      truncated: false,
      isEmpty: false,
    });
    await invalidated(queryClient, keys.index);
    stop();
  });

  it("cuts a large model.changed to 200 KB with truncated set, and the editor refetches the index", async () => {
    const { queryClient, stop } = await connect();
    await queryClient.fetchQuery({ queryKey: keys.index, queryFn: endpoints.getModelIndex });
    const before = api.backend.realtime.published.length;
    const operations = Array.from({ length: 1000 }, (_, i) => ({
      op: "create",
      element: { kind: "package", id: newId(), name: `BulkPackage${i}`, description: "A package created in bulk to make a large change set." },
    }));
    const result = api.backend.model.batch({ operations });
    expect(result.status).toBe(200);
    const events = api.backend.realtime.published.slice(before).filter((e) => e.event === "model.changed");
    expect(events).toHaveLength(1);
    const payload = events[0]!.payload as { changed: unknown[]; deleted: unknown[]; truncated: boolean };
    expect(new TextEncoder().encode(JSON.stringify(payload)).length).toBeLessThanOrEqual(MAX_EVENT_BYTES);
    expect(payload.truncated).toBe(true);
    expect(payload.changed.length).toBeGreaterThan(0);
    expect(payload.changed.length).toBeLessThan(1000);
    await invalidated(queryClient, keys.index);
    stop();
  });

  it("ignores the echo of this window's own save", async () => {
    const { queryClient, drafts, stop } = await connect();
    await queryClient.fetchQuery({ queryKey: keys.index, queryFn: endpoints.getModelIndex });
    queryClient.setQueryData(keys.element(IDS.invoice), await endpoints.getElement(IDS.invoice));
    const echo = delivered("model.changed");
    drafts.edit(IDS.invoice, (json) => {
      (json as { name: string }).name = "Bill";
    });
    await drafts.flush(IDS.invoice);
    await echo; // the editor's handler ran before this one: the echo has been seen and ignored
    expect(queryClient.getQueryState(keys.index)?.isInvalidated).toBe(false);
    const row = queryClient.getQueryData<{ id: string; name: string }[]>(keys.index)!.find((r) => r.id === IDS.invoice);
    expect(row?.name).toBe("Bill");
    stop();
  });

  it("keeps presence from presence.changed in the store", async () => {
    const { store, stop } = await connect();
    await until(
      (l) => store.subscribe(l),
      () => store.getState().presence.length > 0,
    );
    stop();
  });

  it("reloads on site.deployed, or shows the new-version banner while a draft is unsaved", async () => {
    const services = api.services;
    let reloads = 0;
    const stop = connectRealtime({ ...services, reload: () => reloads++, delays: { index: 0, preview: 0 } });
    await connected(services);
    const deployed = { release: "r2", functions: false, source: "deploy" } as never;
    let arrived = delivered("site.deployed");
    api.backend.realtime.publish("site.deployed", deployed);
    await arrived;
    expect(reloads).toBe(1);
    expect(services.store.getState().banner).toBeNull();

    await services.queryClient.fetchQuery({ queryKey: keys.element(IDS.invoice), queryFn: () => endpoints.getElement(IDS.invoice) });
    services.drafts.edit(IDS.invoice, (json) => {
      (json as { name: string }).name = "Bill";
    });
    arrived = delivered("site.deployed");
    api.backend.realtime.publish("site.deployed", deployed);
    await arrived;
    expect(reloads).toBe(1);
    expect(services.store.getState().banner?.kind).toBe("deployed");
    stop();
  });
});
