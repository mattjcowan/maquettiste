#!/usr/bin/env node
// Copies a project's storage into domain/storage/: every model file that domain/*.yaml does not describe (the designed tables
// and the relation mappings "Create tables…" made from the entities) whole, and the bindings of the entities and reference types
// the YAML describes without one. build-model.mjs reads them back. After a storage change made in the editor or with the CLI
// (a column added, a binding edited), run this over the changed project, then build-model.mjs and check-model.mjs.
// Any other difference between the project and the YAML is an error: describe it in domain/*.yaml instead.
// Usage: node take-storage.mjs [--from <project root>]     (default: the sample itself)
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { buildModel, sampleRoot, STORAGE } from "./build-model.mjs";
import { kebab, loadDomain } from "./lib/domain.mjs";

const args = process.argv.slice(2);
const fromIndex = args.indexOf("--from");
const from = fromIndex >= 0 ? resolve(args[fromIndex + 1]) : sampleRoot;

const model = join(from, ".maquettiste", "model");
const found = [];
const walk = (d) => {
  for (const name of readdirSync(d).sort()) {
    const p = join(d, name);
    if (statSync(p).isDirectory()) walk(p);
    else if (name.endsWith(".json")) found.push(p);
  }
};
walk(model);

// The project's tables and mappings, but the tables the YAML designs, are storage; the YAML is built with them (its queries read them).
const { database } = loadDomain(join(sampleRoot, "domain")).project;
const designed = new Set(Object.keys(database.tables ?? {}).map((n) => `databases/${kebab(database.name)}/tables/${kebab(n)}.json`));
const out = new Map(); // path under domain/storage/ (= under model/) -> document
for (const file of found) {
  const path = relative(model, file).split(/[\\/]/).join("/");
  const { $schema, ...doc } = JSON.parse(readFileSync(file, "utf8"));
  if ((doc.kind === "table" || doc.kind === "mapping") && !designed.has(path)) out.set(path, doc);
}
const { documents, files, canonical } = buildModel({ stored: out });
const described = new Map(documents.map((d) => [d.modelPath, d]));

const problems = [];
for (const file of found) {
  const path = relative(model, file).split(/[\\/]/).join("/");
  const modelPath = "model/" + path;
  const text = readFileSync(file, "utf8");
  const doc = JSON.parse(text);
  const yaml = described.get(modelPath);
  if (!yaml) {
    problems.push(`${modelPath}: a ${doc.kind} the YAML does not describe`);
    continue;
  }
  if (text === files.get(".maquettiste/" + modelPath)) continue;
  // The YAML's document with the file's bindings must be the file.
  if (doc.bindings && !yaml.doc.bindings && canonical.write({ ...yaml.doc, bindings: doc.bindings }, yaml.schema, modelPath) === text)
    out.set(path, { bindings: doc.bindings });
  else problems.push(`${modelPath}: differs from the YAML's document`);
}
for (const p of described.keys()) if (!existsSync(join(from, ".maquettiste", p))) problems.push(`${p}: in the YAML, not in the project`);
if (problems.length) {
  console.error(`take-storage: ${problems.length} problem(s); nothing written`);
  for (const p of problems.slice(0, 40)) console.error("  " + p);
  process.exit(1);
}

const storage = join(sampleRoot, STORAGE);
rmSync(storage, { recursive: true, force: true });
for (const [path, doc] of [...out].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
  const full = join(storage, path);
  mkdirSync(dirname(full), { recursive: true });
  writeFileSync(full, JSON.stringify(doc, null, 2) + "\n");
}
console.log(`take-storage: wrote ${out.size} files to ${STORAGE}`);
