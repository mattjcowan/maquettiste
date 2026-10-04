// Settings › Assistant's draft: maquettiste.json `assistant` (erratum E44). Defaults are left out of the saved document, so
// the canonical file stays as it was when nothing changes.
import type { SettingsJson } from "@/api/types";

export const ASSISTANT_DEFAULTS = { maxTurns: 10, tokenBudgetPerRequest: 200_000, tokenBudgetPerDayPerUser: 2_000_000 } as const;
export const MAX_INSTRUCTIONS = 8000;

export interface AssistantDraft {
  instructions: string;
  maxTurns: string;
  tokenBudgetPerRequest: string;
  tokenBudgetPerDayPerUser: string;
}

type AssistantJson = { instructions?: string | null; maxTurns?: number; tokenBudgetPerRequest?: number; tokenBudgetPerDayPerUser?: number };

export function assistantOf(json: SettingsJson): AssistantDraft {
  const a = ((json as Record<string, unknown>).assistant ?? {}) as AssistantJson;
  return {
    instructions: a.instructions ?? "",
    maxTurns: String(a.maxTurns ?? ASSISTANT_DEFAULTS.maxTurns),
    tokenBudgetPerRequest: String(a.tokenBudgetPerRequest ?? ASSISTANT_DEFAULTS.tokenBudgetPerRequest),
    tokenBudgetPerDayPerUser: String(a.tokenBudgetPerDayPerUser ?? ASSISTANT_DEFAULTS.tokenBudgetPerDayPerUser),
  };
}

export const sameAssistant = (a: AssistantDraft, b: AssistantDraft): boolean =>
  a.instructions === b.instructions &&
  a.maxTurns === b.maxTurns &&
  a.tokenBudgetPerRequest === b.tokenBudgetPerRequest &&
  a.tokenBudgetPerDayPerUser === b.tokenBudgetPerDayPerUser;

/** What is wrong with a draft, by field, or an empty object. */
export function assistantProblems(d: AssistantDraft): Partial<Record<keyof AssistantDraft, string>> {
  const out: Partial<Record<keyof AssistantDraft, string>> = {};
  const int = (v: string) => (/^\d+$/.test(v.trim()) ? Number(v.trim()) : NaN);
  if (d.instructions.length > MAX_INSTRUCTIONS) out.instructions = `At most ${MAX_INSTRUCTIONS} characters (${d.instructions.length} now).`;
  const turns = int(d.maxTurns);
  if (!(turns >= 1 && turns <= 25)) out.maxTurns = "A whole number from 1 to 25.";
  if (!(int(d.tokenBudgetPerRequest) >= 1000)) out.tokenBudgetPerRequest = "A whole number of at least 1000.";
  if (!(int(d.tokenBudgetPerDayPerUser) >= 1000)) out.tokenBudgetPerDayPerUser = "A whole number of at least 1000.";
  return out;
}

/** The settings with the draft's `assistant`: defaults and empty instructions left out, the member removed when nothing is left. */
export function withAssistant(json: SettingsJson, d: AssistantDraft): SettingsJson {
  const out: AssistantJson = {};
  if (d.instructions.trim()) out.instructions = d.instructions;
  const turns = Number(d.maxTurns.trim());
  if (turns !== ASSISTANT_DEFAULTS.maxTurns) out.maxTurns = turns;
  const request = Number(d.tokenBudgetPerRequest.trim());
  if (request !== ASSISTANT_DEFAULTS.tokenBudgetPerRequest) out.tokenBudgetPerRequest = request;
  const day = Number(d.tokenBudgetPerDayPerUser.trim());
  if (day !== ASSISTANT_DEFAULTS.tokenBudgetPerDayPerUser) out.tokenBudgetPerDayPerUser = day;
  const next = { ...(json as Record<string, unknown>) };
  if (Object.keys(out).length) next.assistant = out;
  else delete next.assistant;
  return next as SettingsJson;
}
