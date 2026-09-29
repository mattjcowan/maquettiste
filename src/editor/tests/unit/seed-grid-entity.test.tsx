// The Rows grid in entity mode (the entity editor's Seed data tab): columns from the entity's attributes and its
// to-one ends, end cells picked from the far entity's rows, New seed on request, and the seed data bundle's file
// naming, matching and ZIP round trip.
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { ServicesProvider } from "@/app/context";
import * as endpoints from "@/api/endpoints";
import { elementQuery, indexQuery } from "@/api/queries";
import type { AttributeDoc, ElementSummary, ModelJson, RelationDoc, SeedDoc } from "@/api/types";
import { SeedDataTab } from "@/editors/SeedDataTab";
import { readZip, writeZip } from "@/lib/zip";
import { endRowOptions, entityGridColumns, entitySeedEnds, relationGridColumns, withSeedColumns } from "@/workspaces/reference-data/rowsModel";
import { matchSeedFiles, seedFileNames, seedsInDomain } from "@/workspaces/reference-data/seedBundle";
import { createTargetSeed, seedTargetColumns } from "@/workspaces/reference-data/seedTargets";
import { IDS, useMockApi } from "./harness";

const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const CUSTOMER_END = "01J92P0V1EHF7PB28CZJG9C5SN";

const attr = (id: string, name: string, type = "string", required = false) => ({ id, name, type, required }) as unknown as AttributeDoc;
const end = (id: string, entity: string, role: string, max?: 1) => ({ id, entity, role, ...(max ? { max } : {}), min: max ? 1 : 0 });

describe("entity seed columns", () => {
  const relations = [
    { id: "r1", ends: [end("e1", "customer", "customer", 1), end("e2", "invoice", "invoices")] },
    { id: "r2", ends: [end("e3", "invoice", "invoice", 1), end("e4", "line", "lines")] },
    { id: "r3", ends: [end("e5", "invoice", "invoice", 1), end("e6", "note", "note", 1)] },
  ] as unknown as RelationDoc[];

  it("offers the far to-one ends of the entity's relations, not to-many ends, not relations with seeds of their own", () => {
    const ends = entitySeedEnds(new Set(["invoice"]), relations, (id) => id === "r3");
    expect(ends.map((e) => e.end.id)).toEqual(["e1"]);
    expect(entitySeedEnds(new Set(["line"]), relations, () => false).map((e) => e.end.id)).toEqual(["e3"]);
  });

  it("lists the attributes, then the ends as picker columns naming the far entity", () => {
    const ends = entitySeedEnds(new Set(["invoice"]), relations, () => false);
    const columns = entityGridColumns([attr("a1", "number", "string", true), attr("a2", "total", "decimal")], ends, (id) =>
      id === "customer" ? "Customer" : undefined,
    );
    expect(columns.map((c) => [c.key, c.label, c.type, c.end ?? null, c.required])).toEqual([
      ["a1", "number", "string", null, true],
      ["a2", "total", "decimal", null, false],
      ["e1", "customer", "end", "customer", true],
      ["e6", "note", "end", "note", true],
    ]);
  });

  it("gives a relation seed both ends first, then the relation's attributes; keeps stored columns the target lost", () => {
    const relation = { ends: [end("p", "payment", "payments"), end("i", "invoice", "")], attributes: [attr("amt", "amount", "decimal")] };
    expect(relationGridColumns(relation as never, (id) => (id === "invoice" ? "Invoice" : undefined)).map((c) => c.label)).toEqual([
      "payments",
      "Invoice",
      "amount",
    ]);
    const kept = withSeedColumns(
      entityGridColumns([attr("a1", "number")], [], () => undefined),
      [{ columns: ["a1", "gone"] }],
    );
    expect(kept.map((c) => c.key)).toEqual(["a1", "gone"]);
  });

  it("labels a picker's rows by their first two non-empty cells, else by id", () => {
    const seed = {
      id: "s",
      columns: ["a", "b", "c"],
      rows: [
        { id: "r1", values: [null, "Acme", "acme@example.test"] },
        { id: "r2", values: [] },
      ],
    } as unknown as SeedDoc;
    expect(endRowOptions([seed])).toEqual([
      { id: "r1", label: "Acme · acme@example.test" },
      { id: "r2", label: "r2" },
    ]);
  });
});

