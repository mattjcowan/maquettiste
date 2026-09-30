// One explorer (explorer-redesign.md 1.0, 1.1, 1.8, 1.9, 3.4, 4.3): a header naming it with its totals, the tree
// filter, and a virtualized WAI-ARIA tree rendered from explorer/tree.ts. Expansion, filter and scroll live in the
// store per explorer, so each keeps its own while another shows. Expanding and collapsing splice the cached rows;
// the rows are rebuilt only when the forest, the filter or a bulk change (collapse all, reveal) asks for it.
import { rowHeight as densityRowHeight } from "@/design/density";
import { ContentLocaleChip } from "@/l10n/LocaleSwitcher";
import { useCallback, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState, type DragEvent, type KeyboardEvent, type MouseEvent } from "react";
import { flushSync } from "react-dom";
import { useQueries, useQueryClient } from "@tanstack/react-query";
import { useVirtualizer } from "@tanstack/react-virtual";
import { MoreHorizontal, Plus } from "lucide-react";
import {
  elementQuery,
  prefetchAllTables,
  prefetchElement,
  tableDetailQuery,
  tablesQuery,
  useElement,
  useElements,
  useIndex,
  useSettings,
  useValidation,
  type TablesState,
} from "@/api/queries";
import { editorPerf, perfOnce, perfStart, perfSync } from "@/lib/perf";
import type { CategoryTreeDoc, ElementSummary } from "@/api/types";
import { mergeCategoryTrees, type CategoryMaps } from "@/model/vocabularies";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/menu";
import { useEditor } from "@/state/store";
import { PanelToggle } from "@/app/panels";
import { pageChanged } from "@/state/pageState";
import { EXPLORER_LABELS } from "@/model/labels";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { activeTab, hasEditor } from "@/editors/tabs";
import { useDelayedPrefetch } from "./prefetch";
import { isFiltering, type ExplorerFilter } from "./filter";
import { FilterBar } from "./FilterBar";
import { FavoritesStrip } from "./Favorites";
import { searchClient, useSearchRows, useSearchVersion } from "@/search/client";
import { indexPatchOf } from "@/api/indexPatch";
import { SEARCH_PLACES, type SearchPlace } from "@/search/engine";
import { parseQuery } from "@/search/query";
import {
  EXPLORERS,
  collapseAt,
  databaseOf,
  documentChildren,
  tableChildren,
  errorCounts,
  expandAt,
  filteredRows,
  forestOf,
  isExpandable,
  nodeOf,
  positionOf,
  prebuild,
  presenceCounts,
  relatedCounts,
  canvasCounts,
  relatedKeys,
  revealPath,
  visibleRows,
  childKeys,
  type ExplorerId,
  type Forest,
  type TablesInput,
  type TreeNode,
  type VisibleRow,
  type DatabaseInfo,
} from "./tree";
import { schemasOf } from "@/model/databaseSchemas";
import { TreeRow } from "./TreeRow";
import { RowMenu, type RowMenuState } from "./RowMenu";
import { menuFor, isMovable, type MenuActionId, type MenuTarget } from "./menus";
import { useTreeKeyboard, type KeyRow, type TreeAction } from "./useTreeKeyboard";
import { useExplorerActions } from "./actions";
import { AddRelatedDialog, DeleteDialog, MapToDatabaseDialog, MoveDialog } from "./dialogs";
import { NewSchemaDialog } from "@/inspector/DatabaseSchemas";
import { MarkDialog, PromoteDialog } from "./markDialogs";
import type { MarkKind } from "./marks";
import { ImportCsvDialog, ImportSeedsDialog } from "@/workspaces/reference-data/dialogs";
import { exportAllSeeds } from "@/workspaces/reference-data/seedBundle";
import { createTargetSeed } from "@/workspaces/reference-data/seedTargets";
import { CREATE_LABELS, domainOfKey, EXPLORER_CREATE, startDomain, type CreateKind } from "./create";
import { CreateButtons } from "./NewElementDialog";
import { addToDiagram } from "@/workspaces/entities/actions";
import { ELEMENTS_MIME, relatedWithin, type RelationLookup } from "@/canvas/model";

const EMPTY_TABLES = new Map<string, TablesInput | undefined>();

// The forest's inputs are derived once per source object and shared, so two explorers on screen (the pinned one)
// hand `forestOf` the same inputs and share one forest.
const derived = new WeakMap<object, unknown>();
function once<T>(source: object | undefined | null, build: () => T): T | undefined {
  if (!source) return undefined;
  if (!derived.has(source)) derived.set(source, build());
  return derived.get(source) as T;
}
let lastDatabases: { signature: string; map: Map<string, DatabaseInfo> } | null = null;
let lastTables: { signature: string; map: Map<string, TablesInput | undefined> } | null = null;
const NOT_IN_DOMAIN = "@domain-model/not-in-domain";

/** The longest pause between the two clicks of a double click on a tree row. */
export const DOUBLE_CLICK_MS = 500;
/** How far, in pixels, the second click of a double click may land from the first. */
const DOUBLE_CLICK_SLOP = 4;
/** The last plain click on a tree row (its row, time and position), and when a double click was last handled from it.
 * Module state, not a row's or the explorer's: the first click changes the selection, and before the second click
 * arrives the row (or the explorer) may re-render or re-mount, and the tree may move, so the browser's dblclick cannot
 * be relied on to reach the row the user double-clicked, nor the second click to land on a row at all. */
const rowClicks = { explorer: "", key: "", at: 0, x: 0, y: 0, doubled: 0 };

