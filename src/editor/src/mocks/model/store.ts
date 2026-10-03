// MockModel: the mock backend's in-memory model (phase2-design.md 4.4), seeded from the billing
// fixture. It follows the engine's ModelStore contract closely enough for the editor: index, get
// (a sub-element id returns its owner), create, save with If-Match and 409, delete with reference
// checks and remove-references, atomic batches, references, validation and settings. Hashes are
// SHA-256 of the mock's own JSON text, not the engine's canonical bytes; nothing may compare them
// with real hashes.
import { applySchemaOperation, SCHEMA_OPS, type SchemaOp } from "@/model/databaseSchemas";
import { DATABASE_MEMBER_KINDS } from "@/model/model";
import type {
  BatchResult,
  BatchParseResult,
  ChangeSet,
  DeletePlan,
  DeleteResolution,
  Diagnostic,
  ElementDocument,
  ElementKind,
  ElementReadResult,
  ElementSummary,
  ExtensionSchema,
  PackManifest,
  ProjectInfo,
  ProjectSettings,
  ReferenceInfo,
  SaveResult,
  SettingsDocument,
  SettingsSaveResult,
  ValidationReport,
  ValidationScope,
} from "@/api/types";
import { sha256Hex } from "@/lib/sha256";
import { clone, jsonEqual } from "@/lib/json";
import { schemaValidator, describeErrors } from "../contract";
import { referencesOf, subElementIds, type Ref } from "./refs";
import { planDelete } from "./cascade";
import { typedElement } from "./typed";
import { applyRules, diagnosticKey, validateModel, type ModelEntry } from "./validate";
import { ModelIndex } from "./modelIndex";
import { applyCase, foreignKeyMismatches, resolveDatabase } from "./physical";
import { resolveQueries } from "./queries";

type Json = Record<string, unknown>;

export interface SeedFile {
  /** Path relative to .maquettiste/, e.g. model/entities/invoice.json. */
  path: string;
  text: string;
}

export interface Seed {
  files: SeedFile[];
  packs: Json[];
}

export interface Entry {
  id: string;
  path: string;
  json: Json;
  text: string;
  hash: string;
}

export interface MockModelOptions {
  newId: () => string;
  /** Called with every change set a write produces (the handlers publish model.changed). */
  onChanged?: (changes: ChangeSet) => void;
  onSettingsChanged?: (hash: string) => void;
}

const PREFIX = ".maquettiste/";

/** The indexes of the outputs.allow entries that set the retired `commit` flag (RetiredSettings.CommitEntries). */
export function retiredCommitEntries(settings: Json): number[] {
  const allow = (settings?.outputs as Json | undefined)?.allow;
  if (!Array.isArray(allow)) return [];
  return allow.flatMap((entry, i) => (entry && typeof entry === "object" && "commit" in (entry as Json) ? [i] : []));
}

/** Removes `commit` from every outputs.allow entry (RetiredSettings.Strip); whether anything was removed. */
export function stripRetiredCommit(settings: Json): boolean {
  const allow = (settings?.outputs as Json | undefined)?.allow;
  if (!Array.isArray(allow)) return false;
  let removed = false;
  for (const entry of allow)
    if (entry && typeof entry === "object" && "commit" in (entry as Json)) {
      delete (entry as Json).commit;
      removed = true;
    }
  return removed;
}

export function serialize(json: Json): string {
  return JSON.stringify(json, null, 2) + "\n";
}

const FOLDERS: Partial<Record<ElementKind, string>> = {
  package: "model/packages",
  entity: "model/entities",
  "value-object": "model/types",
  "scalar-type": "model/types",
  enum: "model/enums",
  relation: "model/relations",
  mapping: "model/mappings",
  diagram: "model/diagrams",
  stereotype: "model/vocabularies/stereotypes",
  "reference-type": "model/reference-types",
  process: "model/processes",
  actor: "model/actors",
  scenario: "model/scenarios",
};

/** The folders of a database's element files other than tables (KindInfo: model/databases/{db}/<folder>). */
const DATABASE_OBJECT_FOLDERS: Partial<Record<string, string>> = {
  view: "views",
  sequence: "sequences",
  routine: "routines",
  "database-type": "types",
  "sql-object": "objects",
  query: "queries",
};

function emptyChangeSet(source: ChangeSet["source"] = "editor"): ChangeSet {
  return { changed: [], deleted: [], source, truncated: false, isEmpty: true };
}

export class MockModel {
  readonly entries = new Map<string, Entry>();
  private readonly sidecars = new Map<string, string>();
  settingsJson: Json = { formatVersion: 1 };
  settingsHash = "";
  readonly extensions: ExtensionSchema[] = [];
  readonly packs: PackManifest[] = [];
  /** Increments on every write; plans remember it. */
  version = 1;
  /** Paths written since the seed (the top bar's changed-file count). */
  readonly changedPaths = new Set<string>();
  /**
   * Repo paths whose bytes are not in canonical form (MQ1003; the `legacy` scenario). The mock keeps no bytes apart from its
   * JSON, so this set stands for them: a save or a format (POST /api/model/format) clears a path.
   */
  readonly nonCanonical = new Set<string>();

  constructor(
    seed: Seed,
    private readonly options: MockModelOptions,
  ) {
    for (const file of seed.files) {
      if (file.path.endsWith(".js")) continue; // script rules: MockExtensions holds them
      if (file.path.endsWith(".md")) {
        this.sidecars.set(PREFIX + file.path, file.text);
        continue;
      }
      const json = JSON.parse(file.text) as Json;
      if (file.path === "maquettiste.json") {
        this.settingsJson = json;
        continue;
      }
      if (file.path.startsWith("extensions/")) {
        this.extensions.push(extensionRecord(json));
        continue;
      }
      if (!file.path.startsWith("model/")) continue;
      this.put(PREFIX + file.path, json);
    }
    this.settingsHash = sha256Hex(serialize(this.settingsJson));
    for (const pack of seed.packs) this.packs.push(packRecord(pack));
    this.packs.sort((a, b) => a.name.localeCompare(b.name));
  }

  /** The extension schemas changed (the Extensions tab): custom properties are checked again against the new set. */
  replaceExtensions(schemas: ExtensionSchema[]): void {
    this.extensions.splice(0, this.extensions.length, ...schemas);
    this.derivedState = null;
    this.version++;
  }

  private put(path: string, json: Json): Entry {
    const text = serialize(json);
    const entry: Entry = { id: String(json.id), path, json, text, hash: sha256Hex(text) };
    this.entries.set(entry.id, entry);
    return entry;
  }

  // ---------------------------------------------------------------- reads

