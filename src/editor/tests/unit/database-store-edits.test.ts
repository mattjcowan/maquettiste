// The database designer's editing paths against the mock server: a foreign key on a table not stored as a file yet (stored
// first, the key written into the file whose ids the store's result names, never the preview's, which the server draws again),
// the store's undo integrity (expected hashes, no empty undo items), a column's delete plan (whole keys, other tables' keys, one
// batch and one undo step), a column's rename (what names it follows), and schema operations as undo steps.
import { describe, expect, it } from "vitest";
import * as endpoints from "@/api/endpoints";
import { databaseViewQuery, elementQuery } from "@/api/queries";
import type { ModelJson, TableView } from "@/api/types";
import { draftFromDrop } from "@/workspaces/database/fkEdits";
import { saveForeignKey } from "@/workspaces/database/foreignKeys";
import { storedFileOf, storeTables, updateTable } from "@/workspaces/database/storeTables";
import { cancelPartDelete, executePartDelete, preparePartDelete } from "@/workspaces/database/PartDeleteDialog";
import { planColumnRename } from "@/workspaces/database/renames";
import { renameTableColumn, renameViewColumn } from "@/workspaces/database/columnRename";
import { runSchemaOperation } from "@/inspector/DatabaseSchemas";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const DB = "01J92P0V1QRN2181XM2ZWE02W4";
const CUSTOMER = "01J92P0V0ETQKXXP951CMMNHH3";
const PATTERN = "fk_{table}_{columns}";

