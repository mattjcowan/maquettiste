// The mock backend: MockModel, generation and the job queue wired to MockRealtime the way the
// functions wire the engine to IRealtime (phase2-design.md 3.6): every write publishes
// model.changed, a settings change publishes project.changed, and after a quiet period a
// whole-model validation.completed follows.
import { MockPackAuthoring } from "./model/packAuthoring";
import { namesPack } from "@/workspaces/generate/packHints";
import type { PresenceEntry, components } from "@/api/types";
import { newId as randomId } from "@/lib/ids";
import { MockRealtime } from "@/realtime/mock";
import { MockModel, serialize } from "./model/store";
import { sha256Hex } from "@/lib/sha256";
import { MockGeneration } from "./model/generation";
import { MockPacks } from "./model/packs";
import { MockJobQueue, type JobClock } from "./model/jobs";
import { MockLocalization } from "./model/localization";
import { MockExtensions } from "./model/extensions";
import type { Seed } from "./model/store";
import { billingSeed, emptySeed, mediumSeed } from "./model/seed";
import { withDrift, withLifecycleProblems } from "./model/processSeed";
import { withLargeChart } from "./model/processDiagramSeed";
import { truncateChangeEvent } from "./wire";
import { MockAssist } from "./assist";

/**
 * `?mock=` scenarios. `medium` is the in-browser 200-entity model; `large` is the 5,000-entity model
 * that scripts/gen-scale-model.mjs writes (browser.ts loads it and passes it as `seed`).
 */
type PackRemoveResult = components["schemas"]["PackRemoveResult"];
type PackRenameResult = components["schemas"]["PackRenameResult"];
const PACK_NAME = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/;

