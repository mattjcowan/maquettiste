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
}

type Refusal = { refusal?: string | null; refusedBy?: string | null } | null | undefined;

/** "refused by NoDelete (Right)", "refused (Right)", or just "refused". `name` answers an element's name, or null once it is gone. */
function refusalText(value: Refusal, name: (id: string) => string | null): string {
  const word = value?.refusal ? (RL.refusalWords[value.refusal] ?? value.refusal) : null;
  const by = value?.refusedBy ? (name(value.refusedBy) ?? RL.deleted) : null;
  if (!word && !by) return RL.refusedPlain;
  return by ? `${RL.refusedByWord} ${by}${word ? ` (${word})` : ""}` : `refused (${word})`;
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
    if (!expectsRefusal && !failure.actual) return RL.failedAccept(where);
  }
  return `${where} (${failure.rule}): ${rest}`;
}
