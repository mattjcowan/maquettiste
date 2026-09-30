// Incremental placement, the saved viewport and a domain's own diagram (src/canvas/placement.ts, domainDiagram.ts).
import { describe, expect, it } from "vitest";
import { PLACEMENT_GAP, placeNodes, roundViewport, sameViewport, savedViewport, type PlacedBox } from "@/canvas/placement";
import { diagramsInDomain, domainDiagramId, domainOfDiagram, newDomainDiagram, syncDomainMembers } from "@/canvas/domainDiagram";
import { viewportOnly } from "@/state/drafts";
import type { ModelJson } from "@/api/types";

const box = (id: string, x: number, y: number, width = 240, height = 120): PlacedBox => ({ id, x, y, width, height });
const overlap = (a: PlacedBox, b: PlacedBox) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;

describe("placeNodes", () => {
  it("puts a new card to the right of its related card, one layer gap away", () => {
    const placed = [box("a", 0, 0), box("b", 0, 400)];
    const at = placeNodes({ placed, unplaced: [{ id: "n", width: 240, height: 100 }], edges: [{ source: "b", target: "n" }] });
    expect(at).toEqual({ n: { x: 240 + PLACEMENT_GAP.x, y: 400 } });
  });

  it("tries below, left and above when the right is taken, never overlapping a placed card", () => {
    const placed = [box("a", 0, 0), box("r", 336, 0)];
    const at = placeNodes({ placed, unplaced: [{ id: "n", width: 240, height: 120 }], edges: [{ source: "n", target: "a" }] });
    expect(at.n).toEqual({ x: 0, y: 120 + PLACEMENT_GAP.y });
    const busy = [box("a", 0, 0), box("r", 336, 0), box("d", 0, 168), box("l", -336, 0), box("u", 0, -168)];
    const next = placeNodes({ placed: busy, unplaced: [{ id: "n", width: 240, height: 120 }], edges: [{ source: "a", target: "n" }] });
    const placedBox = box("n", next.n.x, next.n.y);
    for (const b of busy) expect(overlap(placedBox, b)).toBe(false);
  });

  it("lays unrelated cards in rows under the drawing, left-aligned, and moves nothing that was placed", () => {
    const placed = [box("a", 100, 50), box("b", 500, 50, 240, 200)];
    const before = JSON.parse(JSON.stringify(placed));
    const at = placeNodes({
      placed,
      unplaced: ["n1", "n2", "n3", "n4", "n5"].map((id) => ({ id, width: 240, height: 120 })),
      edges: [],
    });
    const top = 250 + PLACEMENT_GAP.y;
    expect(at.n1).toEqual({ x: 100, y: top });
    expect(at.n2).toEqual({ x: 100 + 240 + PLACEMENT_GAP.x, y: top });
    // Rows are at least four cards wide; the fifth wraps.
    expect(at.n4.y).toBe(top);
    expect(at.n5).toEqual({ x: 100, y: top + 120 + PLACEMENT_GAP.y });
    expect(placed).toEqual(before);
    expect(Object.keys(at)).toEqual(["n1", "n2", "n3", "n4", "n5"]);
  });

  it("places a card related to another new card beside it", () => {
    const at = placeNodes({
      placed: [box("a", 0, 0)],
      unplaced: [
        { id: "n1", width: 240, height: 120 },
        { id: "n2", width: 240, height: 120 },
      ],
      edges: [{ source: "n1", target: "n2" }],
    });
    expect(at.n1).toEqual({ x: 0, y: 120 + PLACEMENT_GAP.y });
    expect(at.n2).toEqual({ x: at.n1.x + 240 + PLACEMENT_GAP.x, y: at.n1.y });
  });

  it("no placed card: starts at the origin", () => {
    expect(placeNodes({ placed: [], unplaced: [{ id: "n", width: 10.4, height: 10 }], edges: [] })).toEqual({ n: { x: 0, y: 0 } });
  });
});

