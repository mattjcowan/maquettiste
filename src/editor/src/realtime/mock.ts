// MockRealtime: an in-memory bus with the RealtimeClient interface. The mock API handlers publish
// to it the way the functions publish through IRealtime; events sent to a group reach this client
// only while it has joined that group, as on the host's hub.
import type { EventDetails, Handler, RealtimeClient, RealtimeEventMap, RealtimeEventName, RealtimeState } from "./events";

type AnyHandler = (payload: unknown, details: EventDetails) => void;

export class MockRealtime implements RealtimeClient {
  private listeners = new Map<string, AnyHandler[]>();
  private stateHandlers: ((state: RealtimeState, previous: RealtimeState) => void)[] = [];
  private currentState: RealtimeState = "disconnected";
  private connection = 0;
  readonly groups = new Set<string>();
  /** Every event published, for tests and the Output panel's debug view. */
  readonly published: { event: string; group: string | null; payload: unknown }[] = [];
  /** Group joins in order, for tests. */
  readonly joins: string[] = [];

  get state(): RealtimeState {
    return this.currentState;
  }

  get connectionId(): string | null {
    return this.currentState === "connected" ? `mock-connection-${this.connection}` : null;
  }

  on<E extends RealtimeEventName>(event: E, handler: Handler<E>): () => void {
    const list = this.listeners.get(event) ?? [];
    list.push(handler as AnyHandler);
    this.listeners.set(event, list);
    if (this.currentState === "disconnected") void this.connect();
    return () => this.off(event, handler);
  }

  off<E extends RealtimeEventName>(event: E, handler?: Handler<E>): void {
    const list = this.listeners.get(event);
    if (!list) return;
    if (!handler) list.length = 0;
    else {
      const at = list.indexOf(handler as AnyHandler);
      if (at >= 0) list.splice(at, 1);
    }
  }

  private connecting: Promise<void> | null = null;

  /** Idempotent: concurrent callers share one connection attempt (one connection id). */
  connect(): Promise<void> {
    if (this.currentState === "connected") return Promise.resolve();
    this.connecting ??= (async () => {
      this.setState("connecting");
      await Promise.resolve();
      this.connection++;
      this.connecting = null;
      this.setState("connected");
    })();
    return this.connecting;
  }

  async join(group: string): Promise<void> {
    if (!/^[A-Za-z0-9_.:-]{1,64}$/.test(group)) throw new Error(`"${group}" is not a group name.`);
    await this.connect();
    if (this.groups.size >= 100 && !this.groups.has(group)) throw new Error("A connection may join 100 groups.");
    this.groups.add(group);
    this.joins.push(group);
  }

  async leave(group: string): Promise<void> {
    this.groups.delete(group);
  }

  onStateChange(handler: (state: RealtimeState, previous: RealtimeState) => void): () => void {
    this.stateHandlers.push(handler);
    return () => {
      this.stateHandlers = this.stateHandlers.filter((h) => h !== handler);
    };
  }

  /**
   * Publishes an event, as the functions do: to every connection (group null) or to one group.
   * Delivery is asynchronous, like a message over the hub.
   */
  publish<E extends RealtimeEventName>(event: E, payload: RealtimeEventMap[E], group: string | null = null): void {
    this.published.push({ event, group, payload });
    setTimeout(() => {
      if (this.currentState !== "connected") return;
      if (group !== null && !this.groups.has(group)) return;
      for (const handler of [...(this.listeners.get(event) ?? [])]) handler(payload, { event, group });
    }, 0);
  }

  /** Drops and restores the connection, as a network blip would (groups are rejoined). */
  async simulateReconnect(): Promise<void> {
    this.setState("reconnecting");
    await new Promise((r) => setTimeout(r, 10));
    this.connection++;
    this.setState("connected");
  }

  private setState(next: RealtimeState): void {
    if (next === this.currentState) return;
    const previous = this.currentState;
    this.currentState = next;
    for (const handler of [...this.stateHandlers]) handler(next, previous);
  }
}
