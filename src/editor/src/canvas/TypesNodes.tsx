// The nodes of a relation with an end of several types (severalTypes.ts): the junction, the small fork of bundled lines, the
// box of a stereotype or any entity, and the list box of the collapsed look. Each has the same four handles as a card.
import { memo, type MouseEvent } from "react";
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { Merge, Split } from "lucide-react";
import { cn } from "@/lib/cn";
import { toggleLabel, type HubKind, type PlanEntry } from "./severalTypes";

export interface TypesNodeData extends Record<string, unknown> {
  kind: HubKind;
  relationId: string;
  relationName: string;
  entry?: PlanEntry;
  entries?: PlanEntry[];
  count?: number;
  /** "+2 not shown" and the hidden entries' names for its tooltip. */
  notShown?: { text: string; names: string } | null;
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
  const { kind, relationName, entry, entries, count, notShown, canToggle, collapsed, onToggle } = data;
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
  if (kind === "junction") {
    return (
      <div className="relative size-4" data-testid={`relation-node-${relationName}`}>
        <div className={cn("size-4 rounded-full border-2 bg-surface", border)} />
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
        className={cn("w-[190px] overflow-hidden rounded-panel border bg-surface text-primary", border)}
        data-testid={`relation-list-${relationName}`}
        aria-label={`Types of ${relationName}`}
      >
        <div className="flex h-6 items-center gap-1 border-b border-default px-2 text-12 font-semibold">
          <span className="min-w-0 flex-1 truncate">{relationName}</span>
          {canToggle ? <ToggleButton collapsed={collapsed} onClick={toggle} testid={`relation-node-toggle-${relationName}`} /> : null}
        </div>
        <ul className="py-0.5">
          {(entries ?? []).map((e, i) => (
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
