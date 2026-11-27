#!/usr/bin/env node
// Builds the committed Northwind Operations model from domain/*.yaml and domain/storage/:
//   .maquettiste/maquettiste.json, .maquettiste/model/** (replaced wholesale), .maquettiste/.schema/v1/** (copied from
//   schemas/v1, plus the resolved-model reference from docs/resolved-model.md, as init writes them) and
//   .maquettiste/templates/{sql-ddl,csharp-dapper}/** (copied from packs/).
// domain/storage/ holds the database's storage as model documents (take-storage.mjs writes it): the designed tables and the
// relation mappings "Create tables…" made from the entities, written as they are, and the bindings of the entities and reference
// types, merged into the documents the YAML describes.
// Deterministic: ids come from domain keys (lib/ids.mjs) or from the storage files, files are canonical JSON (lib/canonical.mjs),
// and nothing depends on the clock or the machine. Usage: node build-model.mjs [--out <sample root>] [--quiet]
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { Canonical } from "./lib/canonical.mjs";
import { compile, loadDomain } from "./lib/domain.mjs";

const here = dirname(fileURLToPath(import.meta.url));
export const sampleRoot = resolve(here, "..");
export const repoRoot = resolve(sampleRoot, "../..");
export const PACKS = ["sql-ddl", "csharp-dapper"];
/** The resolved-model reference the engine writes beside the schemas (docs/resolved-model.md). */
export const REFERENCE = "resolved-model.md";

/** The storage folder, relative to the sample root: a path under it is the path of the file under .maquettiste/model/. */
export const STORAGE = "domain/storage";

/** The storage documents: path under model/ (for example databases/main/tables/parties.json) to document, in path order. */
export function loadStorage(dir) {
  const stored = new Map();
  if (!existsSync(dir)) return stored;
  const walk = (d) => {
    for (const name of readdirSync(d).sort()) {
      const p = join(d, name);
      if (statSync(p).isDirectory()) walk(p);
      else if (name.endsWith(".json")) stored.set(relative(dir, p).split(/[\\/]/).join("/"), JSON.parse(readFileSync(p, "utf8")));
    }
  };
  walk(dir);
  return stored;
}

/**
 * The model as documents, plus canonical file text keyed by path relative to the sample root. `storageBindings` holds the model
 * paths of the documents whose bindings come from domain/storage/ (seed.mjs writes those bindings after the relations).
 * `stored` replaces domain/storage/ (take-storage.mjs passes a project's tables and mappings).
 */
export function buildModel({ stored = loadStorage(join(sampleRoot, STORAGE)) } = {}) {
  const domain = loadDomain(join(sampleRoot, "domain"));
  const { documents, stats, storageBindings } = compile(domain, stored);
  const canonical = new Canonical(join(repoRoot, "schemas", "v1"));
  const files = new Map();
  files.set(".maquettiste/maquettiste.json", canonical.write(domain.project.settings, "maquettiste.json", "maquettiste.json"));
  for (const d of documents) files.set(".maquettiste/" + d.modelPath, canonical.write(d.doc, d.schema, d.modelPath));
  return { documents, stats, files, settings: domain.project.settings, storageBindings, canonical };
}

export function writeModel(outRoot, files) {
  const mq = join(outRoot, ".maquettiste");
  rmSync(join(mq, "model"), { recursive: true, force: true });
  rmSync(join(mq, ".schema"), { recursive: true, force: true });
  for (const p of PACKS) rmSync(join(mq, "templates", p), { recursive: true, force: true });
  for (const [path, text] of [...files].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
    const full = join(outRoot, path);
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, text);
  }
  cpSync(join(repoRoot, "schemas", "v1"), join(mq, ".schema", "v1"), { recursive: true });
  cpSync(join(repoRoot, "docs", REFERENCE), join(mq, ".schema", "v1", REFERENCE));
  // The pack copies leave the pack's tests/ out (5.4).
  for (const p of PACKS) {
    const source = join(repoRoot, "packs", p);
    const outsideTests = (src) => relative(source, src).split(/[\\/]/)[0] !== "tests";
    cpSync(source, join(mq, "templates", p), { recursive: true, filter: outsideTests });
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const outIndex = args.indexOf("--out");
  const outRoot = outIndex >= 0 ? resolve(args[outIndex + 1]) : sampleRoot;
  const { files, stats } = buildModel();
  writeModel(outRoot, files);
  if (!args.includes("--quiet")) console.log(`build-model: wrote ${files.size} model files to ${outRoot} (${JSON.stringify(stats)})`);
}
