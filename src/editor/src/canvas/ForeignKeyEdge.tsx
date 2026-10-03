// A foreign key on the database diagram: crow's foot ends (at the referencing table: many, or zero or one when its key columns
// are unique; at the referenced table: exactly one when every key column is not null, else zero or one), the key's name on
// hover or while selected, and a tooltip with the columns and the rules. A click selects it (the canvas shows it in its key
// panel), a double click edits it, Delete removes it.
import { memo, useState } from "react";
import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type Edge, type EdgeProps } from "@xyflow/react";
import type { ForeignKeyView } from "@/api/types";
import { cn } from "@/lib/cn";

export type FkEndMarker = "one" | "zero-or-one" | "many";

export interface ForeignKeyEdgeData extends Record<string, unknown> {
  foreignKey: ForeignKeyView;
  ends: { source: FkEndMarker; target: FkEndMarker };
  /** The tooltip: the key's columns, the table it references and its rules, in words. */
  summary: string;
}
export type ForeignKeyFlowEdge = Edge<ForeignKeyEdgeData, "foreignKey">;

function ForeignKeyEdgeView({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, selected, data }: EdgeProps<ForeignKeyFlowEdge>) {
  const [hover, setHover] = useState(false);
  const [path, labelX, labelY] = getSmoothStepPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, borderRadius: 8, offset: 24 });
  if (!data) return null;
  const { foreignKey, ends } = data;
  return (
    <>
      <BaseEdge
        id={id}
        path={path}
        markerStart={`url(#mq-fk-${ends.source})`}
        markerEnd={`url(#mq-fk-${ends.target})`}
        style={{ stroke: selected ? "var(--mq-accent)" : "var(--mq-border-strong)", strokeWidth: selected ? 2 : 1.25 }}
        interactionWidth={0}
      />
      {/* The hit path: wide and transparent, it takes the hover (the label) and the tooltip; clicks reach the edge. */}
      <path
        d={path}
        fill="none"
        stroke="transparent"
        strokeWidth={14}
        className="react-flow__edge-interaction"
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => setHover(false)}
        data-testid={`fk-edge-${foreignKey.name}`}
        data-ends={`${ends.source}:${ends.target}`}
      >
        <title>{data.summary}</title>
      </path>
      {hover || selected ? (
        <EdgeLabelRenderer>
          <div
            className={cn(
              "nodrag nopan pointer-events-none absolute rounded-full border bg-surface px-2 py-0.5 font-mono text-11 text-primary",
              selected ? "border-accent" : "border-default",
            )}
            style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}
            data-testid={`fk-label-${foreignKey.name}`}
          >
            {foreignKey.name}
          </div>
        </EdgeLabelRenderer>
      ) : null}
    </>
  );
}

export const ForeignKeyEdge = memo(ForeignKeyEdgeView);
