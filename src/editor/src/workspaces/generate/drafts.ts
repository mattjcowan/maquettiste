// Unsaved pack edits outlive the component that holds them: switching or closing a pack tab, opening Plan or leaving
// the Generate workspace unmounts the pack editor, and its Units draft and template buffers come back when it
// mounts again. The cache lives for the page; `hasUnsaved` drives the close confirmation and the page-unload warning.
import { useCallback, useState, type SetStateAction } from "react";

const cache = new Map<string, unknown>();
const dirty = new Map<string, boolean>();

/** useState whose value is kept under `key` after unmount. */
export function useDraftState<T>(key: string, initial: T | (() => T)): [T, (next: SetStateAction<T>) => void] {
  const [value, setValue] = useState<T>(() => {
    if (cache.has(key)) return cache.get(key) as T;
    const v = typeof initial === "function" ? (initial as () => T)() : initial;
    cache.set(key, v);
    return v;
  });
  const set = useCallback(
    (next: SetStateAction<T>) =>
      setValue((prev) => {
        const v = typeof next === "function" ? (next as (p: T) => T)(prev) : next;
        cache.set(key, v);
        return v;
      }),
    [key],
  );
  return [value, set];
}

/** Records whether a part of a pack (`<pack>:units`, `<pack>:templates`) has unsaved edits. */
export function markUnsaved(key: string, value: boolean): void {
  if (value) dirty.set(key, true);
  else dirty.delete(key);
}

/** Whether the pack (or, with no argument, any pack) has unsaved edits. */
export function hasUnsaved(pack?: string): boolean {
  for (const key of dirty.keys()) if (pack === undefined || key.startsWith(`${pack}:`)) return true;
  return false;
}

/** Drops a pack's kept drafts (after the user chose to discard them). */
export function discardDrafts(pack: string): void {
  for (const key of [...cache.keys()]) if (key.startsWith(`${pack}:`)) cache.delete(key);
  for (const key of [...dirty.keys()]) if (key.startsWith(`${pack}:`)) dirty.delete(key);
}
