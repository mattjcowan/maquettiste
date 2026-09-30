// The Chart tab (phase-3-design.md 6.2): the statechart canvas (6.3, canvas/statechart) with the simulation panel (6.4)
// docked below it.
import { StatechartCanvas } from "@/canvas/statechart/StatechartCanvas";
import type { ProcessContext } from "./shared";
import { SimulationPanel } from "./simulation/SimulationPanel";

export function ChartTab({ pc }: { pc: ProcessContext }) {
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="chart-tab">
      <StatechartCanvas key={pc.id} pc={pc} />
      <SimulationPanel pc={pc} />
    </div>
  );
}
