// The statechart canvas's model (phase-3-design.md 6.3), kept free of React so it can be unit tested: the state tree
// as the canvas draws it (containers, regions, pseudo-states, the enum member a bound root state stands for, the
// problem badges), one edge per transition target with its label (`event [guard] / actions`, `after 30d`, `done`,
// `always`), the gate badge ("2 of 3" with the signers' names), and what a collapsed container hides.
import type { Diagnostic } from "@/api/types";
import { CHART_LABELS } from "@/model/labels";
import { boundStates, initialOf, stateIndex, type ProcessDoc, type StateType, type TransitionDoc, type Trigger } from "@/model/process";

/** The rules whose findings the canvas marks on states and edges (the others are listed by the Problems panel). */
export const CHART_RULES: ReadonlySet<string> = new Set(["MQ9003", "MQ9004", "MQ9006"]);

export interface ChartBadge {
  rule: string;
  message: string;
}

/** A transition without a target (an internal transition): drawn as a row inside its source state. */
export interface ChartInternal {
  transition: string;
  label: string;
  badges: ChartBadge[];
}

export interface ChartState {
  id: string;
  name: string;
  /** The display name, else the name. */
  label: string;
  type: StateType;
  parent: string | null;
  depth: number;
  /** JSON pointer in the process document. */
  pointer: string;
  children: string[];
  /** A compound or parallel state (or any state with children): drawn as a box holding its children. */
  container: boolean;
  /** A child of a parallel state: one of its regions, laid side by side. */
  region: boolean;
  /** The child the initial arrow points to (containers that are not parallel); null otherwise. */
  initialChild: string | null;
  /** A history state's depth: H or H*. */
  deep: boolean;
  /** The enum member a lifecycle's root state stands for (null for the others). */
  bound: string | null;
  internal: ChartInternal[];
  badges: ChartBadge[];
}

export interface GateBadge {
  /** "2 of 3": signatures required of the signers. */
  text: string;
  required: number;
  of: number;
  signers: string[];
  /** The tooltip: the gate's name and its signers. */
  title: string;
}

export interface ChartEdge {
  /** Unique per drawn edge: the transition id, then `#<n>` for its second and later targets. */
  id: string;
  transition: string;
  source: string;
  target: string;
  label: string;
  trigger: Trigger;
  gate: GateBadge | null;
  badges: ChartBadge[];
}

export interface Chart {
  states: Map<string, ChartState>;
  /** Every state in document order (a parent before its children). */
  order: string[];
  roots: string[];
  /** The process's initial root state. */
  rootInitial: string | null;
  edges: ChartEdge[];
  /** Transitions by id with their document index. */
  transitions: Map<string, { transition: TransitionDoc; index: number }>;
}

export interface ChartInput {
  /** The bound enum's member names, for a lifecycle (null when there is none or it is not loaded). */
  members?: readonly string[] | null;
  problems?: readonly Diagnostic[];
  /** Actor names by id, for the gate badge's tooltip. */
  actorNames?: ReadonlyMap<string, string>;
}

const SHORT_UNITS: Record<string, string> = { Y: "y", M: "mo", W: "w", D: "d", H: "h", TM: "m", S: "s" };

// The engine gives templates the same text (Resolution/ResolveRun.Processes.cs, ProcessText): change both together;
// statechart-label-parity.test.ts compares them on the gate 3 fixture.
/** An ISO 8601 duration written short: `P30D` → `30d`, `PT1H30M` → `1h 30m`, `P1M` → `1mo`; anything else as given. */
export function shortDuration(iso: string | undefined): string {
  if (!iso) return "";
  const m =
    /^P(?:(\d+(?:\.\d+)?)Y)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)W)?(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$/.exec(
      iso.trim(),
    );
  if (!m || iso.trim() === "P" || iso.trim().endsWith("T")) return iso;
  const parts = ["Y", "M", "W", "D", "H", "TM", "S"].flatMap((unit, i) => (m[i + 1] ? [`${m[i + 1]}${SHORT_UNITS[unit]}`] : []));
  return parts.length ? parts.join(" ") : iso;
}

