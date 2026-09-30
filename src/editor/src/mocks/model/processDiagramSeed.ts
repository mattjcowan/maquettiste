// The mock model's process diagrams (phase-3-design.md 2.7 and 6.5, round P4a). PurchaseApproval's statechart is
// saved with its states placed (positions relative to the parent state, sizes on the containers), so a spec can prove
// that opening, editing and reloading leave saved positions where they are. The `chart400` scenario (`?mock=chart400`)
// adds LargeChart, about 400 states nested three deep with parallel regions and a few hundred transitions, with a
// diagram whose places the editor's own placement computes, for the canvas's budgets (4.5: layout of 400 nested
// states in the worker, first paint of a 400-state chart).
import { buildChart } from "@/canvas/statechart/chartModel";
import { INITIAL_ROOM, placeMissing, resolveBoxes } from "@/canvas/statechart/chartLayout";
import { newProcessDiagram } from "@/canvas/statechart/diagram";
import type { ProcessDoc, StateDoc, TransitionDoc } from "@/model/process";
import { BILLING, PURCHASE_APPROVAL } from "./processSeed";
import type { Seed, SeedFile } from "./store";

type Json = Record<string, unknown>;

export const PURCHASE_APPROVAL_DIAGRAM = "01JQDGM000000000000000000" + "1";
export const LARGE_CHART = "01JQBGP000000000000000000" + "1";
export const LARGE_CHART_DIAGRAM = "01JQDGM000000000000000000" + "2";

const S = (n: number) => `01JQSTA0000000000000000${String(n).padStart(3, "0")}`;
/** Ids of LargeChart's nodes: a prefix and a counter, always 26 characters. */
const L = (prefix: string, n: number) => `${prefix}${String(n).padStart(26 - prefix.length, "0")}`;

const file = (path: string, json: Json): SeedFile => ({ path, text: JSON.stringify(json, null, 2) + "\n" });

/** PurchaseApproval's statechart as arranged by hand: Drafting, the parallel Review with its two regions, Approval, then the ends. */
const purchaseApprovalDiagram: Json = {
  kind: "diagram",
  id: PURCHASE_APPROVAL_DIAGRAM,
  name: "PurchaseApproval",
  displayName: "Purchase approval",
  package: BILLING,
  process: PURCHASE_APPROVAL,
  members: [
    { element: S(101), x: 40, y: 100 },
    { element: S(102), x: 190, y: 20, width: 777, height: 160 },
    { element: S(103), x: 12, y: 32, width: 362, height: 116 },
    { element: S(104), x: 48, y: 32 },
    { element: S(105), x: 260, y: 32 },
    { element: S(106), x: 260, y: 80 },
    { element: S(107), x: 390, y: 32, width: 375, height: 116 },
    { element: S(108), x: 48, y: 32 },
    { element: S(109), x: 280, y: 32 },
    { element: S(110), x: 1070, y: 90 },
    { element: S(111), x: 1350, y: 60 },
    { element: S(112), x: 1630, y: 60 },
    { element: S(113), x: 1350, y: 160 },
  ],
};

/** The documents the billing seed gains (seed.ts). */
export function processDiagramFiles(): SeedFile[] {
  return [file("model/diagrams/purchase-approval.json", purchaseApprovalDiagram)];
}

/**
 * LargeChart: Start, ten stages (each a compound of three steps of eight states and a parallel Checks state with two
 * regions of four), an Archive of seven and a final Done: 400 states, 321 transitions on 20 events.
 */
export function largeChart(): ProcessDoc {
  let s = 0;
  let t = 0;
  const state = (name: string, extra: Partial<StateDoc> = {}): StateDoc => ({ id: L("01JQBGS", ++s), name, ...extra });
  const events = Array.from({ length: 20 }, (_, i) => ({ id: L("01JQBGE", i + 1), name: `event${i + 1}` }));
  const transitions: TransitionDoc[] = [];
  const on = (source: string, target: string, n: number) => transitions.push({ id: L("01JQBGT", ++t), source, event: events[n % 20].id, targets: [target] });
  const chain = (list: StateDoc[], n: number) => list.forEach((x, i) => i && on(list[i - 1].id, x.id, n + i));

  const start = state("Start");
  const stages: StateDoc[] = [];
  for (let i = 0; i < 10; i++) {
    const steps = ["A", "B", "C"].map((letter, j) => {
      const items = Array.from({ length: 8 }, (_, k) => state(`Item${i}${letter}${k}`));
      chain(items, i + j);
      return state(`Step${letter}`, { type: "compound", states: items });
    });
    const regions = ["Budget", "Legal"].map((name, j) => {
      const items = Array.from({ length: 4 }, (_, k) => state(`${name}Check${k}`));
      chain(items, i + j + 5);
      return state(name, { type: "compound", states: items });
    });
    const checks = state("Checks", { type: "parallel", states: regions });
    // Each step's last state leaves for the next step (a transition that crosses containers), the last for the checks.
    steps.forEach((step, j) => on(step.states![7].id, (steps[j + 1] ?? checks).id, i + j + 9));
    stages.push(state(`Stage${i}`, { type: "compound", states: [...steps, checks] }));
  }
  const archived = Array.from({ length: 7 }, (_, k) => state(`Archived${k}`));
  chain(archived, 3);
  const archive = state("Archive", { type: "compound", states: archived });
  const done = state("Done", { type: "final" });

  on(start.id, stages[0].id, 0);
  stages.forEach((stage, i) => {
    if (i < 9) transitions.push({ id: L("01JQBGT", ++t), source: stage.id, trigger: "done", targets: [stages[i + 1].id] });
    if (i % 3 === 0) transitions.push({ id: L("01JQBGT", ++t), source: stage.id, trigger: "after", after: "P30D", targets: [archive.id] });
  });
  on(stages[9].id, done.id, 1);

  return {
    kind: "process",
    id: LARGE_CHART,
    name: "LargeChart",
    displayName: "Large chart",
    package: BILLING,
    events,
    states: [start, ...stages, archive, done],
    transitions,
  } as ProcessDoc;
}

/**
 * LargeChart's diagram: every state placed by the canvas's own placement (each container's states beside the ones they
 * have transitions with), the root states then stacked in one column, and a view that shows the whole chart in about
 * 1,100 by 700 pixels (so the first paint draws every state).
 */
export function largeChartDiagram(process: ProcessDoc): Json {
  const chart = buildChart(process);
  const placements = placeMissing(chart, new Map());
  const boxes = resolveBoxes(chart, placements, new Set());
  let y = 0;
  let width = 0;
  for (const id of chart.roots) {
    const b = boxes.get(id)!;
    placements.set(id, { ...placements.get(id)!, x: INITIAL_ROOM, y });
    y += b.height + 32;
    width = Math.max(width, INITIAL_ROOM + b.width);
  }
  const zoom = Math.floor(Math.min(1100 / width, 700 / y) * 1000) / 1000;
  return newProcessDiagram({
    id: LARGE_CHART_DIAGRAM,
    process: { id: process.id, name: process.name, package: process.package },
    order: chart.order,
    placements,
    viewport: { x: 8, y: 8, zoom },
  }) as unknown as Json;
}

/** The `chart400` scenario: LargeChart and its diagram join the model. */
export function withLargeChart(seed: Seed): Seed {
  const process = largeChart();
  return {
    ...seed,
    files: [
      ...seed.files,
      file("model/processes/large-chart.json", process as unknown as Json),
      file("model/diagrams/large-chart.json", largeChartDiagram(process)),
    ],
  };
}
