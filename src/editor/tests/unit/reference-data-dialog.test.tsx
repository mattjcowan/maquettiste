// The New reference type dialog's "Stored as" (reference-types-seeds-localization.md 1.4 and 4.5) over the mock API:
// with the project's strategies declared, check is preselected, its description shown under the select, and written as the type's storage choice for
// every database, and the new type's seed lists code, label and description.
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { ServicesProvider } from "@/app/context";
import * as endpoints from "@/api/endpoints";
import type { SettingsJson } from "@/api/types";
import { NewReferenceTypeDialog } from "@/workspaces/reference-data/dialogs";
import { useMockApi } from "./harness";

describe("New reference type: Stored as", () => {
  const api = useMockApi();

  it("preselects Check constraint and writes it as the type's storage choice", async () => {
    const settings = await endpoints.getSettings();
    const json = structuredClone(settings.json) as SettingsJson & { referenceData?: Record<string, unknown> };
    json.referenceData = {
      ...(json.referenceData ?? {}),
      strategies: { "lookup-table": { collections: true }, check: { description: "CHECK (column IN (codes))" }, native: {} },
    };
    const saved = await endpoints.saveSettings(json as SettingsJson, settings.hash);
    expect(saved.outcome).toBe("saved");

    let created: string | null = null;
    render(
      <ServicesProvider services={api.services}>
        <QueryClientProvider client={api.services.queryClient}>
          <NewReferenceTypeDialog open onOpenChange={() => {}} categories={[]} onCreated={(id) => (created = id)} />
        </QueryClientProvider>
      </ServicesProvider>,
    );
    const storedAs = (await screen.findByLabelText("Stored as")) as HTMLSelectElement;
    await waitFor(() => expect(storedAs.value).toBe("check"));
    expect([...storedAs.options].map((o) => o.text)).toEqual(["Let the packs decide", "lookup-table", "check", "native"]);
    // The chosen strategy's description shows under the select.
    expect(screen.getByTestId("new-reference-type-storage-help")).toHaveTextContent("CHECK (column IN (codes))");
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Country" } });
    fireEvent.click(screen.getByRole("button", { name: "Create" }));
    await waitFor(() => expect(created).not.toBeNull());

    const type = (await endpoints.getElement(created!)).json as { storage?: unknown };
    expect(type.storage).toEqual({ "*": { strategy: "check" } });
    const seed = (await endpoints.getModelIndex()).find((r) => r.kind === "seed" && r.target === created);
    expect((await endpoints.getElement(seed!.id)).json).toMatchObject({ columns: ["code", "label", "description"] });
  });
});
