// The simulation panel's operations over the generated client (phase-3-design.md 4.4): simulate (with the caller's
// AbortSignal, so a newer input list cancels the call in flight) and record. Every configuration, guard result and
// refusal the panel shows comes from these answers; the browser computes none of them.
import { api, ApiProblem } from "@/api/client";
import type { components } from "@/api/schema";
import type { Diagnostic } from "@/api/types";

export type SimInput = Omit<components["schemas"]["SimInput"], "expect">;
export type SimulateRequest = components["schemas"]["SimulateRequest"];
export type SimulationResult = components["schemas"]["SimulationResult"];
export type StepTrace = components["schemas"]["StepTrace"];
export type EnabledTrigger = components["schemas"]["EnabledTrigger"];
export type RecordScenarioRequest = components["schemas"]["RecordScenarioRequest"];
export type RecordScenarioResult = components["schemas"]["RecordScenarioResult"];
export type ScenarioVerification = components["schemas"]["ScenarioVerification"];

/** A simulate answer: the result, or the diagnostics of a draft that does not validate (422: nothing ran). */
export type SimulateOutcome = { kind: "result"; result: SimulationResult } | { kind: "invalid"; diagnostics: Diagnostic[] };

function problemOf(error: unknown, response: Response): ApiProblem {
  const problem = error && typeof error === "object" && "title" in error ? (error as components["schemas"]["Problem"]) : null;
  return new ApiProblem(response.status, problem, `HTTP ${response.status}`);
}

export async function simulateProcess(process: string, body: SimulateRequest, signal?: AbortSignal): Promise<SimulateOutcome> {
  const { data, error, response } = await api().POST("/api/processes/{id}/simulate", { params: { path: { id: process } }, body, signal });
  if (data) return { kind: "result", result: data };
  if (response.status === 422 && error && typeof error === "object" && "diagnostics" in error)
    return { kind: "invalid", diagnostics: (error as { diagnostics: Diagnostic[] }).diagnostics };
  throw problemOf(error, response);
}

/** Records the inputs as a scenario (201); a refusal (409, 422) throws with the first diagnostic's message. */
export async function recordScenario(process: string, body: RecordScenarioRequest): Promise<RecordScenarioResult> {
  const { data, error, response } = await api().POST("/api/processes/{id}/scenarios", { params: { path: { id: process } }, body });
  if (data && response.status === 201) return data;
  if (error && typeof error === "object" && "diagnostics" in error) {
    const first = (error as { diagnostics: Diagnostic[] }).diagnostics[0];
    throw new ApiProblem(response.status, null, first ? `${first.rule} ${first.message}` : `HTTP ${response.status}`);
  }
  throw problemOf(error, response);
}
