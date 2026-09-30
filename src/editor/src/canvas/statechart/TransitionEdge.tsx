// A transition on the statechart canvas (phase-3-design.md 6.3): a stepped line with an arrow, its label on a pill
// (`event [guard] / actions`, `after 30d`, `done`, `always`), the gate badge "2 of 3" with the signers in its tooltip,
// and an MQ9006 finding as a badge. Routes are not saved: they follow the states' positions. A transition from a state
// to itself loops over the state; one whose run would cross other states is lifted over them (routes.ts). The initial arrow (InitialEdge) runs from the initial dot to its state.
import { memo } from "react";
import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, getStraightPath, Position, type Edge, type EdgeProps } from "@xyflow/react";
import { ShieldCheck, TriangleAlert } from "lucide-react";
import { cn } from "@/lib/cn";
import type { Box } from "../layout";
import type { ChartBadge, GateBadge } from "./chartModel";
import { detourPath, isForward, meet, type Detour } from "./routes";

export interface TransitionEdgeData extends Record<string, unknown> {
  /** The source's and the target's boxes on the canvas: the edge leaves the side facing its target. */
  from: Box;
  to: Box;
  transition: string;
  label: string;
  gate: GateBadge | null;
  badges: ChartBadge[];
  /** Lifted over the states its straight run would cross (routes.ts), else null. */
  detour: Detour | null;
  onSelect: (transition: string) => void;
}

export type TransitionFlowEdge = Edge<TransitionEdgeData, "transition">;

/** The DOM id of a drawn edge's label (the canvas's aria-activedescendant names the selected transition's first edge). */
export const edgeDomId = (edge: string) => `mq-chart-edge-${edge}`;
export type InitialFlowEdge = Edge<Record<string, never>, "initial">;

/** The arrowheads: the default stroke and the selected (accent) stroke. */
export function ChartMarkers() {
  return (
    <svg aria-hidden style={{ position: "absolute", width: 0, height: 0 }}>
      <defs>
        <marker id="mq-sc-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M0 0 L10 5 L0 10 Z" fill="var(--mq-text-secondary)" />
        </marker>
        <marker id="mq-sc-arrow-selected" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M0 0 L10 5 L0 10 Z" fill="var(--mq-accent)" />
        </marker>
      </defs>
    </svg>
  );
}

/** An edge's ends from its states' boxes: right to left when the target lies ahead, left to right when it lies behind. */
export function edgeEnds(from: Box, to: Box, self: boolean) {
  const forward = self || isForward(from, to);
  return forward
    ? { sourceX: from.x + from.width, sourceY: meet(from), sourcePosition: Position.Right, targetX: to.x, targetY: meet(to), targetPosition: Position.Left }
    : { sourceX: from.x, sourceY: meet(from), sourcePosition: Position.Left, targetX: to.x + to.width, targetY: meet(to), targetPosition: Position.Right };
}

function TransitionEdgeView({ id, source, target, data, selected }: EdgeProps<TransitionFlowEdge>) {
  if (!data) return null;
  const { sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition } = edgeEnds(data.from, data.to, source === target);
  let path: string;
  let labelX: number;
  let labelY: number;
  if (source === target) {
    // A self transition: a loop over the state, from its right side to its left side.
    const top = Math.min(sourceY, targetY) - 40;
    path = `M ${sourceX} ${sourceY} C ${sourceX + 40} ${top}, ${targetX - 40} ${top}, ${targetX} ${targetY}`;
    labelX = (sourceX + targetX) / 2;
    labelY = top + 8;
  } else if (data.detour) [path, labelX, labelY] = detourPath(sourceX, sourceY, targetX, targetY, data.detour);
  else [path, labelX, labelY] = getSmoothStepPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, borderRadius: 6, offset: 16 });
  return (
    <>
      <BaseEdge
        id={id}
        path={path}
        markerEnd={selected ? "url(#mq-sc-arrow-selected)" : "url(#mq-sc-arrow)"}
        style={{ stroke: selected ? "var(--mq-accent)" : "var(--mq-text-secondary)", strokeWidth: selected ? 2 : 1.25 }}
        interactionWidth={14}
      />
      <EdgeLabelRenderer>
        <div
          className={cn(
            "nodrag nopan pointer-events-auto absolute flex max-w-56 cursor-pointer items-center gap-1 rounded-full border bg-surface px-1.5 text-11 leading-4 text-primary",
            selected ? "border-accent" : "border-default",
          )}
          // Above the containers: a label of a transition inside a container would otherwise sit under its box.
          style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)`, zIndex: 1001 }}
          onClick={(e) => {
            e.stopPropagation();
            data.onSelect(data.transition);
          }}
          id={edgeDomId(id)}
          title={data.label}
          data-testid="transition-label"
          data-transition={data.transition}
          data-selected={selected || undefined}
        >
          <span className="min-w-0 truncate">{data.label}</span>
          {data.gate ? (
            <span
              className="inline-flex shrink-0 items-center gap-0.5 text-secondary"
              title={data.gate.title}
              aria-label={data.gate.title}
              data-testid="gate-badge"
            >
              <ShieldCheck className="size-3" aria-hidden />
              {data.gate.text}
            </span>
          ) : null}
          {data.badges.length ? (
            <span
              className="inline-flex shrink-0 items-center text-warning"
              title={data.badges.map((b) => `${b.rule}: ${b.message}`).join("\n")}
              aria-label={data.badges.map((b) => `${b.rule}: ${b.message}`).join("\n")}
              data-testid="chart-problem"
              data-rules={data.badges.map((b) => b.rule).join(" ")}
            >
              <TriangleAlert className="size-3" aria-hidden />
            </span>
          ) : null}
        </div>
      </EdgeLabelRenderer>
    </>
  );
}

export const TransitionEdge = memo(TransitionEdgeView);

function InitialEdgeView({ id, sourceX, sourceY, targetX, targetY }: EdgeProps<InitialFlowEdge>) {
  const [path] = getStraightPath({ sourceX, sourceY, targetX, targetY });
  return <BaseEdge id={id} path={path} markerEnd="url(#mq-sc-arrow)" style={{ stroke: "var(--mq-text-secondary)", strokeWidth: 1.25 }} />;
}

export const InitialEdge = memo(InitialEdgeView);
