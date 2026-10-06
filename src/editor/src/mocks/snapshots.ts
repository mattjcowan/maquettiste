// The mock's model snapshots (docs/engineering/snapshots.md): a snapshot is a copy of the mock model's entries and settings
// held in memory; list, take, read, rename and publish, delete, compare (elements by id and hash, with the fields of one),
// restore (after a safety snapshot), export (a zip of the documents, snapshot-index.json and snapshot.json, as the engine lays
// it out) and import (of such a zip) answer in contract shape. Reads "as of" a snapshot (`?snapshot=`) are answered by a
// read-only mock backend built from the snapshot's documents, through the same handlers; any other request that carries the
// parameter is refused as the sign-in gate refuses it. The roles are the API's: `?mock=role-viewer`, `role-editor` or
// `role-maintainer` signs in with that role (admin otherwise), and an action above it answers 403.
import { getResponse, http as rawHttp, HttpResponse, type HttpHandler } from "msw";
import { clone, jsonEqual } from "@/lib/json";
import { readZip, writeZip } from "@/lib/zip";
import { sha256Hex } from "@/lib/sha256";
import { classifyRequest, SNAPSHOT_PARAM } from "@/api/snapshotScope";
import type { Problem, Role, SnapshotComparison, SnapshotElementDiff, SnapshotImportResult, SnapshotInfo, SnapshotRestoreResult } from "@/api/types";
import { MockBackend } from "./backend";
import { serialize, type Entry } from "./model/store";

type Json = Record<string, unknown>;
type Change = SnapshotComparison["elements"][number];
type Field = SnapshotElementDiff["fields"][number];

interface MockSnapshot {
  info: SnapshotInfo;
  entries: Entry[];
  settings: Json;
}

const libraries = new WeakMap<MockBackend, MockSnapshot[]>();
/** The read-only backend that answers a snapshot's as-of reads, built on its first read. */
const views = new WeakMap<MockSnapshot, { backend: MockBackend; handlers: HttpHandler[] }>();
const RANK: Record<Role, number> = { viewer: 0, editor: 1, maintainer: 2, admin: 3 };
const WORKING = "working";
const PREFIX = ".maquettiste/";

