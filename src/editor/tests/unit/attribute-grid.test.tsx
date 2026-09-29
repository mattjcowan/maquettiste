// The attribute grid's keyboard entry (phase2-design.md, Entities): Add attribute opens the new row's name
// in edit mode, Tab commits and moves right, Escape cancels the edit.
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import type { AttributeDoc, EntityDoc, ModelJson } from "@/api/types";
import { AttributeGrid } from "@/inspector/AttributeGrid";

const START: AttributeDoc[] = [
  { id: "a1", name: "id", type: "uuid" },
  { id: "a2", name: "number", type: "string" },
];

function Harness() {
  const [entity, setEntity] = useState({ name: "Invoice", attributes: START } as unknown as EntityDoc);
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
    expect(nameCells()).toHaveLength(3);
    const editor = within(nameCells()[2]).getByRole("textbox");
    expect(editor).toHaveFocus();
    expect(editor).toHaveValue("attribute3");
    expect((editor as HTMLInputElement).selectionStart).toBe(0);
    expect((editor as HTMLInputElement).selectionEnd).toBe("attribute3".length);
  });

  it("takes the typed name and moves to the type cell on Tab", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByRole("button", { name: "Add attribute" }));
    await user.keyboard("dueOn");
    await user.keyboard("{Tab}");
    expect(nameCells()[2]).toHaveTextContent("dueOn");
    expect(screen.queryByRole("textbox")).toBeNull();
    const typeCell = screen.getByTestId("attribute-grid").querySelector<HTMLElement>('[data-cell="2:2"]');
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
    expect(nameCells()).toHaveLength(3);
    expect(nameCells()[2]).toHaveTextContent("attribute3");
    expect(nameCells()[2]).toHaveFocus();
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
});
