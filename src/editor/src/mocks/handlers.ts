// The stateful mock handlers (phase2-design.md 4.4, layer 2), typed by the generated `paths`
// through openapi-msw. They run on MockBackend and override the baseline for every operation the
// editor uses. They also apply the sign-in gate's request rules the SPA must satisfy (415 for a
// POST or PUT under /api/ that is not application/json, 428 without If-Match), so the client's
// behaviour is exercised in mock mode too.
import { createOpenApiHttp } from "openapi-msw";
import { delay, http as rawHttp, HttpResponse, type HttpHandler } from "msw";
import type { paths, Problem } from "@/api/types";
import type { MockBackend } from "./backend";
import { baselineHandlers } from "./baseline";
import { mentions, recordings, replayable, type Recording } from "./recorded";

type Json = Record<string, unknown>;

export function problem(status: number, code: Problem["code"], title: string, detail?: string): HttpResponse<Problem> {
  const body: Problem = { type: "about:blank", title, status, code, ...(detail ? { detail } : {}) };
  return HttpResponse.json(body, { status, headers: { "Content-Type": "application/problem+json" } });
}

function etag(hash: string): Record<string, string> {
  return { ETag: `"${hash}"` };
}

function ifMatch(request: Request): string | null {
  return request.headers.get("If-Match");
}

async function jsonBody(request: Request): Promise<{ ok: true; value: Json } | { ok: false; response: HttpResponse<Problem> }> {
  const text = await request.text();
  if (text.trim() === "") return { ok: true, value: {} };
  try {
    return { ok: true, value: JSON.parse(text) as Json };
  } catch (error) {
    return { ok: false, response: problem(400, "bad-request", "The request body is not valid JSON.", String((error as Error).message)) };
  }
}

/**
 * @param baseUrl "" in the browser (paths resolve against the page); an absolute origin under
 * Node, where MSW does not match relative handler paths.
 */
