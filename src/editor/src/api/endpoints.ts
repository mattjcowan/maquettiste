// One function per operation of docs/api/openapi.yaml that the SPA calls. Each returns the
// contract's record; engine outcomes (SaveResult, BatchResult, SettingsSaveResult) come back at any
// status, and every other failure is a thrown ApiProblem (client.ts).
import { api, ApiProblem, etagHash } from "./client";
import type {
  BatchParseResult,
  BatchRequest,
  BatchResult,
  DatabaseTablesResult,
  DatabaseTableResult,
  DatabaseViewResult,
  ElementReadResult,
  ElementDocument,
  ElementSummary,
  GenerationPlan,
  GenerationRequest,
  JobHistoryCleared,
  JobInfo,
  ModelFormatResult,
  ModelJson,
  NewModelDocument,
  PresenceReport,
  PreviewRequest,
  PreviewResult,
  ProjectInfo,
  ReferenceInfo,
  SaveResult,
  DeletePlan,
  DeleteResolution,
  SessionInfo,
  LocalizationStatus,
  SettingsDocument,
  TranslationPage,
  TranslationWrite,
  TranslationWriteResult,
  SettingsJson,
  BrandingIconSaved,
  BrandingIconUpload,
  SettingsSaveResult,
  ValidationReport,
  ValidationScope,
  EditorHealth,
  ImportPreview,
  ReferenceTypeUsage,
  RuleCatalogEntry,
  PackRenameResult,
  QuerySqlResult,
  BindingSqlResult,
  MaterializeBody,
  MaterializePlan,
  MaterializeStatus,
  Problem,
  SnapshotComparison,
  SnapshotCreateBody,
  SnapshotElementDiff,
  SnapshotImportResult,
  SnapshotInfo,
  SnapshotPatchBody,
  SnapshotRestoreResult,
} from "./types";

function must<T>(data: T | undefined, response: Response): T {
  if (data === undefined) throw new ApiProblem(response.status, null, `Empty answer (HTTP ${response.status})`);
  return data;
}

/** An engine record answered at a non-2xx status (409, 422, 404) is still the record. */
function record<T>(data: T | undefined, error: unknown, response: Response): T {
  if (data !== undefined) return data;
  if (error && typeof error === "object") return error as T;
  throw new ApiProblem(response.status, null, `Empty answer (HTTP ${response.status})`);
}

export async function getEditorHealth(): Promise<EditorHealth> {
  const { data, response } = await api().GET("/api/health");
  return must(data, response);
}

export async function getSession(): Promise<SessionInfo> {
  const { data, response } = await api().GET("/api/session");
  return must(data, response);
}

export async function getProject(): Promise<ProjectInfo> {
  const { data, response } = await api().GET("/api/project");
  return must(data, response);
}

export async function getSettings(): Promise<SettingsDocument> {
  const { data, response } = await api().GET("/api/project/settings");
  return must(data, response);
}

export async function listValidationRules(): Promise<RuleCatalogEntry[]> {
  const { data, response } = await api().GET("/api/validation/rules");
  return must(data, response);
}

export async function saveSettings(json: SettingsJson, hash: string): Promise<SettingsSaveResult> {
  const { data, error, response } = await api().PUT("/api/project/settings", {
    params: { header: { "If-Match": `"${hash}"` } },
    body: json,
  });
  return record<SettingsSaveResult>(data, error, response);
}

/** The project icon (anonymous; the ETag is ProjectInfo.iconHash, so add ?v=<hash> to refresh it). */
export const BRANDING_ICON_URL = "/api/project/branding/icon";

export async function uploadBrandingIcon(body: BrandingIconUpload): Promise<BrandingIconSaved> {
  const { data, response } = await api().POST("/api/project/branding/icon", { body });
  return must(data, response);
}

export async function getModelIndex(): Promise<ElementSummary[]> {
  const { data, response } = await api().GET("/api/model/index");
  return must(data, response);
}

/**
 * A conditional index read (E5e): `notModified` on 304, else the body as text with its ETag, and the bytes the text was
 * decoded from when the transport gave bytes: the search worker takes them as a transferable (no copy on the main thread)
 * and parses them itself (explorer-redesign.md 4.5).
 */
export type IndexAnswer = { notModified: true; etag: string | null } | { notModified: false; etag: string | null; text: string; bytes?: ArrayBuffer };

