// A queue request the Locales screen does not take promptly is dropped, so it never fires on a later unrelated visit.
import { afterEach, describe, expect, it, vi } from "vitest";
import { QUEUE_REQUEST_TTL_MS, peekQueueRequest, requestQueue, takeQueueRequest } from "@/l10n/queueTarget";

afterEach(() => {
  vi.useRealTimers();
  takeQueueRequest();
});

describe("queue requests", () => {
  it("is taken once while fresh", () => {
    requestQueue({ locale: "fr", shard: "a.json" });
    expect(peekQueueRequest()).toEqual({ locale: "fr", shard: "a.json" });
    expect(takeQueueRequest()).toEqual({ locale: "fr", shard: "a.json" });
    expect(takeQueueRequest()).toBeNull();
  });
  it("is dropped once it is older than the time to live", () => {
    vi.useFakeTimers();
    requestQueue({ locale: "fr", shard: "a.json" });
    vi.advanceTimersByTime(QUEUE_REQUEST_TTL_MS + 1);
    expect(peekQueueRequest()).toBeNull();
    expect(takeQueueRequest()).toBeNull();
  });
});
