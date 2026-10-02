// The SQL preview of a query (useQuerySql): another query or dialect never shows the previous one's SQL under its label while
// its own answer is on the way; the same query and dialect keep their last answer while the model changes.
import { renderHook, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { createQueryClient, useQuerySql } from "@/api/queries";
import { useMockApi } from "./harness";

const BY_CUSTOMER = "01K6QRY0000000000000000001";
const REVENUE = "01K6QRY0000000000000000002";

describe("useQuerySql", () => {
  useMockApi();

  it("shows nothing for another dialect or query until its own SQL comes", async () => {
    const qc = createQueryClient();
    const { result, rerender } = renderHook(({ id, dialect }: { id: string; dialect: string | null }) => useQuerySql(id, dialect), {
      initialProps: { id: BY_CUSTOMER, dialect: null as string | null },
      wrapper: ({ children }) => <QueryClientProvider client={qc}>{children}</QueryClientProvider>,
    });
    await waitFor(() => expect(result.current.data?.preview?.dialect).toBe("postgresql"));

    rerender({ id: BY_CUSTOMER, dialect: "sqlserver" });
    expect(result.current.data).toBeUndefined();
    await waitFor(() => expect(result.current.data?.preview?.dialect).toBe("sqlserver"));

    rerender({ id: REVENUE, dialect: "sqlserver" });
    expect(result.current.data).toBeUndefined();
    await waitFor(() => expect(result.current.data?.preview?.name).toBe("RevenueByMonth"));
  });
});
