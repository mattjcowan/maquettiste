// The statechart canvas's chart model (phase-3-design.md 6.3): containers and regions, edge labels, the gate badge,
// the problem badges, what a collapsed container hides, and the states a simulation highlights.
import { describe, expect, it } from "vitest";
import type { Diagnostic } from "@/api/types";
import type { ProcessDoc } from "@/model/process";
import { purchase } from "./statechart-fixture";
import {
  activeStates,
  badgesAt,
  buildChart,
  collapsedAncestors,
  edgeLabel,
  gateBadge,
  representatives,
  shortDuration,
  visibleEdges,
} from "@/canvas/statechart/chartModel";

describe("durations", () => {
  it("writes ISO 8601 durations short", () => {
    expect(shortDuration("P30D")).toBe("30d");
    expect(shortDuration("PT1H30M")).toBe("1h 30m");
    expect(shortDuration("P1M")).toBe("1mo");
    expect(shortDuration("P1Y2M3W4DT5H6M7S")).toBe("1y 2mo 3w 4d 5h 6m 7s");
    expect(shortDuration("PT0.5S")).toBe("0.5s");
    expect(shortDuration("soon")).toBe("soon");
    expect(shortDuration("P")).toBe("P");
    expect(shortDuration(undefined)).toBe("");
  });
});

describe("edge labels", () => {
  const t = (id: string) => purchase.transitions!.find((x) => x.id === id)!;
  it("says event [guard] / actions, after, done, always and the invoke's done", () => {
    expect(edgeLabel(purchase, t("t1"))).toBe("submit [isLarge] / log");
    expect(edgeLabel(purchase, t("t2"))).toBe("done: checkBudget");
    expect(edgeLabel(purchase, t("t3"))).toBe("done");
    expect(edgeLabel(purchase, t("t4"))).toBe("approve");
    expect(edgeLabel(purchase, t("t5"))).toBe("after 5d / sendReminder");
    expect(edgeLabel(purchase, t("t6"))).toBe("always");
    expect(edgeLabel(purchase, { id: "x", source: "draft", trigger: "invoke-error", invoke: "check", targets: [] })).toBe("error: checkBudget");
    expect(edgeLabel(purchase, { id: "x", source: "draft", event: "gone" })).toBe("?");
  });

  it("badges a gate with required of signers and names them in its tooltip", () => {
    const badge = gateBadge(
      t("t4"),
      new Map([
        ["a1", "Approver"],
        ["a2", "Controller"],
      ]),
    )!;
    expect(badge.text).toBe("2 of 3");
    expect(badge.signers).toEqual(["Approver", "Controller", "a3"]);
    expect(badge.title).toBe("Purchase approval: 2 of 3 signers (Approver, Controller, a3)");
    expect(gateBadge(t("t1"))).toBeNull();
    expect(gateBadge({ id: "x", source: "s", gate: { id: "g", name: "g" } })!.text).toBe("1 of 0");
  });
});

