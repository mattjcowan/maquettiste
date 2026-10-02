// The Generate screen's pack picker: a toolbar button counting the chosen packs that opens a checkbox list, with a
// filter box past eight packs, All and None, and disabled packs that cannot be ticked.
import { useState } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeAll, describe, expect, it } from "vitest";
import { PackPicker, type PickerPack } from "@/workspaces/generate/PackPicker";

function Harness({ packs, initial }: { packs: PickerPack[]; initial: string[] }) {
  const [selected, setSelected] = useState(initial);
  return (
    <>
      <PackPicker packs={packs} selected={selected} onChange={setSelected} />
      <output data-testid="selected">{selected.join(",")}</output>
    </>
  );
}

const few: PickerPack[] = [
  { name: "sql-ddl", enabled: true },
  { name: "csharp-dapper", enabled: true, warnings: 2 },
  { name: "old-pack", enabled: false },
];
const many: PickerPack[] = Array.from({ length: 12 }, (_, i) => ({ name: `pack-${String(i + 1).padStart(2, "0")}`, enabled: true }));

const trigger = () => screen.getByTestId("pack-picker");
const selected = () => screen.getByTestId("selected").textContent;

describe("PackPicker", () => {
  beforeAll(() => {
    globalThis.ResizeObserver ??= class {
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver;
  });

  it("counts the chosen packs and opens a list in manifest order, without a filter for a few packs", async () => {
    render(<Harness packs={few} initial={["sql-ddl", "csharp-dapper"]} />);
    expect(trigger()).toHaveTextContent("Packs · 2 of 3");
    expect(trigger()).toHaveAttribute("title", "Choose the packs to plan");
    await userEvent.click(trigger());
    const list = await screen.findByTestId("pack-picker-list");
    expect(screen.queryByLabelText("Filter packs")).toBeNull();
    const rows = screen.getAllByRole("checkbox");
    expect(rows.map((r) => r.getAttribute("aria-label"))).toEqual(["Pack sql-ddl", "Pack csharp-dapper", "Pack old-pack"]);
    expect(list).toHaveTextContent("old-pack (disabled)");
    expect(screen.getByLabelText("2 diagnostics")).toBeInTheDocument();
  });

  it("unticks and ticks a pack, and All and None set every enabled pack", async () => {
    render(<Harness packs={few} initial={["sql-ddl", "csharp-dapper"]} />);
    await userEvent.click(trigger());
    await userEvent.click(await screen.findByRole("checkbox", { name: "Pack sql-ddl" }));
    expect(selected()).toBe("csharp-dapper");
    expect(trigger()).toHaveTextContent("Packs · 1 of 3");
    await userEvent.click(screen.getByRole("button", { name: "None" }));
    expect(selected()).toBe("");
    expect(trigger()).toHaveTextContent("Packs · 0 of 3");
    await userEvent.click(screen.getByRole("button", { name: "All" }));
    expect(selected()).toBe("sql-ddl,csharp-dapper");
    expect(trigger()).toHaveTextContent("Packs · 2 of 3");
  });

  it("a disabled pack cannot be ticked and does not count", async () => {
    render(<Harness packs={few} initial={["sql-ddl", "old-pack"]} />);
    expect(trigger()).toHaveTextContent("Packs · 1 of 3");
    await userEvent.click(trigger());
    const off = await screen.findByRole("checkbox", { name: "Pack old-pack" });
    expect(off).toBeDisabled();
    expect(off).toHaveAttribute("aria-checked", "false");
    await userEvent.click(off);
    expect(selected()).toBe("sql-ddl,old-pack");
  });

  it("filters a long list, and All and None act on the packs shown", async () => {
    render(<Harness packs={many} initial={[]} />);
    expect(trigger()).toHaveTextContent("Packs · 0 of 12");
    await userEvent.click(trigger());
    const filter = await screen.findByLabelText("Filter packs");
    expect(screen.getAllByRole("checkbox")).toHaveLength(12);
    await userEvent.type(filter, "pack-1");
    expect(screen.getAllByRole("checkbox").map((r) => r.getAttribute("aria-label"))).toEqual(["Pack pack-10", "Pack pack-11", "Pack pack-12"]);
    await userEvent.click(screen.getByRole("button", { name: "All" }));
    expect(selected()).toBe("pack-10,pack-11,pack-12");
    expect(trigger()).toHaveTextContent("Packs · 3 of 12");
    await userEvent.clear(filter);
    await userEvent.type(filter, "pack-11");
    await userEvent.click(screen.getByRole("button", { name: "None" }));
    expect(selected()).toBe("pack-10,pack-12");
    await userEvent.clear(filter);
    await userEvent.type(filter, "nothing");
    expect(screen.queryAllByRole("checkbox")).toHaveLength(0);
    expect(screen.getByText("No pack matches.")).toBeInTheDocument();
  });

  it("opens with ArrowDown, moves through the rows with the arrows, and Escape returns focus to the button", async () => {
    render(<Harness packs={few} initial={["sql-ddl"]} />);
    trigger().focus();
    await userEvent.keyboard("{ArrowDown}");
    await screen.findByTestId("pack-picker-list");
    await userEvent.keyboard("{ArrowDown}");
    expect(screen.getByRole("checkbox", { name: "Pack sql-ddl" })).toHaveFocus();
    await userEvent.keyboard("{ArrowDown}");
    expect(screen.getByRole("checkbox", { name: "Pack csharp-dapper" })).toHaveFocus();
    await userEvent.keyboard(" ");
    expect(selected()).toBe("sql-ddl,csharp-dapper");
    await userEvent.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByTestId("pack-picker-list")).toBeNull());
    expect(trigger()).toHaveFocus();
  });
});
