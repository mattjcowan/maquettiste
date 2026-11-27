// The Create tables dialog's choices over the mock API: a hierarchy and a list, each with its default preselected; changing one plans
// again with it, and Create sends it with the batch (retire-projection-design.md section 7).
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { ServicesProvider } from "@/app/context";
import * as endpoints from "@/api/endpoints";
import type { ModelJson } from "@/api/types";
import type { Binding } from "@/editors/storage/fieldMap";
import { StorageDialogsHost } from "@/editors/storage/StorageDialogs";
import { useMockApi } from "./harness";

const MAIN = "01J92P0V1QRN2181XM2ZWE02W4";
const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const ulid = (n: number) => `01K9HQRS${String(n).padStart(18, "0")}`;
const PAYMENT = ulid(1);
const CARD = ulid(2);
const TRANSFER = ulid(3);
const MEMBER = ulid(4);
const PHONES = ulid(15);

const entity = (id: string, name: string, attributes: object[], extra: object = {}) => ({ kind: "entity", id, name, package: BILLING, attributes, ...extra });

async function seed() {
  const docs = [
    entity(
      PAYMENT,
      "Receipt",
      [
        { id: ulid(10), name: "id", type: "int32", required: true },
        { id: ulid(11), name: "amount", type: "decimal" },
      ],
      { key: { attributes: [ulid(10)], strategy: "database-identity" } },
    ),
    entity(CARD, "CardReceipt", [{ id: ulid(12), name: "last4", type: "string" }], { base: PAYMENT }),
    entity(TRANSFER, "WireReceipt", [{ id: ulid(13), name: "iban", type: "string" }], { base: PAYMENT }),
    entity(
      MEMBER,
      "Member",
      [
        { id: ulid(14), name: "id", type: "int32", required: true },
        { id: PHONES, name: "phoneNumbers", type: "string", collection: true },
      ],
      { key: { attributes: [ulid(14)], strategy: "database-identity" } },
    ),
  ];
  for (const d of docs) {
    const r = await endpoints.createElement(d as ModelJson);
    expect(JSON.stringify(r), String(d.name)).toContain("saved");
  }
}

describe("Create tables: how to store them", () => {
  const api = useMockApi();

  it("preselects the defaults, plans again with a changed choice and sends it on Create", async () => {
    await seed();
    render(
      <ServicesProvider services={api.services}>
        <QueryClientProvider client={api.services.queryClient}>
          <StorageDialogsHost />
        </QueryClientProvider>
      </ServicesProvider>,
    );
    act(() =>
      api.services.store.getState().requestStorage({ action: "create-tables", entities: [PAYMENT, CARD, TRANSFER, MEMBER], domain: null, database: MAIN }),
    );
    const hierarchy = within(await screen.findByTestId("create-tables-hierarchy-Receipt"));
    expect(hierarchy.getByText("Receipt and 2 derived entities")).toBeTruthy();
    const shape = hierarchy.getByRole("combobox") as HTMLSelectElement;
    expect(shape.value).toBe("one-table");
    expect([...shape.options].map((o) => o.text)).toEqual([
      "One table for the hierarchy",
      "A table per concrete entity",
      "A table per type (each derived table keyed by its base)",
    ]);
    const list = within(screen.getByTestId("create-tables-list-Member.phoneNumbers"));
    const storage = list.getByRole("combobox") as HTMLSelectElement;
    expect(storage.value).toBe("table");
    expect([...storage.options].map((o) => o.text)).toEqual(["In a table of its own", "In a JSON column"]);
    await waitFor(() => expect(screen.getByTestId("storage-plan")).toHaveTextContent("members_phone_numbers"));

    fireEvent.change(shape, { target: { value: "per-type" } });
    await waitFor(() => expect(screen.getByTestId("storage-plan")).toHaveTextContent("card_receipts"));
    expect((within(screen.getByTestId("create-tables-hierarchy-Receipt")).getByRole("combobox") as HTMLSelectElement).value).toBe("per-type");
    fireEvent.change(within(screen.getByTestId("create-tables-list-Member.phoneNumbers")).getByRole("combobox"), { target: { value: "json" } });
    await waitFor(() => expect(screen.getByTestId("storage-plan")).not.toHaveTextContent("members_phone_numbers"));
    expect((within(screen.getByTestId("create-tables-hierarchy-Receipt")).getByRole("combobox") as HTMLSelectElement).value).toBe("per-type");

    fireEvent.click(screen.getByTestId("create-tables-apply"));
    await waitFor(() => expect(screen.queryByTestId("create-tables-dialog")).toBeNull());
    const card = (await endpoints.getElement(CARD)).json as { bindings: Binding[] };
    expect(card.bindings[0].baseForeignKey).toBeTruthy();
    const member = (await endpoints.getElement(MEMBER)).json as { bindings: Binding[] };
    expect(member.bindings[0].childTables).toBeUndefined();
    expect(member.bindings[0].fields?.map((f) => f.attribute)).toContain(PHONES);
  });
});

