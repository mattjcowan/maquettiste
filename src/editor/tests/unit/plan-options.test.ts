// The Generate toolbar's Options (planOptions.ts): the plan request they make, the dot, the project default's label and the
// Apply result's hand-edit line.
import { describe, expect, it } from "vitest";
import { DEFAULT_PLAN_OPTIONS, HAND_EDIT_CHOICES, handEditNote, hasNonDefault, planRequest, projectDefaultLabel } from "@/workspaces/generate/planOptions";

describe("plan options", () => {
  it("leaves the defaults out of the request and sends the choices", () => {
    expect(planRequest(["sql-ddl"], DEFAULT_PLAN_OPTIONS)).toEqual({ packs: ["sql-ddl"] });
    expect(planRequest(["sql-ddl"], { force: true, handEdits: "overwrite" })).toEqual({ packs: ["sql-ddl"], force: true, handEdits: "overwrite" });
    expect(planRequest([], { force: false, handEdits: "skip" })).toEqual({ packs: [], handEdits: "skip" });
  });
  it("shows the dot only for a non-default choice", () => {
    expect(hasNonDefault(DEFAULT_PLAN_OPTIONS)).toBe(false);
    expect(hasNonDefault({ force: true, handEdits: null })).toBe(true);
    expect(hasNonDefault({ force: false, handEdits: "fail" })).toBe(true);
  });
  it("names the engine's policies in words and the project default with its value", () => {
    expect(HAND_EDIT_CHOICES.map((c) => c.value)).toEqual(["fail", "overwrite", "skip"]);
    expect(projectDefaultLabel("fail")).toBe("Project default (Stop and report it)");
    expect(projectDefaultLabel(undefined)).toBe("Project default");
  });
  it("mentions the policy after Apply only when a file was edited by hand", () => {
    const kinds = (...k: string[]) => k.map((kind) => ({ kind }) as { kind: "added" });
    expect(handEditNote(kinds("added", "modified"), "overwrite", "fail", "succeeded")).toBeNull();
    expect(handEditNote(kinds("added", "hand-edited", "hand-edited"), "overwrite", "fail", "succeeded")).toBe(
      "2 files edited by hand: overwritten (this run's choice: Overwrite it)",
    );
    expect(handEditNote(kinds("conflict"), null, "fail", "conflicts")).toBe(
      "1 file edited by hand: reported, not written (project default: Stop and report it)",
    );
    expect(handEditNote(kinds("hand-edited"), "skip", "fail", "stale")).toBeNull();
  });
});
