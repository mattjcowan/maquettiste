// A relation edge: the name on a pill, multiplicities in UML notation or crow's feet,
// composition and aggregation diamonds, and a badge when the relation has attributes; with the Display menu's
// "Relation attributes", the attributes in a box hung off the pill by a dashed line (the UML association class).
import { memo, type MouseEvent } from "react";
import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type Edge, type EdgeProps } from "@xyflow/react";
import { Paperclip } from "lucide-react";
import type { RelationDoc } from "@/api/types";
import { crowsFoot, multiplicity, relationKind } from "@/model/model";
import { cn } from "@/lib/cn";
import { AttributeRow } from "./EntityNode";
import { BOX_GAP, BOX_HEADER, LABEL_GAP, relationBoxRows, relationBoxSize, relationLabelSize, type AttributeBox } from "./model";

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

type Size = { width: number; height: number };
type Offset = { dx: number; dy: number };

/**
 * Where a relation's attribute box goes, relative to the edge's label point (the middle of the smooth-step path), and
 * the dashed connector that joins it to the pill (the UML association class). The pill stays where `labelOffset` puts
 * it, and the box hangs below it (above it when the pill was moved above a short run). Where the line runs level through
 * the label, the box is centered under the pill. Where the label sits on an upright stretch of the line (cards at
 * different heights, or a vertical run), the box moves beside that stretch, to the side the line does not turn toward,
 * so neither the box nor the connector sits on the line. Pure, so it can be tested.
 */
export function attributeBoxPlacement(
  ends: { sourceX: number; sourceY: number; targetX: number; targetY: number },
  pill: Size,
  box: Size,
): { pill: Offset; box: Offset; connector: { x1: number; y1: number; x2: number; y2: number } } {
  const nudge = labelOffset(ends, pill);
  const side = nudge.dy < 0 ? -1 : 1;
  const from = nudge.dy + (side * pill.height) / 2;
  const dy = from + side * (BOX_GAP + box.height / 2);
  // The upright stretch through the label point reaches this far above and below it.
  const reach = Math.abs(ends.targetY - ends.sourceY) / 2;
  if (reach <= pill.height / 2 + BOX_GAP)
    return { pill: nudge, box: { dx: nudge.dx, dy }, connector: { x1: nudge.dx, y1: from, x2: nudge.dx, y2: from + side * BOX_GAP } };
  // The end of the stretch on the box's side (below: the lower one) turns toward its card; the box goes the other way.
  const midX = (ends.sourceX + ends.targetX) / 2;
  const targetOnBoxSide = side > 0 ? ends.targetY > ends.sourceY : ends.targetY < ends.sourceY;
  const turn = Math.sign((targetOnBoxSide ? ends.targetX : ends.sourceX) - midX);
  const away = turn > 0 ? -1 : 1;
  const x = away * Math.max(pill.width / 4, BOX_GAP / 2 + 2);
  return {
    pill: nudge,
    box: { dx: away * (BOX_GAP / 2 + box.width / 2), dy },
    connector: { x1: x, y1: from, x2: x, y2: from + side * BOX_GAP },
  };
}

export type Notation = "uml" | "crowsfoot";

export interface RelationEdgeData extends Record<string, unknown> {
  relation: RelationDoc;
  notation: Notation;
  /** The attribute box's detail when the Display menu's "Relation attributes" is on, else null. */
  attributeBox?: AttributeBox | null;
  /** Selects the relation (a click on its pill or its box); with `additive`, adds it to the selection. */
  onSelect?: (id: string, additive: boolean) => void;
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
  const ends = { sourceX, sourceY, targetX, targetY };
  const box = data.attributeBox ? relationBoxSize(relation, data.attributeBox) : null;
  // With a box the pill carries only the name: the box lists what the badge counts.
  const pill = relationLabelSize(box ? { name: relation.name } : relation);
  const place = box ? attributeBoxPlacement(ends, pill, box) : null;
  const nudge = place?.pill ?? labelOffset(ends, pill);
  const onClick = (e: MouseEvent) => {
    e.stopPropagation();
    data.onSelect?.(id, e.shiftKey);
  };
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
      {place ? (
        <path
          d={`M ${labelX + place.connector.x1} ${labelY + place.connector.y1} L ${labelX + place.connector.x2} ${labelY + place.connector.y2}`}
          fill="none"
          stroke={selected ? "var(--mq-accent)" : "var(--mq-border-strong)"}
          strokeWidth={1.25}
          strokeDasharray="4 3"
          data-testid={`relation-box-connector-${relation.name}`}
        />
      ) : null}
      <EdgeLabelRenderer>
        <div
          className={cn(
            "nodrag nopan pointer-events-auto absolute flex cursor-pointer items-center gap-1 rounded-full border bg-surface px-2 py-0.5 text-11 font-medium text-primary",
            selected ? "border-accent" : "border-default",
          )}
          style={{ transform: `translate(-50%, -50%) translate(${labelX + nudge.dx}px, ${labelY + nudge.dy}px)` }}
          onClick={onClick}
          data-testid={`relation-label-${relation.name}`}
        >
          <span>{relation.name}</span>
          {attributes.length && !box ? (
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
        {place && box && data.attributeBox ? (
          <div
            className={cn(
              "nodrag nopan pointer-events-auto absolute cursor-pointer overflow-hidden rounded-panel border bg-surface text-primary",
              selected ? "border-accent" : "border-strong",
            )}
            style={{
              width: box.width,
              height: box.height,
              transform: `translate(-50%, -50%) translate(${labelX + place.box.dx}px, ${labelY + place.box.dy}px)`,
            }}
            onClick={onClick}
            data-testid={`relation-box-${relation.name}`}
          >
            <div className="truncate border-b border-default px-2 text-12 font-semibold" style={{ height: BOX_HEADER, lineHeight: `${BOX_HEADER - 1}px` }}>
              {relation.name}
            </div>
            <ul className="py-0.5" aria-label={`Attributes of ${relation.name}`}>
              {relationBoxRows(relation, data.attributeBox.mode).map((a) => (
                <AttributeRow
                  key={a.id}
                  attribute={a}
                  isKey={false}
                  type={data.attributeBox!.mode === "names" ? null : data.attributeBox!.typeLabel(a)}
                  testid="relation-attribute"
                />
              ))}
            </ul>
          </div>
        ) : null}
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
