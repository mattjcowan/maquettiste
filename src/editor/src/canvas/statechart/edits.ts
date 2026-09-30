// The statechart canvas's process edits (phase-3-design.md 6.3), kept free of React so they can be unit tested:
// deleting several states at once (a multi-selection) with the transitions that enter them from outside when the
// user confirms, and drawing a transition from one state to another (T).
import { addTransition, deleteState, stateIndex, subtreeIds, type ProcessDoc, type TransitionDoc } from "@/model/process";

/** The selected states without those inside another selected state (deleting the outer one deletes them). */
export function outermost(process: ProcessDoc, ids: readonly string[]): string[] {
  const index = stateIndex(process);
  const chosen = new Set(ids);
  return ids.filter((id, i) => {
    if (ids.indexOf(id) !== i || !index.has(id)) return false;
    for (let p = index.get(id)!.parent; p; p = index.get(p.id)!.parent) if (chosen.has(p.id)) return false;
    return true;
  });
}

/** Every state a delete of `ids` removes: each with its subtree. */
export function removedStates(process: ProcessDoc, ids: readonly string[]): Set<string> {
  const out = new Set<string>();
  for (const id of ids) for (const x of subtreeIds(process, id)) out.add(x);
  return out;
}

/** The transitions from outside the removed states that enter one of them: they block the delete until confirmed. */
export function incomingFromOutside(process: ProcessDoc, ids: readonly string[]): TransitionDoc[] {
  const removed = removedStates(process, ids);
  return (process.transitions ?? []).filter((t) => !removed.has(t.source) && (t.targets ?? []).some((x) => removed.has(x)));
}

/**
 * Deletes states with their subtrees and the transitions leaving them. `alsoTransitions` (the confirmed incoming ones)
 * lose the deleted states as targets first; one left with no target is deleted, one with other targets keeps them.
 * Returns the refusal (the process keeps at least one state; a transition still enters) or null.
 */
export function deleteStates(process: ProcessDoc, ids: readonly string[], alsoTransitions: readonly string[] = []): string | null {
  const targets = outermost(process, ids);
  if (!targets.length) return "The states no longer exist.";
  if (targets.length >= process.states.length && targets.every((id) => process.states.some((s) => s.id === id))) return "A process keeps at least one state.";
  const removed = removedStates(process, targets);
  if (alsoTransitions.length) {
    const confirmed = new Set(alsoTransitions);
    process.transitions = (process.transitions ?? []).flatMap((t) => {
      if (!confirmed.has(t.id) || !(t.targets ?? []).some((x) => removed.has(x))) return [t];
      const left = (t.targets ?? []).filter((x) => !removed.has(x));
      return left.length ? [{ ...t, targets: left }] : [];
    });
    if (!process.transitions.length) delete process.transitions;
  }
  // Transitions leaving the deleted states go with them (so transitions among them do not block the delete).
  if (process.transitions) {
    process.transitions = process.transitions.filter((t) => !removed.has(t.source));
    if (!process.transitions.length) delete process.transitions;
  }
  for (const id of targets) {
    const result = deleteState(process, id);
    if (!result.ok) return result.reason;
  }
  return null;
}

/** Draws a transition from `source` to `target` (on the first event, or `always` when the process has none); returns its id. */
export function linkStates(process: ProcessDoc, source: string, target: string): string {
  const id = addTransition(process, source);
  const t = process.transitions!.find((x) => x.id === id)!;
  t.targets = [target];
  return id;
}
