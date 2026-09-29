// The explorer's keyboard model (explorer-redesign.md 3.4 and 1.8): a pure function from a key and the tree's
// state to one action, so the model is tested without rendering, and a small hook that runs it with a type-ahead
// buffer. Focus stays on the tree container (aria-activedescendant); the caller applies the action.
import { useCallback, useRef, type KeyboardEvent } from "react";

export interface KeyRow {
  key: string;
  depth: number;
  label: string;
  /** The row's kind, for same-kind multi-selection; undefined for folders and containers. */
  kind?: string;
  expandable: boolean;
  expanded: boolean;
}

export interface KeyInput {
  key: string;
  shift?: boolean;
  ctrl?: boolean;
  alt?: boolean;
}

export type TreeAction =
  | { type: "move"; index: number; extend: boolean }
  | { type: "expand"; index: number }
  | { type: "collapse"; index: number }
  | { type: "expand-siblings"; index: number }
  | { type: "open"; index: number }
  | { type: "toggle-select"; index: number }
  | { type: "rename"; index: number }
  | { type: "delete" }
  | { type: "menu"; index: number };

/** The index of the next row, after `from`, whose label starts with `prefix` (wrapping); -1 if none. */
export function typeAhead(rows: readonly Pick<KeyRow, "label">[], from: number, prefix: string): number {
  const p = prefix.toLowerCase();
  if (!p || rows.length === 0) return -1;
  // A repeated single letter cycles through the rows that start with it; a longer prefix may stay on the row.
  const repeated = p.length > 1 && [...p].every((c) => c === p[0]);
  const needle = repeated ? p[0] : p;
  const start = needle.length === 1 ? from + 1 : from;
  for (let i = 0; i < rows.length; i++) {
    const at = (start + i) % rows.length;
    if (rows[at].label.toLowerCase().startsWith(needle)) return at;
  }
  return -1;
}

/** The parent row of the row at `index` (the nearest row above it that is shallower); -1 at the top level. */
export function parentIndex(rows: readonly Pick<KeyRow, "depth">[], index: number): number {
  const depth = rows[index]?.depth ?? 0;
  for (let i = index - 1; i >= 0; i--) if (rows[i].depth < depth) return i;
  return -1;
}

/**
 * The action for a key on the row at `active`. `page` is the number of rows in a viewport. Returns null for a
 * key the tree does not handle (the event then goes on: F6, Tab, the palette's shortcut).
 */
export function treeKeyAction(rows: readonly KeyRow[], active: number, input: KeyInput, page = 10): TreeAction | null {
  // Alt+Left and Alt+Right move through the selection history (3.3); the tree leaves every Alt chord to the shell.
  if (rows.length === 0 || input.alt) return null;
  const last = rows.length - 1;
  const at = Math.min(Math.max(active, 0), last);
  const row = rows[at];
  const move = (index: number): TreeAction => ({ type: "move", index: Math.min(Math.max(index, 0), last), extend: !!input.shift });
  switch (input.key) {
    case "ArrowDown":
      return move(at + 1);
    case "ArrowUp":
      return move(at - 1);
    case "Home":
      return move(0);
    case "End":
      return move(last);
    case "PageDown":
      return move(at + page);
    case "PageUp":
      return move(at - page);
    case "ArrowRight":
      if (!row.expandable) return null;
      if (!row.expanded) return { type: "expand", index: at };
      return rows[at + 1] && rows[at + 1].depth > row.depth ? { type: "move", index: at + 1, extend: false } : null;
    case "ArrowLeft": {
      if (row.expandable && row.expanded) return { type: "collapse", index: at };
      const parent = parentIndex(rows, at);
      return parent >= 0 ? { type: "move", index: parent, extend: false } : null;
    }
    case "*":
      return { type: "expand-siblings", index: at };
    case "Enter":
      return { type: "open", index: at };
    case " ":
      return { type: "toggle-select", index: at };
    case "F2":
      return { type: "rename", index: at };
    case "Delete":
      return { type: "delete" };
    case "ContextMenu":
      return { type: "menu", index: at };
    case "F10":
      return input.shift ? { type: "menu", index: at } : null;
    default:
      return null;
  }
}

/** Whether a key is a printable character for type-ahead. */
export const isTypeAheadKey = (e: Pick<KeyboardEvent, "key" | "ctrlKey" | "metaKey" | "altKey">): boolean =>
  e.key.length === 1 && e.key !== " " && e.key !== "*" && !e.ctrlKey && !e.metaKey && !e.altKey;

/**
 * The tree's keydown handler: type-ahead (a buffer cleared after 700 ms of silence) and the actions of
 * `treeKeyAction`, handed to `apply`.
 */
export function useTreeKeyboard(options: {
  rows: () => readonly KeyRow[];
  active: number;
  page: number;
  apply: (action: TreeAction) => void;
}): (e: KeyboardEvent<HTMLElement>) => void {
  const buffer = useRef({ text: "", at: 0 });
  const { rows, active, page, apply } = options;
  return useCallback(
    (e: KeyboardEvent<HTMLElement>) => {
      if (e.target !== e.currentTarget) return;
      const list = rows();
      if (isTypeAheadKey(e)) {
        const now = Date.now();
        buffer.current = { text: (now - buffer.current.at > 700 ? "" : buffer.current.text) + e.key, at: now };
        const index = typeAhead(list, active, buffer.current.text);
        e.preventDefault();
        if (index >= 0) apply({ type: "move", index, extend: false });
        return;
      }
      const action = treeKeyAction(list, active, { key: e.key, shift: e.shiftKey, ctrl: e.ctrlKey || e.metaKey, alt: e.altKey }, page);
      if (!action) return;
      e.preventDefault();
      apply(action);
    },
    [rows, active, page, apply],
  );
}
