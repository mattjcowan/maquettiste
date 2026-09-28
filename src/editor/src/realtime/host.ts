// HostRealtime: site.realtime from the host's /_host/site.js (static-site-hosting 0.2.0). The
// script is injected at startup rather than listed in index.html, so mock mode never requests it.
import type { EventDetails, Handler, RealtimeClient, RealtimeEventName, RealtimeState } from "./events";

interface SiteRealtime {
  readonly state: RealtimeState;
  readonly connectionId: string | null;
  connect(): Promise<void>;
  on(name: string, handler: (payload: unknown, details: EventDetails) => void): () => void;
  off(name: string, handler?: (payload: unknown, details: EventDetails) => void): void;
  join(group: string): Promise<void>;
  leave(group: string): Promise<void>;
  onStateChange(handler: (state: RealtimeState, previous: RealtimeState) => void): () => void;
}

declare global {
  interface Window {
    site?: { realtime: SiteRealtime };
  }
}

export function loadSiteScript(src = "/_host/site.js"): Promise<SiteRealtime> {
  if (window.site?.realtime) return Promise.resolve(window.site.realtime);
  return new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = src;
    script.async = true;
    script.onload = () => (window.site?.realtime ? resolve(window.site.realtime) : reject(new Error(`${src} did not define site.realtime.`)));
    script.onerror = () => reject(new Error(`Could not load ${src}.`));
    document.head.appendChild(script);
  });
}

export class HostRealtime implements RealtimeClient {
  private site: SiteRealtime | null = null;
  private readonly ready: Promise<SiteRealtime>;
  private pending: (() => void)[] = [];
  private stateHandlers: ((state: RealtimeState, previous: RealtimeState) => void)[] = [];
  private failed = false;

  constructor(load: () => Promise<SiteRealtime> = () => loadSiteScript()) {
    this.ready = load().then(
      (site) => {
        this.site = site;
        site.onStateChange((state, previous) => {
          for (const handler of [...this.stateHandlers]) handler(state, previous);
        });
        for (const run of this.pending) run();
        this.pending = [];
        return site;
      },
      (error: unknown) => {
        this.failed = true;
        console.error("realtime:", error);
        throw error;
      },
    );
    this.ready.catch(() => undefined);
  }

  get state(): RealtimeState {
    return this.site?.state ?? "disconnected";
  }

  get connectionId(): string | null {
    return this.site?.connectionId ?? null;
  }

  get unavailable(): boolean {
    return this.failed;
  }

  on<E extends RealtimeEventName>(event: E, handler: Handler<E>): () => void {
    let stop: (() => void) | null = null;
    let cancelled = false;
    const attach = () => {
      if (!cancelled && this.site) stop = this.site.on(event, handler as (p: unknown, d: EventDetails) => void);
    };
    if (this.site) attach();
    else this.pending.push(attach);
    return () => {
      cancelled = true;
      stop?.();
    };
  }

  off<E extends RealtimeEventName>(event: E, handler?: Handler<E>): void {
    this.site?.off(event, handler as ((p: unknown, d: EventDetails) => void) | undefined);
  }

  async connect(): Promise<void> {
    const site = await this.ready;
    await site.connect();
  }

  async join(group: string): Promise<void> {
    const site = await this.ready;
    await site.join(group);
  }

  async leave(group: string): Promise<void> {
    const site = await this.ready;
    await site.leave(group);
  }

  onStateChange(handler: (state: RealtimeState, previous: RealtimeState) => void): () => void {
    this.stateHandlers.push(handler);
    return () => {
      this.stateHandlers = this.stateHandlers.filter((h) => h !== handler);
    };
  }
}
