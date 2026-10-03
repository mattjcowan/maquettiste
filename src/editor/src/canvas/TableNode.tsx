// A table card on the database diagram: the table's name, then one row per column (as the Display menu asks: all, keys only,
// none) with its markers (primary key, foreign key, unique, not null; each with a tooltip) and the dialect's native type. Every
// row and the header carry a connection handle on the right, shown on hover or focus: dragging it onto a column or the header
// of another table starts a foreign key there (the canvas finishes the drag and opens the dialog); Enter or Space on it opens
// the dialog with that column and no target yet. A hidden handle on the left is where edges arriving from the left end. A
// table not stored as a file yet looks like any other.
import { memo, type KeyboardEvent, type ReactNode } from "react";
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { Asterisk, Fingerprint, KeyRound, Link2 } from "lucide-react";
import type { TableView } from "@/api/types";
import { cn } from "@/lib/cn";
import { handleId, shownColumns, type TableDisplay } from "./tableColumns";

export interface TableNodeData extends Record<string, unknown> {
  table: TableView;
  display?: TableDisplay;
  /** Opens the foreign key dialog from a column (or the header: null) with no target yet (the keyboard's way to drag). */
  onLink?: (table: string, column: string | null) => void;
}
export type TableFlowNode = Node<TableNodeData, "table">;

const hidden = "!size-1 !min-h-0 !min-w-0 !border-0 !opacity-0 !pointer-events-none";

function Marker({ label, children }: { label: string; children: ReactNode }) {
  return (
    <span role="img" title={label} aria-label={label} className="inline-flex shrink-0">
      {children}
    </span>
  );
}

function LinkHandle({ table, column, label, onLink }: { table: string; column: string | null; label: string; onLink?: TableNodeData["onLink"] }) {
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.key !== "Enter" && e.key !== " ") return;
    e.preventDefault();
    e.stopPropagation();
    onLink?.(table, column);
  };
  return (
    <Handle
      type="source"
      position={Position.Right}
      id={handleId("R", column)}
      className="mq-col-handle !size-2.5 !rounded-full !border-2 !border-surface !bg-accent"
      title={label}
      aria-label={label}
      role="button"
      tabIndex={0}
      onKeyDown={onKeyDown}
      data-testid={column ? "column-link-handle" : "table-link-handle"}
    />
  );
}

function TableNodeView({ data, selected }: NodeProps<TableFlowNode>) {
  const { table, onLink } = data;
  const display = data.display ?? "all";
  const columns = shownColumns(table, display);
  const unique = (c: TableView["columns"][number]) => !c.isPrimaryKey && isUnique(table, c.key);
  const title = `${table.schema ? `${table.schema}.` : ""}${table.name}`;
  return (
    <div
      className={cn("w-64 rounded-panel border bg-surface", selected ? "border-accent outline outline-2 outline-accent" : "border-strong")}
      data-testid={`table-card-${table.name}`}
      data-display={display}
    >
      <div className="relative flex items-center gap-1 rounded-t-panel border-b border-default px-2 py-1" data-table-header={table.key}>
        <Handle type="source" position={Position.Left} id={handleId("L", null)} className={hidden} isConnectable={false} />
        <span className="min-w-0 flex-1 truncate text-13 font-semibold text-primary">{title}</span>
        <span className="shrink-0 text-11 text-secondary" title={`${table.columns.length} columns`}>
          {table.columns.length}
        </span>
        <LinkHandle table={table.key} column={null} label={`New foreign key from ${table.name}: drag onto another table, or press Enter`} onLink={onLink} />
      </div>
      {columns.length ? (
        <ul className="py-0.5" aria-label={`Columns of ${table.name}`}>
          {columns.map((c) => (
            <li
              key={c.key}
              className="relative flex h-5 items-center gap-1 px-2 text-12"
              data-column-key={c.key}
              data-testid={`table-column-${c.name}`}
              data-markers={[c.isPrimaryKey ? "pk" : "", c.isForeignKey ? "fk" : "", unique(c) ? "uq" : "", c.nullable ? "" : "nn"].filter(Boolean).join(" ")}
            >
              <Handle type="source" position={Position.Left} id={handleId("L", c.key)} className={hidden} isConnectable={false} />
              <span className="flex w-10 shrink-0 items-center gap-0.5">
                {c.isPrimaryKey ? (
                  <Marker label="Primary key">
                    <KeyRound className="size-3 text-accent" aria-hidden />
                  </Marker>
                ) : null}
                {c.isForeignKey ? (
                  <Marker label="Foreign key">
                    <Link2 className="size-3 text-secondary" aria-hidden />
                  </Marker>
                ) : null}
                {unique(c) ? (
                  <Marker label="Unique">
                    <Fingerprint className="size-3 text-secondary" aria-hidden />
                  </Marker>
                ) : null}
              </span>
              <span className="min-w-0 flex-1 truncate">{c.name}</span>
              <span className="shrink-0 font-mono text-11 text-secondary">{c.nativeType}</span>
              <span className="flex w-3 shrink-0 justify-center">
                {c.nullable ? null : (
                  <Marker label="Not null">
                    <Asterisk className="size-3 text-secondary" aria-hidden />
                  </Marker>
                )}
              </span>
              <LinkHandle
                table={table.key}
                column={c.key}
                label={`New foreign key from ${table.name}.${c.name}: drag onto a column of another table, or press Enter`}
                onLink={onLink}
              />
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

/** Whether a column alone is unique (a single-column unique constraint or unique index). */
function isUnique(table: TableView, key: string): boolean {
  return (
    table.uniques.some((u) => u.columns.length === 1 && u.columns[0] === key) ||
    table.indexes.some((i) => i.unique && i.columns.length === 1 && i.columns[0].column === key)
  );
}

export const TableNode = memo(TableNodeView);
