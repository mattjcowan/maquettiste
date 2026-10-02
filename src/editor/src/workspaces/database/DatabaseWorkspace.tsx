// The Database workspace (phase2-design.md 4.8): a database picker, the resolved tables of
// GET /api/databases/{id}/view as TableNodes and ForeignKeyEdges (auto-laid out, positions per
// browser), the dialect selector (edits the database element) and the live DDL preview
// (POST /api/templates/preview with the unit ddlPreview.ts picks: an enabled pack's database unit, or its each-table
// unit for a selected table, the each-<kind> unit of a picked view, sequence, routine, database type or SQL object; a picked
// query shows its own SQL), refreshed 400 ms after any model.changed. The list beside the canvas shows the tables, views,
// sequences, routines, queries, database types or SQL objects (a kind chip picks which), and the toolbar's New menu creates any
// of them, or a schema, in the database.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Background, Controls, MiniMap, ReactFlow, ReactFlowProvider, useNodesInitialized, useReactFlow, type NodeChange, type Viewport } from "@xyflow/react";
import { Download, LayoutGrid, Plus } from "lucide-react";
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
import { ColumnPanel } from "./ColumnGrid";
import { databaseTargetOf, ddlPreviewCaption, ddlPreviewTarget, emptyObjectUnitNote, NO_DDL_UNIT, type DdlObject } from "./ddlPreview";
import { DATABASE_CREATE, DATABASE_CREATE_LABELS } from "@/explorer/databaseCreate";
import { filterObjects, LIST_KINDS, OBJECT_LIST_MEMBERS, type ListKind, type ObjectListKind } from "./tableList";
import { countOf, KIND_LABELS } from "@/model/labels";

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
        data: { table },
      })),
    [tables, positions, dragging, measured, selectedTable],
  );
  const edges: ForeignKeyFlowEdge[] = useMemo(
    () =>
      tables.flatMap((t) =>
        t.foreignKeys
          .filter((fk) => tables.some((x) => x.key === fk.referencedTable))
          .map((fk) => ({
            id: `${t.key}:${fk.name}`,
            type: "foreignKey" as const,
            source: t.key,
            target: fk.referencedTable,
            sourceHandle: "r",
            targetHandle: "l",
            ariaLabel: `Foreign key ${fk.name}`,
            data: { foreignKey: fk },
          })),
      ),
    [tables],
  );

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
      void flow.fitView({ padding: 0.1 });
    });
  }, [activeDatabase, edges, flow, layoutKey, programmatic]);

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
    const placed = nodes.filter((n) => !set.has(n.id)).map((n) => ({ id: n.id, ...n.position, ...size(n.id) }));
    const added = placeNodes({ placed, unplaced: missing.map((id) => ({ id, ...size(id) })), edges });
    const next = { ...loadPositions(`db.${activeDatabase}`), ...added };
    savePositions(`db.${activeDatabase}`, next);
    setPositions(next);
  }, [initialized, activeDatabase, missing, unplaced, nodes, edges, flow]);

  // Pan and zoom are kept per browser beside the positions; opening restores them and fits only when none is kept.
  useEffect(() => {
    if (!initialized || !activeDatabase || !nodes.length || unplaced || viewportFor.current === layoutKey) return;
    viewportFor.current = layoutKey;
    const kept = loadViewport(`db.${activeDatabase}`);
    programmatic();
    if (kept) void flow.setViewport(kept, { duration: 0 });
    else void flow.fitView({ padding: 0.1, duration: 0 });
  }, [initialized, activeDatabase, nodes.length, unplaced, layoutKey, flow, programmatic]);
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
  const preview = usePreview(target?.pack ?? "", target?.unit ?? "", target?.elementId ?? null, !!target && target.scope !== "query" && !!view.data?.view);
  // An object's unit that writes nothing for the pick (a pack parameter turns it off): the whole database instead.
  const objectEmpty =
    !!picked && target?.scope === picked.kind && !preview.isPlaceholderData && !!preview.data && !preview.data.files.length && !preview.data.diagnostics.length;
  const whole = objectEmpty ? databaseTargetOf(packs.data ?? [], activeDatabase ?? null) : null;
  const wholePreview = usePreview(whole?.pack ?? "", whole?.unit ?? "", whole?.elementId ?? null, !!whole && !!view.data?.view);
  const shownPreview = whole ? wholePreview : preview;
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
          <section className={`flex w-56 shrink-0 flex-col border-r border-default bg-surface`} aria-label="Tables" data-testid="database-tables">
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
                <ul className="min-h-0 flex-1 overflow-auto py-1 text-12" aria-label={`${KIND_LABELS[listKind]} list`}>
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
            <ul className={listedObjects ? "hidden" : "min-h-0 flex-1 overflow-auto py-1 text-12"} aria-label="Table list">
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
          <div className="relative min-h-0 flex-1" role="region" aria-label="Table diagram">
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
                  if (scope.mode !== "all") return;
                  setSelectedTable(null);
                  store.getState().inspectTable(null, null);
                }}
                onMoveEnd={onMoveEnd}
                minZoom={0.1}
                nodesConnectable={false}
                deleteKeyCode={null}
                proOptions={{ hideAttribution: false }}
              >
                <Background gap={16} />
                <Controls showInteractive={false} />
                <MiniMap pannable zoomable ariaLabel="Minimap" />
              </ReactFlow>
            )}
          </div>
          {/* The selected table's columns, editable (a synthesized table's edits go to its overlay). */}
          <ColumnPanel table={allTables.find((t) => t.key === selectedTable) ?? null} databaseId={activeDatabase} />
        </div>
        {ddlCollapsed ? <EdgeToggle panel="ddl" side="right" /> : null}
        {!ddlCollapsed && (
          <aside
            className={`flex w-[40%] min-w-80 max-w-[640px] flex-col border-l border-default bg-surface`}
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
