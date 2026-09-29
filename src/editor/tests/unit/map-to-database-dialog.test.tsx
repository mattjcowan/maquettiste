// The Map to database… dialog (D46): the database a domain or entity selection is mapped to.
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MapToDatabaseDialog } from "@/explorer/dialogs";

describe("MapToDatabaseDialog", () => {
  afterEach(cleanup);

  it("maps to the chosen database", async () => {
    const onMap = vi.fn();
    const databases = [
      { id: "a", name: "main" },
      { id: "b", name: "reporting" },
    ];
    render(<MapToDatabaseDialog databases={databases} count={2} onClose={() => {}} onMap={onMap} />);
    expect(screen.getAllByText("Map 2 elements to database").length).toBeGreaterThan(0);
    await userEvent.selectOptions(screen.getByLabelText("Database"), "b");
    await userEvent.click(screen.getByTestId("map-to-database-apply"));
    expect(onMap).toHaveBeenCalledWith({ id: "b", name: "reporting" });
  });

  it("asks for a database when there is none", () => {
    render(<MapToDatabaseDialog databases={[]} count={1} onClose={() => {}} onMap={() => {}} />);
    expect(screen.getByText("Create a database first.")).toBeTruthy();
    expect((screen.getByTestId("map-to-database-apply") as HTMLButtonElement).disabled).toBe(true);
  });
});