describe("seed data bundle", () => {
  it("names one file per seed, with the id when names collide, and matches files back", () => {
    const seeds = [
      { id: "S1", name: "Customer" },
      { id: "S2", name: "Units" },
      { id: "S3", name: "units" },
    ];
    const names = seedFileNames(seeds);
    expect([...names.values()]).toEqual(["Customer.csv", "units.S3.csv", "Units.S2.csv"]);
    const { matched, skipped } = matchSeedFiles(
      [
        { name: "seed-data/customer.CSV", text: "a" },
        { name: "Units.S2.csv", text: "b" },
        { name: "units.csv", text: "c" },
        { name: "readme.txt", text: "" },
        { name: "Customer.csv", text: "d" },
      ],
      seeds,
    );
    expect(matched.map((m) => [m.seed.id, m.text])).toEqual([
      ["S1", "a"],
      ["S2", "b"],
    ]);
    expect(skipped).toHaveLength(3);
  });

  it("scopes a domain's export to seeds whose target sits in the domain or a sub-domain", () => {
    const rows = [
      { id: "D1", kind: "package", name: "Billing" },
      { id: "D2", kind: "package", name: "Catalog", package: "D1" },
      { id: "D3", kind: "package", name: "Ops" },
      { id: "E1", kind: "entity", name: "Invoice", package: "D1" },
      { id: "E2", kind: "entity", name: "Product", package: "D2" },
      { id: "E3", kind: "entity", name: "Log", package: "D3" },
      { id: "S1", kind: "seed", name: "Invoice", target: "E1" },
      { id: "S2", kind: "seed", name: "Product", target: "E2" },
      { id: "S3", kind: "seed", name: "Log", target: "E3" },
    ] as unknown as ElementSummary[];
    expect(seedsInDomain(rows, "D1").map((s) => s.id)).toEqual(["S1", "S2"]);
    expect(seedsInDomain(rows, "D2").map((s) => s.id)).toEqual(["S2"]);
    expect(seedsInDomain(rows, "D3").map((s) => s.id)).toEqual(["S3"]);
  });

  it("round-trips files through the ZIP writer and reader, byte for byte and deterministically", async () => {
    const enc = new TextEncoder();
    const entries = [
      { name: "Customer.csv", data: enc.encode("@id,name\nr1,Acme\n") },
      { name: "Unités.csv", data: enc.encode("x") },
    ];
    const zip = writeZip(entries);
    expect(writeZip(entries)).toEqual(zip);
    const back = await readZip(zip);
    expect(back.map((e) => [e.name, new TextDecoder().decode(e.data)])).toEqual([
      ["Customer.csv", "@id,name\nr1,Acme\n"],
      ["Unités.csv", "x"],
    ]);
  });
});

