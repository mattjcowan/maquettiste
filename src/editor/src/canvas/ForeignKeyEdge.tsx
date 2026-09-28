import { memo } from "react";
import { BaseEdge, getSmoothStepPath, type Edge, type EdgeProps } from "@xyflow/react";
import type { ForeignKeyView } from "@/api/types";

export interface ForeignKeyEdgeData extends Record<string, unknown> {
  foreignKey: ForeignKeyView;
}
export type ForeignKeyFlowEdge = Edge<ForeignKeyEdgeData, "foreignKey">;

function ForeignKeyEdgeView({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, selected }: EdgeProps<ForeignKeyFlowEdge>) {
  const [path] = getSmoothStepPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, borderRadius: 8 });
  return (
    <BaseEdge id={id} path={path} markerEnd="url(#mq-arrow)" style={{ stroke: selected ? "var(--mq-accent)" : "var(--mq-border-strong)", strokeWidth: 1.25 }} />
  );
}

export const ForeignKeyEdge = memo(ForeignKeyEdgeView);
