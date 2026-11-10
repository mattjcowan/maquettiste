// The scenario editor's revision fields (revisions-design.md section 12, decision 40): an object's Revision, a revise step's Level or Name,
// an event step's Name, and Expect revision. The pure parts are here; the hook reads the entities' merged blocks.
import { useMemo } from "react";
import { useElements, useIndex } from "@/api/queries";
import { indexLookup } from "@/model/index";
import { levelLabels, REVISE_VERB, type LevelLabel } from "@/model/revisionLabels";
import { firstRevision, resolveRevisions, type EntityWithRevisions, type MergedRevisions } from "@/model/revisionSequence";
import type { ProcessDoc, StepDoc } from "@/model/process";

/** The merged `revisions` block of each entity (null for one with none), through its bases. */
export function useMergedRevisions(entityIds: readonly string[]): Map<string, MergedRevisions | null> {
  const lookup = indexLookup(useIndex().data);
  const key = [...new Set(entityIds)].sort().join(",");
  const family = useMemo(() => {
    const ids = new Set<string>();
    for (const id of key ? key.split(",") : []) for (let at: string | undefined = id; at && !ids.has(at); at = lookup.byId.get(at)?.base) ids.add(at);
    return [...ids];
  }, [key, lookup]);
  const docs = useElements(family);
  return useMemo(() => {
    const docOf = (id: string) => docs.byId.get(id)?.json as EntityWithRevisions;
    return new Map((key ? key.split(",") : []).map((id) => [id, resolveRevisions(id, lookup.byId, docOf)] as const));
  }, [docs.byId, key, lookup]);
}

/** The level labels of the subject's type (empty when it has no revisions). */
export const subjectLevelLabels = (merged: MergedRevisions | null | undefined): LevelLabel[] => levelLabels(merged ? [merged.levels] : []);

/** Whether an event is used by a move that revises or gives the next revision, so an event step may choose a name for it. */
export function eventTakesRevision(process: ProcessDoc | undefined, eventId: string | undefined): boolean {
  if (!process || !eventId) return false;
  const verb = process.events?.find((e) => e.id === eventId)?.verb;
  return (process.transitions ?? []).some((t) => t.event === eventId && (t.nextRevision !== undefined || (t.verb ?? verb) === REVISE_VERB));
}

/** What a step's `revision` is: a level (an integer), a name (text), or nothing. */
export const stepRevisionLevel = (s: StepDoc | undefined): number | undefined => (typeof s?.revision === "number" ? s.revision : undefined);
export const stepRevisionName = (s: StepDoc | undefined): string => (typeof s?.revision === "string" ? s.revision : "");

/** A revise verb step takes a level or a name; an event step only a name, and only when its event can take a revising or labelling move. */
export const stepTakesLevel = (s: StepDoc | undefined): boolean => s?.input === "verb" && s.verb === REVISE_VERB;
export const stepTakesName = (s: StepDoc | undefined, process: ProcessDoc | undefined): boolean =>
  stepTakesLevel(s) || ((s?.input ?? "event") === "event" && eventTakesRevision(process, s?.event));

/** Writes the step's revision from the Level field (an integer, or removed) or the Name field (text, or removed). */
export function setStepRevision(s: StepDoc, field: "level" | "name", value: string): void {
  const v = value.trim();
  if (!v) delete s.revision;
  else s.revision = field === "level" ? Number(v) : v;
}

/**
 * The subject's revision before a step, as far as the scenario says: the last revision an earlier step expects, else the subject object's
 * revision, else the sequence's start (the bound attribute's default is not read here).
 */
export function revisionBefore(
  scenario: { steps: StepDoc[]; objects?: { id: string; revision?: string }[]; subject?: string },
  index: number,
  merged: MergedRevisions | null | undefined,
): string {
  for (let i = index - 1; i >= 0; i--) {
    const r = scenario.steps[i]?.expect?.revision;
    if (r !== undefined) return r;
  }
  const object = scenario.objects?.find((o) => o.id === scenario.subject);
  if (object?.revision) return object.revision;
  return merged ? firstRevision({ levels: merged.levels, separator: merged.separator }) : "";
}
