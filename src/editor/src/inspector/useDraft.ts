import { useCallback } from "react";
import { useElement } from "@/api/queries";
import type { ModelJson } from "@/api/types";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";

/** The document an editor shows for an element (its draft, else the server's) and how to edit it. */
export function useDraftDocument(id: string | null) {
  const { drafts, store } = useServices();
  const element = useElement(id);
  const draft = useEditor(store, (s) => (id ? s.drafts[id] : undefined));
  const json = (draft?.json ?? element.data?.json) as ModelJson | undefined;
  const edit = useCallback(
    (update: (json: ModelJson) => ModelJson | void) => {
      if (!id) return;
      drafts.edit(id, update, { base: element.data });
    },
    [drafts, id, element.data],
  );
  const flush = useCallback(() => (id ? drafts.flush(id) : Promise.resolve()), [drafts, id]);
  return { element, draft, json, edit, flush };
}