  index(): ElementSummary[] {
    return [...this.entries.values()].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0)).map((e) => summary(e));
  }

  private tagCache: { version: number; tag: string } | null = null;

  /**
   * E5e: the index's ETag, built like the engine's ModelReads.IndexTag: a hash of the index format and every
   * row's id, hash and path in index order, computed once per model version.
   */
  indexTag(): string {
    if (this.tagCache?.version === this.version) return this.tagCache.tag;
    const rows = this.index();
    const parts = [INDEX_FORMAT, String(rows.length)];
    for (const row of rows) parts.push(row.id, row.hash, row.path);
    const tag = sha256Hex(parts.join("\n"));
    this.tagCache = { version: this.version, tag };
    return tag;
  }

  /** E5b: the documents of up to 200 ids, each once, in the order its first id was asked for. */
  readElements(ids: readonly string[]): ElementReadResult {
    const elements: ElementDocument[] = [];
    const missing: string[] = [];
    const seen = new Set<string>();
    for (const id of ids) {
      const entry = this.owner(id);
      if (!entry) missing.push(id);
      else if (!seen.has(entry.id)) {
        seen.add(entry.id);
        elements.push(this.document(entry));
      }
    }
    return { elements, missing };
  }

  /** The index row of one element, or undefined when it is gone (E5d). */
  summaryOf(id: string): ElementSummary | undefined {
    const entry = this.entries.get(id);
    return entry ? summary(entry) : undefined;
  }

  /** The entry that holds an id (an element or one of its sub-elements). */
  owner(id: string): Entry | undefined {
    const direct = this.entries.get(id);
    if (direct) return direct;
    const owner = this.derived().ownerOf(id);
    return owner === undefined ? undefined : this.entries.get(owner);
  }

  private derivedState: ModelIndex | null = null;
  private derivedVersion = -1;

  /**
   * The owner map, reverse references, name scopes and per-entry diagnostics, synced to the
   * current entries once per model version (explorer-redesign.md section 5 item 4).
   */
  private derived(): ModelIndex {
    this.derivedState ??= new ModelIndex(this.extensions);
    if (this.derivedVersion !== this.version) {
      this.derivedState.sync(this.entries);
      this.derivedVersion = this.version;
    }
    return this.derivedState;
  }

  private rules(): Record<string, string> {
    return ((this.settingsJson.validation as Json | undefined)?.rules as Record<string, string> | undefined) ?? {};
  }

  document(entry: Entry): ElementDocument {
    const description = entry.json.description as { file?: string } | string | undefined;
    const sidecarPath =
      description && typeof description === "object" && description.file ? entry.path.slice(0, entry.path.lastIndexOf("/") + 1) + description.file : null;
    const sidecarText = sidecarPath ? (this.sidecars.get(sidecarPath) ?? null) : null;
    return {
      element: typedElement(entry.json) as ElementDocument["element"],
      path: entry.path,
      hash: entry.hash,
      dependencyHash: sha256Hex(entry.text + (sidecarText ?? "")),
      json: clone(entry.json) as ElementDocument["json"],
      sidecarText,
    };
  }

  get(id: string): ElementDocument | null {
    const entry = this.owner(id);
    return entry ? this.document(entry) : null;
  }

  references(id: string): ReferenceInfo[] | null {
    const target = this.owner(id);
    if (!target) return null;
    const ids = new Set(target.id !== id ? [id] : [target.id, ...subElementIds(target.json)]);
    const out: ReferenceInfo[] = [];
    const from = [...this.derived().referrersOf(ids)]
      .flatMap((r) => this.entries.get(r) ?? [])
      .sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
    for (const entry of from) {
      if (entry.id === target.id) continue;
      for (const ref of referencesOf(entry.json))
        if (ids.has(ref.toId)) out.push({ fromElementId: entry.id, fromId: ref.fromId, jsonPointer: ref.pointer, field: ref.field, toId: ref.toId });
    }
    return out;
  }

  private sortedEntries(): Entry[] {
    return [...this.entries.values()].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
  }

  // ---------------------------------------------------------------- validation

  /** Validates any set of entries from scratch (the full, non-incremental rules). */
  diagnosticsOf(entries: Iterable<Entry | ModelEntry>): Diagnostic[] {
    const list = [...entries].map((e) => ({ id: e.id, path: e.path, json: e.json }));
    return validateModel({ entries: list, extensions: this.extensions, rules: this.rules() });
  }

  /** Diagnostics of the whole model that no entry owns (the backend sets it: localization completeness), before validation.rules applies. */
  modelDiagnostics: (rules: Record<string, string>) => Diagnostic[] = () => [];

  /** Every diagnostic of the current model, from the incrementally maintained per-entry results. */
  private currentDiagnostics(): Diagnostic[] {
    const rules = this.rules();
    return applyRules([...this.derived().diagnostics(), ...this.canonicalFindings(), ...this.modelDiagnostics(rules)], rules);
  }

  private findingsCache: { version: number; diagnostics: Diagnostic[] } | null = null;

  /** MQ1003 for each file not in canonical form, MQ1010 for each outputs.allow entry of maquettiste.json that sets `commit`. */
  private canonicalFindings(): Diagnostic[] {
    const finding = (rule: string, severity: Diagnostic["severity"], message: string, elementId: string | null, filePath: string, jsonPointer: string) => ({
      rule,
      severity,
      message,
      elementId,
      filePath,
      jsonPointer,
      line: null,
      column: null,
    });
    const byPath = new Map([...this.entries.values()].map((e) => [e.path, e.id]));
    const out: Diagnostic[] = [...this.nonCanonical]
      .sort()
      .map((path) =>
        finding(
          "MQ1003",
          "warning",
          "The file is not in canonical form: rewrite it from the Problems panel or run maquettiste format.",
          byPath.get(path) ?? null,
          path,
          "",
        ),
      );
    retiredCommitEntries(this.settingsJson).forEach((i) =>
      out.push(
        finding(
          "MQ1010",
          "info",
          "`commit` is ignored since 0.5.5; remove it (maquettiste format drops it): every output root is a folder generation may write under, and which outputs to commit is the team's choice.",
          null,
          `${PREFIX}maquettiste.json`,
          `/outputs/allow/${i}/commit`,
        ),
      ),
    );
    return out;
  }

  /**
   * POST /api/model/format: rewrites model files in canonical form (ModelStore.FormatAsync). The mock writes its JSON as it always
   * does; what changes is that the file leaves `nonCanonical`, and maquettiste.json loses the retired `commit` flags. `paths`
   * absent means every model file; a path that is not maquettiste.json or an element file refuses the whole call.
   */
  format(paths?: readonly string[]): { formatted: string[]; skipped: string[]; refused: string[]; total: number } {
    const settingsPath = `${PREFIX}maquettiste.json`;
    const byPath = new Map([...this.entries.values()].map((e) => [e.path, e]));
    const all = [settingsPath, ...[...byPath.keys()].sort()];
    const refused = (paths ?? []).filter((p) => p !== settingsPath && !byPath.has(p));
    const targets = paths ? [...new Set(paths)].filter((p) => !refused.includes(p)).sort() : all;
    if (refused.length) return { formatted: [], skipped: [], refused, total: targets.length };
    const formatted: string[] = [];
    const changed: ChangeSet["changed"] = [];
    let settings = false;
    for (const path of targets) {
      const stale = this.nonCanonical.delete(path);
      if (path === settingsPath) {
        const stripped = stripRetiredCommit(this.settingsJson);
        if (!stale && !stripped) continue;
        settings = true;
      } else {
        if (!stale) continue;
        const entry = byPath.get(path)!;
        changed.push({ id: entry.id, kind: entry.json.kind as ElementKind, path: entry.path, hash: entry.hash });
      }
      formatted.push(path);
      this.changedPaths.add(path);
    }
    if (formatted.length) this.version++;
    if (settings) {
      this.settingsHash = sha256Hex(serialize(this.settingsJson));
      this.options.onSettingsChanged?.(this.settingsHash);
    }
    if (changed.length) this.options.onChanged?.({ changed, deleted: [], source: "editor", truncated: false, isEmpty: false });
    return { formatted: formatted.sort(), skipped: [], refused: [], total: targets.length };
  }

  /**
   * What the resolver finds once the model validates without error, as the server's ModelStore.ValidateAsync adds it: the resolved
   * MQ4005 of a foreign key column whose type an overlay pins apart from the referenced column's, and what a query gets wrong
   * (MQ4021 to MQ4043, with MQ3001 and MQ4018). Once per model version.
   */
  private resolverFindings(validated: Diagnostic[]): Diagnostic[] {
    if (validated.some((d) => d.severity === "error")) return [];
    if (this.findingsCache?.version === this.version) return this.findingsCache.diagnostics;
    const docs = this.docs();
    const settings = this.projectSettings();
    const out: Diagnostic[] = [];
    for (const db of [...docs.values()].filter((d) => d.kind === "database")) {
      const view = resolveDatabase(
        { docs, conventions: settings.conventions as Json, databaseConventions: settings.databases as Record<string, Json> },
        String(db.id),
      );
      if (!view) continue;
      out.push(...foreignKeyMismatches(view, docs).map((d) => ({ ...d, filePath: d.elementId ? (this.entries.get(d.elementId)?.path ?? null) : null })));
      // The queries' findings (MQ3001, MQ4018, MQ4021 to MQ4043), which only the resolver can see (queries.ts).
      const paths = new Map([...this.entries.values()].filter((e) => e.json.kind === "query").map((e) => [e.id, e.path]));
      out.push(...applyRules(resolveQueries(view, docs, paths).diagnostics, this.rules()));
    }
    this.findingsCache = { version: this.version, diagnostics: out };
    return out;
  }

  /** The report every validate path answers; `resolved: false` leaves out the resolver's findings (the table summaries, as the server's). */
  validate(scope: ValidationScope = {}, options: { resolved?: boolean } = {}): ValidationReport {
    const validated = this.currentDiagnostics();
    let diagnostics = options.resolved === false ? validated : [...validated, ...this.resolverFindings(validated)];
    if (scope.elementIds) {
      const wanted = new Set<string>();
      for (const id of scope.elementIds) {
        const owner = this.owner(id);
        if (owner) wanted.add(owner.id);
      }
      if (scope.includeReferrers !== false) {
        const index = this.derived();
        for (const r of index.referrersOf([...wanted].flatMap((w) => index.idsHeldBy(w)))) wanted.add(r);
      }
      diagnostics = diagnostics.filter((d) => d.elementId !== null && wanted.has(d.elementId));
    }
    return report(diagnostics);
  }

  hasErrors(): boolean {
    return this.currentDiagnostics().some((d) => d.severity === "error");
  }

  // ---------------------------------------------------------------- writes

  private conventionalPath(json: Json, keepFrom?: Entry): string {
    const kind = json.kind as ElementKind;
    const name = typeof json.name === "string" && json.name ? json.name : String(json.id);
    let folder = FOLDERS[kind];
    let file = `${applyCase(name, "kebab") || String(json.id).toLowerCase()}.json`;
    if (kind === "stereotype") file = `${String(json.key ?? name)}.json`;
    const db = typeof json.database === "string" ? this.entries.get(json.database) : undefined;
    const dbFolder = db ? applyCase(String(db.json.name), "kebab") : "main";
    if (kind === "database") {
      folder = `model/databases/${applyCase(name, "kebab")}`;
      file = "database.json";
    } else if (kind === "table") {
      folder = `model/databases/${dbFolder}/tables`;
      file = `${String(json.id).toLowerCase()}.json`;
    } else if (DATABASE_OBJECT_FOLDERS[kind]) folder = `model/databases/${dbFolder}/${DATABASE_OBJECT_FOLDERS[kind]}`;
    else if (kind === "tag-vocabulary" || kind === "category-tree") {
      // The global vocabularies are tags.json and categories.json; a domain's is <domain>-tags.json (section 1.11).
      folder = "model/vocabularies";
      const scope = typeof json.package === "string" ? this.entries.get(json.package) : undefined;
      const base = kind === "tag-vocabulary" ? "tags" : "categories";
      file = scope ? `${applyCase(String(scope.json.name), "kebab")}-${base}.json` : `${base}.json`;
    } else if (kind === "seed") {
      const target = typeof json.target === "string" ? this.entries.get(json.target) : undefined;
      folder = `model/seeds/${applyCase(String(target?.json.name ?? json.target ?? "seed"), "kebab")}`;
    }
    let path = `${PREFIX}${folder ?? "model"}/${file}`;
    if (keepFrom && keepFrom.path === path) return path;
    let n = 2;
    const taken = (p: string) => [...this.entries.values()].some((e) => e.path === p && e.id !== json.id);
    while (taken(path)) path = `${PREFIX}${folder ?? "model"}/${file.replace(/\.json$/, `-${n++}.json`)}`;
    return path;
  }

  private withSchemaPath(json: Json, path: string): Json {
    const depth = path.slice(PREFIX.length).split("/").length - 1;
    const out: Json = { $schema: `${"../".repeat(depth)}.schema/v1/${String(json.kind)}.json` };
    for (const [k, v] of Object.entries(json)) if (k !== "$schema") out[k] = v;
    return out;
  }

  private notFound(id: string | null): SaveResult {
    return {
      outcome: "not-found",
      id,
      hash: null,
      current: null,
      diagnostics: [],
      referrers: [],
      changes: null,
    };
  }

  private invalid(id: string | null, diagnostics: Diagnostic[]): SaveResult {
    return { outcome: "invalid", id, hash: null, current: null, diagnostics, referrers: [], changes: null };
  }

  /** Errors the candidate introduces (compared with the current model), and every diagnostic of `ids`. */
  /**
   * The errors a candidate state introduces and the candidate's diagnostics of `ids`. Only the
   * entries the difference can affect are re-checked: the index moves to the candidate and back.
   */
  private check(candidate: Map<string, Entry>, ids: string[]): { introduced: Diagnostic[]; all: Diagnostic[] } {
    const index = this.derived();
    const rules = this.rules();
    const affected = index.sync(candidate);
    for (const id of ids) affected.add(id);
    const after = applyRules(index.diagnosticsFor(affected), rules);
    index.sync(this.entries);
    const before = new Set(
      applyRules(index.diagnosticsFor(affected), rules)
        .filter((d) => d.severity === "error")
        .map(diagnosticKey),
    );
    const introduced = after.filter((d) => d.severity === "error" && !before.has(diagnosticKey(d)));
    const scope = new Set(ids);
    return { introduced, all: after.filter((d) => d.elementId !== null && scope.has(d.elementId)) };
  }

  private schemaCheck(id: string, json: Json, path: string): Diagnostic[] {
    const validate = (() => {
      try {
        return schemaValidator(String(json.kind));
      } catch {
        return null;
      }
    })();
    if (!validate)
      return [
        {
          rule: "MQ1002",
          severity: "error",
          message: `/kind: '${String(json.kind)}' is not a model kind`,
          elementId: id,
          filePath: path,
          jsonPointer: "/kind",
          line: null,
          column: null,
        },
      ];
    if (validate(json)) return [];
    return describeErrors(validate.errors).map((message, i) => ({
      rule: "MQ1002",
      severity: "error" as const,
      message,
      elementId: id,
      filePath: path,
      jsonPointer: validate.errors?.[i]?.instancePath ?? "",
      line: null,
      column: null,
    }));
  }

  private commit(candidate: Map<string, Entry>, source: ChangeSet["source"] = "editor"): ChangeSet {
    const changed: ChangeSet["changed"] = [];
    const deleted: string[] = [];
    for (const [id, entry] of candidate) {
      const old = this.entries.get(id);
      if (!old || old.hash !== entry.hash || old.path !== entry.path) {
        // A save writes canonical bytes.
        this.nonCanonical.delete(entry.path);
        changed.push({ id, kind: entry.json.kind as ElementKind, path: entry.path, hash: entry.hash });
        this.changedPaths.add(entry.path);
        if (old && old.path !== entry.path) this.changedPaths.add(old.path);
      }
    }
    for (const id of this.entries.keys())
      if (!candidate.has(id)) {
        deleted.push(id);
        this.changedPaths.add(this.entries.get(id)!.path);
      }
    this.entries.clear();
    for (const [id, entry] of candidate) this.entries.set(id, entry);
    const set: ChangeSet = { changed, deleted, source, truncated: false, isEmpty: changed.length === 0 && deleted.length === 0 };
    if (!set.isEmpty) {
      this.version++;
      this.options.onChanged?.(set);
    }
    return set;
  }

  private entryFor(json: Json, existing?: Entry): Entry {
    const path = this.conventionalPath(json, existing);
    const withSchema = this.withSchemaPath(json, path);
    const text = serialize(withSchema);
    return { id: String(json.id), path, json: withSchema, text, hash: sha256Hex(text) };
  }

  create(input: Json): SaveResult {
    const json = clone(input);
    if (typeof json.id !== "string" || json.id === "") json.id = this.options.newId();
    const id = String(json.id);
    if (!/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/.test(id))
      return this.invalid(id, [
        {
          rule: "MQ1006",
          severity: "error",
          message: `'${id}' is not a valid ULID.`,
          elementId: id,
          filePath: null,
          jsonPointer: "/id",
          line: null,
          column: null,
        },
      ]);
    if (this.owner(id))
      return this.invalid(id, [
        {
          rule: "MQ1004",
          severity: "error",
          message: `The id ${id} is already used.`,
          elementId: id,
          filePath: null,
          jsonPointer: "/id",
          line: null,
          column: null,
        },
      ]);
    const entry = this.entryFor(json);
    const schema = this.schemaCheck(id, entry.json, entry.path);
    if (schema.length) return this.invalid(id, schema);
    const candidate = new Map(this.entries);
    candidate.set(id, entry);
    const { introduced, all } = this.check(candidate, [id]);
    if (introduced.length) return this.invalid(id, all.length ? all : introduced);
    const changes = this.commit(candidate);
    return { outcome: "saved", id, hash: entry.hash, current: this.document(entry), diagnostics: all, referrers: [], changes };
  }

  save(id: string, input: Json, expectedHash: string): SaveResult {
    const existing = this.entries.get(id);
    if (!existing) return this.notFound(id);
    if (normalizeHash(expectedHash) !== existing.hash)
      return { outcome: "conflict", id, hash: existing.hash, current: this.document(existing), diagnostics: [], referrers: [], changes: null };
    const json = clone(input);
    if (json.id !== id)
      return this.invalid(id, [
        {
          rule: "MQ1002",
          severity: "error",
          message: `The body's id must be ${id}.`,
          elementId: id,
          filePath: existing.path,
          jsonPointer: "/id",
          line: null,
          column: null,
        },
      ]);
    const entry = this.entryFor(json, existing);
    const schema = this.schemaCheck(id, entry.json, entry.path);
    if (schema.length) return this.invalid(id, schema);
    const candidate = new Map(this.entries);
    candidate.set(id, entry);
    const { introduced, all } = this.check(candidate, [id]);
    if (introduced.length) return this.invalid(id, all.length ? all : introduced);
    if (entry.hash === existing.hash && entry.path === existing.path)
      return { outcome: "saved", id, hash: entry.hash, current: this.document(existing), diagnostics: all, referrers: [], changes: emptyChangeSet() };
    const changes = this.commit(candidate);
    return { outcome: "saved", id, hash: entry.hash, current: this.document(entry), diagnostics: all, referrers: [], changes };
  }

  /** Referrers of an element or its sub-elements, from elements other than `excluding`. */
  private referrers(target: Entry, from: Iterable<Entry>, excluding: Set<string>): { entry: Entry; ref: Ref }[] {
    const ids = new Set([target.id, ...subElementIds(target.json)]);
    const out: { entry: Entry; ref: Ref }[] = [];
    for (const entry of from) {
      if (entry.id === target.id || excluding.has(entry.id)) continue;
      for (const ref of referencesOf(entry.json)) if (ids.has(ref.toId)) out.push({ entry, ref });
    }
    return out;
  }

  /** What deleting `ids` with `resolution` would do (GET /api/model/elements/{id}/delete-plan, POST /api/model/delete-plan). */
  deletePlan(ids: string[], resolution: DeleteResolution): DeletePlan {
    return planDelete(this.entries, ids, resolution).plan;
  }

  /** The MQ2001 diagnostics of a plan's refusals, readable first (as the engine sorts them). */
  private refusals(plan: DeletePlan): Diagnostic[] {
    return plan.refused.map((r) => ({
      rule: "MQ2001",
      severity: "error" as const,
      message: `${r.kind ?? "element"} '${r.name ?? r.id}' cannot lose ${r.why} Change or delete it first, or delete with its dependents (resolution delete-dependents).`,
      elementId: r.id,
      filePath: r.id ? (this.entries.get(r.id)?.path ?? null) : null,
      jsonPointer: r.pointer,
      line: null,
      column: null,
    }));
  }

  delete(id: string, expectedHash: string, resolution: DeleteResolution = "refuse"): SaveResult {
    const existing = this.entries.get(id);
    if (!existing) return this.notFound(id);
    if (normalizeHash(expectedHash) !== existing.hash)
      return { outcome: "conflict", id, hash: existing.hash, current: this.document(existing), diagnostics: [], referrers: [], changes: null };
    if (resolution !== "refuse") {
      const { plan, deleted, edited } = planDelete(this.entries, [id], resolution);
      if (plan.refused.length) return this.invalid(id, this.refusals(plan));
      const candidate = new Map(this.entries);
      for (const gone of deleted) candidate.delete(gone);
      for (const [refId, json] of edited) candidate.set(refId, this.entryFor(json, this.entries.get(refId)));
      const changes = this.commit(candidate);
      return { outcome: "saved", id, hash: null, current: null, diagnostics: [], referrers: [], changes };
    }
    const found = this.referrers(existing, this.sortedEntries(), new Set());
    const infos = found.map(({ entry, ref }) => ({
      fromElementId: entry.id,
      fromId: ref.fromId,
      jsonPointer: ref.pointer,
      field: ref.field,
      toId: ref.toId,
    }));
    if (found.length && resolution === "refuse")
      return { outcome: "referenced", id, hash: null, current: null, diagnostics: [], referrers: infos, changes: null };
    const candidate = new Map(this.entries);
    candidate.delete(id);
    if (found.length) {
      const required = found.filter((f) => f.ref.required);
      if (required.length)
        return this.invalid(
          id,
          required.map(({ entry, ref }) => ({
            rule: "MQ2001",
            severity: "error" as const,
            message: `'${ref.field}' of ${entry.path} is required and cannot be cleared.`,
            elementId: entry.id,
            filePath: entry.path,
            jsonPointer: ref.pointer,
            line: null,
            column: null,
          })),
        );
      const edited = new Map<string, Json>();
      // Remove array items from the end, so earlier indexes stay valid.
      const sortedRefs = [...found].sort((a, b) =>
        (b.ref.removeItem ?? b.ref.pointer).localeCompare(a.ref.removeItem ?? a.ref.pointer, undefined, { numeric: true }),
      );
      for (const { entry, ref } of sortedRefs) {
        const json = edited.get(entry.id) ?? clone(entry.json);
        removeAt(json, ref.removeItem ?? ref.pointer, ref.removeItem !== undefined);
        edited.set(entry.id, json);
      }
      for (const [refId, json] of edited) candidate.set(refId, this.entryFor(json, this.entries.get(refId)));
    }
    const changes = this.commit(candidate);
    return { outcome: "saved", id, hash: null, current: null, diagnostics: [], referrers: [], changes };
  }

  batch(body: unknown): { status: number; body: BatchResult | BatchParseResult } {
    const parse = schemaValidator("batch");
    if (!parse(body)) {
      return {
        status: 422,
        body: {
          batch: null,
          diagnostics: (parse.errors ?? []).map((e) => ({
            rule: "MQ1002",
            severity: "error" as const,
            message: `${e.instancePath || "/"} ${e.keyword}: ${e.message ?? ""}`,
            elementId: null,
            filePath: null,
            jsonPointer: e.instancePath,
            line: null,
            column: null,
          })),
        },
      };
    }
    const operations = (body as { operations: { op: string; id?: string; expectedHash?: string; element?: Json; resolution?: DeleteResolution }[] }).operations;
    const candidate = new Map(this.entries);
    const items: SaveResult[] = [];
    const deleting = new Set(operations.filter((o) => o.op === "delete" && o.id).map((o) => o.id!));
    let failure: SaveResult["outcome"] | null = null;
    const fail = (result: SaveResult) => {
      items.push(result);
      failure ??= result.outcome;
    };
    for (const op of operations) {
      if (SCHEMA_OPS.has(op.op)) {
        // A database schema operation (erratum E26): expands into updates of the database and what it moves.
        const docs = new Map([...candidate].map(([k, e]) => [k, e.json as Json]));
        const outcome = applySchemaOperation(docs, op as unknown as SchemaOp, this.options.newId);
        if ("error" in outcome) {
          fail(
            this.invalid(op.id ?? null, [
              {
                rule: "MQ4015",
                severity: "error",
                message: outcome.error,
                elementId: op.id ?? null,
                filePath: null,
                jsonPointer: "",
                line: null,
                column: null,
              },
            ]),
          );
          continue;
        }
        for (const [id, json] of outcome.changed) {
          const entry = this.entryFor(json, candidate.get(id));
          candidate.set(id, entry);
          items.push({ outcome: "saved", id, hash: entry.hash, current: null, diagnostics: [], referrers: [], changes: null });
        }
        continue;
      }
      if (op.op === "refresh-scenario") {
        // The mock has no interpreter: the expectations stay as they are (the engine rewrites them from a replay).
        const existing = candidate.get(op.id ?? "");
        if (!existing || existing.json.kind !== "scenario") {
          fail(this.notFound(op.id ?? ""));
          continue;
        }
        items.push({ outcome: "saved", id: existing.id, hash: existing.hash, current: null, diagnostics: [], referrers: [], changes: null });
        continue;
      }
      if (op.op === "set-initial" || op.op === "set-lifecycle") {
        // The process quick fixes (phase-3-design.md 3): expanded into updates of the documents they change.
        const target = (op as { target?: string }).target;
        const changed = op.op === "set-initial" ? setInitialDocs(candidate, op.id ?? "", target) : setLifecycleDocs(candidate, op.id ?? "", target);
        if (typeof changed === "string") {
          fail(
            this.invalid(op.id ?? null, [
              { rule: "MQ9019", severity: "error", message: changed, elementId: op.id ?? null, filePath: null, jsonPointer: "", line: null, column: null },
            ]),
          );
          continue;
        }
        for (const json of changed) {
          const id = String(json.id);
          const entry = this.entryFor(json, candidate.get(id));
          candidate.set(id, entry);
          items.push({ outcome: "saved", id, hash: entry.hash, current: null, diagnostics: [], referrers: [], changes: null });
        }
        continue;
      }
      if (op.op === "create") {
        const json = clone(op.element!);
        if (typeof json.id !== "string") json.id = this.options.newId();
        const id = String(json.id);
        if (candidate.has(id)) {
          fail(
            this.invalid(id, [
              {
                rule: "MQ1004",
                severity: "error",
                message: `The id ${id} is already used.`,
                elementId: id,
                filePath: null,
                jsonPointer: "/id",
                line: null,
                column: null,
              },
            ]),
          );
          continue;
        }
        const entry = this.entryFor(json);
        const schema = this.schemaCheck(id, entry.json, entry.path);
        if (schema.length) {
          fail(this.invalid(id, schema));
          continue;
        }
        candidate.set(id, entry);
        items.push({ outcome: "saved", id, hash: entry.hash, current: null, diagnostics: [], referrers: [], changes: null });
      } else if (op.op === "update") {
        const id = op.id!;
        const existing = candidate.get(id);
        if (!existing) {
          fail(this.notFound(id));
          continue;
        }
        if (normalizeHash(op.expectedHash!) !== existing.hash) {
          fail({ outcome: "conflict", id, hash: existing.hash, current: this.document(existing), diagnostics: [], referrers: [], changes: null });
          continue;
        }
        const json = clone(op.element!);
        if (json.id === undefined) json.id = id;
        const entry = this.entryFor(json, existing);
        const schema = this.schemaCheck(id, entry.json, entry.path);
        if (schema.length || json.id !== id) {
          fail(this.invalid(id, schema.length ? schema : []));
          continue;
        }
        candidate.set(id, entry);
        items.push({ outcome: "saved", id, hash: entry.hash, current: null, diagnostics: [], referrers: [], changes: null });
      } else {
        const id = op.id!;
        const existing = candidate.get(id);
        if (!existing) {
          fail(this.notFound(id));
          continue;
        }
        if (normalizeHash(op.expectedHash!) !== existing.hash) {
          fail({ outcome: "conflict", id, hash: existing.hash, current: this.document(existing), diagnostics: [], referrers: [], changes: null });
          continue;
        }
        if (op.resolution && op.resolution !== "refuse") {
          // A delete that resolves references: its cascade runs on the batch's candidate so far, all or nothing with the rest.
          if (!candidate.has(id)) continue;
          const { plan, deleted, edited } = planDelete(candidate, [id], op.resolution, deleting);
          if (plan.refused.length) {
            fail(this.invalid(id, this.refusals(plan)));
            continue;
          }
          for (const gone of deleted) candidate.delete(gone);
          for (const [refId, json] of edited) candidate.set(refId, this.entryFor(json, candidate.get(refId)));
          items.push({ outcome: "saved", id, hash: null, current: null, diagnostics: [], referrers: [], changes: null });
          continue;
        }
        const found = this.referrers(existing, candidate.values(), deleting);
        if (found.length) {
          fail({
            outcome: "referenced",
            id,
            hash: null,
            current: null,
            diagnostics: [],
            referrers: found.map(({ entry, ref }) => ({
              fromElementId: entry.id,
              fromId: ref.fromId,
              jsonPointer: ref.pointer,
              field: ref.field,
              toId: ref.toId,
            })),
            changes: null,
          });
          continue;
        }
        candidate.delete(id);
        items.push({ outcome: "saved", id, hash: null, current: null, diagnostics: [], referrers: [], changes: null });
      }
    }
    if (!failure) {
      const touched = items.map((i) => i.id!).filter((id) => candidate.has(id));
      const { introduced } = this.check(candidate, touched);
      if (introduced.length) {
        failure = "invalid";
        for (let i = 0; i < items.length; i++) {
          const mine = introduced.filter((d) => d.elementId === items[i].id);
          if (mine.length) items[i] = this.invalid(items[i].id, mine);
        }
        if (!items.some((i) => i.outcome === "invalid")) items[0] = this.invalid(items[0].id, introduced);
      }
    }
    const outcome = failure as SaveResult["outcome"] | null;
    if (outcome) {
      const body: BatchResult = {
        outcome,
        items: items.map((i) => (i.outcome === "saved" ? { ...i, hash: null, current: null } : i)),
        changes: null,
      };
      const status = outcome === "not-found" ? 404 : outcome === "invalid" ? 422 : 409;
      return { status, body };
    }
    const changes = this.commit(candidate);
    const results = items.map((item) => {
      const entry = item.id ? this.entries.get(item.id) : undefined;
      return entry ? { ...item, hash: entry.hash, current: this.document(entry) } : item;
    });
    return { status: 200, body: { outcome: "saved", items: results, changes } };
  }

  /** A change made outside the editor (another window, the disk, the CLI): no hash check, source disk. */
  externalEdit(id: string, mutate: (json: Json) => void): ChangeSet {
    const existing = this.entries.get(id);
    if (!existing) return emptyChangeSet("disk");
    const json = clone(existing.json);
    mutate(json);
    const candidate = new Map(this.entries);
    candidate.set(id, this.entryFor(json, existing));
    return this.commit(candidate, "disk");
  }

  // ---------------------------------------------------------------- project and settings

  projectSettings(): ProjectSettings {
    return projectSettings(this.settingsJson);
  }

  settingsDocument(): SettingsDocument {
    return {
      settings: this.projectSettings(),
      path: `${PREFIX}maquettiste.json`,
      hash: this.settingsHash,
      json: clone(this.settingsJson) as SettingsDocument["json"],
    };
  }

  /** Icons stored by POST /api/project/branding/icon, by model-relative path (the file under .maquettiste/branding/). */
  readonly brandingIcons = new Map<string, { contentType: "image/svg+xml" | "image/png"; data: string; hash: string }>();

  /** The icon settings `branding.icon` names, when it was stored. */
  brandingIcon(): { contentType: "image/svg+xml" | "image/png"; data: string; hash: string } | null {
    const icon = this.projectSettings().branding?.icon;
    return (icon && this.brandingIcons.get(icon)) || null;
  }

  /** MQ8001 and MQ8002 as the engine reports them on a settings save. */
  private brandingDiagnostics(json: Json): SettingsSaveResult["diagnostics"] {
    const branding = projectSettings(json).branding;
    const out: SettingsSaveResult["diagnostics"] = [];
    const at = (rule: string, message: string, jsonPointer: string) =>
      out.push({ rule, severity: "error", message, elementId: null, filePath: `${PREFIX}maquettiste.json`, jsonPointer, line: null, column: null });
    for (const theme of ["light", "dark"] as const) {
      const color = branding?.colors[theme];
      if (color && !/^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(color))
        at("MQ8001", `The ${theme} primary color '${color}' is not a hex color (#rrggbb or #rgb).`, `/branding/colors/${theme}`);
    }
    const icon = branding?.icon;
    if (icon && !this.brandingIcons.has(icon)) at("MQ8002", `The icon file ${icon} does not exist in the model folder.`, "/branding/icon");
    return out;
  }

  saveSettings(json: Json, expectedHash: string): { status: number; body: SettingsSaveResult } {
    // outputs.allow[].commit (ignored since 0.5.5, MQ1010) is dropped from what is saved, as the engine's settings save does.
    if (json && typeof json === "object" && !Array.isArray(json)) {
      json = clone(json);
      stripRetiredCommit(json);
    }
    if (normalizeHash(expectedHash) !== this.settingsHash)
      return { status: 409, body: { outcome: "conflict", hash: this.settingsHash, current: this.settingsDocument(), diagnostics: [] } };
    const validate = schemaValidator("maquettiste");
    if (!validate(json))
      return {
        status: 422,
        body: {
          outcome: "invalid",
          hash: null,
          current: null,
          diagnostics: describeErrors(validate.errors).map((message, i) => ({
            rule: "MQ1002",
            severity: "error" as const,
            message,
            elementId: null,
            filePath: `${PREFIX}maquettiste.json`,
            jsonPointer: validate.errors?.[i]?.instancePath ?? "",
            line: null,
            column: null,
          })),
        },
      };
    const branding = this.brandingDiagnostics(json);
    if (branding.length) return { status: 422, body: { outcome: "invalid", hash: null, current: null, diagnostics: branding } };
    if (jsonEqual(json, this.settingsJson))
      return { status: 200, body: { outcome: "saved", hash: this.settingsHash, current: this.settingsDocument(), diagnostics: [] } };
    this.settingsJson = clone(json);
    this.settingsHash = sha256Hex(serialize(this.settingsJson));
    this.nonCanonical.delete(`${PREFIX}maquettiste.json`);
    this.changedPaths.add(`${PREFIX}maquettiste.json`);
    this.version++;
    this.options.onSettingsChanged?.(this.settingsHash);
    return { status: 200, body: { outcome: "saved", hash: this.settingsHash, current: this.settingsDocument(), diagnostics: [] } };
  }

  project(): ProjectInfo {
    const settings = this.projectSettings();
    return {
      name: settings.name ?? "project",
      projectKey: "0123456789abcdef",
      formatVersion: settings.formatVersion,
      engineVersion: "1.0.0",
      // The release and the workspace the top bar shows under the project name (MAQUETTISTE_WORKSPACE=billing).
      productVersion: "0.5.3",
      build: "0.5.3-mock",
      workspace: "billing",
      branch: "main",
      worktree: null,
      repository: null,
      mode: "local",
      settings,
      settingsHash: this.settingsHash,
      databases: this.index().filter((s) => s.kind === "database"),
      packs: clone(this.packs),
      packDiagnostics: [],
      extensions: clone(this.extensions),
      git: { branch: "main", head: "0000000", changedModelFiles: this.changedPaths.size },
      iconHash: this.brandingIcon()?.hash ?? null,
    };
  }

  docs(): Map<string, Json> {
    return new Map([...this.entries.values()].map((e) => [e.id, e.json]));
  }
}

