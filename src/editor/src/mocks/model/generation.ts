// Mock generation: plans, per-file diffs, apply and previews over the mock model, with an
// in-memory "disk" of generated files. Plans compare planned bytes with that disk (added,
// modified, unchanged, deleted, kept companions, hand edits); apply re-plans, reports `stale`
// when the model or a planned path changed since planning, and otherwise writes the planned bytes.
import type {
  ApplyResult,
  DatabaseTablesResult,
  DatabaseView,
  DatabaseViewResult,
  Diagnostic,
  FileChange,
  GenerationPlan,
  GenerationRequest,
  GenerationResult,
  PlanResult,
  PlanUnit,
  PreviewResult,
  RenderedFile,
  RootSelection,
} from "@/api/types";
import type { components } from "@/api/schema";
import { sha256Hex } from "@/lib/sha256";
import { clone } from "@/lib/json";
import type { MockModel } from "./store";
import { resolveDatabase } from "./physical";
import { renderCSharp, renderSchema, renderTable, type RenderUnit } from "./render";
import { unifiedDiff } from "./diff";

type Json = Record<string, unknown>;

interface PlannedFile {
  path: string;
  text: string;
  pack: string;
  unitKey: string;
  role: RenderedFile["role"];
  mode: "overwrite" | "pair";
  root: { path: string; commit: boolean };
}

interface StoredPlan {
  plan: GenerationPlan;
  files: Map<string, PlannedFile>;
  diskAtPlan: Map<string, string | null>;
  modelVersion: number;
}

const TIMINGS = (items: number): GenerationResult["timings"] =>
  (["load", "validate", "resolve", "plan", "write"] as const).map((stage, i) => ({
    stage,
    wall: `00:00:00.00${String(10 + i * 7).padStart(2, "0")}000`,
    busy: `00:00:00.00${String(10 + i * 7).padStart(2, "0")}000`,
    items,
  }));

export class MockGeneration {
  /** Generated files on the mock disk, by repo path. */
  readonly disk = new Map<string, string>();
  /** What generation last wrote (path → hash), to tell hand edits and orphans apart. */
  readonly manifest = new Map<string, { hash: string; pack: string }>();
  private readonly plans = new Map<string, StoredPlan>();
  /** What the last apply rendered per unit key (the unit state store): its input hash and its element's hash. */
  private readonly unitState = new Map<string, { inputHash: string; elementHash: string | null }>();

  constructor(
    private readonly model: MockModel,
    private readonly newId: () => string,
  ) {}

  private packOutput(pack: string): { path: string; commit: boolean } {
    const settings = this.model.projectSettings();
    const output = settings.packs[pack]?.output || (pack === "sql-ddl" ? "db" : "src/Generated");
    const allow = settings.outputs.allow.find((a) => output === a.path || output.startsWith(a.path + "/"));
    return { path: output, commit: allow?.commit ?? false };
  }

  enabledPacks(): string[] {
    const settings = this.model.projectSettings();
    return this.model.packs.map((p) => p.name).filter((name) => settings.packs[name]?.enabled !== false);
  }

  databaseView(id: string): DatabaseViewResult | null {
    const docs = this.model.docs();
    const db = docs.get(id);
    if (!db || db.kind !== "database") return null;
    const errors = this.model.validate().diagnostics.filter((d) => d.severity === "error");
    if (errors.length) return { view: null, diagnostics: errors };
    const settings = this.model.projectSettings();
    const view = resolveDatabase({ docs, conventions: settings.conventions as Json, databaseConventions: settings.databases as Record<string, Json> }, id);
    return { view, diagnostics: [] };
  }

  private tablesCache: {
    version: number;
    results: Map<string, DatabaseTablesResult>;
    views: Map<string, Map<string, DatabaseView["tables"][number]>>;
  } | null = null;

