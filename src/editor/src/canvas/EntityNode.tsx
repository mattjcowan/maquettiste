// An entity card (S15 canvas look): 4 px left edge in the category color, name at weight 600,
// attribute rows with the type in monospace right-aligned, key and required markers, stereotype
// badges, tag chips and a danger badge with the error count.
import { memo } from "react";
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { Asterisk, ChevronDown, ChevronRight, KeyRound } from "lucide-react";
import type { AttributeDoc, EntityDoc } from "@/api/types";
import { categoryVar } from "@/design/theme";
import { cn } from "@/lib/cn";

export type DisplayMode = "all" | "keys" | "names";

export interface EntityNodeData extends Record<string, unknown> {
  entity: EntityDoc;
  categoryIndex: number | null;
  errorCount: number;
  collapsed: boolean;
  display: DisplayMode;
  typeLabel: (attribute: AttributeDoc) => string;
  dirty: boolean;
  onToggleCollapsed?: (id: string) => void;
}

export type EntityFlowNode = Node<EntityNodeData, "entity">;

function EntityNodeView({ id, data, selected }: NodeProps<EntityFlowNode>) {
  const { entity, categoryIndex, errorCount, collapsed, display } = data;
  const keys = new Set(entity.key?.attributes ?? []);
  const attributes = (entity.attributes ?? []).filter((a) => display === "all" || (display === "keys" && keys.has(a.id)));
  const showAttributes = !collapsed && display !== "names";
  return (
    <div
      className={cn(
        "w-60 overflow-hidden rounded-panel border bg-surface text-primary",
        selected ? "border-accent bg-accent-subtle outline outline-2 outline-accent" : "border-strong",
      )}
      style={{ borderLeft: `4px solid ${categoryVar(categoryIndex)}` }}
      data-testid={`entity-card-${entity.name}`}
    >
      <Handle type="target" position={Position.Left} id="l" className="!size-2" />
      <Handle type="source" position={Position.Right} id="r" className="!size-2" />
      <Handle type="target" position={Position.Right} id="tr" className="!size-2 !opacity-0" isConnectable={false} />
      <Handle type="source" position={Position.Left} id="sl" className="!size-2 !opacity-0" isConnectable={false} />
      <div className="flex flex-col gap-1 border-b border-default px-2 py-1.5">
        <div className="flex items-center gap-1">
          <button
            type="button"
            className="nodrag -ml-1 rounded-[3px] p-0.5 text-secondary hover:text-primary"
            aria-label={collapsed ? `Expand ${entity.name}` : `Collapse ${entity.name}`}
            onClick={(e) => {
              e.stopPropagation();
              data.onToggleCollapsed?.(id);
            }}
          >
            {collapsed ? <ChevronRight className="size-3.5" /> : <ChevronDown className="size-3.5" />}
          </button>
          <span className="min-w-0 flex-1 truncate text-13 font-semibold">{entity.name}</span>
          {data.dirty ? <span className="size-1.5 rounded-full bg-accent" aria-label="unsaved changes" /> : null}
          {errorCount ? (
            <span
              className="rounded-control border border-danger bg-surface px-1 text-11 font-semibold text-danger"
              aria-label={`${errorCount} errors`}
              data-testid="error-badge"
            >
              {errorCount}
            </span>
          ) : null}
        </div>
        {(entity.stereotypes?.length ?? 0) + (entity.tags?.length ?? 0) > 0 ? (
          <div className="flex flex-wrap gap-1">
            {entity.stereotypes?.map((s) => (
              <span key={s} className="text-11 text-secondary">
                «{s}»
              </span>
            ))}
            {entity.tags?.map((t) => (
              <span key={t} className="rounded-[3px] border border-default bg-app px-1 text-11 text-secondary">
                #{t}
              </span>
            ))}
          </div>
        ) : null}
      </div>
      {showAttributes ? (
        <ul className="py-0.5" aria-label={`Attributes of ${entity.name}`}>
          {attributes.map((a) => (
            <li key={a.id} className="flex h-6 items-center gap-1 px-2 text-12" data-testid="card-attribute">
              <span className="flex w-4 shrink-0 justify-center" aria-hidden>
                {keys.has(a.id) ? <KeyRound className="size-3 text-accent" /> : a.required ? <Asterisk className="size-3 text-secondary" /> : null}
              </span>
              <span className="min-w-0 flex-1 truncate">{a.name}</span>
              <span className="shrink-0 font-mono text-11 text-secondary">{data.typeLabel(a)}</span>
              <span className="sr-only">{keys.has(a.id) ? "key" : a.required ? "required" : ""}</span>
            </li>
          ))}
          {attributes.length === 0 ? <li className="px-2 py-1 text-11 text-secondary">No attributes</li> : null}
        </ul>
      ) : null}
    </div>
  );
}

export const EntityNode = memo(EntityNodeView);
