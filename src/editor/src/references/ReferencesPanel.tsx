// The References tab of the bottom panel (explorer-redesign.md 3.3): where an element is used, grouped by kind of the
// referencing element, then by domain, in a virtualized list. The index answers at once (references/data.ts); the
// server's complete list replaces it when it arrives. Clicking a row goes to that element and JSON pointer.
import { useMemo, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import { useVirtualizer } from "@tanstack/react-virtual";
import { RefreshCw } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { keys, useIndex } from "@/api/queries";
import type { ElementDocument } from "@/api/types";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { cn } from "@/lib/cn";
import { KIND_LABELS } from "@/model/labels";
import { groupReferences, indexReferences, referenceCount, type SubName } from "./data";

const ROW = 24;

export function ReferencesPanel() {
  const { store, queryClient } = useServices();
  const target = useEditor(store, (s) => s.references);
  const selection = useEditor(store, (s) => s.selection);
  const index = useIndex();
  const { goTo } = useEditorNavigation();
  const byId = useMemo(() => new Map((index.data ?? []).map((r) => [r.id, r])), [index.data]);
  const server = useQuery({
    queryKey: ["references", target ?? ""],
    queryFn: () => endpoints.getReferences(target!),
    enabled: !!target,
    staleTime: 0,
  });
  const fast = useMemo(() => (target && index.data ? indexReferences(target, index.data) : []), [target, index.data]);
  const subName: SubName = (id, collection, i) => {
    const json = queryClient.getQueryData<ElementDocument>(keys.element(id))?.json as Record<string, unknown> | undefined;
    const item = (json?.[collection] as { name?: string }[] | undefined)?.[i];
    return typeof item?.name === "string" ? item.name : undefined;
  };
  const complete = !!server.data;
  // eslint-disable-next-line react-hooks/exhaustive-deps -- subName reads the query cache; the list follows the data
  const rows = useMemo(() => groupReferences(server.data ?? fast, byId, subName), [server.data, fast, byId]);
  const scrollRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({ count: rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => ROW, overscan: 20 });

  const selected = selection.length === 1 ? selection[0] : null;
  if (!target)
    return (
      <EmptyState title="No element chosen">
        Choose Where used on an element (Shift+F12, or the explorer's menu) to list what refers to it.
        {selected ? (
          <Button size="sm" variant="ghost" className="mt-2" onClick={() => store.getState().showReferences(selected)}>
            Where is {byId.get(selected)?.name ?? "the selection"} used?
          </Button>
        ) : null}
      </EmptyState>
    );

  const summary = byId.get(target);
  const count = referenceCount(rows);
  const title = `${summary ? `${KIND_LABELS[summary.kind] ?? summary.kind} ${summary.name}` : target}`;
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="references-panel" data-complete={complete || undefined}>
      <div className="flex h-8 shrink-0 items-center gap-2 border-b border-default px-3 text-12">
        <span className="min-w-0 flex-1 truncate" data-testid="references-title">
          {count === 1 ? "1 reference" : `${count} references`} to <span className="font-medium">{title}</span>
        </span>
        {!complete && server.isFetching ? <Spinner label="Loading the complete list" /> : null}
        {server.error ? <span className="text-danger">The complete list could not be loaded; showing the index's.</span> : null}
        {selected && selected !== target ? (
          <Button size="sm" variant="ghost" onClick={() => store.getState().showReferences(selected)}>
            Show for the selection
          </Button>
        ) : null}
        <Button size="icon-sm" variant="ghost" aria-label="Refresh the references" onClick={() => void server.refetch()}>
          <RefreshCw />
        </Button>
      </div>
      {count === 0 && (complete || server.error) ? (
        <EmptyState title="Not used anywhere">No element refers to {summary?.name ?? "this element"}.</EmptyState>
      ) : (
        <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto" role="list" aria-label={`References to ${summary?.name ?? target}`}>
          <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
            {virtualizer.getVirtualItems().map((item) => {
              const row = rows[item.index];
              const style = { position: "absolute" as const, top: 0, left: 0, right: 0, height: ROW, transform: `translateY(${item.start}px)` };
              if (row.type !== "reference")
                return (
                  <div
                    key={row.key}
                    role="listitem"
                    style={style}
                    className={cn("flex items-center gap-2 px-3 text-12", row.type === "kind" ? "font-medium" : "pl-6 text-secondary")}
                    data-testid={`references-${row.type}`}
                  >
                    <span className="truncate">{row.label}</span>
                    <span className="text-11 text-secondary">{row.count}</span>
                  </div>
                );
              return (
                <div key={row.key} role="listitem" style={style}>
                  <button
                    type="button"
                    className="flex h-full w-full items-center truncate pl-10 pr-3 text-left text-12 hover:bg-app focus-visible:bg-app focus-visible:outline-none"
                    title={row.pointer}
                    data-testid="reference-row"
                    onClick={() => goTo(row.elementId, row.pointer)}
                  >
                    {row.label}
                  </button>
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