  /**
   * E5c: the table list of one database without columns. Unlike databaseView it answers on a model
   * with errors: it resolves anyway and leaves out the tables whose owning elements have errors
   * (the entity, relation, table file, or the target of a broken mapping or overlay), with
   * partial: true. Answers are cached per model version.
   */
  databaseTables(id: string): DatabaseTablesResult | null {
    const docs = this.model.docs();
    const db = docs.get(id);
    if (!db || db.kind !== "database") return null;
    if (this.tablesCache?.version !== this.model.version) this.tablesCache = { version: this.model.version, results: new Map(), views: new Map() };
    const cached = this.tablesCache.results.get(id);
    if (cached) return cached;
    const diagnostics = this.model.validate().diagnostics;
    const errors = diagnostics.filter((d) => d.severity === "error");
    const failed = new Set<string>();
    for (const error of errors) {
      if (!error.elementId) continue;
      const owner = this.model.owner(error.elementId)?.id ?? error.elementId;
      failed.add(owner);
      const doc = docs.get(owner);
      if (doc && (doc.kind === "mapping" || doc.kind === "table"))
        for (const key of ["entity", "relation", "table"]) if (typeof doc[key] === "string") failed.add(doc[key] as string);
    }
    let view: DatabaseView | null = null;
    if (!failed.has(id)) {
      try {
        const settings = this.model.projectSettings();
        view = resolveDatabase({ docs, conventions: settings.conventions as Json, databaseConventions: settings.databases as Record<string, Json> }, id);
      } catch {
        view = null; // a shape the mock resolver cannot take while the model has errors
      }
    }
    const kept = (view?.tables ?? []).filter(
      (t) => !(t.entityId && failed.has(t.entityId)) && !(t.relationId && failed.has(t.relationId)) && !failed.has(t.key) && !failed.has(t.key.split("@")[0]),
    );
    this.tablesCache.views.set(id, new Map(kept.map((t) => [t.key, t])));
    const tables = kept.map((t) => ({
      key: t.key,
      name: t.name,
      schema: t.schema,
      origin: t.origin,
      entityId: t.entityId,
      relationId: t.relationId,
      isJunction: t.isJunction,
      isLookup: t.isLookup,
      columnCount: t.columns.length,
    }));
    const result: DatabaseTablesResult = { tables, diagnostics, partial: errors.length > 0 };
    this.tablesCache.results.set(id, result);
    return result;
  }

  /**
   * E5f: one table of a database with its columns, keys and indexes, from the same per-version
   * cache as databaseTables; table is null when no kept table has the key.
   */
  databaseTable(id: string, key: string): components["schemas"]["DatabaseTableResult"] | null {
    const summaries = this.databaseTables(id);
    if (!summaries) return null;
    const table = this.tablesCache?.views.get(id)?.get(key) ?? null;
    return { table, diagnostics: summaries.diagnostics, partial: summaries.partial };
  }

  private views(): DatabaseView[] {
    return this.model
      .index()
      .filter((s) => s.kind === "database")
      .map((s) => this.databaseView(s.id)?.view)
      .filter((v): v is DatabaseView => !!v);
  }

  /** Every unit of the selected packs, rendered. */
  renderUnits(packs: string[]): RenderUnit[] {
    const units: RenderUnit[] = [];
    const docs = this.model.docs();
    if (packs.includes("sql-ddl")) {
      const root = this.packOutput("sql-ddl").path;
      for (const view of this.views()) {
        for (const table of view.tables) {
          const entityName = table.entityId ? String(docs.get(table.entityId)?.name ?? "") : null;
          units.push({
            pack: "sql-ddl",
            unit: "table",
            elementId: table.key,
            unitKey: `sql-ddl/table:${table.key}`,
            files: [renderTable(view, table, entityName, root)],
          });
        }
        units.push({ pack: "sql-ddl", unit: "schema", elementId: view.id, unitKey: `sql-ddl/schema:${view.id}`, files: [renderSchema(view, root)] });
      }
    }
    if (packs.includes("csharp-dapper")) {
      const root = this.packOutput("csharp-dapper").path;
      const namespace = String((this.model.packs.find((p) => p.name === "csharp-dapper")?.parameters as Json | undefined)?.namespace ?? "App.Model");
      const sorted = [...docs.values()].sort((a, b) => String(a.name).localeCompare(String(b.name)));
      for (const doc of sorted) {
        const unit = doc.kind === "entity" ? "entity" : doc.kind === "enum" ? "enum" : doc.kind === "value-object" ? "value-object" : null;
        if (!unit) continue;
        units.push({
          pack: "csharp-dapper",
          unit,
          elementId: String(doc.id),
          unitKey: `csharp-dapper/${unit}:${String(doc.id)}`,
          files: renderCSharp(doc, docs, root, namespace),
        });
      }
    }
    return units;
  }

