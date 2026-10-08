// The Database workspace (phase2-design.md 4.8): a database picker, the resolved tables of
// GET /api/databases/{id}/view as TableNodes and ForeignKeyEdges (auto-laid out, positions per
// browser), the dialect selector (edits the database element) and the live DDL preview
// (POST /api/templates/preview with the unit ddlPreview.ts picks: an enabled pack's database unit, or its each-table
// unit for a selected table, the each-<kind> unit of a picked view, sequence, routine, database type or SQL object; a picked
// query shows its own SQL), refreshed 400 ms after any model.changed. The list beside the canvas shows the tables, views,
// sequences, routines, queries, database types or SQL objects (a kind chip picks which), and the toolbar's New menu creates any
// of them, or a schema, in the database.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Background,
  ConnectionMode,
  Controls,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  useNodesInitialized,
  useReactFlow,
  type NodeChange,
  type Viewport,
} from "@xyflow/react";
import { Download, LayoutGrid, Plus, Table2 } from "lucide-react";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/menu";
import { exportCanvas } from "@/canvas/export";
import { useDatabaseTables, useDatabaseView, useIndex, usePacks, usePreview, useProject, useQuerySql } from "@/api/queries";
import type { DatabaseDoc } from "@/api/types";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { tableKeyOf } from "@/search/engine";
import { Toolbar, EmptyState, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { Input, Select } from "@/components/ui/input";
import { CANVAS_CAP, databaseList, filterTables, scopeTables } from "./tableList";
import { CodeView } from "@/code";
import { TableNode, type TableFlowNode } from "@/canvas/TableNode";
import { ForeignKeyEdge, type ForeignKeyFlowEdge } from "@/canvas/ForeignKeyEdge";
import { MarkerDefs } from "@/canvas/markers";
import { defaultLayoutEngine } from "@/canvas/layout";
import { loadPositions, loadViewport, measuredSizes, savePositions, saveViewport, type StoredPositions } from "@/lib/positions";
import { placeNodes, roundViewport } from "@/canvas/placement";
import { useDraftDocument } from "@/inspector/useDraft";
import { DIALECTS } from "@/inspector/fields";
import { EdgeToggle, PanelToggle } from "@/app/panels";
import { Splitter } from "@/components/ui/splitter";
import { LIMITS } from "@/state/layout";
import { ColumnPanel } from "./ColumnGrid";
import { StoreTablesBanner } from "./StoreTablesBanner";
import { shownTable } from "./storeTables";
import { databaseTargetOf, ddlPreviewCaption, ddlPreviewTarget, emptyObjectUnitNote, NO_DDL_UNIT, type DdlObject } from "./ddlPreview";
import { DATABASE_CREATE, DATABASE_CREATE_LABELS } from "@/explorer/databaseCreate";
// The diagram designer (foreign keys by dragging, a picked key shown in the inspector, Delete, New table on the canvas, the
// Display menu).
import { edgeHandles, shownColumns } from "@/canvas/tableColumns";
import { fkEnds } from "./fkEdits";
import { fkEdgeId, fkSummary, useDiagramDesigner } from "./DiagramDesigner";
import { filterObjects, LIST_KINDS, OBJECT_LIST_MEMBERS, type ListKind, type ObjectListKind } from "./tableList";
import { countOf, KIND_LABELS } from "@/model/labels";
import * as endpoints from "@/api/endpoints";
import type { PreviewResult } from "@/api/types";
import { useAskedPreview } from "@/workspaces/generate/widePreview";
import { onListArrowKeys } from "@/lib/listKeys";

const nodeTypes = { table: TableNode };
const edgeTypes = { foreignKey: ForeignKeyEdge };

export function DatabaseWorkspace() {
  return (
    <ReactFlowProvider>
      <DatabaseCanvas />
    </ReactFlowProvider>
  );
}

function DatabaseCanvas() {
  const { store, drafts } = useServices();
  const project = useProject();
  const activeDatabase = useEditor(store, (s) => s.activeDatabase);
  // The tables list and the DDL preview hide and show like the shell's panels (header button, Alt+Shift+L and D, a slim
  // edge to bring them back), kept in the saved layout.
  const tablesCollapsed = useEditor(store, (s) => s.tablesCollapsed);
  const ddlCollapsed = useEditor(store, (s) => s.ddlCollapsed);
  const tablesSize = useEditor(store, (s) => s.tablesSize);
  const ddlSize = useEditor(store, (s) => s.ddlSize);
  const { openDatabase, select } = useEditorNavigation();
  const flow = useReactFlow<TableFlowNode, ForeignKeyFlowEdge>();
  const initialized = useNodesInitialized();
  const index = useIndex();
  // The index's rows: a database created a moment ago shows at once, before the project is read again.
  const databases = useMemo(() => databaseList(project.data?.databases, index.data), [project.data, index.data]);
  const rootRef = useRef<HTMLDivElement>(null);
  const databaseName = databases.find((d) => d.id === activeDatabase)?.name ?? "database";

  useEffect(() => {
    if (!activeDatabase && databases.length) openDatabase(databases[0].id);
  }, [activeDatabase, databases, openDatabase]);

  const view = useDatabaseView(activeDatabase);
  const database = useDraftDocument(activeDatabase);
  // The focused table lives in the store: the explorer's Open sets it before this screen mounts (1.3).
  const selectedTable = useEditor(store, (s) => s.databaseTable);
  const setSelectedTable = useCallback((key: string | null) => store.getState().setDatabaseTable(key), [store]);
  const shownDatabase = useRef(activeDatabase);
  const [positions, setPositions] = useState<StoredPositions>({});
  const [dragging, setDragging] = useState<StoredPositions>({});
  const [measured, setMeasured] = useState<Record<string, { width: number; height: number }>>({});
  const laidOut = useRef<string | null>(null);
  // The canvas's own moves (fit, restore, centring a table) are not saved as the user's pan and zoom.
  const viewportFor = useRef<string | null>(null);
  const programmaticUntil = useRef(0);
  const programmatic = useCallback(() => {
    programmaticUntil.current = performance.now() + 400;
  }, []);

  useEffect(() => {
    if (activeDatabase) setPositions(loadPositions(`db.${activeDatabase}`));
    // Another database: its own tables, none focused, unless the table to focus is one of them (opened from the explorer).
    if (shownDatabase.current && shownDatabase.current !== activeDatabase) {
      const inspected = store.getState().inspectedTable;
      if (!inspected || inspected.database !== activeDatabase) {
        setSelectedTable(null);
        if (inspected) store.getState().inspectTable(null, null);
      }
    }
    shownDatabase.current = activeDatabase;
  }, [activeDatabase, setSelectedTable, store]);

  const allTables = useMemo(() => view.data?.view?.tables ?? [], [view.data]);
  const canvasRef = useRef<HTMLDivElement>(null);
  const designer = useDiagramDesigner({
    databaseId: activeDatabase,
    databaseName: databases.find((d) => d.id === activeDatabase)?.name ?? null,
    allTables,
    flow,
    canvasRef,
    selectedTable,
  });
  const { display, onLink, selectedFk, takePlaced } = designer;
  // Above 300 tables the canvas shows the selected table and its foreign-key neighbours, else the list-and-DDL form.
  const scope = useMemo(() => scopeTables(allTables, selectedTable), [allTables, selectedTable]);
  // Auto-layout runs once per database, and once per focus while the canvas is scoped.
  const layoutKey = scope.mode === "scoped" ? `${activeDatabase}:${scope.focus}` : activeDatabase;
  const tables = useMemo(
    () => (scope.mode === "all" ? allTables : scope.mode === "scoped" ? allTables.filter((t) => scope.keys.has(t.key)) : []),
    [allTables, scope],
  );
  const nodes: TableFlowNode[] = useMemo(
    () =>
      tables.map((table, i) => ({
        id: table.key,
        type: "table" as const,
        position: dragging[table.key] ?? positions[table.key] ?? { x: (i % 4) * 300, y: Math.floor(i / 4) * 260 },
        measured: measured[table.key],
        selected: selectedTable === table.key,
        ariaLabel: `Table ${table.name}`,
        data: { table, display: display.mode, onLink },
      })),
    [tables, positions, dragging, measured, selectedTable, display.mode, onLink],
  );
  // Each key joins the facing sides of its two cards, at its first column's row when the card shows it (tableColumns.ts).
  const edges: ForeignKeyFlowEdge[] = useMemo(() => {
    const shown = new Map(tables.map((t) => [t.key, new Set(shownColumns(t, display.mode).map((c) => c.key))]));
    // Every card is as wide, so their left edges compare as their centres do.
    const center = (key: string) => (dragging[key] ?? positions[key])?.x ?? 0;
    return tables.flatMap((t) =>
      t.foreignKeys.flatMap((fk) => {
        const referenced = tables.find((x) => x.key === fk.referencedTable);
        if (!referenced) return [];
        const id = fkEdgeId(t.key, fk);
        return [
          {
            id,
            type: "foreignKey" as const,
            source: t.key,
            target: fk.referencedTable,
            ...edgeHandles(
              fk,
              { source: shown.get(t.key)!, target: shown.get(referenced.key)! },
              { source: center(t.key), target: center(referenced.key) },
              t.key === referenced.key,
            ),
            selected: selectedFk === id,
            ariaLabel: `Foreign key ${fk.name}`,
            data: { foreignKey: fk, ends: fkEnds(t, fk), summary: fkSummary(t, fk, referenced) },
          },
        ];
      }),
    );
  }, [tables, display.mode, positions, dragging, selectedFk]);

  // Mouse or keyboard (Tab to a table, Enter): the canvas is controlled, so a pick arrives as a
  // `select` change. The DDL preview follows the table, and so does the inspector: the TABLE, by its key (a file or not),
  // never its entity (the entity shows in the Domain model).
  const pickTable = useCallback(
    (key: string) => {
      setSelectedTable(key);
      if (!activeDatabase) return;
      // The explorer row follows the pick; a table already inspected keeps its row (a column of it picked in the explorer).
      const shown = store.getState().inspectedTable;
      const same = shown?.database === activeDatabase && shown.key === key;
      store.getState().inspectTable({ database: activeDatabase, key }, same ? undefined : tableKeyOf(activeDatabase, key));
    },
    [activeDatabase, store, setSelectedTable],
  );

  // The drawing is settled once its first layout (or its kept viewport) is in place and fitted, a frame later: the canvas says
  // so (data-layout="settled"), so what measures the cards (a test's drag) waits for it.
  const [settledFor, setSettledFor] = useState<string | null>(null);
  const settle = useCallback((key: string | null) => requestAnimationFrame(() => setSettledFor(key)), []);
  const layout = useCallback(async () => {
    if (!activeDatabase) return;
    const result = await defaultLayoutEngine().layout(
      flow.getNodes().map((n) => ({ id: n.id, width: n.measured?.width ?? 256, height: n.measured?.height ?? 200 })),
      edges.map((e) => ({ id: e.id, source: e.source, target: e.target })),
    );
    const next = { ...loadPositions(`db.${activeDatabase}`), ...Object.fromEntries(result) };
    savePositions(`db.${activeDatabase}`, next);
    setPositions(next);
    viewportFor.current = layoutKey;
    requestAnimationFrame(() => {
      programmatic();
      void flow.fitView({ padding: 0.1 }).then(() => settle(layoutKey));
    });
  }, [activeDatabase, edges, flow, layoutKey, programmatic, settle]);

  // Every table unplaced (a database seen for the first time): the full automatic layout, then fit. Some unplaced (a
  // table just added): only those are placed, beside a table they share a foreign key with or under the drawing.
  const missing = useMemo(() => tables.filter((t) => !positions[t.key]).map((t) => t.key), [tables, positions]);
  const unplaced = missing.length > 0 && missing.length === tables.length;
  useEffect(() => {
    if (initialized && unplaced && nodes.length && laidOut.current !== layoutKey) {
      laidOut.current = layoutKey;
      void layout();
    }
  }, [initialized, unplaced, nodes.length, layoutKey, layout]);
  useEffect(() => {
    if (!initialized || !activeDatabase || !missing.length || unplaced) return;
    const size = (id: string) => {
      const m = flow.getInternalNode(id)?.measured;
      return { width: m?.width || 256, height: m?.height || 200 };
    };
    const set = new Set(missing);
    // A table made by a double click on the canvas lands where the click was.
    const clicked = takePlaced(missing);
    const placed = nodes.filter((n) => !set.has(n.id)).map((n) => ({ id: n.id, ...n.position, ...size(n.id) }));
    const rest = missing.filter((id) => !clicked[id]);
    const added = rest.length ? placeNodes({ placed, unplaced: rest.map((id) => ({ id, ...size(id) })), edges }) : {};
    const next = { ...loadPositions(`db.${activeDatabase}`), ...added, ...clicked };
    savePositions(`db.${activeDatabase}`, next);
    setPositions(next);
  }, [initialized, activeDatabase, missing, unplaced, nodes, edges, flow, takePlaced]);

  // Pan and zoom are kept per browser beside the positions; opening restores them and fits only when none is kept.
  useEffect(() => {
    if (!initialized || !activeDatabase || !nodes.length || unplaced || viewportFor.current === layoutKey) return;
    viewportFor.current = layoutKey;
    const kept = loadViewport(`db.${activeDatabase}`);
    programmatic();
    void (kept ? flow.setViewport(kept, { duration: 0 }) : flow.fitView({ padding: 0.1, duration: 0 })).then(() => settle(layoutKey));
  }, [initialized, activeDatabase, nodes.length, unplaced, layoutKey, flow, programmatic, settle]);
  const moveTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(moveTimer.current), []);
  const onMoveEnd = useCallback(
    (_event: unknown, viewport: Viewport) => {
      if (!activeDatabase || performance.now() < programmaticUntil.current) return;
      clearTimeout(moveTimer.current);
      const key = `db.${activeDatabase}`;
      moveTimer.current = setTimeout(() => saveViewport(key, roundViewport(viewport)), 300);
    },
    [activeDatabase],
  );

  const onNodesChange = useCallback(
    (changes: NodeChange<TableFlowNode>[]) => {
      const sizes = measuredSizes(changes);
      if (sizes) setMeasured((prev) => ({ ...prev, ...sizes }));
      const picked = changes.find((c) => c.type === "select" && c.selected);
      if (picked && picked.type === "select") pickTable(picked.id);
      for (const change of changes) {
        if (change.type !== "position") continue;
        if (change.position) {
          const p = change.position;
          setDragging((d) => ({ ...d, [change.id]: p }));
        }
        if (change.dragging === false) {
          setDragging((d) => {
            const final = d[change.id];
            const rest = { ...d };
            delete rest[change.id];
            if (final && activeDatabase) {
              const next = { ...loadPositions(`db.${activeDatabase}`), [change.id]: final };
              savePositions(`db.${activeDatabase}`, next);
              setPositions(next);
            }
            return rest;
          });
        }
      }
    },
    [activeDatabase, pickTable],
  );

  // A table opened from the explorer or the table list: selected, its DDL shown and the canvas centred on it.
  const centred = useRef<string | null>(null);
  const focusTable = useCallback(
    (key: string) => {
      pickTable(key);
      centred.current = key;
      requestAnimationFrame(() => {
        programmatic();
        void flow.fitView({ nodes: [{ id: key }], padding: 0.4, maxZoom: 1.2 });
      });
    },
    [pickTable, flow, programmatic],
  );
  useEffect(() => {
    if (!selectedTable || centred.current === selectedTable || !initialized || !tables.some((t) => t.key === selectedTable)) return;
    focusTable(selectedTable);
  }, [selectedTable, tables, initialized, focusTable]);

  // The table section (1.3): the table summaries (E5c) with a filter, capped so a 10,000-table database stays quick; the
  // kind chips switch it to the database's views, sequences, routines, database types or SQL objects (from the resolved view).
  const summaries = useDatabaseTables(activeDatabase);
  const [tableFilter, setTableFilter] = useState("");
  const [listKind, setListKind] = useState<ListKind>("table");
  const listed = useMemo(() => filterTables(summaries.data?.tables ?? [], tableFilter), [summaries.data, tableFilter]);
  // The objects of each kind besides tables, as the resolved view lists them (an older server may leave the newer lists out).
  const objects = useMemo(() => {
    const resolved = view.data?.view;
    const out = {} as Record<ObjectListKind, { id: string; name: string; schema: string | null }[]>;
    for (const kind of Object.keys(OBJECT_LIST_MEMBERS) as ObjectListKind[])
      // A query has no schema.
      out[kind] = (resolved?.[OBJECT_LIST_MEMBERS[kind]] ?? []).map((o) => ({ id: o.id, name: o.name, schema: "schema" in o ? o.schema : null }));
    return out;
  }, [view.data]);
  const listedObjects = useMemo(() => (listKind === "table" ? null : filterObjects(objects[listKind], tableFilter)), [listKind, objects, tableFilter]);
  // An object picked here or in the Databases explorer: the DDL preview renders it.
  const pickedId = useEditor(store, (s) => s.selectionBy.databases?.[0] ?? null);
  const picked: (DdlObject & { name: string }) | null = useMemo(() => {
    for (const kind of Object.keys(objects) as ObjectListKind[]) {
      const o = objects[kind].find((x) => x.id === pickedId);
      if (o) return { kind, id: o.id, name: o.name };
    }
    return null;
  }, [objects, pickedId]);
  const pickObject = (kind: ObjectListKind, id: string) => {
    setSelectedTable(null);
    store.getState().inspectTable(null, null);
    select([id], null, "databases");
    setListKind(kind);
  };

  const packs = usePacks();
  const table = tables.find((t) => t.key === selectedTable) ?? null;
  const target = ddlPreviewTarget(packs.data ?? [], activeDatabase ?? null, table?.key ?? null, picked);
  const querySql = useQuerySql(target?.scope === "query" ? target.elementId : null, null);
  // One object or table renders as it is picked; the whole database (nothing picked, or a picked object whose unit writes
  // nothing for it) renders every table, so it renders only when asked (generation-ui.md 5.2, "Bounds"), and after a
  // model change it renders again by itself only when that render was fast.
  const preview = usePreview(
    target?.pack ?? "",
    target?.unit ?? "",
    target?.elementId ?? null,
    !!target && target.scope !== "query" && target.scope !== "database" && !!view.data?.view,
  );
  // An object's unit that writes nothing for the pick (a pack parameter turns it off): the whole database instead.
  const objectEmpty =
    !!picked && target?.scope === picked.kind && !preview.isPlaceholderData && !!preview.data && !preview.data.files.length && !preview.data.diagnostics.length;
  const whole = objectEmpty ? databaseTargetOf(packs.data ?? [], activeDatabase ?? null) : null;
  const wholeTarget = target?.scope === "database" ? target : whole;
  // The model as the whole preview sees it: a new database view (structurally shared, so a refetch of the same view is
  // not a change) counts as a change.
  const viewStamp = useRef<{ of: unknown; n: number }>({ of: undefined, n: 0 });
  if (viewStamp.current.of !== view.data) viewStamp.current = { of: view.data, n: viewStamp.current.n + 1 };
  const wholePreview = useAskedPreview<PreviewResult>(
    wholeTarget && view.data?.view ? JSON.stringify({ pack: wholeTarget.pack, unit: wholeTarget.unit, elementId: wholeTarget.elementId }) : null,
    String(viewStamp.current.n),
    async (signal) => {
      const value = await endpoints.previewTemplate({ pack: wholeTarget!.pack, unit: wholeTarget!.unit, elementId: wholeTarget!.elementId }, signal);
      return { value, elapsedMs: value.elapsedMs };
    },
  );
  const shownPreview = wholeTarget ? { data: wholePreview.result ?? undefined, isFetching: wholePreview.pending } : preview;
  const dialect = (database.json as DatabaseDoc | undefined)?.dialect;

  if (project.isPending) return <EmptyState title="Loading the project…" />;
  if (!databases.length) return <EmptyState title="No databases">Add a database element to the model to design its tables.</EmptyState>;

  return (
    <div ref={rootRef} className="flex h-full min-h-0 flex-col" data-testid="database-workspace">
      <Toolbar label="Database">
        <label htmlFor="database-picker" className="sr-only">
          Database
        </label>
        <Select id="database-picker" className="w-48" value={activeDatabase ?? ""} onChange={(e) => openDatabase(e.target.value)}>
          {databases.map((d) => (
            <option key={d.id} value={d.id}>
              {d.name}
            </option>
          ))}
        </Select>
        <label htmlFor="dialect-picker" className="text-12 text-secondary">
          Dialect
        </label>
        <Select
          id="dialect-picker"
          className="w-36"
          value={dialect ?? ""}
          disabled={!database.json}
          data-testid="dialect-picker"
          onChange={(e) => {
            if (!activeDatabase) return;
            drafts.edit(activeDatabase, (json) => void ((json as unknown as DatabaseDoc).dialect = e.target.value as DatabaseDoc["dialect"]), {
              base: database.element.data,
            });
            void drafts.flush(activeDatabase);
          }}
        >
          {DIALECTS.map((d) => (
            <option key={d} value={d}>
              {d}
            </option>
          ))}
        </Select>
        <Button size="sm" onClick={() => void layout()}>
          <LayoutGrid /> Auto-layout
        </Button>
        <Button
          size="sm"
          variant="ghost"
          disabled={!activeDatabase}
          onClick={() => designer.newTable(null)}
          title="Create a table in this database (or double-click the empty canvas to place it there)"
          data-testid="database-canvas-new-table"
        >
          <Table2 /> New table
        </Button>
        {designer.displayMenu}
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button size="sm" variant="ghost" data-testid="database-export-menu">
              <Download /> Export
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start">
            <DropdownMenuItem onSelect={() => void exportCanvas("svg", flow.getNodes(), `database-${databaseName}`, rootRef.current)}>SVG</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => void exportCanvas("png", flow.getNodes(), `database-${databaseName}`, rootRef.current)}>PNG</DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              size="sm"
              variant="ghost"
              disabled={!activeDatabase}
              title="Create a schema, table, view, sequence, routine, query, database type or SQL object in this database"
              data-testid="database-new-menu"
            >
              <Plus /> New
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start">
            {DATABASE_CREATE.map((kind) => (
              <DropdownMenuItem
                key={kind}
                onSelect={() => activeDatabase && store.getState().requestNewDatabaseObject({ kind, database: activeDatabase })}
                data-testid={`database-new-${kind}`}
              >
                {DATABASE_CREATE_LABELS[kind]}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
        <span className="ml-auto text-12 text-secondary" data-testid="database-canvas-count">
          {scope.mode === "all"
            ? `${tables.length} tables`
            : scope.mode === "scoped"
              ? `${tables.length} of ${allTables.length} tables: ${allTables.find((t) => t.key === scope.focus)?.name ?? "the table"} and its neighbours${scope.more ? ` (${scope.more} more not drawn)` : ""}`
              : `${allTables.length} tables: pick one to draw it with its neighbours`}
        </span>
      </Toolbar>
      <div className="flex min-h-0 flex-1">
        {tablesCollapsed ? <EdgeToggle panel="tables" side="left" /> : null}
        {!tablesCollapsed && (
          <section
            id="mq-database-tables"
            className="flex max-w-[40%] shrink-0 flex-col bg-surface"
            style={{ width: tablesSize }}
            aria-label="Tables"
            data-testid="database-tables"
          >
            <div className="flex h-6 shrink-0 items-center gap-2 border-b border-default px-2" data-testid="tables-panel-header">
              <span className="min-w-0 flex-1 truncate text-11 font-semibold uppercase tracking-wide text-secondary">Tables</span>
              <PanelToggle panel="tables" />
            </div>
            <div className="flex flex-col gap-1 border-b border-default p-2">
              <div role="group" aria-label="Show" className="flex flex-wrap gap-1" data-testid="database-list-kinds">
                {LIST_KINDS.map((k) => {
                  const count = k.kind === "table" ? listed.total : objects[k.kind].length;
                  return (
                    <button
                      key={k.kind}
                      type="button"
                      aria-pressed={listKind === k.kind}
                      className={`h-5 rounded-control border px-1.5 text-11 ${listKind === k.kind ? "border-accent bg-accent-subtle font-medium text-primary" : "border-default text-secondary hover:text-primary"}`}
                      onClick={() => setListKind(k.kind)}
                      data-testid={`database-list-kind-${k.kind}`}
                    >
                      {k.label} {k.kind === "table" && tableFilter ? "" : count}
                    </button>
                  );
                })}
              </div>
              <Input
                type="search"
                aria-label={`Filter ${LIST_KINDS.find((k) => k.kind === listKind)!.label.toLowerCase()}`}
                placeholder={`Filter ${LIST_KINDS.find((k) => k.kind === listKind)!.label.toLowerCase()}`}
                value={tableFilter}
                onChange={(e) => setTableFilter(e.target.value)}
              />
            </div>
            {listedObjects ? (
              <>
                <ul className="min-h-0 flex-1 overflow-auto py-1 text-12" aria-label={`${KIND_LABELS[listKind]} list`} onKeyDown={onListArrowKeys}>
                  {listedObjects.items.map((o) => (
                    <li key={o.id}>
                      <button
                        type="button"
                        className={`flex w-full items-baseline gap-2 px-2 py-0.5 text-left hover:bg-accent-subtle ${picked?.id === o.id ? "bg-accent-subtle font-medium" : ""}`}
                        aria-current={picked?.id === o.id ? "true" : undefined}
                        onClick={() => pickObject(listKind as ObjectListKind, o.id)}
                        data-testid={`database-${listKind}-${o.name}`}
                      >
                        <span className="truncate">{o.schema ? `${o.schema}.${o.name}` : o.name}</span>
                      </button>
                    </li>
                  ))}
                </ul>
                <p className="border-t border-default px-2 py-1 text-11 text-secondary" data-testid="database-objects-count">
                  {`${countOf(listedObjects.total, listKind)}${tableFilter ? " match" : ""}`}
                </p>
              </>
            ) : null}
            <ul className={listedObjects ? "hidden" : "min-h-0 flex-1 overflow-auto py-1 text-12"} aria-label="Table list" onKeyDown={onListArrowKeys}>
              {listed.tables.map((t) => (
                <li key={t.key}>
                  <button
                    type="button"
                    className={`flex w-full items-baseline gap-2 px-2 py-0.5 text-left hover:bg-accent-subtle ${selectedTable === t.key ? "bg-accent-subtle font-medium" : ""}`}
                    aria-current={selectedTable === t.key ? "true" : undefined}
                    onClick={() => focusTable(t.key)}
                    data-testid={`database-table-${t.name}`}
                  >
                    <span className="truncate">{t.schema ? `${t.schema}.${t.name}` : t.name}</span>
                    <span className="ml-auto shrink-0 text-11 text-secondary">{t.columnCount}</span>
                  </button>
                </li>
              ))}
            </ul>
            <p className={listedObjects ? "hidden" : "border-t border-default px-2 py-1 text-11 text-secondary"} data-testid="database-tables-count">
              {listed.more > 0
                ? `${listed.tables.length} of ${listed.total} shown; refine the filter`
                : `${listed.total} ${listed.total === 1 ? "table" : "tables"}${tableFilter ? " match" : ""}`}
            </p>
          </section>
        )}
        {!tablesCollapsed && (
          <Splitter
            orientation="vertical"
            value={tablesSize}
            {...LIMITS.tables}
            direction={1}
            label="Resize the tables list"
            controls="mq-database-tables"
            onChange={(v) => store.setState({ tablesSize: v })}
          />
        )}
        <div className="flex min-w-0 flex-1 flex-col">
          {view.data?.stale ? (
            <div role="alert" className="border-b border-default bg-surface px-2 py-1 text-12 text-danger" data-testid="database-stale">
              The model has errors; the tables show as last resolved until they are fixed:{" "}
              {view.data.diagnostics
                .filter((d) => d.severity === "error")
                .slice(0, 3)
                .map((d) => `${d.rule} ${d.message}`)
                .join(" · ")}
            </div>
          ) : null}
          <div
            ref={canvasRef}
            tabIndex={-1}
            className="relative min-h-0 flex-1 outline-none"
            role="region"
            aria-label="Table diagram"
            aria-keyshortcuts="Delete"
            onKeyDown={designer.onKeyDown}
            onDoubleClick={designer.onDoubleClick}
            data-testid="database-canvas"
            data-layout={settledFor !== null && settledFor === layoutKey ? "settled" : "pending"}
          >
            <StoreTablesBanner database={activeDatabase} tables={view.data?.view?.tables} />
            <MarkerDefs />
            {view.isPending ? <Spinner label="Resolving tables" /> : null}
            {scope.mode === "list" ? (
              <div data-testid="database-list-form" className="h-full">
                <EmptyState title={`${scope.total} tables are too many to draw at once`}>
                  Pick a table in the list to draw it with the tables its foreign keys connect it to (at most {CANVAS_CAP}). The DDL preview shows the whole
                  database.
                </EmptyState>
              </div>
            ) : view.data && !view.data.view ? (
              <EmptyState title="The model has errors, so its tables cannot be resolved">
                {view.data.diagnostics
                  .slice(0, 3)
                  .map((d) => `${d.rule} ${d.message}`)
                  .join(" · ")}
              </EmptyState>
            ) : (
              <ReactFlow<TableFlowNode, ForeignKeyFlowEdge>
                nodes={nodes}
                edges={edges}
                nodeTypes={nodeTypes}
                edgeTypes={edgeTypes}
                onNodesChange={onNodesChange}
                onPaneClick={() => {
                  designer.selectFk(null);
                  if (scope.mode !== "all") return;
                  setSelectedTable(null);
                  store.getState().inspectTable(null, null);
                }}
                onEdgeClick={(_, edge) => {
                  designer.selectFk(edge.id);
                  canvasRef.current?.focus({ preventScroll: true });
                }}
                onEdgeDoubleClick={(_, edge) => designer.editFk(edge.id)}
                onConnectStart={designer.onConnectStart}
                onConnectEnd={designer.onConnectEnd}
                connectionMode={ConnectionMode.Loose}
                onMoveEnd={onMoveEnd}
                minZoom={0.1}
                nodesConnectable
                // A drag to another table never pans the drawing under the pointer.
                autoPanOnConnect={false}
                zoomOnDoubleClick={false}
                deleteKeyCode={null}
                // A key's edge may name a row's handle a moment before the card has measured it (a Display change).
                onError={(code, message) => code !== "008" && console.warn(message)}
                proOptions={{ hideAttribution: false }}
              >
                <Background gap={16} />
                <Controls showInteractive={false} />
                {display.minimap ? <MiniMap pannable zoomable ariaLabel="Minimap" /> : null}
              </ReactFlow>
            )}
            {designer.overlays}
          </div>
          {/* The selected table's columns, editable (a table not stored as a file yet is stored on its first edit). */}
          <ColumnPanel table={shownTable(allTables, selectedTable)} databaseId={activeDatabase} />
        </div>
        {ddlCollapsed ? <EdgeToggle panel="ddl" side="right" /> : null}
        {!ddlCollapsed && (
          <Splitter
            orientation="vertical"
            value={ddlSize}
            {...LIMITS.ddl}
            direction={-1}
            label="Resize the DDL preview"
            controls="mq-ddl-preview"
            onChange={(v) => store.setState({ ddlSize: v })}
          />
        )}
        {!ddlCollapsed && (
          <aside
            id="mq-ddl-preview"
            className="flex max-w-[60%] shrink-0 flex-col bg-surface"
            style={{ width: ddlSize }}
            aria-label="DDL preview"
            data-testid="ddl-preview"
          >
            <div className="flex h-6 shrink-0 items-center gap-2 border-b border-default px-2 text-12" data-testid="ddl-panel-header">
              <span className="font-semibold">{target?.scope === "query" ? "SQL preview" : "DDL preview"}</span>
              <span className="min-w-0 flex-1 truncate text-secondary" data-testid="ddl-preview-unit" title="The pack and unit this preview renders">
                {target ? ddlPreviewCaption(target, picked?.name ?? table?.name ?? null) : "no unit"}
              </span>
              {(target?.scope === "query" ? querySql : shownPreview).isFetching ? <Spinner label="Rendering" /> : null}
              <PanelToggle panel="ddl" />
            </div>
            <div className="min-h-0 flex-1">
              {packs.isPending ? (
                <Spinner label="Reading the packs" />
              ) : !target ? (
                <EmptyState title="Nothing to preview">
                  <span data-testid="ddl-preview-none">{NO_DDL_UNIT}</span>
                </EmptyState>
              ) : target.scope === "query" ? (
                querySql.data?.preview ? (
                  <CodeView
                    language="sql"
                    readOnly
                    label="Query SQL"
                    value={[querySql.data.preview.sql, ...querySql.data.preview.collections.map((c) => `-- collection ${c.name}\n${c.sql}`)].join("\n\n")}
                  />
                ) : querySql.data ? (
                  <ul className="p-2 text-12 text-danger" data-testid="query-preview-problems">
                    {querySql.data.diagnostics
                      .filter((d) => d.severity === "error")
                      .slice(0, 5)
                      .map((d, i) => (
                        <li key={i}>
                          {d.rule} {d.message}
                        </li>
                      ))}
                  </ul>
                ) : (
                  <Spinner label="Rendering the query" />
                )
              ) : wholeTarget && !wholePreview.asked ? (
                <div className="flex flex-col items-start gap-2 p-2 text-12" data-testid="ddl-preview-whole-note">
                  {whole && target && picked ? <p className="text-secondary">{emptyObjectUnitNote(target, picked.name)}</p> : null}
                  <p className="text-secondary">
                    The whole database renders every table, view and object of it in one go, so it is not rendered on its own. Pick a table or an object to see
                    its DDL, or preview the whole database.
                  </p>
                  <Button size="sm" variant="primary" onClick={() => wholePreview.run()} data-testid="ddl-preview-whole">
                    Preview the whole database
                  </Button>
                </div>
              ) : wholeTarget && wholePreview.error ? (
                <p role="alert" className="p-2 text-12 text-danger">
                  {wholePreview.error}
                </p>
              ) : shownPreview.data?.diagnostics.length && !shownPreview.data.files.length ? (
                <ul className="p-2 text-12 text-danger">
                  {shownPreview.data.diagnostics.map((d, i) => (
                    <li key={i}>
                      {d.rule} {d.message}
                    </li>
                  ))}
                </ul>
              ) : shownPreview.data ? (
                <div className="flex h-full min-h-0 flex-col">
                  {wholeTarget && wholePreview.stale ? (
                    <p role="status" className="flex items-center gap-2 border-b border-default px-2 py-1 text-11 text-warning" data-testid="ddl-preview-stale">
                      <span className="min-w-0 flex-1">
                        Out of date: the model changed after this render, which took {Math.round(wholePreview.elapsedMs ?? 0)} ms, so it does not render again
                        by itself.
                      </span>
                      <Button size="sm" onClick={() => wholePreview.run()} data-testid="ddl-preview-again">
                        Preview again
                      </Button>
                    </p>
                  ) : null}
                  {whole && target && picked ? (
                    <p className="border-b border-default px-2 py-1 text-11 text-secondary" data-testid="ddl-preview-fallback">
                      {emptyObjectUnitNote(target, picked.name)}
                    </p>
                  ) : null}
                  <div className="min-h-0 flex-1">
                    <CodeView language="sql" readOnly label="Generated DDL" value={shownPreview.data.files.map((f) => f.text).join("\n")} />
                  </div>
                </div>
              ) : (
                <Spinner label="Rendering the preview" />
              )}
            </div>
          </aside>
        )}
      </div>
    </div>
  );
}
