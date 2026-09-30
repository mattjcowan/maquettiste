// The process editor's keyboard grid (phase-3-design.md 6.2): a select cell moved by the keyboard commits once, on
// Enter, not on every arrow key; a pointer choice commits at once; the hint shows for a grid with row keys only.
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { Grid, type GridColumn } from "@/editors/process/Grid";

interface Row {
  id: string;
  name: string;
  kind: string;
}

const ROWS: Row[] = [{ id: "r1", name: "Draft", kind: "a" }];
const OPTIONS = ["a", "b", "c"].map((v) => ({ value: v, label: v.toUpperCase() }));
const COLUMNS: GridColumn<Row>[] = [
  { key: "name", label: "Name", kind: "text", value: (r) => r.name },
  { key: "kind", label: "Kind", kind: "select", value: (r) => r.kind, options: () => OPTIONS },
];

const kindCell = () => screen.getByTestId("g").querySelector<HTMLElement>('td[data-column="kind"]')!;

describe("process Grid", () => {
  it("commits a keyboard-chosen select option once, on Enter", () => {
    const onCommit = vi.fn();
    render(<Grid label="Rows" testid="g" noun="row" rows={ROWS} columns={COLUMNS} rowName={(r) => r.name} onCommit={onCommit} />);
    fireEvent.click(kindCell());
    fireEvent.keyDown(kindCell(), { key: "Enter" });
    const select = screen.getByRole("combobox", { name: "Kind of Draft" });
    fireEvent.keyDown(select, { key: "ArrowDown" });
    fireEvent.change(select, { target: { value: "b" } });
    fireEvent.keyDown(select, { key: "ArrowDown" });
    fireEvent.change(select, { target: { value: "c" } });
    expect(onCommit).not.toHaveBeenCalled();
    fireEvent.keyDown(select, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledTimes(1);
    expect(onCommit).toHaveBeenCalledWith(ROWS[0], "kind", "c");
  });

  it("commits a pointer choice at once", () => {
    const onCommit = vi.fn();
    render(<Grid label="Rows" testid="g" noun="row" rows={ROWS} columns={COLUMNS} rowName={(r) => r.name} onCommit={onCommit} />);
    fireEvent.click(kindCell());
    fireEvent.doubleClick(kindCell());
    fireEvent.change(screen.getByRole("combobox", { name: "Kind of Draft" }), { target: { value: "b" } });
    expect(onCommit).toHaveBeenCalledTimes(1);
    expect(onCommit).toHaveBeenCalledWith(ROWS[0], "kind", "b");
  });

  it("shows the hint of a read-only grid with row keys", () => {
    render(<Grid label="Rows" testid="g" noun="row" rows={ROWS} columns={COLUMNS} rowName={(r) => r.name} onKey={() => false} hint="Enter opens" />);
    expect(screen.getByText("Enter opens")).toBeTruthy();
  });
});
