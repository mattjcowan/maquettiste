#!/usr/bin/env node
// Builds the Northwind Operations model through a running editor's API, batch by batch (phase2-design.md section 7.2 and
// PD21): every model file is written by the editor's store. The documents are the ones build-model.mjs writes, with the same
// ids, so the seeded project ends up byte-identical to the committed model (--compare checks that).
//
// The target project must exist and have an empty model: .maquettiste/maquettiste.json plus the sql-ddl and csharp-dapper
// packs under .maquettiste/templates/ (see README.md, "Seeding through the editor"). Order: settings, vocabularies, packages,
// the database (sequences, views, the designed tables), types (enums, scalar types, value objects, reference types), one batch
// of entities per package, one batch of relations per package, the relation mappings together with the bindings of
// domain/storage/ (each entity and reference type written again whole, by name), the queries with the entities that read them,
// diagrams.
//
// Usage: node seed.mjs [--url http://127.0.0.1:8080] [--host maquettiste.localhost] [--token <t>] [--no-settings]
//                      [--dry-run] [--compare <seeded project root>]
// The token defaults to $MAQUETTISTE_EDITOR_TOKEN; without one the editor must have no token set.
import http from "node:http";
import https from "node:https";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { buildModel } from "./build-model.mjs";

const args = process.argv.slice(2);
const opt = (name, fallback) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : fallback; };
const flag = (name) => args.includes(name);
const baseUrl = new URL(opt("--url", "http://127.0.0.1:8080"));
const hostHeader = opt("--host", "maquettiste.localhost");
const token = opt("--token", process.env.MAQUETTISTE_EDITOR_TOKEN ?? "");
const compareRoot = opt("--compare", null);

function request(method, path, body, extraHeaders = {}) {
  const data = body === undefined ? undefined : Buffer.from(JSON.stringify(body));
  const headers = { Accept: "application/json", Host: `${hostHeader}${baseUrl.port ? ":" + baseUrl.port : ""}`, ...extraHeaders };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (data) { headers["Content-Type"] = "application/json"; headers["Content-Length"] = data.length; }
  const lib = baseUrl.protocol === "https:" ? https : http;
  return new Promise((resolve, reject) => {
    const req = lib.request({ hostname: baseUrl.hostname, port: baseUrl.port, path, method, headers }, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => {
        const text = Buffer.concat(chunks).toString("utf8");
        let json = null;
        try { json = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
        resolve({ status: res.statusCode, headers: res.headers, json, text });
      });
    });
    req.on("error", reject);
    req.setTimeout(120_000, () => req.destroy(new Error(`${method} ${path} timed out`)));
    if (data) req.write(data);
    req.end();
  });
}

function fail(message, res) {
  console.error(`seed: ${message}`);
  if (res) {
    console.error(`  HTTP ${res.status}`);
    const items = res.json?.items?.filter((i) => i.outcome !== "saved") ?? [];
    for (const item of items.slice(0, 10))
      for (const d of item.diagnostics ?? []) console.error(`  ${item.id ?? ""} ${d.rule} ${d.message} ${d.jsonPointer ?? ""}`);
    for (const d of (res.json?.diagnostics ?? []).slice(0, 10)) console.error(`  ${d.rule} ${d.message} ${d.jsonPointer ?? ""}`);
    if (!items.length && !res.json?.diagnostics) console.error("  " + res.text.slice(0, 600));
  }
  process.exit(1);
}

/**
 * The batches, in dependency order: [{ label, operations }]. The database's tables come before the types and entities their
 * bindings read; a binding of domain/storage/ can name relation ends, so its document is created without it and written whole
 * (an upsert by name) once the relations and their mappings exist.
 */
function batches(documents, storageBindings) {
  const byPhase = new Map();
  const later = [];
  for (const d of documents) {
    if (!byPhase.has(d.phase)) byPhase.set(d.phase, []);
    let doc = d.doc;
    if (storageBindings.has(d.modelPath)) {
      const { bindings, ...rest } = doc;
      doc = rest;
      later.push({ op: "upsert", id: `@${d.doc.kind}:${d.doc.name}`, element: d.doc });
    }
    byPhase.get(d.phase).push(doc);
  }
  const out = [];
  const creates = (docs) => docs.map((element) => ({ op: "create", element }));
  const take = (phase, label = phase) => { if (byPhase.has(phase)) out.push({ label, operations: creates(byPhase.get(phase)) }); };
  take("vocabularies"); take("packages"); take("database"); take("types");
  for (const phase of byPhase.keys()) if (phase.startsWith("entities:")) take(phase);
  const relations = byPhase.get("relations") ?? [];
  const packages = [...new Set(relations.map((r) => r.package))];
  const packageName = new Map(documents.filter((d) => d.doc.kind === "package").map((d) => [d.doc.id, d.doc.name]));
  for (const p of packages) out.push({ label: `relations:${packageName.get(p) ?? p}`, operations: creates(relations.filter((r) => r.package === p)) });
  // The relation mappings and the bindings in one batch: a relation mapping between bound entities and a binding that names a
  // relation's ends are each incomplete without the other.
  out.push({ label: "mappings and bindings", operations: [...creates(byPhase.get("mappings") ?? []), ...later] });
  take("queries"); take("diagrams");
  return out;
}