/** GET /api/model/index with `If-None-Match` when an ETag is known; the body is parsed by the caller. */
export async function getModelIndexText(etag: string | null, locale?: string | null): Promise<IndexAnswer> {
  const { data, response } = await api().GET("/api/model/index", {
    params: { ...(etag ? { header: { "If-None-Match": etag } } : {}), ...(locale ? { query: { locale } } : {}) },
    parseAs: "arrayBuffer",
  });
  const tag = response.headers.get("ETag");
  if (response.status === 304) return { notModified: true, etag: tag ?? etag };
  const bytes = must(data as ArrayBuffer | undefined, response);
  return { notModified: false, etag: tag, text: new TextDecoder().decode(bytes), bytes };
}

export async function getElement(id: string): Promise<ElementDocument> {
  const { data, response } = await api().GET("/api/model/elements/{id}", { params: { path: { id } } });
  return must(data, response);
}

/** The most ids one `readElements` call takes (E5b). */
export const MAX_READ_IDS = 200;

/** E5b: up to 200 element or sub-element documents from one snapshot; unknown ids come back in `missing`. */
export async function readElements(ids: string[]): Promise<ElementReadResult> {
  const { data, response } = await api().POST("/api/model/elements/read", { body: { ids } });
  return must(data, response);
}

export async function createElement(json: NewModelDocument | ModelJson): Promise<SaveResult> {
  const { data, error, response } = await api().POST("/api/model/elements", { body: json as NewModelDocument });
  return record<SaveResult>(data, error, response);
}

export async function saveElement(id: string, json: ModelJson, hash: string): Promise<SaveResult> {
  const { data, error, response } = await api().PUT("/api/model/elements/{id}", {
    params: { path: { id }, header: { "If-Match": `"${hash}"` } },
    body: json,
  });
  return record<SaveResult>(data, error, response);
}

export async function deleteElement(id: string, hash: string, resolution: DeleteResolution = "refuse"): Promise<SaveResult> {
  const { data, error, response } = await api().DELETE("/api/model/elements/{id}", {
    params: { path: { id }, header: { "If-Match": `"${hash}"` }, query: { resolution } },
  });
  return record<SaveResult>(data, error, response);
}

/** What deleting `id` with `resolution` would do; nothing is written. */
export async function getDeletePlan(id: string, resolution: DeleteResolution = "delete-dependents"): Promise<DeletePlan> {
  const { data, response } = await api().GET("/api/model/elements/{id}/delete-plan", { params: { path: { id }, query: { resolution } } });
  return must(data, response);
}

/** The combined plan of deleting `ids` in one batch (the explorer's bulk delete). */
export async function getBulkDeletePlan(ids: string[], resolution: DeleteResolution = "delete-dependents"): Promise<DeletePlan> {
  const { data, response } = await api().POST("/api/model/delete-plan", { body: { ids, resolution } });
  return must(data, response);
}

export async function applyBatch(batch: BatchRequest): Promise<BatchResult | BatchParseResult> {
  const { data, error, response } = await api().POST("/api/model/batch", { body: batch });
  return record<BatchResult | BatchParseResult>(data, error, response);
}

export function isBatchResult(value: BatchResult | BatchParseResult): value is BatchResult {
  return "outcome" in value;
}

export async function getReferences(id: string): Promise<ReferenceInfo[]> {
  const { data, response } = await api().GET("/api/model/references/{id}", { params: { path: { id } } });
  return must(data, response);
}

export async function validate(scope: ValidationScope = {}): Promise<ValidationReport> {
  const { data, response } = await api().POST("/api/validate", { body: scope });
  return must(data, response);
}

/** Rewrites model files in canonical form (`maquettiste format`); no paths: every model file and maquettiste.json. */
export async function formatModel(paths?: string[]): Promise<ModelFormatResult> {
  const { data, response } = await api().POST("/api/model/format", { body: paths ? { paths } : {} });
  return must(data, response);
}

export async function getDiagram(id: string): Promise<ElementDocument> {
  const { data, response } = await api().GET("/api/diagrams/{id}", { params: { path: { id } } });
  return must(data, response);
}

export async function saveDiagram(id: string, json: ModelJson, hash: string): Promise<SaveResult> {
  const { data, error, response } = await api().PUT("/api/diagrams/{id}", {
    params: { path: { id }, header: { "If-Match": `"${hash}"` } },
    body: json as never,
  });
  return record<SaveResult>(data, error, response);
}

