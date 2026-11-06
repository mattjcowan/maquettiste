// Leftovers of the lifecycle screens: the Sync codes wording and its disabled Apply, the cast's attribute names, a sticky
// Name column, the row icon's severity, and the findings line under an editor's title for a warning alone.
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Grid, type GridColumn } from "@/editors/process/Grid";
import { attributeLabels, castValues } from "@/editors/process/simulation/castNames";
import { applyBlockedReason, codesDescription, hasNoValueList, type CodesView } from "@/problems/syncCodesView";
import { summaryText } from "@/problems/findings";
import { loadScenarioStart, newSession } from "@/editors/process/simulation/session";

describe("a replay's start", () => {
  it("loads the scenario's start values and clock but not its steps", () => {
    const s = loadScenarioStart(newSession("P"), { start: { context: { A: 100 }, at: "2000-01-01T00:00:00Z" } } as never);
    expect(s.start.context).toEqual({ A: 100 });
    expect(s.start.at).toBe("2000-01-01T00:00:00Z");
    expect(s.inputs).toEqual([]);
  });
});

const view = (over: Partial<CodesView> = {}): CodesView => ({ proposed: [], kept: [], unmatched: [], binds: false, ...over });
const unmatched = (n: number) => Array.from({ length: n }, (_, i) => ({ path: `S${i}`, candidates: [] }));

describe("Sync codes wording", () => {
  it("counts the states with no code when none could be proposed", () => {
    expect(codesDescription(view({ unmatched: unmatched(5) }), "Order.status")).toBe(
      "5 states have no code; none could be proposed. States that already have a code keep it.",
    );
    expect(codesDescription(view({ unmatched: unmatched(1) }), "Order.status")).toContain("1 state has no code; none could be proposed");
    expect(codesDescription(view(), "Order.status")).toContain("No state needs a new code for Order.status");
  });

  it("explains a plain attribute with no values, and gives Apply a reason when it is off", () => {
    expect(hasNoValueList({ type: { kind: "plain", typeId: "T" } }, undefined)).toBe(true);
    expect(hasNoValueList({ type: { kind: "plain" }, validation: { allowedValues: ["a"] } }, undefined)).toBe(false);
    expect(hasNoValueList({ type: { kind: "plain" } }, { validation: { allowedValues: ["a"] } })).toBe(false);
    expect(hasNoValueList({ type: { kind: "enum", typeId: "E" } }, undefined)).toBe(false);
    expect(applyBlockedReason(view({ unmatched: unmatched(2) }), false)).toMatch(/No code could be proposed/);
    expect(applyBlockedReason(view(), false)).toMatch(/Every state already has a code/);
    expect(applyBlockedReason(view({ binds: true }), false)).toBeNull();
    expect(applyBlockedReason(view({ binds: true }), true)).toMatch(/refused/);
  });
});

describe("the cast's attribute names", () => {
  it("uses the display name, else the name, from the entity and its bases; an unknown id stays", () => {
    const labels = attributeLabels([{ attributes: [{ id: "A1", name: "total", displayName: "Order total" }] }, { attributes: [{ id: "A2", name: "status" }] }]);
    expect(castValues({ A1: 120, A2: "Draft", A3: 1 }, labels, String)).toBe("Order total 120, status Draft, A3 1");
  });
});

describe("the grid", () => {
  interface Row {
    id: string;
    name: string;
  }
  const columns: GridColumn<Row>[] = [
    { key: "name", label: "Name", kind: "text", sticky: true, value: (r) => r.name },
    { key: "code", label: "Code", kind: "text", value: () => "x" },
  ];

  it("keeps a sticky column at the left edge", () => {
    render(<Grid label="Rows" testid="g" noun="row" rows={[{ id: "1", name: "Draft" }]} columns={columns} rowName={(r) => r.name} />);
    const g = screen.getByTestId("g");
    expect(g.querySelector('td[data-column="name"]')).toHaveClass("sticky", "left-0");
    expect(g.querySelector('td[data-column="code"]')).not.toHaveClass("sticky");
    expect(g.querySelector("th")).toHaveClass("sticky");
  });

  it("draws a warning-only row with a warning icon, not a red error", () => {
    const warn = { rule: "MQ1", message: "m", severity: "warning" as const };
    const err = { rule: "MQ2", message: "m", severity: "error" as const };
    const { rerender } = render(
      <Grid label="Rows" testid="g" noun="row" rows={[{ id: "1", name: "Draft" }]} columns={columns} rowName={(r) => r.name} problems={() => [warn]} />,
    );
    expect(screen.getByTestId("row-problem")).toHaveAttribute("data-severity", "warning");
    expect(screen.getByTestId("row-problem")).toHaveClass("text-warning");
    rerender(
      <Grid label="Rows" testid="g" noun="row" rows={[{ id: "1", name: "Draft" }]} columns={columns} rowName={(r) => r.name} problems={() => [warn, err]} />,
    );
    expect(screen.getByTestId("row-problem")).toHaveAttribute("data-severity", "error");
  });
});

describe("the line under the title", () => {
  it("says the warnings when there are no errors", () => {
    const d = (severity: "warning" | "info") => ({ rule: "MQ1", severity, message: "m" }) as never;
    expect(summaryText([d("warning"), d("warning"), d("warning")])).toBe("3 warnings");
    expect(summaryText([d("warning"), d("info")])).toBe("1 warning · 1 note");
  });
});
