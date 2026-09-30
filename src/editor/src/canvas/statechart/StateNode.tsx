// A state on the statechart canvas (phase-3-design.md 6.3): a rounded box with a 24 px header row and 12 px text; a
// compound or parallel state is a container holding its children (a parallel state's regions separated by dashed
// lines, a region drawn without a frame of its own); a collapsed container is one box with the count of what it
// hides; a final state is a ringed dot, a history state a circled H or H*, a choice a diamond, each beside its name.
// A lifecycle's bound root state shows its enum member in muted text; MQ9003 and MQ9004 findings show as a badge.
// Targetless transitions are rows inside their state. The initial pseudo-state is a filled dot (InitialNode).
import { memo, useEffect, useRef, useState } from "react";
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { ChevronDown, ChevronRight, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CHART_LABELS } from "@/model/labels";
import { cn } from "@/lib/cn";
import type { ChartBadge, ChartState } from "./chartModel";

export interface StateNodeData extends Record<string, unknown> {
  state: ChartState;
  /** An open container: its children are drawn inside it. */
  open: boolean;
  /** A collapsed container, with the number of states it hides. */
  hidden: number | null;
  /** x of each dashed line between a parallel state's regions. */
  separators: number[];
  /** Active in the simulation (an active atomic state or one of its ancestors). */
  active: boolean;
  /** The target a transition being drawn (T) would go to. */
  linkTarget: boolean;
  /** Renaming in place (F2). */
  editing: boolean;
  /** The selected transition, to mark its row when it is an internal one. */
  selectedTransition: string | null;
  onToggle: (id: string) => void;
  onRename: (id: string, name: string | null) => void;
  onSelectTransition: (transition: string) => void;
}

export type StateFlowNode = Node<StateNodeData, "state">;
export type InitialFlowNode = Node<Record<string, never>, "initial">;

function Handles({ label }: { label: string }) {
  // Hidden ends React Flow needs to draw an edge; the transition edge computes its own ends from the states' boxes
  // (forward edges leave right and enter left, backward ones the other way), so two per state are enough. The third
  // handle is the one the mouse draws from: a filled dot on the right edge of every state, grown while the pointer is on
  // it; dragging it onto another state creates a transition (the canvas finishes the drag, so the target needs no
  // handle).
  return (
    <>
      <Handle type="target" position={Position.Left} id="l" className="!size-1 !min-h-0 !min-w-0 !border-0 !opacity-0" isConnectable={false} />
      <Handle type="source" position={Position.Right} id="r" className="!size-1 !min-h-0 !min-w-0 !border-0 !opacity-0" isConnectable={false} />
      <Handle
        type="source"
        position={Position.Right}
        id="draw"
        className="mq-link-handle nodrag nopan !top-3 !size-3 !rounded-full !border-2 !border-surface !bg-accent"
        title={label}
        aria-label={label}
        data-testid="chart-link-handle"
      />
    </>
  );
}

function ProblemBadge({ badges }: { badges: ChartBadge[] }) {
  if (!badges.length) return null;
  const title = badges.map((b) => `${b.rule}: ${b.message}`).join("\n");
  return (
    <span
      className="inline-flex shrink-0 items-center text-warning"
      title={title}
      aria-label={title}
      data-testid="chart-problem"
      data-rules={badges.map((b) => b.rule).join(" ")}
    >
      <TriangleAlert className="size-3.5" aria-hidden />
    </span>
  );
}

function RenameInput({ id, name, onRename }: { id: string; name: string; onRename: StateNodeData["onRename"] }) {
  const ref = useRef<HTMLInputElement>(null);
  const [value, setValue] = useState(name);
  const done = useRef(false);
  useEffect(() => {
    ref.current?.focus();
    ref.current?.select();
  }, []);
  const finish = (next: string | null) => {
    if (done.current) return;
    done.current = true;
    onRename(id, next);
  };
  return (
    <input
      ref={ref}
      className="nodrag nopan h-5 min-w-0 flex-1 rounded-[3px] border border-accent bg-surface px-1 text-12 text-primary outline-none"
      aria-label={CHART_LABELS.renameState(name)}
      value={value}
      onChange={(e) => setValue(e.target.value)}
      onKeyDown={(e) => {
        e.stopPropagation();
        if (e.key === "Enter") finish(value);
        else if (e.key === "Escape") finish(null);
      }}
      onBlur={() => finish(value)}
      data-testid="state-rename"
    />
  );
}

/** The name, or the rename field while renaming. */
function Name({ data, className }: { data: StateNodeData; className?: string }) {
  const { state } = data;
  if (data.editing) return <RenameInput id={state.id} name={state.name} onRename={data.onRename} />;
  return (
    <span className={cn("min-w-0 truncate", className)} title={state.label === state.name ? state.name : `${state.label} (${state.name})`}>
      {state.label}
    </span>
  );
}

function Symbol({ state }: { state: ChartState }) {
  if (state.type === "final")
    return (
      <span className="flex size-4 shrink-0 items-center justify-center rounded-full border-2 border-primary" aria-hidden>
        <span className="size-2 rounded-full bg-primary" />
      </span>
    );
  if (state.type === "history")
    return (
      <span className="flex size-5 shrink-0 items-center justify-center rounded-full border border-primary text-11 font-semibold" aria-hidden>
        {state.deep ? "H*" : "H"}
      </span>
    );
  return <span className="ml-0.5 mr-0.5 size-3 shrink-0 rotate-45 border border-primary bg-surface" aria-hidden />;
}

