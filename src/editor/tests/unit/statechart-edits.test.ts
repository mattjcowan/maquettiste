// The statechart canvas's process edits (phase-3-design.md 6.3): deleting a multi-selection with its subtrees and the
// transitions entering it once confirmed, and drawing a transition (T).
import { describe, expect, it } from "vitest";
import type { ProcessDoc } from "@/model/process";
import { deleteStates, incomingFromOutside, linkStates, outermost, removedStates } from "@/canvas/statechart/edits";
import { purchase } from "./statechart-fixture";

const copy = (): ProcessDoc => structuredClone(purchase);

describe("deleting states", () => {
  it("keeps only the outermost of the selected states, and removes their subtrees", () => {
    const p = copy();
    expect(outermost(p, ["checking", "review", "done", "review", "gone"])).toEqual(["review", "done"]);
    expect([...removedStates(p, ["budget"])].sort()).toEqual(["budget", "checking", "ok"]);
  });

  it("lists the transitions entering the selection from outside it, not those between its states", () => {
    const p = copy();
    expect(incomingFromOutside(p, ["approval"]).map((t) => t.id)).toEqual(["t3", "t6"]);
    expect(incomingFromOutside(p, ["approval", "review"]).map((t) => t.id)).toEqual(["t1"]);
    expect(incomingFromOutside(p, ["draft"])).toEqual([]);
  });

  it("refuses while a transition enters, deletes with the confirmed ones, and keeps at least one state", () => {
    const refused = copy();
    expect(deleteStates(refused, ["approval"])).toContain("Transitions still enter Approval");
    const p = copy();
    expect(deleteStates(p, ["approval"], ["t3", "t6"])).toBeNull();
    expect(p.states.map((s) => s.id)).toEqual(["draft", "review", "hist", "done"]);
    // Its own transitions (t4, t5) go with it; t3 entered only it and goes; t6 keeps its other target, Cleared.
    expect(p.transitions!.map((t) => t.id)).toEqual(["t1", "t2", "t6"]);
    expect(p.transitions!.find((t) => t.id === "t6")!.targets).toEqual(["cleared"]);
    const both = copy();
    expect(deleteStates(both, ["approval", "done"], ["t3", "t6"])).toBeNull();
    expect(both.states.map((s) => s.id)).toEqual(["draft", "review", "hist"]);
    const all = copy();
    expect(
      deleteStates(
        all,
        all.states.map((s) => s.id),
      ),
    ).toBe("A process keeps at least one state.");
    expect(deleteStates(copy(), ["gone"])).toBe("The states no longer exist.");
  });

  it("deletes a confirmed transition once none of its targets is left", () => {
    const p = copy();
    // A fork from Draft into Approval and Done: deleting both of its targets deletes it.
    p.transitions = [...(p.transitions ?? []), { id: "fork", source: "draft", event: "submit", targets: ["approval", "done"] }];
    expect(deleteStates(p, ["approval"], ["t3", "t6", "fork"])).toBeNull();
    expect(p.transitions!.find((t) => t.id === "fork")!.targets).toEqual(["done"]);
    const q = copy();
    q.transitions = [...(q.transitions ?? []), { id: "fork", source: "draft", event: "submit", targets: ["approval", "done"] }];
    expect(deleteStates(q, ["approval", "done"], ["t3", "t6", "fork"])).toBeNull();
    expect(q.transitions!.some((t) => t.id === "fork")).toBe(false);
  });
});

describe("drawing a transition", () => {
  it("goes on the first event, or always when the process has none", () => {
    const p = copy();
    const id = linkStates(p, "draft", "approval");
    expect(p.transitions!.at(-1)).toEqual({ id, source: "draft", event: "submit", targets: ["approval"] });
    const bare: ProcessDoc = {
      kind: "process",
      id: "Q",
      name: "Q",
      states: [
        { id: "a", name: "A" },
        { id: "b", name: "B" },
      ],
    };
    const t = linkStates(bare, "a", "b");
    expect(bare.transitions).toEqual([{ id: t, source: "a", trigger: "always", targets: ["b"] }]);
  });
});