/** The forest every explorer renders, from the index, the validation report, the table summaries and the settings. */
export function useForest(): { forest: Forest | null; rows: readonly ElementSummary[] | undefined; pending: boolean; error: Error | null } {
  const index = useIndex();
  const validation = useValidation();
  const settings = useSettings();
  const qc = useQueryClient();
  const rows = index.data;
  const databaseIds = useMemo(() => (rows ?? []).filter((r) => r.kind === "database").map((r) => r.id), [rows]);
  const tableResults = useQueries({ queries: databaseIds.map((id) => ({ ...tablesQuery(qc, id), enabled: false })) });
  const tablesSignature = tableResults.map((r) => r.dataUpdatedAt).join(",");
  const signature = `${databaseIds.join(",")}|${tablesSignature}`;
  const tables = useMemo(() => {
    if (!databaseIds.length) return EMPTY_TABLES;
    if (lastTables?.signature === signature) return lastTables.map;
    const map = new Map<string, TablesInput | undefined>();
    databaseIds.forEach((id, i) => {
      const data = tableResults[i]?.data as TablesState | undefined;
      map.set(id, data ? { tables: data.tables, stale: data.stale } : undefined);
    });
    lastTables = { signature, map };
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the signature stands for the results
  }, [signature]);
  const errors = useMemo(() => once(validation.data, () => errorCounts(validation.data?.diagnostics)), [validation.data]);
  const folders = useMemo(
    () =>
      once(settings.data, () =>
        settings.data?.json.explorer?.folders?.map((f) => ({
          ...f,
          icon: f.icon ?? null,
          match: { stereotype: f.match.stereotype ?? null, tag: f.match.tag ?? null, category: f.match.category ?? null },
        })),
      ),
    [settings.data],
  );
  // Every category tree, the global one first, then each domain's (§1.11): the filter's sub-categories and the
  // project-defined folders follow the domain-scoped trees too.
  const categoryTreeIds = useMemo(
    () =>
      (rows ?? [])
        .filter((r) => r.kind === "category-tree")
        .sort((a, b) => Number(!!a.package) - Number(!!b.package) || a.id.localeCompare(b.id))
        .map((r) => r.id),
    [rows],
  );
  const categoryTrees = useElements(categoryTreeIds);
  // Each database's declared schemas and default (erratum E26): the tree shows every declared schema as a row.
  const databaseDocs = useElements(databaseIds);
  const databaseSignature = databaseIds.map((x) => databaseDocs.byId.get(x)?.hash ?? "").join(",");
  const databases = useMemo(() => {
    // Shared across every useForest caller (as the tables are), so the forest cache sees one map per signature.
    if (lastDatabases?.signature === databaseSignature) return lastDatabases.map;
    const map = new Map<string, DatabaseInfo>();
    for (const x of databaseIds) {
      const json = databaseDocs.byId.get(x)?.json as Record<string, unknown> | undefined;
      if (!json) continue;
      map.set(x, {
        dialect: String(json.dialect ?? ""),
        defaultSchema: typeof json.defaultSchema === "string" ? json.defaultSchema : null,
        schemas: schemasOf(json).map((s) => s.name),
      });
    }
    lastDatabases = { signature: databaseSignature, map };
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the signature stands for the documents
  }, [databaseSignature]);
  const categoryDocs = categoryTreeIds.map((x) => categoryTrees.byId.get(x)?.json as CategoryTreeDoc | undefined);
  const categoryMaps = categoryMapsOf(categoryDocs);
  const categoryParents = categoryMaps?.parents;
  const categoryNames = categoryMaps?.names;
  const { store } = useServices();
  const referenceFlat = useEditor(store, (s) => s.explorer.referenceFlat);
  // The search worker takes the same rows, table summaries and categories (3.1).
  useSearchRows(rows);
  useEffect(() => {
    const client = searchClient();
    for (const [db, input] of tables) client.setTables(db, input?.tables);
  }, [tables]);
  useEffect(() => {
    searchClient().setCategories(categoryMaps?.list);
  }, [categoryMaps]);
  const forest = useMemo(
    () =>
      rows
        ? perfSync(
            "explorer:build",
            () => forestOf({ rows, tables, databases, errors, folders, categoryParents, categoryNames, referenceFlat }),
            // `patched`: the rows came from a model.changed or save patch, which forestOf applies in place (4.4).
            () => ({ elements: rows.length, patched: !!indexPatchOf(rows) }),
          )
        : null,
    [rows, tables, databases, errors, folders, categoryParents, categoryNames, referenceFlat],
  );
  // In idle time, build the folders the first paint left pending (4.3), so a first search or expand finds them built.
  useEffect(() => {
    if (!forest) return;
    // A forest with nothing pending is settled at once; one with pending folders when its prebuild ends (the scale project
    // waits on this flag: a forest patched from a prebuilt one can have nothing pending and so records no prebuilt entry).
    if (!forest.pending.size) {
      editorPerf().explorerSettled = true;
      return;
    }
    editorPerf().explorerSettled = false;
    let handle = 0;
    const idle = (cb: (deadline: { timeRemaining(): number }) => void) =>
      typeof requestIdleCallback === "function" ? requestIdleCallback(cb, { timeout: 2000 }) : window.setTimeout(() => cb({ timeRemaining: () => 8 }), 50);
    const cancel = (h: number) => (typeof cancelIdleCallback === "function" ? cancelIdleCallback(h) : window.clearTimeout(h));
    const started = perfStart("explorer:prebuilt");
    const step = (deadline: { timeRemaining(): number }) => {
      const sliceEnd = performance.now() + 4;
      if (prebuild(forest, () => deadline.timeRemaining() > 2 && performance.now() < sliceEnd)) handle = idle(step);
      else {
        started({ rows: forest.nodes.size });
        editorPerf().explorerSettled = true;
      }
    };
    handle = idle(step);
    return () => cancel(handle);
  }, [forest]);
  return { forest, rows, pending: index.isPending, error: (index.error as Error | null) ?? null };
}

/** The search worker's answer for one filter (3.1). */
interface FilterResult {
  filter: ExplorerFilter;
  ids: string[];
  counts: Record<SearchPlace, number>;
  tablesLoading: boolean;
  /** The worker's time for it. */
  ms: number;
}

/**
 * One derived value per set of category-tree documents, module-wide like `once`: the forest is cached on the maps'
 * identity, so every explorer on screen (and one mounted after a rail switch) shares the same forest.
 */
let lastCategories: { docs: (CategoryTreeDoc | undefined)[]; maps: CategoryMaps | undefined } | null = null;
function categoryMapsOf(docs: (CategoryTreeDoc | undefined)[]): CategoryMaps | undefined {
  if (!lastCategories || lastCategories.docs.length !== docs.length || lastCategories.docs.some((d, i) => d !== docs[i]))
    lastCategories = { docs, maps: mergeCategoryTrees(docs) };
  return lastCategories.maps;
}

const NO_MEMBERS: ReadonlySet<string> = new Set();
const NO_IDS: string[] = [];

/**
 * The elements on the active canvas (explorer-redesign.md 3.5, membership dots): a diagram's members (its draft while
 * one is open), or a domain view's entities.
 */
function useCanvasMembers(): ReadonlySet<string> {
  const { store } = useServices();
  const active = useEditor(store, (s) => s.activeDiagram);
  const diagramId = active && !active.startsWith("pkg:") ? active : null;
  const doc = useElement(diagramId);
  const draft = useEditor(store, (s) => (diagramId ? s.drafts[diagramId]?.json : undefined));
  const index = useIndex();
  return useMemo(() => {
    if (!active) return NO_MEMBERS;
    if (!diagramId) {
      const pkg = active.slice(4);
      return new Set((index.data ?? []).filter((r) => r.kind === "entity" && r.package === pkg).map((r) => r.id));
    }
    const json = (draft ?? doc.data?.json) as { members?: { element: string }[] } | undefined;
    return new Set((json?.members ?? []).map((m) => m.element));
  }, [active, diagramId, draft, doc.data, index.data]);
}

/** The relation walk over the tree's related maps (no document loads). */
const relationsIn = (forest: Forest): RelationLookup => ({
  relationsOf: (id) => forest.related.relationsOf.get(id) ?? [],
  endsOf: (id) => forest.related.endsOf.get(id),
});

/**
 * The chip data the worker cannot see (3.2): the elements with errors (Has errors) and the chosen diagram's members
 * (On this diagram). Undefined while the chip is off, so a validation run does not re-ask an unrelated filter.
 */
function useChipData(filter: ExplorerFilter): { errorIds?: string[]; members?: string[] } {
  const validation = useValidation();
  const diagram = useElement(filter.diagram);
  const errorIds = useMemo(() => (filter.errors ? [...errorCounts(validation.data?.diagnostics).keys()] : undefined), [filter.errors, validation.data]);
  const members = useMemo(() => {
    if (!filter.diagram) return undefined;
    const json = diagram.data?.json as { members?: { element: string }[] } | undefined;
    return (json?.members ?? []).map((m) => m.element);
  }, [filter.diagram, diagram.data]);
  return useMemo(() => ({ errorIds, members }), [errorIds, members]);
}

/** Asks the worker for the filter's matches; the answer of the latest filter wins. Re-asks when the data changes. */
function useTreeFilter(id: ExplorerId, filter: ExplorerFilter): FilterResult | null {
  const version = useSearchVersion();
  const [result, setResult] = useState<FilterResult | null>(null);
  const filtering = isFiltering(filter);
  const chipData = useChipData(filter);
  // A layout effect: the question leaves right after the commit, not after the next paint.
  useLayoutEffect(() => {
    if (!filtering) return;
    let live = true;
    void searchClient()
      .filter(filter.text, id, {
        tags: filter.tags,
        categories: filter.categories,
        stereotypes: filter.stereotypes,
        kinds: filter.kinds,
        domain: filter.domain,
        errorIds: chipData.errorIds,
        members: chipData.members,
      })
      .then((a) => {
        // Painted in this task: a scheduled render would wait for the next frame.
        if (live) flushSync(() => setResult({ filter, ids: a.ids, counts: a.counts, tablesLoading: a.tablesLoading, ms: a.ms }));
      });
    return () => {
      live = false;
    };
  }, [filter, filtering, id, version, chipData]);
  return filtering ? result : null;
}

interface RowCache {
  forest: Forest | null;
  filter: ExplorerFilter | null;
  answer: FilterResult | null;
  /** In filter mode, per container: how many of its direct children are shown. */
  shown: Map<string, number> | null;
  rows: VisibleRow[];
  /** The expanded set the rows were walked with: the view's, or in filter mode the matches' ancestors. */
  open: Set<string>;
  filtering: boolean;
}

