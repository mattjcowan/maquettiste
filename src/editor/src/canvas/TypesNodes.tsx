// The nodes of a relation with an end of several types (severalTypes.ts): the junction, the small fork of bundled lines, the
// box of a stereotype or any entity, and the list box of the collapsed look. Each has the same four handles as a card.
import { memo, type MouseEvent } from "react";
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { Merge, Split } from "lucide-react";
import { cn } from "@/lib/cn";
import { toggleLabel, type HubKind, type ListGroup, type PlanEntry } from "./severalTypes";

export interface TypesNodeData extends Record<string, unknown> {
  kind: HubKind;
  relationId: string;
  relationName: string;
  entry?: PlanEntry;
  entries?: PlanEntry[];
  count?: number;
  /** "+2 not shown" and the hidden entries' names for its tooltip. */
  notShown?: { text: string; names: string } | null;
  /** Several types at both ends: "+N not shown" per end (first end left, second right) and the tooltip that lists every entry. The relation's name sits on a pill over the node. */
  both?: { notShown: readonly [{ text: string; names: string } | null, { text: string; names: string } | null]; title: string };
  /** The collapsed both-ends box: each end's role and its entries. */
  groups?: ListGroup[];
  canToggle: boolean;
  collapsed: boolean;
  onToggle?: (relationId: string) => void;
}

export type TypesFlowNode = Node<TypesNodeData, "types">;

/** The toggle's button: Collapse lines when the lines are drawn, Expand lines when they are collapsed. */
export function ToggleButton({ collapsed, onClick, testid }: { collapsed: boolean; onClick: (e: MouseEvent) => void; testid: string }) {
  const label = toggleLabel(collapsed);
  const Icon = collapsed ? Split : Merge;
  return (
    <button
      type="button"
      className="nodrag nopan inline-flex size-4 shrink-0 items-center justify-center rounded-control text-secondary hover:bg-accent-subtle hover:text-primary"
      title={label}
      aria-label={label}
      onClick={onClick}
      data-testid={testid}
    >
      <Icon className="size-3" aria-hidden />
    </button>
  );
}

function Handles() {
  return (
    <>
      <Handle type="target" position={Position.Left} id="l" className="!size-1 !opacity-0" isConnectable={false} />
      <Handle type="source" position={Position.Right} id="r" className="!size-1 !opacity-0" isConnectable={false} />
      <Handle type="target" position={Position.Right} id="tr" className="!size-1 !opacity-0" isConnectable={false} />
      <Handle type="source" position={Position.Left} id="sl" className="!size-1 !opacity-0" isConnectable={false} />
    </>
  );
}

function TypesNodeView({ data, selected }: NodeProps<TypesFlowNode>) {
  const { kind, relationName, entry, entries, groups, count, notShown, both, canToggle, collapsed, onToggle } = data;
  const toggle = (e: MouseEvent) => {
    e.stopPropagation();
    onToggle?.(data.relationId);
  };
  const border = selected ? "border-accent" : "border-strong";
  if (kind === "fork") {
    return (
      <div className="size-2 rounded-full bg-[var(--mq-border-strong)]" data-testid={`relation-fork-${relationName}`}>
        <Handles />
      </div>
    );
  }
  if (kind === "junction" && both) {
    return (
      <div className="relative size-4" title={both.title} data-testid={`relation-node-${relationName}`}>
        <div className={cn("m-0.5 size-3 rounded-full border", border, selected ? "bg-accent" : "bg-[var(--mq-border-strong)]")} />
        <div
          className={cn(
            "nodrag nopan absolute bottom-5 left-1/2 flex -translate-x-1/2 items-center gap-1 whitespace-nowrap rounded-full border bg-surface px-2 py-0.5 text-11 font-medium text-primary",
            selected ? "border-accent" : "border-default",
          )}
          data-testid={`relation-label-${relationName}`}
        >
          <span>{relationName}</span>
          {canToggle ? <ToggleButton collapsed={collapsed} onClick={toggle} testid={`relation-node-toggle-${relationName}`} /> : null}
        </div>
        {both.notShown.map((n, i) =>
          n ? (
            <span
              key={i}
              className={cn("absolute top-5 whitespace-nowrap text-11 text-secondary", i === 0 ? "right-4" : "left-4")}
              title={n.names}
              data-testid={`relation-not-shown-${relationName}-${i}`}
            >
              {n.text}
            </span>
          ) : null,
        )}
        <Handles />
      </div>
    );
  }
  if (kind === "junction") {
    return (
      <div className="relative size-4" data-testid={`relation-node-${relationName}`}>
        <div className={cn("m-0.5 size-3 rounded-full border", border, selected ? "bg-accent" : "bg-[var(--mq-border-strong)]")} />
        <div className="nodrag nopan absolute left-5 top-0 flex items-center gap-1 whitespace-nowrap text-11 text-secondary">
          {canToggle ? <ToggleButton collapsed={collapsed} onClick={toggle} testid={`relation-node-toggle-${relationName}`} /> : null}
          {notShown ? (
            <span title={notShown.names} data-testid={`relation-not-shown-${relationName}`}>
              {notShown.text}
            </span>
          ) : null}
        </div>
        <Handles />
      </div>
    );
  }
  if (kind === "list") {
    return (
      <div
        className={cn(
          "overflow-hidden rounded-panel border bg-surface text-primary",
          // The both-ends box lists whole entries: it grows to its longest line (and wraps past 340), where a one-end list keeps its 190.
          groups ? "w-max min-w-[190px] max-w-[340px]" : "w-[190px]",
          border,
        )}
        data-testid={`relation-list-${relationName}`}
        aria-label={`Types of ${relationName}`}
      >
        <div className="flex h-6 items-center gap-1 border-b border-default px-2 text-12 font-semibold">
          <span className="min-w-0 flex-1 truncate">{relationName}</span>
          {canToggle ? <ToggleButton collapsed={collapsed} onClick={toggle} testid={`relation-node-toggle-${relationName}`} /> : null}
        </div>
        <ul className="py-0.5">
          {groups
            ? groups.map((g, i) => {
                const names = g.entries.map((e) => e.label).join(", ");
                return (
                  <li key={i} className="flex min-h-[18px] items-center px-2 text-12 leading-4" title={names} data-testid="relation-list-group">
                    <span className="min-w-0 flex-1 whitespace-normal break-words">{g.text ? `${g.text}: ${names}` : names}</span>
                  </li>
                );
              })
            : null}
          {(groups ? [] : (entries ?? [])).map((e, i) => (
            <li key={i} className="flex h-[18px] items-center px-2 text-12" data-testid="relation-list-entry">
              <span className="min-w-0 flex-1 truncate">{e.label}</span>
              {e.kind === "stereotype" && e.count !== undefined ? <span className="shrink-0 text-11 text-secondary">{e.count}</span> : null}
            </li>
          ))}
        </ul>
        <Handles />
      </div>
    );
  }
  // A stereotype box, or "Any entity".
  return (
    <div
      className={cn("w-[190px] rounded-panel border bg-surface px-2 py-1 text-12 text-primary", border)}
      data-testid={kind === "any" ? `relation-any-${relationName}` : "relation-stereotype-box"}
    >
      <div className="truncate font-semibold">{entry?.label ?? "Any entity"}</div>
      {kind === "stereotype" && count !== undefined ? <div className="text-11 text-secondary">{count === 1 ? "1 entity" : `${count} entities`}</div> : null}
      <Handles />
    </div>
  );
}

export const TypesNode = memo(TypesNodeView);