interface Names {
  events: Map<string, string>;
  guards: Map<string, string>;
  actions: Map<string, string>;
  invokes: Map<string, string>;
}

function namesOf(process: ProcessDoc): Names {
  const invokes = new Map<string, string>();
  for (const { state } of stateIndex(process).values()) for (const i of state.invoke ?? []) invokes.set(i.id, i.name);
  const map = (list: { id: string; name: string }[] | undefined) => new Map((list ?? []).map((x) => [x.id, x.name]));
  return { events: map(process.events), guards: map(process.guards), actions: map(process.actions), invokes };
}

/** A transition's label: `event [guard] / actions`, `after 30d`, `done`, `always`, `done: invoke` or `error: invoke`. */
export function edgeLabel(process: ProcessDoc, t: TransitionDoc, names: Names = namesOf(process)): string {
  const trigger = t.trigger ?? "event";
  const name = (map: Map<string, string>, id: string | undefined) => (id ? (map.get(id) ?? "?") : "?");
  const on =
    trigger === "event"
      ? name(names.events, t.event)
      : trigger === "after"
        ? CHART_LABELS.afterWord(shortDuration(t.after))
        : trigger === "invoke-done"
          ? CHART_LABELS.doneOf(name(names.invokes, t.invoke))
          : trigger === "invoke-error"
            ? CHART_LABELS.errorOf(name(names.invokes, t.invoke))
            : trigger;
  const guard = t.guard ? ` [${name(names.guards, t.guard)}]` : "";
  const actions = t.actions?.length ? ` / ${t.actions.map((a) => name(names.actions, a)).join(", ")}` : "";
  return `${on}${guard}${actions}`;
}

/** The gate badge of a gated transition: N of M signers, with their names in the tooltip. */
export function gateBadge(t: TransitionDoc, actorNames: ReadonlyMap<string, string> = new Map()): GateBadge | null {
  const gate = t.gate;
  if (!gate) return null;
  const signers = (gate.signers ?? []).map((a) => actorNames.get(a) ?? a);
  const required = gate.required ?? 1;
  const of = signers.length;
  const name = gate.displayName || gate.name;
  return { text: `${required} of ${of}`, required, of, signers, title: CHART_LABELS.gateTitle(name, required, of, signers.join(", ")) };
}

/** The canvas's findings at a pointer: the chart rules' diagnostics at it or its fields, not its nested states. */
export function badgesAt(problems: readonly Diagnostic[], pointer: string): ChartBadge[] {
  return problems
    .filter((d) => {
      if (!CHART_RULES.has(d.rule)) return false;
      const p = d.jsonPointer ?? "";
      return p === pointer || (p.startsWith(`${pointer}/`) && !p.startsWith(`${pointer}/states/`));
    })
    .map((d) => ({ rule: d.rule, message: d.message }));
}

