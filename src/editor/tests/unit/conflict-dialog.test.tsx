// The conflict dialog (phase2-design.md 4.6): a 409 opens it; Keep mine, Take theirs and Save merge.
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";
import { ServicesProvider } from "@/app/context";
import { ConflictDialog } from "@/inspector/ConflictDialog";
import { IDS, useMockApi } from "./harness";

// Monaco does not run in jsdom: the right side becomes a textarea whose text is the merge.
vi.mock("@/code", () => ({
  CodeDiff: ({ label, modified, onMount }: { label: string; modified: string; onMount?: (get: () => string) => void }) => {
    let area: HTMLTextAreaElement | null = null;
    onMount?.(() => area?.value ?? modified);
    return <textarea aria-label={label} defaultValue={modified} ref={(el) => void (area = el)} />;
  },
}));

type Named = { name: string; description?: string };

describe("ConflictDialog", () => {
  const api = useMockApi();

  async function conflict() {
    const { drafts, queryClient } = api.services;
    queryClient.setQueryData(keys.element(IDS.invoice), await endpoints.getElement(IDS.invoice));
    api.backend.model.externalEdit(IDS.invoice, (json) => {
      json.description = "from disk";
    });
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Bill";
    });
    render(
      <ServicesProvider services={api.services}>
        <ConflictDialog />
      </ServicesProvider>,
    );
    await act(() => drafts.flush(IDS.invoice));
    return screen.findByRole("dialog", { name: "Bill changed on disk" });
  }

  it("Keep mine saves the draft over the disk version", async () => {
    await conflict();
    await userEvent.click(screen.getByRole("button", { name: "Keep mine" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect((api.backend.model.get(IDS.invoice)!.json as Named).name).toBe("Bill");
  });

  it("Take theirs keeps the disk version and drops the draft", async () => {
    await conflict();
    await userEvent.click(screen.getByRole("button", { name: "Take theirs" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(api.services.store.getState().drafts[IDS.invoice]).toBeUndefined();
    expect((api.backend.model.get(IDS.invoice)!.json as Named).name).toBe("Invoice");
  });

  it("Save merge saves the edited right side", async () => {
    await conflict();
    const area = screen.getByRole("textbox", { name: "Conflict for Bill" }) as HTMLTextAreaElement;
    const merged = { ...(JSON.parse(area.value) as Named), description: "from disk", name: "Merged" };
    await userEvent.clear(area);
    area.value = JSON.stringify(merged);
    await userEvent.click(screen.getByRole("button", { name: "Save merge" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    const disk = api.backend.model.get(IDS.invoice)!.json as Named;
    expect(disk).toMatchObject({ name: "Merged", description: "from disk" });
  });
});
