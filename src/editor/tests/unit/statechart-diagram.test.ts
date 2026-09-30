// The process diagram's members (phase-3-design.md 2.7): positions relative to the parent, sizes on containers,
// collapsed, the new diagram's shape, and finding a process's diagram from the index rows.
import { describe, expect, it } from "vitest";
import type { DiagramDoc } from "@/api/types";
import {
  collapsedStates,
  isProcessDiagram,
  newProcessDiagram,
  processDiagramId,
  removeMembers,
  savedPlacements,
  setCollapsed,
  writePlacements,
  type Placement,
} from "@/canvas/statechart/diagram";

const doc = (): DiagramDoc =>
  ({
    kind: "diagram",
    id: "D",
    name: "PurchaseApproval",
    process: "P",
    members: [
      { element: "review", x: 200, y: 0, width: 400, height: 120, collapsed: true },
      { element: "budget", x: 12, y: 32 },
      { element: "unplaced" },
      { element: "not-a-state", x: 5, y: 5 },
    ],
  }) as DiagramDoc;

describe("process diagrams", () => {
  it("finds a process's diagram by its index row (the first by id) and tells process diagrams apart", () => {
    const rows = [
      { id: "D2", kind: "diagram", process: "P" },
      { id: "D1", kind: "diagram", process: "P" },
      { id: "D0", kind: "diagram" },
      { id: "S", kind: "scenario", process: "P" },
    ];
    expect(processDiagramId(rows, "P")).toBe("D1");
    expect(processDiagramId(rows, "Q")).toBeNull();
    expect(isProcessDiagram(rows[0])).toBe(true);
    expect(isProcessDiagram(rows[2])).toBe(false);
    expect(isProcessDiagram(rows[3])).toBe(false);
  });

  it("reads placements of the process's states (relative positions, container sizes) and the collapsed ones", () => {
    const placed = savedPlacements(doc(), new Set(["review", "budget", "unplaced"]));
    expect([...placed.entries()]).toEqual([
      ["review", { x: 200, y: 0, width: 400, height: 120 }],
      ["budget", { x: 12, y: 32 }],
    ]);
    expect([...collapsedStates(doc())]).toEqual(["review"]);
  });

  it("writes placements rounded in the schema's key order, adds members, keeps collapsed and sizes it was not given", () => {
    const d = doc();
    const changed = writePlacements(
      d,
      new Map<string, Placement>([
        ["review", { x: 210.4, y: 0 }],
        ["budget", { x: 12, y: 32 }],
        ["fresh", { x: 1.6, y: 2, width: 90.2, height: 40 }],
      ]),
    );
    expect(changed).toBe(2);
    expect(d.members![0]).toEqual({ element: "review", x: 210, y: 0, width: 400, height: 120, collapsed: true });
    expect(Object.keys(d.members![0])).toEqual(["element", "x", "y", "width", "height", "collapsed"]);
    expect(d.members![4]).toEqual({ element: "fresh", x: 2, y: 2, width: 90, height: 40 });
  });

  it("collapses and expands a container, and removes deleted states' members", () => {
    const d = doc();
    expect(setCollapsed(d, "review", true)).toBe(false);
    expect(setCollapsed(d, "review", false)).toBe(true);
    expect(d.members![0].collapsed).toBeUndefined();
    expect(setCollapsed(d, "budget", true)).toBe(true);
    expect(setCollapsed(d, "other", true, { x: 3, y: 4, width: 50, height: 60 })).toBe(true);
    expect(d.members!.at(-1)).toEqual({ element: "other", x: 3, y: 4, width: 50, height: 60, collapsed: true });
    expect(setCollapsed(d, "missing", false)).toBe(false);
    expect(removeMembers(d, new Set(["budget", "other"]))).toBe(2);
    expect(d.members!.map((m) => m.element)).toEqual(["review", "unplaced", "not-a-state"]);
  });

  it("creates the diagram named after the process, in its domain, every state in document order", () => {
    const d = newProcessDiagram({
      id: "N",
      process: { id: "P", name: "PurchaseApproval", displayName: "Purchase approval", package: "BILLING" },
      order: ["a", "b", "c"],
      placements: new Map([
        ["a", { x: 0, y: 0 }],
        ["b", { x: 10, y: 20, width: 300, height: 100 }],
      ]),
      collapsed: new Set(["b"]),
      viewport: { x: 1, y: 2, zoom: 0.5 },
    });
    expect(d).toEqual({
      kind: "diagram",
      id: "N",
      name: "PurchaseApproval",
      displayName: "Purchase approval",
      package: "BILLING",
      process: "P",
      members: [{ element: "a", x: 0, y: 0 }, { element: "b", x: 10, y: 20, width: 300, height: 100, collapsed: true }, { element: "c" }],
      viewport: { x: 1, y: 2, zoom: 0.5 },
    });
  });
});
