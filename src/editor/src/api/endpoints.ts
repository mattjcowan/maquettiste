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
  JobInfo,
  ModelJson,
  NewModelDocument,
  PresenceReport,
  PreviewRequest,
  PreviewResult,
  ProjectInfo,
  ReferenceInfo,
  SaveResult,
  SessionInfo,
  LocalizationStatus,
  SettingsDocument,
  TranslationPage,
  TranslationWrite,
  TranslationWriteResult,
  SettingsJson,
  SettingsSaveResult,
  ValidationReport,
  ValidationScope,
  EditorHealth,
  ImportPreview,
  ReferenceTypeUsage,
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

export async function saveSettings(json: SettingsJson, hash: string): Promise<SettingsSaveResult> {
  const { data, error, response } = await api().PUT("/api/project/settings", {
    params: { header: { "If-Match": `"${hash}"` } },
    body: json,
  });
  return record<SettingsSaveResult>(data, error, response);
}

export async function getModelIndex(): Promise<ElementSummary[]> {
  const { data, response } = await api().GET("/api/model/index");
  return must(data, response);
}

/** A conditional index read (E5e): `notModified` on 304, else the body as text with its ETag. */
export type IndexAnswer = { notModified: true; etag: string | null } | { notModified: false; etag: string | null; text: string };

/** GET /api/model/index with `If-None-Match` when an ETag is known; the body is parsed by the caller. */
export async function getModelIndexText(etag: string | null, locale?: string | null): Promise<IndexAnswer> {
  const { data, response } = await api().GET("/api/model/index", {
    params: { ...(etag ? { header: { "If-None-Match": etag } } : {}), ...(locale ? { query: { locale } } : {}) },
    parseAs: "text",
  });
  const tag = response.headers.get("ETag");
  if (response.status === 304) return { notModified: true, etag: tag ?? etag };
  return { notModified: false, etag: tag, text: must(data as string | undefined, response) };
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

export async function deleteElement(id: string, hash: string, resolution: "refuse" | "remove-references" = "refuse"): Promise<SaveResult> {
  const { data, error, response } = await api().DELETE("/api/model/elements/{id}", {
    params: { path: { id }, header: { "If-Match": `"${hash}"` }, query: { resolution } },
  });
  return record<SaveResult>(data, error, response);
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

export async function cancelJob(id: string): Promise<JobInfo> {
  const { data, response } = await api().DELETE("/api/jobs/{id}", { params: { path: { id } } });
  return must(data, response);
}

export async function previewTemplate(request: PreviewRequest): Promise<PreviewResult> {
  const { data, response } = await api().POST("/api/templates/preview", { body: request });
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
