// Playwright (phase2-design.md §4.10). Projects:
// - mock: the static mock build served by `vite preview` (CI on every push). Specs in tests/e2e/*.spec.ts.
// - mock-budgets: the mock project's teardown (it runs with `--project=mock`, after the other specs, alone): the
//   statechart canvas's budgets (phase-3-design.md 4.5), timed without parallel specs competing for the machine.
// - dev: starts the Vite dev server itself (only dev enforces server.fs.allow) and runs the smoke spec.
// - scale: ?mock=large (the 5,000-entity model from `npm run gen:scale`) against the same mock
//   preview, measuring the explorer-redesign.md 4.5 targets (`npm run e2e:scale`).
// - live: the same mock-free specs against a running image (MAQUETTISTE_URL, default
//   http://maquettiste.localhost:8080); no web server is started.
// - docs: the documentation's screenshots (tests/docs, `npm run docs:screenshots`) against the image over the showcase model;
//   never part of a test run.
import { defineConfig, devices } from "@playwright/test";

const ci = !!process.env.CI;
const live = process.env.MAQUETTISTE_URL ?? "http://maquettiste.localhost:8080";
// MOCK_PORT lets two checkouts run the mock project at the same time (default 4173, as vite.config.ts preview.port).
const mockPort = process.env.MOCK_PORT ?? "4173";
const mockUrl = `http://localhost:${mockPort}`;
// Start only the servers the selected projects need (`--project=live` starts none).
const projects = process.argv.flatMap((a, i, all) => (a.startsWith("--project=") ? [a.slice(10)] : a === "--project" ? [all[i + 1] ?? ""] : []));
const wants = (name: string) => projects.length === 0 || projects.includes(name);

export default defineConfig({
  testDir: "tests/e2e",
  timeout: 30_000,
  expect: { timeout: 10_000 },
  fullyParallel: true,
  forbidOnly: ci,
  retries: ci ? 1 : 0,
  workers: ci ? 2 : undefined,
  reporter: ci ? [["list"], ["html", { open: "never" }]] : [["list"]],
  use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 }, trace: "retain-on-failure" },
  projects: [
    // The statechart budgets run after the rest of the mock project, alone, so parallel specs do not skew their timings.
    { name: "mock", testIgnore: /dev-smoke|scale|budget/, use: { baseURL: mockUrl }, teardown: "mock-budgets" },
    { name: "mock-budgets", testMatch: /budget\.mock-only\.spec\.ts/, fullyParallel: false, use: { baseURL: mockUrl } },
    { name: "scale", testMatch: /scale\.spec\.ts/, retries: 0, use: { baseURL: mockUrl } },
    { name: "dev", testMatch: /dev-smoke\.spec\.ts/, use: { baseURL: "http://localhost:5173" } },
    { name: "live", testIgnore: /dev-smoke|mock-only|scale/, use: { baseURL: live } },
    {
      name: "docs",
      testDir: "tests/docs",
      retries: 0,
      fullyParallel: false,
      use: { baseURL: live, viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 },
    },
  ],
  webServer: [
    ...(wants("mock") || wants("mock-budgets") || wants("scale")
      ? [
          {
            command: `npm run build:mock && npm run preview:mock -- --port ${mockPort}`,
            url: mockUrl,
            reuseExistingServer: !ci,
            timeout: 180_000,
          },
        ]
      : []),
    ...(wants("dev")
      ? [{ command: "npx vite --mode mock --port 5173 --strictPort", url: "http://localhost:5173", reuseExistingServer: !ci, timeout: 120_000 }]
      : []),
  ],
});
