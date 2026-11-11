// The Chart tab (phase-3-design.md 6.2): the statechart canvas (6.3, canvas/statechart) with the simulation panel (6.4)
// docked below it. The canvas hears when the panel opens or closes, so the chart fits the room the panel leaves.
import { useState } from "react";
import { StatechartCanvas } from "@/canvas/statechart/StatechartCanvas";
import type { ProcessContext } from "./shared";
import { SimulationPanel } from "./simulation/SimulationPanel";

export function ChartTab({ pc }: { pc: ProcessContext }) {
  // null until the panel reports its first state (open or closed as the browser kept it): that one is no change.
  const [panelOpen, setPanelOpen] = useState<boolean | null>(null);
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="chart-tab">
      <StatechartCanvas key={pc.id} pc={pc} panelOpen={panelOpen} />
      <SimulationPanel pc={pc} onOpenChange={setPanelOpen} />
    </div>
  );
}
