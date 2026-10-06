// The mock contract suite (phase2-design.md 4.4): every answer the mocks give is validated with ajv
// against the bundled openapi.json, so neither the stateful handlers nor the example baseline can
// drift from the contract.
import { describe, expect, it } from "vitest";
import { openapi, validatorAt } from "@/mocks/contract";
import { baselineOperations } from "@/mocks/baseline";
import { APPROVER, BUDGET_HOLDER, BUDGET_REJECTED, INVOICE, INVOICE_LIFECYCLE, PURCHASE_APPROVAL } from "@/mocks/model/processSeed";
import { IDS, useMockApi } from "./harness";
import type { DatabaseView } from "@/api/types";

type Json = Record<string, unknown>;
const esc = (s: string) => s.replace(/~/g, "~0").replace(/\//g, "~1");

/** Follows local $refs to the pointer of an operation's response schema for a status and type. */
function responseSchemaPointer(path: string, method: string, status: number, contentType: string): string | null {
  const root = openapi as unknown as Json;
  const at = (pointer: string): Json =>
    pointer
      .split("/")
      .slice(1)
      .reduce<Json>((node, token) => node[token.replace(/~1/g, "/").replace(/~0/g, "~")] as Json, root);
  let pointer = `/paths/${esc(path)}/${method}/responses/${status}`;
  let node = at(pointer);
  if (!node) return null;
  while (typeof node.$ref === "string") {
    pointer = node.$ref.slice(1);
    node = at(pointer);
  }
  const content = node.content as Json | undefined;
  if (!content) return null;
  const type = Object.keys(content).find((t) => contentType.startsWith(t));
  return type ? `${pointer}/content/${esc(type)}/schema` : null;
}

describe("mock contract", () => {
  const mock = useMockApi();

  async function call(method: string, path: string, template: string, body?: unknown, headers: Record<string, string> = {}) {
    const response = await fetch(mock.url(path), {
      method: method.toUpperCase(),
      headers: { "Content-Type": "application/json", ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const contentType = response.headers.get("Content-Type") ?? "";
    const declared = (openapi.paths as unknown as Record<string, Record<string, { responses: Json }>>)[template][method].responses;
    expect(Object.keys(declared), `${method} ${path} answered ${response.status}, which the contract does not list`).toContain(String(response.status));
    if (/json/.test(contentType)) {
      const pointer = responseSchemaPointer(template, method, response.status, contentType);
      expect(pointer, `${method} ${path} ${response.status} ${contentType} has no schema`).not.toBeNull();
      const payload = await response.json();
      const validate = validatorAt(pointer!);
      expect(validate(payload), `${method} ${path}: ${JSON.stringify(validate.errors?.slice(0, 3))}`).toBe(true);
      return { status: response.status, payload, response };
    }
    return { status: response.status, payload: await response.text(), response };
  }

  it("answers the read operations in contract shape", async () => {
    const { payload: index } = await call("get", "/api/model/index", "/api/model/index");
    const database = (index as { id: string; kind: string }[]).find((e) => e.kind === "database")!;
    await call("get", "/api/health", "/api/health");
    await call("get", "/api/session", "/api/session");
    await call("get", "/api/project", "/api/project");
    await call("get", "/api/project/settings", "/api/project/settings");
    await call("get", `/api/model/elements/${IDS.invoice}`, "/api/model/elements/{id}");
    await call("get", `/api/model/references/${IDS.invoice}`, "/api/model/references/{id}");
    await call("get", `/api/diagrams/${IDS.overview}`, "/api/diagrams/{id}");
    await call("get", `/api/databases/${database.id}/view`, "/api/databases/{id}/view");
    await call("post", "/api/validate", "/api/validate", {});
    await call("get", "/api/jobs", "/api/jobs");
  });

  it("serves each table's, view's and sequence's own annotations in the database view, never its entity's", async () => {
    const { payload: index } = await call("get", "/api/model/index", "/api/model/index");
    const database = (index as { id: string; kind: string; name: string }[]).find((e) => e.kind === "database" && e.name === "main")!;
    const { payload } = await call("get", `/api/databases/${database.id}/view`, "/api/databases/{id}/view");
    const view = (payload as { view: DatabaseView }).view;
    // The invoice table's overlay: its annotations, the audited stereotype's default properties merged under its own.
    const invoices = view.tables.find((t) => t.entityId === IDS.invoice)!;
    expect(invoices).toMatchObject({
      displayName: "Invoice register",
      pluralName: null,
      stereotypes: ["audited"],
      tags: ["billing"],
      properties: { retentionDays: 2555, tablespace: "billing_data" },
      generation: {},
    });
    // The customers table has no overlay: nothing, although the Customer entity is audited.
    const customers = view.tables.find((t) => t.name === "customers")!;
    expect(customers).toMatchObject({ displayName: null, description: null, stereotypes: [], tags: [], category: null, properties: {} });
    expect(view.views.map((v) => v.name)).toEqual(["outstanding_invoices"]);
    expect(view.sequences.find((s) => s.name === "invoice_number_seq")).toMatchObject({ start: 1000, stereotypes: [], properties: {} });
    // A column takes the annotations of its own overlay entry; the others have none, whatever their attribute carries.
    const number = invoices.columns.find((c) => c.name === "number")!;
    expect(number).toMatchObject({
      comment: "Assigned on issue.",
      description: "The number printed on the invoice; customers quote it when they pay.",
      stereotypes: ["audited"],
      properties: { classification: "internal", retentionDays: 2555 },
    });
    expect(invoices.columns.filter((c) => c.name !== "number").every((c) => c.stereotypes.length === 0 && c.description === null)).toBe(true);
    // The database carries its own annotations (none in the fixture) and lists its schemas.
    expect(view).toMatchObject({ quoting: "reserved", byConvention: "all", packages: [], stereotypes: [], displayName: null });
    expect(view.schemas).toEqual([expect.objectContaining({ name: "billing", isDefault: true, isDeclared: true, stereotypes: [] })]);
  });

  it("answers the explorer-at-scale additions (E5 to E5e) in contract shape", async () => {
    // E5: the index rows carry relation ends, diagram member counts and physical owners.
    const first = await call("get", "/api/model/index", "/api/model/index");
    const rows = first.payload as Json[];
    const relation = rows.find((r) => r.kind === "relation")!;
    expect((relation.ends as Json[]).length).toBe(2);
    expect(rows.find((r) => r.id === IDS.overview)!.memberCount).toBeGreaterThan(0);
    for (const row of rows.filter((r) => r.kind === "table" || r.kind === "mapping")) expect(typeof row.database).toBe("string");
    expect(rows.find((r) => r.id === IDS.invoice)).not.toHaveProperty("ends");
    // E5e: an ETag, no-cache, and 304 for a matching If-None-Match.
    const tag = first.response.headers.get("ETag")!;
    expect(tag).toMatch(/^"[0-9a-f]{64}"$/);
    expect(first.response.headers.get("Cache-Control")).toBe("no-cache");
    const again = await call("get", "/api/model/index", "/api/model/index", undefined, { "If-None-Match": tag });
    expect(again.status).toBe(304);
    // Weak and bare tags match too, as Api.TryReadTag reads them; the element read does the same.
    expect((await call("get", "/api/model/index", "/api/model/index", undefined, { "If-None-Match": `W/${tag}` })).status).toBe(304);
    expect((await call("get", "/api/model/index", "/api/model/index", undefined, { "If-None-Match": tag.slice(1, -1) })).status).toBe(304);
    const element = await call("get", `/api/model/elements/${IDS.invoice}`, "/api/model/elements/{id}");
    const elementTag = element.response.headers.get("ETag")!;
    const weakElement = await call("get", `/api/model/elements/${IDS.invoice}`, "/api/model/elements/{id}", undefined, { "If-None-Match": `W/${elementTag}` });
    expect(weakElement.status).toBe(304);
    // E5b: many documents in one request, each once, with the missing ids.
    const unknown = "01J92P0V0FJ23CGSNKM7P1W5V9";
    const read = await call("post", "/api/model/elements/read", "/api/model/elements/read", { ids: [IDS.payment, unknown, IDS.invoice, IDS.payment] });
    expect(((read.payload as Json).elements as { element: Json }[]).map((d) => d.element.id)).toEqual([IDS.payment, IDS.invoice]);
    expect((read.payload as Json).missing).toEqual([unknown]);
    const tooMany = await call("post", "/api/model/elements/read", "/api/model/elements/read", { ids: Array(201).fill(IDS.invoice) });
    expect(tooMany.status).toBe(400);
    // The contract's Ulid starts with 0..7 (Api.IsUlid), so a "Z..." id is a 400, not a missing id.
    const notUlid = await call("post", "/api/model/elements/read", "/api/model/elements/read", { ids: ["Z".repeat(26)] });
    expect(notUlid.status).toBe(400);
    // E5c: the table list without columns.
    const database = rows.find((r) => r.kind === "database")!;
    const tables = await call("get", `/api/databases/${database.id}/tables`, "/api/databases/{id}/tables");
    expect((tables.payload as Json).partial).toBe(false);
    expect(((tables.payload as Json).tables as Json[]).length).toBeGreaterThan(0);
    const notADatabase = await call("get", `/api/databases/${IDS.invoice}/tables`, "/api/databases/{id}/tables");
    expect(notADatabase.status).toBe(404);
    // E5f: one table with its columns.
    const firstTable = ((tables.payload as Json).tables as Json[])[0] as { key: string; columnCount: number };
    const table = await call("get", `/api/databases/${database.id}/tables/${encodeURIComponent(firstTable.key)}`, "/api/databases/{id}/tables/{key}");
    expect((table.payload as { table: { columns: unknown[] } }).table.columns.length).toBe(firstTable.columnCount);
    const missing = await call("get", `/api/databases/${database.id}/tables/no-such-table`, "/api/databases/{id}/tables/{key}");
    expect((missing.payload as { table: unknown }).table).toBeNull();
  });

  it("answers table summaries on a model with errors with partial: true, leaving out the broken entity's table", async () => {
    const database = mock.backend.model.index().find((e) => e.kind === "database")!;
    mock.backend.model.externalEdit(IDS.payment, (json) => {
      delete json.key;
    });
    const { payload } = await call("get", `/api/databases/${database.id}/tables`, "/api/databases/{id}/tables");
    const result = payload as { partial: boolean; tables: { entityId: string | null }[] };
    expect(result.partial).toBe(true);
    expect(result.tables.map((t) => t.entityId)).not.toContain(IDS.payment);
    expect(result.tables.map((t) => t.entityId)).toContain(IDS.invoice);
  });

  it("publishes model.changed with each change's new index row (E5d), in contract shape", async () => {
    mock.backend.model.externalEdit(IDS.invoice, (json) => {
      json.name = "Bill";
    });
    const event = mock.backend.realtime.published.filter((e) => e.event === "model.changed").at(-1)!;
    const validate = validatorAt("/webhooks/model.changed/post/requestBody/content/application~1json/schema");
    expect(validate(event.payload), JSON.stringify(validate.errors?.slice(0, 3))).toBe(true);
    const change = (event.payload as { changed: { id: string; hash: string; summary: Json }[] }).changed[0];
    expect(change.summary.name).toBe("Bill");
    expect(change.summary.hash).toBe(change.hash);
  });

  it("saves with If-Match and answers 409 with the current document on a stale hash", async () => {
    const { payload: doc } = await call("get", `/api/model/elements/${IDS.invoice}`, "/api/model/elements/{id}");
    const { json, hash } = doc as { json: Json; hash: string };
    const saved = await call("put", `/api/model/elements/${IDS.invoice}`, "/api/model/elements/{id}", { ...json, name: "Bill" }, { "If-Match": `"${hash}"` });
    expect(saved.status).toBe(200);
    const stale = await call("put", `/api/model/elements/${IDS.invoice}`, "/api/model/elements/{id}", { ...json, name: "Again" }, { "If-Match": `"${hash}"` });
    expect(stale.status).toBe(409);
    expect((stale.payload as { outcome: string }).outcome).toBe("conflict");
    const missing = await call("put", `/api/model/elements/${IDS.invoice}`, "/api/model/elements/{id}", json);
    expect(missing.status).toBe(428);
  });

  it("saves a custom type's native types and serves them in the database view and the resolved model", async () => {
    const emailAddress = "01J92P0V04TDYE2C73WMNXVDBV";
    const { payload: doc } = await call("get", `/api/model/elements/${emailAddress}`, "/api/model/elements/{id}");
    const { json, hash } = doc as { json: Json; hash: string };
    const nativeTypes = { postgresql: "character varying({length})", sqlserver: "nvarchar({length})" };
    const saved = await call("put", `/api/model/elements/${emailAddress}`, "/api/model/elements/{id}", { ...json, nativeTypes }, { "If-Match": `"${hash}"` });
    expect(saved.status).toBe(200);
    expect((mock.backend.model.get(emailAddress)!.json as Json).nativeTypes).toEqual(nativeTypes);
    const { payload: index } = await call("get", "/api/model/index", "/api/model/index");
    const database = (index as { id: string; kind: string; name: string }[]).find((e) => e.kind === "database" && e.name === "main")!;
    const { payload } = await call("get", `/api/databases/${database.id}/view`, "/api/databases/{id}/view");
    const customers = (payload as { view: DatabaseView }).view.tables.find((t) => t.name === "customers")!;
    expect(customers.columns.find((c) => c.name === "email")?.nativeType).toBe("character varying(254)");
    await call("get", "/api/model/resolved?scope=scalar-types", "/api/model/resolved");
  });

  it("creates, references and deletes elements in contract shape", async () => {
    const created = await call("post", "/api/model/elements", "/api/model/elements", { kind: "entity", name: "Refund", abstract: true, attributes: [] });
    expect(created.status, JSON.stringify(created.payload).slice(0, 400)).toBe(201);
    const { id, hash } = created.payload as { id: string; hash: string };
    const refused = await call("delete", `/api/model/elements/${IDS.invoice}`, "/api/model/elements/{id}", undefined, {
      "If-Match": `"${mock.backend.model.get(IDS.invoice)!.hash}"`,
    });
    expect(refused.status).toBe(409);
    const deleted = await call("delete", `/api/model/elements/${id}`, "/api/model/elements/{id}", undefined, { "If-Match": `"${hash}"` });
    expect(deleted.status).toBe(200);
  });

  /** The first string value under `key` anywhere in a payload. */
  function find(value: unknown, key: string): string | undefined {
    if (!value || typeof value !== "object") return undefined;
    for (const [k, v] of Object.entries(value as Json)) {
      if (k === key && typeof v === "string") return v;
      const inner = find(v, key);
      if (inner !== undefined) return inner;
    }
    return undefined;
  }

  async function finished(jobId: string): Promise<Json> {
    for (let i = 0; i < 200; i++) {
      const { payload } = await call("get", `/api/jobs/${jobId}`, "/api/jobs/{id}");
      const state = (payload as { state?: string; status?: string }).state ?? (payload as { status?: string }).status;
      if (state && !["queued", "running"].includes(state)) return payload as Json;
      await new Promise((r) => setTimeout(r, 10));
    }
    throw new Error(`job ${jobId} did not finish`);
  }

  it("signs in and out in contract shape", async () => {
    const signedIn = await call("post", "/api/session", "/api/session", { token: "local-token" });
    expect(signedIn.status).toBe(200);
    const refused = await call("post", "/api/session", "/api/session", {});
    expect(refused.status).toBe(401);
    const out = await call("delete", "/api/session", "/api/session");
    expect(out.status).toBe(204);
  });

  it("saves settings: 200, then 409 on the stale hash, 428 without one and 422 for a body that is not JSON", async () => {
    const { payload } = await call("get", "/api/project/settings", "/api/project/settings");
    const { json, hash } = payload as { json: Json; hash: string };
    const saved = await call("put", "/api/project/settings", "/api/project/settings", json, { "If-Match": `"${hash}"` });
    expect(saved.status).toBe(200);
    const fresh = (saved.payload as { hash: string }).hash;
    expect((await call("put", "/api/project/settings", "/api/project/settings", json, { "If-Match": `"${hash === fresh ? "stale" : hash}"` })).status).toBe(
      409,
    );
    expect((await call("put", "/api/project/settings", "/api/project/settings", json)).status).toBe(428);
    const broken = await fetch(mock.url("/api/project/settings"), {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${fresh}"` },
      body: "{",
    });
    expect(broken.status).toBe(422);
  });

  it("formats model files: 200 with what was rewritten, 400 for a path outside the model", async () => {
    const all = await call("post", "/api/model/format", "/api/model/format", {});
    expect(all.status).toBe(200);
    expect((all.payload as { formatted: string[] }).formatted).toEqual([]);
    const one = await call("post", "/api/model/format", "/api/model/format", { paths: [".maquettiste/maquettiste.json"] });
    expect(one.status).toBe(200);
    expect((await call("post", "/api/model/format", "/api/model/format", { paths: ["src/Generated/Customer.cs"] })).status).toBe(400);
  });

  it("applies a batch, and refuses a stale one with 409", async () => {
    const { payload } = await call("get", `/api/model/elements/${IDS.invoice}`, "/api/model/elements/{id}");
    const { json, hash } = payload as { json: Json; hash: string };
    const ok = await call("post", "/api/model/batch", "/api/model/batch", {
      operations: [{ op: "update", id: IDS.invoice, expectedHash: hash, element: { ...json, name: "Bill" } }],
    });
    expect(ok.status).toBe(200);
    const stale = await call("post", "/api/model/batch", "/api/model/batch", {
      operations: [{ op: "update", id: IDS.invoice, expectedHash: hash, element: json }],
    });
    expect(stale.status).toBe(409);
  });

  it("saves diagram positions: 200, 409, 428 and 422 for a body that is not JSON", async () => {
    const { payload } = await call("get", `/api/diagrams/${IDS.overview}`, "/api/diagrams/{id}");
    const doc = payload as { json?: Json; hash?: string; diagram?: { json: Json; hash: string } };
    const json = (doc.json ?? doc.diagram?.json)!;
    const hash = (doc.hash ?? doc.diagram?.hash)!;
    const saved = await call(
      "put",
      `/api/diagrams/${IDS.overview}`,
      "/api/diagrams/{id}",
      { ...json, viewport: { x: 5, y: 5, zoom: 1 } },
      { "If-Match": `"${hash}"` },
    );
    expect(saved.status).toBe(200);
    const other = { ...json, viewport: { x: 1, y: 2, zoom: 1 } };
    expect((await call("put", `/api/diagrams/${IDS.overview}`, "/api/diagrams/{id}", other, { "If-Match": `"${hash}"` })).status).toBe(409);
    expect((await call("put", `/api/diagrams/${IDS.overview}`, "/api/diagrams/{id}", json)).status).toBe(428);
    const broken = await fetch(mock.url(`/api/diagrams/${IDS.overview}`), {
      method: "PUT",
      headers: { "Content-Type": "application/json", "If-Match": `"${hash}"` },
      body: "{",
    });
    expect(broken.status).toBe(422);
  });

  it("runs a plan, serves the plan and its diffs, applies it and cancels jobs", async () => {
    const started = await call("post", "/api/generate/plan", "/api/generate/plan", {});
    expect(started.status).toBe(202);
    const jobId = (started.payload as { id: string }).id;
    const job = await finished(jobId);
    const planId = (job as { planResult?: { plan?: { id?: string } } }).planResult?.plan?.id;
    expect(planId, JSON.stringify(job).slice(0, 300)).toBeTruthy();
    const plan = await call("get", `/api/generate/plan/${planId}?units=true`, "/api/generate/plan/{id}");
    expect(plan.status).toBe(200);
    expect((await call("get", "/api/generate/plan/nope", "/api/generate/plan/{id}")).status).toBe(404);
    const path = find(plan.payload, "path")!;
    expect(path).toBeTruthy();
    const diff = await call("get", `/api/generate/plan/${planId}/diff?path=${encodeURIComponent(path)}`, "/api/generate/plan/{id}/diff");
    expect(diff.status).toBe(200);
    expect((await call("get", `/api/generate/plan/${planId}/diff`, "/api/generate/plan/{id}/diff")).status).toBe(404);
    expect((await call("get", `/api/generate/plan/${planId}/diff?path=nope`, "/api/generate/plan/{id}/diff")).status).toBe(404);

    const applied = await call("post", "/api/generate/apply", "/api/generate/apply", { planId });
    expect(applied.status).toBe(202);
    const applyId = (applied.payload as { id: string }).id;
    await finished(applyId);

    // As the engine: a plan after the apply lists every file, the skipped units' as not-rendered, and counts every kind; a forced
    // plan renders every unit, so its files come out identical (or kept) and none is not-rendered. Kept files have no diff.
    type Plan = {
      id: string;
      changes: { path: string; kind: string }[];
      counts: Record<string, number>;
      unitsRendered: Record<string, number>;
      unitsSkipped: Record<string, number>;
    };
    const replan = async (force: boolean) => {
      const started = await call("post", "/api/generate/plan", "/api/generate/plan", force ? { force } : {});
      const job = await finished((started.payload as { id: string }).id);
      const id = (job as { planResult: { plan: { id: string } } }).planResult.plan.id;
      return (await call("get", `/api/generate/plan/${id}`, "/api/generate/plan/{id}")).payload as Plan;
    };
    const total = (plan.payload as Plan).changes.length;
    const later = await replan(false);
    expect(later.changes.length).toBe(total);
    expect(Object.values(later.counts).reduce((a, b) => a + b, 0)).toBe(total);
    expect(later.counts["not-rendered"]).toBeGreaterThan(0);
    expect(later.counts.added + later.counts.modified + later.counts.deleted).toBe(0);
    expect(later.unitsSkipped.unchanged).toBeGreaterThan(0);
    const kept = later.changes.find((c) => c.kind === "kept")!;
    expect(kept).toBeTruthy();
    expect((await call("get", `/api/generate/plan/${later.id}/diff?path=${encodeURIComponent(kept.path)}`, "/api/generate/plan/{id}/diff")).payload).toBe("");
    const forced = await replan(true);
    expect(forced.counts["not-rendered"]).toBe(0);
    expect(forced.counts.unchanged).toBeGreaterThan(0);
    expect(forced.counts.unchanged + forced.counts.kept).toBe(total);
    expect(Object.keys(forced.unitsRendered)).toEqual(["forced"]);

    expect((await call("delete", `/api/jobs/${applyId}`, "/api/jobs/{id}")).status).toBe(409);
    expect((await call("delete", "/api/jobs/nope", "/api/jobs/{id}")).status).toBe(404);
    const again = await call("post", "/api/generate/plan", "/api/generate/plan", {});
    expect((await call("delete", `/api/jobs/${(again.payload as { id: string }).id}`, "/api/jobs/{id}")).status).toBe(202);
    expect((await call("post", "/api/generate/apply", "/api/generate/apply", {})).status).toBe(400);
  });

  it("previews a template and reports presence", async () => {
    const { payload: index } = await call("get", "/api/model/index", "/api/model/index");
    const database = (index as { id: string; kind: string }[]).find((e) => e.kind === "database")!;
    const preview = await call("post", "/api/templates/preview", "/api/templates/preview", { pack: "sql-ddl", unit: "schema", elementId: database.id });
    expect(preview.status).toBe(200);
    expect((await call("post", "/api/templates/preview", "/api/templates/preview", {})).status).toBe(400);
    await mock.backend.realtime.connect();
    const connectionId = mock.backend.realtime.connectionId!;
    expect((await call("put", "/api/presence", "/api/presence", { connectionId, elementId: IDS.invoice, workspace: "entities" })).status).toBe(204);
    expect((await call("put", "/api/presence", "/api/presence", { connectionId: "someone-else" })).status).toBe(404);
    const broken = await fetch(mock.url("/api/presence"), { method: "PUT", headers: { "Content-Type": "application/json" }, body: "{" });
    expect(broken.status).toBe(404);
  });

  it("answers the process operations and the process batch operations in contract shape", async () => {
    const id = (x: string) => `/api/processes/${x}`;
    await call("post", `${id(PURCHASE_APPROVAL)}/simulate`, "/api/processes/{id}/simulate", { scenario: BUDGET_REJECTED });
    await call("post", `${id(PURCHASE_APPROVAL)}/verify`, "/api/processes/{id}/verify", { scenarios: [BUDGET_REJECTED] });
    await call("get", `${id(PURCHASE_APPROVAL)}/export?format=xstate`, "/api/processes/{id}/export");
    const recorded = await call("post", `${id(PURCHASE_APPROVAL)}/scenarios?dryRun=true`, "/api/processes/{id}/scenarios", { name: "Probe", steps: [] });
    expect(recorded.status).toBe(200);
    const config = { id: "probe", initial: "a", states: { a: { on: { go: "b" } }, b: {} } };
    expect((await call("post", "/api/processes/import?dryRun=true", "/api/processes/import", { config })).status).toBe(200);
    expect((await call("post", "/api/processes/import?dryRun=false", "/api/processes/import", { config: "{ not json" })).status).toBe(422);
    expect((await call("post", `${id(INVOICE_LIFECYCLE)}/sync-enum`, "/api/processes/{id}/sync-enum", { dryRun: true })).status).toBe(200);
    expect((await call("post", `${id(PURCHASE_APPROVAL)}/sync-enum`, "/api/processes/{id}/sync-enum", { dryRun: true })).status).toBe(422);

    const { payload: payload0 } = await call("get", `/api/model/elements/${PURCHASE_APPROVAL}`, "/api/model/elements/{id}");
    // The reduced interpreter's answers: several inputs (an event, invoke results, a gated event, time, a refusal), a
    // scenario followed by steps, `from`, a draft document, a draft that does not validate, and a record that saves.
    const steps = [
      { event: "submit", actor: BUDGET_HOLDER },
      { input: "invoke-done", invoke: "checkBudget" },
      { input: "invoke-done", invoke: "complianceReview", actor: APPROVER },
      { event: "approve", actor: APPROVER, signer: "ann", reason: "fine" },
      { event: "approve", actor: BUDGET_HOLDER, signer: "bob" },
      { input: "time", after: "P6D" },
    ];
    const sim = await call("post", `${id(PURCHASE_APPROVAL)}/simulate`, "/api/processes/{id}/simulate", {
      start: { context: { "01JQATT0000000000000000201": 900 } },
      steps,
    });
    const result = sim.payload as { trace: { accepted: boolean; refusal: string | null }[]; gates: unknown[]; context: Record<string, unknown> };
    expect(result.trace.map((t) => t.refusal)).toEqual([null, null, null, null, null, "actor", null]);
    expect(result.gates).toHaveLength(1);
    expect(result.context["01JQATT0000000000000000202"]).toBe(1);
    const tail = await call("post", `${id(PURCHASE_APPROVAL)}/simulate`, "/api/processes/{id}/simulate", {
      scenario: BUDGET_REJECTED,
      steps: [{ event: "requestChanges", actor: BUDGET_HOLDER }],
      from: 3,
    });
    expect((tail.payload as { trace: { index: number }[] }).trace.map((t) => t.index)).toEqual([3]);
    // With a scenario, the scenario's start is used and the request's start is ignored, as the engine does.
    const scenarioStart = await call("post", `${id(PURCHASE_APPROVAL)}/simulate`, "/api/processes/{id}/simulate", {
      scenario: BUDGET_REJECTED,
      start: { context: { "01JQATT0000000000000000201": 1 } },
      steps: [],
    });
    expect((scenarioStart.payload as { context: Record<string, unknown> }).context["01JQATT0000000000000000201"]).toBe(25000);
    const draft = { ...(payload0 as { json: Record<string, unknown> }).json, name: "Draft" };
    expect((await call("post", `${id(PURCHASE_APPROVAL)}/simulate`, "/api/processes/{id}/simulate", { document: draft, steps })).status).toBe(200);
    expect(
      (await call("post", `${id(PURCHASE_APPROVAL)}/simulate`, "/api/processes/{id}/simulate", { document: { ...draft, states: [] }, steps })).status,
    ).toBe(422);
    const saved = await call("post", `${id(PURCHASE_APPROVAL)}/scenarios`, "/api/processes/{id}/scenarios", { name: "Recorded", steps: steps.slice(0, 5) });
    expect(saved.status).toBe(201);
    const element = (saved.payload as { element: { steps: { expect: { accepted?: boolean; states: string[] } }[]; outcome?: string } }).element;
    expect(element.steps.map((st) => st.expect.accepted ?? true)).toEqual([true, true, true, true, false]);
    expect(element.steps[3].expect.states).toEqual(["01JQSTA0000000000000000110"]);
    const { payload: scenarioDoc } = await call("get", `/api/model/elements/${(saved.payload as { id: string }).id}`, "/api/model/elements/{id}");
    expect((scenarioDoc as { json: { kind: string } }).json.kind).toBe("scenario");

    const { payload } = await call("get", `/api/model/elements/${PURCHASE_APPROVAL}`, "/api/model/elements/{id}");
    const states = ((payload as { json: { states: { id: string }[] } }).json.states ?? []).map((st) => st.id);
    const batch = (operations: unknown[]) => call("post", "/api/model/batch", "/api/model/batch", { operations });
    expect((await batch([{ op: "set-initial", id: PURCHASE_APPROVAL, target: states[1] }])).status).toBe(200);
    expect((await batch([{ op: "set-lifecycle", id: INVOICE, target: INVOICE_LIFECYCLE }])).status).toBe(200);
    expect((await batch([{ op: "refresh-scenario", id: BUDGET_REJECTED }])).status).toBe(200);
  });

  it("answers the bulk reads (documents in pages, kinds, the resolved model) in contract shape", async () => {
    const ids: string[] = [];
    let cursor: string | null = null;
    do {
      const path: string = `/api/model/elements?kind=entity&fields=name&limit=2${cursor ? `&cursor=${cursor}` : ""}`;
      const { payload } = await call("get", path, "/api/model/elements");
      const pageBody = payload as { items: { id: string; json: Json }[]; next: string | null };
      for (const item of pageBody.items) {
        expect(Object.keys(item.json).sort()).toEqual(["id", "kind", "name"]);
        ids.push(item.id);
      }
      cursor = pageBody.next;
    } while (cursor);
    const index = mock.backend.model.index().filter((r) => r.kind === "entity");
    expect(ids.sort()).toEqual(index.map((r) => r.id).sort());
    const read = await call("get", `/api/model/elements?ids=${IDS.invoice},01J92P0V0FJ23CGSNKM7P1W5V9`, "/api/model/elements");
    expect((read.payload as { missing: string[] }).missing).toEqual(["01J92P0V0FJ23CGSNKM7P1W5V9"]);
    expect((await call("get", "/api/model/elements?limit=1001", "/api/model/elements")).status).toBe(400);
    expect((await call("get", "/api/model/elements?cursor=nope", "/api/model/elements")).status).toBe(400);

    const { payload: kinds } = await call("get", "/api/model/kinds?by=package", "/api/model/kinds");
    expect((kinds as { total: number }).total).toBe(mock.backend.model.index().length);

    const page = await call("get", "/api/model/index?kind=entity&limit=2", "/api/model/index");
    expect((page.payload as unknown[]).length).toBe(2);
    expect(page.response.headers.get("Link")).toMatch(/^<\/api\/model\/index\?.*cursor=.*>; rel="next"$/);

    const { payload: resolved } = await call("get", "/api/model/resolved?scope=entities", "/api/model/resolved");
    expect((resolved as { items: { kind: string }[] }).items.length).toBe(index.length);
    const database = mock.backend.model.index().find((r) => r.kind === "database")!;
    await call("get", `/api/model/resolved?scope=tables&database=${database.id}&limit=1000`, "/api/model/resolved");
    expect((await call("get", "/api/model/resolved?scope=widgets", "/api/model/resolved")).status).toBe(400);
    expect((await call("get", `/api/model/resolved?database=${IDS.invoice}`, "/api/model/resolved")).status).toBe(404);
  });

  it("answers the snapshot operations in contract shape, and a restore brings the model back", async () => {
    const { status, payload: created } = await call("post", "/api/snapshots", "/api/snapshots", { name: "Before" });
    expect(status).toBe(201);
    const id = (created as { id: string }).id;
    await call("get", "/api/snapshots", "/api/snapshots");
    await call("get", `/api/snapshots/${id}`, "/api/snapshots/{id}");
    const { payload: patched } = await call("patch", `/api/snapshots/${id}`, "/api/snapshots/{id}", { published: true });
    expect((patched as { published: boolean }).published).toBe(true);

    const { payload: element } = await call("get", `/api/model/elements/${IDS.payment}`, "/api/model/elements/{id}");
    const document = element as { hash: string; json: Record<string, unknown> };
    await call(
      "put",
      `/api/model/elements/${IDS.payment}`,
      "/api/model/elements/{id}",
      { ...document.json, name: "Settlement" },
      { "If-Match": `"${document.hash}"` },
    );

    const { payload: compared } = await call("get", `/api/snapshots/compare?from=${id}`, "/api/snapshots/compare");
    expect((compared as { elements: { id: string; previousName?: string }[] }).elements).toEqual([
      expect.objectContaining({ id: IDS.payment, previousName: "Payment" }),
    ]);
    const { payload: detail } = await call("get", `/api/snapshots/compare/element?from=${id}&id=${IDS.payment}`, "/api/snapshots/compare/element");
    expect((detail as { fields: { pointer: string }[] }).fields.map((f) => f.pointer)).toContain("/name");

    const { payload: restored } = await call("post", `/api/snapshots/${id}/restore`, "/api/snapshots/{id}/restore", {});
    expect((restored as { safety: { origin: string } }).safety.origin).toBe("before-restore");
    expect(mock.backend.model.get(IDS.payment)!.json.name).toBe("Payment");
    expect((await call("get", "/api/snapshots/missing-20260101-000000", "/api/snapshots/{id}")).status).toBe(404);
    expect((await call("delete", `/api/snapshots/${id}`, "/api/snapshots/{id}")).status).toBe(204);
  });

  it("answers every contract operation from the baseline (first example), in contract shape", () => {
    const operations = baselineOperations();
    const declared = Object.entries(openapi.paths).flatMap(([, ops]) => Object.keys(ops).filter((m) => ["get", "put", "post", "delete", "patch"].includes(m)));
    expect(operations.length).toBe(declared.length);
    for (const op of operations) {
      const responses = (openapi.paths as unknown as Record<string, Record<string, { responses: Json }>>)[op.path][op.method].responses;
      expect(Object.keys(responses), `${op.operationId} baseline status ${op.status}`).toContain(String(op.status));
      if (op.contentType === null || !/json/.test(op.contentType)) continue;
      const pointer = responseSchemaPointer(op.path, op.method, op.status, op.contentType);
      expect(pointer, `${op.operationId} ${op.status} has no schema`).not.toBeNull();
      const validate = validatorAt(pointer!);
      expect(validate(op.body), `${op.operationId}: ${JSON.stringify(validate.errors?.slice(0, 3))}`).toBe(true);
    }
  });
});
