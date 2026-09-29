// The mock realtime transport and the cache patching it drives (phase2-design.md 4.3, 4.5).
// Every wait is on the state a step needs (joined and connected, the event delivered, the query invalidated), never on a pause:
// under a loaded run a fixed pause could end before the connection was up, and an event published then is dropped.
import { describe, expect, it, vi } from "vitest";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";
import { MockRealtime } from "@/realtime/mock";
import { connectRealtime } from "@/realtime/sync";
import { newId } from "@/lib/ids";
import { MAX_EVENT_BYTES } from "@/mocks/wire";
import { IDS, useMockApi } from "./harness";

const WAIT = { timeout: 5000, interval: 5 };

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
    rt.on("job.progress", (job) => seen.push(job.id));
    await rt.join("job:A");
    // B first: deliveries run in publish order, so by the time A arrives B has been dropped (or, wrongly, delivered).
    rt.publish("job.progress", { id: "B" } as never, "job:B");
    rt.publish("job.progress", { id: "A" } as never, "job:A");
    expect(seen).toEqual([]); // delivered asynchronously, as over the wire
    await vi.waitFor(() => expect(seen).toContain("A"), WAIT);
    expect(seen).toEqual(["A"]);
  });
});

describe("realtime cache patching", () => {
  const api = useMockApi();

  /** Resolves once this window has joined "editors", is connected and has reported its presence (the connect-time work is done). */
  async function connected(services: typeof api.services) {
    await vi.waitFor(() => {
      expect(api.backend.realtime.joins).toContain("editors");
      expect(services.store.getState().connection).toBe("connected");
      expect([...api.backend.presence.keys()].some((id) => id !== "mock-colleague")).toBe(true);
    }, WAIT);
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
    await vi.waitFor(() => expect(mine()).toMatchObject({ elementId: IDS.invoice, workspace: "database" }), WAIT);
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
    await vi.waitFor(() => expect(queryClient.getQueryState(keys.element(IDS.invoice))?.isInvalidated).toBe(true), WAIT);
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
    await vi.waitFor(() => expect(queryClient.getQueryState(keys.index)?.isInvalidated).toBe(true), WAIT);
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
    await vi.waitFor(() => expect(queryClient.getQueryState(keys.index)?.isInvalidated).toBe(true), WAIT);
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
    await vi.waitFor(() => expect(store.getState().presence.length).toBeGreaterThan(0), WAIT);
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
