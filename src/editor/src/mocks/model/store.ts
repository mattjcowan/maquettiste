// MockModel: the mock backend's in-memory model (phase2-design.md 4.4), seeded from the billing
// fixture. It follows the engine's ModelStore contract closely enough for the editor: index, get
// (a sub-element id returns its owner), create, save with If-Match and 409, delete with reference
// checks and remove-references, atomic batches, references, validation and settings. Hashes are
// SHA-256 of the mock's own JSON text, not the engine's canonical bytes; nothing may compare them
// with real hashes.
import type {
  BatchResult,
  BatchParseResult,
  ChangeSet,
  Diagnostic,
  ElementDocument,
  ElementKind,
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
import { typedElement } from "./typed";
import { diagnosticKey, validateModel, type ModelEntry } from "./validate";
import { applyCase } from "./physical";

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

  constructor(
    seed: Seed,
    private readonly options: MockModelOptions,
  ) {
    for (const file of seed.files) {
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

  /** The entry that holds an id (an element or one of its sub-elements). */
  owner(id: string): Entry | undefined {
    const direct = this.entries.get(id);
    if (direct) return direct;
    for (const entry of this.entries.values()) if (subElementIds(entry.json).includes(id)) return entry;
    return undefined;
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
    const ids = new Set([target.id, ...subElementIds(target.json)]);
    if (target.id !== id) {
      ids.clear();
      ids.add(id);
    }
    const out: ReferenceInfo[] = [];
    for (const entry of this.sortedEntries()) {
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

  diagnosticsOf(entries: Iterable<Entry | ModelEntry>): Diagnostic[] {
    const list = [...entries].map((e) => ({ id: e.id, path: e.path, json: e.json }));
    const rules = ((this.settingsJson.validation as Json | undefined)?.rules as Record<string, string> | undefined) ?? {};
    return validateModel({ entries: list, extensions: this.extensions, rules });
  }

  validate(scope: ValidationScope = {}): ValidationReport {
    let diagnostics = this.diagnosticsOf(this.entries.values());
    if (scope.elementIds) {
      const wanted = new Set<string>();
      for (const id of scope.elementIds) {
        const owner = this.owner(id);
        if (owner) wanted.add(owner.id);
      }
      if (scope.includeReferrers !== false)
        for (const entry of this.entries.values()) if (referencesOf(entry.json).some((r) => wanted.has(this.owner(r.toId)?.id ?? r.toId))) wanted.add(entry.id);
      diagnostics = diagnostics.filter((d) => d.elementId !== null && wanted.has(d.elementId));
    }
    return report(diagnostics);
  }

  hasErrors(): boolean {
    return this.diagnosticsOf(this.entries.values()).some((d) => d.severity === "error");
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
    } else if (kind === "view" || kind === "sequence") folder = `model/databases/${dbFolder}/${kind}s`;
    else if (kind === "tag-vocabulary") {
      folder = "model/vocabularies";
      file = "tags.json";
    } else if (kind === "category-tree") {
      folder = "model/vocabularies";
      file = "categories.json";
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
  private check(candidate: Map<string, Entry>, ids: string[]): { introduced: Diagnostic[]; all: Diagnostic[] } {
    const before = new Set(
      this.diagnosticsOf(this.entries.values())
        .filter((d) => d.severity === "error")
        .map(diagnosticKey),
    );
    const after = this.diagnosticsOf(candidate.values());
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

  delete(id: string, expectedHash: string, resolution: "refuse" | "remove-references" = "refuse"): SaveResult {
    const existing = this.entries.get(id);
    if (!existing) return this.notFound(id);
    if (normalizeHash(expectedHash) !== existing.hash)
      return { outcome: "conflict", id, hash: existing.hash, current: this.document(existing), diagnostics: [], referrers: [], changes: null };
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
    const operations = (body as { operations: { op: string; id?: string; expectedHash?: string; element?: Json }[] }).operations;
    const candidate = new Map(this.entries);
    const items: SaveResult[] = [];
    const deleting = new Set(operations.filter((o) => o.op === "delete" && o.id).map((o) => o.id!));
    let failure: SaveResult["outcome"] | null = null;
    const fail = (result: SaveResult) => {
      items.push(result);
      failure ??= result.outcome;
    };
    for (const op of operations) {
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

  saveSettings(json: Json, expectedHash: string): { status: number; body: SettingsSaveResult } {
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
    if (jsonEqual(json, this.settingsJson))
      return { status: 200, body: { outcome: "saved", hash: this.settingsHash, current: this.settingsDocument(), diagnostics: [] } };
    this.settingsJson = clone(json);
    this.settingsHash = sha256Hex(serialize(this.settingsJson));
    this.changedPaths.add(`${PREFIX}maquettiste.json`);
    this.version++;
    this.options.onSettingsChanged?.(this.settingsHash);
    return { status: 200, body: { outcome: "saved", hash: this.settingsHash, current: this.settingsDocument(), diagnostics: [] } };
  }

  project(): ProjectInfo {
    const settings = this.projectSettings();
    return {
      name: settings.name ?? "project",
      formatVersion: settings.formatVersion,
      engineVersion: "1.0.0",
      mode: "local",
      settings,
      settingsHash: this.settingsHash,
      databases: this.index().filter((s) => s.kind === "database"),
      packs: clone(this.packs),
      packDiagnostics: [],
      extensions: clone(this.extensions),
      git: { branch: "main", head: "0000000", changedModelFiles: this.changedPaths.size },
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
    package: typeof json.package === "string" ? json.package : null,
    tags: (json.tags as string[] | undefined) ?? [],
    category: typeof json.category === "string" ? json.category : null,
    stereotypes: (json.stereotypes as string[] | undefined) ?? [],
    hash: e.hash,
    path: e.path,
  };
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
      formatter: (u.formatter as string | undefined) ?? null,
      delimiters: (u.delimiters as { open: string; close: string } | undefined) ?? null,
      companion: (u.companion as { template: string; output: string } | undefined) ?? null,
      transforms: (u.transforms as string[] | undefined) ?? [],
    })),
  };
}
