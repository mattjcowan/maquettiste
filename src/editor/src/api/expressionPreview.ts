// POST /api/model/expressions/preview: the printed form, the type and the findings of one expression. The Value box asks
// it for a set's value (a stored set by its id, or text for a set not yet saved, with the action, `site` "set" and the attribute).
import { api, ApiProblem } from "./client";
import type { components } from "./schema";

export type ExpressionPreviewRequest = components["schemas"]["ExpressionPreviewRequest"];
export type ExpressionPreview = components["schemas"]["ExpressionPreview"];

export async function previewExpression(body: ExpressionPreviewRequest, signal?: AbortSignal): Promise<ExpressionPreview> {
  const { data, error, response } = await api().POST("/api/model/expressions/preview", { body, signal });
  if (data) return data;
  // A refusal that carries diagnostics is still an answer (nothing to preview, and why).
  if (error && typeof error === "object" && "diagnostics" in error) return { preview: null, diagnostics: (error as unknown as ExpressionPreview).diagnostics };
  throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
}

/** The request for a set's value: stored (the set's id), or text for a set not saved yet or being edited. */
export function setPreviewRequest(set: { action: string; set: string; attribute: string; text?: string }): ExpressionPreviewRequest {
  return set.text === undefined ? { element: set.set } : { element: set.action, site: "set", attribute: set.attribute, text: set.text };
}

/** The request for the value of a call's input: stored (the input's id), or text with the action, `site` "input" and the member. */
export function inputPreviewRequest(input: { action: string; input: string; member: string; text?: string }): ExpressionPreviewRequest {
  return input.text === undefined ? { element: input.input } : { element: input.action, site: "input", member: input.member, text: input.text };
}
