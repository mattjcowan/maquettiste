// The explorer's edits (explorer-redesign.md 1.8): rename, move to a domain (the menu and drag and drop) and
// delete, through the draft manager and the element endpoints like the inspector's own edits, so they save,
// undo and conflict the same way.
import { useCallback } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { applySaveResult, elementQuery, keys } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ElementDocument, ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { newId } from "@/lib/ids";

const clone = <T>(v: T): T => structuredClone(v);

export function useExplorerActions() {
  const qc = useQueryClient();
  const { drafts, store } = useServices();

  const edit = useCallback(
    async (id: string, update: (json: Record<string, unknown>) => void) => {
      const base = await qc.fetchQuery(elementQuery(id));
      drafts.edit(id, (json) => update(json as unknown as Record<string, unknown>), { base });
      await drafts.flush(id);
    },
    [qc, drafts],
  );

  const rename = useCallback(
    async (id: string, name: string) => {
      await edit(id, (json) => {
        json.name = name;
      });
    },
    [edit],
  );

  /** Moves elements to a domain (null: out of every domain). A domain's own parent is its `parent` field. */
  const move = useCallback(
    async (ids: readonly string[], kinds: ReadonlyMap<string, string>, domain: string | null) => {
      for (const id of ids) {
        const field = kinds.get(id) === "package" ? "parent" : "package";
        await edit(id, (json) => {
          if (domain) json[field] = domain;
          else delete json[field];
        });
      }
      store.getState().notify(`Moved ${ids.length === 1 ? "1 element" : `${ids.length} elements`}.`);
    },
    [edit, store],
  );

  /** Deletes elements, refusing any that is still referenced (the inspector's delete clears references). */
  const remove = useCallback(
    async (ids: readonly string[], names: ReadonlyMap<string, string>) => {
      const deleted: string[] = [];
      const before: ModelJson[] = [];
      const refused: string[] = [];
      for (const id of ids) {
        await drafts.flush(id);
        const current = qc.getQueryData<ElementDocument>(keys.element(id)) ?? (await qc.fetchQuery(elementQuery(id)));
        if (!current) continue;
        const result = await endpoints.deleteElement(id, current.hash, "refuse");
        if (result.outcome === "saved") {
          applySaveResult(qc, result);
          drafts.discard(id);
          deleted.push(id);
          before.push(clone(current.json as ModelJson));
        } else refused.push(names.get(id) ?? id);
      }
      const s = store.getState();
      if (deleted.length) {
        s.pushUndo({
          label: `Delete ${deleted.length === 1 ? (names.get(deleted[0]) ?? "element") : `${deleted.length} elements`}`,
          ids: deleted,
          before,
          after: deleted.map(() => null),
          afterHashes: deleted.map(() => null),
        });
        s.select(s.selection.filter((x) => !deleted.includes(x)));
      }
      if (refused.length)
        s.notify(`Not deleted, still referenced: ${refused.join(", ")}. Open one in the inspector to delete it and clear its references.`, "error");
    },
    [qc, drafts, store],
  );

  /** Duplicate (1.8, diagrams): the same members and layout under a new id and "<name> copy"; opened once saved. */
  const duplicate = useCallback(
    async (id: string): Promise<string | null> => {
      await drafts.flush(id);
      const current = await qc.fetchQuery(elementQuery(id));
      if (!current) return null;
      const json = clone(current.json as ModelJson) as unknown as Record<string, unknown>;
      json.id = newId();
      json.name = `${String(json.name ?? "Diagram")} copy`;
      const result = await endpoints.createElement(json as unknown as ModelJson);
      const s = store.getState();
      if (result.outcome !== "saved") {
        s.notify(`Not duplicated: ${result.diagnostics[0]?.message ?? result.outcome}`, "error");
        return null;
      }
      applySaveResult(qc, result);
      const copy = String(json.id);
      s.pushUndo({ label: `Duplicate ${String(current.json.name ?? "")}`, ids: [copy], before: [null], after: [clone(json as unknown as ModelJson)], afterHashes: [result.hash] });
      s.select([copy]);
      s.notify(`Created ${String(json.name)}.`);
      return copy;
    },
    [qc, drafts, store],
  );

  return { rename, move, remove, duplicate };
}
