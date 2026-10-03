// The Domain model explorer's storage actions (the entity side's bulk storage, StorageDialogs.tsx), kept here so the
// explorer's menu data (explorer/menus.ts) and its runner (explorer/Explorer.tsx) each take them with one line.
import type { EditorStore, StorageAction } from "@/state/store";
import { STORAGE_LABELS } from "./labels";

export type StorageActionId = `storage:${StorageAction}`;

interface Target {
  type: string;
  kind?: string;
  element: boolean;
}

const item = (action: StorageAction, label: string, multi: boolean) => ({ id: `storage:${action}` as StorageActionId, label, multi });

/** The storage items of a row's menu: on an entity (one or many) and on a domain. */
export function storageMenuItems(t: Target) {
  if (t.type === "domain" && t.element)
    return [
      item("entities-from-tables", STORAGE_LABELS.entitiesFromTables, false),
      item("auto-map", STORAGE_LABELS.autoMap, false),
      item("create-tables", STORAGE_LABELS.createTables, false),
      item("remove-bindings", STORAGE_LABELS.removeBindings, false),
    ];
  if (t.kind === "entity" && t.element)
    return [
      item("auto-map", STORAGE_LABELS.autoMap, true),
      item("create-tables", STORAGE_LABELS.createTables, true),
      item("remove-bindings", STORAGE_LABELS.removeBindings, true),
    ];
  return [];
}

export function isStorageAction(action: string): action is StorageActionId {
  return action.startsWith("storage:");
}

/**
 * Runs a storage action from the explorer: `ids` are the picked rows' element ids (entities, or domains). A domain stands for
 * its entities (and is where New entities from tables puts them).
 */
export function runStorageAction(store: EditorStore, action: StorageActionId, ids: readonly string[], kindOf: (id: string) => string | undefined): void {
  const domains = ids.filter((id) => kindOf(id) === "package");
  const entities = ids.filter((id) => kindOf(id) === "entity");
  store.getState().requestStorage({ action: action.slice(8) as StorageAction, entities, domain: domains[0] ?? null });
}
