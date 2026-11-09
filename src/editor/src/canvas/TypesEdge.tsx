// A line of a relation with an end of several types (severalTypes.ts): the trunk from the other end's card to the node, box or
// fork carries the relation's label and the toggle; a branch from the node to a type shows the far end's role and cardinality.
import { memo, type MouseEvent } from "react";
import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type Edge, type EdgeProps } from "@xyflow/react";
import { Paperclip } from "lucide-react";
import type { RelationDoc } from "@/api/types";
import { cn } from "@/lib/cn";
import { attributeBoxPlacement, labelOffset, type Notation } from "./RelationEdge";
import { AttributeRow } from "./EntityNode";
import { BOX_HEADER, relationBoxRows, relationBoxSize, relationLabelSize, type AttributeBox } from "./model";
import { lineMarkers, lineText } from "./severalTypes";
import { ToggleButton } from "./TypesNodes";

export interface TypesEdgeData extends Record<string, unknown> {
  relation: RelationDoc;
  notation: Notation;
  part: "trunk" | "branch";
  endIndex: 0 | 1;
  /** On the one line into a box or list: the far end, shown at the box. */
  farEndIndex?: 0 | 1;
  /** The Display menu's "Relation attributes": the attributes in a box off the label, as on any relation. */
  attributeBox?: AttributeBox | null;
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
  const { relation, notation, part, endIndex, farEndIndex } = data;
  const markers = lineMarkers(relation, notation, { part, endIndex, farEndIndex });
  const attributes = relation.attributes ?? [];
  const ends = { sourceX, sourceY, targetX, targetY };
  const box = data.labelled && data.attributeBox ? relationBoxSize(relation, data.attributeBox) : null;
  // With a box the pill carries only the name, as on any relation.
  const pill = relationLabelSize(box ? { name: relation.name } : relation);
  const place = box ? attributeBoxPlacement(ends, pill, box) : null;
  const nudge = place?.pill ?? labelOffset(ends, { width: pill.width + (data.canToggle ? 20 : 0), height: pill.height });
  // A role and cardinality sit at their line's card and run away from it, so a card never covers them; on the labelled line they sit
  // under the line, clear of the label.
  const texts: { text: string; x: number; y: number; pos: string; testid: string }[] = [];
  const nearText = lineText(relation, notation, endIndex);
  if (nearText) {
    const atStart = part === "trunk";
    texts.push({
      text: nearText,
      x: atStart ? sourceX : targetX,
      y: atStart ? sourceY : targetY,
      pos: atStart ? String(sourcePosition) : String(targetPosition),
      testid: `relation-end-${relation.name}-${endIndex}`,
    });
  }
  const farText = farEndIndex === undefined ? "" : lineText(relation, notation, farEndIndex);
  if (farText) texts.push({ text: farText, x: targetX, y: targetY, pos: String(targetPosition), testid: `relation-end-${relation.name}-${farEndIndex}` });
  const under = data.labelled;
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
            onClick={select}
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
        {texts.map((t) => {
          // The line leaves the card sideways: run the text away from the card, from just beside the line's end.
          const left = t.pos === "left";
          const x = left ? t.x - 4 : t.x + 4;
          const y = under ? t.y + 13 : t.y - 2;
          return (
            <span
              key={t.testid}
              className="pointer-events-none absolute whitespace-nowrap font-mono text-11 text-secondary"
              style={{ transform: `translate(${left ? "-100%" : "0"}, ${under ? "0" : "-100%"}) translate(${x}px, ${y}px)` }}
              data-testid={t.testid}
            >
              {t.text}
            </span>
          );
        })}
      </EdgeLabelRenderer>
    </>
  );
}

export const TypesEdge = memo(TypesEdgeView);
