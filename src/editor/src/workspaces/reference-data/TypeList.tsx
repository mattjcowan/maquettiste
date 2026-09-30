// The type list (reference-types-seeds-localization.md 4.2): a virtualized tree of the category path with a count on
// every group ("3 of 12" while searching), or flat A to Z; the search box takes the explorer's four operators.
// Arrow keys move the selection, Left and Right collapse and expand a group, `/` focuses the search from anywhere in
// the screen.
import { rowHeight } from "@/design/density";
import { useMemo, useRef, useState, type KeyboardEvent, type RefObject } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ChevronDown, ChevronRight, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/cn";
import { Tooltip } from "@/components/ui/tooltip";
import { useElements, useIndex } from "@/api/queries";
import { firstCodes } from "./rowsModel";
import { countLabel, listRows, type RefTypeItem } from "./listModel";

export function TypeList({
  items,
  selected,
  searchRef,
  onSelect,
  onNew,
  onMenu,
}: {
  items: RefTypeItem[];
  selected: string | null;
  searchRef: RefObject<HTMLInputElement | null>;
  onSelect(id: string): void;
  onNew(): void;
  /** A right click or Shift+F10 on a type: its menu at a point (TypeMenu.tsx). */
  onMenu?(id: string, x: number, y: number): void;
}) {
  const [query, setQuery] = useState("");
  const [flat, setFlat] = useState(false);
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());
  const list = useMemo(() => listRows(items, { query, flat, collapsed }), [items, query, flat, collapsed]);
  const filtering = query.trim() !== "";
  const scrollRef = useRef<HTMLDivElement>(null);
  const ROW_H = useMemo(() => rowHeight(), []);
  const virtualizer = useVirtualizer({ count: list.rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => ROW_H, overscan: 20 });
  const at = list.rows.findIndex((r) => r.kind === "type" && r.key === selected);

  const toggle = (key: string, open?: boolean) =>
    setCollapsed((c) => {
      const next = new Set(c);
      if (open ?? next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const onKeyDown = (e: KeyboardEvent<HTMLElement>) => {
    if (onMenu && selected && (e.key === "ContextMenu" || (e.shiftKey && e.key === "F10"))) {
      e.preventDefault();
      const box = e.currentTarget.getBoundingClientRect();
      onMenu(selected, box.left + 24, box.top + 24 + Math.max(0, at) * ROW_H - (scrollRef.current?.scrollTop ?? 0));
      return;
    }
    const types = list.rows.map((r, i) => (r.kind === "type" ? i : -1)).filter((i) => i >= 0);
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      const pos = types.indexOf(at);
      const next = types[pos < 0 ? 0 : Math.max(0, Math.min(types.length - 1, pos + (e.key === "ArrowDown" ? 1 : -1)))];
      const row = next === undefined ? undefined : list.rows[next];
      if (row?.kind === "type") {
        onSelect(row.key);
        virtualizer.scrollToIndex(next);
      }
    }
  };

  return (
    <aside aria-label="Reference types" className="flex w-72 shrink-0 flex-col border-r border-default bg-surface" data-testid="reference-type-list">
      <div className="flex flex-col gap-1 border-b border-default p-2">
        <div className="flex items-center gap-1">
          <Input
            ref={searchRef}
            type="search"
            aria-label="Search reference types"
            placeholder={`Search ${items.length} ${items.length === 1 ? "type" : "types"}…`}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "ArrowDown") onKeyDown(e);
            }}
            title="* contains (default), ^ starts with, ~ like with %, = equals"
          />
          <Button size="icon-sm" variant="ghost" label="New reference type" data-testid="new-reference-type" onClick={onNew}>
            <Plus />
          </Button>
        </div>
        <div className="flex items-center justify-between text-11 text-secondary">
          <span data-testid="reference-type-count">
            {filtering ? `${list.matched} of ${list.total}` : `${list.total} ${list.total === 1 ? "type" : "types"}`}
          </span>
          <button
            type="button"
            aria-pressed={flat}
            className={cn("rounded-[4px] px-1.5 py-0.5", flat && "bg-accent-subtle text-accent")}
            onClick={() => setFlat((f) => !f)}
          >
            A to Z
          </button>
        </div>
      </div>
      <div
        ref={scrollRef}
        className="min-h-0 flex-1 overflow-auto"
        role={list.rows.length ? "tree" : undefined}
        aria-label={list.rows.length ? "Reference types by category" : undefined}
        tabIndex={list.rows.length ? 0 : undefined}
        onKeyDown={onKeyDown}
      >
        <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
          {virtualizer.getVirtualItems().map((v) => {
            const row = list.rows[v.index];
            const style = {
              position: "absolute" as const,
              top: 0,
              left: 0,
              right: 0,
              height: ROW_H,
              transform: `translateY(${v.start}px)`,
              paddingLeft: 8 + row.depth * 14,
            };
            if (row.kind === "group")
              return (
                <div
                  key={row.key}
                  role="treeitem"
                  aria-expanded={row.expanded}
                  aria-level={row.depth + 1}
                  aria-selected={false}
                  style={style}
                  className="flex cursor-default items-center gap-1 pr-2 text-12 font-semibold text-secondary hover:bg-accent-subtle"
                  onClick={() => !filtering && toggle(row.key)}
                >
                  {row.expanded ? <ChevronDown className="size-3.5" aria-hidden /> : <ChevronRight className="size-3.5" aria-hidden />}
                  <span className="min-w-0 flex-1 truncate">{row.label}</span>
                  <span className="text-11 font-normal">{countLabel(row, filtering)}</span>
                </div>
              );
            const item = row.item;
            return (
              <Tooltip key={row.key} side="right" content={<TypeCard item={item} />}>
                <div
                  role="treeitem"
                  aria-level={row.depth + 1}
                  aria-selected={item.id === selected}
                  data-testid={`reference-type-${item.name}`}
                  style={style}
                  className={cn(
                    "flex cursor-default items-center gap-1 pr-2 text-13 hover:bg-accent-subtle",
                    item.id === selected && "bg-accent-subtle text-accent",
                  )}
                  onClick={() => onSelect(item.id)}
                  onContextMenu={(e) => {
                    if (!onMenu) return;
                    e.preventDefault();
                    onSelect(item.id);
                    onMenu(item.id, e.clientX, e.clientY);
                  }}
                >
                  <span className="min-w-0 flex-1 truncate">{item.label}</span>
                  <span className="text-11 text-secondary">{item.rows}</span>
                </div>
              </Tooltip>
            );
          })}
        </div>
        {list.rows.length === 0 && items.length > 0 ? <p className="p-2 text-12 text-secondary">No type matches.</p> : null}
      </div>
    </aside>
  );
}

