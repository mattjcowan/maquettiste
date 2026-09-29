// The Reference data screen (reference-types-seeds-localization.md section 4): the type list on the left (nested by
// category with counts, or flat A to Z, with the explorer's search operators) and, for the selected type, a header
// (name, rows, used by) above the tabs Fields, Rows, Used by and Storage. The selection is the editor's selection,
// so the explorer's Reference data rows reveal a type here and a type picked here is selected everywhere. The tab
// stays when another type is selected, so a user can review Rows or Storage across types with the arrow keys.
import { useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useIndex } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { ElementSummary } from "@/api/types";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/misc";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useVocabularies } from "@/inspector/fields";
import { useEditor } from "@/state/store";
import { typeItems } from "./listModel";
import { TypeList } from "./TypeList";
import { FieldsTab } from "./FieldsTab";
import { RowsTab } from "./RowsGrid";
import { UsedByTab } from "./UsedByTab";
import { StorageTab } from "./StorageTab";
import { NewReferenceTypeDialog } from "./dialogs";

export const TABS = ["fields", "rows", "used-by", "storage"] as const;
export type RefTab = (typeof TABS)[number];
const TAB_LABELS: Record<RefTab, string> = { fields: "Fields", rows: "Rows", "used-by": "Used by", storage: "Storage" };

/** The reference type a selection shows: the type itself, or the target of one of its seeds. */
export function selectedType(index: readonly ElementSummary[] | undefined, selection: readonly string[]): string | null {
  const first = selection[0];
  if (!first || !index) return null;
  const row = index.find((r) => r.id === first);
  if (row?.kind === "reference-type") return row.id;
  if (row?.kind === "seed" && row.target && index.find((r) => r.id === row.target)?.kind === "reference-type") return row.target;
  return null;
}

/** Focuses the type search: the screen's list's, else the Reference data explorer's. */
function focusTypeSearch(own: HTMLInputElement | null) {
  (own ?? document.querySelector<HTMLInputElement>('[data-testid="explorer-reference-data"] input'))?.focus();
}

export function ReferenceDataWorkspace() {
  const { store } = useServices();
  const index = useIndex();
  const vocab = useVocabularies("reference-type");
  const selection = useEditor(store, (s) => s.selection);
  // One list on screen (EX 1.7): while the sidebar shows the Reference data explorer, that explorer is the screen's
  // list; the screen's own list shows only when the sidebar is collapsed or shows another explorer.
  const explorerShown = useEditor(
    store,
    (s) => !s.explorerCollapsed && (s.explorer.active === "reference-data" || s.explorer.pinned === "reference-data"),
  );
  const { select } = useEditorNavigation();
  const items = useMemo(() => typeItems(index.data, vocab.categories), [index.data, vocab.categories]);
  const typeId = selectedType(index.data, selection);
  const item = items.find((i) => i.id === typeId) ?? null;
  const [tab, setTab] = useState<RefTab>("rows");
  const [creating, setCreating] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);
  const usage = useQuery({
    queryKey: ["reference-usage", typeId ?? ""],
    queryFn: () => endpoints.getReferenceTypeUsage(typeId!),
    enabled: !!typeId,
    // Attributes change in other screens: read the usages again whenever the screen or a type shows.
    staleTime: 0,
    refetchOnMount: "always",
  });
  const usedBy = usage.data?.usages.length ?? 0;
  // Ctrl+1 to Ctrl+4 pick a tab and `/` focuses the type search from anywhere in the screen, outside text fields
  // (the Rows grid handles the same keys itself and marks them handled).
  const onScreenKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.defaultPrevented || e.altKey) return;
    const target = e.target as HTMLElement;
    if (target.closest("input, textarea, select, [contenteditable='true']")) return;
    const mod = e.ctrlKey || e.metaKey;
    if (mod && item && /^[1-4]$/.test(e.key)) {
      e.preventDefault();
      setTab(TABS[Number(e.key) - 1]!);
    } else if (!mod && !e.shiftKey && e.key === "/") {
      e.preventDefault();
      focusTypeSearch(searchRef.current);
    }
  };
  const totalRows = items.reduce((n, i) => n + i.rows, 0);

  return (
    <div className="flex h-full min-h-0" data-testid="reference-data" onKeyDown={onScreenKey}>
      {explorerShown ? null : (
        <TypeList items={items} selected={typeId} searchRef={searchRef} onSelect={(id) => select([id])} onNew={() => setCreating(true)} />
      )}
      <section aria-label="Reference type" className="flex min-w-0 flex-1 flex-col bg-surface">
        {item ? (
          <Tabs value={tab} onValueChange={(v) => setTab(v as RefTab)} className="flex min-h-0 flex-1 flex-col">
            <header className="flex flex-wrap items-baseline gap-x-3 gap-y-1 border-b border-default px-3 pb-1 pt-2">
              <h2 className="text-15 font-semibold" data-testid="reference-type-title">
                {item.label}
              </h2>
              <span className="text-12 text-secondary" data-testid="reference-type-facts">
                {item.name} · {item.rows} {item.rows === 1 ? "row" : "rows"} · used by {usedBy}
              </span>
              {explorerShown ? (
                <Button className="ml-auto" size="sm" onClick={() => setCreating(true)} data-testid="new-reference-type">
                  <Plus /> New reference type
                </Button>
              ) : null}
              <TabsList aria-label="Reference type views" className="w-full">
                {TABS.map((t, i) => (
                  <TabsTrigger key={t} value={t} title={`Ctrl+${i + 1}`}>
                    {TAB_LABELS[t]}
                  </TabsTrigger>
                ))}
              </TabsList>
            </header>
            <TabsContent value="fields" className="min-h-0 flex-1 overflow-auto p-3">
              <FieldsTab typeId={item.id} />
            </TabsContent>
            <TabsContent value="rows" className="flex min-h-0 flex-1 flex-col">
              <RowsTab
                key={item.id}
                typeId={item.id}
                seeds={item.seeds}
                onTab={(i) => setTab(TABS[i] ?? tab)}
                onFocusSearch={() => focusTypeSearch(searchRef.current)}
              />
            </TabsContent>
            <TabsContent value="used-by" className="min-h-0 flex-1 overflow-auto p-3">
              <UsedByTab usage={usage.data} loading={usage.isLoading} />
            </TabsContent>
            <TabsContent value="storage" className="min-h-0 flex-1 overflow-auto p-3">
              <StorageTab typeId={item.id} />
            </TabsContent>
          </Tabs>
        ) : (
          <EmptyState title={items.length ? "Select a reference type" : "No reference types yet"} className="m-auto">
            <p className="text-12 text-secondary">
              {items.length
                ? `${items.length} ${items.length === 1 ? "type" : "types"} · ${totalRows} ${totalRows === 1 ? "row" : "rows"}. Pick one in the list, or press / to search.`
                : "Reference types are sets of rows managed as data, such as units of measure or countries, that attributes use as their type."}
            </p>
            <Button
              className="mt-2"
              variant="primary"
              size="sm"
              onClick={() => setCreating(true)}
              data-testid={explorerShown ? "new-reference-type" : undefined}
            >
              <Plus /> New reference type
            </Button>
          </EmptyState>
        )}
      </section>
      <NewReferenceTypeDialog
        open={creating}
        onOpenChange={setCreating}
        categories={vocab.categories}
        onCreated={(id) => {
          setCreating(false);
          setTab("fields");
          select([id]);
        }}
      />
    </div>
  );
}
