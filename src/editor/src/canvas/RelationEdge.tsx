// A relation edge: the name on a pill, multiplicities in UML notation or crow's feet,
// composition and aggregation diamonds, and a badge when the relation has attributes.
import { memo } from "react";
import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type Edge, type EdgeProps } from "@xyflow/react";
import { Paperclip } from "lucide-react";
import type { RelationDoc } from "@/api/types";
import { crowsFoot, multiplicity, relationKind } from "@/model/model";
import { cn } from "@/lib/cn";
import { LABEL_GAP, relationLabelSize } from "./model";

/**
 * Where the label sits: at the edge's midpoint when the run between the cards has room for the pill and a gap on each
 * side, else moved off the line (above a horizontal run, left of a vertical one) so it never covers a card. Pure, so it
 * can be tested.
 */
export function labelOffset(
  ends: { sourceX: number; sourceY: number; targetX: number; targetY: number },
  label: { width: number; height: number },
): { dx: number; dy: number } {
  const dx = Math.abs(ends.targetX - ends.sourceX);
  const dy = Math.abs(ends.targetY - ends.sourceY);
  const horizontal = dx >= dy;
  const run = horizontal ? dx : dy;
  const needed = (horizontal ? label.width : label.height) + 2 * LABEL_GAP;
  if (run >= needed) return { dx: 0, dy: 0 };
  return horizontal ? { dx: 0, dy: -(label.height / 2 + 6) } : { dx: -(label.width / 2 + 6), dy: 0 };
}

export type Notation = "uml" | "crowsfoot";

export interface RelationEdgeData extends Record<string, unknown> {
  relation: RelationDoc;
  notation: Notation;
}

export type RelationFlowEdge = Edge<RelationEdgeData, "relation">;

function RelationEdgeView({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data, selected }: EdgeProps<RelationFlowEdge>) {
  const [path, labelX, labelY] = getSmoothStepPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, borderRadius: 8 });
  if (!data) return null;
  const { relation, notation } = data;
  const [a, b] = relation.ends;
  const kind = relationKind(relation);
  const startMarker =
    kind === "composition"
      ? "url(#mq-diamond-filled)"
      : kind === "aggregation"
        ? "url(#mq-diamond-hollow)"
        : notation === "crowsfoot" && a
          ? `url(#mq-cf-${crowsFoot(a)})`
          : undefined;
  const endMarker = notation === "crowsfoot" && b ? `url(#mq-cf-${crowsFoot(b)})` : undefined;
  const attributes = relation.attributes ?? [];
  const offset = (x: number, pos: string) => (pos === "right" ? x + 14 : pos === "left" ? x - 14 : x);
  const nudge = labelOffset({ sourceX, sourceY, targetX, targetY }, relationLabelSize(relation));
  return (
    <>
      <BaseEdge
        id={id}
        path={path}
        markerStart={startMarker}
        markerEnd={endMarker}
        style={{ stroke: selected ? "var(--mq-accent)" : "var(--mq-border-strong)", strokeWidth: selected ? 2 : 1.25 }}
        interactionWidth={16}
      />
      <EdgeLabelRenderer>
        <div
          className={cn(
            "nodrag nopan pointer-events-auto absolute flex items-center gap-1 rounded-full border bg-surface px-2 py-0.5 text-11 font-medium text-primary",
            selected ? "border-accent" : "border-default",
          )}
          style={{ transform: `translate(-50%, -50%) translate(${labelX + nudge.dx}px, ${labelY + nudge.dy}px)` }}
          data-testid={`relation-label-${relation.name}`}
        >
          <span>{relation.name}</span>
          {attributes.length ? (
            <span
              className="inline-flex items-center gap-0.5 text-secondary"
              title={attributes.map((x) => x.name).join(", ")}
              aria-label={`${attributes.length} relation attributes`}
            >
              <Paperclip className="size-3" aria-hidden />
              {attributes.length}
            </span>
          ) : null}
        </div>
        {notation === "uml" && a && b ? (
          <>
            <span
              className="pointer-events-none absolute font-mono text-11 text-secondary"
              style={{ transform: `translate(-50%, -120%) translate(${offset(sourceX, sourcePosition)}px, ${sourceY}px)` }}
            >
              {multiplicity(a)}
            </span>
            <span
              className="pointer-events-none absolute font-mono text-11 text-secondary"
              style={{ transform: `translate(-50%, -120%) translate(${offset(targetX, targetPosition)}px, ${targetY}px)` }}
            >
              {multiplicity(b)}
            </span>
          </>
        ) : null}
      </EdgeLabelRenderer>
    </>
  );
}

export const RelationEdge = memo(RelationEdgeView);