describe("the chart", () => {
  const diag = (rule: string, pointer: string): Diagnostic => ({
    rule,
    severity: "warning",
    message: `${rule} here`,
    elementId: "P",
    filePath: null,
    jsonPointer: pointer,
    line: null,
    column: null,
  });

  it("draws containers, regions, pseudo-states and their initial children", () => {
    const chart = buildChart(purchase);
    expect(chart.roots).toEqual(["draft", "review", "approval", "hist", "done"]);
    expect(chart.rootInitial).toBe("draft");
    const review = chart.states.get("review")!;
    expect(review).toMatchObject({ container: true, type: "parallel", initialChild: null, children: ["budget", "compliance"] });
    expect(chart.states.get("budget")).toMatchObject({ region: true, container: true, initialChild: "checking", depth: 1, pointer: "/states/1/states/0" });
    expect(chart.states.get("compliance")!.initialChild).toBe("cleared");
    expect(chart.states.get("hist")).toMatchObject({ type: "history", deep: true, container: false });
    expect(chart.states.get("approval")!.label).toBe("Awaiting approval");
    expect(chart.order).toEqual(["draft", "review", "budget", "checking", "ok", "compliance", "pending", "cleared", "approval", "hist", "done"]);
  });

  it("draws one edge per target and a targetless transition inside its state", () => {
    const chart = buildChart(purchase);
    expect(chart.edges.map((e) => `${e.id}:${e.source}>${e.target}`)).toEqual([
      "t1:draft>review",
      "t2:checking>ok",
      "t3:review>approval",
      "t4:approval>done",
      "t6:pending>cleared",
      "t6#1:pending>approval",
    ]);
    expect(chart.edges.find((e) => e.id === "t4")!.gate!.text).toBe("2 of 3");
    expect(chart.states.get("approval")!.internal).toEqual([{ transition: "t5", label: "after 5d / sendReminder", badges: [] }]);
  });

  it("shows a lifecycle's bound member on its root states only", () => {
    const lifecycle: ProcessDoc = { ...purchase, use: "lifecycle" };
    const chart = buildChart(lifecycle, { members: ["Draft", "InReview", "Approval", "Done"] });
    expect(chart.states.get("draft")!.bound).toBe("Draft");
    expect(chart.states.get("review")!.bound).toBe("InReview");
    // History and choice states are not bound: the enum's third member is Approval's, the fourth Ordered's.
    expect(chart.states.get("hist")!.bound).toBeNull();
    expect(chart.states.get("done")!.bound).toBe("Done");
    expect(chart.states.get("checking")!.bound).toBeNull();
    expect(buildChart(purchase, { members: ["X"] }).states.get("draft")!.bound).toBeNull();
  });

  it("marks MQ9003, MQ9004 and MQ9006 on states and edges by pointer", () => {
    const problems = [
      diag("MQ9003", "/states/1/states/0/states/1"),
      diag("MQ9004", "/states/0"),
      diag("MQ9006", "/transitions/5/guard"),
      diag("MQ9006", "/transitions/4"),
      diag("MQ9203", "/states/0"),
    ];
    const chart = buildChart(purchase, { problems });
    expect(chart.states.get("ok")!.badges.map((b) => b.rule)).toEqual(["MQ9003"]);
    expect(chart.states.get("draft")!.badges.map((b) => b.rule)).toEqual(["MQ9004"]);
    expect(chart.states.get("review")!.badges).toEqual([]);
    expect(chart.edges.filter((e) => e.transition === "t6").every((e) => e.badges[0]?.rule === "MQ9006")).toBe(true);
    expect(chart.states.get("approval")!.internal[0].badges.map((b) => b.rule)).toEqual(["MQ9006"]);
    expect(badgesAt(problems, "/states/1")).toEqual([]);
  });

  it("hides a collapsed container's states and moves their edges to it", () => {
    const chart = buildChart(purchase);
    const rep = representatives(chart, new Set(["review"]));
    expect(rep.get("checking")).toBe("review");
    expect(rep.get("budget")).toBe("review");
    expect(rep.get("review")).toBe("review");
    expect(rep.get("approval")).toBe("approval");
    const edges = visibleEdges(chart, rep);
    // t2 and t6's first target stay inside the collapsed box; t6's second leaves it for Approval.
    expect(edges.map((e) => `${e.id}:${e.source}>${e.target}`)).toEqual(["t1:draft>review", "t3:review>approval", "t4:approval>done", "t6#1:review>approval"]);
    // A collapsed region inside an open parallel state stands for its own children.
    const inner = representatives(chart, new Set(["budget"]));
    expect([inner.get("checking"), inner.get("budget"), inner.get("pending")]).toEqual(["budget", "budget", "pending"]);
  });

  it("names the collapsed containers that hide a state (the canvas expands them when it becomes selected)", () => {
    const chart = buildChart(purchase);
    expect(collapsedAncestors(chart, new Set(["review", "budget"]), "checking")).toEqual(["budget", "review"]);
    expect(collapsedAncestors(chart, new Set(["review"]), "pending")).toEqual(["review"]);
    expect(collapsedAncestors(chart, new Set(["review"]), "review")).toEqual([]);
    expect(collapsedAncestors(chart, new Set(), "checking")).toEqual([]);
  });

  it("highlights the active atomic states and their ancestors", () => {
    const chart = buildChart(purchase);
    expect([...activeStates(chart, ["checking", "pending", "gone"])].sort()).toEqual(["budget", "checking", "compliance", "pending", "review"]);
  });
});
