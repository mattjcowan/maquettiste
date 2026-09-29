// The mock realtime transport and the cache patching it drives (phase2-design.md 4.3, 4.5).
import { describe, expect, it, vi } from "vitest";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";
import { MockRealtime } from "@/realtime/mock";
import { connectRealtime } from "@/realtime/sync";
import { newId } from "@/lib/ids";
import { MAX_EVENT_BYTES } from "@/mocks/wire";
import { IDS, useMockApi } from "./harness";

const settle = () => new Promise((r) => setTimeout(r, 20));

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
    rt.publish("job.progress", { id: "A" } as never, "job:A");
    rt.publish("job.progress", { id: "B" } as never, "job:B");
    expect(seen).toEqual([]); // delivered asynchronously, as over the wire
    await settle();
    expect(seen).toEqual(["A"]);
  });
});

describe("realtime cache patching", () => {
  const api = useMockApi();

  function connect() {
    const services = api.services;
    const stop = connectRealtime({ ...services, reload: () => undefined, delays: { index: 0, preview: 0 } });
    return { ...services, stop };
  }

  it("joins editors and reports presence on connect", async () => {
    const { stop } = connect();
    await settle();
    expect(api.backend.realtime.joins).toContain("editors");
    expect(api.backend.presence.size).toBe(1);
    stop();
  });

  it("reports presence again when the selection or workspace changes, debounced", async () => {
    const { stop, store } = connect();
    await settle();
    const mine = () => [...api.backend.presence.values()].find((p) => p.connectionId !== "mock-colleague");
    store.getState().select([IDS.invoice]);
    store.getState().setWorkspace("database");
    expect(mine()?.elementId ?? null).toBeNull();
    await new Promise((r) => setTimeout(r, 300));
    await settle();
    expect(mine()).toMatchObject({ elementId: IDS.invoice, workspace: "database" });
    stop();
  });

  it("patches the index from the change's summary (E5d) and invalidates the element on a change made elsewhere", async () => {
    const { queryClient, stop } = connect();
    await queryClient.fetchQuery({ queryKey: keys.index, queryFn: endpoints.getModelIndex });
    await queryClient.fetchQuery({ queryKey: keys.element(IDS.invoice), queryFn: () => endpoints.getElement(IDS.invoice) });
    api.backend.model.externalEdit(IDS.invoice, (json) => {
      json.name = "Bill";
    });
    await settle();
    expect(queryClient.getQueryState(keys.index)?.isInvalidated).toBe(false);
    const rows = queryClient.getQueryData<{ id: string; name: string }[]>(keys.index);
    expect(rows?.find((r) => r.id === IDS.invoice)?.name).toBe("Bill");
    expect(queryClient.getQueryState(keys.element(IDS.invoice))?.isInvalidated).toBe(true);
    stop();
  });

  it("invalidates the index when a change carries no summary (an older server)", async () => {
    const { queryClient, stop } = connect();
    await queryClient.fetchQuery({ queryKey: keys.index, queryFn: endpoints.getModelIndex });
    api.backend.realtime.publish("model.changed", {
      changed: [{ id: IDS.invoice, kind: "entity", path: ".maquettiste/model/entities/invoice.json", hash: "0".repeat(64) }],
      deleted: [],
      source: "disk",
      truncated: false,
      isEmpty: false,
    });
    await settle();
    expect(queryClient.getQueryState(keys.index)?.isInvalidated).toBe(true);
    stop();
  });

  it("cuts a large model.changed to 200 KB with truncated set, and the editor refetches the index", async () => {
    const { queryClient, stop } = connect();
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
    // The invalidation follows the event through the realtime channel; under a loaded test run it can take longer
    // than one settle, so poll for it rather than pause once.
    await vi.waitFor(() => expect(queryClient.getQueryState(keys.index)?.isInvalidated).toBe(true), { timeout: 2000 });
    stop();
  });

  it("ignores the echo of this window's own save", async () => {
    const { queryClient, drafts, stop } = connect();
    await queryClient.fetchQuery({ queryKey: keys.index, queryFn: endpoints.getModelIndex });
    queryClient.setQueryData(keys.element(IDS.invoice), await endpoints.getElement(IDS.invoice));
    drafts.edit(IDS.invoice, (json) => {
      (json as { name: string }).name = "Bill";
    });
    await drafts.flush(IDS.invoice);
    await settle();
    expect(queryClient.getQueryState(keys.index)?.isInvalidated).toBe(false);
    const row = queryClient.getQueryData<{ id: string; name: string }[]>(keys.index)!.find((r) => r.id === IDS.invoice);
    expect(row?.name).toBe("Bill");
    stop();
  });

  it("keeps presence from presence.changed in the store", async () => {
    const { store, stop } = connect();
    await settle();
    expect(store.getState().presence.length).toBeGreaterThan(0);
    stop();
  });
  it("reloads on site.deployed, or shows the new-version banner while a draft is unsaved", async () => {
    const services = api.services;
    let reloads = 0;
    const stop = connectRealtime({ ...services, reload: () => reloads++, delays: { index: 0, preview: 0 } });
    await settle();
    const deployed = { release: "r2", functions: false, source: "deploy" } as never;
    api.backend.realtime.publish("site.deployed", deployed);
    await settle();
    expect(reloads).toBe(1);
    expect(services.store.getState().banner).toBeNull();

    await services.queryClient.fetchQuery({ queryKey: keys.element(IDS.invoice), queryFn: () => endpoints.getElement(IDS.invoice) });
    services.drafts.edit(IDS.invoice, (json) => {
      (json as { name: string }).name = "Bill";
    });
    api.backend.realtime.publish("site.deployed", deployed);
    await settle();
    expect(reloads).toBe(1);
    expect(services.store.getState().banner?.kind).toBe("deployed");
    stop();
  });
});