export async function getDatabaseView(id: string): Promise<DatabaseViewResult> {
  const { data, response } = await api().GET("/api/databases/{id}/view", { params: { path: { id } } });
  return must(data, response);
}

/** The SQL of one query for its database's dialect or the one named: its statement and one per collection, with what stops it. */
export async function getQuerySql(id: string, dialect?: string | null): Promise<QuerySqlResult> {
  const { data, response } = await api().GET("/api/model/queries/{id}/sql", { params: { path: { id }, query: dialect ? { dialect } : {} } });
  return must(data, response);
}

/** The five statements of an entity's binding for its database's dialect or the one named (erratum E43). */
export async function getBindingSql(entityId: string, bindingId: string, dialect?: string | null): Promise<BindingSqlResult> {
  const { data, response } = await api().GET("/api/model/entities/{id}/bindings/{bindingId}/sql", {
    params: { path: { id: entityId, bindingId }, query: dialect ? { dialect } : {} },
  });
  return must(data, response);
}

/** What can be materialized in a database: the entities with no binding to it, its tables and views with their binders. */
export async function getMaterializeStatus(databaseId: string): Promise<MaterializeStatus> {
  const { data, response } = await api().GET("/api/model/databases/{id}/materialize", { params: { path: { id: databaseId } } });
  return must(data, response);
}

/** What a materialize operation would write, validated, writing nothing. */
export async function previewMaterialize(databaseId: string, body: MaterializeBody): Promise<MaterializePlan> {
  const { data, response } = await api().POST("/api/model/databases/{id}/materialize/preview", { params: { path: { id: databaseId } }, body });
  return must(data, response);
}

/** E5c: the table list of one database without columns; `partial` on a model with errors. */
export async function getDatabaseTables(id: string): Promise<DatabaseTablesResult> {
  const { data, response } = await api().GET("/api/databases/{id}/tables", { params: { path: { id } } });
  return must(data, response);
}

/** E5f: one table's detail (columns with `attributeId`, keys, foreign keys, unique constraints, indexes). */
export async function getDatabaseTable(id: string, key: string): Promise<DatabaseTableResult> {
  const { data, response } = await api().GET("/api/databases/{id}/tables/{key}", { params: { path: { id, key } } });
  return must(data, response);
}

export async function startPlan(request: GenerationRequest): Promise<JobInfo> {
  const { data, response } = await api().POST("/api/generate/plan", { body: request });
  return must(data, response);
}

export async function getPlan(id: string, units = false): Promise<GenerationPlan> {
  const { data, response } = await api().GET("/api/generate/plan/{id}", {
    params: { path: { id }, query: units ? { units: true } : {} },
  });
  return must(data, response);
}

/** One unit of a plan with its reason, causes and grouped read keys: the answer to "Why not?" (generation-ui.md 4.3). */
export async function getPlanUnit(id: string, key: string): Promise<import("./types").PlanUnitDetail> {
  const { data, response } = await api().GET("/api/generate/plan/{id}/unit", { params: { path: { id }, query: { key } } });
  return must(data, response);
}

/** Why a unit does or does not render for an element, planned or not (generation-ui.md 4.3). */
export async function explainUnit(request: import("./types").ExplainRequest): Promise<import("./types").ExplainResult> {
  const { data, response } = await api().POST("/api/generate/explain", { body: request });
  return must(data, response);
}

export async function getPlanDiff(id: string, path: string): Promise<string> {
  const { data, response } = await api().GET("/api/generate/plan/{id}/diff", {
    params: { path: { id }, query: { path } },
    parseAs: "text",
  });
  return must(data as string | undefined, response);
}

export async function startApply(planId: string): Promise<JobInfo> {
  const { data, response } = await api().POST("/api/generate/apply", { body: { planId } });
  return must(data, response);
}

export async function listJobs(): Promise<JobInfo[]> {
  const { data, response } = await api().GET("/api/jobs");
  return must(data, response);
}

export async function getJob(id: string): Promise<JobInfo> {
  const { data, response } = await api().GET("/api/jobs/{id}", { params: { path: { id } } });
  return must(data, response);
}

/** Clears the run history: every finished job and every stored plan no queued or running job names (maintainer). */
export async function clearJobHistory(): Promise<JobHistoryCleared> {
  const { data, response } = await api().DELETE("/api/jobs");
  return must(data, response);
}

