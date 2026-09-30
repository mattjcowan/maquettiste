// The statechart canvas (phase-3-design.md 6.3), on the Chart tab of the process editor. It draws the process document
// (chartModel.ts) at the places its process diagram saves (diagram.ts, 2.7): a chart that has no diagram yet is drawn
// from a full layout held in memory, and the first arrangement (a drag, Layout, a pan or zoom, a collapse) creates the
// diagram as one undoable step. Moves save through the diagram's draft (one PUT, 500 ms after the last); the viewport
// is saved on move end as the entity canvas saves it. A state without a saved place (a new one) is placed inside its
// container only (chartLayout.ts placeMissing) and nothing else moves; only Layout (Ctrl/Cmd+L: the whole chart, or the
// selected container) moves saved states. Selection and keyboard follow keyboard.ts; every gesture is one undo step
// (process changes through pc.change, positions through the diagram's draft). The simulation view (P4b's store)
// highlights the active states and flashes the edges it just took.
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { Background, Controls, ReactFlow, ReactFlowProvider, useReactFlow, type EdgeChange, type NodeChange, type Viewport } from "@xyflow/react";
import { LayoutGrid } from "lucide-react";
import { keys, useElement, useIndex } from "@/api/queries";
import type { DiagramDoc, ElementDocument } from "@/api/types";
import { useServices } from "@/app/context";
import { focusProcess } from "@/app/processFocus";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { EmptyState, Toolbar } from "@/components/ui/misc";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/menu";
import { useBinding } from "@/editors/process/ProcessControls";
import { selectProcessNode, useProcessSelection } from "@/editors/process/selection";
import type { ProcessContext } from "@/editors/process/shared";
import { useSimulationView } from "@/editors/process/simulation/view";
import { useDraftDocument } from "@/inspector/useDraft";
import { indexLookup } from "@/model/index";
import { CHART_LABELS } from "@/model/labels";
import { addSiblingState, addState, stateIndex, transitionLabel, type ProcessDoc } from "@/model/process";
import { perfRecord } from "@/lib/perf";
import { applySelectChanges } from "../model";
import { defaultHierarchyEngine, warmHierarchyEngine, type Boxes } from "../layout";
import { roundViewport, sameViewport, savedViewport } from "../placement";
import { changeWithDiagram, createProcessDiagram } from "./actions";
import { activeStates, buildChart, collapsedAncestors, descendants, representatives, visibleEdges, type Chart } from "./chartModel";
import { dotBox, grownSizes, INIT, isOpen, layoutChart, placeMissing, regionSeparators, resolveBoxes } from "./chartLayout";
import { collapsedStates, processDiagramId, savedPlacements, setCollapsed, writePlacements, type Placement, type Placements } from "./diagram";
import { deleteStates, incomingFromOutside, linkStates, removedStates } from "./edits";
import { directionOf, enterTarget, moveSelection, nearestInDirection, outgoing, parentTarget, walk } from "./keyboard";
import { InitialNode, StateNode, stateDomId, type InitialFlowNode, type StateFlowNode } from "./StateNode";
import { ChartMarkers, edgeDomId, InitialEdge, TransitionEdge, type InitialFlowEdge, type TransitionFlowEdge } from "./TransitionEdge";

const nodeTypes = { state: StateNode, initial: InitialNode };
const edgeTypes = { transition: TransitionEdge, initial: InitialEdge };

type FlowNode = StateFlowNode | InitialFlowNode;
type FlowEdge = TransitionFlowEdge | InitialFlowEdge;

/** How long a taken edge stays marked after a simulation step (the flash animation's length). */
const FLASH_MS = 1200;