describe("the designer's writes against the mock", () => {
  const api = useMockApi();

  const view = async () => {
    const qc = api.services.queryClient;
    await qc.invalidateQueries({ queryKey: databaseViewQuery(qc, DB).queryKey });
    return (await qc.fetchQuery({ ...databaseViewQuery(qc, DB), staleTime: 0 })).view!;
  };
  const doc = async (id: string) => (await api.services.queryClient.fetchQuery({ ...elementQuery(id), staleTime: 0 })).json as unknown as Json;

  it("writes a diagram's foreign key into the file a not-yet-stored table gets, whatever ids the preview drew", async () => {
    const { services } = api;
    const qc = services.queryClient;
    const before = await view();
    const customers = before.tables.find((t) => t.name === "customers")!;
    const invoices = before.tables.find((t) => t.name === "invoices")!;
    expect(customers.origin).toBe("synthesized");
    // The server draws the new file's id again when it applies: the preview's is not the file's.
    const preview = await endpoints.previewMaterialize(DB, { op: "materialize-tables", entities: [CUSTOMER] });
    const previewId = preview.creates.find((c) => c.kind === "table")!.id;

    const draft = draftFromDrop(before.tables, { sourceTable: customers.key, sourceColumn: null, targetTable: invoices.key, targetColumn: null }, PATTERN);
    draft.pairs = [{ column: null, newColumn: "last_invoice_id", referenced: invoices.primaryKey!.columns[0] }];
    draft.name = "fk_customers_last_invoice";
    const undoBefore = services.store.getState().undo.length;
    expect(await saveForeignKey(services, qc, { database: DB, draft, tables: before.tables, pattern: PATTERN })).toBe(true);

    const file = storedFileOf(customers.key)!;
    expect(file).toBeTruthy();
    expect(file).not.toBe(previewId);
    const stored = await doc(file);
    const column = (stored.columns as Json[]).find((c) => c.name === "last_invoice_id")!;
    expect(stored.foreignKeys).toEqual(
      expect.arrayContaining([expect.objectContaining({ name: "fk_customers_last_invoice", columns: [column.id], referencesTable: invoices.key })]),
    );
    // The store and the key are one undo step, every item of it something the undo can do.
    const undo = services.store.getState().undo;
    expect(undo.length).toBe(undoBefore + 1);
    const step = undo[undo.length - 1];
    expect(step.label).toBe("New foreign key on customers");
    expect(step.ids).toContain(file);
    expect(step.ids.every((_, i) => step.before[i] !== null || step.after[i] !== null)).toBe(true);
    expect(step.ids).not.toContain(previewId);

    // Undo puts the projection back; redo stores the table with its key again.
    expect(await services.undo.undo()).toMatchObject({ ok: true });
    expect((await view()).tables.find((t) => t.name === "customers")!.origin).toBe("synthesized");
    expect(await services.undo.redo()).toMatchObject({ ok: true });
    const again = (await view()).tables.find((t) => t.name === "customers")!;
    expect(again.key).toBe(file);
    expect(again.foreignKeys.map((f) => f.name)).toContain("fk_customers_last_invoice");
  });

  it("refuses a store whose documents changed since they were read, writing nothing", async () => {
    const { services } = api;
    const qc = services.queryClient;
    const before = await view();
    const customers = before.tables.find((t) => t.name === "customers")!;
    // Read for the undo step, then changed by another save: the store's expected hash no longer matches.
    const entity = await qc.fetchQuery({ ...elementQuery(CUSTOMER), staleTime: 0 });
    const changed = { ...(entity.json as unknown as Json), displayName: "Client" };
    expect((await endpoints.saveElement(CUSTOMER, changed as unknown as ModelJson, entity.hash)).outcome).toBe("saved");
    const result = await endpoints.applyBatch({
      operations: [{ op: "materialize-tables", database: DB, entities: [CUSTOMER], expectedHashes: { [CUSTOMER]: entity.hash } }],
    } as never);
    expect(endpoints.isBatchResult(result) && result.outcome).toBe("conflict");
    expect((await view()).tables.find((t) => t.key === customers.key)?.origin).toBe("synthesized");
    // The editor's store reads the hashes itself, so it goes through.
    const stored = await storeTables(services, qc, DB, new Map([[customers.key, CUSTOMER]]), "Store customers");
    expect(stored.ok).toBe(true);
  });

  it("deletes a key column with what holds it and the other tables' keys to it, in one batch and one undo step", async () => {
    const { services } = api;
    const qc = services.queryClient;
    const P = "01K7PAR0000000000000000001",
      PID = "01K7PAR0000000000000000002",
      PCODE = "01K7PAR0000000000000000003";
    const C = "01K7CH10000000000000000001",
      CID = "01K7CH10000000000000000002",
      CPARENT = "01K7CH10000000000000000003";
    for (const json of [
      {
        kind: "table",
        id: P,
        name: "parents",
        database: DB,
        columns: [
          { id: PID, name: "id", type: "int64", nullable: false },
          { id: PCODE, name: "code", type: "string", length: 8 },
        ],
        primaryKey: { columns: [PID] },
        uniques: [{ id: "01K7PAR0000000000000000004", name: "uq_parents_id_code", columns: [PID, PCODE] }],
      },
      {
        kind: "table",
        id: C,
        name: "children",
        database: DB,
        columns: [
          { id: CID, name: "id", type: "int64", nullable: false },
          { id: CPARENT, name: "parent_id", type: "int64" },
        ],
        primaryKey: { columns: [CID] },
        foreignKeys: [{ id: "01K7CH10000000000000000004", name: "fk_children_parent", columns: [CPARENT], referencesTable: P }],
      },
    ])
      expect((await endpoints.createElement(json as never)).outcome).toBe("saved");
    const req = { database: DB, key: P, part: { kind: "column" as const, id: PID } };
    const prepared = await preparePartDelete(services, qc, req);
    if (typeof prepared === "string") throw new Error(prepared);
    const { plan } = prepared;
    // The whole primary key and the composite unique go with the column; the children's key to the parents' key goes too.
    expect(plan.table).toEqual(["primary key pk_parents (id is in it)", "unique constraint uq_parents_id_code (id is in it)"]);
    expect(plan.others).toEqual([{ id: C, kind: "table", name: "children", lines: ["foreign key fk_children_parent goes (it references parents.id)"] }]);
    expect(plan.blockers).toEqual([]);
    const undoBefore = services.store.getState().undo.length;
    expect(await executePartDelete(services, qc, req, prepared)).toBeNull();
    const after = await view();
    expect(after.tables.find((t) => t.key === P)!.primaryKey).toBeNull();
    expect(after.tables.find((t) => t.key === C)!.foreignKeys).toEqual([]);
    expect(services.store.getState().undo.length).toBe(undoBefore + 1);
    expect(services.store.getState().undo.at(-1)!.label).toBe("Delete column id of parents");
    expect(await services.undo.undo()).toMatchObject({ ok: true });
    const back = await view();
    expect(back.tables.find((t) => t.key === P)!.primaryKey?.columns).toEqual([PID]);
    expect(back.tables.find((t) => t.key === C)!.foreignKeys.map((f) => f.name)).toEqual(["fk_children_parent"]);

    // A column a query reads is not deleted: the plan says which query.
    const customers = back.tables.find((t) => t.name === "customers")!;
    const read = await preparePartDelete(services, qc, { database: DB, key: customers.key, part: { kind: "column", id: customers.primaryKey!.columns[0] } });
    if (typeof read === "string") throw new Error(read);
    expect(read.plan.blockers.join(" ")).toContain("Query");
  });

  it("never overwrites a change made after the plan: Delete with a newer table is a conflict, nothing written", async () => {
    const { services } = api;
    const qc = services.queryClient;
    const T = "01K7CNF0000000000000000001";
    const COL = "01K7CNF0000000000000000003";
    const created = await endpoints.createElement({
      kind: "table",
      id: T,
      name: "drafts",
      database: DB,
      columns: [
        { id: "01K7CNF0000000000000000002", name: "id", type: "int64" },
        { id: COL, name: "title", type: "string" },
      ],
      uniques: [{ id: "01K7CNF0000000000000000004", name: "uq_drafts_title", columns: [COL] }],
    } as never);
    expect(created.outcome).toBe("saved");
    const req = { database: DB, key: T, part: { kind: "column" as const, id: COL } };
    const prepared = await preparePartDelete(services, qc, req);
    if (typeof prepared === "string") throw new Error(prepared);
    expect(prepared.plan.table).toEqual(["unique constraint uq_drafts_title (title is in it)"]);
    // Someone adds a comment meanwhile.
    const now = await qc.fetchQuery({ ...elementQuery(T), staleTime: 0 });
    expect((await endpoints.saveElement(T, { ...(now.json as unknown as Json), comment: "kept" } as unknown as ModelJson, now.hash)).outcome).toBe("saved");
    const undoBefore = services.store.getState().undo.length;
    expect(await executePartDelete(services, qc, req, prepared)).toContain("changed since the plan was made");
    const after = await doc(T);
    expect(after.comment).toBe("kept");
    expect((after.columns as Json[]).map((c) => c.name)).toEqual(["id", "title"]);
    expect(services.store.getState().undo.length).toBe(undoBefore);
  });

  it("stores a laid-out table and the laid-out tables whose keys go first, plans on the files, and takes the store back on cancel", async () => {
    const { services } = api;
    const qc = services.queryClient;
    const before = await view();
    const customers = before.tables.find((t) => t.name === "customers")!;
    const invoices = before.tables.find((t) => t.name === "invoices")!;
    expect([customers.origin, invoices.origin]).toEqual(["synthesized", "synthesized"]);
    expect(invoices.foreignKeys.some((f) => f.referencedTable === customers.key)).toBe(true);
    const req = { database: DB, key: customers.key, part: { kind: "primary-key" as const, id: "primary-key" } };
    const undoBefore = services.store.getState().undo.length;
    const prepared = await preparePartDelete(services, qc, req);
    if (typeof prepared === "string") throw new Error(prepared);
    // Both were stored to make the plan, which is made on their files: invoices' key to customers goes with the primary key.
    expect([...prepared.stored.keys()].sort()).toEqual([customers.key, invoices.key].sort());
    expect(prepared.key).toBe(prepared.stored.get(customers.key));
    const invoicesFile = prepared.stored.get(invoices.key)!;
    expect(prepared.plan.others.map((o) => o.id)).toEqual([invoicesFile]);
    expect(prepared.plan.others[0].lines.join(" ")).toContain("fk_invoices_customer_id goes");
    expect(services.store.getState().undo.at(-1)!.label).toBe(prepared.label);

    // Cancel: the store is taken back, and no step is left in the history.
    await cancelPartDelete(services, qc, req, prepared);
    expect(services.store.getState().undo.length).toBe(undoBefore);
    expect(services.store.getState().redo.at(-1)?.label).not.toBe(prepared.label);
    const back = await view();
    expect(back.tables.find((t) => t.name === "customers")!.origin).toBe("synthesized");
    expect(back.tables.find((t) => t.name === "invoices")!.origin).toBe("synthesized");

    // Asked again and deleted: the store and the delete are one undo step; undo lays both tables out again.
    const again = await preparePartDelete(services, qc, req);
    if (typeof again === "string") throw new Error(again);
    const files = new Map(again.stored);
    expect(await executePartDelete(services, qc, req, again)).toBeNull();
    expect(services.store.getState().undo.length).toBe(undoBefore + 1);
    expect(services.store.getState().undo.at(-1)!.label).toBe(again.label);
    const deleted = await view();
    expect((await doc(again.key)).primaryKey).toBeUndefined();
    const invoiceKeys = ((await doc(files.get(invoices.key)!)).foreignKeys as Json[] | undefined) ?? [];
    expect(invoiceKeys.some((f) => f.name === "fk_invoices_customer_id")).toBe(false);
    expect(deleted.tables.find((t) => t.name === "invoices")!.origin).not.toBe("synthesized");
    expect(await services.undo.undo()).toMatchObject({ ok: true });
    expect((await view()).tables.find((t) => t.name === "customers")!.origin).toBe("synthesized");
  });

  it("renames a column and what names it in the table's SQL, one undo step", async () => {
    const { services } = api;
    const qc = services.queryClient;
    const status = await endpoints.getMaterializeStatus(DB);
    const customers = (await view()).tables.find((t) => t.name === "customers")!;
    expect((await storeTables(services, qc, DB, new Map([[customers.key, CUSTOMER]]), "Store customers")).ok).toBe(true);
    expect(status.entities.length).toBeGreaterThan(0);
    const file = storedFileOf(customers.key)!;
    const stored = await doc(file);
    const email = (stored.columns as Json[]).find((c) => c.name === "email")!;
    // A check and a partial index that read email.
    expect(
      await updateTable(services, qc, DB, file, "Add a check", (d) => {
        d.checks = [{ id: "01K7CHK0000000000000000001", name: "ck_email", expression: { "*": "email LIKE '%@%' AND length(email) > 3" } }];
        d.indexes = [
          ...((d.indexes as Json[]) ?? []),
          { id: "01K7N0X0000000000000000001", name: "ix_email_set", columns: [{ column: email.id }], where: "email IS NOT NULL" },
        ];
      }),
      services.store.getState().notice?.text,
    ).toBe(true);
    const plan = planColumnRename({
      table: await doc(file),
      column: String(email.id),
      to: "email_address",
      docs: [],
      tables: (await view()).tables as TableView[],
    });
    expect(plan.rewritten).toEqual(["check ck_email", "the filter of index ix_email_set"]);
    expect(await renameTableColumn(services, qc, { database: DB, key: file, column: String(email.id), to: "email_address" })).toBe(true);
    const renamed = await doc(file);
    expect((renamed.checks as Json[])[0].expression).toEqual({ "*": "email_address LIKE '%@%' AND length(email_address) > 3" });
    expect((renamed.indexes as Json[]).find((i) => i.name === "ix_email_set")!.where).toBe("email_address IS NOT NULL");
    const top = services.store.getState().undo.at(-1)!;
    expect(top.label).toBe("Rename column email of customers to email_address");
    expect(await services.undo.undo()).toMatchObject({ ok: true });
    expect(((await doc(file)).checks as Json[])[0].expression).toEqual({ "*": "email LIKE '%@%' AND length(email) > 3" });
  });

  it("renames a view's declared column in its body too when CREATE VIEW names no column list, one undo step", async () => {
    const { services } = api;
    const qc = services.queryClient;
    const VIEW = "01J92P0V1YZ4YP352KD1A50FXS";
    expect(await renameViewColumn(services, qc, VIEW, 1, "invoice_number")).toBe(true);
    const renamed = await doc(VIEW);
    expect((renamed.columns as Json[])[1].name).toBe("invoice_number");
    expect(renamed.body).toEqual({ "*": "select id, number AS invoice_number, issued_on from billing.invoices where status = 1" });
    expect(services.store.getState().notice?.text).toContain("Also rewritten: the body of view outstanding_invoices.");
    expect(await services.undo.undo()).toMatchObject({ ok: true });
    expect((await doc(VIEW)).body).toEqual({ "*": "select id, number, issued_on from billing.invoices where status = 1" });
  });

  it("makes each schema operation one undo step", async () => {
    const { services } = api;
    const qc = services.queryClient;
    const schemasOf = async () => (((await doc(DB)).schemas as Json[] | undefined) ?? []).map((x) => String(x.name));
    const before = await schemasOf();
    expect(await runSchemaOperation(services, qc, { op: "add-schema", id: DB, name: "archive" })).toBeNull();
    expect(await schemasOf()).toContain("archive");
    expect(services.store.getState().undo.at(-1)!.label).toBe("New schema archive");
    const added = (((await doc(DB)).schemas as Json[]) ?? []).find((x) => x.name === "archive")!;
    expect(await runSchemaOperation(services, qc, { op: "rename-schema", id: DB, schema: String(added.id), name: "attic" })).toBeNull();
    expect(services.store.getState().undo.at(-1)!.label).toBe("Rename schema archive to attic");
    expect(await services.undo.undo()).toMatchObject({ ok: true });
    expect(await schemasOf()).toContain("archive");
    expect(await services.undo.undo()).toMatchObject({ ok: true });
    expect(await schemasOf()).toEqual(before);
  });

  it("labels a table's undo step with the gesture's words, and says false when the table's file is in conflict", async () => {
    const { services } = api;
    const qc = services.queryClient;
    const T = "01K7TAB0000000000000000001";
    const created = await endpoints.createElement({
      kind: "table",
      id: T,
      name: "notes_archive",
      database: DB,
      columns: [{ id: "01K7TAB0000000000000000002", name: "id", type: "int64" }],
    } as never);
    expect(created.outcome).toBe("saved");
    expect(
      await updateTable(
        services,
        qc,
        DB,
        T,
        "Add column body",
        (d) => void (d.columns as Json[]).push({ id: "01K7TAB0000000000000000003", name: "body", type: "text" }),
      ),
    ).toBe(true);
    expect(services.store.getState().undo.at(-1)!.label).toBe("Add column body");
    // A save from elsewhere while a stale draft waits: the draft is in conflict, and the next write says so instead of saving.
    const current = await qc.fetchQuery({ ...elementQuery(T), staleTime: 0 });
    services.drafts.edit(T, (j) => void ((j as unknown as Json).comment = "mine"), { base: { ...current, hash: "stale" } });
    await services.drafts.flush(T);
    expect(services.store.getState().drafts[T]?.status).toBe("conflict");
    expect(await updateTable(services, qc, DB, T, "Add a comment", (d) => void (d.comment = "again"))).toBe(false);
    expect(services.store.getState().notice?.text).toContain("changed on disk");
  });
});