export async function cancelJob(id: string): Promise<JobInfo> {
  const { data, response } = await api().DELETE("/api/jobs/{id}", { params: { path: { id } } });
  return must(data, response);
}

/** This tab's live-preview key: the server keeps one request of each kind in flight per key (generation-ui.md 5.2). */
export const LIVE_CLIENT = `tab-${Math.random().toString(36).slice(2, 14)}`;

const clientHeader = (client?: string) => (client ? { header: { "X-Maquettiste-Client": client } } : {});

export async function previewTemplate(request: PreviewRequest, signal?: AbortSignal, client?: string): Promise<PreviewResult> {
  const { data, response } = await api().POST("/api/templates/preview", {
    params: clientHeader(client),
    body: request,
    signal,
  });
  return must(data, response);
}

// Pack authoring (generation-ui.md 3 and 5.1): the pack's own files under .maquettiste/templates/<pack>/.
type Schemas = import("./schema").components["schemas"];
export type PackSummary = Schemas["PackSummary"];
export type PackDocument = Schemas["PackDocument"];
export type PackUnit = Schemas["PackUnit"];
export type PackWriteResult = Schemas["PackWriteResult"];
export type PackOutputs = Schemas["PackOutputs"];
export type PathsRequest = Schemas["PathsRequest"];
export type UnitPathsResult = Schemas["UnitPathsResult"];

export async function listPacks(): Promise<PackSummary[]> {
  const { data, response } = await api().GET("/api/packs");
  return must(data, response).packs;
}

export async function getPack(pack: string): Promise<PackDocument> {
  const { data, response } = await api().GET("/api/packs/{pack}", { params: { path: { pack } } });
  return must(data, response);
}

/** Creates `.maquettiste/templates/<name>/`, empty or copied from a pack of this project. */
export async function createPack(name: string, from: string): Promise<PackWriteResult> {
  const { data, error, response } = await api().POST("/api/packs", { body: { name, from } });
  return record<PackWriteResult>(data as PackWriteResult | undefined, error, response);
}

/** Saves the whole pack.json document (every member it loaded kept), written in canonical form. */
export async function savePack(pack: string, document: Record<string, unknown>, hash: string): Promise<PackWriteResult> {
  const { data, error, response } = await api().PUT("/api/packs/{pack}", {
    params: { path: { pack }, header: { "If-Match": `"${hash}"` } },
    body: document as never,
  });
  return record<PackWriteResult>(data, error, response);
}

export type PackRemoveResult = Schemas["PackRemoveResult"];

/**
 * Removes a pack with the pack.json hash read: its folder, its `packs.<pack>` settings entry and its manifests. The files
 * it generated stay on disk, untracked (`untracked` lists them); 409 `conflict` when pack.json changed.
 */
export async function deletePack(pack: string, hash: string): Promise<PackRemoveResult> {
  const { data, error, response } = await api().DELETE("/api/packs/{pack}", {
    params: { path: { pack }, header: { "If-Match": `"${hash}"` } },
  });
  return record<PackRemoveResult>(data as PackRemoveResult | undefined, error, response);
}

/**
 * Renames a pack with the pack.json hash read: its folder, its `packs.<pack>` settings entry, its manifests and unit
 * states move to `name` together, so the files it generated stay tracked (`tracked` lists them); 409 `conflict` when
 * pack.json changed, 422 `invalid` when the name is refused. `dryRun` checks and answers what would move (with `hints`, the
 * elements whose generation hints name the pack), writing nothing.
 */
export async function renamePack(pack: string, name: string, hash: string, dryRun = false): Promise<PackRenameResult> {
  const { data, error, response } = await api().POST("/api/packs/{pack}/rename", {
    params: { path: { pack }, header: { "If-Match": `"${hash}"` } },
    body: { name, dryRun },
  });
  return record<PackRenameResult>(data as PackRenameResult | undefined, error, response);
}

export type PackFileContent = Schemas["PackFileContent"];

/** One pack file's text and hash (the If-Match of its next save). */
export async function getPackFile(pack: string, path: string): Promise<PackFileContent> {
  const { data, response } = await api().GET("/api/packs/{pack}/file", { params: { path: { pack }, query: { path } } });
  return must(data, response);
}