/** The hover card of a type row: name, category, field count and the first codes of its seeds (loaded when shown). */
function TypeCard({ item }: { item: RefTypeItem }) {
  const index = useIndex();
  const seedIds = useMemo(() => (index.data ?? []).filter((r) => r.kind === "seed" && r.target === item.id).map((r) => r.id), [index.data, item.id]);
  const seeds = useElements(seedIds);
  const docs = seedIds.map((id) => seeds.byId.get(id)?.json as unknown as Parameters<typeof firstCodes>[0][number] | undefined).filter((d) => !!d);
  const { codes, more } = firstCodes(docs);
  return (
    <div className="flex max-w-72 flex-col gap-1" data-testid="type-card">
      <span className="font-semibold">
        {item.name}
        {item.categoryPath.length ? <span className="font-normal text-secondary"> · {item.categoryPath.join(" › ")}</span> : null}
      </span>
      <span className="text-secondary">
        {item.fields} {item.fields === 1 ? "field" : "fields"} · {item.rows} {item.rows === 1 ? "row" : "rows"}
      </span>
      {codes.length ? (
        <span className="font-mono text-11">
          {codes.join(", ")}
          {more ? ` and ${more} more` : ""}
        </span>
      ) : seedIds.length && docs.length < seedIds.length ? null : (
        <span className="text-secondary">No rows yet.</span>
      )}
    </div>
  );
}
