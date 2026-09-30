// The mock backend: MockModel, generation and the job queue wired to MockRealtime the way the
// functions wire the engine to IRealtime (phase2-design.md 3.6): every write publishes
// model.changed, a settings change publishes project.changed, and after a quiet period a
// whole-model validation.completed follows.
import { MockPackAuthoring } from "./model/packAuthoring";
import type { PresenceEntry } from "@/api/types";
import { newId as randomId } from "@/lib/ids";
import { MockRealtime } from "@/realtime/mock";
import { MockModel } from "./model/store";
import { MockGeneration } from "./model/generation";
import { MockPacks } from "./model/packs";
import { MockJobQueue, type JobClock } from "./model/jobs";
import { MockLocalization } from "./model/localization";
import type { Seed } from "./model/store";
import { billingSeed, emptySeed, mediumSeed } from "./model/seed";
import { withDrift, withLifecycleProblems } from "./model/processSeed";
import { withLargeChart } from "./model/processDiagramSeed";
import { truncateChangeEvent } from "./wire";

/**
 * `?mock=` scenarios. `medium` is the in-browser 200-entity model; `large` is the 5,000-entity model
 * that scripts/gen-scale-model.mjs writes (browser.ts loads it and passes it as `seed`).
 */
export type Scenario =
  "conflict" | "slow" | "empty" | "medium" | "wide" | "large" | "unauthenticated" | "presence" | "invalid" | "locales" | "drift" | "lifecycle" | "chart400";

export interface MockBackendOptions {
  scenarios?: Scenario[];
  realtime?: MockRealtime;
  newId?: () => string;
  clock?: JobClock;
  /** Quiet period before validation.completed (the functions use 750 ms). */
  validationDelayMs?: number;
  /** The model to start from; when absent the scenario picks one (empty, medium or the billing fixture). */
  seed?: Seed;
}

export class MockBackend {
  readonly realtime: MockRealtime;
  readonly model: MockModel;
  readonly generation: MockGeneration;
  /** Pack folders: pack.json, templates and partials with hashes (generation-ui.md section 5.1). */
  readonly packs: MockPacks;
  /** Unit paths, template context, explain, pack outputs and the pack settings save (generation-ui.md sections 4.3 and 5.1). */
  readonly packAuthoring: MockPackAuthoring;
  readonly jobs: MockJobQueue;
  /** Translations, seed CSV and reference type usage; locales are declared with the `locales` scenario. */
  readonly localization: MockLocalization;
  readonly scenarios: Set<Scenario>;
  /** Whether the model came from `options.seed` (the large mock), so no engine recording applies. */
  readonly seeded: boolean;
  readonly presence = new Map<string, PresenceEntry>();
  private validationTimer: unknown = null;
  private readonly clock: JobClock;
  private readonly validationDelay: number;
  /** One-shot: the next element save meets a disk edit first (scenario `conflict`). */
  conflictPending: boolean;

  constructor(options: MockBackendOptions = {}) {
    this.scenarios = new Set(options.scenarios ?? []);
    this.seeded = options.seed !== undefined;
    this.realtime = options.realtime ?? new MockRealtime();
    const newId = options.newId ?? randomId;
    this.clock = options.clock ?? { now: () => new Date(), setTimeout: (fn, ms) => setTimeout(fn, ms) };
    this.validationDelay = options.validationDelayMs ?? 150;
    const seed =
      options.seed ??
      (this.scenarios.has("empty") ? emptySeed() : this.scenarios.has("medium") ? mediumSeed() : this.scenarios.has("wide") ? mediumSeed(400) : billingSeed());
    const lifecycle = this.scenarios.has("lifecycle") ? withLifecycleProblems(seed) : seed;
    const drifted = this.scenarios.has("drift") ? withDrift(lifecycle) : lifecycle;
    // `chart400`: a 400-state process with its diagram, for the statechart canvas's budgets (phase-3-design.md 4.5).
    const charted = this.scenarios.has("chart400") ? withLargeChart(drifted) : drifted;
    this.model = new MockModel(this.scenarios.has("locales") ? withLocales(charted) : charted, {
      newId,
      onChanged: (changes) => {
        // E5d: each change carries the element's index row as it is now.
        const changed = changes.changed.map((c) => {
          const summary = this.model.summaryOf(c.id);
          return summary ? { ...c, summary } : c;
        });
        // The host cuts the event to 200 KB and sets truncated (EditorEvents.OnModelChangedAsync).
        const event = truncateChangeEvent({ ...changes, changed });
        this.realtime.publish("model.changed", { ...event, isEmpty: event.changed.length === 0 && event.deleted.length === 0 });
        this.scheduleValidation();
      },
      onSettingsChanged: (hash) => {
        this.realtime.publish("project.changed", { settingsHash: hash });
        this.scheduleValidation();
      },
    });
    this.generation = new MockGeneration(this.model, newId);
    this.packs = new MockPacks(this.model);
    this.packAuthoring = new MockPackAuthoring(this.model, this.generation, (pack) => this.packs.registrations(pack));
    this.localization = new MockLocalization(this.model, this.scenarios.has("locales"), newId);
    this.model.modelDiagnostics = (rules) => this.localization.diagnostics(rules);
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

  /** Publishes model.changed for a translation write: no element changed, the display names of every affected locale. */
  publishTranslations(locales: string[]): void {
    if (locales.length === 0) return;
    const translations = this.localization.translationsEvent(locales);
    this.realtime.publish("model.changed", { changed: [], deleted: [], source: "editor", truncated: false, isEmpty: false, translations });
    this.scheduleValidation();
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

/** The `locales` scenario's settings: en (default), fr and fr-CA falling back to fr (RT 3.2). */
export const MOCK_LOCALIZATION = { defaultLocale: "en", locales: ["en", "fr", "fr-CA"], fallbacks: { "fr-CA": ["fr"] } };

function withLocales(seed: Seed): Seed {
  const files = seed.files.map((f) => {
    if (f.path !== "maquettiste.json") return f;
    const json = JSON.parse(f.text) as Record<string, unknown>;
    return { ...f, text: JSON.stringify({ ...json, localization: MOCK_LOCALIZATION }, null, 2) + "\n" };
  });
  if (!files.some((f) => f.path === "maquettiste.json"))
    files.push({ path: "maquettiste.json", text: JSON.stringify({ formatVersion: 1, localization: MOCK_LOCALIZATION }) } as (typeof files)[number]);
  return { ...seed, files };
}

export function scenariosFrom(search: string): Scenario[] {
  const params = new URLSearchParams(search);
  const all = params.getAll("mock").flatMap((v) => v.split(","));
  const known: Scenario[] = [
    "conflict",
    "slow",
    "empty",
    "medium",
    "wide",
    "large",
    "unauthenticated",
    "presence",
    "invalid",
    "locales",
    "drift",
    "lifecycle",
    "chart400",
  ];
  return all.filter((v): v is Scenario => (known as string[]).includes(v));
}
