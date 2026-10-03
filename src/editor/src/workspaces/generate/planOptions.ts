// The Generate toolbar's Options (one run only, never saved): Re-render every file (`force`) and what to do with a generated
// file that was edited by hand (`handEdits`, the engine's HandEditPolicy: fail, overwrite, skip), sent with the plan request.
// Apply takes only the plan id: it runs with the options the plan was made with. Pure: no React.
import type { ApplyResult, FileChange, GenerationRequest, components } from "@/api/types";

export type HandEditPolicy = components["schemas"]["HandEditPolicy"];

export interface PlanOptions {
  /** Every unit renders again even if its inputs did not change (reason `forced`); Apply still writes only files whose bytes differ. */
  force: boolean;
  /** This run's hand-edit policy; null keeps the project's (and each pack's) setting. */
  handEdits: HandEditPolicy | null;
}

export const DEFAULT_PLAN_OPTIONS: PlanOptions = { force: false, handEdits: null };

/** The choices in words, in the engine's order. */
export const HAND_EDIT_CHOICES: readonly { value: HandEditPolicy; label: string }[] = [
  { value: "fail", label: "Stop and report it" },
  { value: "overwrite", label: "Overwrite it" },
  { value: "skip", label: "Keep it and skip" },
];

export const handEditLabel = (policy: HandEditPolicy): string => HAND_EDIT_CHOICES.find((c) => c.value === policy)?.label ?? policy;

/** "Project default (Stop and report it)"; "Project default" while the settings load. */
export const projectDefaultLabel = (policy: HandEditPolicy | null | undefined): string =>
  policy ? `Project default (${handEditLabel(policy)})` : "Project default";

/** Whether anything differs from the defaults (the Options button shows a dot). */
export const hasNonDefault = (o: PlanOptions): boolean => o.force || o.handEdits !== null;

/** The plan request for the chosen packs and options; a default option is left out so the server's default applies. */
export function planRequest(packs: string[], o: PlanOptions): GenerationRequest {
  return { packs, ...(o.force ? { force: true } : {}), ...(o.handEdits ? { handEdits: o.handEdits } : {}) };
}

const OUTCOME_WORDS: Record<HandEditPolicy, string> = {
  fail: "reported, not written",
  overwrite: "overwritten",
  skip: "kept as edited and skipped",
};

/**
 * The Apply result's hand-edit line, only when the plan held files edited by hand: "2 files edited by hand: overwritten (this
 * run's choice: Overwrite it)". `chosen` is the plan request's policy, `fallback` the project's.
 */
export function handEditNote(
  changes: readonly Pick<FileChange, "kind">[],
  chosen: HandEditPolicy | null | undefined,
  fallback: HandEditPolicy | null | undefined,
  outcome: ApplyResult["outcome"] | null,
): string | null {
  const n = changes.filter((c) => c.kind === "hand-edited" || c.kind === "conflict").length;
  const policy = chosen ?? fallback;
  if (!n || !policy || (outcome !== "succeeded" && outcome !== "conflicts")) return null;
  const files = `${n} ${n === 1 ? "file" : "files"} edited by hand`;
  const source = chosen ? `this run's choice: ${handEditLabel(chosen)}` : `project default: ${handEditLabel(policy)}`;
  return `${files}: ${OUTCOME_WORDS[policy]} (${source})`;
}
