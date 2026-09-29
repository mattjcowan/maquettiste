// Mock mode in the browser (`npm run dev`, `build:mock`): MSW's service worker answers /api/ from
// MockBackend, and the SPA uses the backend's MockRealtime instead of the host's hub.
// `?mock=large` first loads the generated 5,000-entity seed (model/largeSeed.ts); the mock's own
// costs (seed load, backend start, each handled request) are exposed as window.__mqPerf.mock so the
// scale tests can tell the mock's time from the editor's (explorer-redesign.md 4.5).
import { setupWorker } from "msw/browser";
import { MockBackend, scenariosFrom, type Scenario } from "./backend";
import { mockHandlers } from "./handlers";
import { LARGE_SEED_SCRIPT, loadLargeSeed, type SeedLoadTimings } from "./model/largeSeed";
import type { Seed } from "./model/store";

/** One handled request: method, path, status and the time from request to mocked response on this page. */
export interface MockRequestTiming {
  method: string;
  path: string;
  status: number;
  ms: number;
}

/** window.__mqPerf.mock. */
export interface MockPerf {
  scenario: "large" | "medium" | "billing" | "empty";
  elements: number;
  seed: SeedLoadTimings | null;
  backendMs: number;
  requests: MockRequestTiming[];
}

const MAX_TIMINGS = 2000;

export async function startMockBrowser(): Promise<MockBackend> {
  const scenarios = scenariosFrom(window.location.search);
  let seed: Seed | undefined;
  let seedTimings: SeedLoadTimings | null = null;
  let active: Scenario[] = scenarios;
  if (scenarios.includes("large")) {
    const loaded = await loadLargeSeed().catch((error: unknown) => {
      console.error(error);
      return null;
    });
    if (loaded) {
      seed = loaded.seed;
      seedTimings = loaded.timings;
    } else {
      // Without the generated file, fall back to the in-browser medium model and say how to get it.
      active = [...scenarios.filter((s) => s !== "large"), "medium"];
      showBanner(`The large mock model has not been generated. Run ${LARGE_SEED_SCRIPT}, then reload. Showing ?mock=medium instead.`);
    }
  }

  const started = performance.now();
  const backend = new MockBackend({ scenarios: active, seed });
  const backendMs = performance.now() - started;
  if (backend.scenarios.has("invalid")) seedInvalid(backend);

  const perf: MockPerf = {
    scenario: seed ? "large" : backend.scenarios.has("medium") ? "medium" : backend.scenarios.has("empty") ? "empty" : "billing",
    elements: backend.model.entries.size,
    seed: seedTimings,
    backendMs,
    requests: [],
  };
  const holder = window as unknown as { __mqPerf?: Record<string, unknown> };
  holder.__mqPerf ??= {};
  holder.__mqPerf.mock = perf;

  const worker = setupWorker(...mockHandlers(backend));
  const pending = new Map<string, number>();
  worker.events.on("request:start", ({ requestId }) => pending.set(requestId, performance.now()));
  worker.events.on("response:mocked", ({ request, requestId, response }) => {
    const start = pending.get(requestId);
    pending.delete(requestId);
    if (start === undefined || perf.requests.length >= MAX_TIMINGS) return;
    perf.requests.push({ method: request.method, path: new URL(request.url).pathname, status: response.status, ms: performance.now() - start });
  });
  await worker.start({
    onUnhandledRequest: (request, print) => {
      if (new URL(request.url).pathname.startsWith("/api/")) print.warning();
    },
    quiet: true,
  });
  (window as unknown as { __mqMock?: MockBackend }).__mqMock = backend;
  return backend;
}

/** A plain banner above the app (the mock layer has no access to the SPA's components). */
function showBanner(text: string): void {
  const banner = document.createElement("div");
  banner.setAttribute("role", "alert");
  banner.dataset.testid = "mock-large-missing";
  banner.textContent = text;
  banner.style.cssText =
    "position:fixed;top:0;left:0;right:0;z-index:1000;padding:6px 12px;font:13px system-ui,sans-serif;background:var(--mq-status-warning);color:var(--mq-bg-app);";
  document.body.prepend(banner);
}

/** Scenario `invalid`: Payment loses its key, so the problems panel starts with an error. */
export function seedInvalid(backend: MockBackend): void {
  backend.model.externalEdit("01J92P0V0HEGSC6MW92CST5KA6", (json) => {
    delete json.key;
  });
}
