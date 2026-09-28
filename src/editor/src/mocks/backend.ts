// The mock backend: MockModel, generation and the job queue wired to MockRealtime the way the
// functions wire the engine to IRealtime (phase2-design.md 3.6): every write publishes
// model.changed, a settings change publishes project.changed, and after a quiet period a
// whole-model validation.completed follows.
import type { PresenceEntry } from "@/api/types";
import { newId as randomId } from "@/lib/ids";
import { MockRealtime } from "@/realtime/mock";
import { MockModel } from "./model/store";
import { MockGeneration } from "./model/generation";
import { MockJobQueue, type JobClock } from "./model/jobs";
import { billingSeed, emptySeed, largeSeed } from "./model/seed";

export type Scenario = "conflict" | "slow" | "empty" | "large" | "unauthenticated" | "presence" | "invalid";

export interface MockBackendOptions {
  scenarios?: Scenario[];
  realtime?: MockRealtime;
  newId?: () => string;
  clock?: JobClock;
  /** Quiet period before validation.completed (the functions use 750 ms). */
  validationDelayMs?: number;
}

export class MockBackend {
  readonly realtime: MockRealtime;
  readonly model: MockModel;
  readonly generation: MockGeneration;
  readonly jobs: MockJobQueue;
  readonly scenarios: Set<Scenario>;
  readonly presence = new Map<string, PresenceEntry>();
  private validationTimer: unknown = null;
  private readonly clock: JobClock;
  private readonly validationDelay: number;
  /** One-shot: the next element save meets a disk edit first (scenario `conflict`). */
  conflictPending: boolean;

  constructor(options: MockBackendOptions = {}) {
    this.scenarios = new Set(options.scenarios ?? []);
    this.realtime = options.realtime ?? new MockRealtime();
    const newId = options.newId ?? randomId;
    this.clock = options.clock ?? { now: () => new Date(), setTimeout: (fn, ms) => setTimeout(fn, ms) };
    this.validationDelay = options.validationDelayMs ?? 150;
    const seed = this.scenarios.has("empty") ? emptySeed() : this.scenarios.has("large") ? largeSeed() : billingSeed();
    this.model = new MockModel(seed, {
      newId,
      onChanged: (changes) => {
        this.realtime.publish("model.changed", changes);
        this.scheduleValidation();
      },
      onSettingsChanged: (hash) => {
        this.realtime.publish("project.changed", { settingsHash: hash });
        this.scheduleValidation();
      },
    });
    this.generation = new MockGeneration(this.model, newId);
    this.jobs = new MockJobQueue(
      this.generation,
      this.clock,
      {
        progress: (job) => this.realtime.publish("job.progress", job, `job:${job.id}`),
        completed: (summary) => this.realtime.publish("job.completed", summary, `job:${summary.id}`),
      },
      newId,
      () => this.model.entries.size,
    );
    if (this.scenarios.has("slow")) this.jobs.stepMs = 400;
    this.conflictPending = this.scenarios.has("conflict");
    if (this.scenarios.has("presence")) {
      this.presence.set("mock-colleague", {
        connectionId: "mock-colleague",
        user: "alex",
        elementId: "01J92P0V0FJ23CGSNKM7P1W5V7",
        workspace: "entities",
        updatedUtc: this.clock.now().toISOString(),
      });
    }
  }

  scheduleValidation(): void {
    if (this.validationTimer !== null) clearTimeout(this.validationTimer as ReturnType<typeof setTimeout>);
    this.validationTimer = this.clock.setTimeout(() => {
      this.validationTimer = null;
      this.realtime.publish("validation.completed", this.model.validate());
    }, this.validationDelay);
  }

  /** Stores one window's selection and publishes presence.changed (PUT /api/presence). */
  reportPresence(connectionId: string, elementId: string | null, workspace: PresenceEntry["workspace"]): boolean {
    if (connectionId !== this.realtime.connectionId) return false;
    this.presence.set(connectionId, { connectionId, user: "local", elementId, workspace, updatedUtc: this.clock.now().toISOString() });
    this.realtime.publish("presence.changed", { editors: [...this.presence.values()] }, "editors");
    return true;
  }

  /** Response delay for `?mock=slow`. */
  get latencyMs(): number {
    return this.scenarios.has("slow") ? 400 : 0;
  }
}

export function scenariosFrom(search: string): Scenario[] {
  const params = new URLSearchParams(search);
  const all = params.getAll("mock").flatMap((v) => v.split(","));
  const known: Scenario[] = ["conflict", "slow", "empty", "large", "unauthenticated", "presence", "invalid"];
  return all.filter((v): v is Scenario => (known as string[]).includes(v));
}