/** The chart of a process document. */
export function buildChart(process: ProcessDoc, input: ChartInput = {}): Chart {
  const problems = input.problems ?? [];
  const index = stateIndex(process);
  const names = namesOf(process);
  const bound = new Map<string, string>();
  if (process.use === "lifecycle" && input.members) boundStates(process).forEach((s, i) => bound.set(s.id, input.members?.[i] ?? ""));

  const states = new Map<string, ChartState>();
  const order: string[] = [];
  for (const [id, info] of index) {
    const s = info.state;
    const type = s.type ?? "atomic";
    const children = (s.states ?? []).map((c) => c.id);
    const container = type === "compound" || type === "parallel" || children.length > 0;
    const parentType = info.parent ? (info.parent.type ?? "atomic") : null;
    states.set(id, {
      id,
      name: s.name,
      label: s.displayName || s.name,
      type,
      parent: info.parent?.id ?? null,
      depth: info.depth,
      pointer: info.pointer,
      children,
      container,
      region: parentType === "parallel",
      initialChild: container && type !== "parallel" && children.length ? (initialOf(s) ?? null) : null,
      deep: type === "history" && s.history === "deep",
      bound: info.parent ? null : (bound.get(id) ?? null),
      internal: [],
      badges: badgesAt(problems, info.pointer),
    });
    order.push(id);
  }

  const edges: ChartEdge[] = [];
  const transitions = new Map<string, { transition: TransitionDoc; index: number }>();
  (process.transitions ?? []).forEach((t, i) => {
    transitions.set(t.id, { transition: t, index: i });
    const label = edgeLabel(process, t, names);
    const badges = badgesAt(problems, `/transitions/${i}`);
    const targets = (t.targets ?? []).filter((x) => states.has(x));
    const source = states.get(t.source);
    if (!source) return;
    if (!targets.length) {
      source.internal.push({ transition: t.id, label, badges });
      return;
    }
    const gate = gateBadge(t, input.actorNames);
    targets.forEach((target, n) =>
      edges.push({ id: n ? `${t.id}#${n}` : t.id, transition: t.id, source: t.source, target, label, trigger: t.trigger ?? "event", gate, badges }),
    );
  });

  const roots = process.states.map((s) => s.id);
  return { states, order, roots, rootInitial: initialOf(process) ?? null, edges, transitions };
}

/** The ids of a state's descendants (not the state itself), in document order. */
export function descendants(chart: Chart, id: string): string[] {
  const out: string[] = [];
  const walk = (s: string) => {
    for (const c of chart.states.get(s)?.children ?? []) {
      out.push(c);
      walk(c);
    }
  };
  walk(id);
  return out;
}

/** The collapsed containers that hide a state (its collapsed ancestors, nearest first); empty when it is drawn. */
export function collapsedAncestors(chart: Chart, collapsed: ReadonlySet<string>, id: string): string[] {
  return ancestors(chart, id).filter((a) => collapsed.has(a));
}

/** The ancestors of a state, nearest first. */
export function ancestors(chart: Chart, id: string): string[] {
  const out: string[] = [];
  let p = chart.states.get(id)?.parent ?? null;
  while (p) {
    out.push(p);
    p = chart.states.get(p)?.parent ?? null;
  }
  return out;
}

/**
 * The state that stands for each state on the canvas: itself, or the outermost collapsed container above it (a
 * collapsed container hides every state inside it). A state is visible when it stands for itself.
 */
export function representatives(chart: Chart, collapsed: ReadonlySet<string>): Map<string, string> {
  const rep = new Map<string, string>();
  for (const id of chart.order) {
    const parent = chart.states.get(id)!.parent;
    if (!parent) {
      rep.set(id, id);
      continue;
    }
    const up = rep.get(parent)!;
    // Inside a hidden state, or directly inside a collapsed one: the collapsed container stands for it.
    rep.set(id, up !== parent ? up : collapsed.has(parent) ? parent : id);
  }
  return rep;
}

/** The edges the canvas draws: ends moved to the visible state standing for them; an edge inside one collapsed box is dropped. */
export function visibleEdges(chart: Chart, rep: ReadonlyMap<string, string>): ChartEdge[] {
  const out: ChartEdge[] = [];
  for (const e of chart.edges) {
    const source = rep.get(e.source) ?? e.source;
    const target = rep.get(e.target) ?? e.target;
    if (source === target && (source !== e.source || target !== e.target)) continue;
    out.push(source === e.source && target === e.target ? e : { ...e, source, target });
  }
  return out;
}

/** The active states a simulation configuration highlights: the atomic states and every ancestor. */
export function activeStates(chart: Chart, configuration: readonly string[]): Set<string> {
  const out = new Set<string>();
  for (const id of configuration) {
    if (!chart.states.has(id)) continue;
    out.add(id);
    for (const a of ancestors(chart, id)) out.add(a);
  }
  return out;
}