export function normalizeHash(value: string): string {
  return value.replace(/^W\//, "").replace(/"/g, "").trim();
}

export function summary(e: Entry): ElementSummary {
  const json = e.json;
  return {
    id: e.id,
    kind: json.kind as ElementKind,
    name: typeof json.name === "string" ? json.name : "",
    // A package's index row holds its parent (the engine's ModelIndexer.PackageOf).
    package: typeof (json.kind === "package" ? json.parent : json.package) === "string" ? String(json.kind === "package" ? json.parent : json.package) : null,
    tags: (json.tags as string[] | undefined) ?? [],
    category: typeof json.category === "string" ? json.category : null,
    stereotypes: (json.stereotypes as string[] | undefined) ?? [],
    hash: e.hash,
    path: e.path,
    ...e5(json),
  };
}

/** The index format hashed into the index tag; the engine's ModelReads.IndexFormat. */
export const INDEX_FORMAT = "maquettiste-index/e9";

/** The E5 members of an index row, present only for the kinds that carry them (as the engine writes them). */
function e5(json: Json): Partial<ElementSummary> {
  const out: Partial<ElementSummary> = {};
  if (typeof json.displayName === "string") out.displayName = json.displayName;
  const kind = json.kind;
  if (DATABASE_MEMBER_KINDS.has(String(kind)) && typeof json.database === "string") out.database = json.database;
  if ((kind === "table" || kind === "mapping") && typeof json.entity === "string") out.entity = json.entity;
  if (kind === "entity" && typeof json.base === "string") out.base = json.base;
  if (kind === "seed") {
    if (typeof json.target === "string") out.target = json.target;
    out.rowCount = Array.isArray(json.rows) ? json.rows.length : 0;
  }
  if (kind === "reference-type") out.fieldCount = Array.isArray(json.attributes) ? json.attributes.length : 0;
  if (kind === "diagram") out.memberCount = Array.isArray(json.members) ? json.members.length : 0;
  // A process diagram names its process (the engine's index row since maquettiste-index/e9).
  if (kind === "diagram" && typeof json.process === "string") out.process = json.process;
  // Phase 3 (phase-3-design.md 2.1): a process's use, subject and state count; an actor's type; a scenario's process and steps.
  if (kind === "process") {
    out.use = json.use === "lifecycle" ? "lifecycle" : "orchestration";
    if (typeof json.subject === "string") out.subject = json.subject;
    const count = (states: unknown): number => (Array.isArray(states) ? (states as Json[]).reduce((n, s) => n + 1 + count(s.states), 0) : 0);
    out.stateCount = count(json.states);
  }
  if (kind === "actor" && (json.type === "person" || json.type === "role" || json.type === "external-system")) out.actorType = json.type;
  if (kind === "scenario") {
    if (typeof json.process === "string") out.process = json.process;
    out.stepCount = Array.isArray(json.steps) ? json.steps.length : 0;
  }
  if (kind === "relation" && Array.isArray(json.ends))
    out.ends = (json.ends as Json[]).map((end) => ({ entity: String(end.entity ?? ""), role: String(end.role ?? "") }));
  return out;
}

export function report(diagnostics: Diagnostic[]): ValidationReport {
  const errors = diagnostics.filter((d) => d.severity === "error").length;
  const warnings = diagnostics.filter((d) => d.severity === "warning").length;
  const infos = diagnostics.filter((d) => d.severity === "info").length;
  return { diagnostics, errors, warnings, infos, truncated: false, hasErrors: errors > 0 };
}

function removeAt(json: Json, pointer: string, isItem: boolean): void {
  const parts = pointer.split("/").slice(1);
  const last = parts.pop()!;
  let parent: unknown = json;
  for (const p of parts) parent = (parent as Record<string, unknown>)[p];
  if (Array.isArray(parent) && isItem) parent.splice(Number(last), 1);
  else if (parent && typeof parent === "object") delete (parent as Record<string, unknown>)[last];
}

const CONVENTION_KEYS = [
  "tableCase",
  "pluralTables",
  "columnCase",
  "tableName",
  "keyColumn",
  "foreignKeyColumn",
  "junctionTable",
  "childTable",
  "valueObjectColumn",
  "orderColumn",
  "discriminatorColumn",
  "primaryKeyName",
  "foreignKeyName",
  "uniqueName",
  "indexName",
  "checkName",
  "sequenceName",
  "defaultStringLength",
  "decimalPrecision",
  "decimalScale",
  "datetimePrecision",
  "enumStorage",
  "valueObjectStorage",
  "valueObjectCollectionStorage",
  "relationsWithAttributes",
  "inheritance",
  "comments",
] as const;

export function conventionsRecord(json: Json | undefined): ProjectSettings["conventions"] {
  const out: Record<string, unknown> = {};
  for (const key of CONVENTION_KEYS) out[key] = json?.[key] ?? null;
  return out as ProjectSettings["conventions"];
}

export function projectSettings(json: Json): ProjectSettings {
  const outputs = (json.outputs as Json | undefined) ?? {};
  const inflection = (json.inflection as Json | undefined) ?? {};
  const limits = (json.limits as Json | undefined) ?? {};
  const packs = (json.packs as Record<string, Json> | undefined) ?? {};
  const databases = (json.databases as Record<string, Json> | undefined) ?? {};
  return {
    $schema: (json.$schema as string | undefined) ?? null,
    formatVersion: (json.formatVersion as number | undefined) ?? 1,
    name: (json.name as string | undefined) ?? null,
    outputs: {
      allow: ((outputs.allow as Json[] | undefined) ?? []).map((a) => ({ path: String(a.path), commit: a.commit === true })),
      deny: (outputs.deny as string[] | undefined) ?? [],
    },
    handEdits: ((json.handEdits as string | undefined) ?? "fail") as ProjectSettings["handEdits"],
    formatters: ((json.formatters as Json[] | undefined) ?? []).map((f) => ({
      name: String(f.name),
      extensions: (f.extensions as string[]) ?? [],
      command: String(f.command),
      args: (f.args as string[] | undefined) ?? [],
      version: String(f.version),
      versionArgs: (f.versionArgs as string[] | undefined) ?? ["--version"],
      timeoutSeconds: (f.timeoutSeconds as number | undefined) ?? 30,
    })),
    conventions: conventionsRecord(json.conventions as Json | undefined),
    databases: Object.fromEntries(Object.entries(databases).map(([k, v]) => [k, conventionsRecord(v)])),
    typeMaps: (json.typeMaps as ProjectSettings["typeMaps"] | undefined) ?? {},
    inflection: {
      plurals: (inflection.plurals as Record<string, string> | undefined) ?? {},
      uncountable: (inflection.uncountable as string[] | undefined) ?? [],
    },
    packs: Object.fromEntries(
      Object.entries(packs)
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([k, v]) => [
          k,
          {
            enabled: v.enabled !== false,
            output: (v.output as string | undefined) ?? "",
            parameters: (v.parameters as Json | undefined) ?? {},
            handEdits: (v.handEdits as ProjectSettings["handEdits"] | undefined) ?? null,
          },
        ]),
    ),
    validation: { rules: ((json.validation as Json | undefined)?.rules as Record<string, string> | undefined) ?? {} },
    limits: {
      scriptTimeoutMs: (limits.scriptTimeoutMs as number | undefined) ?? 2000,
      scriptStatements: (limits.scriptStatements as number | undefined) ?? 5000000,
      scriptRecursion: (limits.scriptRecursion as number | undefined) ?? 256,
      scriptMemoryBytes: (limits.scriptMemoryBytes as number | undefined) ?? 67108864,
      templateLoopLimit: (limits.templateLoopLimit as number | undefined) ?? 1000000,
      templateRecursionLimit: (limits.templateRecursionLimit as number | undefined) ?? 64,
    },
    branding: (() => {
      const branding = (json.branding as Json | undefined) ?? {};
      const colors = (branding.colors as Json | undefined) ?? {};
      const text = (v: unknown) => (typeof v === "string" ? v : null);
      return { icon: text(branding.icon), colors: { light: text(colors.light), dark: text(colors.dark) } };
    })(),
    explorer: {
      folders: ((((json.explorer as Json | undefined) ?? {}).folders as Json[] | undefined) ?? []).map((f) => {
        const match = (f.match as Json | undefined) ?? {};
        const text = (v: unknown) => (typeof v === "string" ? v : null);
        return {
          label: String(f.label),
          icon: text(f.icon),
          kind: String(f.kind),
          match: { stereotype: text(match.stereotype), tag: text(match.tag), category: text(match.category) },
        };
      }),
    },
  };
}

