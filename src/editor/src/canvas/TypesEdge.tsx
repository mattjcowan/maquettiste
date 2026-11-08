// A line of a relation with an end of several types (severalTypes.ts): the trunk from the other end's card to the node, box or
// fork carries the relation's label and the toggle; a branch from the node to a type shows the far end's role and cardinality.
import { memo, type MouseEvent } from "react";
import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type Edge, type EdgeProps } from "@xyflow/react";
import { Paperclip } from "lucide-react";
import type { RelationDoc } from "@/api/types";
import { cn } from "@/lib/cn";
import { labelOffset, type Notation } from "./RelationEdge";
import { relationLabelSize } from "./model";
import { lineMarkers, lineText } from "./severalTypes";
import { ToggleButton } from "./TypesNodes";

export interface TypesEdgeData extends Record<string, unknown> {
  relation: RelationDoc;
  notation: Notation;
  part: "trunk" | "branch";
  endIndex: 0 | 1;
  labelled: boolean;
  canToggle: boolean;
  collapsed: boolean;
  onToggle?: (relationId: string) => void;
  onSelect?: (id: string, additive: boolean) => void;
}

export type TypesFlowEdge = Edge<TypesEdgeData, "types">;

function TypesEdgeView({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data, selected }: EdgeProps<TypesFlowEdge>) {
  const [path, labelX, labelY] = getSmoothStepPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, borderRadius: 8 });
  if (!data) return null;
  const { relation, notation, part, endIndex } = data;
  const markers = lineMarkers(relation, notation, { part, endIndex });
  const text = lineText(relation, notation, endIndex);
  const pill = relationLabelSize(relation);
  const nudge = labelOffset({ sourceX, sourceY, targetX, targetY }, { width: pill.width + (data.canToggle ? 20 : 0), height: pill.height });
  const attributes = relation.attributes ?? [];
  const offset = (x: number, pos: string) => (pos === "right" ? x + 14 : pos === "left" ? x - 14 : x);
  // The text sits at the line's card: the trunk's start, a branch's end.
  const at = part === "trunk" ? { x: offset(sourceX, sourcePosition), y: sourceY } : { x: offset(targetX, targetPosition), y: targetY };
  const relationId = relation.id;
  const select = (e: MouseEvent) => {
    e.stopPropagation();
    data.onSelect?.(relationId, e.shiftKey);
  };
  return (
    <>
      <BaseEdge
        id={id}
        path={path}
        markerStart={markers.start}
        markerEnd={markers.end}
        style={{ stroke: selected ? "var(--mq-accent)" : "var(--mq-border-strong)", strokeWidth: selected ? 2 : 1.25 }}
        interactionWidth={16}
      />
      <EdgeLabelRenderer>
        {data.labelled ? (
          <div
            className={cn(
              "nodrag nopan pointer-events-auto absolute flex cursor-pointer items-center gap-1 rounded-full border bg-surface px-2 py-0.5 text-11 font-medium text-primary",
              selected ? "border-accent" : "border-default",
            )}
            style={{ transform: `translate(-50%, -50%) translate(${labelX + nudge.dx}px, ${labelY + nudge.dy}px)` }}
            onClick={select}
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
            {data.canToggle ? (
              <ToggleButton
                collapsed={data.collapsed}
                onClick={(e) => {
                  e.stopPropagation();
                  data.onToggle?.(relationId);
                }}
                testid={`relation-toggle-${relation.name}`}
              />
            ) : null}
          </div>
        ) : null}
        {text ? (
          <span
            className="pointer-events-none absolute whitespace-nowrap font-mono text-11 text-secondary"
            style={{ transform: `translate(-50%, -120%) translate(${at.x}px, ${at.y}px)` }}
            data-testid={`relation-end-${relation.name}-${endIndex}`}
          >
            {text}
          </span>
        ) : null}
      </EdgeLabelRenderer>
    </>
  );
}

export const TypesEdge = memo(TypesEdgeView);
