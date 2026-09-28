// The mock contract suite (phase2-design.md 4.4): every answer the mocks give is validated with ajv
// against the bundled openapi.json, so neither the stateful handlers nor the example baseline can
// drift from the contract.
import { describe, expect, it } from "vitest";
import { openapi, validatorAt } from "@/mocks/contract";
import { baselineOperations } from "@/mocks/baseline";
import { IDS, useMockApi } from "./harness";

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
