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

  it("offers a pick cell as a grouped checklist and commits the chosen ids once", () => {
    const onCommit = vi.fn();
    const columns: GridColumn<Row>[] = [
      { key: "name", label: "Name", kind: "text", value: (r) => r.name },
      {
        key: "picked",
        label: "Picked",
        kind: "pick",
        value: () => "Employment.Active",
        editValue: () => "a",
        options: () => [
          { value: "a", label: "Active", group: "Employment" },
          { value: "b", label: "Left", group: "Employment" },
          { value: "c", label: "Open", group: "Orders" },
        ],
      },
    ];
    render(<Grid label="Rows" testid="g" noun="row" rows={ROWS} columns={columns} rowName={(r) => r.name} onCommit={onCommit} />);
    const cell = screen.getByTestId("g").querySelector<HTMLElement>('td[data-column="picked"]')!;
    fireEvent.click(cell);
    fireEvent.doubleClick(cell);
    expect(screen.getByText("Employment")).toBeTruthy();
    expect(screen.getByText("Orders")).toBeTruthy();
    expect((screen.getByRole("checkbox", { name: "Active" }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole("checkbox", { name: "Open" }));
    expect(onCommit).not.toHaveBeenCalled();
    fireEvent.keyDown(screen.getByRole("checkbox", { name: "Open" }), { key: "Enter" });
    expect(onCommit).toHaveBeenCalledTimes(1);
    expect(onCommit).toHaveBeenCalledWith(ROWS[0], "picked", "a,c");
  });

  it("keeps a sticky column at its own edge and width", () => {
    const columns: GridColumn<Row>[] = [
      { key: "name", label: "Name", kind: "text", sticky: { left: "0", width: "9rem" }, value: (r) => r.name },
      { key: "kind", label: "Kind", kind: "select", sticky: { left: "9rem", width: "6rem" }, value: (r) => r.kind, options: () => OPTIONS },
    ];
    render(<Grid label="Rows" testid="g" noun="row" rows={ROWS} columns={columns} rowName={(r) => r.name} />);
    const cell = kindCell();
    expect(cell.style.left).toBe("9rem");
    expect(cell.style.width).toBe("6rem");
    expect(cell.className).toContain("sticky");
  });

  it("shows the hint of a read-only grid with row keys", () => {
    render(<Grid label="Rows" testid="g" noun="row" rows={ROWS} columns={COLUMNS} rowName={(r) => r.name} onKey={() => false} hint="Enter opens" />);
    expect(screen.getByText("Enter opens")).toBeTruthy();
  });
});