describe("the mock's materialize-tables shapes", () => {
  const api = useMockApi();
  const rules = (model: typeof api.backend.model) => model.validate().diagnostics.filter((d) => /^MQ40(4[4-9]|5\d)|^MQ41[23]/.test(d.rule));

  it.each([
    ["one-table", 1, ["discriminator"]],
    ["per-concrete", 3, []],
    ["per-type", 3, []],
  ] as const)("writes a hierarchy as %s with no binding finding", async (shape, tables, columns) => {
    await seed();
    const model = api.backend.model;
    const body = { op: "materialize-tables" as const, entities: [PAYMENT, CARD, TRANSFER] };
    const plan = model.previewMaterialize(MAIN, { ...body, hierarchy: shape });
    expect(plan.valid).toBe(true);
    expect(plan.choices?.hierarchies).toEqual([
      { root: PAYMENT, rootName: "Receipt", entities: ["Receipt", "CardReceipt", "WireReceipt"], shape, default: "one-table" },
    ]);
    expect(plan.creates.filter((c) => c.kind === "table").length).toBe(tables);
    const result = model.batch({ operations: [{ ...body, database: MAIN, hierarchy: { [PAYMENT]: shape } }] });
    expect(result.body).toMatchObject({ outcome: "saved" });
    const card = (model.get(CARD)!.json as { bindings: Binding[] }).bindings[0];
    expect(Boolean(card.baseForeignKey)).toBe(shape === "per-type");
    expect(card.discriminatorValue).toBe(shape === "one-table" ? "CardReceipt" : undefined);
    const table = [...model.docs().values()].find((d) => d.id === card.source) as { columns: { name: string }[] };
    for (const name of columns) expect(table.columns.map((c) => c.name)).toContain(name);
    expect(rules(model)).toEqual([]);
  });

  it("writes a list as a child table or a JSON column", async () => {
    await seed();
    const model = api.backend.model;
    const table = model.previewMaterialize(MAIN, { op: "materialize-tables", entities: [MEMBER], listStorage: "table" });
    expect(table.choices?.lists).toEqual([
      { entity: MEMBER, entityName: "Member", attribute: PHONES, attributeName: "phoneNumbers", storage: "table", default: "table" },
    ]);
    expect(table.creates.map((c) => c.name).sort()).toEqual(["members", "members_phone_numbers"]);
    const json = model.previewMaterialize(MAIN, { op: "materialize-tables", entities: [MEMBER], listStorage: { [PHONES]: "json" } });
    expect(json.creates.map((c) => c.name)).toEqual(["members"]);
    model.batch({ operations: [{ op: "materialize-tables", database: MAIN, entities: [MEMBER] }] });
    const binding = (model.get(MEMBER)!.json as { bindings: Binding[] }).bindings[0];
    expect(binding.childTables).toMatchObject([{ attribute: PHONES, fields: [{ attribute: PHONES }], orderColumn: expect.any(String) }]);
    expect(rules(model)).toEqual([]);
  });
});
