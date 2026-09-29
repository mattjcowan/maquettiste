// The Domain model screen (phase2-design.md 4.8): a diagram picker (the model's diagrams plus a
// virtual "All of <domain>" view of every entity in a domain) and the React Flow canvas. In step with the explorer
// (explorer-redesign.md 3.5): explorer rows dropped on a diagram become members, and Show on canvas, go to definition
// and history centre the card they select.
import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from "react";
import {
  Background,
  Controls,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  useNodesInitialized,
  useReactFlow,
  type Connection,
  type NodeChange,
  type EdgeChange,
} from "@xyflow/react";
import { Download, LayoutGrid, Link2, Plus, Share2 } from "lucide-react";
import { applySaveResult, keys, loadElement, useElement, useElements, useIndex, useValidation } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { CategoryTreeDoc, DiagramDoc, EntityDoc, ModelJson, RelationDoc } from "@/api/types";
import { useEditor } from "@/state/store";
import { indexLookup, categoryColorIndex } from "@/model/index";
import { typeLabel } from "@/model/model";
import { newId } from "@/lib/ids";
import { local } from "@/lib/storage";
import { loadPositions, measuredSizes, savePositions, type StoredPositions } from "@/lib/positions";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Toolbar, EmptyState } from "@/components/ui/misc";
import { CreateButtons } from "@/explorer/NewElementDialog";
import { domainOfRow, FIRST_RUN_CREATE } from "@/explorer/create";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/input";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/menu";
import { useDraftDocument } from "@/inspector/useDraft";
import { EntityNode, type DisplayMode, type EntityFlowNode } from "@/canvas/EntityNode";
import { RelationEdge, type Notation, type RelationFlowEdge } from "@/canvas/RelationEdge";
import { MarkerDefs } from "@/canvas/markers";
import { defaultLayoutEngine } from "@/canvas/layout";
import { exportCanvas } from "@/canvas/export";
import {
  applyPositions,
  applySelectChanges,
  cardPosition,
  expandRelated,
  ELEMENTS_MIME,
  layoutInput,
  NODE_HEIGHT,
  NODE_WIDTH,
  needsLayout,
  parseView,
  relationEnds,
  relationLookup,
  selectedEntities,
  storedPosition,
  viewElements,
  ALL_OF_CAP,
  viewKey as keyOfView,
} from "@/canvas/model";
import { addToDiagram, createWithMember } from "./actions";
import { NewEntityDialog, NewRelationDialog, type NewRelationInput } from "./dialogs";
import { allOf, DOMAIN_VIEWS_LABEL, KIND_LABELS } from "@/model/labels";

const nodeTypes = { entity: EntityNode };
const edgeTypes = { relation: RelationEdge };

