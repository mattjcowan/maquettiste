// The statechart canvas's keyboard model (phase-3-design.md 6.3): arrows to the nearest state that way within the
// container, Enter into a container, Escape to the parent, Tab and Shift+Tab over the outgoing transitions.
import { describe, expect, it } from "vitest";
import { buildChart } from "@/canvas/statechart/chartModel";
import { directionOf, enterTarget, moveSelection, nearestInDirection, outgoing, parentTarget, siblings, walk } from "@/canvas/statechart/keyboard";
import type { Box } from "@/canvas/layout";
import { purchase } from "./statechart-fixture";

const box = (x: number, y: number, width = 100, height = 24): Box => ({ x, y, width, height });

describe("nearest in a direction", () => {
  const from = box(100, 100);
  const candidates = [
    { id: "ahead", box: box(300, 100) },
    { id: "near-but-off", box: box(220, 200) },
    { id: "behind", box: box(0, 100) },
    { id: "above", box: box(100, 0) },
    { id: "below", box: box(110, 160) },
  ];

  it("prefers a box straight ahead over a nearer one off to the side", () => {
    expect(nearestInDirection(from, candidates, "right")).toBe("ahead");
    expect(nearestInDirection(from, candidates, "left")).toBe("behind");
    expect(nearestInDirection(from, candidates, "up")).toBe("above");
    expect(nearestInDirection(from, candidates, "down")).toBe("below");
  });

  it("finds nothing at the edge", () => {
    expect(nearestInDirection(from, [{ id: "behind", box: box(0, 100) }], "right")).toBeNull();
    expect(nearestInDirection(from, [], "up")).toBeNull();
  });

  it("reads the arrow keys", () => {
    expect(["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Enter"].map(directionOf)).toEqual(["left", "right", "up", "down", null]);
  });
});

describe("the chart's keyboard", () => {
  const chart = buildChart(purchase);
  const boxes = new Map<string, Box>([
    ["draft", box(0, 0)],
    ["review", box(200, 0, 400, 200)],
    ["approval", box(700, 0)],
    ["hist", box(700, 100)],
    ["done", box(900, 0)],
    ["budget", box(12, 32, 180, 100)],
    ["compliance", box(208, 32, 180, 100)],
    ["checking", box(12, 32)],
    ["ok", box(140, 32)],
  ]);

  it("moves among the states of the same container only", () => {
    expect(siblings(chart, "review")).toEqual(["draft", "approval", "hist", "done"]);
    expect(siblings(chart, "checking")).toEqual(["ok"]);
    expect(moveSelection(chart, boxes, "draft", "right")).toBe("review");
    // Review is tall: Resume, level with its middle, is nearer than Approval at its top.
    expect(moveSelection(chart, boxes, "review", "right")).toBe("hist");
    expect(moveSelection(chart, boxes, "hist", "up")).toBe("approval");
    expect(moveSelection(chart, boxes, "approval", "down")).toBe("hist");
    expect(moveSelection(chart, boxes, "budget", "right")).toBe("compliance");
    // Checking's only sibling is to its right; Review's states are not candidates.
    expect(moveSelection(chart, boxes, "checking", "right")).toBe("ok");
    expect(moveSelection(chart, boxes, "checking", "left")).toBeNull();
  });

  it("enters a container at its initial child, a parallel state at its first region, and escapes to the parent", () => {
    expect(enterTarget(chart, "review", new Set())).toBe("budget");
    expect(enterTarget(chart, "budget", new Set())).toBe("checking");
    expect(enterTarget(chart, "compliance", new Set())).toBe("cleared");
    expect(enterTarget(chart, "draft", new Set())).toBeNull();
    expect(enterTarget(chart, "review", new Set(["review"]))).toBeNull();
    expect(parentTarget(chart, "checking")).toBe("budget");
    expect(parentTarget(chart, "budget")).toBe("review");
    expect(parentTarget(chart, "review")).toBeNull();
  });

  it("walks the outgoing transitions in document order, wrapping, internal ones included", () => {
    const list = outgoing(chart, "approval");
    expect(list).toEqual(["t4", "t5"]);
    expect(walk(list, null, 1)).toBe("t4");
    expect(walk(list, null, -1)).toBe("t5");
    expect(walk(list, "t4", 1)).toBe("t5");
    expect(walk(list, "t5", 1)).toBe("t4");
    expect(walk(list, "t4", -1)).toBe("t5");
    expect(walk([], null, 1)).toBeNull();
    expect(outgoing(chart, "pending")).toEqual(["t6"]);
  });
});
