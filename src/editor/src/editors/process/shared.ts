// What every process editor tab reads: the process document from the draft, one committed change per gesture, the
// problems of the process (the draft's diagnostics while it is refused, else the Problems store's report), and the
// actors for the pickers.
import { useMemo } from "react";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { focusProcess, type ProcessTab } from "@/app/processFocus";
import { useIndex, useValidation } from "@/api/queries";
import type { Diagnostic, ElementSummary } from "@/api/types";
import { indexLookup } from "@/model/index";
import type { FormProps } from "@/inspector/fields";
import type { ProcessDoc } from "@/model/process";
import type { GridTarget } from "./Grid";

export interface ProcessContext {
  id: string;
  process: ProcessDoc;
  /** One change: edits the draft and saves it (one undo step). */
  change: (update: (process: ProcessDoc) => void) => void;
  /** An edit that is saved later (typing); `commit` saves. */
  draftEdit: (update: (process: ProcessDoc) => void) => void;
  commit: () => void;
  problems: Diagnostic[];
  actors: ElementSummary[];
  actorNames: Map<string, string>;
}

export function useProblems(id: string, draft: Diagnostic[]): Diagnostic[] {
  const validation = useValidation();
  return useMemo(() => {
    if (draft.length) return draft;
    return (validation.data?.diagnostics ?? []).filter((d) => d.elementId === id);
  }, [validation.data, draft, id]);
}

export function useActors(): { actors: ElementSummary[]; actorNames: Map<string, string> } {
  const index = useIndex();
  return useMemo(() => {
    const actors = indexLookup(index.data).ofKind("actor");
    return { actors, actorNames: new Map(actors.map((a) => [a.id, a.name])) };
  }, [index.data]);
}

export function useProcessContext(props: FormProps): ProcessContext {
  const { id, json, edit, flush, diagnostics } = props;
  const problems = useProblems(id, diagnostics);
  const { actors, actorNames } = useActors();
  return {
    id,
    process: json as unknown as ProcessDoc,
    change: (update) => {
      edit((j) => void update(j as unknown as ProcessDoc));
      flush();
    },
    draftEdit: (update) => edit((j) => void update(j as unknown as ProcessDoc)),
    commit: flush,
    problems,
    actors,
    actorNames,
  };
}

/** A row's own problems: at its pointer or its fields, not its nested states. */
export function rowProblems(problems: Diagnostic[], pointer: string): { rule: string; message: string }[] {
  return problems.filter((d) => {
    const p = d.jsonPointer ?? "";
    return p === pointer || (p.startsWith(`${pointer}/`) && !p.startsWith(`${pointer}/states/`));
  });
}

/** F12 targets for the process grids: another element (an actor, a process, a scenario), or a node of this process
 * (a state, an event, a guard or an action) shown on its tab. Shift+F12 lists where the id is used. */
export function useGridTargets(process: string) {
  const { goTo } = useEditorNavigation();
  const { store } = useServices();
  const whereUsed = (id: string) => () => store.getState().showReferences(id);
  return {
    element: (id: string | null | undefined): GridTarget | null => (id ? { go: () => goTo(id), whereUsed: whereUsed(id) } : null),
    node: (tab: ProcessTab, id: string | null | undefined): GridTarget | null =>
      id ? { go: () => void focusProcess(process, tab, tab === "states" || tab === "events" ? id : null), whereUsed: whereUsed(id) } : null,
  };
}
