// A relation edge: the name on a pill, multiplicities in UML notation or crow's feet,
// composition and aggregation diamonds, and a badge when the relation has attributes.
import { memo } from "react";
import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type Edge, type EdgeProps } from "@xyflow/react";
import { Paperclip } from "lucide-react";
import type { RelationDoc } from "@/api/types";
import { crowsFoot, multiplicity, relationKind } from "@/model/model";
import { cn } from "@/lib/cn";

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
          style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}
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