const kebab = (name: string) =>
  name
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replace(/[^A-Za-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .toLowerCase()
    .slice(0, 80) || "snapshot";

function stamp(date: Date): { id: string; utc: string } {
  const iso = date.toISOString().slice(0, 19);
  return { id: iso.replace(/[-:]/g, "").replace("T", "-"), utc: `${iso}Z` };
}

export function snapshotHandlers(
  backend: MockBackend,
  baseUrl: string,
  problem: (status: number, code: Problem["code"], title: string) => Response,
  handlersFor: (view: MockBackend) => HttpHandler[],
): HttpHandler[] {
  const model = backend.model;
  const forbidden = (need: Role) =>
    RANK[backend.role] >= RANK[need] ? null : problem(403, "forbidden", `This needs the ${need} role; you are signed in as ${backend.role}.`);
  const list = () => {
    let found = libraries.get(backend);
    if (!found) libraries.set(backend, (found = []));
    return found;
  };
  const find = (id: string) => list().find((s) => s.info.id === id);
  const missing = (id: string) => problem(404, "not-found", `No snapshot has the id ${id}.`);

  const freshId = (name: string, time: { id: string }) => {
    const slug = kebab(name);
    const base = slug.endsWith(`-${time.id}`) ? slug : `${slug}-${time.id}`;
    let id = base;
    for (let n = 2; find(id); n++) id = `${base}-${n}`;
    return id;
  };

  const take = (name: string, description: string, includePacks: boolean, origin: "user" | "before-restore"): SnapshotInfo => {
    const time = stamp(new Date());
    const id = freshId(name, time);
    const entries = [...model.entries.values()].map((e) => ({ ...e, json: clone(e.json) }));
    const info = describe(entries, { id, name, description, createdUtc: time.utc, origin, includesPacks: includePacks, author: "local" });
    list().unshift({ info, entries, settings: clone(model.settingsJson) });
    return info;
  };

  const describe = (
    entries: readonly Entry[],
    meta: Pick<SnapshotInfo, "id" | "name" | "description" | "createdUtc" | "origin" | "includesPacks" | "author"> & { published?: boolean },
  ): SnapshotInfo => {
    const kinds: Record<string, number> = {};
    for (const e of entries) kinds[String(e.json.kind)] = (kinds[String(e.json.kind)] ?? 0) + 1;
    return {
      ...meta,
      published: meta.published ?? false,
      modelHash: sha256Hex(entries.map((e) => `${e.path}:${e.hash}`).join("\n")),
      modelFormat: 1,
      engine: "mock",
      files: entries.length + 1,
      elements: entries.length,
      kinds: Object.fromEntries(Object.entries(kinds).sort(([a], [b]) => (a < b ? -1 : 1))),
      size: entries.reduce((n, e) => n + e.text.length, 0),
    };
  };

  /** The archive as the engine lays it out (docs/engineering/snapshots.md section 3), stored and dated 1980-01-01. */
  const archive = (snapshot: MockSnapshot): Uint8Array => {
    const encoder = new TextEncoder();
    const documents = [
      ...snapshot.entries.map((e) => ({ path: e.path.slice(PREFIX.length), text: e.text, element: e })),
      { path: "maquettiste.json", text: serialize(snapshot.settings), element: null },
    ].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
    const index = documents.map((d) => ({
      path: d.path,
      sha256: sha256Hex(d.text),
      ...(d.element ? { id: d.element.id, kind: String(d.element.json.kind), name: String(d.element.json.name ?? "") } : {}),
    }));
    return writeZip([
      ...documents.map((d) => ({ name: d.path, data: encoder.encode(d.text) })),
      { name: "snapshot-index.json", data: encoder.encode(JSON.stringify(index, null, 2) + "\n") },
      { name: "snapshot.json", data: encoder.encode(JSON.stringify({ kind: "maquettiste-model-snapshot", format: 1, ...snapshot.info }, null, 2) + "\n") },
    ]);
  };

  /** An exported archive back as a snapshot under a new id; the diagnostics when it is refused (MQ1011, MQ1001). */
  const importArchive = async (bytes: Uint8Array): Promise<SnapshotImportResult> => {
    const refuse = (message: string, filePath: string | null = null): SnapshotImportResult => ({
      snapshot: null,
      diagnostics: [{ rule: "MQ1011", severity: "error", message, elementId: null, filePath, jsonPointer: null, line: null, column: null }],
      tooLarge: false,
    });
    let files;
    try {
      files = await readZip(bytes);
    } catch (e) {
      return refuse(`The archive is not a zip: ${(e as Error).message}`);
    }
    const decoder = new TextDecoder();
    const meta = files.find((f) => f.name === "snapshot.json");
    if (!meta) return refuse("The archive has no snapshot.json.");
    let info: Json;
    try {
      info = JSON.parse(decoder.decode(meta.data)) as Json;
    } catch {
      return refuse("snapshot.json is not JSON.", "snapshot.json");
    }
    if (info.kind !== "maquettiste-model-snapshot" || info.format !== 1) return refuse("snapshot.json is not a format 1 model snapshot.", "snapshot.json");
    const entries: Entry[] = [];
    let settings: Json = { formatVersion: 1 };
    for (const f of files) {
      if (f.name === "maquettiste.json" || (f.name.startsWith("model/") && f.name.endsWith(".json"))) {
        let json: Json;
        try {
          json = JSON.parse(decoder.decode(f.data)) as Json;
        } catch {
          return refuse(`${f.name} is not JSON.`, f.name);
        }
        if (f.name === "maquettiste.json") settings = json;
        else if (typeof json.id === "string") {
          const text = serialize(json);
          entries.push({ id: json.id, path: PREFIX + f.name, json, text, hash: sha256Hex(text) });
        }
      }
    }
    const name = typeof info.name === "string" && info.name.trim() ? info.name : "Imported snapshot";
    const time = stamp(new Date());
    const snapshot: MockSnapshot = {
      info: describe(entries, {
        id: freshId(name, time),
        name,
        description: typeof info.description === "string" ? info.description : "",
        author: typeof info.author === "string" ? info.author : "",
        createdUtc: typeof info.createdUtc === "string" ? info.createdUtc : time.utc,
        origin: info.origin === "before-restore" ? "before-restore" : "user",
        includesPacks: false,
        published: info.published === true,
      }),
      entries,
      settings,
    };
    list().unshift(snapshot);
    list().sort((a, b) => (a.info.createdUtc > b.info.createdUtc ? -1 : a.info.createdUtc < b.info.createdUtc ? 1 : a.info.id < b.info.id ? -1 : 1));
    return { snapshot: snapshot.info, diagnostics: [], tooLarge: false };
  };

  /** The read-only backend over a snapshot's documents, which answers its as-of reads with the ordinary handlers. */
  const viewOf = (snapshot: MockSnapshot) => {
    let view = views.get(snapshot);
    if (!view) {
      const files = [
        ...snapshot.entries.map((e) => ({ path: e.path.slice(PREFIX.length), text: e.text })),
        { path: "maquettiste.json", text: serialize(snapshot.settings) },
      ];
      const viewBackend = new MockBackend({ seed: { files, packs: [] }, scenarios: [] });
      view = { backend: viewBackend, handlers: handlersFor(viewBackend) };
      views.set(snapshot, view);
    }
    return view;
  };

  /** `?snapshot=<id>`: the reads that take it answer from the snapshot; anything else is refused as the sign-in gate does. */
  const asOf = rawHttp.all(`${baseUrl}/api/*`, async ({ request }) => {
    const url = new URL(request.url);
    const id = url.searchParams.get(SNAPSHOT_PARAM);
    if (id === null) return undefined;
    const kind = classifyRequest(request.method, url.pathname);
    if (kind !== "as-of")
      return request.method === "GET" || request.method === "HEAD"
        ? problem(400, "snapshot-unsupported", "This read does not take a snapshot; it answers for the working model only.")
        : problem(409, "snapshot-read-only", "A snapshot is read-only; restore it to change it.");
    const found = find(id);
    if (!found) return missing(id);
    const view = viewOf(found);
    url.searchParams.delete(SNAPSHOT_PARAM);
    const body = request.method === "GET" || request.method === "HEAD" ? undefined : await request.text();
    const response = await getResponse(view.handlers, new Request(url, { method: request.method, headers: request.headers, body }));
    if (!response) return problem(404, "not-found", `${url.pathname} has no mock answer.`);
    response.headers.set("X-Maquettiste-Snapshot", id);
    return response;
  });

  const side = (name: string): { entries: Entry[]; settings: Json } | null => {
    if (name === WORKING) return { entries: [...model.entries.values()], settings: model.settingsJson };
    return find(name) ?? null;
  };

  const compare = (from: string, to: string, offset: number, limit: number): SnapshotComparison | null => {
    const a = side(from);
    const b = side(to);
    if (!a || !b) return null;
    const before = new Map(a.entries.map((e) => [e.id, e]));
    const after = new Map(b.entries.map((e) => [e.id, e]));
    const name = (e: Entry) => String(e.json.name ?? "");
    const rel = (path: string) => (path.startsWith(PREFIX) ? path.slice(PREFIX.length) : path);
    const changes: Change[] = [];
    for (const [id, e] of after) {
      const old = before.get(id);
      if (!old) changes.push({ id, kind: String(e.json.kind), name: name(e), change: "added", path: rel(e.path) });
      else if (old.hash !== e.hash)
        changes.push({
          id,
          kind: String(e.json.kind),
          name: name(e),
          change: "changed",
          path: rel(e.path),
          ...(name(old) !== name(e) ? { previousName: name(old) } : {}),
          ...(old.path !== e.path ? { previousPath: rel(old.path) } : {}),
        });
    }
    for (const [id, e] of before) if (!after.has(id)) changes.push({ id, kind: String(e.json.kind), name: name(e), change: "removed", path: rel(e.path) });
    changes.sort((x, y) => (x.kind !== y.kind ? (x.kind < y.kind ? -1 : 1) : x.name !== y.name ? (x.name < y.name ? -1 : 1) : x.id < y.id ? -1 : 1));
    const byKind = new Map<string, { kind: string; added: number; removed: number; changed: number }>();
    for (const c of changes) {
      const row = byKind.get(c.kind) ?? { kind: c.kind, added: 0, removed: 0, changed: 0 };
      row[c.change] += 1;
      byKind.set(c.kind, row);
    }
    const kinds = [...byKind.values()].sort((x, y) => (x.kind < y.kind ? -1 : 1));
    const elements = changes.slice(offset, offset + limit);
    return {
      from,
      to,
      added: kinds.reduce((n, k) => n + k.added, 0),
      removed: kinds.reduce((n, k) => n + k.removed, 0),
      changed: kinds.reduce((n, k) => n + k.changed, 0),
      kinds,
      elements,
      next: offset + elements.length < changes.length ? offset + elements.length : null,
      files: jsonEqual(a.settings, b.settings) ? [] : [{ path: "maquettiste.json", change: "changed" }],
      filesTruncated: false,
      packsCompared: false,
    };
  };

  const fields = (before: Json, after: Json): Field[] => {
    const out: Field[] = [];
    for (const key of [...new Set([...Object.keys(before), ...Object.keys(after)])].sort()) {
      const pointer = `/${key.replace(/~/g, "~0").replace(/\//g, "~1")}`;
      if (!(key in after)) out.push({ pointer, change: "removed", before: before[key], after: null });
      else if (!(key in before)) out.push({ pointer, change: "added", before: null, after: after[key] });
      else if (!jsonEqual(before[key], after[key])) out.push({ pointer, change: "changed", before: before[key], after: after[key] });
    }
    return out;
  };

  const url = (path: string) => `${baseUrl}${path}`;
  return [
    asOf,
    rawHttp.get(url("/api/snapshots"), () => HttpResponse.json(list().map((s) => s.info))),
    rawHttp.post(url("/api/snapshots"), async ({ request }) => {
      const denied = forbidden("editor");
      if (denied) return denied;
      const body = ((await request.json().catch(() => ({}))) ?? {}) as Json;
      const name = typeof body.name === "string" ? body.name.trim() : "";
      if (!name || name.length > 200) return problem(400, "bad-request", "A snapshot name has 1 to 200 characters.");
      const info = take(name, typeof body.description === "string" ? body.description : "", body.includePacks === true, "user");
      return HttpResponse.json(info, { status: 201, headers: { Location: `/api/snapshots/${info.id}` } });
    }),
    rawHttp.get(url("/api/snapshots/compare"), ({ request }) => {
      const query = new URL(request.url).searchParams;
      const from = query.get("from");
      const offset = Number(query.get("offset") ?? 0);
      const limit = Number(query.get("limit") ?? 500);
      if (!from) return problem(400, "bad-request", "from is required: a snapshot id or working.");
      if (!Number.isInteger(offset) || offset < 0 || !Number.isInteger(limit) || limit < 1 || limit > 5000)
        return problem(400, "bad-request", "offset must be 0 or more and limit 1 to 5000.");
      const result = compare(from, query.get("to") || WORKING, offset, limit);
      return result ? HttpResponse.json(result) : missing(from);
    }),
    rawHttp.get(url("/api/snapshots/compare/element"), ({ request }) => {
      const query = new URL(request.url).searchParams;
      const from = query.get("from");
      const id = query.get("id");
      if (!from || !id) return problem(400, "bad-request", "from and id are required.");
      const a = side(from);
      const b = side(query.get("to") || WORKING);
      const before = a?.entries.find((e) => e.id === id);
      const after = b?.entries.find((e) => e.id === id);
      if (!a || !b || (!before && !after)) return problem(404, "not-found", `No element on either side has the id ${id}.`);
      const row = (after ?? before)!;
      const diff: SnapshotElementDiff = {
        id,
        kind: String(row.json.kind),
        name: String(row.json.name ?? ""),
        change: !before ? "added" : !after ? "removed" : before.hash === after.hash ? "unchanged" : "changed",
        fromPath: before?.path.slice(PREFIX.length) ?? null,
        toPath: after?.path.slice(PREFIX.length) ?? null,
        fromHash: before?.hash ?? null,
        toHash: after?.hash ?? null,
        before: before ? clone(before.json) : null,
        after: after ? clone(after.json) : null,
        fields: before && after && before.hash !== after.hash ? fields(before.json, after.json) : [],
        fieldsTruncated: false,
      };
      return HttpResponse.json(diff);
    }),
    rawHttp.post(url("/api/snapshots/import"), async ({ request }) => {
      const denied = forbidden("maintainer");
      if (denied) return denied;
      const result = await importArchive(new Uint8Array(await request.arrayBuffer()));
      return result.snapshot
        ? HttpResponse.json(result, { status: 201, headers: { Location: `/api/snapshots/${result.snapshot.id}` } })
        : HttpResponse.json(result, { status: 422 });
    }),
    rawHttp.get(url("/api/snapshots/:id"), ({ params }) => {
      const found = find(String(params.id));
      return found ? HttpResponse.json(found.info) : missing(String(params.id));
    }),
    rawHttp.patch(url("/api/snapshots/:id"), async ({ params, request }) => {
      const denied = forbidden("editor");
      if (denied) return denied;
      const found = find(String(params.id));
      if (!found) return missing(String(params.id));
      const body = ((await request.json().catch(() => ({}))) ?? {}) as Json;
      if (body.name !== undefined && body.name !== null && (typeof body.name !== "string" || !body.name.trim() || body.name.length > 200))
        return problem(400, "bad-request", "A snapshot name has 1 to 200 characters.");
      found.info = {
        ...found.info,
        ...(typeof body.name === "string" ? { name: body.name.trim() } : {}),
        ...(typeof body.description === "string" ? { description: body.description } : {}),
        ...(typeof body.published === "boolean" ? { published: body.published } : {}),
      };
      return HttpResponse.json(found.info);
    }),
    rawHttp.delete(url("/api/snapshots/:id"), ({ params }) => {
      const denied = forbidden("maintainer");
      if (denied) return denied;
      const all = list();
      const index = all.findIndex((s) => s.info.id === String(params.id));
      if (index < 0) return missing(String(params.id));
      all.splice(index, 1);
      return new HttpResponse(null, { status: 204 });
    }),
    rawHttp.post(url("/api/snapshots/:id/restore"), async ({ params, request }) => {
      const denied = forbidden("maintainer");
      if (denied) return denied;
      const found = find(String(params.id));
      if (!found) return missing(String(params.id));
      if (backend.runLocked) return problem(409, "run-locked", "A generation run is writing; restore the snapshot when it ends.");
      const body = ((await request.json().catch(() => ({}))) ?? {}) as Json;
      const packs = body.includePacks === true && found.info.includesPacks;
      const safety = take(`before-restore-${stamp(new Date()).id}`, `Taken automatically before restoring ${found.info.id}.`, packs, "before-restore");
      const comparison = compare(WORKING, found.info.id, 0, 5000)!;
      const changes = model.replaceAll(found.entries, found.settings);
      const result: SnapshotRestoreResult = {
        outcome: "restored",
        snapshot: found.info,
        safety,
        written: comparison.added + comparison.changed + comparison.files.length,
        deleted: comparison.removed,
        packsRestored: packs,
        undo: `Restore snapshot ${safety.id} to undo this restore.`,
        elementsChanged: changes.changed.length,
        elementsDeleted: changes.deleted.length,
        diagnostics: [],
      };
      return HttpResponse.json(result);
    }),
    rawHttp.get(url("/api/snapshots/:id/export"), ({ params }) => {
      const denied = forbidden("editor");
      if (denied) return denied;
      const found = find(String(params.id));
      if (!found) return missing(String(params.id));
      return new HttpResponse(archive(found), {
        headers: { "Content-Type": "application/zip", "Content-Disposition": `attachment; filename="${found.info.id}.zip"` },
      });
    }),
  ];
}
