// SchemaForm: objects with declared properties draw a nested group; shapes it cannot draw, and values whose runtime
// type does not match the schema, go to a JSON box that passes on only valid JSON; "[object Object]" never shows.
import { useState } from "react";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ExtensionSchema, ModelJson } from "@/api/types";
import { SchemaForm } from "@/inspector/SchemaForm";

const extension = (properties: Record<string, unknown>): ExtensionSchema =>
  ({ $schema: null, name: "demo", description: null, appliesTo: { kinds: [], stereotypes: [] }, properties, required: [] }) as unknown as ExtensionSchema;

function show(properties: Record<string, unknown>, stored: Record<string, unknown>, defaults = {}) {
  const onChange = vi.fn();
  const view = render(
    <SchemaForm extensions={[extension(properties)]} json={{ properties: stored } as unknown as ModelJson} defaults={defaults} onChange={onChange} />,
  );
  return { onChange, view };
}

const limits = {
  type: "object",
  required: ["max"],
  properties: { max: { type: "integer" }, label: { type: "string" }, deep: { type: "object", properties: { on: { type: "boolean" } } } },
};

describe("SchemaForm", () => {
  it("draws an object with declared properties as a nested group and edits one key", () => {
    const { onChange, view } = show({ limits }, { limits: { max: 3, label: "a" } });
    expect(screen.getByTestId("custom-object-limits")).toBeTruthy();
    expect(screen.getByLabelText("max *")).toHaveProperty("value", "3");
    fireEvent.change(screen.getByLabelText("label"), { target: { value: "ab" } });
    expect(onChange).toHaveBeenLastCalledWith("limits", { max: 3, label: "ab" });
    fireEvent.click(screen.getByLabelText("on"));
    expect(onChange).toHaveBeenLastCalledWith("limits", { max: 3, label: "a", deep: { on: true } });
    expect(view.container.textContent).not.toContain("[object Object]");
  });

  it("clears the property when the last key is cleared", () => {
    const { onChange } = show({ limits }, { limits: { label: "a" } });
    fireEvent.change(screen.getByLabelText("label"), { target: { value: "" } });
    expect(onChange).toHaveBeenLastCalledWith("limits", undefined);
  });

  it("shows an array of objects as JSON and passes on only valid JSON", () => {
    const { onChange, view } = show({ rules: { type: "array", items: { type: "object" } } }, { rules: [{ a: 1 }] });
    const box = screen.getByLabelText("rules") as HTMLTextAreaElement;
    expect(box.value).toBe(JSON.stringify([{ a: 1 }], null, 2));
    fireEvent.change(box, { target: { value: '[{"a": ' } });
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("Not valid JSON");
    fireEvent.change(box, { target: { value: '[{"a": 2}]' } });
    expect(onChange).toHaveBeenLastCalledWith("rules", [{ a: 2 }]);
    expect(screen.queryByRole("alert")).toBeNull();
    fireEvent.change(box, { target: { value: "" } });
    expect(onChange).toHaveBeenLastCalledWith("rules", undefined);
    expect(view.container.textContent).not.toContain("[object Object]");
  });

  it("shows an object with no declared properties and oneOf as JSON", () => {
    show(
      { free: { type: "object", additionalProperties: true }, either: { oneOf: [{ type: "string" }, { type: "object" }] } },
      { free: { k: 1 }, either: { z: 1 } },
    );
    expect((screen.getByLabelText("free") as HTMLTextAreaElement).value).toContain('"k": 1');
    expect((screen.getByLabelText("either") as HTMLTextAreaElement).value).toContain('"z": 1');
  });

  it("sends a stored value that does not match its schema to the JSON box", () => {
    const { view } = show(
      { title: { type: "string" }, count: { type: "integer" }, flag: { type: "boolean" }, tags: { type: "array", items: { type: "string" } } },
      {
        title: { a: 1 },
        count: [1],
        flag: { b: 2 },
        tags: [{ c: 3 }],
      },
    );
    for (const name of ["title", "count", "flag", "tags"]) expect(screen.getByLabelText(name).tagName).toBe("TEXTAREA");
    expect(view.container.textContent).not.toContain("[object Object]");
    expect(view.container.innerHTML).not.toContain("[object Object]");
  });

  it("keeps the default as a placeholder", () => {
    show({ title: { type: "string" } }, {}, { title: { value: "x", from: "audited" } });
    expect(screen.getByLabelText("title").getAttribute("placeholder")).toContain("from «audited»");
  });

  it("draws an array of enum values as a comma input mapped back to the members, and never saves a non-member", () => {
    const { onChange } = show({ modes: { type: "array", items: { type: "string", enum: ["a", "b", "c"] } } }, { modes: ["a", "b"] });
    const input = screen.getByLabelText("modes") as HTMLInputElement;
    expect(input.tagName).toBe("INPUT");
    expect(input.value).toBe("a, b");
    fireEvent.change(input, { target: { value: "a, c" } });
    expect(onChange).toHaveBeenLastCalledWith("modes", ["a", "c"]);
    onChange.mockClear();
    fireEvent.change(input, { target: { value: "a, zz" } });
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("zz");
    fireEvent.change(input, { target: { value: "" } });
    expect(onChange).toHaveBeenLastCalledWith("modes", undefined);
  });

  it("maps numeric enum members back to numbers", () => {
    const { onChange } = show({ levels: { type: "array", items: { type: "integer", enum: [1, 2, 3] } } }, {});
    fireEvent.change(screen.getByLabelText("levels"), { target: { value: "1, 3" } });
    expect(onChange).toHaveBeenLastCalledWith("levels", [1, 3]);
  });

  it("draws an array of booleans as a comma input of true and false", () => {
    const { onChange } = show({ flags: { type: "array", items: { type: "boolean" } } }, { flags: [true, false] });
    const input = screen.getByLabelText("flags") as HTMLInputElement;
    expect(input.value).toBe("true, false");
    fireEvent.change(input, { target: { value: "false, TRUE" } });
    expect(onChange).toHaveBeenLastCalledWith("flags", [false, true]);
    onChange.mockClear();
    fireEvent.change(input, { target: { value: "yes" } });
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole("alert")).toBeTruthy();
  });

  it("infers an object from properties and an array from items when there is no type", () => {
    show({ grp: { properties: { n: { type: "integer" } } }, list: { items: { type: "string" } } }, { grp: { n: 2 }, list: ["x"] });
    expect(screen.getByTestId("custom-object-grp")).toBeTruthy();
    expect((screen.getByLabelText("n") as HTMLInputElement).value).toBe("2");
    expect((screen.getByLabelText("list") as HTMLInputElement).value).toBe("x");
  });

  it("keeps the JSON box mounted while it has focus, even when the text now fits the schema, and decides again on blur", () => {
    function Host() {
      const [stored, setStored] = useState<unknown>(5);
      return (
        <SchemaForm
          extensions={[extension({ opts: { type: "object", properties: { a: { type: "string" } } } })]}
          json={{ properties: { opts: stored } } as unknown as ModelJson}
          defaults={{}}
          onChange={(_, v) => setStored(v)}
        />
      );
    }
    render(<Host />);
    const box = screen.getByLabelText("opts") as HTMLTextAreaElement;
    expect(box.tagName).toBe("TEXTAREA");
    fireEvent.focus(box);
    fireEvent.change(box, { target: { value: "{" } });
    fireEvent.change(box, { target: { value: "{}" } });
    expect(screen.getByLabelText("opts")).toBe(box);
    expect(box.isConnected).toBe(true);
    fireEvent.blur(box);
    expect(screen.queryByLabelText("opts")).toBeNull();
    expect(screen.getByTestId("custom-object-opts")).toBeTruthy();
  });
});
