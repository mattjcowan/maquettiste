// The revision part of the mock's run (revisions-design.md section 9): what the subject's revision starts as, what a revise or a move that
// gives the next revision moves it to, and the values a revise resets or clears. Pure: the interpreter in stepper.ts calls these and
// does the moving of states. The sequence functions are the editor's own (model/revisionSequence.ts), which the shared fixture keeps equal
// to the engine's.
import { checkChosenRevision, firstRevision, nextRevision, type MergedRevisions, type RevisionSequence } from "@/model/revisionSequence";

type Json = Record<string, unknown>;

export const sequenceOf = (merged: MergedRevisions): RevisionSequence => ({ levels: merged.levels, separator: merged.separator });

/**
 * The revision the subject starts with: the object's `revision`, else the bound attribute's value when it holds a text, else the
 * sequence's start (the bound attribute's default is in `row` already).
 */
export function startRevision(merged: MergedRevisions, objectRevision: unknown, row: Json): string {
  if (typeof objectRevision === "string") return objectRevision;
  if (merged.boundAttribute && typeof row[merged.boundAttribute] === "string") return row[merged.boundAttribute] as string;
  return firstRevision(sequenceOf(merged));
}

export interface RevisionMove {
  to: string;
  /** The level that moved; null for a chosen name. */
  level: number | null;
}

/** The revision after a move: a chosen name (a later value) or the next value at `level`; null when the sequence cannot give it. */
export function moveRevision(merged: MergedRevisions, current: string, level: number, chosen: string | null): RevisionMove | null {
  const seq = sequenceOf(merged);
  if (chosen !== null && chosen !== "") return checkChosenRevision(seq, current, chosen).ok ? { to: chosen, level: null } : null;
  const to = nextRevision(seq, current, level);
  return to === null ? null : { to, level };
}

/**
 * The values a revise resets or clears, by attribute id: `reset` takes the attribute's default (empty without one, and empty with a
 * default expression, which the team's system fills), `clear` empties. Returns the values and the attributes whose default is an expression.
 */
export function reviseValues(
  merged: MergedRevisions,
  attributes: readonly { id: string; default?: unknown; defaultExpression?: unknown }[],
): { values: Json; expressions: string[] } {
  const values: Json = {};
  const expressions: string[] = [];
  for (const entry of merged.attributes) {
    const attribute = attributes.find((a) => a.id === entry.attribute);
    if (entry.onRevise === "reset" && attribute?.defaultExpression) expressions.push(entry.attribute);
    values[entry.attribute] = entry.onRevise === "reset" && !attribute?.defaultExpression ? (attribute?.default ?? null) : null;
  }
  return { values, expressions };
}