export type Scenario =
  | "conflict"
  | "slow"
  | "empty"
  | "medium"
  | "wide"
  | "large"
  | "unauthenticated"
  | "presence"
  | "invalid"
  | "locales"
  | "drift"
  | "lifecycle"
  | "chart400"
  | "legacy"
  | "noai"
  | "role-viewer"
  | "role-editor"
  | "role-maintainer";

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
  /** The extensions folder: custom property schemas and script rules, whose findings join validation. */
  readonly extensions: MockExtensions;
  /** The assistant: status, conversations and the scripted chat; `noai` leaves the site's AI not configured. */
  readonly assist: MockAssist;
  readonly scenarios: Set<Scenario>;
  /** Whether the model came from `options.seed` (the large mock), so no engine recording applies. */
  readonly seeded: boolean;
  readonly presence = new Map<string, PresenceEntry>();
  private validationTimer: unknown = null;
  private readonly clock: JobClock;
  private readonly validationDelay: number;
  /** One-shot: the next element save meets a disk edit first (scenario `conflict`). */
  conflictPending: boolean;
  /**
   * The `elapsedMs` every template preview reports when set (tests: a slow render, which the editor does not repeat as the
   * template is typed; `window.__mqMock.previewElapsedMs = 5000`).
   */
  previewElapsedMs: number | null = null;
  /** A generation run holds the run lock: a restore answers 409 run-locked (`window.__mqMock.runLocked = true`). */
  runLocked = false;

  /** The signed-in role: `?mock=role-viewer`, `role-editor` or `role-maintainer`, else admin (the one role local mode has). */
  get role(): "viewer" | "editor" | "maintainer" | "admin" {
    if (this.scenarios.has("role-viewer")) return "viewer";
    if (this.scenarios.has("role-editor")) return "editor";
    if (this.scenarios.has("role-maintainer")) return "maintainer";
    return "admin";
  }

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
    // `legacy`: a project from before 0.5.5, whose maquettiste.json still sets the retired `commit` flag (MQ1010) and is not in
    // canonical form (MQ1003), with one element file hand-written out of canonical form too.
    if (this.scenarios.has("legacy")) withLegacyFiles(this.model);
    this.generation = new MockGeneration(this.model, newId);
    this.packs = new MockPacks(this.model);
    this.packAuthoring = new MockPackAuthoring(this.model, this.generation, (pack) => this.packs.registrations(pack));
    this.localization = new MockLocalization(this.model, this.scenarios.has("locales"), newId);
    this.extensions = new MockExtensions(this.model, seed.files);
    this.assist = new MockAssist(this.model, !this.scenarios.has("noai"));
    this.model.modelDiagnostics = (rules) => [...this.localization.diagnostics(rules), ...this.extensions.diagnostics()];
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

  /**
   * Removes a pack as GenerationService.DeletePackAsync does (DELETE /api/packs/{pack}): pack.json must still have
   * `expected`; the packs.<pack> settings entry goes first (a refused save removes nothing), then the folder, then the
   * manifest entries, whose files stay on disk untracked. Publishes templates.changed and packs.changed as the host does.
   */
  removePack(pack: string, expected: string): { status: 200 | 404 | 409 | 422; body: PackRemoveResult } {
    const result = (outcome: PackRemoveResult["outcome"], extra: Partial<PackRemoveResult> = {}): PackRemoveResult => ({
      outcome,
      hash: null,
      current: null,
      files: [],
      untracked: [],
      settingsHash: null,
      diagnostics: [],
      ...extra,
    });
    const refused = this.packs.removalRefusal(pack, expected);
    if (refused?.status === 404) return { status: 404, body: result("not-found") };
    if (refused) return { status: 409, body: result("conflict", { hash: refused.hash, current: refused.current }) };
    let settingsHash: string | null = null;
    const settings = this.model.settingsDocument();
    if ((settings.json as { packs?: Record<string, unknown> }).packs?.[pack] !== undefined) {
      const saved = this.packAuthoring.saveSettings(pack, {}, settings.hash);
      if (saved.body.outcome !== "saved") return { status: 422, body: result("invalid", { diagnostics: saved.body.diagnostics }) };
      settingsHash = saved.body.hash;
    }
    const files = this.packs.drop(pack);
    const untracked = [...this.generation.manifest].filter(([, entry]) => entry.pack === pack).map(([path]) => path);
    for (const path of untracked) this.generation.manifest.delete(path);
    this.realtime.publish("templates.changed", { pack, files: [] });
    this.realtime.publish("packs.changed", { packs: [pack] });
    return { status: 200, body: result("saved", { files, untracked: untracked.sort(), settingsHash }) };
  }

  /**
   * Renames a pack as GenerationService.RenamePackAsync does (POST /api/packs/{pack}/rename): pack.json must still have
   * `expected`, the new name must be a free pack name; the packs.<pack> settings entry moves first (a refused save renames
   * nothing), then the folder, then the manifest entries, so the files it generated stay tracked. Publishes
   * templates.changed for both names and packs.changed as the host does.
   */
  renamePack(pack: string, name: string, expected: string, dryRun = false): { status: 200 | 404 | 409 | 422; body: PackRenameResult } {
    const result = (outcome: PackRenameResult["outcome"], extra: Partial<PackRenameResult> = {}): PackRenameResult => ({
      outcome,
      from: pack,
      to: name,
      hash: null,
      current: null,
      settingsHash: null,
      files: [],
      tracked: [],
      hints: [],
      hintsUpdated: [],
      diagnostics: [],
      ...extra,
    });
    const refusedName = (message: string) => ({
      status: 422 as const,
      body: result("invalid", {
        diagnostics: [
          {
            rule: "MQ6001",
            severity: "error",
            message,
            elementId: null,
            filePath: `.maquettiste/templates/${name}/pack.json`,
            jsonPointer: "/name",
            line: null,
            column: null,
          },
        ],
      }),
    });
    const refused = this.packs.removalRefusal(pack, expected);
    if (refused?.status === 404) return { status: 404, body: result("not-found") };
    if (!PACK_NAME.test(name))
      return refusedName(`'${name}' is not a pack name: lowercase letters and digits separated by single hyphens, starting with a letter.`);
    if (name === pack) return refusedName(`The pack is already named '${pack}'.`);
    if (refused) return { status: 409, body: result("conflict", { hash: refused.hash, current: refused.current }) };
    if (this.packs.has(name)) return refusedName(`templates/${name}/ already exists; choose another name.`);
    const settings = this.model.settingsDocument();
    const json = structuredClone(settings.json) as { packs?: Record<string, unknown> };
    if (json.packs?.[name] !== undefined) return refusedName(`maquettiste.json already has a packs.${name} entry; remove it or choose another name.`);
    const hints = [...this.model.docs()]
      .filter(([, doc]) => namesPack(doc, pack))
      .map(([id]) => id)
      .sort();
    if (dryRun) {
      const files = this.packs.names().includes(pack) ? this.packs.fileNames(pack) : [];
      const tracked = [...this.generation.manifest].filter(([, entry]) => entry.pack === pack).map(([path]) => path);
      return { status: 200, body: result("saved", { files, tracked: tracked.sort(), hints }) };
    }
    let settingsHash: string | null = null;
    if (json.packs?.[pack] !== undefined) {
      const { [pack]: section, ...others } = json.packs;
      json.packs = { ...others, [name]: section };
      const saved = this.model.saveSettings(json as never, settings.hash);
      if (saved.body.outcome !== "saved") return { status: 422, body: result("invalid", { diagnostics: saved.body.diagnostics }) };
      settingsHash = saved.body.hash;
    }
    const { files, hash } = this.packs.rename(pack, name);
    const tracked = this.generation.renamePack(pack, name);
    this.realtime.publish("templates.changed", { pack: [pack, name].sort()[0], files: [] });
    this.realtime.publish("templates.changed", { pack: [pack, name].sort()[1], files: [] });
    this.realtime.publish("packs.changed", { packs: [pack, name].sort() });
    return { status: 200, body: result("saved", { hash, settingsHash, files, tracked, hints }) };
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
    "legacy",
    "noai",
    "role-viewer",
    "role-editor",
    "role-maintainer",
  ];
  return all.filter((v): v is Scenario => (known as string[]).includes(v));
}

/** The `legacy` scenario's files: the first outputs.allow entry sets `commit`, and the settings and the first entity are not canonical. */
function withLegacyFiles(model: MockModel): void {
  const allow = (model.settingsJson.outputs as { allow?: Record<string, unknown>[] } | undefined)?.allow;
  if (allow?.[0]) allow[0].commit = true;
  model.settingsHash = sha256Hex(serialize(model.settingsJson));
  model.nonCanonical.add(".maquettiste/maquettiste.json");
  const entity = [...model.entries.values()].filter((e) => e.json.kind === "entity").sort((a, b) => (a.path < b.path ? -1 : 1))[0];
  if (entity) model.nonCanonical.add(entity.path);
}
