// The database inspector's Mapping section over the mock API: mappings that ignore an entity are listed apart from the
// ones that place it, and the convention's domains show as paths.
import { render, screen, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { ServicesProvider } from "@/app/context";
import * as endpoints from "@/api/endpoints";
import { indexQuery } from "@/api/queries";
import type { ModelJson } from "@/api/types";
import { DatabaseMappingSection } from "@/inspector/DatabaseMapping";
import { IDS, useMockApi } from "./harness";

describe("the database Mapping section", () => {
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
          <DatabaseMappingSection id={database.id} json={{ kind: "database", byConvention: "packages", packages: [] }} edit={() => {}} flush={() => {}} />
        </QueryClientProvider>
      </ServicesProvider>,
    );
    await waitFor(() => expect(screen.getByTestId("database-mapping-ignored")).toHaveTextContent("Ignored (kept out of this database): Payment"));
    expect(screen.getByTestId("database-mapping-mapped")).toHaveTextContent("Mapped one by one: Invoice");
    expect(screen.getByTestId("database-mapping-mapped")).not.toHaveTextContent("Payment");
    expect(screen.getByRole("group", { name: "Domains mapped by convention" })).toHaveTextContent("Billing › Catalog");
  });
});
