import type { KeyboardEvent } from "react";

/**
 * Up and Down (Home and End) on a pick list of buttons, the master side of a list and its form: the focus moves to the
 * previous or next item and picks it, as its click does. Put it on the list's element; the items are the list's buttons
 * marked `aria-current` when picked, in document order. A key with a modifier, or one in another control, is left alone.
 */
export function onListArrowKeys(e: KeyboardEvent<HTMLElement>): void {
  if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
  if (e.key !== "ArrowDown" && e.key !== "ArrowUp" && e.key !== "Home" && e.key !== "End") return;
  const items = Array.from(e.currentTarget.querySelectorAll<HTMLButtonElement>("li > button:not(:disabled)"));
  const at = items.indexOf(e.target as HTMLButtonElement);
  if (at < 0) return;
  const to = e.key === "Home" ? 0 : e.key === "End" ? items.length - 1 : Math.max(0, Math.min(items.length - 1, at + (e.key === "ArrowDown" ? 1 : -1)));
  e.preventDefault();
  if (to === at) return;
  items[to].focus();
  items[to].click();
}