describe("viewport", () => {
  it("rounds to three decimals and compares rounded", () => {
    expect(roundViewport({ x: 12.34567, y: -0.00001, zoom: 0.8999999 })).toEqual({ x: 12.346, y: 0, zoom: 0.9 });
    expect(sameViewport({ x: 1.0001, y: 2, zoom: 1 }, { x: 1.0002, y: 2, zoom: 1 })).toBe(true);
    expect(sameViewport({ x: 1, y: 2, zoom: 1 }, { x: 1, y: 2, zoom: 1.5 })).toBe(false);
    expect(sameViewport(null, { x: 1, y: 2, zoom: 1 })).toBe(false);
  });

  it("a diagram save that changes only the viewport is view state (it joins the last undo step)", () => {
    const d = (extra: object) => ({ kind: "diagram", id: "d", name: "D", members: [{ element: "a", x: 1, y: 2 }], ...extra }) as unknown as ModelJson;
    expect(viewportOnly(d({ viewport: { x: 0, y: 0, zoom: 1 } }), d({ viewport: { x: 5, y: 0, zoom: 2 } }))).toBe(true);
    expect(viewportOnly(d({}), d({ viewport: { x: 5, y: 0, zoom: 2 } }))).toBe(true);
    expect(viewportOnly(d({}), d({ members: [] }))).toBe(false);
    expect(viewportOnly(null, d({}))).toBe(false);
  });

  it("restores only a viewport with x, y and a positive zoom", () => {
    expect(savedViewport({ x: 1, y: 2, zoom: 0.5 })).toEqual({ x: 1, y: 2, zoom: 0.5 });
    expect(savedViewport({ zoom: 0.9 })).toBeNull();
    expect(savedViewport({ x: 1, y: 2, zoom: 0 })).toBeNull();
    expect(savedViewport(undefined)).toBeNull();
  });
});

describe("a domain's diagram", () => {
  const rows = [
    { id: "P", kind: "package", name: "Billing" },
    { id: "Q", kind: "package", name: "Catalog" },
    { id: "e1", kind: "entity", name: "Invoice", package: "P" },
    { id: "e2", kind: "entity", name: "Payment", package: "P" },
    { id: "e3", kind: "entity", name: "Product", package: "Q" },
    { id: "r1", kind: "relation", name: "pays", package: "P", ends: [{ entity: "e1" }, { entity: "e2" }] },
    { id: "r2", kind: "relation", name: "sells", package: "P", ends: [{ entity: "e1" }, { entity: "e3" }] },
    { id: "d1", kind: "diagram", name: "Billing overview", package: "P" },
  ];

  it("is the domain's diagram whose membership is package, the first in ordinal id order, never found by name", () => {
    const withDiagrams = [...rows, { id: "d3", kind: "diagram", name: "Billing", package: "P" }, { id: "d2", kind: "diagram", name: "Other", package: "P" }];
    expect(diagramsInDomain(withDiagrams, "P")).toEqual(["d1", "d2", "d3"]);
    expect(diagramsInDomain(withDiagrams, "Q")).toEqual([]);
    // Named after the domain but explicit: not the domain's diagram.
    expect(
      domainDiagramId(
        [
          { id: "d3", package: "P" },
          { id: "d1", package: "P", membership: "explicit" },
        ],
        "P",
      ),
    ).toBeNull();
    const docs = [
      { id: "d3", package: "P", membership: "package" as const },
      { id: "d2", package: "P", membership: "package" as const },
      { id: "d1", package: "P" },
    ];
    expect(domainDiagramId(docs, "P")).toBe("d2");
    expect(domainDiagramId(docs, "Q")).toBeNull();
    expect(domainOfDiagram({ package: "P", membership: "package" })).toBe("P");
    expect(domainOfDiagram({ package: "P" })).toBeNull();
    expect(domainOfDiagram({ package: "P", membership: "explicit" })).toBeNull();
    expect(domainOfDiagram({ membership: "package" })).toBeNull();
  });

  it("is created with the domain's entities at their positions, the relationships between them and the viewport", () => {
    const doc = newDomainDiagram({
      id: "D",
      domain: { id: "P", name: "Billing", displayName: "Billing and payments" },
      rows,
      positions: { e1: { x: 10.4, y: 20.6 } },
      viewport: { x: 1, y: 2, zoom: 0.5 },
    });
    expect(doc).toEqual({
      kind: "diagram",
      id: "D",
      name: "Billing",
      displayName: "Billing and payments",
      package: "P",
      membership: "package",
      members: [{ element: "e1", x: 10, y: 21 }, { element: "e2" }, { element: "r1" }],
      viewport: { x: 1, y: 2, zoom: 0.5 },
    });
  });

  it("follows the domain: joined entities come last without a position, those that left drop out", () => {
    const members = [{ element: "e1", x: 5, y: 6, collapsed: true }, { element: "e2", x: 1, y: 1 }, { element: "r1" }];
    expect(syncDomainMembers(members, rows, "P")).toBeNull();
    const moved = rows.map((r) => (r.id === "e2" ? { ...r, package: "Q" } : r.id === "e3" ? { ...r, package: "P" } : r));
    expect(syncDomainMembers(members, moved, "P")).toEqual([{ element: "e1", x: 5, y: 6, collapsed: true }, { element: "e3" }, { element: "r2" }]);
  });
});
