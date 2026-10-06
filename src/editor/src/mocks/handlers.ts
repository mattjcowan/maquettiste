// The stateful mock handlers (phase2-design.md 4.4, layer 2), typed by the generated `paths`
// through openapi-msw. They run on MockBackend and override the baseline for every operation the
// editor uses. They also apply the sign-in gate's request rules the SPA must satisfy (415 for a
// POST or PUT under /api/ that is not application/json, 428 without If-Match), so the client's
// behaviour is exercised in mock mode too.
import { sha256Hex } from "@/lib/sha256";
import type { components } from "@/api/schema";
import { createOpenApiHttp } from "openapi-msw";
import { delay, http as rawHttp, HttpResponse, type HttpHandler } from "msw";
import type { paths, Problem } from "@/api/types";
import type { MockBackend } from "./backend";
import { baselineHandlers } from "./baseline";
import { mentions, recordings, replayable, type Recording } from "./recorded";
import { validPackPath } from "./model/packs";
import { validExtensionPath } from "./model/extensions";
import { BAD_CURSOR, filterOf, filterRows, isEmptyFilter, kinds, MAX_LIMIT, page, parseLimit, trim } from "./model/bulk";
import type { DatabaseView, ResolvedRecord } from "@/api/types";
import { resolveQueries } from "./model/queries";
import { bindingSql } from "./model/bindings";
import { parseDialect } from "./model/querySql";
import { isUlid, readTag } from "./wire";
import { processHandlers } from "./processHandlers";
import { assistHandlers } from "./assist";
import { snapshotHandlers } from "./snapshots";
// Recorded by the functions test of GET /api/validation/rules, which fails when the catalog changes without a new recording.
import validationRules from "./recorded/validation-rules.json";

type Json = Record<string, unknown>;

export function problem(status: number, code: Problem["code"], title: string, detail?: string): HttpResponse<Problem> {
  const body: Problem = { type: "about:blank", title, status, code, ...(detail ? { detail } : {}) };
  return HttpResponse.json(body, { status, headers: { "Content-Type": "application/problem+json" } });
}

const RESOLUTIONS = ["refuse", "remove-references", "delete-dependents"] as const;
function isResolution(value: unknown): value is (typeof RESOLUTIONS)[number] {
  return (RESOLUTIONS as readonly unknown[]).includes(value);
}

function etag(hash: string): Record<string, string> {
  return { ETag: `"${hash}"` };
}