function cssColor(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

export function EntitiesWorkspace() {
  return (
    <ReactFlowProvider>
      <EntitiesCanvas />
    </ReactFlowProvider>
  );
}

function EntitiesCanvas() {
  const services = useServices();
  const { store, drafts, queryClient } = services;
  const index = useIndex();
  const lookup = useMemo(() => indexLookup(index.data), [index.data]);
  const validation = useValidation();
  const activeDiagram = useEditor(store, (s) => s.activeDiagram);
  const selection = useEditor(store, (s) => s.selection);
  const draftsState = useEditor(store, (s) => s.drafts);
  const { openDiagram, select } = useEditorNavigation();
  const flow = useReactFlow<EntityFlowNode, RelationFlowEdge>();
  const nodesInitialized = useNodesInitialized();

  const diagrams = lookup.ofKind("diagram");
  const packages = lookup.ofKind("package");
  // "All of <domain>" only for a domain of at most ALL_OF_CAP entities (explorer-redesign.md 1.6).
  const allOfDomains = useMemo(() => {
    const counts = new Map<string, number>();
    for (const r of lookup.rows) if (r.kind === "entity" && r.package) counts.set(r.package, (counts.get(r.package) ?? 0) + 1);
    return packages.filter((p) => (counts.get(p.id) ?? 0) <= ALL_OF_CAP);
  }, [lookup, packages]);
  const view = parseView(activeDiagram);

  useEffect(() => {
    if (!activeDiagram && index.data) {
      // The default pick keeps the editors (see openDiagram): an editor opened in the first second after load stays.
      if (diagrams.length) openDiagram(diagrams[0].id, { keepEditors: true });
      else if (packages.length) openDiagram(`pkg:${packages[0].id}`, { keepEditors: true });
    }
  }, [activeDiagram, index.data, diagrams, packages, openDiagram]);

  const diagram = useDraftDocument(view?.type === "diagram" ? view.id : null);
  const diagramJson = diagram.json as DiagramDoc | undefined;
  const members = useMemo(() => diagramJson?.members ?? [], [diagramJson]);
  const viewKey = keyOfView(view);

  const { entityIds, relationIds } = useMemo(() => viewElements(view, lookup.rows, members), [view, lookup, members]);

  const docs = useElements(useMemo(() => [...entityIds, ...relationIds], [entityIds, relationIds]));
  const treeId = lookup.ofKind("category-tree")[0]?.id ?? null;
  const tree = useElement(treeId);
  const categoryIds = useMemo(() => ((tree.data?.json as CategoryTreeDoc | undefined)?.categories ?? []).map((c) => c.id), [tree.data]);
  const errorCounts = useMemo(() => {
    const map = new Map<string, number>();
    for (const d of validation.data?.diagnostics ?? []) if (d.severity === "error" && d.elementId) map.set(d.elementId, (map.get(d.elementId) ?? 0) + 1);
    return map;
  }, [validation.data]);

  const [display, setDisplay] = useState<{ mode: DisplayMode; notation: Notation }>({ mode: "all", notation: "uml" });
  useEffect(() => {
    setDisplay(local.getJson<{ mode: DisplayMode; notation: Notation }>(`mq.display.${viewKey}`) ?? { mode: "all", notation: "uml" });
  }, [viewKey]);
  const changeDisplay = (next: { mode: DisplayMode; notation: Notation }) => {
    setDisplay(next);
    local.setJson(`mq.display.${viewKey}`, next);
  };

  const [pkgPositions, setPkgPositions] = useState<StoredPositions>({});
  useEffect(() => {
    if (view?.type === "package") setPkgPositions(loadPositions(`pkg.${view.id}`));
  }, [view?.type, view?.id]);
  const [dragging, setDragging] = useState<StoredPositions>({});
  // Controlled nodes: React Flow shows a node only once its measured size comes back through
  // onNodesChange as a `dimensions` change, so the sizes are kept here and passed back.
  const [measured, setMeasured] = useState<Record<string, { width: number; height: number }>>({});
  const [layoutMs, setLayoutMs] = useState<number | null>(null);
  const [layingOut, setLayingOut] = useState(false);
  const laidOutFor = useRef<string | null>(null);
  const canvasRef = useRef<HTMLDivElement>(null);
  // Fit the view once the laid-out positions have reached the nodes (they arrive through the draft).
  const [fitPending, setFitPending] = useState(false);

  const nameOf = useCallback((id: string) => lookup.nameOf(id), [lookup]);
  const typeOf = useCallback((a: Parameters<typeof typeLabel>[0]) => typeLabel(a, nameOf), [nameOf]);
  const toggleCollapsed = useCallback(
    (id: string) => {
      if (view?.type !== "diagram") return;
      drafts.edit(view.id, (json) => {
        const m = (json as unknown as DiagramDoc).members?.find((x) => x.element === id);
        if (!m) return;
        if (m.collapsed) delete m.collapsed;
        else m.collapsed = true;
      });
    },
    [drafts, view],
  );

  const memberOf = useMemo(() => new Map(members.map((m) => [m.element, m])), [members]);
  const unplaced = needsLayout(view, entityIds, memberOf, pkgPositions);

  const nodes: EntityFlowNode[] = useMemo(
    () =>
      entityIds
        .filter((id) => docs.byId.has(id))
        .map((id, i) => {
          const json = (draftsState[id]?.json ?? docs.byId.get(id)!.json) as unknown as EntityDoc;
          const member = memberOf.get(id);
          const position = cardPosition(i, dragging[id], storedPosition(view, member, pkgPositions, id));
          return {
            id,
            type: "entity" as const,
            position,
            measured: measured[id],
            selected: selection.includes(id),
            ariaLabel: `Entity ${json.name}`,
            data: {
              entity: json,
              categoryIndex: categoryColorIndex(categoryIds, json.category),
              errorCount: errorCounts.get(id) ?? 0,
              collapsed: member?.collapsed === true,
              display: display.mode,
              typeLabel: typeOf,
              dirty: !!draftsState[id],
              onToggleCollapsed: toggleCollapsed,
            },
          };
        }),
    [
      entityIds,
      docs.byId,
      draftsState,
      memberOf,
      view,
      pkgPositions,
      dragging,
      measured,
      selection,
      categoryIds,
      errorCounts,
      display.mode,
      typeOf,
      toggleCollapsed,
    ],
  );

  const positionOf = useMemo(() => new Map(nodes.map((n) => [n.id, n.position])), [nodes]);
  const edges: RelationFlowEdge[] = useMemo(
    () =>
      relationIds
        .map((rid) => {
          const doc = docs.byId.get(rid);
          if (!doc) return null;
          const relation = (draftsState[rid]?.json ?? doc.json) as unknown as RelationDoc;
          const ends = relationEnds(relation, positionOf);
          if (!ends) return null;
          return {
            id: rid,
            type: "relation" as const,
            ...ends,
            selected: selection.includes(rid),
            ariaLabel: `${KIND_LABELS.relation} ${relation.name}`,
            data: { relation, notation: display.notation },
          };
        })
        .filter((e): e is NonNullable<typeof e> => e !== null),
    [relationIds, docs.byId, draftsState, positionOf, selection, display.notation],
  );

  const commitPositions = useCallback(
    (positions: StoredPositions) => {
      if (!view) return;
      if (view.type === "package") {
        const next = { ...loadPositions(`pkg.${view.id}`), ...positions };
        savePositions(`pkg.${view.id}`, next);
        setPkgPositions(next);
        return;
      }
      drafts.edit(view.id, (json) => void applyPositions(json as unknown as DiagramDoc, positions));
    },
    [drafts, view],
  );

  // Controlled canvas: clicks, Shift+click, Enter on a focused card and box selection arrive as
  // `select` changes on nodes and edges; they update the store's selection (and so the inspector).
  const applySelection = useCallback(
    (changes: readonly { type: string; id?: string; selected?: boolean }[]) => {
      const next = applySelectChanges(store.getState().selection, changes);
      if (next) select(next);
    },
    [select, store],
  );
  const onEdgesChange = useCallback((changes: EdgeChange<RelationFlowEdge>[]) => applySelection(changes), [applySelection]);

  const onNodesChange = useCallback(
    (changes: NodeChange<EntityFlowNode>[]) => {
      const sizes = measuredSizes(changes);
      if (sizes) setMeasured((prev) => ({ ...prev, ...sizes }));
      applySelection(changes);
      const done: StoredPositions = {};
      setDragging((prev) => {
        const next = { ...prev };
        for (const change of changes) {
          if (change.type !== "position") continue;
          if (change.position) next[change.id] = change.position;
          if (change.dragging === false) {
            const final = change.position ?? prev[change.id];
            if (final) done[change.id] = final;
            delete next[change.id];
          }
        }
        return next;
      });
      queueMicrotask(() => {
        if (Object.keys(done).length) commitPositions(done);
      });
    },
    [commitPositions, applySelection],
  );

  const autoLayout = useCallback(async () => {
    if (!view) return;
    setLayingOut(true);
    try {
      const input = layoutInput(flow.getNodes(), flow.getEdges());
      const started = performance.now();
      const positions = await defaultLayoutEngine().layout(input.nodes, input.edges);
      setLayoutMs(Math.round(performance.now() - started));
      commitPositions(Object.fromEntries(positions));
      if (view.type === "diagram") await drafts.flush(view.id);
      setFitPending(true);
    } finally {
      setLayingOut(false);
    }
  }, [view, flow, commitPositions, drafts]);

  useEffect(() => {
    if (nodesInitialized && unplaced && nodes.length && laidOutFor.current !== viewKey) {
      laidOutFor.current = viewKey;
      void autoLayout();
    }
  }, [nodesInitialized, unplaced, nodes.length, viewKey, autoLayout]);

  useEffect(() => {
    if (!fitPending) return;
    const frame = requestAnimationFrame(() => {
      setFitPending(false);
      void flow.fitView({ padding: 0.1, duration: 0 });
    });
    return () => cancelAnimationFrame(frame);
  }, [fitPending, nodes, flow]);

  // ------------------------------------------------------------------ in step with the explorer (3.5)
  const relations = useMemo(() => relationLookup(lookup.rows), [lookup]);
  const [dropping, setDropping] = useState(false);
  const onCanvasDragOver = (e: DragEvent) => {
    if (!e.dataTransfer.types.includes(ELEMENTS_MIME)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = view?.type === "diagram" ? "copy" : "none";
    setDropping(true);
  };
  const onCanvasDrop = async (e: DragEvent) => {
    setDropping(false);
    const raw = e.dataTransfer.getData(ELEMENTS_MIME);
    if (!raw) return;
    e.preventDefault();
    let ids: string[] = [];
    try {
      const parsed: unknown = JSON.parse(raw);
      if (Array.isArray(parsed)) ids = parsed.filter((x): x is string => typeof x === "string");
    } catch {
      return;
    }
    if (view?.type !== "diagram") {
      store.getState().notify("Choose a diagram to drop elements on; a domain's view already shows all of its entities.");
      return;
    }
    const kindOf = (id: string) => lookup.byId.get(id)?.kind;
    const dropped = ids.filter((id) => kindOf(id) === "relation");
    // A dropped relation brings its ends, so its edge has somewhere to go.
    const entities = [...ids.filter((id) => kindOf(id) === "entity"), ...dropped.flatMap((r) => relations.endsOf(r) ?? [])];
    if (!entities.length) {
      store.getState().notify("Only entities and relationships can go on this diagram.");
      return;
    }
    const at = flow.screenToFlowPosition({ x: e.clientX, y: e.clientY });
    const added = await addToDiagram(services, view.id, { entities, relations: dropped, lookup: relations, at: { x: at.x - NODE_WIDTH / 2, y: at.y - 20 } });
    store
      .getState()
      .notify(added.length ? `Added ${added.length === 1 ? "1 element" : `${added.length} elements`} to the diagram.` : "Already on the diagram.");
  };

  // Centre a card on request (Show on canvas, go to definition, history). A request waits for its card to be measured,
  // and one not answered within a few seconds (the element is not on this canvas) is dropped.
  const centerRequest = useEditor(store, (s) => s.centerRequest);
  const handled = useRef({ nonce: 0, seen: 0, at: 0 });
  const [centered, setCentered] = useState<string | null>(null);
  useEffect(() => {
    if (!centerRequest || handled.current.nonce === centerRequest.nonce) return;
    if (handled.current.seen !== centerRequest.nonce) handled.current = { nonce: 0, seen: centerRequest.nonce, at: performance.now() };
    if (performance.now() - handled.current.at > 3000) {
      handled.current.nonce = centerRequest.nonce;
      return;
    }
    const id = lookup.byId.get(centerRequest.id)?.kind === "relation" ? (relations.endsOf(centerRequest.id)?.[0] ?? centerRequest.id) : centerRequest.id;
    const node = flow.getInternalNode(id);
    if (!node?.measured.width) return;
    handled.current.nonce = centerRequest.nonce;
    const p = node.internals.positionAbsolute;
    void flow.setCenter(p.x + (node.measured.width ?? NODE_WIDTH) / 2, p.y + (node.measured.height ?? NODE_HEIGHT) / 2, {
      zoom: Math.max(flow.getZoom(), 0.75),
      duration: 0,
    });
    setCentered(centerRequest.id);
  }, [centerRequest, nodes, flow, lookup, relations]);

  const [newEntityOpen, setNewEntityOpen] = useState(false);
  // The palette's New entity (4.8) opens the same dialog as the toolbar button.
  const command = useEditor(store, (s) => s.command);
  useEffect(() => {
    if (command?.name !== "new-entity") return;
    store.getState().requestCommand(null);
    setNewEntityOpen(true);
  }, [command, store]);
  const [connecting, setConnecting] = useState<{ source: string; target: string } | null>(null);
  const [depth, setDepth] = useState("1");

  const createEntity = async (name: string, packageId: string | null): Promise<string | null> => {
    const id = newId();
    const keyId = newId();
    const json = {
      kind: "entity",
      id,
      name,
      ...(packageId ? { package: packageId } : {}),
      key: { attributes: [keyId], strategy: "uuid-v7" },
      attributes: [{ id: keyId, name: "id", type: "uuid", required: true }],
    } as unknown as ModelJson;
    const center = flow.screenToFlowPosition({ x: window.innerWidth / 2, y: window.innerHeight / 2 });
    if (view?.type === "diagram") {
      const outcome = await createWithMember(services, json, view.id, { element: id, x: Math.round(center.x), y: Math.round(center.y) }, `New entity ${name}`);
      if (!outcome.ok) return outcome.reason;
    } else {
      const result = await endpoints.createElement(json);
      if (result.outcome !== "saved") return result.diagnostics[0]?.message ?? result.outcome;
      applySaveResult(queryClient, result);
      commitPositions({ [id]: center });
      store
        .getState()
        .pushUndo({ label: `New entity ${name}`, ids: [id], before: [null], after: [result.current!.json as ModelJson], afterHashes: [result.hash] });
    }
    setNewEntityOpen(false);
    select([id]);
    return null;
  };

  const createRelation = async (input: NewRelationInput): Promise<string | null> => {
    if (!connecting) return "No ends chosen.";
    const id = newId();
    const pkg = view?.type === "diagram" ? (diagramJson?.package ?? lookup.byId.get(connecting.source)?.package) : view?.id;
    const end = (entity: string, role: string, min: 0 | 1, max: 1 | "*", onDelete?: string) => ({
      id: newId(),
      entity,
      role,
      navigation: role,
      ...(min === 1 ? { min: 1 } : {}),
      ...(max === 1 ? { max: 1 } : {}),
      ...(onDelete && onDelete !== "none" ? { onDelete } : {}),
    });
    const json = {
      kind: "relation",
      id,
      name: input.name,
      ...(pkg ? { package: pkg } : {}),
      ...(input.kind !== "association" ? { relationKind: input.kind } : {}),
      ends: [
        end(connecting.source, input.sourceRole, input.sourceMin, input.sourceMax, input.onDelete),
        end(connecting.target, input.targetRole, input.targetMin, input.targetMax),
      ],
    } as unknown as ModelJson;
    if (view?.type === "diagram") {
      const outcome = await createWithMember(services, json, view.id, { element: id }, `New relation ${input.name}`);
      if (!outcome.ok) return outcome.reason;
    } else {
      const result = await endpoints.createElement(json);
      if (result.outcome !== "saved") return result.diagnostics[0]?.message ?? result.outcome;
      applySaveResult(queryClient, result);
    }
    setConnecting(null);
    select([id]);
    return null;
  };

  const selectedIds = selectedEntities(selection, (id) => lookup.byId.get(id)?.kind);
  const selectedPair = selectedIds.length === 2 ? { source: selectedIds[0], target: selectedIds[1] } : null;

  const addRelated = async () => {
    if (view?.type !== "diagram") return;
    const start = selectedEntities(selection, (id) => lookup.byId.get(id)?.kind);
    if (!start.length) {
      store.getState().notify("Select an entity on the canvas first.", "error");
      return;
    }
    const added = await expandRelated({
      start,
      depth: Number(depth),
      present: new Set(members.map((m) => m.element)),
      origin: positionOf.get(start[0]) ?? { x: 0, y: 0 },
      relationsOf: async (entityId) => {
        const refs = await endpoints.getReferences(entityId);
        const relationIds = [...new Set(refs.map((r) => r.fromElementId))].filter((id) => lookup.byId.get(id)?.kind === "relation");
        return Promise.all(
          relationIds.map(
            async (id) => (await queryClient.fetchQuery({ queryKey: keys.element(id), queryFn: () => loadElement(id) })).json as unknown as RelationDoc,
          ),
        );
      },
    });
    if (!added.length) {
      store.getState().notify("No related elements to add at that depth.");
      return;
    }
    drafts.edit(view.id, (json) => {
      const d = json as unknown as DiagramDoc;
      d.members = [...(d.members ?? []), ...added];
    });
    await drafts.flush(view.id);
    store.getState().notify(`Added ${added.length} related elements.`);
  };

  // "New entity" starts on the current domain, by the explorer's rule (create.ts currentDomain): the selected
  // element's domain (a seed follows its target; an element in no domain gives none), else the view's.
  const newEntityDomain =
    selection[0] && lookup.byId.has(selection[0])
      ? domainOfRow(lookup.byId, selection[0])
      : view?.type === "package"
        ? view.id
        : (diagramJson?.package ?? null);

  if (index.isPending) return <EmptyState title="Loading the model…" />;
  if (!view)
    return isEmptyModel(lookup.rows) ? (
      <FirstRunPanel />
    ) : (
      <EmptyState title="No diagrams or domains yet">
        <p className="mb-2">Create a domain or a diagram to start drawing.</p>
        <CreateButtons kinds={["package", "diagram"]} testid="canvas-create" />
      </EmptyState>
    );

  const viewName = view.type === "diagram" ? (diagramJson?.name ?? lookup.nameOf(view.id) ?? "Diagram") : allOf(lookup.nameOf(view.id) ?? "?");
  const sourceName = connecting ? (lookup.nameOf(connecting.source) ?? "") : "";
  const targetName = connecting ? (lookup.nameOf(connecting.target) ?? "") : "";

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="entities-workspace">
      <Toolbar label="Diagram">
        <label htmlFor="diagram-picker" className="sr-only">
          Diagram
        </label>
        <Select id="diagram-picker" className="w-56" value={activeDiagram ?? ""} onChange={(e) => openDiagram(e.target.value)} data-testid="diagram-picker">
          <optgroup label="Diagrams">
            {diagrams.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </optgroup>
          <optgroup label={DOMAIN_VIEWS_LABEL}>
            {allOfDomains.map((p) => (
              <option key={p.id} value={`pkg:${p.id}`}>
                {allOf(p.name)}
              </option>
            ))}
          </optgroup>
        </Select>
        <Button size="sm" onClick={() => setNewEntityOpen(true)} data-testid="new-entity">
          <Plus /> New entity
        </Button>
        <Button
          size="sm"
          disabled={selectedPair === null}
          title={
            selectedPair ? undefined : "Select two entities (Shift+click, or Shift+Enter on a focused card), or drag from one card's right handle to another."
          }
          onClick={() => selectedPair && setConnecting(selectedPair)}
          data-testid="new-relation"
        >
          <Link2 /> New relation
        </Button>
        {view.type === "diagram" ? (
          <div className="flex items-center gap-1">
            <Button size="sm" onClick={() => void addRelated()} data-testid="add-related">
              <Share2 /> Add related
            </Button>
            <label htmlFor="related-depth" className="sr-only">
              Depth
            </label>
            <Select id="related-depth" className="h-7 w-14 text-12" value={depth} onChange={(e) => setDepth(e.target.value)} aria-label="Depth for add related">
              {["1", "2", "3"].map((d) => (
                <option key={d} value={d}>
                  {d}
                </option>
              ))}
            </Select>
          </div>
        ) : null}
        <Button size="sm" onClick={() => void autoLayout()} disabled={layingOut} data-testid="auto-layout">
          <LayoutGrid /> {layingOut ? "Laying out…" : "Auto-layout"}
        </Button>
        <div className="ml-auto flex items-center gap-1">
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button size="sm" variant="ghost" data-testid="display-options">
                Display
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuLabel>Attributes</DropdownMenuLabel>
              <DropdownMenuRadioGroup value={display.mode} onValueChange={(v) => changeDisplay({ ...display, mode: v as DisplayMode })}>
                <DropdownMenuRadioItem value="all">All attributes</DropdownMenuRadioItem>
                <DropdownMenuRadioItem value="keys">Keys only</DropdownMenuRadioItem>
                <DropdownMenuRadioItem value="names">Names only</DropdownMenuRadioItem>
              </DropdownMenuRadioGroup>
              <DropdownMenuSeparator />
              <DropdownMenuLabel>Cardinality</DropdownMenuLabel>
              <DropdownMenuRadioGroup value={display.notation} onValueChange={(v) => changeDisplay({ ...display, notation: v as Notation })}>
                <DropdownMenuRadioItem value="uml">UML multiplicities</DropdownMenuRadioItem>
                <DropdownMenuRadioItem value="crowsfoot">Crow's feet</DropdownMenuRadioItem>
              </DropdownMenuRadioGroup>
            </DropdownMenuContent>
          </DropdownMenu>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button size="sm" variant="ghost" data-testid="export-menu">
                <Download /> Export
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem onSelect={() => void exportCanvas("svg", flow.getNodes(), viewName, canvasRef.current)}>SVG</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => void exportCanvas("png", flow.getNodes(), viewName, canvasRef.current)}>PNG</DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </Toolbar>
      <div
        className="relative min-h-0 flex-1"
        ref={canvasRef}
        data-testid="canvas"
        data-layout-ms={layoutMs ?? undefined}
        data-node-count={nodes.length}
        data-centered={centered ?? undefined}
        data-drop-target={dropping || undefined}
        onDragOver={onCanvasDragOver}
        onDragLeave={() => setDropping(false)}
        onDrop={(e) => void onCanvasDrop(e)}
        aria-label={`Canvas: ${viewName}`}
        role="region"
      >
        <MarkerDefs />
        {docs.pending && !nodes.length ? <EmptyState title="Loading the diagram…" /> : null}
        <ReactFlow<EntityFlowNode, RelationFlowEdge>
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          edgeTypes={edgeTypes}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          onPaneClick={() => select([])}
          onConnect={(c: Connection) => c.source && c.target && setConnecting({ source: c.source, target: c.target })}
          defaultViewport={{ x: diagramJson?.viewport?.x ?? 0, y: diagramJson?.viewport?.y ?? 0, zoom: diagramJson?.viewport?.zoom ?? 1 }}
          fitView
          fitViewOptions={{ padding: 0.15 }}
          minZoom={0.1}
          onlyRenderVisibleElements={nodes.length > 100}
          proOptions={{ hideAttribution: false }}
          deleteKeyCode={null}
          multiSelectionKeyCode="Shift"
          ariaLabelConfig={{ "node.a11yDescription.default": "Press Enter or Space to select. Arrow keys move the selected entity." }}
        >
          <Background color={cssColor("--mq-border-default")} gap={16} />
          <Controls showInteractive={false} />
          <MiniMap
            pannable
            zoomable
            ariaLabel="Minimap"
            nodeColor={(n) => cssColor(`--mq-cat-${(n.data as { categoryIndex: number | null }).categoryIndex ?? 1}`)}
          />
        </ReactFlow>
      </div>
      <NewEntityDialog
        key={newEntityOpen ? `open:${newEntityDomain ?? ""}` : "closed"}
        open={newEntityOpen}
        onOpenChange={setNewEntityOpen}
        packages={packages}
        defaultPackage={newEntityDomain}
        onCreate={createEntity}
      />
      {connecting ? (
        <NewRelationDialog
          key={`${connecting.source}-${connecting.target}`}
          open
          onOpenChange={(open) => !open && setConnecting(null)}
          sourceName={sourceName}
          targetName={targetName}
          onCreate={createRelation}
        />
      ) : null}
    </div>
  );
}

/** Only settings documents (vocabularies, stereotypes) or nothing at all: the model has no element to show yet. */
export function isEmptyModel(rows: readonly { kind: string }[]): boolean {
  return rows.every((r) => r.kind === "tag-vocabulary" || r.kind === "category-tree" || r.kind === "stereotype");
}

/** The first-run panel of an empty model: every New action, in the order a model is usually built. */
function FirstRunPanel() {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-3 p-6 text-center" data-testid="first-run">
      <h2 className="text-15 font-semibold text-primary">Start the model</h2>
      <p className="max-w-md text-13 text-secondary">
        Create a domain first, then its entities, enums, value objects and custom types. Reference types hold rows managed as data; a database maps the model
        onto tables; a diagram shows a chosen set of entities.
      </p>
      <CreateButtons kinds={FIRST_RUN_CREATE} testid="first-run-create" />
    </div>
  );
}
