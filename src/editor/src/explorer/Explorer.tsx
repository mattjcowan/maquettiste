import { useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ChevronDown, ChevronRight, Filter, X } from "lucide-react";
import { useIndex, useValidation, useElement } from "@/api/queries";
import type { CategoryTreeDoc } from "@/api/types";
import { Input } from "@/components/ui/input";
import { Badge, EmptyState, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/menu";
import { useEditor } from "@/state/store";
import { cn } from "@/lib/cn";
import { displayName } from "@/model/model";
import { KindIcon } from "@/app/icons";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { emptyFilter, explorerRows, type ExplorerFilter } from "./filter";

export function Explorer() {
  const { store, realtime } = useServices();
  const index = useIndex();
  const validation = useValidation();
  const selection = useEditor(store, (s) => s.selection);
  const drafts = useEditor(store, (s) => s.drafts);
  const presence = useEditor(store, (s) => s.presence);
  const density = useEditor(store, (s) => s.density);
  const { reveal, select } = useEditorNavigation();
  const [filter, setFilter] = useState<ExplorerFilter>(emptyFilter);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [active, setActive] = useState(0);
  const scrollRef = useRef<HTMLDivElement>(null);

  const rowsAll = index.data;
  const categoryTreeId = rowsAll?.find((r) => r.kind === "category-tree")?.id ?? null;
  const categoryTree = useElement(categoryTreeId);
  const categories = ((categoryTree.data?.json as CategoryTreeDoc | undefined)?.categories ?? []).map((c) => ({ id: c.id, name: c.name }));
  const tags = useMemo(() => [...new Set((rowsAll ?? []).flatMap((r) => r.tags))].sort(), [rowsAll]);
  const stereotypes = useMemo(() => [...new Set((rowsAll ?? []).flatMap((r) => r.stereotypes))].sort(), [rowsAll]);

  const rows = useMemo(() => explorerRows(rowsAll ?? [], filter, collapsed), [rowsAll, filter, collapsed]);
  const errors = useMemo(() => {
    const map = new Map<string, number>();
    for (const d of validation.data?.diagnostics ?? []) if (d.severity === "error" && d.elementId) map.set(d.elementId, (map.get(d.elementId) ?? 0) + 1);
    return map;
  }, [validation.data]);
  const others = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const p of presence) {
      if (p.connectionId === realtime.connectionId || !p.elementId) continue;
      map.set(p.elementId, [...(map.get(p.elementId) ?? []), p.user]);
    }
    return map;
  }, [presence, realtime.connectionId]);

  const rowHeight = density === "compact" ? 32 : 40;
  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => rowHeight, overscan: 12 });

  const toggleGroup = (key: string) => {
    const next = new Set(collapsed);
    const k = key.replace(/^group:/, "");
    if (next.has(k)) next.delete(k);
    else next.add(k);
    setCollapsed(next);
  };

  const activate = (i: number) => {
    const row = rows[i];
    if (!row) return;
    if (row.type === "group") toggleGroup(row.key);
    else reveal(row.summary);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const last = rows.length - 1;
    let next = active;
    if (e.key === "ArrowDown") next = Math.min(last, active + 1);
    else if (e.key === "ArrowUp") next = Math.max(0, active - 1);
    else if (e.key === "Home") next = 0;
    else if (e.key === "End") next = last;
    else if (e.key === "PageDown") next = Math.min(last, active + 10);
    else if (e.key === "PageUp") next = Math.max(0, active - 10);
    else if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      activate(active);
      return;
    } else if (e.key === "ArrowLeft" && rows[active]?.type === "group" && !(rows[active] as { collapsed: boolean }).collapsed) {
      toggleGroup(rows[active].key);
      return;
    } else if (e.key === "ArrowRight" && rows[active]?.type === "group" && (rows[active] as { collapsed: boolean }).collapsed) {
      toggleGroup(rows[active].key);
      return;
    } else return;
    e.preventDefault();
    setActive(next);
    virtualizer.scrollToIndex(next);
    const row = rows[next];
    if (row?.type === "element" && e.shiftKey) select([...new Set([...selection, row.summary.id])]);
  };

  const activeRow = rows[active];
  const filterCount = filter.tags.length + filter.categories.length + filter.stereotypes.length;
  const toggleIn = (key: "tags" | "categories" | "stereotypes", value: string) =>
    setFilter((f) => ({ ...f, [key]: f[key].includes(value) ? f[key].filter((v) => v !== value) : [...f[key], value] }));

  return (
    <section aria-label="Model explorer" className="flex h-full min-h-0 flex-col bg-surface">
      <div className="flex flex-col gap-2 border-b border-default p-2">
        <div className="flex items-center gap-1">
          <Input
            type="search"
            placeholder="Filter elements"
            aria-label="Filter elements by name"
            value={filter.text}
            onChange={(e) => setFilter((f) => ({ ...f, text: e.target.value }))}
            data-region-focus
          />
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                variant="ghost"
                size="icon"
                aria-label={`Filter by tag, category or stereotype${filterCount ? ` (${filterCount} active)` : ""}`}
                data-testid="explorer-filters"
              >
                <Filter />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="max-h-96 overflow-auto">
              <DropdownMenuLabel>Tags</DropdownMenuLabel>
              {tags.length === 0 ? <p className="px-2 py-1 text-12 text-secondary">No tags</p> : null}
              {tags.map((t) => (
                <DropdownMenuCheckboxItem key={t} checked={filter.tags.includes(t)} onCheckedChange={() => toggleIn("tags", t)}>
                  {t}
                </DropdownMenuCheckboxItem>
              ))}
              <DropdownMenuSeparator />
              <DropdownMenuLabel>Categories</DropdownMenuLabel>
              {categories.map((c) => (
                <DropdownMenuCheckboxItem key={c.id} checked={filter.categories.includes(c.id)} onCheckedChange={() => toggleIn("categories", c.id)}>
                  {c.name}
                </DropdownMenuCheckboxItem>
              ))}
              <DropdownMenuSeparator />
              <DropdownMenuLabel>Stereotypes</DropdownMenuLabel>
              {stereotypes.map((s) => (
                <DropdownMenuCheckboxItem key={s} checked={filter.stereotypes.includes(s)} onCheckedChange={() => toggleIn("stereotypes", s)}>
                  {s}
                </DropdownMenuCheckboxItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
        {filterCount > 0 ? (
          <div className="flex flex-wrap gap-1" aria-label="Active filters">
            {[
              ...filter.tags.map((v) => ({ key: "tags" as const, v, label: `#${v}` })),
              ...filter.categories.map((v) => ({ key: "categories" as const, v, label: categories.find((c) => c.id === v)?.name ?? v })),
              ...filter.stereotypes.map((v) => ({ key: "stereotypes" as const, v, label: `«${v}»` })),
            ].map((chip) => (
              <button
                key={chip.key + chip.v}
                type="button"
                onClick={() => toggleIn(chip.key, chip.v)}
                className="inline-flex h-6 items-center gap-1 rounded-control border border-accent bg-accent-subtle px-1.5 text-12"
                aria-label={`Remove filter ${chip.label}`}
              >
                {chip.label}
                <X className="size-3" aria-hidden />
              </button>
            ))}
          </div>
        ) : null}
      </div>
      {index.isPending ? (
        <div className="p-3">
          <Spinner label="Loading the model" />
        </div>
      ) : index.error ? (
        <EmptyState title="The model index could not be loaded">{String((index.error as Error).message)}</EmptyState>
      ) : rows.length === 0 ? (
        <EmptyState title={rowsAll?.length ? "Nothing matches the filters" : "The model is empty"} />
      ) : (
        <div
          ref={scrollRef}
          role="listbox"
          aria-label="Model elements"
          aria-multiselectable="true"
          tabIndex={0}
          aria-activedescendant={activeRow ? `explorer-${activeRow.key}` : undefined}
          onKeyDown={onKeyDown}
          className="min-h-0 flex-1 overflow-auto focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent"
          data-testid="explorer-list"
        >
          <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
            {virtualizer.getVirtualItems().map((item) => {
              const row = rows[item.index];
              const isActive = item.index === active;
              const style = {
                position: "absolute" as const,
                top: 0,
                left: 0,
                right: 0,
                height: item.size,
                transform: `translateY(${item.start}px)`,
              };
              if (row.type === "group") {
                return (
                  <div
                    key={row.key}
                    id={`explorer-${row.key}`}
                    role="option"
                    aria-selected={false}
                    style={style}
                    onClick={() => {
                      setActive(item.index);
                      toggleGroup(row.key);
                    }}
                    className={cn(
                      "flex cursor-default items-center gap-1 px-2 text-11 font-semibold uppercase tracking-wide text-secondary",
                      isActive && "bg-accent-subtle",
                    )}
                  >
                    {row.collapsed ? <ChevronRight className="size-3.5" aria-hidden /> : <ChevronDown className="size-3.5" aria-hidden />}
                    <span className="flex-1 truncate">{row.label}</span>
                    <span className="font-normal">{row.count}</span>
                    <span className="sr-only">{row.collapsed ? "collapsed" : "expanded"}</span>
                  </div>
                );
              }
              const s = row.summary;
              const selected = selection.includes(s.id);
              const count = errors.get(s.id) ?? 0;
              const users = others.get(s.id);
              return (
                <div
                  key={row.key}
                  id={`explorer-${row.key}`}
                  role="option"
                  aria-selected={selected}
                  data-testid={`explorer-row-${displayName(s)}`}
                  style={style}
                  onClick={(e) => {
                    setActive(item.index);
                    if (e.shiftKey || e.metaKey || e.ctrlKey) select(selected ? selection.filter((x) => x !== s.id) : [...selection, s.id]);
                    else reveal(s);
                  }}
                  className={cn(
                    "flex cursor-default items-center gap-2 pl-6 pr-2 text-13",
                    selected ? "bg-accent-subtle text-primary" : "hover:bg-app",
                    isActive && "outline outline-1 -outline-offset-1 outline-accent",
                  )}
                >
                  <KindIcon kind={s.kind} />
                  <span className="min-w-0 flex-1 truncate">{displayName(s)}</span>
                  {drafts[s.id] ? <span className="size-1.5 rounded-full bg-accent" aria-label="unsaved changes" /> : null}
                  {users ? (
                    <span className="size-2 rounded-full bg-cat-2" aria-label={`Selected by ${users.join(", ")}`} title={`Selected by ${users.join(", ")}`} />
                  ) : null}
                  {count ? (
                    <Badge tone="danger" aria-label={`${count} errors`}>
                      {count}
                    </Badge>
                  ) : null}
                </div>
              );
            })}
          </div>
        </div>
      )}
    </section>
  );
}