  preview(pack: string, unit: string, elementId: string | null): PreviewResult | { problem: string } {
    const manifest = this.model.packs.find((p) => p.name === pack);
    if (!manifest) return { problem: `No pack named '${pack}' is installed.` };
    if (!manifest.units.some((u) => u.id === unit)) return { problem: `Pack '${pack}' has no unit '${unit}'.` };
    const errors = this.model.validate().diagnostics.filter((d) => d.severity === "error");
    if (errors.length) return { files: [], diagnostics: errors, readKeys: [], elapsedMs: 0 };
    const found = this.renderUnits([pack]).find((u) => u.unit === unit && u.elementId === elementId);
    if (!found) {
      const diagnostic: Diagnostic = {
        rule: "MQ6006",
        severity: "error",
        message: `Unit ${pack}/${unit} does not render for ${elementId ?? "the model"}.`,
        elementId,
        filePath: `.maquettiste/templates/${pack}/${unit}.scriban`,
        jsonPointer: null,
        line: null,
        column: null,
      };
      return { files: [], diagnostics: [diagnostic], readKeys: [], elapsedMs: 0 };
    }
    const template = manifest.units.find((u) => u.id === unit)?.template ?? `${unit}.scriban`;
    const readKeys = [`e:${elementId ?? "model"}`, "s:conventions", `t:${pack}/${template}`].sort();
    return { files: found.files, diagnostics: [], readKeys, elapsedMs: 3 };
  }

  private planFiles(packs: string[], roots: RootSelection): { units: RenderUnit[]; files: Map<string, PlannedFile> } {
    const files = new Map<string, PlannedFile>();
    const units = this.renderUnits(packs).filter((u) => {
      const root = this.packOutput(u.pack);
      return roots === "all" || (roots === "committed" ? root.commit : !root.commit);
    });
    for (const unit of units)
      for (const file of unit.files)
        files.set(file.path, {
          path: file.path,
          text: file.text,
          pack: unit.pack,
          unitKey: unit.unitKey,
          role: file.role,
          mode: file.role === "companion" || unit.unit === "entity" ? "pair" : "overwrite",
          root: this.packOutput(unit.pack),
        });
    return { units, files };
  }

  private request(input: GenerationRequest): Required<GenerationRequest> {
    return {
      mode: "dry-run",
      packs: input.packs ?? null,
      force: input.force ?? false,
      jobs: input.jobs ?? null,
      handEdits: input.handEdits ?? null,
      includeDiffs: false,
      roots: input.roots ?? "all",
      lock: "wait",
      stageBarriers: false,
    };
  }