export function Explorer({ id, pinned = false }: { id: ExplorerId; pinned?: boolean }) {
  const services = useServices();
  const { store, realtime, queryClient } = services;
  const { forest, rows: indexRows, pending, error } = useForest();
  const selection = useEditor(store, (s) => s.selection);
  // Each explorer's own selection (the rows it shows selected, what a Ctrl+click extends): the store records every
  // selection under the explorer it was made in. The related highlight and follow-selection read the global one.
  const own = useEditor(store, (s) => s.selectionBy[id] ?? NO_IDS);
  const drafts = useEditor(store, (s) => s.drafts);
  const presence = useEditor(store, (s) => s.presence);
  const view = useEditor(store, (s) => s.explorer.views[id]);
  const highlight = useEditor(store, (s) => s.explorer.highlightRelated);
  const referenceFlat = useEditor(store, (s) => s.explorer.referenceFlat);
  const followSelection = useEditor(store, (s) => s.explorer.followSelection);
  const pinnedId = useEditor(store, (s) => s.explorer.pinned);
  const { reveal, select: selectIn, openDatabase, openWorkspace, openDiagram, openEditor, openSeedData } = useEditorNavigation();
  // The pinned second explorer keeps its own selection (1.0); the inspector follows it while it is pinned.
  const select = useCallback((ids: string[]) => selectIn(ids, null, pinned ? id : undefined), [selectIn, pinned, id]);
  const [importingSeeds, setImportingSeeds] = useState(false);
  const [importingCsv, setImportingCsv] = useState<{ id: string; name: string } | null>(null);
  const exportSeeds = (domain?: { id: string; name: string }) =>
    void exportAllSeeds(services, domain)
      .then((n) =>
        store
          .getState()
          .notify(
            n
              ? `Exported ${n} ${n === 1 ? "seed" : "seeds"} to ${domain ? `${domain.name} seed data.zip` : "seed-data.zip"}.`
              : domain
                ? `${domain.name} has no seed data yet.`
                : "The model has no seed data yet.",
          ),
      )
      .catch((e: Error) => store.getState().notify(e.message, "error"));
  /** Import seed CSV… on an entity: into its seed (New seed first when it has none), shown on its Seed data tab. */
  const importSeedCsv = async (entity: ElementSummary) => {
    const existing = (indexRows ?? []).filter((r) => r.kind === "seed" && r.target === entity.id).sort((a, b) => a.name.localeCompare(b.name));
    const seed = existing.find((r) => r.name === entity.name) ?? existing[0];
    const id = seed?.id ?? (await createTargetSeed(services, entity.id));
    if (!id) return;
    openSeedData(entity);
    setImportingCsv({ id, name: seed?.name ?? entity.name });
  };
  const canvasMembers = useCanvasMembers();
  const [addingRelated, setAddingRelated] = useState<string[] | null>(null);
  const actions = useExplorerActions();
  const [version, bump] = useReducer((n: number) => n + 1, 0);
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const [anchorKey, setAnchorKey] = useState<string | null>(null);
  const [menu, setMenu] = useState<(RowMenuState & { keys: string[] }) | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);
  const [newSchemaFor, setNewSchemaFor] = useState<string | null>(null);
  const [loading, setLoading] = useState<ReadonlySet<string>>(new Set());
  const [moving, setMoving] = useState<string[] | null>(null);
  const [mappingTo, setMappingTo] = useState<string[] | null>(null);
  const [marking, setMarking] = useState<{ kind: MarkKind; ids: string[] } | null>(null);
  const [promoting, setPromoting] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<string[] | null>(null);
  const [dropKey, setDropKey] = useState<string | null>(null);
  const dragged = useRef<string[]>([]);
  const scrollRef = useRef<HTMLDivElement>(null);
  const cache = useRef<RowCache>({ forest: null, filter: null, answer: null, shown: null, rows: [], open: view.expanded, filtering: false });
  const filter = view.filter;
  const searchRef = useRef<HTMLInputElement>(null);
  const result = useTreeFilter(id, filter);
  const filteringNow = isFiltering(filter);

  // ------------------------------------------------------------------ rows
  // Filter mode shows the worker's answer for the current filter; until it arrives the previous rows stay.
  const c = cache.current;
  const answer = filteringNow && result?.filter === filter ? result : null;
  if (forest) {
    if (answer) {
      if (c.forest !== forest || c.answer !== answer) {
        const { rows, open, shown } = perfSync(
          "explorer:filter",
          () => filteredRows(forest, id, answer.ids),
          (r) => ({ matches: answer.ids.length, rows: r.rows.length, workerMs: Math.round(answer.ms * 10) / 10 }),
        );
        cache.current = { forest, filter, answer, shown, rows, open, filtering: true };
      }
    } else if (!filteringNow) {
      if (c.forest !== forest || c.filtering || c.filter !== filter)
        cache.current = { forest, filter, answer: null, shown: null, rows: visibleRows(forest, id, view.expanded), open: view.expanded, filtering: false };
    } else if (c.forest !== forest) {
      cache.current = { forest, filter, answer: null, shown: null, rows: visibleRows(forest, id, view.expanded), open: view.expanded, filtering: false };
    }
  }
  const rows = cache.current.rows;
  const open = cache.current.open;
  const rebuild = useCallback(() => {
    cache.current.forest = null;
    bump();
  }, []);

  // Expansion survives a reload (3.3) as page state, per project: announced whenever the rows change outside filter mode.
  useEffect(() => {
    if (cache.current.filtering) return;
    pageChanged();
  }, [version, id, view]);

  // Each forest the tree commits, patched or built (the scale project times model.changed → tree updated with it).
  useLayoutEffect(() => {
    if (forest) perfStart("explorer:commit")({ nodes: forest.nodes.size });
  }, [forest]);

  // The first rows painted from the index (4.5, "first rows").
  const firstRows = useRef<ReturnType<typeof perfStart> | null>(null);
  if (!firstRows.current && rows.length > 0) firstRows.current = perfStart("explorer:first-rows");
  useLayoutEffect(() => {
    const end = firstRows.current;
    if (end && rows.length > 0) perfOnce("explorer:first-rows", () => end({ rows: rows.length }));
  }, [rows]);

  // Right after the first paint, the table summaries of every database, in the background (4.2).
  const tablesStarted = useRef(false);
  useEffect(() => {
    if (!indexRows || tablesStarted.current) return;
    tablesStarted.current = true;
    prefetchAllTables(queryClient, indexRows);
  }, [indexRows, queryClient]);

  const others = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const p of presence) {
      if (p.connectionId === realtime.connectionId || !p.elementId) continue;
      const list = map.get(p.elementId);
      if (!list) map.set(p.elementId, [p.user]);
      else if (!list.includes(p.user)) list.push(p.user);
    }
    return map;
  }, [presence, realtime.connectionId]);
  // The "n people here" roll-up on collapsed containers (1.10).
  const peopleHere = useMemo(() => (forest && others.size ? presenceCounts(forest, others) : new Map<string, number>()), [forest, others]);
  const favorites = useEditor(store, (s) => s.explorer.favorites);
  const favoriteSet = useMemo(() => new Set(favorites), [favorites]);
  const onToggleFavorite = useCallback(
    (key: string) => {
      const x = forest ? nodeOf(forest, key)?.id : undefined;
      if (x) store.getState().toggleFavorite(x);
    },
    [forest, store],
  );

  const selected = useMemo(() => new Set(own), [own]);
  const keyIndex = useMemo(() => {
    const map = new Map<string, number>();
    rows.forEach((r, i) => map.set(r.key, i));
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps -- rows is spliced in place; the version re-runs it
  }, [rows, version]);
  const active = activeKey !== null ? (keyIndex.get(activeKey) ?? 0) : 0;

  const rowHeight = useMemo(() => densityRowHeight(), []);
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => rowHeight,
    overscan: 12,
    initialOffset: view.scroll,
  });
  useEffect(() => {
    const el = scrollRef.current;
    return () => {
      // The store's current view object: a filter change replaces it.
      if (el) store.getState().explorer.views[id].scroll = el.scrollTop;
    };
  }, [store, id, pending]);

  // ------------------------------------------------------------------ selection follows the active tab (1.0, 3.5)
  useEffect(() => {
    if (!forest || !followSelection || selection.length !== 1 || cache.current.filtering) return;
    const key = forest.place.get(selection[0]);
    if (!key || forest.nodes.get(key)?.explorer !== id) return;
    let added = false;
    for (const k of revealPath(forest, selection[0]))
      if (!k.startsWith("@") || forest.nodes.get(k)?.type !== "root")
        if (!view.expanded.has(k)) {
          view.expanded.add(k);
          added = true;
        }
    if (added) rebuild();
    setActiveKey(key);
  }, [forest, selection, followSelection, id, view, rebuild, filteringNow]);
  // A row made active by a pointer click is on screen already: scrolling to it would move another row under the
  // pointer between the two clicks of a double click.
  const pointerActive = useRef<string | null>(null);
  useEffect(() => {
    if (activeKey === null) return;
    if (pointerActive.current === activeKey) {
      pointerActive.current = null;
      return;
    }
    pointerActive.current = null;
    const i = keyIndex.get(activeKey);
    if (i !== undefined) virtualizer.scrollToIndex(i, { align: "auto" });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- scroll only when the active row changes
  }, [activeKey]);

  // ------------------------------------------------------------------ related highlighting (1.9)
  // A selected table or column (explorerItem) takes the place of the element selection: its "mapped by" rows (1.3).
  const explorerItem = useEditor(store, (s) => s.explorerItem);
  const selectedKey = forest && selection.length === 1 ? forest.place.get(selection[0]) : undefined;
  const focusKey = forest && explorerItem && forest.nodes.has(explorerItem) ? explorerItem : selectedKey;
  const related = useMemo(() => (forest && highlight && focusKey ? relatedKeys(forest, focusKey) : new Set<string>()), [forest, highlight, focusKey]);
  const onCanvasCount = useMemo(
    () => (forest && canvasMembers.size ? canvasCounts(forest, canvasMembers) : new Map<string, number>()),
    [forest, canvasMembers],
  );
  const relatedCount = useMemo(() => (forest && related.size ? relatedCounts(forest, related) : new Map<string, number>()), [forest, related]);
  // The commit that paints a new highlight (the scale project times selection -> related rows highlighted with it).
  useLayoutEffect(() => {
    if (related.size) perfStart("explorer:highlight")({ related: related.size });
  }, [related]);

  // ------------------------------------------------------------------ expand and collapse
  const expand = useCallback(
    async (key: string) => {
      if (!forest) return;
      const node = nodeOf(forest, key);
      if (!node || !isExpandable(forest, key)) return;
      const loaded = loadedDocs.get(forest) ?? new Set<string>();
      loadedDocs.set(forest, loaded);
      const database = node.load === "table" && node.table ? databaseOf(forest, key) : undefined;
      if (((node.load === "document" && node.id) || database) && !loaded.has(key)) {
        setLoading((s) => new Set(s).add(key));
        try {
          if (database) {
            // A table's children (1.3): its detail (E5f, else the database read) as Columns, keys and indexes.
            const view = await queryClient.fetchQuery(tableDetailQuery(database, node.table!.key));
            if (view) tableChildren(forest, key, view);
          } else documentChildren(forest, key, await queryClient.fetchQuery(elementQuery(node.id!)));
          loaded.add(key);
        } catch {
          // The row expands with the children the index answers.
        } finally {
          setLoading((s) => {
            const next = new Set(s);
            next.delete(key);
            return next;
          });
        }
      }
      const { rows: list, open: set } = cache.current;
      const i = list.findIndex((r) => r.key === key);
      if (set.has(key) || i < 0) return;
      set.add(key);
      expandAt(forest, list, i, set);
      bump();
    },
    [forest, queryClient],
  );
  const collapse = useCallback((key: string) => {
    const { rows: list, open: set } = cache.current;
    const i = list.findIndex((r) => r.key === key);
    if (!set.has(key) || i < 0) return;
    set.delete(key);
    collapseAt(list, i);
    bump();
  }, []);
  const toggle = useCallback((key: string) => (cache.current.open.has(key) ? collapse(key) : void expand(key)), [expand, collapse]);
  const expandAll = useCallback(
    (key: string) => {
      if (!forest) return;
      const stack = [key];
      for (let guard = 0; stack.length && guard < 20_000; guard++) {
        const k = stack.pop()!;
        if (!isExpandable(forest, k)) continue;
        cache.current.open.add(k);
        const node = forest.nodes.get(k);
        if (node && (node.type === "element" || node.type === "table")) continue;
        stack.push(...childKeys(forest, k));
      }
      rebuild();
    },
    [forest, rebuild],
  );
  const collapseAll = useCallback(() => {
    view.expanded.clear();
    rebuild();
  }, [view, rebuild]);

  // ------------------------------------------------------------------ selection (1.8: one kind at a time)
  const kindOf = useCallback((key: string) => (forest ? nodeOf(forest, key)?.kind : undefined), [forest]);
  const idOf = useCallback(
    (key: string) => {
      const node = forest ? nodeOf(forest, key) : undefined;
      return node?.home && node.id && forest?.byId.has(node.id) ? node.id : undefined;
    },
    [forest],
  );
  const selectKeys = useCallback(
    (keys: string[]) => {
      const ids = keys.map(idOf).filter((x): x is string => !!x);
      select(ids);
    },
    [idOf, select],
  );

  /** A single click ("click"), or Enter, a double click and the menu's Open ("open"): an element with an editor
   * opens pinned in its editor tab on "open", and in the preview tab on a click while an editor tab shows (3.6). */
  const openRow = useCallback(
    (key: string, how: "click" | "open" = "click") => {
      if (!forest) return;
      const node = nodeOf(forest, key);
      if (!node) return;
      const targetId = node.target ? forest.nodes.get(node.target)?.id : undefined;
      // Opening (not a click, which only selects) makes the element a recent one (3.3).
      const opened = targetId ?? node.id;
      if (how === "open" && opened && forest.byId.has(opened)) store.getState().noteRecent(opened);
      if (targetId && forest.byId.has(targetId)) {
        reveal(forest.byId.get(targetId) as ElementSummary);
        return;
      }
      if (node.type === "domain" && node.id && forest.byId.has(node.id)) {
        // The domain editor (1.11): General, Tags, Categories; pinned on Open (Enter, a double click, the menu).
        openEditor(forest.byId.get(node.id) as ElementSummary, how === "open");
        return;
      }
      // Opening a table shows it focused in the Database screen (1.3).
      const database = node.type === "table" && node.table ? databaseOf(forest, key) : undefined;
      if (how === "open" && database && node.table) {
        store.getState().setDatabaseTable(node.table.key);
        openDatabase(database);
        return;
      }
      if (node.id && forest.byId.has(node.id) && node.home) {
        const summary = forest.byId.get(node.id) as ElementSummary;
        if (how === "open" && hasEditor(summary.kind)) openEditor(summary, true);
        else if (hasEditor(summary.kind) && store.getState().editors.active !== null) openEditor(summary, false);
        else reveal(summary);
        return;
      }
      // A table or a column is selected in place: its "mapped by" rows are highlighted in the Domain model (1.3).
      if (node.type === "table" || (node.type === "item" && node.attribute !== undefined)) {
        store.getState().setExplorerItem(key);
        return;
      }
      toggle(key);
    },
    [forest, reveal, toggle, openEditor, openDatabase, store],
  );
  /** A double click: opens the row pinned (as Enter does). A folder, a schema or a database only toggles, which the
   * first click did already; a domain opens its editor without toggling again. */
  const doubleClickRow = useCallback(
    (key: string) => {
      if (!forest) return;
      const type = nodeOf(forest, key)?.type;
      if (type === "folder" || type === "group" || type === "schema" || type === "database") return;
      openRow(key, "open");
    },
    [forest, openRow],
  );
  /** The browser's dblclick: ignored when the second click already handled the double click, which it does unless
   * the two clicks came without a click before (a synthetic double click). */
  const onRowDoubleClick = useCallback(
    (key: string) => {
      if (performance.now() - rowClicks.doubled < DOUBLE_CLICK_MS) return;
      openRow(key, "open");
    },
    [openRow],
  );

  /** True when a plain click at `now` may be the second click of a double click on the last clicked row. A folder, a
   * schema or a database has no double-click action, so every click on one toggles it, however fast. */
  const awaitsSecondClick = useCallback(
    (now: number) => {
      if (!forest || !rowClicks.key || rowClicks.explorer !== id || now - rowClicks.at >= DOUBLE_CLICK_MS) return false;
      const firstType = nodeOf(forest, rowClicks.key)?.type;
      return !(firstType === "folder" || firstType === "group" || firstType === "schema" || firstType === "database");
    },
    [forest, id],
  );
  /** The second click of a double click, whatever is under the pointer now: the first click may have grown the strip
   * above the tree or moved the tree, so the second one can land on something that is not a row. Caught on the way down
   * (the capture phase) at the explorer, it opens the first row and goes no further. */
  const onSectionClickCapture = useCallback(
    (e: MouseEvent) => {
      if (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
      // Only the browser's own second click counts here (detail 2): a fresh single click that happens to land where
      // the last row was, after a search narrowed the tree, is a plain click on the row now there.
      if (e.detail < 2) return;
      const now = performance.now();
      if (!awaitsSecondClick(now)) return;
      if (Math.abs(e.clientX - rowClicks.x) > DOUBLE_CLICK_SLOP || Math.abs(e.clientY - rowClicks.y) > DOUBLE_CLICK_SLOP) return;
      e.stopPropagation();
      const first = rowClicks.key;
      rowClicks.at = 0;
      rowClicks.doubled = now;
      doubleClickRow(first);
    },
    [awaitsSecondClick, doubleClickRow],
  );

  const onRowClick = useCallback(
    (key: string, e: MouseEvent) => {
      if (!forest) return;
      const plain = !e.ctrlKey && !e.metaKey && !e.shiftKey;
      const now = performance.now();
      if (plain && awaitsSecondClick(now)) {
        // The second click of a double click on the same row, even when its element was re-rendered in between or it
        // moved away from the first click's place (a click where the first one was is caught by the explorer first).
        const inPlace = Math.abs(e.clientX - rowClicks.x) <= DOUBLE_CLICK_SLOP && Math.abs(e.clientY - rowClicks.y) <= DOUBLE_CLICK_SLOP;
        if (rowClicks.key === key || (e.detail >= 2 && inPlace)) {
          const first = rowClicks.key;
          rowClicks.at = 0;
          rowClicks.doubled = now;
          doubleClickRow(first);
          return;
        }
      }
      Object.assign(rowClicks, { explorer: id, key: plain ? key : "", at: now, x: e.clientX, y: e.clientY });
      pointerActive.current = key;
      setActiveKey(key);
      const node = nodeOf(forest, key);
      if (!node) return;
      const elementId = idOf(key);
      if ((e.ctrlKey || e.metaKey) && elementId) {
        const sameKind = own.every((s) => forest.byId.get(s)?.kind === node.kind);
        select(sameKind ? (selected.has(elementId) ? own.filter((x) => x !== elementId) : [...own, elementId]) : [elementId]);
        setAnchorKey(key);
        return;
      }
      if (e.shiftKey && elementId && anchorKey) {
        const from = keyIndex.get(anchorKey) ?? 0;
        const to = keyIndex.get(key) ?? 0;
        const range = cache.current.rows
          .slice(Math.min(from, to), Math.max(from, to) + 1)
          .map((r) => r.key)
          .filter((k) => kindOf(k) === node.kind);
        selectKeys(range);
        return;
      }
      setAnchorKey(key);
      if (node.type === "folder" || node.type === "group" || node.type === "schema") toggle(key);
      else if (node.type === "domain" || node.type === "database") {
        toggle(key);
        if (elementId) select([elementId]);
      } else openRow(key);
    },
    [forest, id, idOf, own, selected, select, anchorKey, keyIndex, kindOf, selectKeys, toggle, openRow, doubleClickRow, awaitsSecondClick],
  );

  // ------------------------------------------------------------------ menus
  const targetOf = useCallback(
    (key: string): MenuTarget | null => {
      const node = forest ? nodeOf(forest, key) : undefined;
      if (!node || !forest) return null;
      const linked =
        node.kind === "entity" ? !!node.id && (forest.related.tablesOf.get(node.id)?.length ?? 0) > 0 : node.type === "table" ? !!node.table?.entityId : false;
      const element = idOf(key);
      const domainGroup = node.type === "group" && !!node.id && forest.byId.get(node.id)?.kind === "package";
      const creates = node.type === "group" && !domainGroup && (key === NOT_IN_DOMAIN_KEY || node.explorer !== "domain-model");
      return {
        type: node.type,
        kind: node.kind,
        element: !!element,
        linked,
        favorite: !!element && favoriteSet.has(element),
        domainGroup,
        explorer: creates ? node.explorer : undefined,
      };
    },
    [forest, idOf, favoriteSet],
  );
  const openMenu = useCallback(
    (key: string, at: { x: number; y: number }) => {
      if (!forest) return;
      const id = idOf(key);
      const keys = id && selected.has(id) && own.length > 1 ? own.map((s) => forest.place.get(s)).filter((k): k is string => !!k) : [key];
      if (id && !selected.has(id)) select([id]);
      const targets = keys.map(targetOf).filter((t): t is MenuTarget => !!t);
      const title = keys.length > 1 ? `${keys.length} selected` : (nodeOf(forest, key)?.label ?? "");
      setMenu({ ...at, title, items: menuFor(targets), keys });
    },
    [forest, idOf, selected, own, select, targetOf],
  );
  const onContextMenu = useCallback(
    (key: string, e: MouseEvent) => {
      e.preventDefault();
      setActiveKey(key);
      openMenu(key, { x: e.clientX, y: e.clientY });
    },
    [openMenu],
  );
  const menuAtRow = (key: string) => {
    const el = document.getElementById(domIdOf(id, key));
    const box = el?.getBoundingClientRect();
    openMenu(key, { x: (box?.left ?? 0) + 24, y: (box?.bottom ?? 0) - 4 });
  };

  const namesOf = (ids: readonly string[]) => new Map(ids.map((x) => [x, forest?.byId.get(x)?.name ?? x]));
  const kindsOf = (ids: readonly string[]) => new Map(ids.map((x) => [x, forest?.byId.get(x)?.kind ?? ""]));

  /** Add to diagram, and Add with related… at a depth (1.8): entities, relationships (with their ends), and the
   * relationships between everything then on the active diagram. */
  const activeDiagram = useEditor(store, (s) => s.activeDiagram);
  const diagramOpen = activeDiagram && !activeDiagram.startsWith("pkg:") ? activeDiagram : null;
  const addTo = async (ids: readonly string[], depth: number) => {
    const s = store.getState();
    if (!forest) return;
    if (!diagramOpen) {
      s.notify("Open a diagram first: a domain's view already shows all of its entities.", "error");
      return;
    }
    const lookup = relationsIn(forest);
    const kind = (x: string) => forest.byId.get(x)?.kind;
    const relations = ids.filter((x) => kind(x) === "relation");
    const start = [...ids.filter((x) => kind(x) === "entity"), ...relations.flatMap((r) => lookup.endsOf(r) ?? [])];
    const entities = depth > 0 ? relatedWithin(start, depth, lookup) : start;
    const added = await addToDiagram(services, diagramOpen, { entities, relations, lookup });
    const name = forest.byId.get(diagramOpen)?.name ?? "the diagram";
    s.notify(added.length ? `Added ${added.length === 1 ? "1 element" : `${added.length} elements`} to ${name}.` : `Already on ${name}.`);
    if (start[0]) s.requestCenter(start[0]);
  };

  const run = (action: MenuActionId, keys: string[]) => {
    if (!forest) return;
    if (action.startsWith("type:")) {
      const ids = keys.map(idOf).filter((x): x is string => !!x);
      select(ids.slice(0, 1));
      if (store.getState().workspace !== "reference-data") openWorkspace("reference-data");
      store.getState().requestTypeAction({ action: action.slice(5), ids });
      return;
    }
    if (action.startsWith("new:")) {
      store.getState().requestNew({ kind: action.slice(4) as CreateKind, domain: domainOfKey(forest, keys[0]) });
      return;
    }
    const key = keys[0];
    const node = nodeOf(forest, key);
    const ids = keys.map(idOf).filter((x): x is string => !!x);
    switch (action) {
      case "open":
        openRow(key, "open");
        break;
      case "search-in-domain":
        if (node?.id) setFilter({ ...filter, domain: node.id });
        searchRef.current?.focus();
        break;
      case "favorite":
        for (const x of ids) store.getState().toggleFavorite(x);
        break;
      case "open-database":
        if (node?.id) openDatabase(node.id);
        break;
      case "open-mappings":
        openWorkspace("mappings");
        break;
      case "new-schema":
        if (node?.id) setNewSchemaFor(node.id);
        break;
      case "show-on-canvas": {
        const target = ids[0];
        if (!target) break;
        const row = forest.byId.get(target);
        if (canvasMembers.has(target)) openWorkspace("entities");
        else if (row?.kind === "entity" && row.package) openDiagram(`pkg:${row.package}`);
        else {
          store.getState().notify(`${row?.name ?? "The element"} is not on the canvas. Add it to a diagram first.`, "error");
          break;
        }
        select(ids);
        store.getState().requestCenter(target);
        break;
      }
      case "add-to-diagram":
        void addTo(ids, 0);
        break;
      case "add-with-related":
        setAddingRelated(ids);
        break;
      case "where-used":
        if (ids[0]) store.getState().showReferences(ids[0]);
        break;
      case "go-to-table": {
        const table = node?.id ? forest.related.tablesOf.get(node.id)?.[0] : undefined;
        if (table) {
          store.getState().setSidebar("databases");
          const t = forest.nodes.get(table);
          if (t?.id) select([t.id]);
        }
        break;
      }
      case "go-to-entity":
        if (node?.table?.entityId) {
          store.getState().setSidebar("domain-model");
          select([node.table.entityId]);
        }
        break;
      case "go-to-ends": {
        const end = node?.id ? forest.related.endsOf.get(node.id)?.[0] : undefined;
        if (end) select([end]);
        break;
      }
      case "duplicate":
        if (ids[0]) void actions.duplicate(ids[0]);
        break;
      case "select-all":
        selectKeys([...childKeys(forest, key)]);
        break;
      case "expand-all":
        expandAll(key);
        break;
      case "move":
        setMoving(ids);
        break;
      case "map-to-database":
        if (ids.length) setMappingTo(ids);
        break;
      case "apply-stereotype":
      case "tag":
      case "set-category":
        if (ids.length) setMarking({ kind: action === "apply-stereotype" ? "stereotype" : action === "tag" ? "tag" : "category", ids });
        break;
      case "promote":
        if (ids[0]) setPromoting(ids[0]);
        break;
      case "edit-seed-data":
        if (ids[0] && forest.byId.has(ids[0])) openSeedData(forest.byId.get(ids[0]) as ElementSummary);
        break;
      case "import-seed-csv":
        if (ids[0] && forest.byId.has(ids[0])) void importSeedCsv(forest.byId.get(ids[0]) as ElementSummary);
        break;
      case "export-seeds": {
        const domain = ids[0] ? forest.byId.get(ids[0]) : undefined;
        exportSeeds(domain ? { id: domain.id, name: domain.name } : undefined);
        break;
      }
      case "import-seeds":
        setImportingSeeds(true);
        break;
      case "rename":
        setRenaming(key);
        break;
      case "delete":
        if (ids.length) setDeleting(ids);
        break;
    }
  };

  // ------------------------------------------------------------------ keyboard (3.4)
  const keyRows = useCallback(
    (): KeyRow[] =>
      forest
        ? cache.current.rows.map((r) => {
            const node = forest.nodes.get(r.key);
            return {
              key: r.key,
              depth: r.depth,
              label: node?.label ?? "",
              kind: node?.kind,
              expandable: isExpandable(forest, r.key),
              expanded: cache.current.open.has(r.key),
            };
          })
        : [],
    [forest],
  );
  const apply = (a: TreeAction) => {
    const list = cache.current.rows;
    if (a.type === "move") {
      const key = list[a.index]?.key;
      if (!key) return;
      setActiveKey(key);
      if (a.extend) {
        const kind = kindOf(key);
        const from = keyIndex.get(anchorKey ?? activeKey ?? key) ?? a.index;
        const range = list
          .slice(Math.min(from, a.index), Math.max(from, a.index) + 1)
          .map((r) => r.key)
          .filter((k) => kindOf(k) === kind);
        selectKeys(range);
      } else setAnchorKey(key);
      return;
    }
    const key = "index" in a ? list[a.index]?.key : undefined;
    if (a.type === "expand" && key) void expand(key);
    else if (a.type === "collapse" && key) collapse(key);
    else if (a.type === "expand-siblings" && key && forest) {
      const parent = forest.parent.get(key);
      for (const k of parent ? childKeys(forest, parent) : []) if (isExpandable(forest, k)) cache.current.open.add(k);
      rebuild();
    } else if (a.type === "open" && key) openRow(key, "open");
    else if (a.type === "toggle-select" && key) {
      const id = idOf(key);
      if (!id) return;
      const kind = kindOf(key);
      const same = own.every((s) => forest?.byId.get(s)?.kind === kind);
      select(same ? (selected.has(id) ? own.filter((x) => x !== id) : [...own, id]) : [id]);
    } else if (a.type === "rename" && key && isMovable(kindOf(key)) && idOf(key)) setRenaming(key);
    else if (a.type === "delete") {
      const ids = own.filter((s) => forest?.byId.has(s) && forest.nodes.get(forest.place.get(s) ?? "")?.explorer === id);
      if (ids.length) setDeleting(ids);
    } else if (a.type === "menu" && key) menuAtRow(key);
  };
  const page = Math.max(1, Math.floor((scrollRef.current?.clientHeight ?? 600) / rowHeight) - 1);
  const onKeyDown = useTreeKeyboard({ rows: keyRows, active, page, apply });

  // ------------------------------------------------------------------ drag to move to a domain
  const onDragStart = useCallback(
    (key: string, e: DragEvent) => {
      const id = idOf(key);
      if (!id) return;
      dragged.current = selected.has(id) ? own.filter((s) => isMovable(forest?.byId.get(s)?.kind)) : [id];
      e.dataTransfer.effectAllowed = "copyMove";
      e.dataTransfer.setData("text/plain", dragged.current.join(","));
      // Onto the canvas (3.5): the entities and relationships among the dragged rows.
      const onCanvas = dragged.current.filter((x) => ["entity", "relation"].includes(forest?.byId.get(x)?.kind ?? ""));
      if (onCanvas.length) e.dataTransfer.setData(ELEMENTS_MIME, JSON.stringify(onCanvas));
    },
    [idOf, selected, own, forest],
  );
  const dropTargetOf = useCallback(
    (key: string): string | null | undefined => {
      if (!forest || !dragged.current.length) return undefined;
      const node = nodeOf(forest, key);
      if (key === NOT_IN_DOMAIN) return null;
      if (node?.type !== "domain" || !node.id || !forest.byId.has(node.id)) return undefined;
      // Not into itself or one of its own sub-domains.
      for (let k: string | undefined = key, guard = 0; k && guard < 256; k = forest.parent.get(k), guard++) {
        const n = forest.nodes.get(k);
        if (n?.id && dragged.current.includes(n.id)) return undefined;
      }
      return node.id;
    },
    [forest],
  );
  const onDragOver = useCallback(
    (key: string, e: DragEvent) => {
      if (dropTargetOf(key) === undefined) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = "move";
      setDropKey(key);
    },
    [dropTargetOf],
  );
  const onDrop = useCallback(
    (key: string, e: DragEvent) => {
      const target = dropTargetOf(key);
      setDropKey(null);
      if (target === undefined) return;
      e.preventDefault();
      const ids = dragged.current;
      dragged.current = [];
      void actions.move(ids, new Map(ids.map((x) => [x, forest?.byId.get(x)?.kind ?? ""])), target);
    },
    [dropTargetOf, actions, forest],
  );

  const onRename = useCallback(
    (key: string, name: string | null) => {
      setRenaming(null);
      const id = idOf(key);
      const node = forest?.nodes.get(key);
      if (id && name && name !== node?.label) void actions.rename(id, name);
    },
    [idOf, forest, actions],
  );

  // ------------------------------------------------------------------ render
  const setFilter = (next: ExplorerFilter) => store.getState().setExplorerFilter(id, next);
  const title = EXPLORER_LABELS[id];
  const activeRow = rows[active];
  // Read ahead (EX 3.5, 4.2): the focused row after 300 ms; with the General-mode editor showing, the rows beside it.
  const focusPrefetch = useDelayedPrefetch();
  const readAhead = useQueryClient();
  const following = useEditor(store, (s) => activeTab(s.editors)?.follow === true);
  useEffect(() => {
    const idAt = (i: number) => {
      const row = rows[i];
      return row && forest ? nodeOf(forest, row.key)?.id : undefined;
    };
    const own = idAt(active);
    if (own) focusPrefetch.start(own);
    else focusPrefetch.cancel();
    if (!following) return;
    for (const i of [active - 1, active + 1]) {
      const next = idAt(i);
      if (next) prefetchElement(readAhead, next);
    }
  }, [active, rows, forest, following, focusPrefetch, readAhead]);
  const shown = cache.current.shown;
  // The highlight follows the answer shown, so the rows re-render once per answer, not once per keystroke.
  const shownText = cache.current.answer?.filter.text;
  const query = useMemo(() => (shownText ? parseQuery(shownText) : undefined), [shownText]);

  // The search box's keys (3.1): Enter moves to the first match, Down into the tree, Esc clears and reveals the selection.
  const focusTree = () => scrollRef.current?.focus();
  const onSearchKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      if (!activeKey && rows[0]) setActiveKey(rows[0].key);
      focusTree();
    } else if (e.key === "Enter") {
      e.preventDefault();
      const matched = new Set(answer?.ids ?? []);
      const first = rows.find((r) => {
        const node = forest?.nodes.get(r.key);
        return !!node && ((!!node.id && matched.has(node.id)) || matched.has(r.key));
      });
      if (first) setActiveKey(first.key);
      focusTree();
    } else if (e.key === "Escape" && filter.text) {
      e.preventDefault();
      e.stopPropagation();
      setFilter({ ...filter, text: "" });
      focusTree();
    }
  };
  const onSectionKey = (e: KeyboardEvent<HTMLElement>) => {
    const target = e.target as HTMLElement;
    if (e.key === "/" && !e.ctrlKey && !e.metaKey && !e.altKey && !(target instanceof HTMLInputElement) && !(target instanceof HTMLTextAreaElement)) {
      e.preventDefault();
      searchRef.current?.focus();
    }
  };

  return (
    <section
      aria-label={`${title} explorer`}
      className="flex h-full min-h-0 min-w-0 flex-1 flex-col bg-surface"
      data-testid={`explorer-${id}`}
      onKeyDown={onSectionKey}
      onClickCapture={onSectionClickCapture}
    >
      <ExplorerHeader
        id={id}
        title={title}
        totals={forest?.headers[id] ?? ""}
        pinned={pinned}
        pinnedId={pinnedId}
        highlight={highlight}
        referenceFlat={referenceFlat}
        onCollapseAll={collapseAll}
        onExportSeeds={() => exportSeeds()}
        onImportSeeds={() => setImportingSeeds(true)}
        schemaDatabase={
          id === "databases" && forest
            ? ((selection[0] && forest.byId.get(selection[0])?.kind === "database" ? selection[0] : undefined) ??
              (selection[0] && forest.byId.get(selection[0])?.database) ??
              (activeRow ? databaseOf(forest, activeRow.key) : undefined) ??
              null)
            : null
        }
        onNewSchema={setNewSchemaFor}
        onNew={(kind) => {
          const key = selection[0] && forest ? forest.place.get(selection[0]) : undefined;
          const current = forest && key && forest.nodes.get(key)?.explorer === id ? domainOfKey(forest, key) : null;
          store.getState().requestNew({ kind, domain: startDomain(kind, current) });
        }}
      />
      <FilterBar id={id} filter={filter} rows={indexRows} setFilter={setFilter} inputRef={searchRef} onKeyDown={onSearchKey} />
      {forest && !cache.current.filtering ? <FavoritesStrip forest={forest} id={id} /> : null}
      {pending ? (
        <div className="p-2">
          <Spinner label="Loading the model" />
        </div>
      ) : error ? (
        <EmptyState title="The model index could not be loaded">{error.message}</EmptyState>
      ) : rows.length === 0 ? (
        <div data-testid="explorer-empty" data-filter={cache.current.answer ? cache.current.answer.filter.text : undefined}>
          <EmptyState title={cache.current.filtering ? "Nothing matches the search" : `Nothing in ${title} yet`}>
            {cache.current.filtering || pending || error ? null : <CreateButtons kinds={EXPLORER_CREATE[id]} testid="explorer-create" />}
          </EmptyState>
        </div>
      ) : (
        <div
          ref={scrollRef}
          role="tree"
          aria-label={title}
          aria-multiselectable="true"
          tabIndex={0}
          aria-activedescendant={activeRow ? domIdOf(id, activeRow.key) : undefined}
          onKeyDown={onKeyDown}
          onDragLeave={() => setDropKey(null)}
          className="min-h-0 flex-1 overflow-auto focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent"
          data-testid="explorer-tree"
          data-filter={cache.current.answer ? cache.current.answer.filter.text : undefined}
        >
          <div style={{ height: virtualizer.getTotalSize(), position: "relative" }}>
            {forest
              ? virtualizer.getVirtualItems().map((item) => {
                  const row = rows[item.index];
                  const node = nodeOf(forest, row.key) as TreeNode;
                  const { pos, size } = positionOf(forest, row.key);
                  const elementId = node.home && node.id ? node.id : undefined;
                  return (
                    <TreeRow
                      key={row.key}
                      node={node}
                      domId={domIdOf(id, row.key)}
                      depth={row.depth}
                      pos={pos}
                      size={size}
                      expandable={isExpandable(forest, row.key)}
                      expanded={open.has(row.key)}
                      shown={shown?.get(row.key)}
                      search={query}
                      selected={(!!elementId && selected.has(elementId)) || row.key === explorerItem}
                      active={item.index === active}
                      related={related.has(row.key)}
                      onCanvas={!!elementId && canvasMembers.has(elementId)}
                      onCanvasCount={onCanvasCount.get(row.key) ?? 0}
                      relatedCount={relatedCount.get(row.key) ?? 0}
                      draft={!!elementId && !!drafts[elementId]}
                      presence={elementId ? others.get(elementId) : undefined}
                      peopleHere={peopleHere.get(row.key) ?? 0}
                      favorite={elementId && forest.byId.has(elementId) ? favoriteSet.has(elementId) : undefined}
                      onToggleFavorite={onToggleFavorite}
                      loading={loading.has(row.key)}
                      renaming={renaming === row.key}
                      dropTarget={dropKey === row.key}
                      draggable={!!elementId && isMovable(node.kind) && forest.byId.has(elementId)}
                      rowHeight={item.size}
                      style={{ position: "absolute", top: 0, left: 0, right: 0, height: item.size, transform: `translateY(${item.start}px)` }}
                      onRowClick={onRowClick}
                      onRowDoubleClick={onRowDoubleClick}
                      onToggle={toggle}
                      onContextMenu={onContextMenu}
                      onRename={onRename}
                      onDragStart={onDragStart}
                      onDragOver={onDragOver}
                      onDrop={onDrop}
                    />
                  );
                })
              : null}
          </div>
        </div>
      )}
      {answer ? <AlsoMatches id={id} answer={answer} filter={filter} /> : null}
      <RowMenu
        menu={menu}
        onClose={() => setMenu(null)}
        onRun={(action) => {
          const keys = menu?.keys ?? [];
          setMenu(null);
          run(action, keys);
        }}
      />
      {newSchemaFor ? <NewSchemaDialog database={newSchemaFor} onClose={() => setNewSchemaFor(null)} /> : null}
      {forest ? (
        <>
          <ImportSeedsDialog open={importingSeeds} onOpenChange={setImportingSeeds} />
          {importingCsv ? <ImportCsvDialog open onOpenChange={(o) => !o && setImportingCsv(null)} seed={importingCsv} /> : null}
          <MapToDatabaseDialog
            databases={
              mappingTo
                ? [...forest.byId.values()]
                    .filter((r) => r.kind === "database")
                    .map((r) => ({ id: r.id, name: r.name }))
                    .sort((a, b) => a.name.localeCompare(b.name))
                : null
            }
            count={mappingTo?.length ?? 0}
            onClose={() => setMappingTo(null)}
            onMap={(database) => {
              const ids = mappingTo ?? [];
              setMappingTo(null);
              const rows = [...forest.byId.values()];
              void actions.mapToDatabase(
                database,
                ids.map((x) => ({ id: x, kind: forest.byId.get(x)?.kind ?? "", name: forest.byId.get(x)?.name ?? x })),
                (entity) => rows.find((r) => r.kind === "mapping" && r.database === database.id && r.entity === entity),
                (entity) => forest.byId.get(entity)?.package,
                (pkg) => forest.byId.get(pkg)?.package,
              );
            }}
          />
          {marking ? <MarkDialog forest={forest} request={marking} onClose={() => setMarking(null)} /> : null}
          {promoting ? (
            <PromoteDialog
              id={promoting}
              onClose={() => setPromoting(null)}
              onDone={(entity) => {
                setPromoting(null);
                select([entity]);
              }}
            />
          ) : null}
          <MoveDialog
            forest={forest}
            ids={moving}
            onClose={() => setMoving(null)}
            onMove={(domain) => {
              const ids = moving ?? [];
              setMoving(null);
              void actions.move(ids, kindsOf(ids), domain);
            }}
          />
          <AddRelatedDialog
            names={addingRelated ? addingRelated.map((x) => namesOf([x]).get(x)!) : null}
            diagram={diagramOpen ? (forest.byId.get(diagramOpen)?.name ?? "the diagram") : null}
            onClose={() => setAddingRelated(null)}
            onAdd={(depth) => {
              const ids = addingRelated ?? [];
              setAddingRelated(null);
              void addTo(ids, depth);
            }}
          />
          <DeleteDialog
            names={deleting ? deleting.map((x) => namesOf([x]).get(x)!) : null}
            onClose={() => setDeleting(null)}
            onDelete={() => {
              const ids = deleting ?? [];
              setDeleting(null);
              void actions.remove(ids, namesOf(ids));
            }}
          />
        </>
      ) : null}
    </section>
  );
}

