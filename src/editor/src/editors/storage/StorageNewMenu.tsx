// The Domain model explorer's New menu item for the entity side's storage: New entities from tables… (StorageDialogs.tsx).
import { useIndex } from "@/api/queries";
import { useServices } from "@/app/context";
import { DropdownMenuItem } from "@/components/ui/menu";
import { indexLookup } from "@/model/index";
import { useEditor } from "@/state/store";
import { STORAGE_LABELS } from "./labels";

export function StorageNewMenuItems() {
  const { store } = useServices();
  const index = useIndex();
  // New entities go to the domain of the selection, when there is one.
  const selected = useEditor(store, (s) => s.selection[0]);
  const row = selected ? indexLookup(index.data).byId.get(selected) : undefined;
  const domain = row?.kind === "package" ? row.id : (row?.package ?? null);
  return (
    <DropdownMenuItem
      data-testid="explorer-new-entities-from-tables"
      onSelect={() => store.getState().requestStorage({ action: "entities-from-tables", entities: [], domain })}
    >
      {STORAGE_LABELS.entitiesFromTables}
    </DropdownMenuItem>
  );
}
