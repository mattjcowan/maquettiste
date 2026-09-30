// The Settings hint for a project that declares no reference-data storage strategy (a project made before init declared
// them): one click declares the three the sql-ddl pack realizes, so the New reference type dialog offers them.
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { keys, useSettings } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { SettingsJson } from "@/api/types";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { needsStandardStrategies, withStandardStrategies } from "@/workspaces/reference-data/storageChoices";

export function StrategiesHint() {
  const settings = useSettings();
  const qc = useQueryClient();
  const { store } = useServices();
  const [busy, setBusy] = useState(false);
  const json = settings.data?.json as SettingsJson | undefined;
  if (!settings.data || !needsStandardStrategies(json as Parameters<typeof needsStandardStrategies>[0])) return null;
  const declare = async () => {
    setBusy(true);
    try {
      const next = withStandardStrategies(json as Parameters<typeof withStandardStrategies>[0]) as unknown as SettingsJson;
      const result = await endpoints.saveSettings(next, settings.data.hash);
      if (result.outcome === "saved" && result.current) {
        qc.setQueryData(keys.settings, result.current);
        void qc.invalidateQueries({ queryKey: keys.project });
        store.getState().notify("Declared the storage strategies lookup-table, check and native.");
      } else store.getState().notify(`The strategies were not declared: ${result.diagnostics?.[0]?.message ?? result.outcome}.`, "error");
    } finally {
      setBusy(false);
    }
  };
  return (
    <div
      role="note"
      data-testid="strategies-hint"
      className="mb-2 flex max-w-4xl items-center gap-2 rounded-control border border-default bg-surface p-2 text-12"
    >
      <span className="flex-1">
        This project declares no reference-data storage strategy, so a new reference type offers only Template-defined. The sql-ddl pack builds lookup tables,
        check constraints and native types.
      </span>
      <Button size="sm" variant="secondary" disabled={busy} onClick={() => void declare()}>
        Declare the standard storage strategies
      </Button>
    </div>
  );
}
