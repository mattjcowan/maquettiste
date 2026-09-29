// The Generate explorer (generation-ui.md 2.1): a virtualized ARIA tree of the project's packs, each with its Units
// (`id · scope → template → path summary`), Templates (with roles and the units that use them), Parameters
// (`name = value`, default or set) and Outputs (the manifest grouped by unit, with hand-edited, missing and orphan
// states). Enter or a click on a pack opens its editor as a centre tab; a unit, parameter or output opens the
// matching tab. New pack sits in the header; the palette has it too.
import { useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useQueries } from "@tanstack/react-query";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ChevronDown, ChevronRight, FileCode2, Filter, Package, Plus, SlidersHorizontal, TriangleAlert } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { keys, useIndex, usePacks } from "@/api/queries";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { Tooltip } from "@/components/ui/tooltip";
import { rowHeight } from "@/design/density";
import { cn } from "@/lib/cn";
import type { PackPane } from "@/state/store";
import { packRows, packTotals, type PackDetails, type PackRow } from "./explorerModel";
import { openPackTab } from "./packTabs";
import { NewPackDialog } from "./NewPackDialog";
import { PanelToggle } from "@/app/panels";

const PANE: Partial<Record<PackRow["kind"], PackPane>> = {
  units: "units",
  unit: "units",
  templates: "templates",
  file: "templates",
  parameters: "parameters",
  parameter: "parameters",
  outputs: "outputs",
  "output-group": "outputs",
  output: "outputs",
};