/** Writes one pack file with the hash read (409 `conflict` carries the disk hash and text); a template that fails to parse is saved with its MQ6003 diagnostics. */
export async function savePackFile(pack: string, path: string, text: string, hash: string | null): Promise<PackWriteResult> {
  const { data, error, response } = await api().PUT("/api/packs/{pack}/file", {
    // No hash: the file is gone on disk and this write creates it again.
    params: { path: { pack }, query: { path }, header: hash ? { "If-Match": `"${hash}"` } : { "If-None-Match": "*" } },
    body: { text },
  });
  return record<PackWriteResult>(data, error, response);
}

/** Deletes one pack file with the hash read; 409 `referenced` (with the users in `diagnostics`) while a unit names it or a template includes it. */
export async function deletePackFile(pack: string, path: string, hash: string): Promise<PackWriteResult> {
  const { data, error, response } = await api().DELETE("/api/packs/{pack}/file", {
    params: { path: { pack }, query: { path }, header: { "If-Match": `"${hash}"` } },
  });
  return record<PackWriteResult>(data as PackWriteResult | undefined, error, response);
}

/** Moves (renames) one pack file; with `packHash` the units naming it are rewritten in the same change (`updateUnits`). */
export async function movePackFile(pack: string, from: string, to: string, hash: string, packHash: string | null): Promise<PackWriteResult> {
  const { data, error, response } = await api().POST("/api/packs/{pack}/file/move", {
    params: { path: { pack }, header: { "If-Match": `"${hash}"` } },
    body: packHash ? { from, to, updateUnits: true, expectedPackHash: packHash } : { from, to },
  });
  return record<PackWriteResult>(data as PackWriteResult | undefined, error, response);
}

export type ExtensionFileList = Schemas["ExtensionFileList"];
export type ExtensionFileInfo = Schemas["ExtensionFileInfo"];
export type ExtensionFileContent = Schemas["ExtensionFileContent"];
export type ExtensionWriteResult = Schemas["ExtensionWriteResult"];

/** The model's extension files: custom property schemas (`<name>.json`) and script rules (`rules/<name>.js`). */
export async function listExtensionFiles(): Promise<ExtensionFileList> {
  const { data, response } = await api().GET("/api/extensions/files");
  return must(data, response);
}

/** One extension file's text and hash (the If-Match of its next save). */
export async function getExtensionFile(path: string): Promise<ExtensionFileContent> {
  const { data, response } = await api().GET("/api/extensions/file", { params: { query: { path } } });
  return must(data, response);
}

/**
 * Writes one extension file with the hash read, or creates it (`hash` null). A schema that is not valid is 422 `invalid` with
 * nothing written; a valid one comes back in canonical form (`text`). A rule script is saved and its syntax check returned.
 */
export async function saveExtensionFile(path: string, text: string, hash: string | null): Promise<ExtensionWriteResult> {
  const { data, error, response } = await api().PUT("/api/extensions/file", {
    params: { query: { path }, header: hash ? { "If-Match": `"${hash}"` } : { "If-None-Match": "*" } },
    body: { text },
  });
  return record<ExtensionWriteResult>(data, error, response);
}

/** Deletes one extension file with the hash read. */
export async function deleteExtensionFile(path: string, hash: string): Promise<ExtensionWriteResult> {
  const { data, error, response } = await api().DELETE("/api/extensions/file", {
    params: { query: { path }, header: { "If-Match": `"${hash}"` } },
  });
  return record<ExtensionWriteResult>(data as ExtensionWriteResult | undefined, error, response);
}

/** Renames one extension file within its kind. */
export async function moveExtensionFile(from: string, to: string, hash: string): Promise<ExtensionWriteResult> {
  const { data, error, response } = await api().POST("/api/extensions/file/move", {
    params: { header: { "If-Match": `"${hash}"` } },
    body: { from, to },
  });
  return record<ExtensionWriteResult>(data as ExtensionWriteResult | undefined, error, response);
}

export type TemplateContextResult = Schemas["TemplateContextResult"];

/** Completion data for a unit's templates: variables, member lists, helpers and the pack's registrations. */
export async function getTemplateContext(pack: string, unit: string): Promise<TemplateContextResult> {
  const { data, response } = await api().GET("/api/templates/context", { params: { query: { pack, unit } } });
  return must(data, response);
}

