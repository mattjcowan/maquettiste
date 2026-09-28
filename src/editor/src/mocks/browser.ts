// Mock mode in the browser (`npm run dev`, `build:mock`): MSW's service worker answers /api/ from
// MockBackend, and the SPA uses the backend's MockRealtime instead of the host's hub.
import { setupWorker } from "msw/browser";
import { MockBackend, scenariosFrom } from "./backend";
import { mockHandlers } from "./handlers";

export async function startMockBrowser(): Promise<MockBackend> {
  const backend = new MockBackend({ scenarios: scenariosFrom(window.location.search) });
  if (backend.scenarios.has("invalid")) seedInvalid(backend);
  const worker = setupWorker(...mockHandlers(backend));
  await worker.start({
    onUnhandledRequest: (request, print) => {
      if (new URL(request.url).pathname.startsWith("/api/")) print.warning();
    },
    quiet: true,
  });
  (window as unknown as { __mqMock?: MockBackend }).__mqMock = backend;
  return backend;
}

/** Scenario `invalid`: Payment loses its key, so the problems panel starts with an error. */
export function seedInvalid(backend: MockBackend): void {
  backend.model.externalEdit("01J92P0V0HEGSC6MW92CST5KA6", (json) => {
    delete json.key;
  });
}
