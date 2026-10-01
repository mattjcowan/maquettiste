// Go to definition from the inspector (explorer-redesign.md 3.3): F12, or Ctrl/Cmd+click, on a field that references
// another element goes to it; Shift+F12 lists where that element is used.
import { useCallback, type KeyboardEvent, type MouseEvent } from "react";
import { useEditorNavigation } from "@/app/navigation";
import { useServices } from "@/app/context";

export const DEFINITION_HINT = "F12 or Ctrl+click: go to definition. Shift+F12: Used (what refers to it).";

export interface DefinitionProps {
  onKeyDown?: (e: KeyboardEvent<HTMLElement>) => void;
  onMouseDown?: (e: MouseEvent<HTMLElement>) => void;
  title?: string;
  "data-definition"?: string;
}

/** Handles F12, Shift+F12 and Ctrl/Cmd+click for a reference to `target`; true when it handled the event. */
export function useDefinition() {
  const { goTo } = useEditorNavigation();
  const { store } = useServices();
  const onKey = useCallback(
    (target: string | null | undefined, e: KeyboardEvent<HTMLElement>): boolean => {
      if (!target || e.key !== "F12" || e.ctrlKey || e.metaKey || e.altKey) return false;
      e.preventDefault();
      e.stopPropagation();
      if (e.shiftKey) store.getState().showReferences(target);
      else goTo(target);
      return true;
    },
    [goTo, store],
  );
  const onClick = useCallback(
    (target: string | null | undefined, e: MouseEvent<HTMLElement>): boolean => {
      if (!target || !(e.ctrlKey || e.metaKey) || e.button !== 0) return false;
      e.preventDefault();
      e.stopPropagation();
      goTo(target);
      return true;
    },
    [goTo],
  );
  /** Props for a control whose value references `target` (nothing when it references nothing). */
  const props = useCallback(
    (target: string | null | undefined): DefinitionProps =>
      target
        ? {
            onKeyDown: (e) => void onKey(target, e),
            onMouseDown: (e) => void onClick(target, e),
            title: DEFINITION_HINT,
            "data-definition": target,
          }
        : {},
    [onKey, onClick],
  );
  return { props, onKey, onClick };
}
