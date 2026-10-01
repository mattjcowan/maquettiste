// The attribute grid's keyboard entry (phase2-design.md, Entities): Add attribute opens the new row's name
// in edit mode, Tab commits and moves right, Escape cancels the edit; each attribute's display name and description
// (what the field means) edit in the last two columns, the description in a text area where Shift+Enter adds a line.
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import type { AttributeDoc, EntityDoc, ModelJson } from "@/api/types";
import { AttributeGrid } from "@/inspector/AttributeGrid";

const START: AttributeDoc[] = [
  { id: "a1", name: "id", type: "uuid" },
  { id: "a2", name: "number", type: "string" },
  { id: "a3", name: "notes", type: "string", description: { file: "invoice.notes.md" } } as AttributeDoc,
];

let last: EntityDoc | null = null;

function Harness() {
  const [entity, setEntity] = useState({ name: "Invoice", attributes: START } as unknown as EntityDoc);
  last = entity;
  return (
    <AttributeGrid
      label="Attributes of Invoice"
      attributes={entity.attributes ?? []}
      keyIds={entity.key?.attributes ?? []}
      typeOptions={[]}
      onChange={(update) =>
        setEntity((prev) => {
          const next = structuredClone(prev);
          update(next as unknown as ModelJson);
          return next;
        })
      }
    />
  );
}

const nameCells = () => screen.getByTestId("attribute-grid").querySelectorAll<HTMLElement>('td[data-column="name"]');

describe("AttributeGrid", () => {
  it("opens the new row's name in edit mode, focused and selected", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "Add attribute" }));
    expect(nameCells()).toHaveLength(4);
    const editor = within(nameCells()[3]).getByRole("textbox");
    expect(editor).toHaveFocus();
    expect(editor).toHaveValue("attribute4");
    expect((editor as HTMLInputElement).selectionStart).toBe(0);
    expect((editor as HTMLInputElement).selectionEnd).toBe("attribute4".length);
  });

  it("takes the typed name and moves to the type cell on Tab", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "Add attribute" }));
    await user.keyboard("dueOn");
    await user.keyboard("{Tab}");
    expect(nameCells()[3]).toHaveTextContent("dueOn");
    expect(screen.queryByRole("textbox")).toBeNull();
    const typeCell = screen.getByTestId("attribute-grid").querySelector<HTMLElement>('[data-cell="3:2"]');
    expect(typeCell).toHaveAttribute("data-column", "type");
    expect(typeCell).toHaveFocus();
    // Enter opens the type editor, as on any cell.
    await user.keyboard("{Enter}");
    expect(within(typeCell!).getByRole("combobox", { name: "Type of dueOn" })).toHaveFocus();
  });

  it("cancels the edit on Escape and keeps the new row with its generated name", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "Add attribute" }));
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(nameCells()).toHaveLength(4);
    expect(nameCells()[3]).toHaveTextContent("attribute4");
    expect(nameCells()[3]).toHaveFocus();
  });

  it("opens the editor on double-click and leaves it on blur without taking focus back", async () => {
    const user = userEvent.setup();
    render(
      <>
        <Harness />
        <input aria-label="Elsewhere" />
      </>,
    );
    await user.dblClick(nameCells()[1]);
    const editor = within(nameCells()[1]).getByRole("textbox", { name: "Name of number" });
    expect(editor).toHaveFocus();
    await user.clear(editor);
    await user.type(editor, "invoiceNo");
    await user.click(screen.getByRole("textbox", { name: "Elsewhere" }));
    expect(nameCells()[1]).toHaveTextContent("invoiceNo");
    expect(screen.getByRole("textbox", { name: "Elsewhere" })).toHaveFocus();
  });

  it("edits an attribute's display name and a description that spans lines; one in a sidecar file is shown, not edited", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const grid = screen.getByTestId("attribute-grid");
    expect(
      within(grid)
        .getAllByRole("columnheader")
        .map((h) => h.textContent),
    ).toContain("Display name");
    const cell = (row: number, column: string) => grid.querySelectorAll<HTMLElement>(`td[data-column="${column}"]`)[row];
    await user.dblClick(cell(1, "displayName"));
    await user.keyboard("Invoice number{Enter}");
    expect(last?.attributes?.[1].displayName).toBe("Invoice number");
    await user.dblClick(cell(1, "description"));
    const text = within(cell(1, "description")).getByRole("textbox", { name: "Description of number" });
    expect(text.tagName).toBe("TEXTAREA");
    await user.keyboard("The number printed{Shift>}{Enter}{/Shift}on the invoice{Enter}");
    expect(last?.attributes?.[1].description).toBe("The number printed\non the invoice");
    expect(cell(1, "description")).toHaveAttribute("title", "The number printed\non the invoice");
    // A description kept in a file is named, and a double click opens no editor.
    expect(cell(2, "description")).toHaveTextContent("(in invoice.notes.md)");
    await user.dblClick(cell(2, "description"));
    expect(within(cell(2, "description")).queryByRole("textbox")).toBeNull();
  });
});
