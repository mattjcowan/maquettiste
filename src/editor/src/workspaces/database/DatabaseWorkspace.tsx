// The Database workspace (phase2-design.md 4.8): a database picker, the resolved tables of
// GET /api/databases/{id}/view as TableNodes and ForeignKeyEdges (auto-laid out, positions per
// browser), the dialect selector (edits the database element) and the live DDL preview
// (POST /api/templates/preview with sql-ddl/schema or sql-ddl/table), refreshed 400 ms after any
// model.changed.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Background, Controls, MiniMap, ReactFlow, ReactFlowProvider, useNodesInitialized, useReactFlow, type NodeChange } from "@xyflow/react";
import { Download, LayoutGrid } from "lucide-react";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/menu";
import { exportCanvas } from "@/canvas/export";
import { useDatabaseTables, useDatabaseView, useIndex, usePreview, useProject } from "@/api/queries";
import type { DatabaseDoc } from "@/api/types";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Toolbar, EmptyState, Spinner } from "@/components/ui/misc";
import { Button } from "@/components/ui/button";
import { Input, Select } from "@/components/ui/input";
import { databaseList, filterTables } from "./tableList";
import { CodeView } from "@/code";
import { TableNode, type TableFlowNode } from "@/canvas/TableNode";
import { ForeignKeyEdge, type ForeignKeyFlowEdge } from "@/canvas/ForeignKeyEdge";
import { MarkerDefs } from "@/canvas/markers";
import { defaultLayoutEngine } from "@/canvas/layout";
import { loadPositions, measuredSizes, savePositions, type StoredPositions } from "@/lib/positions";
import { useDraftDocument } from "@/inspector/useDraft";
import { DIALECTS } from "@/inspector/fields";

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

  useEffect(() => {
    if (activeDatabase) setPositions(loadPositions(`db.${activeDatabase}`));
    // Another database: its own tables, none focused.
    if (shownDatabase.current && shownDatabase.current !== activeDatabase) setSelectedTable(null);
    shownDatabase.current = activeDatabase;
  }, [activeDatabase, setSelectedTable]);

  const tables = useMemo(() => view.data?.view?.tables ?? [], [view.data]);
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
  // `select` change. The DDL preview follows the table, the inspector the table's entity.
  const pickTable = useCallback(
    (key: string) => {
      setSelectedTable(key);
      const entityId = tables.find((t) => t.key === key)?.entityId;
      if (entityId) select([entityId]);
    },
    [select, tables, setSelectedTable],
  );

  const layout = useCallback(async () => {
    if (!activeDatabase) return;
    const result = await defaultLayoutEngine().layout(
      flow.getNodes().map((n) => ({ id: n.id, width: n.measured?.width ?? 256, height: n.measured?.height ?? 200 })),
      edges.map((e) => ({ id: e.id, source: e.source, target: e.target })),
    );
    const next = Object.fromEntries(result);
    savePositions(`db.${activeDatabase}`, next);
    setPositions(next);
    requestAnimationFrame(() => void flow.fitView({ padding: 0.1 }));
  }, [activeDatabase, edges, flow]);

  const unplaced = tables.some((t) => !positions[t.key]);
  useEffect(() => {
    if (initialized && unplaced && nodes.length && laidOut.current !== activeDatabase) {
      laidOut.current = activeDatabase;
      void layout();
    }
  }, [initialized, unplaced, nodes.length, activeDatabase, layout]);

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
      requestAnimationFrame(() => void flow.fitView({ nodes: [{ id: key }], padding: 0.4, maxZoom: 1.2 }));
    },
    [pickTable, flow],
  );
  useEffect(() => {
    if (!selectedTable || centred.current === selectedTable || !initialized || !tables.some((t) => t.key === selectedTable)) return;
    focusTable(selectedTable);
  }, [selectedTable, tables, initialized, focusTable]);

  // The table section (1.3): the table summaries (E5c) with a filter, capped so a 10,000-table database stays quick.
  const summaries = useDatabaseTables(activeDatabase);
  const [tableFilter, setTableFilter] = useState("");
  const listed = useMemo(() => filterTables(summaries.data?.tables ?? [], tableFilter), [summaries.data, tableFilter]);

  const hasPack = (project.data?.packs ?? []).some((p) => p.name === "sql-ddl");
  const table = tables.find((t) => t.key === selectedTable) ?? null;
  const preview = usePreview("sql-ddl", table ? "table" : "schema", table ? table.key : activeDatabase, hasPack && !!activeDatabase && !!view.data?.view);
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
        <span className="ml-auto text-12 text-secondary">{tables.length} tables</span>
      </Toolbar>
      <div className="flex min-h-0 flex-1">
        <section className="flex w-56 shrink-0 flex-col border-r border-default bg-surface" aria-label="Tables" data-testid="database-tables">
          <div className="border-b border-default p-2">
            <Input type="search" aria-label="Filter tables" placeholder="Filter tables" value={tableFilter} onChange={(e) => setTableFilter(e.target.value)} />
          </div>
          <ul className="min-h-0 flex-1 overflow-auto py-1 text-12" aria-label="Table list">
            {listed.tables.map((t) => (
              <li key={t.key}>
                <button
                  type="button"
                  className={`flex w-full items-baseline gap-2 px-3 py-0.5 text-left hover:bg-accent-subtle ${selectedTable === t.key ? "bg-accent-subtle font-medium" : ""}`}
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
          <p className="border-t border-default px-3 py-1 text-11 text-secondary" data-testid="database-tables-count">
            {listed.more > 0
              ? `${listed.tables.length} of ${listed.total} shown; refine the filter`
              : `${listed.total} ${listed.total === 1 ? "table" : "tables"}${tableFilter ? " match" : ""}`}
          </p>
        </section>
        <div className="relative min-w-0 flex-1" role="region" aria-label="Table diagram">
          <MarkerDefs />
          {view.isPending ? <Spinner label="Resolving tables" /> : null}
          {view.data && !view.data.view ? (
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
              onPaneClick={() => setSelectedTable(null)}
              fitView
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
        <aside className="flex w-[40%] min-w-80 max-w-[640px] flex-col border-l border-default bg-surface" aria-label="DDL preview" data-testid="ddl-preview">
          <div className="flex h-9 items-center gap-2 border-b border-default px-3 text-12">
            <span className="font-semibold">DDL preview</span>
            <span className="truncate text-secondary">{table ? `sql-ddl/table · ${table.name}` : "sql-ddl/schema · whole database"}</span>
            {preview.isFetching ? <Spinner label="Rendering" /> : null}
          </div>
          <div className="min-h-0 flex-1">
            {!hasPack ? (
              <EmptyState title="Install the sql-ddl pack to preview DDL">
                Copy packs/sql-ddl into .maquettiste/templates/ and enable it in maquettiste.json.
              </EmptyState>
            ) : preview.data?.diagnostics.length && !preview.data.files.length ? (
              <ul className="p-3 text-12 text-danger">
                {preview.data.diagnostics.map((d, i) => (
                  <li key={i}>
                    {d.rule} {d.message}
                  </li>
                ))}
              </ul>
            ) : preview.data ? (
              <CodeView language="sql" readOnly label="Generated DDL" value={preview.data.files.map((f) => f.text).join("\n")} />
            ) : (
              <Spinner label="Rendering the preview" />
            )}
          </div>
        </aside>
      </div>
    </div>
  );
}