export async function getPackOutputs(pack: string): Promise<PackOutputs> {
  const { data, response } = await api().GET("/api/packs/{pack}/outputs", { params: { path: { pack } } });
  return must(data, response);
}

/** Replaces `packs.<pack>` (enabled, output, parameters) in maquettiste.json with the settings hash. */
export async function savePackSettings(pack: string, section: Record<string, unknown>, hash: string): Promise<SettingsSaveResult> {
  const { data, error, response } = await api().PUT("/api/project/settings/packs/{pack}", {
    params: { path: { pack }, header: { "If-Match": `"${hash}"` } },
    body: section,
  });
  return record<SettingsSaveResult>(data, error, response);
}

export async function unitPaths(request: PathsRequest, signal?: AbortSignal, client?: string): Promise<UnitPathsResult> {
  const { data, response } = await api().POST("/api/templates/paths", {
    params: clientHeader(client),
    body: request,
    signal,
  });
  return must(data, response);
}

export async function reportPresence(report: PresenceReport): Promise<void> {
  await api().PUT("/api/presence", { body: report });
}

export { etagHash };

/** Ends the session (the account menu's Sign out). */
export async function signOut(): Promise<void> {
  await api().DELETE("/api/session");
}

/** A seed's rows as CSV (reference-types-seeds-localization.md 2.3): `@id` first, then the columns by name. */
export async function exportSeedCsv(id: string, options: { bom?: boolean; locale?: string[] } = {}): Promise<string> {
  const { data, response } = await api().GET("/api/seeds/{id}/csv", {
    params: { path: { id }, query: { bom: options.bom, locale: options.locale } },
    parseAs: "text",
  });
  return must(data as string | undefined, response);
}

/**
 * Previews (`dryRun`, the default) or applies a CSV import into a seed. Applying sends the hash the preview was read
 * with, so a seed changed since answers 409 with nothing written; 409 and 422 come back as the preview record.
 */
export async function importSeedCsv(
  id: string,
  content: string,
  options: { mode?: "merge" | "replace"; dryRun?: boolean; hash?: string } = {},
): Promise<ImportPreview & { status: number }> {
  const { data, error, response } = await api().POST("/api/seeds/{id}/csv", {
    params: { path: { id }, query: { mode: options.mode ?? "merge", dryRun: options.dryRun ?? true } },
    headers: options.hash ? { "If-Match": `"${options.hash}"` } : undefined,
    body: { content },
  });
  return { ...record<ImportPreview>(data, error, response), status: response.status };
}

/** Several seed CSV imports: previews every file, or applies the rows of all or none (409 stale, 422 invalid), then their translations. */
export async function importSeedsCsv(
  files: readonly { seed: string; content: string; hash?: string }[],
  options: { mode?: "merge" | "replace"; dryRun?: boolean } = {},
): Promise<{ items: ImportPreview[]; status: number }> {
  const { data, error, response } = await api().POST("/api/seeds/csv", {
    params: { query: { mode: options.mode ?? "merge", dryRun: options.dryRun ?? true } },
    body: { files: files.map((f) => ({ seed: f.seed, content: f.content, ...(f.hash ? { hash: f.hash } : {}) })) },
  });
  return { ...record<{ items: ImportPreview[] }>(data, error, response), status: response.status };
}

/** Every attribute typed by a reference type, with its owner and the effective storage per database. */
export async function getReferenceTypeUsage(id: string): Promise<ReferenceTypeUsage> {
  const { data, response } = await api().GET("/api/reference-types/{id}/usage", { params: { path: { id } } });
  return must(data, response);
}

/** Localization settings and completeness per locale and shard (reference-types-seeds-localization.md 3.9). */
export async function getLocalization(): Promise<LocalizationStatus> {
  const { data, response } = await api().GET("/api/localization");
  return must(data, response);
}

/** One locale's entries: by owner, by shard, or the paged queue of those that need work (`missing`). */
export async function getTranslations(
  locale: string,
  query: { owner?: string; shard?: string; missing?: boolean; cursor?: string | null } = {},
): Promise<TranslationPage> {
  const { data, response } = await api().GET("/api/localization/{locale}/entries", {
    params: { path: { locale }, query: { owner: query.owner, shard: query.shard, missing: query.missing, cursor: query.cursor ?? undefined } },
  });
  return must(data, response);
}

