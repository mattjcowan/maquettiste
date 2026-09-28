// Startup: in mock mode MSW answers /api/ and MockRealtime stands in for the host's hub; otherwise
// the host's realtime client is loaded from /_host/site.js (never requested in mock mode).
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "@fontsource-variable/inter";
import "@fontsource-variable/jetbrains-mono";
import "./index.css";
import { App } from "./app/App";
import { createServices } from "./app/context";
import type { RealtimeClient } from "./realtime/events";
import { connectRealtime } from "./realtime/sync";

async function startRealtime(): Promise<RealtimeClient> {
  if (__MQ_MOCK__) {
    const { startMockBrowser } = await import("./mocks/browser");
    const backend = await startMockBrowser();
    return backend.realtime;
  }
  const { HostRealtime } = await import("./realtime/host");
  return new HostRealtime();
}

async function boot(): Promise<void> {
  const realtime = await startRealtime();
  const services = createServices(realtime, __MQ_MOCK__);
  connectRealtime({ ...services });
  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <App services={services} />
    </StrictMode>,
  );
}

void boot();
