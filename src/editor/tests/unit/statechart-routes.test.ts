// Transitions lifted over the states their straight run would cross (routes.ts): a chain Draft, Issued, Paid, Void
// with Draft to Paid and Draft to Void shows both above the row in separate lanes, the longer one outside; a backward
// transition runs below; a clear run, a self loop and a run through the container holding both ends are left alone.
import { describe, expect, it } from "vitest";
import type { Box } from "@/canvas/layout";
import { DETOUR_CLEARANCE, detourPath, detours, LANE_GAP, roundedPath } from "@/canvas/statechart/routes";

const box = (x: number, y = 20, width = 88, height = 24): Box => ({ x, y, width, height });
const draft = box(0);
const issued = box(200);
const paid = box(400);
const voided = box(600);
const row = [draft, issued, paid, voided];

describe("detours", () => {
  it("lifts a transition over the states it would cross, the longer one outside", () => {
    const out = detours(
      [
        { id: "d-i", from: draft, to: issued },
        { id: "d-p", from: draft, to: paid },
        { id: "i-p", from: issued, to: paid },
        { id: "p-v", from: paid, to: voided },
        { id: "d-v", from: draft, to: voided },
      ],
      row,
    );
    expect([...out.keys()].sort()).toEqual(["d-p", "d-v"]);
    const inner = out.get("d-p")!;
    const outer = out.get("d-v")!;
    expect(inner.above).toBe(true);
    expect(inner.y).toBe(20 - DETOUR_CLEARANCE);
    expect(outer.y).toBe(20 - DETOUR_CLEARANCE - LANE_GAP);
  });

  it("runs a backward transition below the states it crosses", () => {
    const out = detours([{ id: "v-d", from: voided, to: draft }], row);
    expect(out.get("v-d")).toEqual({ y: 44 + DETOUR_CLEARANCE, above: false });
  });

  it("leaves a self loop, a clear run and a container holding both ends alone", () => {
    const container = box(-20, 0, 800, 80);
    const below = box(200, 120);
    const out = detours(
      [
        { id: "self", from: draft, to: draft },
        { id: "d-i", from: draft, to: issued },
        { id: "d-below", from: draft, to: below },
      ],
      [...row, container, below],
    );
    expect(out.size).toBe(0);
  });

  it("gives two transitions over different stretches the same lane", () => {
    const out = detours(
      [
        { id: "d-p", from: draft, to: paid },
        { id: "i-v", from: issued, to: voided },
      ],
      row,
    );
    // They overlap between Issued and Paid, so they take different lanes; the tie goes by id.
    expect(out.get("d-p")!.y).not.toBe(out.get("i-v")!.y);
    const far = box(1000);
    const apart = detours(
      [
        { id: "d-i", from: draft, to: paid },
        { id: "v-f", from: voided, to: box(1200) },
      ],
      [...row, far, box(1200)],
    );
    expect(apart.get("d-i")!.y).toBe(apart.get("v-f")!.y);
  });
});

describe("paths", () => {
  it("rounds every corner of an orthogonal path", () => {
    const d = roundedPath([
      { x: 0, y: 0 },
      { x: 10, y: 0 },
      { x: 10, y: 10 },
    ]);
    expect(d).toBe("M 0 0 L 5 0 Q 10 0 10 5 L 10 10");
  });

  it("puts a lifted edge's label on its run", () => {
    const [path, x, y] = detourPath(88, 32, 400, 32, { y: 0, above: true });
    expect(path.startsWith("M 88 32")).toBe(true);
    expect(path.endsWith("L 400 32")).toBe(true);
    expect(x).toBe((100 + 388) / 2);
    expect(y).toBe(0);
  });
});
