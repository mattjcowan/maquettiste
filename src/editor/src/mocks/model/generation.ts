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
  PackManifest,
  PlanUnit,
  PreviewResult,
  RenderedFile,
} from "@/api/types";
import type { components } from "@/api/schema";
import { sha256Hex } from "@/lib/sha256";
import { clone } from "@/lib/json";
import type { MockModel } from "./store";
import { resolveDatabase } from "./physical";
import {
  renderCSharp,
  renderDatabaseType,
  renderRoutine,
  renderSchema,
  renderSeed,
  renderSequence,
  renderSqlObject,
  renderTable,
  renderView,
  type RenderUnit,
  type SeedRows,
} from "./render";
import { unifiedDiff } from "./diff";
import { resolveQueries, sortDiagnostics } from "./queries";
import type { QuerySqlOptions } from "./querySql";

type Json = Record<string, unknown>;

interface PlannedFile {
  path: string;
  text: string;
  pack: string;
  unitKey: string;
  role: RenderedFile["role"];
  mode: "overwrite" | "pair";
  root: { path: string };
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

  /** Which example renderer each pack name uses: the two example packs, under their current names (a rename moves them). */
  private readonly renderers = new Map<string, "sql-ddl" | "csharp-dapper">([
    ["sql-ddl", "sql-ddl"],
    ["csharp-dapper", "csharp-dapper"],
  ]);

  /** The pack name the example renderer `kind` renders for now, or null when it was renamed away and nothing took it. */
  private packOf(kind: "sql-ddl" | "csharp-dapper"): string | null {
    for (const [name, k] of this.renderers) if (k === kind) return name;
    return null;
  }

  private packOutput(pack: string): { path: string } {
    const settings = this.model.projectSettings();
    const output = settings.packs[pack]?.output || ((this.renderers.get(pack) ?? pack) === "sql-ddl" ? "db" : "src/Generated");
    return { path: output };
  }

