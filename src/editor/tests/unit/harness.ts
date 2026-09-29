// A mock backend behind msw/node plus the app's services wired to it, for integration tests.
import { afterAll, afterEach, beforeAll, beforeEach } from "vitest";
import { createApiClient, setApiClient } from "@/api/client";
import { createServices, type AppServices } from "@/app/context";
import { resetLoaders } from "@/api/queries";
import type { MockBackendOptions } from "@/mocks/backend";
import { NODE_BASE_URL, startMockServer } from "@/mocks/node";

export const IDS = {
  invoice: "01J92P0V0FJ23CGSNKM7P1W5V7",
  payment: "01J92P0V0HEGSC6MW92CST5KA6",
  overview: "01J92P0V2164SDBW687ZV6E1MV",
};

/** Registers lifecycle hooks; `current()` returns a fresh backend and services for each test. */
export function useMockApi(options: MockBackendOptions = {}) {
  let mock = startMockServer(options);
  let services: AppServices | null = null;
  beforeAll(() => {
    mock.server.listen({ onUnhandledRequest: "error" });
    setApiClient(createApiClient(NODE_BASE_URL));
  });
  // Each test starts with fresh services: a page state (state/pageState.ts) a previous test's shell saved on its way
  // out must not be restored into it.
  beforeEach(() => {
    for (const key of Object.keys(localStorage)) if (key.startsWith("mq.page.")) localStorage.removeItem(key);
  });
  afterEach(() => {
    mock.server.close();
    mock = startMockServer(options);
    mock.server.listen({ onUnhandledRequest: "error" });
    services = null;
    resetLoaders({ indexStore: false });
  });
  afterAll(() => mock.server.close());
  return {
    get backend() {
      return mock.backend;
    },
    get server() {
      return mock.server;
    },
    get services(): AppServices {
      services ??= createServices(mock.backend.realtime, true);
      return services;
    },
    url: (path: string) => `${NODE_BASE_URL}${path}`,
  };
}