export function extensionRecord(json: Json): ExtensionSchema {
  const appliesTo = (json.appliesTo as Json | undefined) ?? {};
  return {
    $schema: (json.$schema as string | undefined) ?? null,
    name: String(json.name),
    description: (json.description as string | undefined) ?? null,
    appliesTo: { kinds: (appliesTo.kinds as string[] | undefined) ?? [], stereotypes: (appliesTo.stereotypes as string[] | undefined) ?? [] },
    properties: (json.properties as Json | undefined) ?? {},
    required: (json.required as string[] | undefined) ?? [],
  };
}

export function packRecord(json: Json): PackManifest {
  return {
    $schema: (json.$schema as string | undefined) ?? null,
    name: String(json.name),
    version: String(json.version),
    engine: String(json.engine),
    parameterSchema: (json.parameterSchema as Json | undefined) ?? null,
    description: (json.description as string | undefined) ?? null,
    parameters: (json.parameters as Json | undefined) ?? {},
    scripts: (json.scripts as string[] | undefined) ?? [],
    usesSchemaDiff: json.usesSchemaDiff === true,
    units: ((json.units as Json[] | undefined) ?? []).map((u) => ({
      id: String(u.id),
      template: String(u.template),
      for: String(u.for),
      where: (u.where as Json | undefined) ?? null,
      output: (u.output as string | undefined) ?? null,
      mode: ((u.mode as string | undefined) ?? "overwrite") as PackManifest["units"][number]["mode"],
      blockComment: (u.blockComment as string | undefined) ?? "#",
      createFile: u.createFile === true,
      formatter: (u.formatter as string | undefined) ?? null,
      delimiters: (u.delimiters as { open: string; close: string } | undefined) ?? null,
      companion: (u.companion as { template: string; output: string } | undefined) ?? null,
      transforms: (u.transforms as string[] | undefined) ?? [],
    })),
  };
}

