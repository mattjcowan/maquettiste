// A mock backend behind msw/node plus the app's services wired to it, for integration tests.
import { afterAll, afterEach, beforeAll } from "vitest";
import { createApiClient, setApiClient } from "@/api/client";
import { createServices, type AppServices } from "@/app/context";
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
  afterEach(() => {
    mock.server.close();
    mock = startMockServer(options);
    mock.server.listen({ onUnhandledRequest: "error" });
    services = null;
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