  plan(input: GenerationRequest): PlanResult {
    const request = this.request(input);
    const packs = (request.packs ?? this.enabledPacks()).filter((p) => this.model.packs.some((m) => m.name === p)).sort();
    const id = this.newId();
    const errors = this.model.validate().diagnostics.filter((d) => d.severity === "error");
    if (errors.length) {
      const plan: GenerationPlan = {
        id,
        request: { ...request, packs: request.packs },
        modelVersion: this.model.version,
        packs,
        units: [],
        changes: [],
        diagnostics: errors,
      };
      this.store(id, { plan, files: new Map(), diskAtPlan: new Map(), modelVersion: this.model.version });
      return { outcome: "invalid", plan: clone(plan) };
    }
    const { units, files } = this.planFiles(packs, request.roots);
    const handPolicy = request.handEdits ?? this.model.projectSettings().handEdits;
    const changes: FileChange[] = [];
    const diskAtPlan = new Map<string, string | null>();
    for (const file of [...files.values()].sort((a, b) => a.path.localeCompare(b.path))) {
      const disk = this.disk.get(file.path) ?? null;
      diskAtPlan.set(file.path, disk === null ? null : sha256Hex(disk));
      const newHash = sha256Hex(file.text);
      const oldHash = disk === null ? null : sha256Hex(disk);
      const written = this.manifest.get(file.path);
      let kind: FileChange["kind"];
      if (disk === null) kind = "added";
      else if (file.role === "companion") kind = "kept";
      else if (written && written.hash !== oldHash) kind = request.force || handPolicy === "overwrite" ? "modified" : "hand-edited";
      else kind = oldHash === newHash ? "unchanged" : "modified";
      changes.push({ path: file.path, kind, pack: file.pack, unitKey: file.unitKey, oldHash, newHash: kind === "kept" ? oldHash : newHash, diff: null });
    }
    for (const [path, entry] of [...this.manifest].sort(([a], [b]) => a.localeCompare(b))) {
      if (files.has(path) || !packs.includes(entry.pack) || !this.disk.has(path)) continue;
      changes.push({
        path,
        kind: "deleted",
        pack: entry.pack,
        unitKey: `${entry.pack}/(removed)`,
        oldHash: sha256Hex(this.disk.get(path)!),
        newHash: null,
        diff: null,
      });
      diskAtPlan.set(path, sha256Hex(this.disk.get(path)!));
    }
    const diagnostics: Diagnostic[] = changes
      .filter((c) => c.kind === "hand-edited")
      .map((c) => ({
        rule: "MQ6009",
        severity: handPolicy === "fail" ? ("error" as const) : ("warning" as const),
        message: `${c.path} was edited by hand since it was generated.`,
        elementId: null,
        filePath: c.path,
        jsonPointer: null,
        line: null,
        column: null,
      }));
    const docs = this.model.docs();
    const elementHash = (id: string | null) => (id && docs.has(id) ? sha256Hex(JSON.stringify(docs.get(id))) : null);
    const planUnits: PlanUnit[] = units.map((u) => {
      const inputHash = sha256Hex(u.files.map((f) => f.text).join("\u0000"));
      const previous = this.unitState.get(u.unitKey);
      const outputCauses: PlanUnit["causes"] = [];
      for (const f of u.files) {
        if (f.role === "companion") continue;
        const disk = this.disk.get(f.path);
        if (disk === undefined) outputCauses.push({ kind: "output-missing", key: f.path, detail: `${f.path} is missing`, elementId: null, path: f.path });
        else if (this.manifest.get(f.path)?.hash !== sha256Hex(disk))
          outputCauses.push({ kind: "output-edited", key: f.path, detail: `${f.path} was edited on disk`, elementId: null, path: f.path });
      }
      let reason: PlanUnit["reason"];
      let causes: PlanUnit["causes"] = [];
      if (request.force) reason = "forced";
      else if (!previous) reason = "new";
      else if (previous.inputHash !== inputHash) {
        reason = "inputs";
        const doc = u.elementId ? docs.get(u.elementId) : undefined;
        causes =
          doc && previous.elementHash !== elementHash(u.elementId)
            ? [{ kind: "element", key: `e:${u.elementId}`, detail: `${String(doc.name)} (${String(doc.kind)}) changed`, elementId: u.elementId, path: null }]
            : [
                {
                  kind: "inputs",
                  key: `e:${u.elementId ?? "model"}`,
                  detail: "An input it read changed (a referenced element, a setting or a parameter)",
                  elementId: null,
                  path: null,
                },
              ];
      } else if (outputCauses.length) {
        reason = "outputs";
        causes = outputCauses;
      } else reason = "unchanged";
      return {
        key: u.unitKey,
        inputHash,
        readKeys: [`e:${u.elementId ?? "model"}`, "s:conventions"],
        skipped: reason === "unchanged",
        pack: u.pack,
        unit: u.unit,
        template: this.model.packs.find((p) => p.name === u.pack)?.units.find((x) => x.id === u.unit)?.template ?? null,
        elementId: u.elementId ?? null,
        reason,
        causes: causes.slice(0, 20),
        causeCount: causes.length,
        outputs: u.files.map((f) => ({
          path: f.path,
          contentHash: sha256Hex(f.text),
          manifestHash: (f.role === "companion" ? "o:" : "") + sha256Hex(f.text),
          mode: f.role === "companion" || u.unit === "entity" ? "pair" : "overwrite",
          role: f.role,
          root: this.packOutput(u.pack),
          diskHashAtPlan: diskAtPlan.get(f.path) ?? null,
        })),
      };
    });
    const plan: GenerationPlan = {
      id,
      request: { ...request, packs: request.packs },
      modelVersion: this.model.version,
      packs,
      units: planUnits,
      changes,
      diagnostics,
    };
    this.store(id, { plan, files, diskAtPlan, modelVersion: this.model.version });
    const outcome = diagnostics.some((d) => d.severity === "error") ? "conflicts" : "succeeded";
    return { outcome, plan: clone(plan) };
  }

  private store(id: string, stored: StoredPlan): void {
    this.plans.set(id, stored);
    while (this.plans.size > 20) this.plans.delete(this.plans.keys().next().value!);
  }

  getPlan(id: string, units: boolean): GenerationPlan | null {
    const stored = this.plans.get(id);
    if (!stored) return null;
    const plan = clone(stored.plan);
    if (!units) plan.units = [];
    return plan;
  }