function ifMatch(request: Request): string | null {
  return readTag(request.headers.get("If-Match"));
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
  const l10n = backend.localization;
  const http = createOpenApiHttp<paths>({ baseUrl });

  // Engine recordings (src/mocks/recorded/, from P2-F) are of the billing fixture: they answer the
  // plan, diff, preview and database-view operations while the model is still that fixture, and
  // the in-house resolver answers once it has been edited (phase2-design.md 4.4).
  const seedVersion = model.version;
  const fixtureSeed = !backend.seeded && !backend.scenarios.has("empty") && !backend.scenarios.has("medium");
  const pristine = () => ({ pristine: fixtureSeed && model.version === seedVersion });
  const answer = (r: Recording) =>
    /json/.test(r.contentType)
      ? HttpResponse.json(r.body as never, { status: r.status, headers: { "Content-Type": r.contentType } })
      : new HttpResponse(String(r.body ?? ""), { status: r.status, headers: { "Content-Type": r.contentType } });

  // An extension file changed: project.changed (the extension schemas are part of the project), then a validation, as the host does.
  const extensionsChanged = () => {
    backend.realtime.publish("project.changed", { settingsHash: model.settingsHash });
    backend.scheduleValidation();
  };

  const gate: HttpHandler = rawHttp.all(`${baseUrl}/api/*`, async ({ request }) => {
    if (backend.latencyMs) await delay(backend.latencyMs);
    const url = new URL(request.url);
    const anonymous = url.pathname === "/api/health" || url.pathname === "/api/session";
    if (backend.scenarios.has("unauthenticated") && !anonymous) return problem(401, "unauthenticated", "Sign in to use the editor.");
    if (backend.scenarios.has("unauthenticated") && url.pathname === "/api/session" && request.method === "GET")
      return problem(401, "unauthenticated", "Sign in to use the editor.");
    if ((request.method === "POST" || request.method === "PUT") && !(url.pathname === "/api/session" && request.method === "POST")) {
      const type = request.headers.get("Content-Type") ?? "";
      const zipImport = url.pathname === "/api/snapshots/import" && /^application\/zip\b/i.test(type);
      if (!/^application\/json\b/i.test(type) && !zipImport) return problem(415, "unsupported-media-type", "Send the body as application/json.");
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
    // First after the gate: a request with ?snapshot= is answered (or refused) before any other handler sees it.
    ...snapshotHandlers(backend, baseUrl, problem, (view) => mockHandlers(view, baseUrl)),
    ...processHandlers(backend, { baseUrl, recorded, pristine, answer, problem: problem as never }),
    ...assistHandlers(backend.assist, baseUrl),
    http.get("/api/health", ({ response }) =>
      response(200).json({
        status: "ok",
        productVersion: "0.5.3",
        build: "0.5.3-mock",
        engineVersion: "1.0.0",
        engineBuild: "0.5.3-mock",
        modelLoaded: true,
        elements: model.entries.size,
        worker: "running",
        watcher: "watching",
      }),
    ),
    http.get("/api/session", ({ response }) =>
      response(200).json({ user: { name: "local", displayName: "Local developer", role: backend.role }, via: "local", mode: "local" }),
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
    http.post("/api/project/branding/icon", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const { contentType, data } = body.value as { contentType?: string; data?: string };
      if (contentType !== "image/svg+xml" && contentType !== "image/png")
        return problem(400, "bad-request", "The request is not valid.", "contentType must be image/svg+xml or image/png.");
      let binary: string;
      try {
        binary = atob(data ?? "");
      } catch {
        return problem(400, "bad-request", "The request is not valid.", "data is not base64.");
      }
      if (binary.length > 512 * 1024) return problem(413, "too-large", "The icon is too large.", "An icon may be at most 512 KB.");
      const removed: string[] = [];
      let stored = data ?? "";
      if (contentType === "image/png") {
        if (!binary.startsWith("\x89PNG\r\n\x1a\n")) return problem(422, "invalid-icon", "The icon cannot be used.", "The file is not a PNG image.");
      } else {
        const text = new TextDecoder().decode(Uint8Array.from(binary, (c) => c.charCodeAt(0)));
        if (!/<svg[\s>]/.test(text) || !text.includes("http://www.w3.org/2000/svg"))
          return problem(422, "invalid-icon", "The icon cannot be used.", "The root element is not <svg> in the SVG namespace.");
        let clean = text;
        if (/<script[\s>]/i.test(clean)) {
          removed.push("a <script> element");
          clean = clean.replace(/<script[\s\S]*?<\/script>/gi, "");
        }
        const handler = /\s(on[a-z]+)="[^"]*"/i.exec(clean);
        if (handler) {
          removed.push(`an event handler attribute '${handler[1]}'`);
          clean = clean.replace(/\son[a-z]+="[^"]*"/gi, "");
        }
        stored = btoa(String.fromCharCode(...new TextEncoder().encode(clean)));
      }
      const hash = sha256Hex(stored);
      const icon = `branding/icon-${hash.slice(0, 12)}.${contentType === "image/png" ? "png" : "svg"}`;
      model.brandingIcons.set(icon, { contentType, data: stored, hash });
      return HttpResponse.json({ icon, contentType, hash, removed });
    }),
    http.get("/api/project/branding/icon", () => {
      const icon = model.brandingIcon();
      if (!icon) return problem(404, "not-found", "The project has no icon.");
      const bytes = Uint8Array.from(atob(icon.data), (c) => c.charCodeAt(0));
      return new HttpResponse(bytes, { headers: { "Content-Type": icon.contentType, ...etag(icon.hash), "Cache-Control": "no-cache" } }) as never;
    }),
    http.get("/api/model/index", ({ request }) => {
      // E5e: the ETag of the index, 304 on a matching If-None-Match, kept by the browser with no-cache.
      // ?locale= (reference-types-seeds-localization.md 3.8) fills displayName from the locale's chain.
      // The index filters narrow it and limit or cursor page it (no ETag then; the next page in a Link header).
      const q = new URL(request.url).searchParams;
      const locale = q.get("locale");
      const translated = !!locale && locale !== l10n.defaultLocale;
      if (translated && !l10n.isTranslated(locale)) return problem(400, "bad-request", `'${locale}' is not a declared locale.`) as never;
      const limit = parseLimit(q.get("limit"));
      if (typeof limit === "string") return problem(400, "bad-request", "The request is not valid.", limit) as never;
      const names = translated ? l10n.displayNames(locale) : null;
      const rows = names
        ? model.index().map((row) => (names[row.id] && names[row.id] !== (row.displayName ?? row.name) ? { ...row, displayName: names[row.id] } : row))
        : model.index();
      const filter = filterOf(q);
      const paged = !!q.get("limit") || !!q.get("cursor");
      if (!isEmptyFilter(filter) || paged) {
        const filtered = filterRows(rows, filter);
        if (!paged) return HttpResponse.json(filtered, { headers: { "Cache-Control": "no-store" } });
        const result = page(filtered, (r) => [r.kind, r.name, r.id], q.get("cursor"), limit);
        if (!result) return problem(400, "bad-request", "The request is not valid.", BAD_CURSOR) as never;
        const headers: Record<string, string> = { "Cache-Control": "no-store" };
        if (result.next) {
          q.set("cursor", result.next);
          headers.Link = `</api/model/index?${q.toString()}>; rel="next"`;
        }
        return HttpResponse.json(result.items, { headers });
      }
      const tag = translated ? `${model.indexTag()}-${locale}-${l10n.version}` : model.indexTag();
      const headers = { ...etag(tag), "Cache-Control": "no-cache" };
      if (readTag(request.headers.get("If-None-Match")) === tag) return new HttpResponse(null, { status: 304, headers }) as never;
      return HttpResponse.json(rows, { headers });
    }),
    http.get("/api/model/elements", ({ request }) => {
      // Documents in pages, by (kind, name, id), trimmed to fields (ModelPages.ReadElements).
      const q = new URL(request.url).searchParams;
      const limit = parseLimit(q.get("limit"));
      if (typeof limit === "string") return problem(400, "bad-request", "The request is not valid.", limit);
      const ids =
        q
          .get("ids")
          ?.split(",")
          .map((s) => s.trim())
          .filter(Boolean) ?? null;
      if (ids && ids.length > MAX_LIMIT)
        return problem(400, "bad-request", "The request is not valid.", `At most ${MAX_LIMIT} ids can be read at once; ${ids.length} were given.`);
      if (ids && !ids.every(isUlid))
        return problem(400, "bad-request", "The request is not valid.", "ids must be element or sub-element ids (uppercase ULIDs), separated by commas.");
      const fields =
        q
          .get("fields")
          ?.split(",")
          .map((s) => s.trim())
          .filter(Boolean) ?? null;
      let rows = model.index();
      const missing: string[] = [];
      if (ids && ids.length) {
        const wanted = new Set<string>();
        for (const id of ids) {
          const owner = model.owner(id);
          if (owner) wanted.add(owner.id);
          else missing.push(id);
        }
        rows = rows.filter((r) => wanted.has(r.id));
      }
      const result = page(filterRows(rows, filterOf(q)), (r) => [r.kind, r.name, r.id], q.get("cursor"), limit);
      if (!result) return problem(400, "bad-request", "The request is not valid.", BAD_CURSOR);
      const items = result.items.map((r) => {
        const doc = model.get(r.id)!;
        return { id: r.id, kind: r.kind, path: doc.path, hash: doc.hash, json: trim(doc.json as Record<string, unknown>, fields) };
      });
      return HttpResponse.json({ items, next: result.next, missing });
    }),
    http.get("/api/model/kinds", ({ request }) => {
      const by = new URL(request.url).searchParams.get("by");
      if (by && by !== "kind" && by !== "package") return problem(400, "bad-request", "The request is not valid.", `by must be kind or package, not '${by}'.`);
      return HttpResponse.json(kinds(model.index(), by === "package"));
    }),
    http.get("/api/model/resolved", ({ request }) => {
      // The engine's records, recorded from the billing fixture (getResolvedModel.json), while the model is that fixture;
      // afterwards the databases and tables from the in-house resolver (the mock has no conceptual resolver).
      const q = new URL(request.url).searchParams;
      const scope = q.get("scope") || "all";
      const scopes: Record<string, string> = {
        packages: "package",
        entities: "entity",
        relations: "relation",
        enums: "enum",
        "value-objects": "value-object",
        "scalar-types": "scalar-type",
        "reference-types": "reference-type",
        seeds: "seed",
        processes: "process",
        actors: "actor",
        scenarios: "scenario",
        databases: "database",
        tables: "table",
      };
      if (scope !== "all" && !scopes[scope])
        return problem(400, "bad-request", "The request is not valid.", `scope must be one of all, ${Object.keys(scopes).join(", ")}, not '${scope}'.`);
      const limit = parseLimit(q.get("limit"));
      if (typeof limit === "string") return problem(400, "bad-request", "The request is not valid.", limit);
      const database = q.get("database");
      if (database) {
        const entry = model.entries.get(database);
        if (!entry) return problem(404, "not-found", `No database has the id ${database}.`);
        if (entry.json.kind !== "database") return problem(404, "not-a-database", `${database} is not a database.`);
      }
      const rec = replayable(recorded, "getResolvedModel", pristine());
      let records: ResolvedRecord[];
      if (rec) {
        records = (rec.body as { items: ResolvedRecord[] }).items;
      } else {
        const views = model
          .index()
          .filter((r) => r.kind === "database")
          .map((r) => generation.databaseView(r.id)?.view)
          .filter((v) => !!v);
        records = views.map((v) => ({ ...v, kind: "database" as const }));
      }
      const databases = records.filter((r) => r.kind === "database") as Extract<ResolvedRecord, { kind: "database" }>[];
      const mappedTo = (r: ResolvedRecord) => !database || !("mappings" in r) || (r.mappings as { database: string }[]).some((m) => m.database === database);
      const selected: ResolvedRecord[] =
        scope === "tables"
          ? databases
              .filter((d) => !database || d.id === database)
              .flatMap((d) => d.tables.map((t) => ({ id: t.key, kind: "table" as const, name: t.name, database: d.id, table: t })))
          : records.filter(
              (r) =>
                (scope === "all" || r.kind === scopes[scope]) &&
                (r.kind === "database" ? !database || r.id === database : r.kind === "entity" || r.kind === "relation" ? mappedTo(r) : true),
            );
      const result = page(selected, (r) => [r.kind, r.name, r.id], q.get("cursor"), limit);
      if (!result) return problem(400, "bad-request", "The request is not valid.", BAD_CURSOR);
      return HttpResponse.json({ items: result.items, next: result.next, diagnostics: [] });
    }),
    http.get("/api/localization", () => HttpResponse.json(l10n.status() as never)),
    http.get("/api/localization/{locale}/entries", ({ params, request }) => {
      if (!l10n.isTranslated(params.locale)) return problem(404, "not-found", `'${params.locale}' is not a declared locale other than the default.`);
      const q = new URL(request.url).searchParams;
      return HttpResponse.json(
        l10n.entries(params.locale, { owner: q.get("owner"), shard: q.get("shard"), missing: q.get("missing") === "true", cursor: q.get("cursor") }) as never,
      );
    }),
    http.put("/api/localization/{locale}/entries", async ({ params, request }) => {
      if (!l10n.isTranslated(params.locale)) return problem(404, "not-found", `'${params.locale}' is not a declared locale other than the default.`);
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      if (!Array.isArray(body.value.entries)) return problem(400, "bad-request", "entries is required.");
      const result = l10n.write(params.locale, body.value.entries as Json[], (body.value.expected ?? {}) as Record<string, string>);
      backend.publishTranslations(result.locales);
      return HttpResponse.json(result.body as never, { status: result.status as 200 });
    }),
    http.get("/api/localization/{locale}/export", ({ params, request }) => {
      const q = new URL(request.url).searchParams;
      const format = q.get("format") ?? "xliff";
      if (format !== "xliff" && format !== "csv") return problem(400, "bad-request", `format must be 'xliff' or 'csv', not '${format}'.`) as never;
      if (!l10n.isTranslated(params.locale)) return problem(404, "not-found", `'${params.locale}' is not a declared locale other than the default.`) as never;
      const text = l10n.exportText(params.locale, format, q.get("shard"));
      return new HttpResponse(text, {
        headers: { "Content-Type": format === "csv" ? "text/csv; charset=utf-8" : "application/xliff+xml; charset=utf-8" },
      }) as never;
    }),
    http.post("/api/localization/{locale}/import", async ({ params, request }) => {
      if (!l10n.isTranslated(params.locale)) return problem(404, "not-found", `'${params.locale}' is not a declared locale other than the default.`);
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const { format, content } = body.value;
      if ((format !== "xliff" && format !== "csv") || typeof content !== "string")
        return problem(400, "bad-request", "format ('xliff' or 'csv') and content are required.");
      try {
        const dryRun = new URL(request.url).searchParams.get("dryRun") !== "false";
        const result = l10n.importText(params.locale, format, content, dryRun);
        backend.publishTranslations(result.locales);
        return HttpResponse.json(result.body as never, { status: result.status as 200 });
      } catch (error) {
        return problem(400, "bad-request", (error as Error).message);
      }
    }),
    http.get("/api/seeds/{id}/csv", ({ params, request }) => {
      const q = new URL(request.url).searchParams;
      const text = l10n.seedCsv(params.id, q.get("bom") === "true", q.getAll("locale"));
      if (text === null) return problem(404, "not-found", `No seed has the id ${params.id}.`) as never;
      return new HttpResponse(text, { headers: { "Content-Type": "text/csv; charset=utf-8" } }) as never;
    }),
    http.post("/api/seeds/csv", async ({ request }) => {
      const q = new URL(request.url).searchParams;
      const mode = q.get("mode") ?? "merge";
      if (mode !== "merge" && mode !== "replace") return problem(400, "bad-request", `mode must be 'merge' or 'replace', not '${mode}'.`);
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const files = body.value.files as { seed?: unknown; content?: unknown; hash?: unknown }[] | undefined;
      if (!Array.isArray(files) || !files.length || files.some((f) => typeof f.seed !== "string" || typeof f.content !== "string"))
        return problem(400, "bad-request", "files (each with seed and content) is required.");
      try {
        const typed = files.map((f) => ({ seed: f.seed as string, content: f.content as string, hash: typeof f.hash === "string" ? f.hash : null }));
        const result = l10n.importSeedsCsv(typed, mode === "replace", q.get("dryRun") !== "false");
        if (!result) return problem(404, "not-found", `No seed has the id ${typed.map((f) => f.seed).join(", ")}.`);
        return HttpResponse.json(result.body as never, { status: result.status as 200 });
      } catch (error) {
        return problem(400, "bad-request", (error as Error).message);
      }
    }),
    http.post("/api/seeds/{id}/csv", async ({ params, request }) => {
      const q = new URL(request.url).searchParams;
      const mode = q.get("mode") ?? "merge";
      if (mode !== "merge" && mode !== "replace") return problem(400, "bad-request", `mode must be 'merge' or 'replace', not '${mode}'.`);
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      if (typeof body.value.content !== "string") return problem(400, "bad-request", "content (the CSV text) is required.");
      try {
        const result = l10n.importSeedCsv(params.id, body.value.content, mode === "replace", q.get("dryRun") !== "false", ifMatch(request));
        if (!result) return problem(404, "not-found", `No seed has the id ${params.id}.`);
        return HttpResponse.json(result.body as never, { status: result.status as 200, headers: result.body.hash ? etag(result.body.hash) : {} });
      } catch (error) {
        return problem(400, "bad-request", (error as Error).message);
      }
    }),
    http.get("/api/reference-types/{id}/usage", ({ params }) => {
      const usage = l10n.usage(params.id);
      if (!usage) return problem(404, "not-found", `No reference type has the id ${params.id}.`);
      return HttpResponse.json(usage as never);
    }),
    http.post("/api/model/elements/read", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const ids = body.value.ids;
      if (!Array.isArray(ids)) return problem(400, "bad-request", "ids is required.");
      if (ids.length > 200) return problem(400, "bad-request", `At most 200 ids can be read at once; ${ids.length} were given.`);
      if (!ids.every(isUlid)) return problem(400, "bad-request", "ids must be element or sub-element ids (uppercase ULIDs).");
      return HttpResponse.json(model.readElements(ids as string[]));
    }),
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
      if (readTag(request.headers.get("If-None-Match")) === doc.hash) return new HttpResponse(null, { status: 304, headers: etag(doc.hash) });
      return HttpResponse.json(doc, { headers: { ...etag(doc.hash), "Cache-Control": "no-store" } });
    }),
    http.put("/api/model/elements/{id}", ({ params, request }) => saveLike(params.id, request, false) as never),
    http.delete("/api/model/elements/{id}", ({ params, request }) => {
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the hash you loaded in If-Match.");
      const resolution = new URL(request.url).searchParams.get("resolution") ?? "refuse";
      if (!isResolution(resolution))
        return problem(400, "bad-request", `resolution must be refuse, remove-references or delete-dependents, not '${resolution}'.`);
      const result = model.delete(params.id, hash, resolution);
      const status = { saved: 200, conflict: 409, invalid: 422, "not-found": 404, referenced: 409 }[result.outcome];
      return HttpResponse.json(result, { status });
    }),
    http.get("/api/model/elements/{id}/delete-plan", ({ params, request }) => {
      const resolution = new URL(request.url).searchParams.get("resolution") ?? "delete-dependents";
      if (!isResolution(resolution))
        return problem(400, "bad-request", `resolution must be refuse, remove-references or delete-dependents, not '${resolution}'.`);
      if (!model.entries.has(params.id)) return problem(404, "not-found", `No element has the id ${params.id}.`);
      return HttpResponse.json(model.deletePlan([params.id], resolution));
    }),
    http.post("/api/model/delete-plan", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const ids = body.value.ids;
      const resolution = body.value.resolution ?? "delete-dependents";
      if (!Array.isArray(ids) || ids.length === 0 || ids.length > 200 || !ids.every(isUlid))
        return problem(400, "bad-request", "ids must list 1 to 200 element ids (uppercase ULIDs).");
      if (!isResolution(resolution))
        return problem(400, "bad-request", `resolution must be refuse, remove-references or delete-dependents, not '${String(resolution)}'.`);
      return HttpResponse.json(model.deletePlan(ids as string[], resolution));
    }),
    http.post("/api/model/format", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const paths: unknown = body.value.paths;
      if (paths !== undefined && (!Array.isArray(paths) || paths.length > 1000 || !paths.every((p) => typeof p === "string")))
        return problem(400, "bad-request", "paths must list at most 1000 repo-relative model files.");
      const result = model.format(paths as string[] | undefined);
      if (result.refused.length)
        return problem(400, "bad-request", `Only model files can be formatted (maquettiste.json, model/**/*.json): ${result.refused.join(", ")}.`);
      return HttpResponse.json(result);
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
    http.get("/api/validation/rules", () => HttpResponse.json(validationRules as never)),
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
      if (rec) {
        // A recording made before queries existed: its queries are bound against the recorded tables (queries.ts).
        const body = rec.body as { view: DatabaseView | null; diagnostics: unknown[] };
        if (!body.view || body.view.queries || !Array.isArray(body.view.tables)) return answer(rec) as never;
        return HttpResponse.json({ ...body, view: { ...body.view, queries: resolveQueries(body.view, model.docs()).queries } } as never);
      }
      const result = generation.databaseView(params.id);
      if (!result)
        return model.entries.has(params.id)
          ? problem(404, "not-a-database", `${params.id} is not a database.`)
          : problem(404, "not-found", `No element has the id ${params.id}.`);
      return HttpResponse.json(result);
    }),
    http.get("/api/model/queries/{id}/sql", ({ params, request }) => {
      const entry = model.entries.get(params.id);
      if (!entry) return problem(404, "not-found", `No query has the id ${params.id}.`);
      if (entry.json.kind !== "query") return problem(404, "not-a-query", `${params.id} is not a query.`);
      const q = new URL(request.url).searchParams;
      const placeholder = q.get("placeholder") || "@";
      const lists = q.get("lists") || "expand";
      const dialect = q.get("dialect") || null;
      if (!["@", ":", "$"].includes(placeholder))
        return problem(400, "bad-request", "The request is not valid.", `placeholder must be @, : or $, not '${placeholder}'.`);
      if (!["expand", "any"].includes(lists)) return problem(400, "bad-request", "The request is not valid.", `lists must be expand or any, not '${lists}'.`);
      if (dialect !== null && !parseDialect(dialect))
        return problem(400, "bad-request", "The request is not valid.", `'${dialect}' is not a dialect (postgresql, sqlserver, mysql, sqlite, oracle).`);
      return HttpResponse.json(generation.querySql(params.id, dialect, { placeholder, lists }));
    }),
    http.get("/api/model/databases/{id}/materialize", ({ params }) => {
      const status = model.materializeStatus(params.id);
      if (!status)
        return model.entries.has(params.id)
          ? problem(404, "not-a-database", `${params.id} is not a database.`)
          : problem(404, "not-found", `No element has the id ${params.id}.`);
      return HttpResponse.json(status);
    }),
    http.post("/api/model/databases/{id}/materialize/preview", async ({ params, request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const op = body.value.op;
      if (op !== "materialize-tables" && op !== "materialize-entities")
        return problem(400, "bad-request", "The request is not valid.", "op must be materialize-tables or materialize-entities.");
      if (model.entries.get(params.id)?.json.kind !== "database")
        return model.entries.has(params.id)
          ? problem(404, "not-a-database", `${params.id} is not a database.`)
          : problem(404, "not-found", `No element has the id ${params.id}.`);
      return HttpResponse.json(model.previewMaterialize(params.id, body.value as never));
    }),
    http.get("/api/model/entities/{id}/bindings/{bindingId}/sql", ({ params, request }) => {
      const q = new URL(request.url).searchParams;
      const placeholder = q.get("placeholder") || "@";
      const dialect = q.get("dialect") || null;
      if (!["@", ":", "$"].includes(placeholder))
        return problem(400, "bad-request", "The request is not valid.", `placeholder must be @, : or $, not '${placeholder}'.`);
      if (dialect !== null && !parseDialect(dialect))
        return problem(400, "bad-request", "The request is not valid.", `'${dialect}' is not a dialect (postgresql, sqlserver, mysql, sqlite, oracle).`);
      const settings = model.projectSettings();
      const result = bindingSql(
        { docs: model.docs(), conventions: settings.conventions as Json, databaseConventions: settings.databases as Record<string, Json> },
        params.id,
        params.bindingId,
        dialect,
        placeholder,
      );
      if (!result) return problem(404, "not-found", `No binding ${params.bindingId} on ${params.id}.`);
      return HttpResponse.json(result);
    }),
    http.get("/api/databases/{id}/tables", ({ params }) => {
      const rec = replayable(recorded, "getDatabaseTables", pristine(), (r) => mentions(r, params.id));
      if (rec) return answer(rec) as never;
      const result = generation.databaseTables(params.id);
      if (!result)
        return model.entries.has(params.id)
          ? problem(404, "not-a-database", `${params.id} is not a database.`)
          : problem(404, "not-found", `No element has the id ${params.id}.`);
      return HttpResponse.json(result);
    }),
    http.get("/api/databases/{id}/tables/{key}", ({ params }) => {
      const rec = replayable(recorded, "getDatabaseTable", pristine(), (r) => mentions(r, params.id) && mentions(r, params.key));
      if (rec) return answer(rec) as never;
      const result = generation.databaseTable(params.id, params.key);
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
    http.delete("/api/jobs", () => {
      const cleared = jobs.clearHistory();
      backend.realtime.publish("jobs.cleared", cleared);
      return HttpResponse.json(cleared);
    }),
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
      const { pack, unit, elementId, overlay, unitOverride } = body.value as {
        pack?: string;
        unit?: string;
        elementId?: string | null;
        overlay?: Record<string, string> | null;
        unitOverride?: { id?: string; template?: string } | null;
      };
      if (typeof pack !== "string" || typeof unit !== "string") return problem(400, "bad-request", "Name the pack and the unit.");
      // A test can make every render report as slow (MockBackend.previewElapsedMs).
      const timed = <T extends { elapsedMs: number }>(result: T): T =>
        backend.previewElapsedMs === null ? result : { ...result, elapsedMs: backend.previewElapsedMs };
      if (unitOverride && unitOverride.id !== unit) return problem(400, "bad-request", "unitOverride.id must equal unit.");
      if (overlay && Object.keys(overlay).some((p) => p === "pack.json" || !validPackPath(p)))
        return problem(400, "bad-request", "overlay paths must be pack files other than pack.json.");
      if (overlay || unitOverride) {
        // Unsaved text: the mock does not run Scriban; it shows the unsaved template text as the rendered file.
        const saved = generation.preview(pack, unit, elementId ?? null);
        if ("problem" in saved) return problem(400, "bad-request", saved.problem);
        const template = unitOverride?.template ?? model.packs.find((p) => p.name === pack)?.units.find((u) => u.id === unit)?.template;
        const text = template && overlay?.[template];
        return HttpResponse.json(
          timed(text === undefined || text === null ? saved : { ...saved, files: saved.files.map((f, i) => (i === 0 ? { ...f, text } : f)) }),
        );
      }
      // MQ6026 before any recorded answer: an element outside the unit's scope never renders (engine: UnitPlanner).
      const outOfScope = generation.previewScope(pack, unit, elementId ?? null);
      if (outOfScope) return HttpResponse.json(timed(outOfScope));
      const rec = replayable(recorded, "previewTemplate", pristine(), (r) => mentions(r, unit) && (!elementId || mentions(r, elementId)));
      if (rec && backend.previewElapsedMs === null) return answer(rec) as never;
      const result = generation.preview(pack, unit, elementId ?? null);
      if ("problem" in result) return problem(400, "bad-request", result.problem);
      return HttpResponse.json(timed(result));
    }),
    http.get("/api/packs", () => HttpResponse.json(backend.packs.list())),
    http.post("/api/packs", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const { name, from } = body.value as { name?: string; from?: string | null };
      if (typeof name !== "string" || !name) return problem(400, "bad-request", "name is required.");
      const created = backend.packs.create(name, from ?? "empty");
      if ("problem" in created) return problem(400, "bad-request", created.problem);
      return HttpResponse.json(created.body, { status: created.status, headers: created.body.hash ? etag(created.body.hash) : {} }) as never;
    }),
    http.get("/api/packs/{pack}", ({ params }) => {
      const document = backend.packs.get(params.pack);
      if (!document) return problem(404, "not-found", `No pack has the name ${params.pack}.`);
      return HttpResponse.json(document, { headers: etag(document.hash) });
    }),
    http.put("/api/packs/{pack}", async ({ params, request }) => {
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the hash you loaded in If-Match.");
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const saved = backend.packs.saveManifest(params.pack, body.value as Record<string, unknown>, hash);
      // As the host does: every connection hears that the pack's files changed.
      if (saved.status === 200) backend.realtime.publish("packs.changed", { packs: [params.pack] });
      return HttpResponse.json(saved.body, { status: saved.status, headers: saved.status === 200 && saved.body.hash ? etag(saved.body.hash) : {} }) as never;
    }),
    http.delete("/api/packs/{pack}", ({ params, request }) => {
      if (!/^[a-z][a-z0-9]*(-[a-z0-9]+)*$/.test(params.pack)) return problem(400, "bad-request", `'${params.pack}' is not a pack name.`);
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the pack.json hash you loaded in If-Match.");
      const removed = backend.removePack(params.pack, hash);
      return HttpResponse.json(removed.body, { status: removed.status }) as never;
    }),
    http.post("/api/packs/{pack}/rename", async ({ params, request }) => {
      if (!/^[a-z][a-z0-9]*(-[a-z0-9]+)*$/.test(params.pack)) return problem(400, "bad-request", `'${params.pack}' is not a pack name.`);
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the pack.json hash you loaded in If-Match.");
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      if (typeof body.value.name !== "string") return problem(400, "bad-request", "name is required.");
      const renamed = backend.renamePack(params.pack, body.value.name, hash, body.value.dryRun === true);
      return HttpResponse.json(renamed.body, {
        status: renamed.status,
        headers: renamed.status === 200 && renamed.body.hash ? etag(renamed.body.hash) : {},
      }) as never;
    }),
    http.get("/api/packs/{pack}/file", ({ params, request }) => {
      const path = new URL(request.url).searchParams.get("path") ?? "";
      if (!validPackPath(path)) return problem(400, "bad-request", `'${path}' is not a pack-relative path.`);
      const file = backend.packs.readFile(params.pack, path);
      if (!file) return problem(404, "not-found", `No pack file ${params.pack}/${path}.`);
      return HttpResponse.json(file, { headers: etag(file.hash) });
    }),
    http.put("/api/packs/{pack}/file", async ({ params, request }) => {
      const path = new URL(request.url).searchParams.get("path") ?? "";
      if (!validPackPath(path) || path === "pack.json")
        return problem(400, "bad-request", "pack.json is saved whole with PUT /api/packs/{pack}; other paths must be pack-relative.");
      const create = (request.headers.get("If-None-Match") ?? "").trim() === "*";
      const hash = create ? null : ifMatch(request);
      if (!create && !hash) return problem(428, "precondition-required", "Send the hash you loaded in If-Match, or If-None-Match: * to create.");
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const { text } = body.value as { text?: string };
      if (typeof text !== "string") return problem(400, "bad-request", "text is required.");
      const written = backend.packs.writeFile(params.pack, path, text, hash);
      if (written.body.outcome === "saved") {
        backend.realtime.publish("templates.changed", { pack: params.pack, files: [{ path, hash: written.body.hash ?? null }] });
        backend.realtime.publish("packs.changed", { packs: [params.pack] });
      }
      return HttpResponse.json(written.body, {
        status: written.status,
        headers: written.body.outcome === "saved" && written.body.hash ? etag(written.body.hash) : {},
      }) as never;
    }),
    http.delete("/api/packs/{pack}/file", ({ params, request }) => {
      const path = new URL(request.url).searchParams.get("path") ?? "";
      if (!validPackPath(path) || path === "pack.json") return problem(400, "bad-request", `'${path}' cannot be deleted.`);
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the hash you loaded in If-Match.");
      const deleted = backend.packs.deleteFile(params.pack, path, hash);
      return HttpResponse.json(deleted.body, { status: deleted.status }) as never;
    }),
    http.get("/api/generate/plan/{id}/unit", ({ params, request }) => {
      const key = new URL(request.url).searchParams.get("key") ?? "";
      const unit = generation.getPlan(params.id, true)?.units.find((u) => u.key === key);
      if (!unit) return problem(404, "not-found", `Plan ${params.id} has no unit ${key}.`);
      const kinds: Record<string, string> = { e: "element", k: "kind-set", r: "referrers", s: "setting", t: "template", d: "schema-diff", l: "translation" };
      const groups = new Map<string, string[]>();
      for (const k of unit.readKeys) {
        const kind = k[1] === ":" ? (kinds[k[0]] ?? "inputs") : "inputs";
        groups.set(kind, [...(groups.get(kind) ?? []), k].sort());
      }
      const summary = unit.skipped
        ? `Skipped: its ${unit.readKeys.length} recorded inputs are unchanged since its last render, and its ${unit.outputs.length} outputs are intact.`
        : `Renders (${unit.reason ?? "reason not recorded"}): ${unit.causes[0]?.detail ?? "no recorded state to compare with"}.`;
      return HttpResponse.json({ unit, groups: [...groups].sort(([a], [b]) => a.localeCompare(b)).map(([kind, keys]) => ({ kind, keys })), summary });
    }),
    http.post("/api/templates/paths", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const value = body.value as components["schemas"]["PathsRequest"];
      if (typeof value.pack !== "string" || typeof value.unit !== "string") return problem(400, "bad-request", "pack and unit are required.");
      const paths = backend.packAuthoring.paths(value);
      if ("problem" in paths) return problem(400, "bad-request", paths.problem);
      return HttpResponse.json(paths);
    }),
    http.get("/api/templates/context", ({ request }) => {
      const query = new URL(request.url).searchParams;
      const pack = query.get("pack");
      const unit = query.get("unit");
      if (!pack || !unit) return problem(400, "bad-request", "pack and unit are required.");
      const context = backend.packAuthoring.context(pack, unit);
      if (!context) return problem(404, "not-found", `No unit ${pack}/${unit}.`);
      return HttpResponse.json(context);
    }),
    http.post("/api/packs/{pack}/file/move", async ({ params, request }) => {
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the source file's hash in If-Match.");
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const move = body.value as components["schemas"]["PackFileMove"];
      if (!validPackPath(move.from ?? "") || !validPackPath(move.to ?? "") || move.from === "pack.json" || move.to === "pack.json")
        return problem(400, "bad-request", "from and to must be pack-relative paths other than pack.json.");
      const moved = backend.packs.moveFile(params.pack, move, hash);
      return HttpResponse.json(moved.body, { status: moved.status, headers: moved.status === 200 && moved.body.hash ? etag(moved.body.hash) : {} }) as never;
    }),
    http.get("/api/extensions/files", () => HttpResponse.json(backend.extensions.list())),
    http.get("/api/extensions/file", ({ request }) => {
      const path = new URL(request.url).searchParams.get("path") ?? "";
      if (!validExtensionPath(path)) return problem(400, "bad-request", `'${path}' is not an extension file path: <name>.json or rules/<name>.js.`);
      const file = backend.extensions.read(path);
      if (!file) return problem(404, "not-found", `No extension file ${path}.`);
      return HttpResponse.json(file, { headers: etag(file.hash) });
    }),
    http.put("/api/extensions/file", async ({ request }) => {
      const path = new URL(request.url).searchParams.get("path") ?? "";
      if (!validExtensionPath(path)) return problem(400, "bad-request", `'${path}' is not an extension file path: <name>.json or rules/<name>.js.`);
      const create = (request.headers.get("If-None-Match") ?? "").trim() === "*";
      const hash = create ? null : ifMatch(request);
      if (!create && !hash) return problem(428, "precondition-required", "Send the hash you loaded in If-Match, or If-None-Match: * to create.");
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const { text } = body.value as { text?: string };
      if (typeof text !== "string") return problem(400, "bad-request", "text is required.");
      const written = backend.extensions.write(path, text, hash);
      if (written.body.outcome === "saved") extensionsChanged();
      return HttpResponse.json(written.body, {
        status: written.status,
        headers: written.body.outcome === "saved" && written.body.hash ? etag(written.body.hash) : {},
      }) as never;
    }),
    http.delete("/api/extensions/file", ({ request }) => {
      const path = new URL(request.url).searchParams.get("path") ?? "";
      if (!validExtensionPath(path)) return problem(400, "bad-request", `'${path}' is not an extension file path.`);
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the hash you loaded in If-Match.");
      const deleted = backend.extensions.delete(path, hash);
      if (deleted.body.outcome === "saved") extensionsChanged();
      return HttpResponse.json(deleted.body, { status: deleted.status }) as never;
    }),
    http.post("/api/extensions/file/move", async ({ request }) => {
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the source file's hash in If-Match.");
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const move = body.value as components["schemas"]["ExtensionFileMove"];
      if (!validExtensionPath(move.from ?? "") || !validExtensionPath(move.to ?? ""))
        return problem(400, "bad-request", "from and to must be extension file paths.");
      const moved = backend.extensions.move(move.from, move.to, hash);
      if (moved.body.outcome === "saved") extensionsChanged();
      return HttpResponse.json(moved.body, { status: moved.status, headers: moved.status === 200 && moved.body.hash ? etag(moved.body.hash) : {} }) as never;
    }),
    http.get("/api/packs/{pack}/outputs", ({ params }) => {
      const outputs = backend.packAuthoring.outputs(params.pack);
      if (!outputs) return problem(404, "not-found", `No pack has the name ${params.pack}.`);
      return HttpResponse.json(outputs);
    }),
    http.put("/api/project/settings/packs/{pack}", async ({ params, request }) => {
      const hash = ifMatch(request);
      if (!hash) return problem(428, "precondition-required", "Send the settings hash in If-Match.");
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      if (typeof body.value !== "object" || body.value === null || Array.isArray(body.value))
        return problem(400, "bad-request", "The body must be a JSON object: enabled, output, parameters.");
      const { status, body: result } = backend.packAuthoring.saveSettings(params.pack, body.value as Record<string, unknown>, hash);
      return HttpResponse.json(result, { status, headers: result.hash && status === 200 ? etag(result.hash) : {} }) as never;
    }),
    http.post("/api/generate/explain", async ({ request }) => {
      const body = await jsonBody(request);
      if (!body.ok) return body.response;
      const value = body.value as components["schemas"]["ExplainRequest"];
      if (typeof value.pack !== "string" || typeof value.unit !== "string") return problem(400, "bad-request", "pack and unit are required.");
      const answer = backend.packAuthoring.explain(value);
      if (!answer) return problem(404, "not-found", `No pack has the name ${value.pack}.`);
      return HttpResponse.json(answer);
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
