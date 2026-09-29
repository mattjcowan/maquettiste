// One function per operation of docs/api/openapi.yaml that the SPA calls. Each returns the
// contract's record; engine outcomes (SaveResult, BatchResult, SettingsSaveResult) come back at any
// status, and every other failure is a thrown ApiProblem (client.ts).
import { api, ApiProblem, etagHash } from "./client";
import type {
  BatchParseResult,
  BatchRequest,
  BatchResult,
  DatabaseTablesResult,
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
  SettingsDocument,
  SettingsJson,
  SettingsSaveResult,
  ValidationReport,
  ValidationScope,
  EditorHealth,
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
