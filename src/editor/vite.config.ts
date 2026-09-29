import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import fs from "node:fs";
import path from "node:path";
import { liveProxy } from "./scripts/live-proxy.mjs";

const workerSource = path.resolve(import.meta.dirname, "node_modules/msw/lib/mockServiceWorker.js");

/** COOP and COEP: a cross-origin isolated page (crossOriginIsolated === true). */
const ISOLATION_HEADERS = {
  "Cross-Origin-Opener-Policy": "same-origin",
  "Cross-Origin-Embedder-Policy": "require-corp",
} as const;

/**
 * Serves MSW's service worker in mock mode (dev) and emits it into mock builds, straight from the
 * installed msw package, so the worker always matches the library and never ships in a real build.
 */
function mockServiceWorker(enabled: boolean): Plugin {
  return {
    name: "maquettiste-mock-service-worker",
    configureServer(server) {
      if (!enabled) return;
      server.middlewares.use("/mockServiceWorker.js", (_req, res) => {
        res.setHeader("Content-Type", "text/javascript");
        res.setHeader("Service-Worker-Allowed", "/");
        res.end(fs.readFileSync(workerSource));
      });
    },
    configurePreviewServer(server) {
      if (!enabled) return;
      server.middlewares.use("/mockServiceWorker.js", (_req, res) => {
        res.setHeader("Content-Type", "text/javascript");
        for (const [name, value] of Object.entries(ISOLATION_HEADERS)) res.setHeader(name, value);
        res.end(fs.readFileSync(workerSource));
      });
    },
    generateBundle() {
      if (!enabled) return;
      this.emitFile({ type: "asset", fileName: "mockServiceWorker.js", source: fs.readFileSync(workerSource) });
    },
  };
}

export default defineConfig(({ mode }) => {
  const mock = mode === "mock" || mode === "test";
  return {
    plugins: [react(), tailwindcss(), mockServiceWorker(mock)],
    resolve: { alias: { "@": path.resolve(import.meta.dirname, "src") } },
    define: {
      __MQ_MOCK__: JSON.stringify(mock),
    },
    worker: { format: "es" },
    server: {
      port: 5173,
      proxy: mode === "live" ? liveProxy() : undefined,
    },
    // Cross-origin isolation on the preview (the Playwright mock and scale projects), so the scale
    // project can read performance.measureUserAgentSpecificMemory() (explorer-redesign.md 4.5).
    preview: { port: 4173, headers: ISOLATION_HEADERS },
    build: {
      target: "es2022",
      sourcemap: false,
      chunkSizeWarningLimit: 6000,
    },
  };
});
