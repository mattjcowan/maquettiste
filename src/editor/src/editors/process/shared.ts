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

/**
 * The findings of one element. `entryIds` are the ids of its `subjects` entries: the engine reports a finding about one
 * entry under the entry's id, not the process's (lifecycle-design.md decision 5).
 */
export function useProblems(id: string, draft: Diagnostic[], entryIds: readonly string[] = []): Diagnostic[] {
  const validation = useValidation();
  const index = useIndex();
  const entries = entryIds.join("|");
  // A finding of a part (an event, a transition) carries the part's id; its file path names the element that owns it.
  const path = index.data?.find((r) => r.id === id)?.path;
  return useMemo(() => {
    if (draft.length) return draft;
    const own = new Set([id, ...(entries ? entries.split("|") : [])]);
    return (validation.data?.diagnostics ?? []).filter((d) => (d.elementId != null && own.has(d.elementId)) || (!!path && d.filePath === path));
  }, [validation.data, draft, id, entries, path]);
}

/** The ids of a process file's `subjects` entries. */
export function entryIdsOf(json: object): string[] {
  const { subjects } = json as { subjects?: unknown };
  return Array.isArray(subjects) ? (subjects as { id?: unknown }[]).flatMap((e) => (e && typeof e.id === "string" ? [e.id] : [])) : [];
}

export function useActors(): { actors: ElementSummary[]; actorNames: Map<string, string> } {
  const index = useIndex();
  return useMemo(() => {
    const actors = indexLookup(index.data).ofKind("actor");
    return { actors, actorNames: new Map(actors.map((a) => [a.id, a.name])) };
  }, [index.data]);
}

function withStates(json: unknown): ProcessDoc {
  const process = json as ProcessDoc;
  process.states ??= [];
  return process;
}

export function useProcessContext(props: FormProps): ProcessContext {
  const { id, json, edit, flush, diagnostics } = props;
  const problems = useProblems(id, diagnostics, entryIdsOf(json));
  const { actors, actorNames } = useActors();
  return {
    id,
    // A file with no states is MQ9020, and its problem shows here rather than a failed view.
    process: (json.states ? json : { ...json, states: [] }) as unknown as ProcessDoc,
    change: (update) => {
      edit((j) => void update(withStates(j)));
      flush();
    },
    draftEdit: (update) => edit((j) => void update(withStates(j))),
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
  const { goTo, openEditor } = useEditorNavigation();
  const index = useIndex();
  const { store } = useServices();
  const whereUsed = (id: string) => () => store.getState().showReferences(id);
  return {
    element: (id: string | null | undefined): GridTarget | null => (id ? { go: () => goTo(id), whereUsed: whereUsed(id) } : null),
    /** The element's own editor tab, as a double-click in the explorer opens it (a named right has no place in the lifecycle's tree). */
    open: (id: string | null | undefined): GridTarget | null =>
      id
        ? {
            go: () => {
              const summary = indexLookup(index.data).byId.get(id);
              if (summary) openEditor(summary, true);
            },
            whereUsed: whereUsed(id),
          }
        : null,
    node: (tab: ProcessTab, id: string | null | undefined): GridTarget | null =>
      id ? { go: () => void focusProcess(process, tab, tab === "states" || tab === "events" ? id : null), whereUsed: whereUsed(id) } : null,
  };
}
