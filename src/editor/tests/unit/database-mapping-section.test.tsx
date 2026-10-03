// Settings > Conventions, Older projects: tables laid out by convention (the database inspector's Mapping section before), over
// the mock API: read-only, mappings that ignore an entity are listed apart from the ones that place it, and the convention's
// domains show as paths, each with Stop laying out by convention and nothing to add one.
import { render, screen, waitFor, within } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { ServicesProvider } from "@/app/context";
import * as endpoints from "@/api/endpoints";
import { indexQuery } from "@/api/queries";
import type { ModelJson } from "@/api/types";
import { DatabaseMappingSection } from "@/workspaces/settings/DatabaseConvention";
import { IDS, useMockApi } from "./harness";

const CATALOG = "01J92P0V025DRFTXKMS240G2NG";

describe("the Tables laid out by convention section", () => {
  const api = useMockApi();

  it("lists ignoring mappings apart and shows domain paths", async () => {
    const rows = await api.services.queryClient.fetchQuery(indexQuery);
    const database = rows.find((r) => r.kind === "database")!;
    await endpoints.createElement({
      kind: "mapping",
      id: "01J92P0V9Z0000000000000A01",
      name: "Invoice in main",
      database: database.id,
      entity: IDS.invoice,
    } as unknown as ModelJson);
    await endpoints.createElement({
      kind: "mapping",
      id: "01J92P0V9Z0000000000000A02",
      name: "Payment out of main",
      database: database.id,
      entity: IDS.payment,
      ignore: true,
    } as unknown as ModelJson);
    await api.services.queryClient.invalidateQueries();
    render(
      <ServicesProvider services={api.services}>
        <QueryClientProvider client={api.services.queryClient}>
          <DatabaseMappingSection id={database.id} name="main" json={{ kind: "database", byConvention: "packages", packages: [CATALOG] }} />
        </QueryClientProvider>
      </ServicesProvider>,
    );
    await waitFor(() => expect(screen.getByTestId("database-mapping-ignored")).toHaveTextContent("Ignored (kept out of this database): Payment"));
    expect(screen.getByTestId("database-mapping-mapped")).toHaveTextContent("Placed one by one by an older mapping element: Invoice");
    expect(screen.getByTestId("database-mapping-mapped")).not.toHaveTextContent("Payment");
    const group = screen.getByRole("group", { name: "Domains laid out by convention" });
    expect(group).toHaveTextContent("Billing › Catalog");
    expect(
      within(group)
        .getAllByRole("button")
        .map((b) => b.textContent),
    ).toEqual(["Stop laying out by convention"]);
    // Read-only: no checkbox, no picker to add a domain.
    expect(screen.queryAllByRole("checkbox")).toHaveLength(0);
    expect(screen.queryAllByRole("combobox")).toHaveLength(0);
  });
});