function cssColor(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

export function StatechartCanvas({ pc }: { pc: ProcessContext }) {
  return (
    <ReactFlowProvider>
      <ChartCanvas pc={pc} />
    </ReactFlowProvider>
  );
}

/** Boxes relative to the canvas: each state's box moved by its ancestors' positions. */
function absoluteBoxes(chart: Chart, boxes: Boxes): Boxes {
  const out: Boxes = new Map();
  for (const id of chart.order) {
    const b = boxes.get(id);
    if (!b) continue;
    const parent = chart.states.get(id)!.parent;
    const p = parent ? out.get(parent) : undefined;
    out.set(id, p ? { ...b, x: b.x + p.x, y: b.y + p.y } : b);
  }
  return out;
}

interface Selection {
  states: string[];
  transition: string | null;
}

interface MenuAt {
  x: number;
  y: number;
  kind: "state" | "transition";
  id: string;
}

function ChartCanvas({ pc }: { pc: ProcessContext }) {
  const services = useServices();
  const { store, drafts, queryClient } = services;
  const { process, id: processId } = pc;
  const flow = useReactFlow<FlowNode, FlowEdge>();
  const index = useIndex();
  const lookup = useMemo(() => indexLookup(index.data), [index.data]);
  const diagramId = useMemo(() => processDiagramId(lookup.rows, processId), [lookup, processId]);
  const diagram = useDraftDocument(diagramId);
  const diagramJson = diagram.json as DiagramDoc | undefined;
  const binding = useBinding(process.subject, process.boundAttribute).members;
  // The member names by value: the binding reads them into a new list on every render.
  const membersKey = binding ? JSON.stringify(binding) : null;
  const members = useMemo(() => (membersKey === null ? null : (JSON.parse(membersKey) as string[])), [membersKey]);
  const chart = useMemo(
    () => buildChart(process, { members, problems: pc.problems, actorNames: pc.actorNames }),
    [process, members, pc.problems, pc.actorNames],
  );
  const stateIds = useMemo(() => new Set(chart.order), [chart]);
  const mountedAt = useRef(performance.now());
  // The layout worker starts while the user looks at the chart, not when they first ask for Layout.
  useEffect(() => {
    const timer = setTimeout(() => warmHierarchyEngine(), 200);
    return () => clearTimeout(timer);
  }, []);

  // ------------------------------------------------------------------ placements
  // With a diagram: its members. Without one: a layout held in memory (and the collapsed containers the user chose).
  const ready = !diagramId || !!diagramJson;
  const [memory, setMemory] = useState<{ process: string; placements: Placements; collapsed: Set<string> }>({
    process: processId,
    placements: new Map(),
    collapsed: new Set(),
  });
  const memoryOf = memory.process === processId ? memory : { process: processId, placements: new Map<string, Placement>(), collapsed: new Set<string>() };
  const saved = useMemo(() => (diagramJson ? savedPlacements(diagramJson, stateIds) : null), [diagramJson, stateIds]);
  const collapsed = useMemo(() => (diagramJson ? collapsedStates(diagramJson) : memoryOf.collapsed), [diagramJson, memoryOf.collapsed]);
  const [dragging, setDragging] = useState<Record<string, { x: number; y: number }>>({});
  const placements = useMemo(() => {
    const base = new Map(saved ?? memoryOf.placements);
    for (const [id, p] of Object.entries(dragging)) base.set(id, { ...base.get(id), ...p });
    return base;
  }, [saved, memoryOf.placements, dragging]);
  const boxes = useMemo(() => resolveBoxes(chart, placements, collapsed), [chart, placements, collapsed]);
  const absolute = useMemo(() => absoluteBoxes(chart, boxes), [chart, boxes]);
  const rep = useMemo(() => representatives(chart, collapsed), [chart, collapsed]);
  const empty = ready && chart.order.every((id) => !placements.has(id));
  // Nothing is drawn until the states have their places (the diagram loaded, or the first layout done): a node drawn
  // before would sit at the origin under the overlay, and the first paint would time a frame with no state in place.
  const drawable = ready && !empty;
  const [layingOut, setLayingOut] = useState(false);
  const [layoutMs, setLayoutMs] = useState<number | null>(null);
  const canvasRef = useRef<HTMLDivElement>(null);

  // Positions the canvas decides on its own (a first layout, a new state's place) join the user's last undo step.
  const writeFollowUp = useCallback(
    (next: Placements) => {
      if (!next.size) return;
      if (diagramId)
        drafts.edit(diagramId, (json) => void writePlacements(json as unknown as DiagramDoc, next), { followUp: true, base: diagram.element.data });
      else
        setMemory((prev) => ({
          ...(prev.process === processId ? prev : { process: processId, collapsed: new Set() }),
          placements: new Map([...(prev.process === processId ? prev.placements : []), ...next]),
        }));
    },
    [diagramId, drafts, diagram.element.data, processId],
  );

  // A chart with no placement at all: the full layout (in memory until the first arrangement creates the diagram).
  const laidOutFor = useRef<string | null>(null);
  const [fitPending, setFitPending] = useState(false);
  useEffect(() => {
    if (!empty || !chart.order.length) return;
    const key = `${processId}:${diagramId ?? ""}`;
    if (laidOutFor.current === key) return;
    laidOutFor.current = key;
    setLayingOut(true);
    const started = performance.now();
    void layoutChart(chart, collapsed, null, defaultHierarchyEngine())
      .then(({ placements: next }) => {
        const ms = Math.round(performance.now() - started);
        setLayoutMs(ms);
        perfRecord("chart:layout", ms, { states: chart.order.length, scope: "open" });
        writeFollowUp(next);
        setFitPending(true);
      })
      .finally(() => setLayingOut(false));
  }, [empty, chart, collapsed, processId, diagramId, writeFollowUp]);

  // States without a place while others have one (a new state, one added on the States grid): placed inside their
  // container only; nothing that has a place moves. With a diagram, the placement waits for the process's save to
  // settle and names only the states the saved process holds: the diagram's save must neither reach the server before
  // the state it places (a dangling member refuses it) nor join an earlier undo step than the add. A refused save
  // leaves the state unplaced until the process saves again (its hash is in the key).
  const placedFor = useRef("");
  const savedProcessHash = useElement(processId).data?.hash ?? "";
  useEffect(() => {
    if (!ready || empty || layingOut) return;
    const next = placeMissing(chart, placements);
    if (!next.size) return;
    const key = `${diagramId ?? ""}:${[...next.keys()].join(",")}:${savedProcessHash}`;
    if (placedFor.current === key) return;
    placedFor.current = key;
    if (!diagramId) {
      writeFollowUp(next);
      return;
    }
    let settled = false;
    let cancelled = false;
    void drafts.whenSettled(processId).then(() => {
      settled = true;
      if (cancelled) return;
      const saved = queryClient.getQueryData<ElementDocument>(keys.element(processId))?.json as unknown as ProcessDoc | undefined;
      const held = saved ? stateIndex(saved) : new Map();
      writeFollowUp(new Map([...next].filter(([id]) => held.has(id))));
    });
    return () => {
      // Left before the write (the chart changed meanwhile): the next run places them.
      if (settled) return;
      cancelled = true;
      if (placedFor.current === key) placedFor.current = "";
    };
  }, [ready, empty, layingOut, chart, placements, diagramId, writeFollowUp, savedProcessHash, drafts, processId, queryClient]);

  // ------------------------------------------------------------------ the first arrangement creates the diagram
  const creating = useRef(false);
  const createDiagram = useCallback(
    async (overrides: Placements, nextCollapsed: ReadonlySet<string>, viewport: Viewport | null) => {
      if (creating.current) return null;
      creating.current = true;
      try {
        const all = new Map([...placements, ...overrides]);
        return await createProcessDiagram(services, {
          process: { id: processId, name: process.name, displayName: (process as { displayName?: string }).displayName, package: process.package },
          order: chart.order,
          placements: all,
          collapsed: nextCollapsed,
          viewport: viewport ? roundViewport(viewport) : null,
        });
      } finally {
        creating.current = false;
      }
    },
    [services, placements, processId, process, chart.order],
  );

  /** Saves placements the user made (a drag, Layout): into the diagram's draft, or by creating the diagram. */
  const savePlacements = useCallback(
    async (next: Placements, flush = false) => {
      if (!next.size) return;
      if (!diagramId) {
        await createDiagram(next, collapsed, flow.getViewport());
        return;
      }
      drafts.edit(diagramId, (json) => void writePlacements(json as unknown as DiagramDoc, next), { base: diagram.element.data });
      if (flush) await drafts.flush(diagramId);
    },
    [diagramId, createDiagram, collapsed, flow, drafts, diagram.element.data],
  );

  // ------------------------------------------------------------------ pan and zoom
  const programmaticUntil = useRef(0);
  const programmatic = useCallback(() => {
    programmaticUntil.current = performance.now() + 400;
  }, []);
  const persistViewport = useCallback(
    (viewport: Viewport) => {
      const next = roundViewport(viewport);
      if (!diagramId) {
        if (!empty) void createDiagram(new Map(), collapsed, viewport);
        return;
      }
      if (sameViewport(savedViewport((drafts.current(diagramId) as unknown as DiagramDoc | undefined)?.viewport), next)) return;
      drafts.edit(
        diagramId,
        (json) => {
          (json as unknown as DiagramDoc).viewport = next;
        },
        { followUp: true, base: diagram.element.data },
      );
    },
    [diagramId, empty, createDiagram, collapsed, drafts, diagram.element.data],
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

  // On open: the saved viewport, else fit the drawing once it has places; after a full Layout, fit and save that view.
  const restored = useRef(false);
  const saveFit = useRef(false);
  const storedViewport = savedViewport(diagramJson?.viewport);
  useEffect(() => {
    if (restored.current || !ready) return;
    if (storedViewport) {
      restored.current = true;
      programmatic();
      void flow.setViewport(storedViewport, { duration: 0 });
      return;
    }
    if (empty || !chart.order.length) return;
    restored.current = true;
    setFitPending(true);
  }, [ready, storedViewport, empty, chart.order.length, flow, programmatic]);
  useEffect(() => {
    if (!fitPending) return;
    const frame = requestAnimationFrame(() => {
      setFitPending(false);
      programmatic();
      void flow.fitView({ padding: 0.1, duration: 0, maxZoom: 1.25 }).then(() => {
        if (saveFit.current) persistViewport(flow.getViewport());
        saveFit.current = false;
      });
    });
    return () => cancelAnimationFrame(frame);
  }, [fitPending, flow, programmatic, persistViewport]);

  // ------------------------------------------------------------------ selection
  const external = useProcessSelection(processId);
  const [extra, setExtra] = useState<string[]>([]);
  const expected = useRef<string | null>(null);
  const selection: Selection = useMemo(
    () => ({
      states: [
        ...extra.filter((x) => x !== external?.id && stateIds.has(x)),
        ...(external?.kind === "state" && stateIds.has(external.id) ? [external.id] : []),
      ],
      transition: external?.kind === "transition" && chart.transitions.has(external.id) ? external.id : null,
    }),
    [extra, external, stateIds, chart.transitions],
  );
  const primary = selection.states.at(-1) ?? null;
  // A selection made elsewhere (the States grid, the explorer) replaces the canvas's multi-selection.
  useEffect(() => {
    const key = external ? `${external.kind}:${external.id}` : null;
    if (key !== expected.current) setExtra([]);
    expected.current = key;
  }, [external]);
  const setSelection = useCallback(
    (states: string[], transition: string | null) => {
      if (transition) {
        expected.current = `transition:${transition}`;
        setExtra([]);
        selectProcessNode({ process: processId, kind: "transition", id: transition });
        return;
      }
      const last = states.at(-1);
      expected.current = last ? `state:${last}` : null;
      setExtra(states.slice(0, -1));
      selectProcessNode(last ? { process: processId, kind: "state", id: last } : null);
    },
    [processId],
  );

  // ------------------------------------------------------------------ simulation highlight
  const sim = useSimulationView(processId);
  const active = useMemo(() => activeStates(chart, sim?.configuration ?? []), [chart, sim?.configuration]);
  // Each result that took transitions flashes them once, even when an edge is still flashing from the result before:
  // the flash class alternates with every result flashed (not with the parity of `seq`, which also grows on selection
  // changes), and a change of animation name restarts the animation.
  const [flash, setFlash] = useState<{ ids: Set<string>; seq: number; n: number } | null>(null);
  const flashes = useRef(0);
  useEffect(() => {
    if (!sim || !sim.taken.length) return;
    flashes.current += 1;
    setFlash({ ids: new Set(sim.taken), seq: sim.seq, n: flashes.current });
    const timer = setTimeout(() => setFlash(null), FLASH_MS);
    return () => clearTimeout(timer);
  }, [sim?.seq]); // eslint-disable-line react-hooks/exhaustive-deps -- once per result: `taken` is read with its seq

  // ------------------------------------------------------------------ editing state
  const [editing, setEditing] = useState<string | null>(null);
  const [linking, setLinking] = useState<{ source: string; target: string } | null>(null);
  const [menu, setMenu] = useState<MenuAt | null>(null);
  const [confirm, setConfirm] = useState<{ states: string[]; incoming: { id: string; label: string }[] } | null>(null);

  const toggleCollapsed = useCallback(
    (id: string) => {
      const next = !collapsed.has(id);
      if (!diagramId) {
        const set = new Set(collapsed);
        if (next) set.add(id);
        else set.delete(id);
        setMemory((prev) => ({ ...(prev.process === processId ? prev : { process: processId, placements: new Map() }), collapsed: set }));
        void createDiagram(new Map(), set, flow.getViewport());
        return;
      }
      drafts.edit(diagramId, (json) => void setCollapsed(json as unknown as DiagramDoc, id, next, placements.get(id)), { base: diagram.element.data });
      // Collapsing a container that holds the selection selects the container.
      if (next && primary && descendants(chart, id).includes(primary)) setSelection([id], null);
    },
    [collapsed, diagramId, processId, createDiagram, flow, drafts, diagram.element.data, placements, primary, chart, setSelection],
  );

  const rename = useCallback(
    (id: string, name: string | null) => {
      setEditing(null);
      canvasRef.current?.focus({ preventScroll: true });
      const value = name?.trim();
      if (!value) return;
      pc.change((p) => {
        const s = stateIndex(p).get(id)?.state;
        if (s && s.name !== value) s.name = value;
      });
    },
    [pc],
  );

  const selectTransition = useCallback((transition: string) => setSelection([], transition), [setSelection]);

  // ------------------------------------------------------------------ nodes and edges
  const flashClass = flash ? (flash.n % 2 ? "mq-flash-a" : "mq-flash-b") : "";
  const nodes: FlowNode[] = useMemo(() => {
    const out: FlowNode[] = [];
    if (!drawable) return out;
    const selectedStates = new Set(selection.states);
    for (const id of chart.order) {
      if (rep.get(id) !== id) continue;
      const s = chart.states.get(id)!;
      const b = boxes.get(id);
      if (!b) continue;
      const open = isOpen(s, collapsed);
      out.push({
        id,
        type: "state",
        position: { x: b.x, y: b.y },
        width: b.width,
        height: b.height,
        ...(s.parent ? { parentId: s.parent } : {}),
        selected: selectedStates.has(id),
        draggable: editing !== id,
        ariaLabel: s.label,
        // The state inside is a tree item that carries aria-selected; React Flow's application wrapper stands between
        // the nodes and the canvas, so each node is the tree that holds its item.
        ariaRole: "tree",
        data: {
          state: s,
          open,
          hidden: s.container && s.children.length && !open ? descendants(chart, id).length : null,
          separators: open ? regionSeparators(chart, id, boxes) : [],
          active: active.has(id),
          linkTarget: linking?.target === id && linking.source !== id,
          editing: editing === id,
          selectedTransition: selection.transition,
          onToggle: toggleCollapsed,
          onRename: rename,
          onSelectTransition: selectTransition,
        },
      });
    }
    // The initial dots: one per open container with an initial child, one for the process.
    const dot = (holder: string | null, target: string | null) => {
      if (!target || rep.get(target) !== target) return;
      const tb = boxes.get(target);
      if (!tb) return;
      const d = dotBox(tb);
      out.push({
        id: `${INIT}${holder ?? ""}`,
        type: "initial",
        position: { x: d.x, y: d.y },
        width: d.width,
        height: d.height,
        ...(holder ? { parentId: holder } : {}),
        selectable: false,
        draggable: false,
        focusable: false,
        data: {},
      });
    };
    dot(null, chart.rootInitial);
    for (const id of chart.order) {
      const s = chart.states.get(id)!;
      if (rep.get(id) === id && isOpen(s, collapsed)) dot(id, s.initialChild);
    }
    return out;
  }, [drawable, chart, rep, boxes, collapsed, selection, editing, active, linking, toggleCollapsed, rename, selectTransition]);

  const edges: FlowEdge[] = useMemo(() => {
    const out: FlowEdge[] = [];
    if (!drawable) return out;
    for (const e of visibleEdges(chart, rep)) {
      const a = absolute.get(e.source);
      const b = absolute.get(e.target);
      if (!a || !b) continue;
      out.push({
        id: e.id,
        type: "transition",
        source: e.source,
        target: e.target,
        sourceHandle: "r",
        targetHandle: "l",
        selected: selection.transition === e.transition,
        className: flash?.ids.has(e.transition) ? flashClass : undefined,
        ariaLabel: CHART_LABELS.transitionAria(e.label),
        zIndex: 1000,
        data: { from: a, to: b, transition: e.transition, label: e.label, gate: e.gate, badges: e.badges, onSelect: selectTransition },
      });
    }
    const initial = (holder: string | null, target: string | null) => {
      if (!target || rep.get(target) !== target || !boxes.has(target)) return;
      out.push({
        id: `${INIT}${holder ?? ""}`,
        type: "initial",
        source: `${INIT}${holder ?? ""}`,
        target,
        sourceHandle: "r",
        targetHandle: "l",
        selectable: false,
        focusable: false,
        zIndex: 1000,
        data: {},
      });
    };
    initial(null, chart.rootInitial);
    for (const id of chart.order) {
      const s = chart.states.get(id)!;
      if (rep.get(id) === id && isOpen(s, collapsed)) initial(id, s.initialChild);
    }
    return out;
  }, [drawable, chart, rep, absolute, boxes, collapsed, selection.transition, flash, flashClass, selectTransition]);

  // First paint (phase-3-design.md 4.5): from the tab's first render to the first frame that shows the states at their
  // places. Nodes exist only once the places are known (`drawable`), so the first state node in the page is placed;
  // the time is taken after that frame is painted.
  const [firstPaint, setFirstPaint] = useState<number | null>(null);
  useEffect(() => {
    if (firstPaint !== null || !drawable || !nodes.length) return;
    let frame = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const check = () => {
      if (!canvasRef.current?.querySelector(".react-flow__node-state")) {
        frame = requestAnimationFrame(check);
        return;
      }
      timer = setTimeout(() => {
        const ms = Math.round(performance.now() - mountedAt.current);
        perfRecord("chart:first-paint", ms, { states: chart.order.length });
        setFirstPaint(ms);
      }, 0);
    };
    frame = requestAnimationFrame(check);
    return () => {
      cancelAnimationFrame(frame);
      clearTimeout(timer);
    };
  }, [drawable, nodes.length, firstPaint, chart.order.length]);

  // A selected state hidden inside a collapsed container (selected on the States grid or in the explorer) would be
  // stuck: arrows start from a box that is not drawn and F2 renames nothing visible. When the selection becomes such a
  // state, its collapsed ancestors are expanded, as one follow-up of the diagram's draft (in memory without a diagram).
  const revealedFor = useRef<string | null>(null);
  useEffect(() => {
    if (!primary) {
      revealedFor.current = null;
      return;
    }
    if (!drawable || revealedFor.current === primary) return;
    revealedFor.current = primary;
    const hiding = collapsedAncestors(chart, collapsed, primary);
    if (!hiding.length) return;
    if (diagramId)
      drafts.edit(
        diagramId,
        (json) => {
          for (const id of hiding) setCollapsed(json as unknown as DiagramDoc, id, false);
        },
        { followUp: true, base: diagram.element.data },
      );
    else
      setMemory((prev) => {
        const base = prev.process === processId ? prev : { process: processId, placements: new Map<string, Placement>(), collapsed: new Set<string>() };
        return { ...base, collapsed: new Set([...base.collapsed].filter((id) => !hiding.includes(id))) };
      });
  }, [primary, drawable, chart, collapsed, diagramId, drafts, diagram.element.data, processId]);

  // The selection for assistive technology: the focused canvas names the drawn node or edge that is selected
  // (aria-activedescendant, set once that element is in the page, since React Flow draws nodes a frame after they are
  // given), and a polite live region says what was selected.
  const activeTarget = useMemo(() => {
    if (selection.transition) {
      const edge = edges.find((e) => e.type === "transition" && e.data?.transition === selection.transition);
      return edge ? edgeDomId(edge.id) : null;
    }
    return primary && rep.get(primary) === primary ? stateDomId(primary) : null;
  }, [selection.transition, edges, primary, rep]);
  const [activeDescendant, setActiveDescendant] = useState<string | undefined>(undefined);
  useEffect(() => {
    if (!activeTarget) {
      setActiveDescendant(undefined);
      return;
    }
    // React Flow draws a new node, or re-draws a selected edge in its own layer, a frame or two later: look for the
    // element for a few frames before naming it (a name that points at nothing is invalid).
    let frame = 0;
    let tries = 0;
    const look = () => {
      if (document.getElementById(activeTarget)) setActiveDescendant(activeTarget);
      else if (++tries < 30) frame = requestAnimationFrame(look);
      else setActiveDescendant(undefined);
    };
    frame = requestAnimationFrame(look);
    return () => cancelAnimationFrame(frame);
  }, [activeTarget]);
  const announcement = useMemo(() => {
    if (selection.transition) return CHART_LABELS.selectedTransition(chart.edges.find((e) => e.transition === selection.transition)?.label ?? "");
    if (selection.states.length > 1) return CHART_LABELS.selectedStates(selection.states.length);
    const s = primary ? chart.states.get(primary) : undefined;
    return s ? CHART_LABELS.selectedState(CHART_LABELS.stateTypes[s.type] ?? CHART_LABELS.stateTypes.atomic, s.label) : "";
  }, [selection, primary, chart]);

  // ------------------------------------------------------------------ changes from the canvas
  const onNodesChange = useCallback(
    (changes: NodeChange<FlowNode>[]) => {
      const selects = changes.filter((c) => c.type === "select" && !c.id.startsWith(INIT));
      if (selects.length) {
        const next = applySelectChanges(selection.states, selects);
        if (next) setSelection(next, null);
      }
      const done: Placements = new Map();
      setDragging((prev) => {
        let next = prev;
        for (const change of changes) {
          if (change.type !== "position" || change.id.startsWith(INIT)) continue;
          if (next === prev) next = { ...prev };
          // A state stays inside its container: never above its header or left of its edge.
          const inside = chart.states.get(change.id)?.parent ? { x: 2, y: 26 } : { x: -Infinity, y: -Infinity };
          if (change.position) next[change.id] = { x: Math.max(inside.x, Math.round(change.position.x)), y: Math.max(inside.y, Math.round(change.position.y)) };
          if (change.dragging === false) {
            const final = next[change.id] ?? prev[change.id];
            if (final) done.set(change.id, { x: final.x, y: final.y });
            delete next[change.id];
          }
        }
        return next;
      });
      queueMicrotask(() => {
        if (!done.size) return;
        // The containers that now draw larger save their new size with the move (sizes, not positions).
        const merged = new Map(placements);
        for (const [id, p] of done) merged.set(id, { ...merged.get(id), ...p });
        const grown = grownSizes(chart, merged, resolveBoxes(chart, merged, collapsed), done.keys());
        void savePlacements(new Map([...done, ...grown]));
      });
    },
    [selection.states, setSelection, chart, placements, collapsed, savePlacements],
  );

  const onEdgesChange = useCallback(
    (changes: EdgeChange<FlowEdge>[]) => {
      const picked = changes.find((c) => c.type === "select" && c.selected && !c.id.startsWith(INIT));
      if (picked && picked.type === "select") {
        const transition = chart.edges.find((e) => e.id === picked.id)?.transition;
        if (transition) setSelection([], transition);
      }
    },
    [chart.edges, setSelection],
  );

  // ------------------------------------------------------------------ commands
  const reveal = useCallback(
    (id: string) => {
      const b = absolute.get(id);
      const el = canvasRef.current;
      if (!b || !el) return;
      const { x, y, zoom } = flow.getViewport();
      const left = b.x * zoom + x;
      const top = b.y * zoom + y;
      if (left >= 0 && top >= 0 && left + b.width * zoom <= el.clientWidth && top + b.height * zoom <= el.clientHeight) return;
      programmatic();
      void flow.setCenter(b.x + b.width / 2, b.y + b.height / 2, { zoom, duration: 0 });
    },
    [absolute, flow, programmatic],
  );
  const selectState = useCallback(
    (id: string | null) => {
      setSelection(id ? [id] : [], null);
      if (id) reveal(id);
    },
    [setSelection, reveal],
  );

  const runLayout = useCallback(
    async (scope: string | null) => {
      if (layingOut || !chart.order.length) return;
      setLayingOut(true);
      try {
        const started = performance.now();
        const { placements: next, size } = await layoutChart(chart, collapsed, scope, defaultHierarchyEngine());
        const ms = Math.round(performance.now() - started);
        setLayoutMs(ms);
        perfRecord("chart:layout", ms, { states: chart.order.length, scope: scope ?? "chart" });
        if (scope && size) {
          const own = placements.get(scope) ?? { x: 0, y: 0 };
          next.set(scope, { ...own, ...size });
          const merged = new Map([...placements, ...next]);
          for (const [id, p] of grownSizes(chart, merged, resolveBoxes(chart, merged, collapsed), [scope])) next.set(id, p);
        }
        await savePlacements(next, true);
        if (!scope) {
          saveFit.current = true;
          setFitPending(true);
        }
      } finally {
        setLayingOut(false);
      }
    },
    [layingOut, chart, collapsed, placements, savePlacements],
  );
  /** Layout's scope: the selected open container, else the whole chart. */
  const layoutScope = primary && isOpen(chart.states.get(primary), collapsed) ? primary : null;

  const addNew = useCallback(
    (child: boolean) => {
      const created: string[] = [];
      if (child && !primary) return;
      pc.change((p) => void created.push(child ? addState(p, primary) : primary ? addSiblingState(p, primary) : addState(p, null)));
      if (created[0]) setSelection([created[0]], null);
    },
    [pc, primary, setSelection],
  );

  const removeTransition = useCallback(
    (transition: string) => {
      pc.change((p) => {
        p.transitions = (p.transitions ?? []).filter((t) => t.id !== transition);
        if (!p.transitions.length) delete p.transitions;
      });
      setSelection([], null);
    },
    [pc, setSelection],
  );

  const removeStates = useCallback(
    async (states: string[], alsoTransitions: string[]) => {
      const probe = structuredClone(process);
      const refusal = deleteStates(probe, states, alsoTransitions);
      if (refusal) {
        store.getState().notify(refusal, "error");
        return;
      }
      const removed = removedStates(process, states);
      const label = `Delete ${states.map((s) => chart.states.get(s)?.name ?? s).join(", ")}`;
      setSelection([], null);
      if (diagramId && (diagramJson?.members ?? []).some((m) => removed.has(m.element))) {
        const failed = await changeWithDiagram(services, {
          process: processId,
          diagram: diagramId,
          label,
          removed,
          apply: (p) => deleteStates(p, states, alsoTransitions),
        });
        if (failed) store.getState().notify(CHART_LABELS.notDeleted(failed), "error");
        return;
      }
      pc.change((p) => void deleteStates(p, states, alsoTransitions));
    },
    [process, store, chart, setSelection, diagramId, diagramJson, services, processId, pc],
  );

  const requestDelete = useCallback(() => {
    if (selection.transition) {
      removeTransition(selection.transition);
      return;
    }
    if (!selection.states.length) return;
    const incoming = incomingFromOutside(process, selection.states);
    if (incoming.length) setConfirm({ states: selection.states, incoming: incoming.map((t) => ({ id: t.id, label: transitionLabel(process, t) })) });
    else void removeStates(selection.states, []);
  }, [selection, process, removeTransition, removeStates]);

  /** F12 on a transition: its gate, else its guard, else its event, on the matching tab; else the Transitions tab. */
  const goToDefinition = useCallback(
    (transition: string) => {
      const t = chart.transitions.get(transition)?.transition;
      if (!t) return;
      if (t.gate) focusProcess(processId, "gates", null);
      else if (t.guard || !t.event) {
        selectProcessNode({ process: processId, kind: "transition", id: transition });
        focusProcess(processId, "transitions", null);
      } else focusProcess(processId, "events", t.event);
    },
    [chart.transitions, processId],
  );

  const startLink = useCallback(() => {
    if (primary) setLinking({ source: primary, target: primary });
  }, [primary]);

  const openMenuFor = useCallback((kind: MenuAt["kind"], id: string) => {
    const el = kind === "state" ? canvasRef.current?.querySelector(`[data-id="${id}"]`) : canvasRef.current?.querySelector(`[data-transition="${id}"]`);
    const r = el?.getBoundingClientRect();
    const c = canvasRef.current!.getBoundingClientRect();
    setMenu({ kind, id, x: r ? r.left + 16 : c.left + 24, y: r ? r.top + 20 : c.top + 24 });
  }, []);

  // ------------------------------------------------------------------ keyboard (6.3)
  const onKeyDown = (e: ReactKeyboardEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    if (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.isContentEditable) return;
    const mod = e.ctrlKey || e.metaKey;
    const key = e.key;
    const handled = () => {
      e.preventDefault();
      e.stopPropagation();
    };
    if (mod && key.toLowerCase() === "l" && !e.shiftKey && !e.altKey) {
      handled();
      void runLayout(layoutScope);
      return;
    }
    if (mod || e.altKey) return;
    if (linking) {
      const dir = directionOf(key);
      if (dir) {
        handled();
        const from = absolute.get(linking.target);
        if (!from) return;
        const candidates = [...absolute].filter(([id]) => rep.get(id) === id && id !== linking.target).map(([id, box]) => ({ id, box }));
        const next = nearestInDirection(from, candidates, dir);
        if (next) {
          setLinking({ ...linking, target: next });
          reveal(next);
        }
      } else if (key === "Enter") {
        handled();
        const { source, target: to } = linking;
        setLinking(null);
        if (source === to) return;
        const created: string[] = [];
        pc.change((p) => void created.push(linkStates(p, source, to)));
        if (created[0]) setSelection([], created[0]);
      } else if (key === "Escape") {
        handled();
        setLinking(null);
      }
      return;
    }
    const transition = selection.transition;
    const source = transition ? (chart.transitions.get(transition)?.transition.source ?? null) : null;
    const dir = directionOf(key);
    if (dir) {
      handled();
      const from = primary ?? source;
      if (!from) {
        selectState(chart.rootInitial ?? chart.roots[0] ?? null);
        return;
      }
      if (!primary) {
        selectState(from);
        return;
      }
      const next = moveSelection(chart, boxes, from, dir);
      if (next) selectState(next);
      return;
    }
    switch (key) {
      case "Enter": {
        if (!primary) return;
        handled();
        const inner = enterTarget(chart, primary, collapsed);
        if (inner) selectState(inner);
        return;
      }
      case "Escape": {
        if (!primary && !transition) return;
        handled();
        if (transition) selectState(source);
        else selectState(parentTarget(chart, primary!));
        return;
      }
      case "Tab": {
        const from = primary ?? source;
        if (!from) return;
        const list = outgoing(chart, from);
        if (!list.length) return;
        handled();
        const next = walk(list, transition, e.shiftKey ? -1 : 1);
        if (next) setSelection([], next);
        return;
      }
      case "F2":
        if (!primary) return;
        handled();
        setEditing(primary);
        return;
      case "Delete":
      case "Backspace":
        if (!primary && !transition) return;
        handled();
        requestDelete();
        return;
      case "F12":
        if (e.shiftKey) {
          // Where the selected state or transition is used (the References panel), not where the process is.
          const target = transition ?? primary;
          if (!target) return;
          handled();
          store.getState().showReferences(target);
          return;
        }
        if (!transition) return;
        handled();
        goToDefinition(transition);
        return;
      case "ContextMenu":
        if (!primary && !transition) return;
        handled();
        openMenuFor(transition ? "transition" : "state", transition ?? primary!);
        return;
      case "F10":
        if (!e.shiftKey || (!primary && !transition)) return;
        handled();
        openMenuFor(transition ? "transition" : "state", transition ?? primary!);
        return;
    }
    const lower = key.toLowerCase();
    if (lower === "n") {
      handled();
      addNew(e.shiftKey);
    } else if (lower === "t" && !e.shiftKey && primary) {
      handled();
      startLink();
    }
  };

  // ------------------------------------------------------------------ render
  const name = (process as { displayName?: string }).displayName || process.name;
  const menuState = menu?.kind === "state" ? chart.states.get(menu.id) : undefined;
  const menuTransition = menu?.kind === "transition" ? chart.transitions.get(menu.id)?.transition : undefined;
  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="statechart">
      <Toolbar label={CHART_LABELS.toolbar}>
        <Button
          size="sm"
          label={layoutScope ? CHART_LABELS.layoutState(chart.states.get(layoutScope)!.label) : CHART_LABELS.layoutChart}
          shortcut="Ctrl+L"
          onClick={() => void runLayout(layoutScope)}
          disabled={layingOut || !chart.order.length}
          data-testid="chart-layout"
        >
          <LayoutGrid /> {layingOut ? CHART_LABELS.layingOut : CHART_LABELS.layout}
        </Button>
        {!diagramId && !empty ? (
          <span className="text-11 text-secondary" data-testid="chart-not-saved">
            {CHART_LABELS.notSaved}
          </span>
        ) : null}
        <span className="ml-auto min-w-0 truncate text-11 text-secondary" data-testid="chart-hint">
          {linking ? CHART_LABELS.linking(chart.states.get(linking.source)?.label ?? "") : CHART_LABELS.hint}
        </span>
      </Toolbar>
      <div
        ref={canvasRef}
        className="relative min-h-0 flex-1 outline-none focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent"
        tabIndex={0}
        role="group"
        aria-label={CHART_LABELS.canvas(name)}
        aria-keyshortcuts="ArrowLeft ArrowRight ArrowUp ArrowDown Enter Escape Tab N T F2 Delete F12 Shift+F12 Control+L"
        aria-activedescendant={activeDescendant}
        onKeyDown={onKeyDown}
        data-testid="statechart-canvas"
        data-diagram={diagramId ?? undefined}
        data-layout-ms={layoutMs ?? undefined}
        data-first-paint-ms={firstPaint ?? undefined}
        data-state-count={chart.order.length}
        data-selected={selection.transition ?? primary ?? undefined}
      >
        <ChartMarkers />
        <p className="sr-only" aria-live="polite" data-testid="chart-announcement">
          {announcement}
        </p>
        {!ready || (empty && chart.order.length) ? <EmptyState title={CHART_LABELS.loading} className="absolute inset-0 z-10" /> : null}
        <ReactFlow<FlowNode, FlowEdge>
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          edgeTypes={edgeTypes}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          onPaneClick={() => {
            setSelection([], null);
            setMenu(null);
          }}
          onNodeClick={() => canvasRef.current?.focus({ preventScroll: true })}
          onEdgeClick={() => canvasRef.current?.focus({ preventScroll: true })}
          onNodeContextMenu={(e, node) => {
            if (node.type !== "state") return;
            e.preventDefault();
            if (!selection.states.includes(node.id)) setSelection([node.id], null);
            setMenu({ kind: "state", id: node.id, x: e.clientX, y: e.clientY });
          }}
          onEdgeContextMenu={(e, edge) => {
            const transition = chart.edges.find((x) => x.id === edge.id)?.transition;
            if (!transition) return;
            e.preventDefault();
            setSelection([], transition);
            setMenu({ kind: "transition", id: transition, x: e.clientX, y: e.clientY });
          }}
          onMoveEnd={onMoveEnd}
          minZoom={0.1}
          maxZoom={2}
          onlyRenderVisibleElements={nodes.length > 150}
          nodesConnectable={false}
          disableKeyboardA11y
          deleteKeyCode={null}
          multiSelectionKeyCode="Shift"
          selectionKeyCode="Shift"
          proOptions={{ hideAttribution: false }}
        >
          <Background color={cssColor("--mq-border-default")} gap={16} />
          <Controls showInteractive={false} />
        </ReactFlow>
      </div>
      <DropdownMenu open={!!menu} onOpenChange={(open) => !open && setMenu(null)} modal={false}>
        <DropdownMenuTrigger asChild>
          <span aria-hidden className="pointer-events-none fixed size-0" style={{ left: menu?.x ?? 0, top: menu?.y ?? 0 }} />
        </DropdownMenuTrigger>
        {menu ? (
          <DropdownMenuContent
            align="start"
            aria-label={CHART_LABELS.menu(menuState?.label ?? (menuTransition ? transitionLabel(process, menuTransition) : ""))}
            data-testid="chart-menu"
            onCloseAutoFocus={(e) => {
              e.preventDefault();
              canvasRef.current?.focus({ preventScroll: true });
            }}
          >
            {menuState ? (
              <>
                <DropdownMenuLabel>{menuState.label}</DropdownMenuLabel>
                <DropdownMenuItem onSelect={() => addNew(false)}>
                  {CHART_LABELS.addState}
                  <Shortcut>N</Shortcut>
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={() => addNew(true)}>
                  {CHART_LABELS.addChild}
                  <Shortcut>Shift+N</Shortcut>
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={startLink}>
                  {CHART_LABELS.addTransition}
                  <Shortcut>T</Shortcut>
                </DropdownMenuItem>
                <DropdownMenuItem onSelect={() => setEditing(menuState.id)}>
                  {CHART_LABELS.rename}
                  <Shortcut>F2</Shortcut>
                </DropdownMenuItem>
                {menuState.container && menuState.children.length ? (
                  <>
                    <DropdownMenuItem disabled={!isOpen(menuState, collapsed)} onSelect={() => void runLayout(menuState.id)}>
                      {CHART_LABELS.layoutInside}
                      <Shortcut>Ctrl+L</Shortcut>
                    </DropdownMenuItem>
                    <DropdownMenuItem onSelect={() => toggleCollapsed(menuState.id)}>
                      {collapsed.has(menuState.id) ? CHART_LABELS.expandItem : CHART_LABELS.collapseItem}
                    </DropdownMenuItem>
                  </>
                ) : null}
                <DropdownMenuSeparator />
                <DropdownMenuItem className="text-danger" onSelect={requestDelete}>
                  {CHART_LABELS.delete}
                  <Shortcut>Delete</Shortcut>
                </DropdownMenuItem>
              </>
            ) : menuTransition ? (
              <>
                <DropdownMenuLabel>{transitionLabel(process, menuTransition)}</DropdownMenuLabel>
                <DropdownMenuItem onSelect={() => goToDefinition(menuTransition.id)}>
                  {CHART_LABELS.goToDefinition}
                  <Shortcut>F12</Shortcut>
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem className="text-danger" onSelect={() => removeTransition(menuTransition.id)}>
                  {CHART_LABELS.delete}
                  <Shortcut>Delete</Shortcut>
                </DropdownMenuItem>
              </>
            ) : null}
          </DropdownMenuContent>
        ) : null}
      </DropdownMenu>
      {confirm ? (
        <Dialog open onOpenChange={(open) => !open && setConfirm(null)}>
          <DialogContent title={CHART_LABELS.deleteTitle(confirm.states.map((s) => chart.states.get(s)?.label ?? s).join(", "))}>
            <p className="text-12">{CHART_LABELS.deleteIncoming}</p>
            <ul className="flex flex-col gap-0.5 text-12" data-testid="delete-incoming">
              {confirm.incoming.map((t) => (
                <li key={t.id} className="font-mono text-11">
                  {t.label}
                </li>
              ))}
            </ul>
            <div className="flex justify-end gap-2" data-testid="delete-state-dialog">
              <Button size="sm" variant="ghost" onClick={() => setConfirm(null)}>
                {CHART_LABELS.cancel}
              </Button>
              <Button
                size="sm"
                variant="danger"
                onClick={() => {
                  const { states, incoming } = confirm;
                  setConfirm(null);
                  void removeStates(
                    states,
                    incoming.map((t) => t.id),
                  );
                  canvasRef.current?.focus({ preventScroll: true });
                }}
                data-testid="confirm-delete-state"
              >
                {CHART_LABELS.confirmDelete}
              </Button>
            </div>
          </DialogContent>
        </Dialog>
      ) : null}
    </div>
  );
}

function Shortcut({ children }: { children: string }) {
  return <span className="ml-auto pl-4 text-11 text-secondary">{children}</span>;
}
