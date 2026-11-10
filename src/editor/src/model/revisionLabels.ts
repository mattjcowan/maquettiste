// How a lifecycle's moves speak of revision levels (revisions-design.md section 12): the level picker's labels, taken from the levels of the
// types the lifecycle governs, and the short text on a move ("next major", "revise: minor"). Pure; the hook that gathers the levels is in
// editors/process/revisionLevels.ts.
import type { SequenceLevel } from "@/model/revisionSequence";

/** The verb whose move revises (its transitions name `Revise at`); any other verb's move only labels with the next revision. */
export const REVISE_VERB = "revise";

export interface LevelLabel {
  number: number;
  /** The level's name when the types agree on one, else null. */
  name: string | null;
  /** "Level 1 (major)", or "Level 1" without a name. */
  label: string;
}

/**
 * The labels of levels 1 to n, n being the most levels any governed type has. A level takes the name the types give it; when types
 * disagree, the first type's name is used.
 */
export function levelLabels(levelLists: readonly (readonly SequenceLevel[])[]): LevelLabel[] {
  const most = Math.max(0, ...levelLists.map((l) => l.length));
  return Array.from({ length: most }, (_, i) => {
    const name = levelLists.map((l) => l[i]?.name).find((n) => !!n) ?? null;
    return { number: i + 1, name, label: name ? `Level ${i + 1} (${name})` : `Level ${i + 1}` };
  });
}

/** The label of the Next revision field: a revise move names where it revises. */
export const nextRevisionFieldLabel = (verb: string | undefined) => (verb === REVISE_VERB ? "Revise at" : "Next revision");

/** The text of a move that gives the next revision: "next major" for a labelling move, "revise: minor" for a revise; "" without one. */
export function transitionRevisionText(nextRevision: number | undefined, verb: string | undefined, labels: readonly LevelLabel[]): string {
  if (nextRevision === undefined) return "";
  const level = labels.find((l) => l.number === nextRevision);
  const name = level?.name ?? `level ${nextRevision}`;
  return verb === REVISE_VERB ? `revise: ${name}` : `next ${name}`;
}

/** The choices of the level picker: None, then each level; a level the types lack but the file names is kept, so it can be seen and fixed. */
export function levelChoices(labels: readonly LevelLabel[], current: number | undefined): { value: string; label: string }[] {
  const choices = [{ value: "", label: "None" }, ...labels.map((l) => ({ value: String(l.number), label: l.label }))];
  if (current !== undefined && !labels.some((l) => l.number === current)) choices.push({ value: String(current), label: `Level ${current} (no governed type has it)` });
  return choices;
}
