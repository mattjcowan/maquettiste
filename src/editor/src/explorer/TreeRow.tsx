// One row of the explorer's virtualized tree (explorer-redesign.md 1.1, 3.4, 4.3): a WAI-ARIA treeitem with its
// level, position and expansion, the icon of what it is, its count phrase or secondary text, and its markers
// (unsaved draft, another user's presence, errors, related, stale or pending table summaries). Memoized on
// primitive props, so a selection change re-renders only the rows it touches.
import { useDelayedPrefetch } from "./prefetch";
import { matchRange, type SearchQuery } from "@/search/query";
import { memo, useEffect, useRef, type CSSProperties, type DragEvent, type MouseEvent } from "react";
import {
  ArrowLeftRight,
  Box,
  Boxes,
  ChevronDown,
  ChevronRight,
  CircleHelp,
  Database,
  Eye,
  FolderTree,
  Gem,
  Hash,
  Layers,
  Link2,
  ListOrdered,
  Loader2,
  Package,
  PackageOpen,
  Sprout,
  Star,
  Stamp,
  Table2,
  Tags,
  Type,
  UserRound,
  Users,
  Workflow,
  type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import type { TreeNode } from "./tree";

const ICONS: Record<string, LucideIcon> = {
  domain: Package,
  "no-domain": PackageOpen,
  entity: Box,
  relationship: Link2,
  enum: ListOrdered,
  "value-object": Gem,
  "custom-type": Type,
  seed: Sprout,
  process: Workflow,
  operation: Workflow,
  event: Workflow,
  query: Workflow,
  projection: Workflow,
  actor: UserRound,
  permission: Stamp,
  people: Users,
  database: Database,
  schema: Layers,
  table: Table2,
  view: Eye,
  sequence: Hash,
  mapping: ArrowLeftRight,
  diagram: Workflow,
  "reference-type": Boxes,
  tag: Tags,
  category: FolderTree,
  stereotype: Stamp,
  other: CircleHelp,
  item: Box,
};

export function TreeIcon({ name, className }: { name: string; className?: string }) {
  const Icon = ICONS[name] ?? Box;
  return <Icon aria-hidden className={className ?? "size-4 shrink-0 text-secondary"} />;
}

export interface TreeRowProps {
  node: TreeNode;
  /** The DOM id the tree's aria-activedescendant points at. */
  domId: string;
  depth: number;
  pos: number;
  size: number;
  expandable: boolean;
  expanded: boolean;
  /** Filter mode: how many of a container's direct children are shown ("3 of 41"). */
  shown?: number;
  /** Filter mode: the query whose matched text is highlighted in the label. */
  search?: SearchQuery;
  selected: boolean;
  active: boolean;
  related: boolean;
  /** Related rows under this collapsed container ("n related", 1.9). */
  relatedCount: number;
  draft: boolean;
  /** The other people who have this element selected (1.10). */
  presence?: readonly string[];
  /** People with a selection under this collapsed container ("n people here", 1.10). */
  peopleHere: number;
  /** Starred (3.3); undefined on rows that cannot be starred. */
  favorite?: boolean;
  onToggleFavorite(key: string): void;
  loading: boolean;
  renaming: boolean;
  dropTarget: boolean;
  draggable: boolean;
  /** A member of the active diagram or domain view (3.5). */
  onCanvas?: boolean;
  style: CSSProperties;
  rowHeight: number;
  onRowClick(key: string, e: MouseEvent): void;
  onRowDoubleClick(key: string): void;
  onToggle(key: string): void;
  onContextMenu(key: string, e: MouseEvent): void;
  onRename(key: string, name: string | null): void;
  onDragStart(key: string, e: DragEvent): void;
  onDragOver(key: string, e: DragEvent): void;
  onDrop(key: string, e: DragEvent): void;
}

const CONTAINER_TYPES = new Set(["domain", "database", "schema"]);

function testId(node: TreeNode): string {
  if (node.type === "domain") return `explorer-domain-${node.label}`;
  if (node.type === "folder" || node.type === "group" || node.type === "database" || node.type === "schema") return `explorer-folder-${node.label}`;
  return node.home && node.id ? `explorer-row-${node.label}` : `explorer-item-${node.label}`;
}

/** The label with the search's matched text marked (3.1). */
function Highlighted({ label, search }: { label: string; search?: SearchQuery }) {
  const range = search ? matchRange(label, search) : undefined;
  if (!range) return <>{label}</>;
  return (
    <>
      {label.slice(0, range[0])}
      <mark className="rounded-[2px] bg-accent-subtle text-primary">{label.slice(range[0], range[1])}</mark>
      {label.slice(range[1])}
    </>
  );
}

export const TreeRow = memo(function TreeRow(props: TreeRowProps) {
  const { node, depth, expandable, expanded } = props;
  const hover = useDelayedPrefetch();
  const container = CONTAINER_TYPES.has(node.type);
  const folder = node.type === "folder" || node.type === "group";
  const counted =
    props.shown !== undefined && node.count !== undefined
      ? `${props.shown.toLocaleString("en-US")} of ${node.count.toLocaleString("en-US")}`
      : (node.secondary ?? (node.count !== undefined ? node.count.toLocaleString("en-US") : undefined));
  return (
    <div
      id={props.domId}
      role="treeitem"
      aria-level={depth + 1}
      aria-posinset={props.pos}
      aria-setsize={props.size}
      aria-expanded={expandable ? expanded : undefined}
      aria-selected={props.selected}
      data-testid={testId(node)}
      data-key={node.key}
      data-type={node.type}
      data-related={props.related ? "true" : undefined}
      title={node.warning ?? node.tooltip}
      style={{ ...props.style, paddingLeft: 4 + depth * 14 }}
      draggable={props.draggable}
      onClick={(e) => props.onRowClick(node.key, e)}
      onDoubleClick={() => props.onRowDoubleClick(node.key)}
      onMouseEnter={node.id ? () => hover.start(node.id!) : undefined}
      onMouseLeave={node.id ? hover.cancel : undefined}
      onContextMenu={(e) => props.onContextMenu(node.key, e)}
      onDragStart={props.draggable ? (e) => props.onDragStart(node.key, e) : undefined}
      onDragOver={(e) => props.onDragOver(node.key, e)}
      onDrop={(e) => props.onDrop(node.key, e)}
      className={cn(
        "group flex cursor-default select-none items-center gap-1.5 pr-2 text-13",
        folder && "text-secondary",
        props.selected ? "bg-accent-subtle text-primary" : props.related ? "bg-cat-2/10" : "hover:bg-app",
        props.active && "outline outline-1 -outline-offset-1 outline-accent",
        props.dropTarget && "outline-dashed outline-2 -outline-offset-2 outline-accent",
      )}
    >
      <span
        aria-hidden
        className="grid size-4 shrink-0 place-items-center"
        onClick={(e) => {
          if (!expandable) return;
          e.stopPropagation();
          props.onToggle(node.key);
        }}
      >
        {props.loading ? (
          <Loader2 className="size-3.5 animate-spin" />
        ) : expandable ? (
          expanded ? (
            <ChevronDown className="size-3.5" />
          ) : (
            <ChevronRight className="size-3.5" />
          )
        ) : null}
      </span>
      {folder ? null : <TreeIcon name={node.icon} className={cn("size-4 shrink-0", container ? "text-accent" : "text-secondary")} />}
      {props.renaming ? (
        <RenameInput value={node.label} onDone={(name) => props.onRename(node.key, name)} height={props.rowHeight - 8} />
      ) : (
        <span className={cn("min-w-0 truncate", folder && "font-medium", node.warning && "text-warning")}>
          <Highlighted label={node.label} search={props.search} />
        </span>
      )}
      {node.markers?.map((m) => (
        <span key={m} className="shrink-0 rounded-control border border-default px-1 text-11 text-secondary">
          {m}
        </span>
      ))}
      <span className="min-w-0 flex-1 truncate text-right text-11 text-secondary" data-part="count">
        {counted}
      </span>
      {node.pending ? <Loader2 className="size-3 shrink-0 animate-spin text-secondary" aria-label="Loading the table summaries" /> : null}
      {node.stale ? (
        <Badge tone="warning" aria-label="Stale: the model has errors" title="Stale: the model has errors; these are the last complete tables">
          stale
        </Badge>
      ) : null}
      {props.relatedCount > 0 && !expanded ? (
        <span className="shrink-0 text-11 text-accent" data-part="related">
          {props.relatedCount} related
        </span>
      ) : null}
      {props.peopleHere > 0 && !expanded ? (
        <span className="shrink-0 text-11 text-secondary" data-part="people">
          {props.peopleHere === 1 ? "1 person here" : `${props.peopleHere} people here`}
        </span>
      ) : null}
      {props.onCanvas ? (
        <span role="img" className="size-1.5 shrink-0 rounded-full bg-success" aria-label="On the canvas" title="On the canvas" data-part="on-canvas" />
      ) : null}
      {props.draft ? <span className="size-1.5 shrink-0 rounded-full bg-accent" aria-label="unsaved changes" /> : null}
      {props.favorite !== undefined ? (
        // Not a button: a treeitem holds no focusable content. The row menu offers the same toggle from the keyboard.
        <span
          aria-hidden
          data-part="star"
          title={props.favorite ? "Remove from favorites" : "Add to favorites"}
          className={cn("shrink-0 cursor-pointer text-secondary", props.favorite ? "text-warning" : "opacity-0 group-hover:opacity-100")}
          onClick={(e) => {
            e.stopPropagation();
            props.onToggleFavorite(node.key);
          }}
        >
          <Star className={cn("size-3.5", props.favorite && "fill-current")} />
        </span>
      ) : null}
      {props.presence?.length ? <Avatars people={props.presence} /> : null}
      {node.errors ? (
        <Badge tone="danger" aria-label={`${node.errors} errors`}>
          {node.errors}
        </Badge>
      ) : null}
      <span className="sr-only">{expandable ? (expanded ? "expanded" : "collapsed") : ""}</span>
    </div>
  );
});

/** Up to three avatars of the people who have the element selected, then "+n" (1.10). */
function Avatars({ people }: { people: readonly string[] }) {
  const label = `Selected by ${people.join(", ")}`;
  return (
    <span className="flex shrink-0 items-center -space-x-1" role="img" aria-label={label} title={label} data-part="presence">
      {people.slice(0, 3).map((p) => (
        <span
          key={p}
          aria-hidden
          className="grid size-4 place-items-center rounded-full border border-surface bg-cat-2 text-[9px] font-medium text-accent-foreground"
        >
          {initials(p)}
        </span>
      ))}
      {people.length > 3 ? (
        <span aria-hidden className="pl-1.5 text-11 text-secondary">
          +{people.length - 3}
        </span>
      ) : null}
    </span>
  );
}

function initials(name: string): string {
  const words = name.split(/[\s@._-]+/).filter(Boolean);
  return ((words[0]?.[0] ?? "?") + (words[1]?.[0] ?? "")).toUpperCase();
}

function RenameInput({ value, onDone, height }: { value: string; onDone: (name: string | null) => void; height: number }) {
  const ref = useRef<HTMLInputElement>(null);
  const done = useRef(false);
  useEffect(() => {
    ref.current?.focus();
    ref.current?.select();
  }, []);
  const finish = (name: string | null) => {
    if (done.current) return;
    done.current = true;
    onDone(name);
  };
  return (
    <input
      ref={ref}
      aria-label="New name"
      defaultValue={value}
      style={{ height }}
      className="min-w-0 flex-1 rounded-control border border-accent bg-surface px-1 text-13 outline-none"
      onClick={(e) => e.stopPropagation()}
      onKeyDown={(e) => {
        e.stopPropagation();
        if (e.key === "Enter") finish(e.currentTarget.value.trim() || null);
        else if (e.key === "Escape") finish(null);
      }}
      onBlur={(e) => finish(e.currentTarget.value.trim() || null)}
    />
  );
}
