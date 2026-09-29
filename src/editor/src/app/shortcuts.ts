// Global keyboard: Ctrl/Cmd+K palette, Ctrl/Cmd+P quick open, Ctrl/Cmd+Z undo, Ctrl/Cmd+Shift+Z or Ctrl+Y redo, F6 cycles
// regions (rail, explorer, center, inspector, bottom; phase2-design.md 4.8), Alt+Left and Alt+Right move back and
// forward through selections and Shift+F12 lists where the selected element is used (explorer-redesign.md 3.3).
import { useCallback, useEffect } from "react";
import { useServices } from "./context";
import { useEditorNavigation } from "./navigation";

export const REGIONS = ["rail", "explorer", "center", "inspector", "bottom"] as const;

function isTextTarget(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  if (!el) return false;
  return el.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(el.tagName) || !!el.closest(".monaco-editor");
}

export function focusRegion(offset: 1 | -1): void {
  const regions = REGIONS.map((r) => document.querySelector<HTMLElement>(`[data-region="${r}"]`)).filter((el): el is HTMLElement => !!el);
  if (!regions.length) return;
  const current = regions.findIndex((el) => el.contains(document.activeElement));
  const next = regions[(current + offset + regions.length) % regions.length];
  const target = next.querySelector<HTMLElement>("[data-region-focus]") ?? next;
  if (!target.hasAttribute("tabindex") && target === next) next.tabIndex = -1;
  target.focus();
}

export function useUndoRedo() {
  const { undo, store } = useServices();
  const run = useCallback(
    async (direction: "undo" | "redo") => {
      const result = direction === "undo" ? await undo.undo() : await undo.redo();
      if (result.ok) store.getState().notify(`${direction === "undo" ? "Undid" : "Redid"}: ${result.label}`);
      else store.getState().notify(result.reason, "error");
    },
    [undo, store],
  );
  return { undo: () => run("undo"), redo: () => run("redo") };
}

export function useGlobalShortcuts(): void {
  const { store } = useServices();
  const { undo, redo } = useUndoRedo();
  const { travel } = useEditorNavigation();
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const mod = e.ctrlKey || e.metaKey;
      if (!e.defaultPrevented && e.altKey && !mod && !e.shiftKey && (e.key === "ArrowLeft" || e.key === "ArrowRight") && !isTextTarget(e.target)) {
        e.preventDefault();
        travel(e.key === "ArrowLeft" ? "back" : "forward");
      } else if (!e.defaultPrevented && e.key === "F12" && e.shiftKey && !mod) {
        const id = store.getState().selection[0];
        if (id) {
          e.preventDefault();
          store.getState().showReferences(id);
        }
      } else if (mod && e.key.toLowerCase() === "k") {
        e.preventDefault();
        store.getState().setPaletteOpen(!store.getState().paletteOpen);
      } else if (mod && !e.shiftKey && e.key.toLowerCase() === "p") {
        e.preventDefault();
        store.getState().setQuickOpen(!store.getState().quickOpen);
      } else if (e.key === "F6") {
        e.preventDefault();
        focusRegion(e.shiftKey ? -1 : 1);
      } else if (mod && !isTextTarget(e.target) && e.key.toLowerCase() === "z") {
        e.preventDefault();
        void (e.shiftKey ? redo() : undo());
      } else if (mod && !isTextTarget(e.target) && e.key.toLowerCase() === "y") {
        e.preventDefault();
        void redo();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [store, undo, redo, travel]);
}