/** The DOM id of a drawn state (the canvas's aria-activedescendant names it). */
export const stateDomId = (id: string) => `mq-chart-state-${id}`;

function StateNodeView({ id, data, selected }: NodeProps<StateFlowNode>) {
  const { state, open, hidden, active, linkTarget } = data;
  const ring = selected ? "outline outline-2 outline-accent" : linkTarget ? "outline outline-2 outline-dashed outline-accent" : null;
  const common = {
    "data-testid": `state-${state.name}`,
    "data-state": state.id,
    "data-type": state.type,
    "data-active": active || undefined,
    "data-selected": selected || undefined,
    "data-link-target": linkTarget || undefined,
    // A leaf (an atomic, pseudo or collapsed state) sits above the edges (index.css), so its transition handle takes
    // the pointer where an edge's hit path would otherwise cover it.
    "data-leaf": !open || undefined,
    // A tree item (its node is the tree): the canvas's aria-activedescendant points at it and it carries the selection,
    // so a screen reader follows the keyboard's selection while the focus stays on the canvas.
    id: stateDomId(state.id),
    role: "treeitem",
    "aria-selected": selected,
    ...(state.container && state.children.length ? { "aria-expanded": open } : {}),
    "aria-label": CHART_LABELS.stateAria(CHART_LABELS.stateTypes[state.type] ?? CHART_LABELS.stateTypes.atomic, state.label, active),
  };

  if (state.type === "final" || state.type === "history" || state.type === "choice")
    return (
      <div
        {...common}
        className={cn("flex h-6 w-full items-center gap-1.5 rounded-control px-0.5 text-12 text-primary", ring, active && "bg-accent-subtle font-semibold")}
      >
        <Handles label={CHART_LABELS.linkHandle(state.label)} />
        <Symbol state={state} />
        <Name data={data} />
        <ProblemBadge badges={state.badges} />
      </div>
    );

  const toggle =
    state.container && state.children.length ? (
      <Button
        size="icon-row"
        variant="ghost"
        className="nodrag -ml-0.5 shrink-0"
        label={hidden !== null ? CHART_LABELS.expand(state.label) : CHART_LABELS.collapse(state.label)}
        onClick={(e) => {
          e.stopPropagation();
          data.onToggle(id);
        }}
        data-testid="state-toggle"
      >
        {hidden !== null ? <ChevronRight /> : <ChevronDown />}
      </Button>
    ) : null;

  const header = (
    <div className="flex h-6 shrink-0 items-center gap-1 px-1.5 text-12">
      {toggle}
      <Name data={data} className={state.container ? "font-semibold" : undefined} />
      {state.bound !== null ? (
        <span className="shrink-0 truncate text-11 text-secondary" title={CHART_LABELS.boundMember(state.bound)} data-testid="state-bound-member">
          {state.bound}
        </span>
      ) : null}
      <ProblemBadge badges={state.badges} />
      {hidden !== null ? (
        <span
          className="ml-auto shrink-0 rounded-control border border-default bg-app px-1 text-11 text-secondary"
          title={CHART_LABELS.hiddenStates(hidden)}
          data-testid="state-hidden-count"
        >
          {hidden}
        </span>
      ) : null}
    </div>
  );

  if (open) {
    return (
      <div
        {...common}
        className={cn(
          "relative flex h-full w-full flex-col text-primary",
          state.region ? "rounded-control" : "rounded-panel border bg-surface",
          !state.region && (active ? "border-success" : "border-strong"),
          state.region && active && "bg-accent-subtle",
          ring,
        )}
      >
        <Handles label={CHART_LABELS.linkHandle(state.label)} />
        {header}
        {data.separators.map((x) => (
          <span
            key={x}
            aria-hidden
            className="pointer-events-none absolute bottom-1 top-6 border-l border-dashed border-strong"
            style={{ left: x }}
            data-testid="region-separator"
          />
        ))}
      </div>
    );
  }

  return (
    <div
      {...common}
      className={cn(
        "flex h-full w-full flex-col overflow-hidden rounded-control border text-primary",
        active ? "border-success bg-accent-subtle" : "border-strong bg-surface",
        hidden !== null && "border-double border-[3px]",
        ring,
      )}
    >
      <Handles label={CHART_LABELS.linkHandle(state.label)} />
      {header}
      {state.internal.map((row) => (
        <button
          key={row.transition}
          type="button"
          className={cn(
            "nodrag flex h-[18px] shrink-0 items-center gap-1 truncate px-2 text-left text-11 text-secondary hover:text-primary",
            data.selectedTransition === row.transition && "bg-accent-subtle text-primary",
          )}
          title={row.label}
          onClick={(e) => {
            e.stopPropagation();
            data.onSelectTransition(row.transition);
          }}
          data-testid="internal-transition"
        >
          <span className="min-w-0 truncate">{row.label}</span>
          <ProblemBadge badges={row.badges} />
        </button>
      ))}
    </div>
  );
}

export const StateNode = memo(StateNodeView);

function InitialNodeView() {
  return (
    <div className="size-3 rounded-full bg-primary" aria-hidden data-testid="initial-dot">
      <Handle type="source" position={Position.Right} id="r" className="!size-1 !min-h-0 !min-w-0 !border-0 !opacity-0" isConnectable={false} />
    </div>
  );
}

export const InitialNode = memo(InitialNodeView);
