// The project's verb vocabulary (model/vocabularies/verbs.json, verbs-design.md decision 6) as the screens use it: the own verbs,
// the hidden built-in keys, and ways to change them. The file is created with the first own verb or the first hidden verb.
import { useMemo } from "react";
import { applySaveResult, useIndex } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { newId } from "@/lib/ids";
import { useDraftDocument } from "@/inspector/useDraft";
import type { OwnVerb } from "@/model/verbList";

export interface VerbVocabularyDoc {
  hidden?: string[];
  verbs?: OwnVerb[];
}

export function useVerbs() {
  const index = useIndex();
  const { queryClient, store } = useServices();
  const id = useMemo(() => index.data?.find((r) => r.kind === "verb-vocabulary")?.id ?? null, [index.data]);
  const draft = useDraftDocument(id);
  const doc = (draft.json ?? {}) as unknown as VerbVocabularyDoc;
  const own = useMemo(() => doc.verbs ?? [], [doc.verbs]);
  const hidden = useMemo(() => new Set(doc.hidden ?? []), [doc.hidden]);
  /** Creates the vocabulary with its first entries; resolves to the new id, or null when refused. */
  const create = async (first: VerbVocabularyDoc): Promise<string | null> => {
    const json = { kind: "verb-vocabulary", id: newId(), name: "verbs", ...first } as unknown as ModelJson;
    const result = await endpoints.createElement(json);
    if (result.outcome === "saved") {
      applySaveResult(queryClient, result);
      return String(result.id);
    }
    store.getState().notify(result.diagnostics[0]?.message ?? result.outcome, "error");
    return null;
  };
  return {
    id,
    loading: index.isPending || (id !== null && !draft.json),
    rows: index.data ?? [],
    own,
    hidden,
    edit: (update: (doc: VerbVocabularyDoc) => void) => draft.edit((j) => void update(j as unknown as VerbVocabularyDoc)),
    flush: draft.flush,
    create,
  };
}

/** Adds an own verb (creating the vocabulary when there is none), as one saved change. */
export function useAddVerb() {
  const verbs = useVerbs();
  return async (verb: Omit<OwnVerb, "id">): Promise<void> => {
    const entry: OwnVerb = { id: newId(), ...verb };
    if (verbs.id === null) await verbs.create({ verbs: [entry] });
    else {
      verbs.edit((d) => void (d.verbs = [...(d.verbs ?? []), entry]));
      await verbs.flush();
    }
  };
}
