// Navigation history (explorer-redesign.md 3.3): Alt+Left and Alt+Right move back and forward through selections.
// A visit is a non-empty selection; clearing the selection is not one, and selecting what is current again is not
// either. Pure functions over an immutable value, so the store and the tests share them.

export interface NavHistory {
  /** Earlier selections, oldest first. */
  back: string[][];
  /** The selection the history stands on (null before the first visit). */
  current: string[] | null;
  /** Selections left by going back, the next one last. */
  forward: string[][];
}

/** How many selections each direction keeps. */
export const HISTORY_LIMIT = 50;

export const emptyHistory: NavHistory = { back: [], current: null, forward: [] };

const same = (a: readonly string[] | null, b: readonly string[]) => !!a && a.length === b.length && a.every((x, i) => x === b[i]);

/** The history after selecting `ids`: the current selection moves to the back list and the forward list is cleared. */
export function visit(h: NavHistory, ids: readonly string[]): NavHistory {
  if (!ids.length || same(h.current, ids)) return h;
  const back = h.current ? [...h.back, h.current].slice(-HISTORY_LIMIT) : h.back;
  return { back, current: [...ids], forward: [] };
}

/** One step back or forward, or null when there is nowhere to go. */
export function travel(h: NavHistory, direction: "back" | "forward"): NavHistory | null {
  if (direction === "back") {
    if (!h.back.length) return null;
    const target = h.back[h.back.length - 1];
    return { back: h.back.slice(0, -1), current: target, forward: h.current ? [...h.forward, h.current].slice(-HISTORY_LIMIT) : h.forward };
  }
  if (!h.forward.length) return null;
  const target = h.forward[h.forward.length - 1];
  return { back: h.current ? [...h.back, h.current].slice(-HISTORY_LIMIT) : h.back, current: target, forward: h.forward.slice(0, -1) };
}

/** Drops ids that no longer exist (a deleted element) from every entry, and the entries left empty. */
export function prune(h: NavHistory, exists: (id: string) => boolean): NavHistory {
  const keep = (list: string[][]) => list.map((ids) => ids.filter(exists)).filter((ids) => ids.length > 0);
  const current = h.current?.filter(exists) ?? null;
  return { back: keep(h.back), current: current?.length ? current : null, forward: keep(h.forward) };
}
