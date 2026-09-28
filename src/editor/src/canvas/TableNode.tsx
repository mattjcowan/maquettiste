// A table card for the Database workspace: columns with primary and foreign key icons and the
// dialect's native type in monospace; synthesized tables have their header in muted text.
import { memo } from "react";
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { KeyRound, Link2 } from "lucide-react";
import type { TableView } from "@/api/types";
import { cn } from "@/lib/cn";

export interface TableNodeData extends Record<string, unknown> {
  table: TableView;
}
export type TableFlowNode = Node<TableNodeData, "table">;

function TableNodeView({ data, selected }: NodeProps<TableFlowNode>) {
  const { table } = data;
  return (
    <div
      className={cn("w-64 overflow-hidden rounded-panel border bg-surface", selected ? "border-accent outline outline-2 outline-accent" : "border-strong")}
      data-testid={`table-card-${table.name}`}
    >
      <Handle type="target" position={Position.Left} id="l" className="!size-2" isConnectable={false} />
      <Handle type="source" position={Position.Right} id="r" className="!size-2" isConnectable={false} />
      <div className="flex items-center gap-1 border-b border-default px-2 py-1.5">
        <span className={cn("min-w-0 flex-1 truncate text-13 font-semibold", table.origin === "synthesized" ? "text-secondary" : "text-primary")}>
          {table.schema ? `${table.schema}.` : ""}
          {table.name}
        </span>
        <span className="text-11 text-secondary">{table.isJunction ? "junction" : table.origin}</span>
      </div>
      <ul className="py-0.5">
        {table.columns.map((c) => (
          <li key={c.key} className="flex h-6 items-center gap-1 px-2 text-12">
            <span className="flex w-4 shrink-0 justify-center" aria-hidden>
              {c.isPrimaryKey ? <KeyRound className="size-3 text-accent" /> : c.isForeignKey ? <Link2 className="size-3 text-secondary" /> : null}
            </span>
            <span className="min-w-0 flex-1 truncate">{c.name}</span>
            <span className="shrink-0 font-mono text-11 text-secondary">
              {c.nativeType}
              {c.nullable ? "" : " not null"}
            </span>
            <span className="sr-only">{c.isPrimaryKey ? "primary key" : c.isForeignKey ? "foreign key" : ""}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export const TableNode = memo(TableNodeView);
