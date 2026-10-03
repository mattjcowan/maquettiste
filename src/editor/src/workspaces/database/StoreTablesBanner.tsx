// The Database screen's one banner for the tables the model lays out by convention: "N tables are not stored as table files
// yet", with Store as table files, which previews the change and then stores them all in one batch (one undo step; the engine's
// schema snapshot remembers each table's former key, so neither the store nor its undo reads as a drop and a create in the next
// migration). It floats over the diagram's bottom edge, clear of the zoom controls and the minimap in the corners.
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Database } from "lucide-react";
import type { TableView } from "@/api/types";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Spinner } from "@/components/ui/misc";
import { storeTables, useStorableTables } from "./storeTables";

export function StoreTablesBanner({ database, tables }: { database: string | null; tables: readonly TableView[] | undefined }) {
  const services = useServices();
  const qc = useQueryClient();
  const storable = useStorableTables(database, tables);
  const [busy, setBusy] = useState(false);
  if (!database || !storable.size) return null;
  const n = storable.size;
  return (
    // Over the diagram's bottom edge, so the diagram keeps its size (and its fitted view) whether the banner shows or not; only
    // its button takes the pointer, so a table under the words stays clickable.
    <div
      role="status"
      className="pointer-events-none absolute bottom-2 left-1/2 z-10 flex max-w-[calc(100%-1rem)] -translate-x-1/2 items-center gap-2 rounded-control border border-default bg-surface px-2 py-1 text-12 shadow-sm"
      data-testid="store-tables-banner"
    >
      <span className="min-w-0 flex-1">
        {n === 1 ? "1 table is" : `${n} tables are`} not stored as table files yet. Store them as files to edit every part of them.
      </span>
      {busy ? <Spinner label="Storing the tables" /> : null}
      <Button
        size="sm"
        className="pointer-events-auto"
        disabled={busy}
        title="Store every table counted here as a table file, in one step. Undo puts them back; either way the next migration sees the same tables (no drop and create). Tables laid out for an inheritance hierarchy or an abstract type, and junction tables, are left out: they cannot be stored as files yet."
        onClick={() => {
          setBusy(true);
          void storeTables(services, qc, database, storable, `Store ${n === 1 ? "1 table" : `${n} tables`} as table files`)
            .then((r) => {
              if (r.ok) services.store.getState().notify(`${r.files.size === 1 ? "1 table is" : `${r.files.size} tables are`} now stored as table files.`);
            })
            .catch((e: Error) => services.store.getState().notify(`The tables were not stored as files: ${e.message}`, "error"))
            .finally(() => setBusy(false));
        }}
        data-testid="store-tables"
      >
        <Database /> Store as table files
      </Button>
    </div>
  );
}
