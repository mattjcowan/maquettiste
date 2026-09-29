#!/usr/bin/env node
// Builds the committed Northwind Operations model from domain/*.yaml:
//   .maquettiste/maquettiste.json, .maquettiste/model/** (replaced wholesale), .maquettiste/.schema/v1/** (copied from
//   schemas/v1) and .maquettiste/templates/{sql-ddl,csharp-dapper}/** (copied from packs/).
// Deterministic: ids come from domain keys (lib/ids.mjs), files are canonical JSON (lib/canonical.mjs), and nothing depends on
// the clock or the machine. Usage: node build-model.mjs [--out <sample root>] [--quiet]
import { cpSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { Canonical } from "./lib/canonical.mjs";
import { compile, loadDomain } from "./lib/domain.mjs";

const here = dirname(fileURLToPath(import.meta.url));
export const sampleRoot = resolve(here, "..");
export const repoRoot = resolve(sampleRoot, "../..");
export const PACKS = ["sql-ddl", "csharp-dapper"];

/** The model as documents, plus canonical file text keyed by path relative to the sample root. */
export function buildModel() {
  const domain = loadDomain(join(sampleRoot, "domain"));
  const { documents, stats } = compile(domain);
  const canonical = new Canonical(join(repoRoot, "schemas", "v1"));
  const files = new Map();
  files.set(".maquettiste/maquettiste.json", canonical.write(domain.project.settings, "maquettiste.json", "maquettiste.json"));
  for (const d of documents) files.set(".maquettiste/" + d.modelPath, canonical.write(d.doc, d.schema, d.modelPath));
  return { documents, stats, files, settings: domain.project.settings };
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
  for (const p of PACKS) cpSync(join(repoRoot, "packs", p), join(mq, "templates", p), { recursive: true });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const outIndex = args.indexOf("--out");
  const outRoot = outIndex >= 0 ? resolve(args[outIndex + 1]) : sampleRoot;
  const { files, stats } = buildModel();
  writeModel(outRoot, files);
  if (!args.includes("--quiet")) console.log(`build-model: wrote ${files.size} model files to ${outRoot} (${JSON.stringify(stats)})`);
}
