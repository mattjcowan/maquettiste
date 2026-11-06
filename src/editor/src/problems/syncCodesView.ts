// What the Sync codes dialog says (MQ9210's quick fix): the plan of the engine's dry run in plain words. Pure, so the
// wording is tested without the dialog.
import type { components } from "@/api/schema";
import { stateIndex, type ProcessDoc } from "@/model/process";

type Plan = components["schemas"]["SyncCodesResult"];

export interface CodeLine {
  /** The state's dotted path. */
  path: string;
  code: string | number;
  /** Where the proposed code comes from, in plain words. */
  from: string;
}

export interface CodesView {
  /** States that get a code now. */
  proposed: CodeLine[];
  /** States that already have a code (read-only). */
  kept: { path: string; code: string | number }[];
  /** States for which nothing could be proposed, with the codes that nearly matched. */
  unmatched: { path: string; candidates: (string | number)[] }[];
  binds: boolean;
}

export const codeText = (code: string | number) => JSON.stringify(code);

/** Where a proposed code comes from: the engine's `source` said in words; a label match names the label it matched. */
export function sourceText(source: Plan["codes"][number]["source"], label: string): string {
  if (source === "given") return "the code you gave";
  if (source === "name") return "same as the state's name";
  if (source === "label") return `from the row labeled ${JSON.stringify(label)}`;
  return "already there";
}

export function codesView(plan: Plan, process: Pick<ProcessDoc, "states"> | undefined): CodesView {
  const index = process ? stateIndex(process) : undefined;
  // A label match compares the state's display name, else its name, with the label of a row or member.
  const labelOf = (id: string, path: string) => {
    const state = index?.get(id)?.state;
    return state?.displayName || state?.name || path.split(".").at(-1) || path;
  };
  return {
    proposed: plan.codes.filter((c) => c.source !== "kept").map((c) => ({ path: c.path, code: c.code, from: sourceText(c.source, labelOf(c.state, c.path)) })),
    kept: plan.codes.filter((c) => c.source === "kept").map((c) => ({ path: c.path, code: c.code })),
    unmatched: plan.unmatched.map((u) => ({ path: u.path, candidates: u.candidates })),
    binds: plan.binds,
  };
}

/** The dialog's one-line description: what will happen, for the binding it is about. */
export function codesDescription(view: CodesView, binding: string): string {
  const n = view.proposed.length;
  const m = view.unmatched.length;
  const states = (k: number) => (k === 1 ? "1 state has" : `${k} states have`);
  const head = n
    ? `${n === 1 ? "1 state gets" : `${n} states get`} a code for ${binding}`
    : m
      ? `${states(m)} no code; none could be proposed`
      : `No state needs a new code for ${binding}`;
  const parts = [head];
  if (n && m) parts.push(`${m === 1 ? "1 other state" : `${m} other states`} could not be proposed a code`);
  if (view.binds) parts.push("the lifecycle will also bind this attribute");
  return `${parts.join("; ")}. States that already have a code keep it.`;
}

interface AttributeShape {
  type?: { kind?: string; typeId?: string };
  validation?: { allowedValues?: unknown[] };
}

/** True for a plain attribute (text, integer...) with no list of allowed values, own or from its scalar type: there is nothing to take codes from. */
export function hasNoValueList(attribute: AttributeShape | undefined, scalar: AttributeShape | undefined): boolean {
  if (!attribute || (attribute.type?.kind ?? "plain") !== "plain") return false;
  return !attribute.validation?.allowedValues?.length && !scalar?.validation?.allowedValues?.length;
}

/** The plain line shown when nothing could be proposed because the attribute has no list of values. */
export const NO_VALUE_LIST_NOTE =
  "This attribute has no list of values to take codes from. Type the codes in the States tab, or give the attribute allowed values.";

/** Why Apply is off, or null when it is on. */
export function applyBlockedReason(view: CodesView, hasDiagnostics: boolean): string | null {
  if (hasDiagnostics) return "The engine refused this sync; see the message above.";
  if (!view.binds && !view.proposed.length)
    return view.unmatched.length ? "No code could be proposed, so there is nothing to apply." : "Every state already has a code, so there is nothing to apply.";
  return null;
}