export function GenerateExplorer() {
  const { store } = useServices();
  const { openWorkspace } = useEditorNavigation();
  const packs = usePacks();
  const index = useIndex();
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const [active, setActive] = useState(0);
  const list = useMemo(() => packs.data ?? [], [packs.data]);
  const open = list.filter((p) => expanded.has(`p:${p.name}`)).map((p) => p.name);
  const documents = useQueries({ queries: open.map((name) => ({ queryKey: keys.pack(name), queryFn: () => endpoints.getPack(name) })) });
  const outputs = useQueries({ queries: list.map((p) => ({ queryKey: keys.packOutputs(p.name), queryFn: () => endpoints.getPackOutputs(p.name) })) });
  const dataKey = [open.join("\n"), ...documents.map((d) => d.dataUpdatedAt), ...outputs.map((o) => o.dataUpdatedAt)].join("|");
  const details = useMemo(() => {
    const map = new Map<string, PackDetails>();
    list.forEach((p, i) => map.set(p.name, { outputs: outputs[i]?.data }));
    open.forEach((name, i) => map.set(name, { ...map.get(name), document: documents[i]?.data }));
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the query results are new arrays each render; their update times are what counts
  }, [list, dataKey]);
  const ids = useMemo(() => new Set((index.data ?? []).map((e) => e.id)), [index.data]);
  const rows = useMemo(() => packRows(list, expanded, details, index.data ? (id) => ids.has(id) : undefined), [list, expanded, details, ids, index.data]);
  const scrollRef = useRef<HTMLDivElement>(null);
  const size = useMemo(() => rowHeight(), []);
  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => size, overscan: 12 });
  const at = Math.min(active, Math.max(0, rows.length - 1));

  const toggle = (key: string, to?: boolean) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (to ?? !next.has(key)) next.add(key);
      else next.delete(key);
      return next;
    });

  const openRow = (row: PackRow) => {
    const g = store.getState().generation;
    store.getState().setGeneration(
      openPackTab(g, row.pack, PANE[row.kind] ?? (g.packPane[row.pack] ? undefined : "units"), {
        unit: row.unit,
        parameter: row.parameter,
        file: row.kind === "file" ? row.path : undefined,
      }),
    );
    if (store.getState().workspace !== "generate") openWorkspace("generate");
  };

  const focusRow = (i: number) => {
    setActive(i);
    virtualizer.scrollToIndex(i);
    requestAnimationFrame(() => scrollRef.current?.querySelector<HTMLElement>(`[data-index="${i}"]`)?.focus());
  };

  const onKeyDown = (e: KeyboardEvent) => {
    const row = rows[at];
    if (!row) return;
    const parent = () => {
      for (let i = at - 1; i >= 0; i--) if (rows[i].level < row.level) return i;
      return at;
    };
    const moves: Record<string, () => void> = {
      ArrowDown: () => focusRow(Math.min(rows.length - 1, at + 1)),
      ArrowUp: () => focusRow(Math.max(0, at - 1)),
      Home: () => focusRow(0),
      End: () => focusRow(rows.length - 1),
      ArrowRight: () => (row.expandable && !row.expanded ? toggle(row.key, true) : row.expanded ? focusRow(at + 1) : undefined),
      ArrowLeft: () => (row.expanded ? toggle(row.key, false) : focusRow(parent())),
      Enter: () => openRow(row),
      " ": () => (row.expandable ? toggle(row.key) : openRow(row)),
    };
    const move = moves[e.key];
    if (!move) return;
    e.preventDefault();
    move();
  };

  return (
    <section aria-label="Generate" className="flex h-full min-h-0 flex-1 flex-col bg-surface" data-testid="explorer-generate">
      <header className="flex h-6 shrink-0 items-center gap-2 border-b border-default px-2" data-testid="explorer-header">
        <h2 className="text-11 font-semibold uppercase tracking-wide text-secondary">Generate</h2>
        <span className="min-w-0 flex-1 truncate text-11 text-secondary" data-testid="explorer-totals">
          {packTotals(list)}
        </span>
        <Tooltip content="New pack…">
          <Button
            variant="ghost"
            size="icon"
            className="size-5"
            aria-label="New pack…"
            data-testid="new-pack"
            onClick={() => store.getState().setGeneration({ newPack: true })}
          >
            <Plus className="size-3.5" />
          </Button>
        </Tooltip>
        <PanelToggle panel="explorer" />
      </header>
      <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto">
        <div role="tree" aria-label="Packs" className="relative" style={{ height: virtualizer.getTotalSize() }} onKeyDown={onKeyDown}>
          {virtualizer.getVirtualItems().map((v) => {
            const row = rows[v.index];
            return (
              <div
                key={row.key}
                role="treeitem"
                data-index={v.index}
                data-testid={`pack-row-${row.key}`}
                aria-level={row.level}
                aria-setsize={row.setsize}
                aria-posinset={row.posinset}
                aria-expanded={row.expandable ? row.expanded : undefined}
                aria-selected={v.index === at}
                tabIndex={v.index === at ? 0 : -1}
                onClick={() => {
                  setActive(v.index);
                  if (row.kind === "pack" || !row.expandable) openRow(row);
                  else toggle(row.key);
                }}
                className={cn(
                  "absolute left-0 flex w-full cursor-default select-none items-center gap-1 pr-2 text-12 outline-none hover:bg-accent-subtle focus-visible:bg-accent-subtle",
                  v.index === at && "bg-accent-subtle/60",
                )}
                style={{ top: 0, height: v.size, transform: `translateY(${v.start}px)`, paddingLeft: 4 + (row.level - 1) * 12 }}
              >
                <span
                  className="grid size-4 shrink-0 place-items-center"
                  onClick={(e) => {
                    if (!row.expandable) return;
                    e.stopPropagation();
                    toggle(row.key);
                  }}
                  aria-hidden
                >
                  {row.expandable ? row.expanded ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" /> : null}
                </span>
                <RowIcon row={row} />
                <span className={cn("min-w-0 flex-1 truncate", row.mono && "font-mono text-11", row.kind === "pack" && "font-medium")} title={row.label}>
                  {row.label}
                </span>
                {row.filter ? (
                  <Tooltip content={`Filter: ${row.filter}`}>
                    <Filter className="size-3 shrink-0 text-secondary" aria-label={`Filter: ${row.filter}`} />
                  </Tooltip>
                ) : null}
                {row.warnings ? (
                  <span className="flex shrink-0 items-center gap-0.5 text-11 text-warning" aria-label={`${row.warnings} diagnostics`}>
                    <TriangleAlert className="size-3" aria-hidden />
                    {row.warnings}
                  </span>
                ) : null}
                <span className={cn("shrink-0 text-11 text-secondary", row.state && row.state !== "clean" && "text-warning")}>
                  {row.off ? "off" : row.detail}
                </span>
              </div>
            );
          })}
        </div>
        {packs.data && !list.length ? <p className="px-2 py-1 text-12 text-secondary">No packs. Create one with New pack.</p> : null}
      </div>
      <NewPackDialog />
    </section>
  );
}

function RowIcon({ row }: { row: PackRow }) {
  const cls = "size-3.5 shrink-0 text-secondary";
  if (row.kind === "pack") return <Package className={cls} aria-hidden />;
  if (row.kind === "file" || row.kind === "templates") return <FileCode2 className={cls} aria-hidden />;
  if (row.kind === "parameters" || row.kind === "parameter") return <SlidersHorizontal className={cls} aria-hidden />;
  return null;
}
