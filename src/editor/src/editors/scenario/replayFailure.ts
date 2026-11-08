// A failed replay in plain words (rights-design.md decision 26): which step, what it was expected to do, and what the replay did
// instead. A refusal is named with the element that refused it; an element that no longer exists reads "(deleted)".
import { RIGHT_LABELS as RL } from "@/model/labels";

export interface ReplayFailure {
  step: number;
  rule: string;
  message: string;
  expected?: unknown;
  actual?: unknown;
}

export interface FailedStep {
  expect?: { accepted?: boolean; refusal?: string; refusedBy?: string };
  /** What the step says the team's checks do (a check id to pass, warning or error). */
  assume?: Record<string, unknown>;
}

type Refusal = { refusal?: string | null; refusedBy?: string | null } | null | undefined;

/** "refused by NoDelete (Right)", "refused (Right)", or just "refused". `name` answers an element's name, or null once it is gone. */
function refusalText(value: Refusal, name: (id: string) => string | null): string {
  const word = value?.refusal ? (RL.refusalWords[value.refusal] ?? value.refusal) : null;
  const by = value?.refusedBy ? (name(value.refusedBy) ?? RL.deleted) : null;
  if (!word && !by) return RL.refusedPlain;
  return by ? `${RL.refusedByWord} ${by}${word ? ` (${word})` : ""}` : `refused (${word})`;
}

/** "A, B" for ids, or "no check" and the like for none; an id that is gone reads "(deleted)". */
function listText(ids: unknown, none: string, name: (id: string) => string | null): string {
  const list = Array.isArray(ids) ? ids.filter((i): i is string => typeof i === "string") : [];
  return list.length ? list.map((i) => name(i) ?? RL.deleted).join(", ") : none;
}

/** The checks a step says refuse (assumed Error), named; none when it says nothing of the kind. */
function assumedRefusers(step: FailedStep | undefined, name: (id: string) => string | null): string[] {
  return Object.entries(step?.assume ?? {})
    .filter(([, word]) => word === "error")
    .map(([id]) => name(id) ?? RL.deleted);
}

/** The failure as one sentence, with the step named once. */
export function failureSentence(failure: ReplayFailure, step: FailedStep | undefined, name: (id: string) => string | null): string {
  const rest = failure.message.replace(/^Step \d+:?\s*/i, "");
  if (failure.step < 0) return `${RL.outcomeWord} (${failure.rule}): ${rest}`;
  const where = `Step ${failure.step + 1}`;
  const expect = step?.expect;
  const expectsRefusal = expect?.accepted === false || !!expect?.refusal || !!expect?.refusedBy;
  if (failure.rule === "MQ9308") {
    const expected = (failure.expected ?? { refusal: expect?.refusal, refusedBy: expect?.refusedBy }) as Refusal;
    const actual = failure.actual as Refusal;
    return RL.failedRefusal(where, refusalText(expected, name), actual?.refusal || actual?.refusedBy ? `it is ${refusalText(actual, name)}` : "it is accepted");
  }
  if (failure.rule === "MQ9301" && typeof failure.actual === "boolean" && /accepted while|refused while/.test(failure.message)) {
    if (expectsRefusal && failure.actual) return RL.failedRefusal(where, refusalText(expect, name), "it is accepted");
    if (!expectsRefusal && !failure.actual) {
      // The replay refused: when the step says a check of the team's refuses, that check is the one.
      const checks = assumedRefusers(step, name);
      return checks.length ? RL.failedAcceptByCheck(where, checks.join(" or ")) : RL.failedAccept(where);
    }
  }
  if (failure.rule === "MQ9311" && failure.expected && typeof failure.expected === "object" && "notices" in failure.expected) {
    // The notices as "Finance remind, Manager escalate".
    const list = (value: unknown) =>
      ((value as { notices?: { signature: string; kind: string }[] } | undefined)?.notices ?? [])
        .map((n) => `${name(n.signature) ?? RL.deleted} ${n.kind}`)
        .join(", ") || "no notice";
    return RL.failedNotices(where, list(failure.expected), list(failure.actual));
  }
  if (failure.rule === "MQ9311" && failure.expected && typeof failure.expected === "object") {
    // The counts by named signature, as "Finance 1, Manager 0" (a signature that is gone reads "(deleted)").
    const counts = (value: unknown) =>
      Object.entries((value ?? {}) as Record<string, number>)
        .map(([id, n]) => `${name(id) ?? RL.deleted} ${n}`)
        .join(", ") || "no signature";
    return RL.failedSignatures(where, counts(failure.expected), counts(failure.actual));
  }
  if (failure.rule === "MQ9310") {
    const expected = (failure.expected ?? {}) as { checkWarnings?: unknown; triggerPoints?: unknown };
    const actual = (failure.actual ?? {}) as { checkWarnings?: unknown; triggerPoints?: unknown };
    const differ = (a: unknown, b: unknown) => a !== undefined && a !== null && JSON.stringify(a) !== JSON.stringify(b);
    const parts = [
      differ(expected.checkWarnings, actual.checkWarnings)
        ? RL.failedWarnings(listText(expected.checkWarnings, "no check", name), listText(actual.checkWarnings, "no check", name))
        : null,
      differ(expected.triggerPoints, actual.triggerPoints)
        ? RL.failedPoints(listText(expected.triggerPoints, "no trigger point", name), listText(actual.triggerPoints, "no trigger point", name))
        : null,
    ].filter((p): p is string => !!p);
    if (parts.length) return `${where}: ${parts.join(" ")}`;
  }
  return `${where} (${failure.rule}): ${rest}`;
}