  /** A pack's effective parameter: the project's value, else the manifest's default. */
  private packParameter(pack: string, name: string): unknown {
    const project = (this.model.projectSettings().packs[pack] as { parameters?: Record<string, unknown> } | undefined)?.parameters;
    if (project && name in project) return project[name];
    return (this.model.packs.find((p) => p.name === pack)?.parameters as Record<string, unknown> | undefined)?.[name];
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

  /**
   * GET /api/model/queries/{id}/sql (GenerationService.GetQuerySqlAsync): the query's statement and one per collection for a
   * dialect, or `preview: null` with the diagnostics while the model has validation or resolution errors (MQ6017 when no query has
   * the id); what the dialect cannot render (MQ4029) joins the diagnostics. Throws on an unknown dialect or option.
   */
  querySql(id: string, dialect: string | null, options: QuerySqlOptions | null): components["schemas"]["QuerySqlResult"] {
    const report = this.model.validate().diagnostics;
    if (report.some((d) => d.severity === "error")) return { preview: null, diagnostics: report };
    const docs = this.model.docs();
    const settings = this.model.projectSettings();
    const database = docs.get(id)?.kind === "query" ? String(docs.get(id)?.database ?? "") : "";
    const view = database
      ? resolveDatabase({ docs, conventions: settings.conventions as Json, databaseConventions: settings.databases as Record<string, Json> }, database)
      : null;
    const found = view ? resolveQueries(view, docs).preview(id, dialect, options) : null;
    if (!found)
      return {
        preview: null,
        diagnostics: [
          ...report,
          {
            rule: "MQ6017",
            severity: "error",
            message: `No query has the id '${id}'.`,
            elementId: id,
            filePath: null,
            jsonPointer: null,
            line: null,
            column: null,
          },
        ],
      };
    return { preview: found.preview, diagnostics: sortDiagnostics([...report, ...found.diagnostics]) };
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
    // The server's summaries see validation only: a resolver finding (MQ4005) does not leave a table out.
    const diagnostics = this.model.validate({}, { resolved: false }).diagnostics;
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

  /**
   * Moves a pack's manifest entries and unit states to a new name (a pack rename), so its files stay tracked; answers the
   * paths now tracked under the new name, ordinal.
   */
  renamePack(from: string, to: string): string[] {
    const tracked: string[] = [];
    for (const [path, entry] of this.manifest)
      if (entry.pack === from) {
        entry.pack = to;
        tracked.push(path);
      }
    const renderer = this.renderers.get(from);
    if (renderer) {
      this.renderers.delete(from);
      this.renderers.set(to, renderer);
    }
    for (const [key, state] of [...this.unitState])
      if (key.startsWith(`${from}/`)) {
        this.unitState.delete(key);
        this.unitState.set(`${to}/${key.slice(from.length + 1)}`, state);
      }
    return tracked.sort();
  }

  /**
   * The name and kind a unit path carries for its element (UnitPath.elementName and elementKind), as the engine gives
   * them: a database by name, a table as `name (database)` (schema-qualified when not the default), an element by name.
   */
  elementLabel(id: string | null): { elementName: string | null; elementKind: string | null } {
    if (id === null) return { elementName: null, elementKind: null };
    for (const view of this.views()) {
      if (view.id === id) return { elementName: view.name, elementKind: "database" };
      const table = view.tables.find((t) => t.key === id);
      if (table) {
        const qualified = table.schema && table.schema !== view.defaultSchema ? `${table.schema}.${table.name}` : table.name;
        return { elementName: `${qualified} (${view.name})`, elementKind: "table" };
      }
    }
    const doc = this.model.docs().get(id);
    return doc ? { elementName: String(doc.name), elementKind: String(doc.kind) } : { elementName: null, elementKind: null };
  }

  /**
   * Each reference type with rows, by name: its strategy in the database (type[db id] → type["*"] → the database's
   * settings → the project's, as the Storage tab resolves it), and its codes and labels across its seeds, in seed name
   * then row order.
   */
  private referenceRows(view: DatabaseView): SeedRows[] {
    const docs = [...this.model.docs().values()];
    type Choice = { strategy?: string | null } | undefined;
    const settings = this.model.settingsJson as { conventions?: { referenceStorage?: Choice }; databases?: Record<string, { referenceStorage?: Choice }> };
    const fallback = settings.databases?.[view.name]?.referenceStorage ?? settings.conventions?.referenceStorage;
    const strategyOf = (t: Json): string | null => {
      const storage = t.storage as Record<string, Choice> | undefined;
      return ((storage?.[view.id] ?? storage?.["*"] ?? fallback)?.strategy ?? null) as string | null;
    };
    return docs
      .filter((d) => d.kind === "reference-type")
      .sort((a, b) => String(a.name).localeCompare(String(b.name)))
      .map((t) => {
        const seeds = docs.filter((d) => d.kind === "seed" && d.target === t.id).sort((a, b) => String(a.name).localeCompare(String(b.name)));
        const rows = seeds.flatMap((s) => {
          const columns = (s.columns as string[] | undefined) ?? [];
          const code = columns.indexOf("code");
          const label = columns.indexOf("label");
          return ((s.rows as { values?: unknown[] }[] | undefined) ?? [])
            .map((r) => ({ code: r.values?.[code], label: r.values?.[label] }))
            .filter((r) => r.code !== undefined && r.code !== null)
            .map((r) => ({ code: String(r.code), label: String(r.label ?? "") }));
        });
        return { name: String(t.name), pluralName: typeof t.pluralName === "string" ? t.pluralName : undefined, strategy: strategyOf(t), rows };
      })
      .filter((t) => t.rows.length > 0);
  }

  /** Every unit of the selected packs, rendered. */
  renderUnits(packs: string[]): RenderUnit[] {
    const units: RenderUnit[] = [];
    const docs = this.model.docs();
    const ddl = this.packOf("sql-ddl");
    if (ddl && packs.includes(ddl)) {
      const root = this.packOutput(ddl).path;
      for (const view of this.views()) {
        for (const table of view.tables) {
          const entityName = table.entityId ? String(docs.get(table.entityId)?.name ?? "") : null;
          units.push({
            pack: ddl,
            unit: "table",
            elementId: table.key,
            unitKey: `${ddl}/table:${table.key}`,
            files: [renderTable(view, table, entityName, root)],
          });
        }
        units.push({ pack: ddl, unit: "schema", elementId: view.id, unitKey: `${ddl}/schema:${view.id}`, files: [renderSchema(view, root)] });
        // The view, sequence, routine, database type and SQL object scripts, as the pack writes them: only with its objectScripts
        // parameter on.
        if (this.packParameter(ddl, "objectScripts") === true) {
          for (const v of view.views)
            units.push({ pack: ddl, unit: "view", elementId: v.id, unitKey: `${ddl}/view:${v.id}`, files: [renderView(view, v, root)] });
          for (const q of view.sequences)
            units.push({ pack: ddl, unit: "sequence", elementId: q.id, unitKey: `${ddl}/sequence:${q.id}`, files: [renderSequence(view, q, root)] });
          for (const r of view.routines ?? [])
            units.push({ pack: ddl, unit: "routine", elementId: r.id, unitKey: `${ddl}/routine:${r.id}`, files: [renderRoutine(view, r, root)] });
          for (const t of view.types ?? [])
            units.push({
              pack: ddl,
              unit: "database-type",
              elementId: t.id,
              unitKey: `${ddl}/database-type:${t.id}`,
              files: [renderDatabaseType(view, t, root)],
            });
          for (const o of view.objects ?? [])
            units.push({ pack: ddl, unit: "sql-object", elementId: o.id, unitKey: `${ddl}/sql-object:${o.id}`, files: [renderSqlObject(view, o, root)] });
        }
        // The seed script of every database (the unit is "select databases"), with the rows of the types stored as
        // lookup tables there.
        units.push({
          pack: ddl,
          unit: "seed",
          elementId: view.id,
          unitKey: `${ddl}/seed:${view.id}`,
          files: [renderSeed(view, this.referenceRows(view), root)],
        });
      }
    }
    const dapper = this.packOf("csharp-dapper");
    if (dapper && packs.includes(dapper)) {
      const root = this.packOutput(dapper).path;
      const namespace = String((this.model.packs.find((p) => p.name === dapper)?.parameters as Json | undefined)?.namespace ?? "App.Model");
      const sorted = [...docs.values()].sort((a, b) => String(a.name).localeCompare(String(b.name)));
      for (const doc of sorted) {
        const unit = doc.kind === "entity" ? "entity" : doc.kind === "enum" ? "enum" : doc.kind === "value-object" ? "value-object" : null;
        if (!unit) continue;
        units.push({
          pack: dapper,
          unit,
          elementId: String(doc.id),
          unitKey: `${dapper}/${unit}:${String(doc.id)}`,
          files: renderCSharp(doc, docs, root, namespace),
        });
      }
    }
    return units;
  }

  /** The MQ6026 refusal of a preview whose element is outside the unit's scope, or null (also for an unknown pack or unit). */
  previewScope(pack: string, unit: string, elementId: string | null): PreviewResult | null {
    const manifest = this.model.packs.find((p) => p.name === pack);
    const outOfScope = manifest && manifest.units.some((u) => u.id === unit) ? this.outOfScope(manifest, unit, elementId) : null;
    return outOfScope ? { files: [], diagnostics: [outOfScope], readKeys: [], elapsedMs: 0 } : null;
  }

  preview(pack: string, unit: string, elementId: string | null): PreviewResult | { problem: string } {
    const manifest = this.model.packs.find((p) => p.name === pack);
    if (!manifest) return { problem: `No pack named '${pack}' is installed.` };
    if (!manifest.units.some((u) => u.id === unit)) return { problem: `Pack '${pack}' has no unit '${unit}'.` };
    const outOfScope = this.outOfScope(manifest, unit, elementId);
    if (outOfScope) return { files: [], diagnostics: [outOfScope], readKeys: [], elapsedMs: 0 };
    const errors = this.model.validate().diagnostics.filter((d) => d.severity === "error");
    if (errors.length) return { files: [], diagnostics: errors, readKeys: [], elapsedMs: 0 };
    const found = this.renderUnits([pack]).find((u) => u.unit === unit && u.elementId === elementId);
    // An object's each-<kind> unit whose template writes nothing for the element (sql-ddl with objectScripts off).
    const scope = manifest.units.find((u) => u.id === unit)?.for ?? "";
    const kindOf = elementId ? this.model.docs().get(elementId)?.kind : undefined;
    const objectScopes = ["each view", "each sequence", "each routine", "each database type", "each sql object"];
    if (!found && objectScopes.includes(scope) && String(kindOf ?? "").replace(/-/g, " ") === scope.slice(5))
      return { files: [], diagnostics: [], readKeys: [`e:${elementId}`], elapsedMs: 1 };
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

  /** MQ6026 as the engine words it: a model unit given an element, or an `each <kind>` unit given another kind. */
  private outOfScope(manifest: PackManifest, unit: string, elementId: string | null): Diagnostic | null {
    const index = manifest.units.findIndex((u) => u.id === unit);
    const scope = manifest.units[index]?.for ?? "";
    const doc = elementId ? this.model.docs().get(elementId) : undefined;
    const got = doc ? `'${String(doc.name)}' (${String(doc.kind)})` : "";
    let message: string | null = null;
    if (scope === "model") {
      if (elementId) message = `Unit '${unit}' renders once for the whole model; preview it without an element (got ${got || `'${elementId}'`}).`;
    } else if (scope.startsWith("each ") && !["each table", "each locale"].includes(scope)) {
      const noun = scope.slice("each ".length);
      const article = /^[aeiou]/.test(noun) ? "an" : "a";
      if (!elementId) message = `This template expects ${article} ${noun} (unit '${unit}' is '${scope}'); pick one.`;
      else if (doc && String(doc.kind).replace(/-/g, " ") !== noun)
        message = `This template expects ${article} ${noun} (unit '${unit}' is '${scope}'), not ${got}; pick one.`;
    }
    if (!message) return null;
    return {
      rule: "MQ6026",
      severity: "error",
      message,
      elementId,
      filePath: `.maquettiste/templates/${manifest.name}/pack.json`,
      jsonPointer: `/units/${index}/for`,
      line: null,
      column: null,
    };
  }

  private planFiles(packs: string[]): { units: RenderUnit[]; files: Map<string, PlannedFile> } {
    const files = new Map<string, PlannedFile>();
    const units = this.renderUnits(packs);
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

  private request(input: GenerationRequest): Required<Omit<GenerationRequest, "roots">> {
    return {
      mode: "dry-run",
      packs: input.packs ?? null,
      force: input.force ?? false,
      jobs: input.jobs ?? null,
      handEdits: input.handEdits ?? null,
      includeDiffs: false,
      lock: "wait",
      stageBarriers: false,
    };
  }

  plan(input: GenerationRequest): PlanResult {
    const request = this.request(input);
    const packs = (request.packs ?? this.enabledPacks()).filter((p) => this.model.packs.some((m) => m.name === p)).sort();
    const id = this.newId();
    const report = this.model.validate().diagnostics;
    const errors = report.filter((d) => d.severity === "error");
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
    const { units, files } = this.planFiles(packs);
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
      // As WriteRun: a hand edit is hand-edited under overwrite (written) and skip (left), a conflict under fail; force changes nothing here.
      else if (written && written.hash !== oldHash) kind = handPolicy === "fail" ? "conflict" : "hand-edited";
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
    // As the engine's plan: the model's validation findings (warnings and notes here, the run stops on errors), then the run's own.
    const diagnostics: Diagnostic[] = [...report];
    for (const d of changes
      .filter((c) => c.kind === "hand-edited" || c.kind === "conflict")
      .map((c) => ({
        rule: "MQ6009",
        severity: handPolicy === "fail" ? ("error" as const) : ("warning" as const),
        message: `${c.path} was edited by hand since it was generated.`,
        elementId: null,
        filePath: c.path,
        jsonPointer: null,
        line: null,
        column: null,
      })))
      diagnostics.push(d);
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

  /** Deletes the stored plans `remove` accepts (JobQueue.ClearHistoryAsync); returns how many. */
  deletePlans(remove: (id: string) => boolean): number {
    let removed = 0;
    for (const id of [...this.plans.keys()])
      if (remove(id)) {
        this.plans.delete(id);
        removed++;
      }
    return removed;
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
    if (
      !change ||
      change.kind === "unchanged" ||
      change.kind === "kept" ||
      change.kind === "conflict" ||
      (change.kind === "hand-edited" && stored.plan.request.handEdits !== "overwrite")
    )
      return "";
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
    const { units, files } = this.planFiles(stored.plan.packs);
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
    const handPolicy = request.handEdits ?? this.model.projectSettings().handEdits;
    if (stored.plan.changes.some((c) => c.kind === "conflict"))
      return {
        outcome: "conflicts",
        staleUnits: [],
        stalePaths: [],
        result: this.result(runId, "conflicts", stored.plan.changes, 0, 0, stored.plan.diagnostics),
      };
    let written = 0;
    let deleted = 0;
    for (const change of stored.plan.changes) {
      if (change.kind === "added" || change.kind === "modified" || (change.kind === "hand-edited" && handPolicy === "overwrite")) {
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