describe("the Seed data tab over the mock API", () => {
  const api = useMockApi();

  it("imports several seeds as one change: a stale seed writes none of them", async () => {
    const a = (await createTargetSeed(api.services, CUSTOMER))!;
    const b = (await createTargetSeed(api.services, IDS.invoice))!;
    const [hashA, hashB] = [(await endpoints.getElement(a)).hash, (await endpoints.getElement(b)).hash];
    const files = [
      { seed: a, content: "@id,name\n,Acme\n", hash: hashA },
      { seed: b, content: "@id,note\n,1\n", hash: "0".repeat(64) },
    ];
    const preview = await endpoints.importSeedsCsv(files);
    expect(preview.status).toBe(200);
    expect(preview.items.map((i) => i.added)).toEqual([1, 1]);
    const stale = await endpoints.importSeedsCsv(files, { dryRun: false });
    expect(stale.status).toBe(409);
    expect((await endpoints.getElement(a)).hash).toBe(hashA);
    expect((await endpoints.getElement(b)).hash).toBe(hashB);
  });
  // jsdom lays nothing out: give the grid's scroller a size so the virtualizer renders its rows.
  const sizes = ["offsetHeight", "offsetWidth"] as const;
  const saved = sizes.map((k) => Object.getOwnPropertyDescriptor(HTMLElement.prototype, k));
  beforeAll(() => sizes.forEach((k) => Object.defineProperty(HTMLElement.prototype, k, { configurable: true, get: () => 600 })));
  afterAll(() => sizes.forEach((k, i) => saved[i] && Object.defineProperty(HTMLElement.prototype, k, saved[i])));
  const renderTab = (id: string) =>
    render(
      <ServicesProvider services={api.services}>
        <QueryClientProvider client={api.services.queryClient}>
          <div style={{ height: 600 }}>
            <SeedDataTab id={id} />
          </div>
        </QueryClientProvider>
      </ServicesProvider>,
    );

  it("reads the columns of the fixture's entities: Invoice gets its customer end, Customer gets none", async () => {
    const { queryClient } = api.services;
    const rows = await queryClient.fetchQuery(indexQuery);
    const docs = new Map<string, ModelJson>();
    for (const r of rows)
      if (r.kind === "entity" || r.kind === "relation") docs.set(r.id, (await queryClient.fetchQuery(elementQuery(r.id))).json as ModelJson);
    const invoice = seedTargetColumns(rows, IDS.invoice, (id) => docs.get(id))!;
    expect(invoice.map((c) => c.label)).toEqual(["id", "number", "issuedOn", "total", "status", "notes", "customer"]);
    expect(invoice.at(-1)).toMatchObject({ key: CUSTOMER_END, end: CUSTOMER, type: "end" });
    expect(seedTargetColumns(rows, CUSTOMER, (id) => docs.get(id))!.map((c) => c.label)).toEqual(["id", "name", "email", "customerSince"]);
    const settles = rows.find((r) => r.kind === "relation" && r.name === "settles")!;
    expect(seedTargetColumns(rows, settles.id, (id) => docs.get(id))!.map((c) => c.type)).toEqual(["end", "end", expect.any(String)]);
  });

  it("creates a seed only on New seed, then picks the invoice's customer from the customer rows", async () => {
    const customerSeed = await createTargetSeed(api.services, CUSTOMER);
    expect(customerSeed).toBeTruthy();
    const doc = (await endpoints.getElement(customerSeed!)).json as unknown as SeedDoc;
    await endpoints.saveElement(
      doc.id,
      { ...doc, rows: [{ id: "01J92P0V9Z0000000000000001", values: [null, "Acme", "billing@acme.test"] }] } as unknown as ModelJson,
      (await endpoints.getElement(doc.id)).hash,
    );
    await act(() => api.services.queryClient.invalidateQueries());

    renderTab(IDS.invoice);
    const newSeed = await screen.findByTestId("new-seed");
    await waitFor(() => expect(newSeed).toBeEnabled());
    fireEvent.click(newSeed);
    const grid = await screen.findByTestId("rows-grid", {}, { timeout: 5000 });
    expect(
      within(grid)
        .getAllByRole("columnheader")
        .map((h) => h.textContent),
    ).toContain("customer");
    expect(screen.getByRole("button", { name: /Import CSV/ })).toBeEnabled();
    expect(screen.getByRole("button", { name: /Export CSV/ })).toBeEnabled();

    fireEvent.keyDown(grid, { key: "Enter", ctrlKey: true });
    const picker = (await screen.findByRole("textbox", { name: "id of row 1" })) as HTMLInputElement;
    fireEvent.keyDown(picker, { key: "Escape" });
    for (let i = 0; i < 6; i++) fireEvent.keyDown(grid, { key: "ArrowRight" });
    fireEvent.keyDown(grid, { key: "Enter" });
    const select = (await screen.findByRole("combobox", { name: "customer of row 1" })) as HTMLSelectElement;
    await waitFor(() => expect([...select.options].map((o) => o.textContent)).toContain("Acme · billing@acme.test"));
    fireEvent.change(select, { target: { value: "01J92P0V9Z0000000000000001" } });
    fireEvent.keyDown(select, { key: "Enter" });
    await waitFor(() => expect(within(grid).getByText("Acme · billing@acme.test")).toBeInTheDocument());
  });
});