  /** The unified diff for one planned path; null when the plan or the path is unknown. */
  diff(planId: string, path: string): string | null {
    const stored = this.plans.get(planId);
    if (!stored) return null;
    const change = stored.plan.changes.find((c) => c.path === path);
    const file = stored.files.get(path);
    if (!change && !file) return null;
    if (!change || change.kind === "unchanged" || change.kind === "kept" || change.kind === "hand-edited") return "";
    const before = this.disk.get(path) ?? "";
    const after = change.kind === "deleted" ? "" : (file?.text ?? "");
    return unifiedDiff(path, before, after);
  }

  apply(planId: string): ApplyResult {
    const stored = this.plans.get(planId);
    if (!stored) return { outcome: "failed", staleUnits: [], stalePaths: [], result: null };
    const runId = this.newId();
    if (this.model.hasErrors() || stored.plan.diagnostics.some((d) => d.rule !== "MQ6009" && d.severity === "error"))
      return {
        outcome: "invalid",
        staleUnits: [],
        stalePaths: [],
        result: this.result(
          runId,
          "invalid",
          [],
          0,
          0,
          this.model.validate().diagnostics.filter((d) => d.severity === "error"),
        ),
      };
    const request = stored.plan.request;
    const { units, files } = this.planFiles(stored.plan.packs, (request.roots ?? "all") as RootSelection);
    const staleUnits = new Set<string>();
    const planned = new Map(stored.plan.units.map((u) => [u.key, u.inputHash]));
    for (const u of units) {
      const hash = sha256Hex(u.files.map((f) => f.text).join("\u0000"));
      if (planned.get(u.unitKey) !== hash) staleUnits.add(u.unitKey);
    }
    for (const key of planned.keys()) if (!units.some((u) => u.unitKey === key)) staleUnits.add(key);
    const stalePaths: string[] = [];
    for (const [path, hash] of stored.diskAtPlan) {
      const disk = this.disk.get(path);
      const now = disk === undefined ? null : sha256Hex(disk);
      if (now !== hash) stalePaths.push(path);
    }
    if (staleUnits.size || stalePaths.length) return { outcome: "stale", staleUnits: [...staleUnits].sort(), stalePaths: stalePaths.sort(), result: null };
    if (stored.plan.changes.some((c) => c.kind === "hand-edited") && (request.handEdits ?? this.model.projectSettings().handEdits) === "fail")
      return {
        outcome: "conflicts",
        staleUnits: [],
        stalePaths: [],
        result: this.result(runId, "conflicts", stored.plan.changes, 0, 0, stored.plan.diagnostics),
      };
    let written = 0;
    let deleted = 0;
    for (const change of stored.plan.changes) {
      if (change.kind === "added" || change.kind === "modified") {
        const file = stored.files.get(change.path) ?? files.get(change.path);
        if (!file) continue;
        this.disk.set(change.path, file.text);
        this.manifest.set(change.path, { hash: sha256Hex(file.text), pack: change.pack });
        written++;
      } else if (change.kind === "deleted") {
        this.disk.delete(change.path);
        this.manifest.delete(change.path);
        deleted++;
      }
    }
    const docs = this.model.docs();
    for (const u of stored.plan.units)
      this.unitState.set(u.key, {
        inputHash: u.inputHash,
        elementHash: u.elementId && docs.has(u.elementId) ? sha256Hex(JSON.stringify(docs.get(u.elementId))) : null,
      });
    return { outcome: "succeeded", staleUnits: [], stalePaths: [], result: this.result(runId, "succeeded", stored.plan.changes, written, deleted, []) };
  }

  private result(
    runId: string,
    outcome: GenerationResult["outcome"],
    changes: FileChange[],
    written: number,
    deleted: number,
    diagnostics: Diagnostic[],
  ): GenerationResult {
    return {
      runId,
      mode: "apply",
      outcome,
      changes: clone(changes),
      unitsRendered: new Set(changes.map((c) => c.unitKey)).size,
      unitsSkipped: 0,
      filesWritten: written,
      filesDeleted: deleted,
      diagnostics,
      timings: TIMINGS(changes.length),
    };
  }

  /** Simulates a hand edit of a generated file (for the hand-edit and stale scenarios). */
  editOnDisk(path: string, text: string): void {
    this.disk.set(path, text);
  }

  plannedFileCount(id: string): number {
    return this.plans.get(id)?.plan.changes.length ?? 0;
  }
}
