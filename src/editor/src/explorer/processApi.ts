// The process operations the explorer runs (phase-3-design.md 4.4): verify, export and import, over the generated
// client; and the last verify result per scenario, which the Processes explorer shows (passed counts, statuses).
import { useSyncExternalStore } from "react";
import { api, ApiProblem } from "@/api/client";
import type { components } from "@/api/schema";
import type { ScenarioStatus } from "./tree";

export type VerifyResult = components["schemas"]["VerifyResult"];
export type ProcessImportResult = components["schemas"]["ProcessImportResult"];
export type ProcessImportRequest = components["schemas"]["ProcessImportRequest"];

function answer<T>(data: T | undefined, error: unknown, response: Response): T {
  if (data !== undefined) return data;
  if (error && typeof error === "object" && !("status" in error && "title" in error)) return error as T;
  const problem = error && typeof error === "object" ? (error as components["schemas"]["Problem"]) : null;
  throw new ApiProblem(response.status, problem, `HTTP ${response.status}`);
}

export async function verifyScenarios(process: string): Promise<VerifyResult> {
  const { data, error, response } = await api().POST("/api/processes/{id}/verify", { params: { path: { id: process } }, body: {} });
  const result = answer<VerifyResult>(data, error, response);
  recordVerify(result);
  return result;
}

/** The statechart config text of a process (Export XState). */
export async function exportProcess(process: string): Promise<string> {
  const { data, response } = await api().GET("/api/processes/{id}/export", {
    params: { path: { id: process }, query: { format: "xstate" } },
    parseAs: "text",
  });
  if (!response.ok || data === undefined) throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
  return data as unknown as string;
}

export async function importProcess(request: ProcessImportRequest, dryRun: boolean): Promise<ProcessImportResult> {
  const { data, error, response } = await api().POST("/api/processes/import", {
    params: { query: { format: "xstate", dryRun: dryRun ? "true" : "false" } as never },
    body: request,
  });
  return answer<ProcessImportResult>(data, error, response);
}

// ------------------------------------------------------------------ last verify results

let statuses: ReadonlyMap<string, ScenarioStatus> = new Map();
const listeners = new Set<() => void>();

export function recordVerify(result: VerifyResult): void {
  const next = new Map(statuses);
  for (const r of result.results) next.set(r.scenario, r.passed ? { passed: true } : { passed: false, step: (r.failure?.step ?? -1) + 1 });
  statuses = next;
  for (const l of listeners) l();
}

export function scenarioStatuses(): ReadonlyMap<string, ScenarioStatus> {
  return statuses;
}

export function resetScenarioStatuses(): void {
  statuses = new Map();
  for (const l of listeners) l();
}

export function useScenarioStatuses(): ReadonlyMap<string, ScenarioStatus> {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    scenarioStatuses,
    scenarioStatuses,
  );
}

/** The Output panel's lines for a verify run. */
export function verifyLines(name: string, result: VerifyResult): string[] {
  const passed = result.results.filter((r) => r.passed).length;
  const head = `${name}: ${passed} of ${result.results.length} scenarios passed`;
  return [
    head,
    ...result.results.map((r) =>
      r.passed
        ? `  ✓ ${r.name}`
        : `  ✗ ${r.name}: failed at step ${(r.failure?.step ?? -1) + 1}${r.failure ? ` (${r.failure.rule}) ${r.failure.message}` : ""}`,
    ),
  ];
}