/** Rows whose document children were added (per forest: a rebuilt forest starts over). */
const loadedDocs = new WeakMap<Forest, Set<string>>();

const NOT_IN_DOMAIN_KEY = "@domain-model/not-in-domain";

const domIdOf = (explorer: string, key: string) => `tree-${explorer}-${key.replace(/[^A-Za-z0-9_-]/g, "_")}`;

function ExplorerHeader(props: {
  id: ExplorerId;
  title: string;
  totals: string;
  pinned: boolean;
  pinnedId: ExplorerId | null;
  highlight: boolean;
  referenceFlat: boolean;
  onCollapseAll: () => void;
  onNew: (kind: CreateKind) => void;
  /** The database the New menu's New schema… adds to (a database row or a row inside one), if any. */
  schemaDatabase?: string | null;
  onNewSchema?: (database: string) => void;
  onExportSeeds: () => void;
  onImportSeeds: () => void;
}) {
  const { store } = useServices();
  const others = EXPLORERS.filter((e) => e !== props.id);
  return (
    <header className="flex h-6 shrink-0 items-center gap-2 border-b border-default px-2" data-testid="explorer-header">
      <h2 className="text-11 font-semibold uppercase tracking-wide text-secondary">{props.title}</h2>
      <ContentLocaleChip />
      <span className="min-w-0 flex-1 truncate text-11 text-secondary" data-testid="explorer-totals">
        {props.totals}
      </span>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon" aria-label={`New in ${props.title}`} title="New…" data-testid="explorer-new">
            <Plus />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          {EXPLORER_CREATE[props.id].map((kind) => (
            <DropdownMenuItem key={kind} onSelect={() => props.onNew(kind)}>
              {CREATE_LABELS[kind]}
            </DropdownMenuItem>
          ))}
          {props.schemaDatabase ? (
            <DropdownMenuItem onSelect={() => props.onNewSchema?.(props.schemaDatabase!)} data-testid="explorer-new-schema">
              New schema…
            </DropdownMenuItem>
          ) : null}
        </DropdownMenuContent>
      </DropdownMenu>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon" aria-label={`${props.title} actions`} data-testid="explorer-header-menu">
            <MoreHorizontal />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem onSelect={props.onCollapseAll}>Collapse all</DropdownMenuItem>
          <DropdownMenuSeparator />
          {props.pinned || props.pinnedId ? (
            <DropdownMenuItem onSelect={() => store.getState().pinExplorer(null)}>Unpin the second explorer</DropdownMenuItem>
          ) : (
            <>
              <DropdownMenuLabel>Pin beside…</DropdownMenuLabel>
              {others.map((e) => (
                <DropdownMenuItem key={e} onSelect={() => store.getState().pinExplorer(e)}>
                  {EXPLORER_LABELS[e]}
                </DropdownMenuItem>
              ))}
            </>
          )}
          <DropdownMenuSeparator />
          <DropdownMenuCheckboxItem checked={props.highlight} onCheckedChange={(on) => store.getState().setHighlightRelated(!!on)}>
            Highlight related elements
          </DropdownMenuCheckboxItem>
          {props.id === "reference-data" ? (
            <DropdownMenuCheckboxItem
              checked={props.referenceFlat}
              onCheckedChange={(on) => store.getState().setReferenceFlat(!!on)}
              data-testid="reference-flat"
            >
              Types A to Z (no categories)
            </DropdownMenuCheckboxItem>
          ) : null}
          {props.id === "reference-data" ? (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={props.onExportSeeds} data-testid="export-all-seeds">
                Export all seed data
              </DropdownMenuItem>
              <DropdownMenuItem onSelect={props.onImportSeeds} data-testid="import-seeds">
                Import seed data…
              </DropdownMenuItem>
            </>
          ) : null}
        </DropdownMenuContent>
      </DropdownMenu>
      {props.pinned ? null : <PanelToggle panel="explorer" />}
    </header>
  );
}