async function main() {
  const { documents, settings, files, storageBindings } = buildModel();
  const plan = batches(documents, storageBindings);
  const count = (op) => plan.reduce((n, b) => n + b.operations.filter((o) => o.op === op).length, 0);
  const total = count("create");
  if (total !== documents.length || count("upsert") !== storageBindings.size)
    fail(`internal: ${documents.length} documents and ${storageBindings.size} bindings but ${total} creates and ${count("upsert")} upserts batched`);
  const describe = (b) => {
    const creates = b.operations.filter((o) => o.op === "create").length;
    const bound = b.operations.length - creates;
    return [creates ? `${creates} created` : "", bound ? `${bound} bound` : ""].filter(Boolean).join(", ");
  };
  if (flag("--dry-run")) {
    for (const b of plan) console.log(`${b.label}: ${describe(b)}`);
    console.log(`seed: ${plan.length} batches, ${total} elements (dry run, nothing sent)`);
    return;
  }

  const health = await request("GET", "/api/project");
  if (health.status !== 200) fail(`GET /api/project failed; is the editor running at ${baseUrl.origin} (Host ${hostHeader}) and is the token right?`, health);
  const index = await request("GET", "/api/model/index");
  if (index.status !== 200) fail("GET /api/model/index failed", index);
  if (Array.isArray(index.json) && index.json.length > 0) fail(`the target model is not empty (${index.json.length} elements); seed an empty project`);

  const saveSettings = async (body) => {
    const current = await request("GET", "/api/project/settings");
    if (current.status !== 200) fail("GET /api/project/settings failed", current);
    const saved = await request("PUT", "/api/project/settings", body, { "If-Match": `"${current.json.hash}"` });
    if (saved.status !== 200) fail("saving maquettiste.json failed", saved);
  };

  // The project has never been deployed, and the batches reshape its database as they go (the entities' tables, then the
  // overlays and mappings that change them): none of that is a migration. Seed with migration facts off, then save the real
  // settings.
  if (!flag("--no-settings")) {
    await saveSettings({ ...settings, migrations: { ...settings.migrations, record: "off" } });
    console.log("seed: settings saved, migration facts off while seeding");
  }

  for (const b of plan) {
    const res = await request("POST", "/api/model/batch", { operations: b.operations });
    if (res.status !== 200 || res.json?.outcome !== "saved") fail(`batch '${b.label}' was refused (${res.json?.outcome ?? "no outcome"})`, res);
    console.log(`seed: ${b.label}: ${describe(b)}`);
  }

  if (!flag("--no-settings")) {
    await saveSettings(settings);
    console.log("seed: settings saved");
  }

  const check = await request("POST", "/api/validate", {});
  const diagnostics = check.json?.diagnostics ?? check.json;
  if (check.status !== 200 || !Array.isArray(diagnostics)) fail("POST /api/validate failed", check);
  if (diagnostics.length > 0) {
    for (const d of diagnostics.slice(0, 10)) console.error(`  ${d.rule} ${d.severity} ${d.message} ${d.filePath ?? ""}`);
    fail(`the seeded model has ${diagnostics.length} diagnostic(s); expected none`);
  }
  console.log("seed: validate reports no diagnostics");
  console.log(`seed: ${plan.length} batches, ${total} elements created`);

  if (compareRoot) {
    const differ = [];
    for (const [path, text] of files) {
      const full = join(compareRoot, path);
      if (!existsSync(full)) differ.push(`missing ${path}`);
      else if (readFileSync(full, "utf8") !== text) differ.push(`differs ${path}`);
    }
    // Nothing else may be under .maquettiste/model: no duplicates, stray renames or leftover temp files.
    const walk = (d) => {
      if (!existsSync(d)) return;
      for (const name of readdirSync(d).sort()) {
        const p = join(d, name);
        if (statSync(p).isDirectory()) walk(p);
        else {
          const rel = relative(compareRoot, p).split("\\").join("/");
          if (!files.has(rel)) differ.push(`not built by build-model.mjs ${rel}`);
        }
      }
    };
    walk(join(compareRoot, ".maquettiste", "model"));
    if (differ.length) {
      console.error(`seed: the seeded project differs from build-model.mjs in ${differ.length} file(s):`);
      for (const d of differ.slice(0, 20)) console.error("  " + d);
      process.exit(1);
    }
    console.log(`seed: ${compareRoot} matches build-model.mjs byte for byte (${files.size} files)`);
  }
}

main().catch((e) => fail(e.stack ?? String(e)));
