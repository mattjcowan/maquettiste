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
  type Viewport,
} from "@xyflow/react";
import { Download, LayoutGrid, Link2, Plus, Share2 } from "lucide-react";
import { activeTab } from "@/editors/tabs";
import { applySaveResult, elementQuery, keys, loadElement, useElement, useElements, useIndex, useValidation } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { CategoryTreeDoc, DiagramDoc, EntityDoc, ModelJson, PackageDoc, RelationDoc } from "@/api/types";
import { useEditor } from "@/state/store";
import { indexLookup, categoryColorIndex } from "@/model/index";
import { typeLabel } from "@/model/model";
import { newId } from "@/lib/ids";
import { local } from "@/lib/storage";
import { loadPositions, measuredSizes, removePositions, type StoredPositions } from "@/lib/positions";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Toolbar, EmptyState } from "@/components/ui/misc";
import { CreateButtons } from "@/explorer/NewElementDialog";
import { domainOfRow, FIRST_RUN_CREATE } from "@/explorer/create";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/input";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
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
import { placeNodes, roundViewport, sameViewport, savedViewport } from "@/canvas/placement";
import { diagramsInDomain, domainDiagramId, domainOfDiagram, newDomainDiagram, syncDomainMembers } from "@/canvas/domainDiagram";
import { isProcessDiagram } from "@/canvas/statechart/diagram";
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

/** The Display menu's options, kept per browser for each view (PD18): attribute detail, cardinality notation, and
 * whether relations show their attributes in a box off the label, and whether the minimap shows (it covers the
 * canvas's lower right corner, which is most of a short canvas while the bottom panel is open). */