/** Writes, removes (`value: null`) or confirms translations; 409 (a shard changed) and 422 come back as the record. */
export async function saveTranslations(locale: string, body: TranslationWrite): Promise<TranslationWriteResult & { status: number }> {
  const { data, error, response } = await api().PUT("/api/localization/{locale}/entries", { params: { path: { locale } }, body });
  return { ...record<TranslationWriteResult>(data, error, response), status: response.status };
}

// ---------------------------------------------------------------- model snapshots (docs/engineering/snapshots.md)

/** Every snapshot, newest first (each row from the archive's metadata; nothing is loaded). */
export async function listSnapshots(): Promise<SnapshotInfo[]> {
  const { data, response } = await api().GET("/api/snapshots");
  return must(data, response);
}

/** Takes a snapshot of the working model; the packs only when `includePacks` (editor). */
export async function createSnapshot(body: SnapshotCreateBody): Promise<SnapshotInfo> {
  const { data, response } = await api().POST("/api/snapshots", { body });
  return must(data, response);
}

/** Renames, describes or publishes a snapshot; the id and the documents stay (editor). */
export async function updateSnapshot(id: string, body: SnapshotPatchBody): Promise<SnapshotInfo> {
  const { data, response } = await api().PATCH("/api/snapshots/{id}", { params: { path: { id } }, body });
  return must(data, response);
}

/** Deletes a snapshot (maintainer). */
export async function deleteSnapshot(id: string): Promise<void> {
  await api().DELETE("/api/snapshots/{id}", { params: { path: { id } } });
}

/** One page of what differs between `from` and `to` (snapshot ids or `working`), elements by kind, name and id. */
export async function compareSnapshots(from: string, to: string, offset = 0, limit = 500): Promise<SnapshotComparison> {
  const { data, response } = await api().GET("/api/snapshots/compare", { params: { query: { from, to, offset, limit } } });
  return must(data, response);
}

/** One element on both sides of a comparison: both documents whole and the fields that differ. */
export async function compareSnapshotElement(from: string, to: string, id: string): Promise<SnapshotElementDiff> {
  const { data, response } = await api().GET("/api/snapshots/compare/element", { params: { query: { from, to, id } } });
  return must(data, response);
}

/** Replaces the working model with the snapshot after a safety snapshot; 409 `run-locked` while generation runs (maintainer). */
export async function restoreSnapshot(id: string, includePacks: boolean): Promise<SnapshotRestoreResult> {
  const { data, error, response } = await api().POST("/api/snapshots/{id}/restore", { params: { path: { id } }, body: { includePacks } });
  return record<SnapshotRestoreResult>(data, error, response);
}

/** Where a snapshot's archive downloads from (a zip, not JSON, so it is not read through the client). */
export const snapshotExportUrl = (id: string): string => `/api/snapshots/${encodeURIComponent(id)}/export`;

/** The archive's bytes (the mock's service worker answers fetches, not downloads). */
export async function exportSnapshot(id: string): Promise<Blob> {
  const response = await fetch(snapshotExportUrl(id), { headers: { Accept: "application/zip, application/problem+json" } });
  if (!response.ok) throw await problemOf(response);
  return response.blob();
}

/**
 * Imports an exported archive, sent as the `application/zip` body (the one body the sign-in gate takes that is not JSON):
 * the stored snapshot, or the refusal with its diagnostics (413 over a limit, 422 refused). A body the host refuses before
 * the engine reads it (a request size limit) is a thrown ApiProblem.
 */
export async function importSnapshot(file: Blob): Promise<SnapshotImportResult & { status: number }> {
  const response = await fetch("/api/snapshots/import", {
    method: "POST",
    headers: { "Content-Type": "application/zip", Accept: "application/json, application/problem+json" },
    body: file,
  });
  const type = response.headers.get("Content-Type") ?? "";
  if (/problem\+json/i.test(type) || !/json/i.test(type)) throw await problemOf(response);
  return { ...((await response.json()) as SnapshotImportResult), status: response.status };
}

async function problemOf(response: Response): Promise<ApiProblem> {
  let body: Problem | null = null;
  try {
    if (/json/i.test(response.headers.get("Content-Type") ?? "")) body = (await response.json()) as Problem;
  } catch {
    body = null;
  }
  const fallback = response.status === 413 ? "The archive is larger than the server accepts." : `HTTP ${response.status}`;
  return new ApiProblem(response.status, body, fallback);
}
