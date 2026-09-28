// Mock mode under Vitest: the same handlers through msw/node.
import { setupServer } from "msw/node";
import { MockBackend, type MockBackendOptions } from "./backend";
import { mockHandlers } from "./handlers";

export const NODE_BASE_URL = "http://localhost";

export function startMockServer(options: MockBackendOptions = {}) {
  const backend = new MockBackend(options);
  const server = setupServer(...mockHandlers(backend, NODE_BASE_URL));
  return { backend, server };
}