/** `set-initial`: the process or the compound state `id` (a state of some process) starts in its direct child `target`. */
function setInitialDocs(entries: Map<string, { json: unknown }>, id: string, target: string | undefined): Json[] | string {
  const own = entries.get(id)?.json as Json | undefined;
  const find = (states: Json[] | undefined): Json | null => {
    for (const state of states ?? []) {
      if (state.id === id) return state;
      const inner = find(state.states as Json[] | undefined);
      if (inner) return inner;
    }
    return null;
  };
  for (const entry of entries.values()) {
    const json = entry.json as Json;
    if (json.kind !== "process" || (own && json.id !== id)) continue;
    const next = clone(json);
    const node = own ? next : find(next.states as Json[] | undefined);
    if (!node) continue;
    if (!((node.states as Json[] | undefined) ?? []).some((s) => s.id === target)) return `${String(target)} is not a direct child of ${id}.`;
    node.initial = target;
    return [next];
  }
  return `No process or compound state ${id}.`;
}

/** `set-lifecycle`: binds entity `id` and process `target` in one change and unbinds their previous partners. */
function setLifecycleDocs(entries: Map<string, { json: unknown }>, id: string, target: string | undefined): Json[] | string {
  const get = (key: unknown) => (typeof key === "string" ? (entries.get(key)?.json as Json | undefined) : undefined);
  const entity = get(id);
  if (entity?.kind !== "entity") return `No entity ${id}.`;
  const process = target ? get(target) : undefined;
  if (target && process?.kind !== "process") return `No process ${target}.`;
  const changed = new Map<string, Json>();
  const edit = (json: Json) => changed.get(String(json.id)) ?? changed.set(String(json.id), clone(json)).get(String(json.id))!;
  const orchestrate = (json: Json) => {
    const next = edit(json);
    next.use = "orchestration";
    delete next.subject;
    delete next.boundAttribute;
  };
  const previous = get(entity.lifecycle);
  if (previous && previous.id !== target) orchestrate(previous);
  if (process) {
    const partner = get(process.subject);
    if (partner && partner.id !== id && partner.lifecycle === target) delete edit(partner).lifecycle;
    const next = edit(process);
    next.use = "lifecycle";
    next.subject = id;
    edit(entity).lifecycle = target;
  } else delete edit(entity).lifecycle;
  return [...changed.values()];
}