interface DisplayOptions {
  mode: DisplayMode;
  notation: Notation;
  relationAttributes: boolean;
  minimap: boolean;
}
const DEFAULT_DISPLAY: DisplayOptions = { mode: "all", notation: "uml", relationAttributes: false, minimap: true };

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
  // While an element editor covers the canvas (General mode walks the tree), the cards keep the selection they showed:
  // re-rendering every card and edge on each step is work no one sees (EX 4.5 walk). Uncovered, they catch up.
  const covered = useEditor(store, (s) => activeTab(s.editors) !== null);
  const shownSelection = useRef(selection);
  if (!covered) shownSelection.current = selection;
  const canvasSelection = shownSelection.current;
  const draftsState = useEditor(store, (s) => s.drafts);
  const { openDiagram, select } = useEditorNavigation();
  const flow = useReactFlow<EntityFlowNode, RelationFlowEdge>();
  const nodesInitialized = useNodesInitialized();

  // A process's diagram is its statechart, shown by the process editor: the picker and the default pick leave it out.
  const diagrams = useMemo(() => lookup.ofKind("diagram").filter((d) => !isProcessDiagram(d)), [lookup]);
  const packages = lookup.ofKind("package");
  // "All of <domain>" only for a domain of at most ALL_OF_CAP entities (explorer-redesign.md 1.6).
  const allOfDomains = useMemo(() => {
    const counts = new Map<string, number>();
    for (const r of lookup.rows) if (r.kind === "entity" && r.package) counts.set(r.package, (counts.get(r.package) ?? 0) + 1);
    // A domain that has its own diagram (domainDiagram.ts) is listed under Diagrams by its name instead.
    const own = new Set(diagrams.filter((d) => d.package).map((d) => `${d.package}\u0000${d.name}`));
    return packages.filter((p) => (counts.get(p.id) ?? 0) <= ALL_OF_CAP && !own.has(`${p.id}\u0000${p.name}`));
  }, [lookup, packages, diagrams]);
  const view = parseView(activeDiagram);

  // A process diagram named by the URL is never drawn here: the default pick replaces it.
  const processDiagram = view?.type === "diagram" && isProcessDiagram(lookup.byId.get(view.id));
  useEffect(() => {
    if (processDiagram) {
      if (diagrams.length) openDiagram(diagrams[0].id, { keepEditors: true, replace: true });
      else if (packages.length) openDiagram(`pkg:${packages[0].id}`, { keepEditors: true, replace: true });
    }
  }, [processDiagram, diagrams, packages, openDiagram]);
  useEffect(() => {
    if (!activeDiagram && index.data) {
      // The default pick keeps the editors (see openDiagram): an editor opened in the first second after load stays.
      if (diagrams.length) openDiagram(diagrams[0].id, { keepEditors: true });
      else if (packages.length) openDiagram(`pkg:${packages[0].id}`, { keepEditors: true });
    }
  }, [activeDiagram, index.data, diagrams, packages, openDiagram]);

  const diagram = useDraftDocument(view?.type === "diagram" && !processDiagram ? view.id : null);
  const diagramJson = diagram.json as DiagramDoc | undefined;
  const members = useMemo(() => diagramJson?.members ?? [], [diagramJson]);
  const viewKey = keyOfView(view);

  // A domain's canvas is its own diagram once the user has arranged it: "All of <domain>" opens that diagram.
  // The domain's diagrams are read to find the one whose membership is "package" (the first in ordinal id order).
  const domainCandidates = useMemo(() => (view?.type === "package" ? diagramsInDomain(lookup.rows, view.id) : []), [view?.type, view?.id, lookup]);
  const candidateDocs = useElements(domainCandidates);
  const domainDiagram = useMemo(
    () =>
      view?.type === "package"
        ? domainDiagramId(
            domainCandidates.flatMap((id) => {
              const json = candidateDocs.byId.get(id)?.json as unknown as DiagramDoc | undefined;
              return json ? [json] : [];
            }),
            view.id,
          )
        : null,
    [view?.type, view?.id, domainCandidates, candidateDocs.byId],
  );
  useEffect(() => {
    if (domainDiagram) openDiagram(domainDiagram, { keepEditors: true, replace: true });
  }, [domainDiagram, openDiagram]);
  // The open diagram is gone (deleted, or its creation undone): a diagram in a domain falls back to "All of <domain>",
  // any other to the default pick.
  const homeOf = useRef<{ id: string; home: string | null } | null>(null);
  if (view?.type === "diagram" && diagramJson) homeOf.current = { id: view.id, home: diagramJson.package ?? null };
  useEffect(() => {
    if (view?.type !== "diagram" || !index.data || lookup.byId.has(view.id)) return;
    const home = homeOf.current?.id === view.id ? homeOf.current.home : null;
    openDiagram(home && lookup.byId.has(home) ? `pkg:${home}` : null, { keepEditors: true, replace: true });
  }, [view?.type, view?.id, index.data, lookup, openDiagram]);
  // The domain whose canvas the open diagram is (null for an ordinary diagram): its members follow the domain's entities,
  // those that joined drawn where placement puts them, those that left dropped, all through the diagram's draft.
  const diagramDomain = useMemo(() => (view?.type === "diagram" ? domainOfDiagram(diagramJson) : null), [view?.type, diagramJson]);
  useEffect(() => {
    if (view?.type !== "diagram" || !diagramDomain || !diagramJson || !syncDomainMembers(diagramJson.members ?? [], lookup.rows, diagramDomain)) return;
    drafts.edit(
      view.id,
      (json) => {
        const d = json as unknown as DiagramDoc;
        d.members = syncDomainMembers(d.members ?? [], lookup.rows, diagramDomain) ?? d.members;
      },
      { followUp: true },
    );
  }, [view?.type, view?.id, diagramDomain, diagramJson, lookup, drafts]);

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

  const [display, setDisplay] = useState<DisplayOptions>(DEFAULT_DISPLAY);
  useEffect(() => {
    setDisplay({ ...DEFAULT_DISPLAY, ...local.getJson<Partial<DisplayOptions>>(`mq.display.${viewKey}`) });
  }, [viewKey]);
  const changeDisplay = (next: DisplayOptions) => {
    setDisplay(next);
    local.setJson(`mq.display.${viewKey}`, next);
  };

  // A domain's canvas before it has a diagram: positions held in memory only (the automatic layout and placement), plus
  // any the browser kept from before domains had diagrams, which move into the diagram created on open.
  // Read as the view opens (not in an effect), so the first render of the view already has them.
  const keptPositions = useMemo(() => (view?.type === "package" ? loadPositions(`pkg.${view.id}`) : {}), [view?.type, view?.id]);
  const [placedInMemory, setPlacedInMemory] = useState<{ key: string; positions: StoredPositions }>({ key: "", positions: {} });
  const pkgPositions = useMemo(
    () => (placedInMemory.key === viewKey ? { ...keptPositions, ...placedInMemory.positions } : keptPositions),
    [keptPositions, placedInMemory, viewKey],
  );
  const setPkgPositions = useCallback(
    (update: (prev: StoredPositions) => StoredPositions) =>
      setPlacedInMemory((prev) => ({ key: viewKey, positions: update(prev.key === viewKey ? prev.positions : {}) })),
    [viewKey],
  );
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
  const relations = useMemo(() => relationLookup(lookup.rows), [lookup]);
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
            selected: canvasSelection.includes(id),
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
      canvasSelection,
      categoryIds,
      errorCounts,
      display.mode,
      typeOf,
      toggleCollapsed,
    ],
  );

  const positionOf = useMemo(() => new Map(nodes.map((n) => [n.id, n.position])), [nodes]);
  // The Display menu's "Relation attributes": each relation's attributes in a box off its label, at the cards' detail.
  const attributeBox = useMemo(
    () => (display.relationAttributes ? { mode: display.mode, typeLabel: typeOf } : null),
    [display.relationAttributes, display.mode, typeOf],
  );
  const selectRelation = useCallback(
    (id: string, additive: boolean) => {
      const current = store.getState().selection;
      select(additive ? (current.includes(id) ? current.filter((x) => x !== id) : [...current, id]) : [id]);
    },
    [select, store],
  );
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
            selected: canvasSelection.includes(rid),
            ariaLabel: `${KIND_LABELS.relation} ${relation.name}`,
            data: { relation, notation: display.notation, attributeBox, onSelect: selectRelation },
          };
        })
        .filter((e): e is NonNullable<typeof e> => e !== null),
    [relationIds, docs.byId, draftsState, positionOf, canvasSelection, display.notation, attributeBox, selectRelation],
  );

  // The first arrangement of a domain's canvas (a drag, Auto-layout, a pan or zoom) creates the domain's diagram, with
  // the entities where they are drawn, as one undoable step, and opens it; mere viewing creates nothing.
  const creatingDomain = useRef<string | null>(null);
  useEffect(() => {
    creatingDomain.current = null;
  }, [viewKey]);
  const createDomainDiagram = useCallback(
    async (packageId: string, positions: StoredPositions, viewport: Viewport | null) => {
      if (creatingDomain.current === packageId) return;
      creatingDomain.current = packageId;
      const pkg = (await queryClient.fetchQuery(elementQuery(packageId))).json as unknown as PackageDoc;
      const json = newDomainDiagram({
        id: newId(),
        domain: { id: packageId, name: pkg.name ?? lookup.nameOf(packageId) ?? "", displayName: pkg.displayName },
        rows: lookup.rows,
        positions,
        viewport: viewport ? roundViewport(viewport) : null,
      }) as unknown as ModelJson;
      const result = await endpoints.createElement(json);
      if (result.outcome !== "saved") {
        creatingDomain.current = null;
        store.getState().notify(`The domain's diagram was not saved: ${result.diagnostics[0]?.message ?? result.outcome}`, "error");
        return;
      }
      applySaveResult(queryClient, result);
      removePositions(`pkg.${packageId}`);
      store.getState().pushUndo({
        label: `New diagram ${String(json.name)}`,
        ids: [String(json.id)],
        before: [null],
        after: [result.current!.json as ModelJson],
        afterHashes: [result.hash],
      });
      openDiagram(String(json.id), { keepEditors: true, replace: true });
    },
    [queryClient, lookup, store, openDiagram],
  );
  // Positions from before domains had diagrams (kept per browser): the user arranged this canvas, so it becomes the
  // domain's diagram with them, once, and the browser's copy is removed.
  // It waits until every card has a position (the others placed beside them in memory), so the diagram is complete.
  useEffect(() => {
    if (view?.type !== "package" || domainDiagram || !index.data || !nodesInitialized || !nodes.length) return;
    if (!Object.keys(loadPositions(`pkg.${view.id}`)).length || nodes.some((n) => !pkgPositions[n.id])) return;
    void createDomainDiagram(view.id, pkgPositions, null);
  }, [view?.type, view?.id, domainDiagram, index.data, nodesInitialized, nodes, pkgPositions, createDomainDiagram]);

  /** Writes positions: into the diagram's draft, or for a domain's canvas without a diagram into memory (`user`: the user arranged it, so the diagram is created). */
  const commitPositions = useCallback(
    (positions: StoredPositions, user: boolean) => {
      if (!view) return;
      if (view.type === "package") {
        setPkgPositions((prev) => ({ ...prev, ...positions }));
        if (user) {
          const drawn = Object.fromEntries(flow.getNodes().map((n) => [n.id, n.position]));
          void createDomainDiagram(view.id, { ...drawn, ...positions }, flow.getViewport());
        }
        return;
      }
      // What the canvas places on its own (a new card, the layout on open) joins the user's last undo step.
      drafts.edit(view.id, (json) => void applyPositions(json as unknown as DiagramDoc, positions), { followUp: !user });
    },
    [drafts, view, flow, createDomainDiagram, setPkgPositions],
  );

  // ------------------------------------------------------------------ pan and zoom
  // The viewport is saved in the diagram (x, y, zoom to three decimals) on the end of a pan or zoom, 300 ms after the
  // last; opening a diagram restores it, and fits the view only when none is saved. The canvas's own moves (restoring,
  // fitting, centring a card) are not saved.
  const programmaticUntil = useRef(0);
  const programmatic = useCallback(() => {
    programmaticUntil.current = performance.now() + 400;
  }, []);
  const persistViewport = useCallback(
    (viewport: Viewport) => {
      if (!view) return;
      const next = roundViewport(viewport);
      if (view.type === "package") {
        if (!domainDiagram) void createDomainDiagram(view.id, Object.fromEntries(flow.getNodes().map((n) => [n.id, n.position])), next);
        return;
      }
      if (sameViewport(savedViewport((drafts.current(view.id) as unknown as DiagramDoc | undefined)?.viewport), next)) return;
      drafts.edit(
        view.id,
        (json) => {
          (json as unknown as DiagramDoc).viewport = next;
        },
        { followUp: true },
      );
    },
    [view, domainDiagram, createDomainDiagram, flow, drafts],
  );
  const moveTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(moveTimer.current), []);
  const onMoveEnd = useCallback(
    (_event: unknown, viewport: Viewport) => {
      if (performance.now() < programmaticUntil.current) return;
      clearTimeout(moveTimer.current);
      moveTimer.current = setTimeout(() => persistViewport(viewport), 300);
    },
    [persistViewport],
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
        if (Object.keys(done).length) commitPositions(done, true);
      });
    },
    [commitPositions, applySelection],
  );

  // The full automatic layout: the Auto-layout button (`explicit`: it re-lays out every card, fits the view and saves
  // that view), or on open when no card has a position yet.
  const restoredFor = useRef<string | null>(null);
  const saveFit = useRef(false);
  const autoLayout = useCallback(
    async (explicit: boolean) => {
      if (!view) return;
      setLayingOut(true);
      try {
        const input = layoutInput(flow.getNodes(), flow.getEdges());
        const started = performance.now();
        const positions = Object.fromEntries(await defaultLayoutEngine().layout(input.nodes, input.edges));
        setLayoutMs(Math.round(performance.now() - started));
        restoredFor.current = viewKey;
        if (view.type === "package" && explicit) {
          // The domain's diagram is created with these positions and opened; with no viewport saved it fits on open.
          setPkgPositions((prev) => ({ ...prev, ...positions }));
          await createDomainDiagram(view.id, positions, null);
          return;
        }
        commitPositions(positions, explicit);
        if (view.type === "diagram") await drafts.flush(view.id);
        saveFit.current = explicit && view.type === "diagram";
        setFitPending(true);
      } finally {
        setLayingOut(false);
      }
    },
    [view, viewKey, flow, commitPositions, drafts, createDomainDiagram, setPkgPositions],
  );

  useEffect(() => {
    if (nodesInitialized && unplaced && nodes.length && laidOutFor.current !== viewKey) {
      laidOutFor.current = viewKey;
      void autoLayout(false);
    }
  }, [nodesInitialized, unplaced, nodes.length, viewKey, autoLayout]);

  useEffect(() => {
    if (!fitPending) return;
    const frame = requestAnimationFrame(() => {
      setFitPending(false);
      programmatic();
      void flow.fitView({ padding: 0.1, duration: 0 }).then(() => {
        if (saveFit.current) persistViewport(flow.getViewport());
        saveFit.current = false;
      });
    });
    return () => cancelAnimationFrame(frame);
  }, [fitPending, nodes, flow, programmatic, persistViewport]);

  // On open: the saved viewport, else fit the cards (once they are measured; a canvas laid out on open fits after the layout).
  const stored = view?.type === "diagram" ? savedViewport(diagramJson?.viewport) : null;
  const viewReady = view?.type === "package" || !!diagramJson;
  useEffect(() => {
    if (!view || !viewReady || restoredFor.current === viewKey) return;
    if (stored) {
      restoredFor.current = viewKey;
      programmatic();
      void flow.setViewport(stored, { duration: 0 });
      return;
    }
    if (!nodesInitialized || !nodes.length || unplaced) return;
    restoredFor.current = viewKey;
    programmatic();
    void flow.fitView({ padding: 0.15, duration: 0 });
  }, [view, viewKey, viewReady, stored, nodesInitialized, nodes.length, unplaced, flow, programmatic]);

  // Cards with no position while others have one (a new entity, an entity that joined the domain, a member added
  // without a position): only those are placed, beside a related card or under the drawing, and nothing else moves.
  // A new card that is selected is scrolled into view without changing the zoom.
  const placedFor = useRef("");
  useEffect(() => {
    if (!view || !nodesInitialized || !nodes.length || unplaced || layingOut) return;
    const missing = nodes.filter((n) => !storedPosition(view, memberOf.get(n.id), pkgPositions, n.id)).map((n) => n.id);
    if (!missing.length) return;
    const key = `${viewKey}:${missing.join(",")}`;
    if (placedFor.current === key) return;
    placedFor.current = key;
    const size = (id: string) => {
      const m = flow.getInternalNode(id)?.measured;
      return { width: m?.width || NODE_WIDTH, height: m?.height || NODE_HEIGHT };
    };
    const missingSet = new Set(missing);
    const placed = nodes.filter((n) => !missingSet.has(n.id)).map((n) => ({ id: n.id, ...n.position, ...size(n.id) }));
    const links = relationIds.flatMap((r) => {
      const ends = relations.endsOf(r);
      return ends && ends.length >= 2 ? [{ source: ends[0], target: ends[1] }] : [];
    });
    const positions = placeNodes({ placed, unplaced: missing.map((id) => ({ id, ...size(id) })), edges: links });
    commitPositions(positions, false);
    const shown = store.getState().selection.find((id) => missingSet.has(id));
    const el = canvasRef.current;
    if (shown && el && positions[shown]) {
      const { x, y, zoom } = flow.getViewport();
      const p = positions[shown];
      const s = size(shown);
      const left = p.x * zoom + x;
      const top = p.y * zoom + y;
      if (left < 0 || top < 0 || left + s.width * zoom > el.clientWidth || top + s.height * zoom > el.clientHeight) {
        programmatic();
        void flow.setCenter(p.x + s.width / 2, p.y + s.height / 2, { zoom, duration: 0 });
      }
    }
  }, [view, viewKey, nodes, nodesInitialized, unplaced, layingOut, memberOf, pkgPositions, relationIds, relations, flow, commitPositions, store, programmatic]);

  // ------------------------------------------------------------------ in step with the explorer (3.5)
  const [dropping, setDropping] = useState(false);
  const onCanvasDragOver = (e: DragEvent) => {
    if (!e.dataTransfer.types.includes(ELEMENTS_MIME)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = view?.type === "diagram" && !diagramDomain ? "copy" : "none";
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
    if (view?.type !== "diagram" || diagramDomain) {
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
    programmatic();
    const p = node.internals.positionAbsolute;
    void flow.setCenter(p.x + (node.measured.width ?? NODE_WIDTH) / 2, p.y + (node.measured.height ?? NODE_HEIGHT) / 2, {
      zoom: Math.max(flow.getZoom(), 0.75),
      duration: 0,
    });
    setCentered(centerRequest.id);
  }, [centerRequest, nodes, flow, lookup, relations, programmatic]);

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
    // No position: the canvas places the new card beside the drawing (placement.ts) and scrolls it into view.
    // A domain's own diagram gains it through the domain (or not, when it goes to another domain).
    if (view?.type === "diagram" && !diagramDomain) {
      const outcome = await createWithMember(services, json, view.id, { element: id }, `New entity ${name}`);
      if (!outcome.ok) return outcome.reason;
    } else {
      const result = await endpoints.createElement(json);
      if (result.outcome !== "saved") return result.diagnostics[0]?.message ?? result.outcome;
      applySaveResult(queryClient, result);
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
        {view.type === "diagram" && !diagramDomain ? (
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
        <Button size="sm" onClick={() => void autoLayout(true)} disabled={layingOut} data-testid="auto-layout">
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
              <DropdownMenuCheckboxItem
                checked={display.relationAttributes}
                onCheckedChange={(checked) => changeDisplay({ ...display, relationAttributes: checked === true })}
                data-testid="display-relation-attributes"
              >
                Relation attributes
              </DropdownMenuCheckboxItem>
              <DropdownMenuCheckboxItem
                checked={display.minimap}
                onCheckedChange={(checked) => changeDisplay({ ...display, minimap: checked === true })}
                data-testid="display-minimap"
              >
                Minimap
              </DropdownMenuCheckboxItem>
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
          onMoveEnd={onMoveEnd}
          minZoom={0.1}
          onlyRenderVisibleElements={nodes.length > 100}
          proOptions={{ hideAttribution: false }}
          deleteKeyCode={null}
          multiSelectionKeyCode="Shift"
          ariaLabelConfig={{ "node.a11yDescription.default": "Press Enter or Space to select. Arrow keys move the selected entity." }}
        >
          <Background color={cssColor("--mq-border-default")} gap={16} />
          <Controls showInteractive={false} />
          {display.minimap ? (
            <MiniMap
              pannable
              zoomable
              ariaLabel="Minimap"
              style={{ width: 160, height: 100 }}
              nodeColor={(n) => cssColor(`--mq-cat-${(n.data as { categoryIndex: number | null }).categoryIndex ?? 1}`)}
            />
          ) : null}
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
    <div className="flex h-full flex-col items-center justify-center gap-2 p-2 text-center" data-testid="first-run">
      <h2 className="text-15 font-semibold text-primary">Start the model</h2>
      <p className="max-w-md text-13 text-secondary">
        Create a domain first, then its entities, enums, value objects and custom types. Reference types hold rows managed as data; a database maps the model
        onto tables; a diagram shows a chosen set of entities.
      </p>
      <CreateButtons kinds={FIRST_RUN_CREATE} testid="first-run-create" />
    </div>
  );
}