/**
 * Below the filtered tree (3.1): the matches in the other explorers and in Settings, each a button that goes there
 * with the same search; and a line while table names are still loading (the filter re-runs when they land).
 */
function AlsoMatches({ id, answer, filter }: { id: ExplorerId; answer: FilterResult; filter: ExplorerFilter }) {
  const { store } = useServices();
  const { openSettings } = useEditorNavigation();
  const others = SEARCH_PLACES.filter((p) => p !== id && answer.counts[p] > 0);
  if (!others.length && !answer.tablesLoading) return null;
  const go = (place: SearchPlace) => {
    if (place === "settings") {
      openSettings("tags");
      return;
    }
    store.getState().setExplorerFilter(place, { ...filter });
    store.getState().setSidebar(place);
  };
  return (
    <div className="flex flex-col gap-1 border-t border-default p-2 text-12 text-secondary" data-testid="explorer-also" aria-label="Also matches">
      {others.length ? (
        <div className="flex flex-wrap items-center gap-1">
          <span>Also</span>
          {others.map((p) => (
            <button
              key={p}
              type="button"
              className="rounded-control px-1 text-accent underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-accent"
              onClick={() => go(p)}
              data-testid={`explorer-also-${p}`}
            >
              {answer.counts[p].toLocaleString("en-US")} in {p === "settings" ? "Settings" : EXPLORER_LABELS[p]}
            </button>
          ))}
        </div>
      ) : null}
      {answer.tablesLoading ? <span data-testid="explorer-tables-loading">Table names still loading</span> : null}
    </div>
  );
}