export function statefulHandlers(backend: MockBackend, baseUrl = "", recorded: ReadonlyMap<string, Recording> = recordings()): HttpHandler[] {
  const { model, generation, jobs } = backend;
  const http = createOpenApiHttp<paths>({ baseUrl });

  // Engine recordings (src/mocks/recorded/, from P2-F) are of the billing fixture: they answer the
  // plan, diff, preview and database-view operations while the model is still that fixture, and
  // the in-house resolver answers once it has been edited (phase2-design.md 4.4).
  const seedVersion = model.version;
  const fixtureSeed = !backend.scenarios.has("empty") && !backend.scenarios.has("large");
  const pristine = () => ({ pristine: fixtureSeed && model.version === seedVersion });
  const answer = (r: Recording) =>
    /json/.test(r.contentType)
      ? HttpResponse.json(r.body as never, { status: r.status, headers: { "Content-Type": r.contentType } })
      : new HttpResponse(String(r.body ?? ""), { status: r.status, headers: { "Content-Type": r.contentType } });

  const gate: HttpHandler = rawHttp.all(`${baseUrl}/api/*`, async ({ request }) => {
    if (backend.latencyMs) await delay(backend.latencyMs);
    const url = new URL(request.url);
    const anonymous = url.pathname === "/api/health" || url.pathname === "/api/session";
    if (backend.scenarios.has("unauthenticated") && !anonymous) return problem(401, "unauthenticated", "Sign in to use the editor.");
    if (backend.scenarios.has("unauthenticated") && url.pathname === "/api/session" && request.method === "GET")
      return problem(401, "unauthenticated", "Sign in to use the editor.");
    if ((request.method === "POST" || request.method === "PUT") && !(url.pathname === "/api/session" && request.method === "POST")) {
      const type = request.headers.get("Content-Type") ?? "";
      if (!/^application\/json\b/i.test(type)) return problem(415, "unsupported-media-type", "Send the body as application/json.");
    }
    return undefined; // fall through to the operation's handler
  });

  const saveLike = async (id: string, request: Request, diagramOnly: boolean) => {
    const hash = ifMatch(request);
    if (!hash) return problem(428, "precondition-required", "Send the hash you loaded in If-Match.");
    const body = await jsonBody(request);
    if (!body.ok) {
      if (!diagramOnly) return body.response;
      // saveDiagram declares no 400: a body that is not JSON is an invalid document.
      const diagnostic = {
        rule: "MQ1002",
        severity: "error" as const,
        message: "The request body is not valid JSON.",
        elementId: id,
        filePath: null,
        jsonPointer: "",
        line: null,
        column: null,
      };
      return HttpResponse.json({ outcome: "invalid", id, hash: null, current: null, diagnostics: [diagnostic], referrers: [], changes: null }, { status: 422 });
    }
    const existing = model.entries.get(id);
    if (diagramOnly && existing && existing.json.kind !== "diagram") return problem(404, "not-a-diagram", `${id} is not a diagram.`);
    if (diagramOnly && !existing) return problem(404, "not-found", `No element has the id ${id}.`);
    if (backend.conflictPending && existing && !diagramOnly) {
      backend.conflictPending = false;
      model.externalEdit(id, (json) => {
        json.description = "Edited on disk while you were editing.";
      });
    }
    const result = model.save(id, body.value, hash);
    const status = { saved: 200, conflict: 409, invalid: 422, "not-found": 404, referenced: 409 }[result.outcome];
    return HttpResponse.json(result, { status, headers: result.hash && result.outcome === "saved" ? etag(result.hash) : {} });
  };

  return [
    gate,
    http.get("/api/health", ({ response }) =>
      response(200).json({
        status: "ok",
        engineVersion: "1.0.0",
        engineBuild: "1.0.0-alpha.1.mock",
        modelLoaded: true,
        elements: model.entries.size,
        worker: "running",
        watcher: "watching",
      }),
    ),
    http.get("/api/session", ({ response }) =>
      response(200).json({ user: { name: "local", displayName: "Local developer", role: "admin" }, via: "local", mode: "local" }),
    ),
    http.post("/api/session", async ({ request, response }) => {
      const text = await request.text();
      const token =
        /token=([^&]+)/.exec(text)?.[1] ??
        (() => {
          try {
            return (JSON.parse(text) as { token?: string }).token;
          } catch {
            return undefined;
          }
        })();
      if (!token) return problem(401, "bad-token", "That is not the editor token.");
      return response(200).json({ user: { name: "token", displayName: "Token user", role: "admin" }, via: "cookie", mode: "local" });
    }),
    http.delete("/api/session", ({ response }) => response(204).empty()),
    http.get("/api/project", ({ response }) => response(200).json(model.project())),
    http.get("/api/project/settings", () => {
      const doc = model.settingsDocument();
      return HttpResponse.json(doc, { headers: etag(doc.hash) });
    }),
    http.put("/api/project/settings", async ({ request }) => {
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the hash you loaded in If-Match.");
      const body = await jsonBody(request);
      // updateSettings declares no 400: a body that is not JSON fails the schema (422).
      const { status, body: result } = model.saveSettings(body.ok ? body.value : (null as unknown as Json), hash);
      return HttpResponse.json(result, { status, headers: result.hash && status === 200 ? etag(result.hash) : {} });
    }),
    http.get("/api/model/index", () => HttpResponse.json(model.index(), { headers: { "Cache-Control": "no-store" } })),
    http.post("/api/model/elements", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      if (typeof body.value.kind !== "string") return problem(400, "bad-request", "The body needs a kind.");
      const result = model.create(body.value);
      if (result.outcome !== "saved") return HttpResponse.json(result, { status: 422 });
      return HttpResponse.json(result, { status: 201, headers: { ...etag(result.hash!), Location: `/api/model/elements/${result.id}` } });
    }),
    http.get("/api/model/elements/{id}", ({ params, request }) => {
      const doc = model.get(params.id);
      if (!doc) return problem(404, "not-found", `No element has the id ${params.id}.`);
      const none = request.headers.get("If-None-Match");
      if (none && none.replace(/"/g, "") === doc.hash) return new HttpResponse(null, { status: 304, headers: etag(doc.hash) });
      return HttpResponse.json(doc, { headers: { ...etag(doc.hash), "Cache-Control": "no-store" } });
    }),
    http.put("/api/model/elements/{id}", ({ params, request }) => saveLike(params.id, request, false) as never),
    http.delete("/api/model/elements/{id}", ({ params, request }) => {
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the hash you loaded in If-Match.");
      const resolution = new URL(request.url).searchParams.get("resolution") ?? "refuse";
      if (resolution !== "refuse" && resolution !== "remove-references")
        return problem(400, "bad-request", `resolution must be refuse or remove-references, not '${resolution}'.`);
      const result = model.delete(params.id, hash, resolution);
      const status = { saved: 200, conflict: 409, invalid: 422, "not-found": 404, referenced: 409 }[result.outcome];
      return HttpResponse.json(result, { status });
    }),
    http.post("/api/model/batch", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const { status, body: result } = model.batch(body.value);
      return HttpResponse.json(result, { status });
    }),
    http.get("/api/model/references/{id}", ({ params }) => {
      const refs = model.references(params.id);
      if (!refs) return problem(404, "not-found", `No element has the id ${params.id}.`);
      return HttpResponse.json(refs);
    }),
    http.post("/api/validate", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const ids = body.value.elementIds;
      if (ids !== undefined && ids !== null && !Array.isArray(ids)) return problem(400, "bad-request", "elementIds must be an array.");
      return HttpResponse.json(model.validate(body.value as never));
    }),
    http.get("/api/diagrams/{id}", ({ params }) => {
      const entry = model.entries.get(params.id);
      if (!entry) return problem(404, "not-found", `No element has the id ${params.id}.`);
      if (entry.json.kind !== "diagram") return problem(404, "not-a-diagram", `${params.id} is not a diagram.`);
      const doc = model.document(entry);
      return HttpResponse.json(doc, { headers: etag(doc.hash) });
    }),
    http.put("/api/diagrams/{id}", ({ params, request }) => saveLike(params.id, request, true) as never),
    http.get("/api/databases/{id}/view", ({ params }) => {
      const rec = replayable(recorded, "getDatabaseView", pristine(), (r) => mentions(r, params.id));
      if (rec) return answer(rec) as never;
      const result = generation.databaseView(params.id);
      if (!result)
        return model.entries.has(params.id)
          ? problem(404, "not-a-database", `${params.id} is not a database.`)
          : problem(404, "not-found", `No element has the id ${params.id}.`);
      return HttpResponse.json(result);
    }),
    http.post("/api/generate/plan", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const job = jobs.enqueue("plan", { plan: body.value });
      if (!job)
        return HttpResponse.json(
          { type: "about:blank", title: "The job queue is full.", status: 503, code: "queue-full" },
          { status: 503, headers: { "Content-Type": "application/problem+json", "Retry-After": "5" } },
        );
      return HttpResponse.json(job, { status: 202, headers: { Location: `/api/jobs/${job.id}` } });
    }),
    http.get("/api/generate/plan/{id}", ({ params, request }) => {
      const units = new URL(request.url).searchParams.get("units") === "true";
      const rec = replayable(recorded, "getPlan", pristine(), (r) => mentions(r, params.id));
      if (rec) return answer(rec) as never;
      const plan = generation.getPlan(params.id, units);
      if (!plan) return problem(404, "not-found", `No plan has the id ${params.id}.`);
      return HttpResponse.json(plan);
    }),
    http.get("/api/generate/plan/{id}/diff", ({ params, request }) => {
      const path = new URL(request.url).searchParams.get("path");
      if (!path) return problem(404, "not-found", "Name the file with ?path=; the plan has no file without a path.");
      const plan = replayable(recorded, "getPlan", pristine(), (r) => mentions(r, params.id));
      const rec = plan && replayable(recorded, "getPlanDiff", pristine(), (r) => mentions(r, path));
      if (rec) return answer(rec) as never;
      const diff = generation.diff(params.id, path);
      if (diff === null) return problem(404, "not-found", `Plan ${params.id} has no file ${path}.`);
      return new HttpResponse(diff, { headers: { "Content-Type": "text/x-diff; charset=utf-8" } });
    }),
    http.post("/api/generate/apply", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      if (typeof body.value.planId !== "string") return problem(400, "bad-request", "Name the plan with planId.");
      const job = jobs.enqueue("apply", { planId: body.value.planId });
      if (!job)
        return HttpResponse.json(
          { type: "about:blank", title: "The job queue is full.", status: 503, code: "queue-full" },
          { status: 503, headers: { "Content-Type": "application/problem+json", "Retry-After": "5" } },
        );
      return HttpResponse.json(job, { status: 202, headers: { Location: `/api/jobs/${job.id}` } });
    }),
    http.get("/api/jobs", () => HttpResponse.json(jobs.list())),
    http.get("/api/jobs/{id}", ({ params }) => {
      const job = jobs.get(params.id);
      if (!job) return problem(404, "not-found", `No job has the id ${params.id}.`);
      return HttpResponse.json(job);
    }),
    http.delete("/api/jobs/{id}", ({ params }) => {
      const outcome = jobs.cancel(params.id);
      if (outcome === "not-found") return problem(404, "not-found", `No job has the id ${params.id}.`);
      if (outcome === "finished") return problem(409, "job-finished", "The job already finished.");
      return HttpResponse.json(jobs.get(params.id)!, { status: 202 });
    }),
    http.post("/api/templates/preview", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const { pack, unit, elementId } = body.value as { pack?: string; unit?: string; elementId?: string | null };
      if (typeof pack !== "string" || typeof unit !== "string") return problem(400, "bad-request", "Name the pack and the unit.");
      const rec = replayable(recorded, "previewTemplate", pristine(), (r) => mentions(r, unit) && (!elementId || mentions(r, elementId)));
      if (rec) return answer(rec) as never;
      const result = generation.preview(pack, unit, elementId ?? null);
      if ("problem" in result) return problem(400, "bad-request", result.problem);
      return HttpResponse.json(result);
    }),
    http.put("/api/presence", async ({ request }) => {
      const body = await jsonBody(request);
      // reportPresence declares no 400: a body that is not JSON names no connection of yours (404).
      if (!body.ok) return problem(404, "not-found", "That connection is not yours.");
      const { connectionId, elementId, workspace } = body.value as { connectionId?: string; elementId?: string | null; workspace?: string | null };
      if (!connectionId || !backend.reportPresence(connectionId, elementId ?? null, (workspace ?? null) as never))
        return problem(404, "not-found", "That connection is not yours.");
      return new HttpResponse(null, { status: 204 });
    }),
  ];
}

/** Stateful handlers first; the baseline answers anything they do not. */
export function mockHandlers(backend: MockBackend, baseUrl = ""): HttpHandler[] {
  return [...statefulHandlers(backend, baseUrl), ...baselineHandlers(baseUrl)];
}
