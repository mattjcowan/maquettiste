#!/usr/bin/env node
// Checks that the committed model is exactly what build-model.mjs produces from domain/*.yaml, and that the committed
// schemas and packs are copies of schemas/v1 and packs/. Builds twice to prove determinism. Exit 0 when equal, 1 otherwise.
// Usage: node check-model.mjs            (npm test runs it)
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { buildModel, PACKS, repoRoot, sampleRoot } from "./build-model.mjs";

const problems = [];
const first = buildModel();
const second = buildModel();
for (const [path, text] of first.files) if (second.files.get(path) !== text) problems.push(`not deterministic: ${path}`);

function listFiles(dir) {
  if (!existsSync(dir)) return [];
  const out = [];
  const walk = (d) => {
    for (const name of readdirSync(d).sort()) {
      const p = join(d, name);
      if (statSync(p).isDirectory()) walk(p);
      else out.push(p);
    }
  };
  walk(dir);
  return out;
}

// The model and the settings: every built file must be on disk with the same bytes, and nothing else may be there.
const mq = join(sampleRoot, ".maquettiste");
const onDisk = new Set(listFiles(join(mq, "model")).map((p) => relative(sampleRoot, p).split("\\").join("/")));
onDisk.add(".maquettiste/maquettiste.json");
for (const [path, text] of first.files) {
  const full = join(sampleRoot, path);
  if (!existsSync(full)) problems.push(`missing: ${path}`);
  else if (readFileSync(full, "utf8") !== text) problems.push(`differs: ${path}`);
  onDisk.delete(path);
}
for (const extra of onDisk) problems.push(`not built by build-model.mjs: ${extra}`);

// Copies: .schema/v1 from schemas/v1, templates/<pack> from packs/<pack>.
function compareTrees(source, copy, label) {
  const a = listFiles(source).map((p) => relative(source, p));
  const b = new Set(listFiles(copy).map((p) => relative(copy, p)));
  for (const f of a) {
    if (!b.has(f)) problems.push(`${label}: missing ${f}`);
    else if (!readFileSync(join(source, f)).equals(readFileSync(join(copy, f)))) problems.push(`${label}: differs ${f}`);
    b.delete(f);
  }
  for (const f of b) problems.push(`${label}: extra ${f}`);
}
compareTrees(join(repoRoot, "schemas", "v1"), join(mq, ".schema", "v1"), ".schema/v1");
for (const p of PACKS) compareTrees(join(repoRoot, "packs", p), join(mq, "templates", p), `templates/${p}`);

// The domain's promised shape (phase2-design.md section 7.1).
const s = first.stats;
const expect = { packages: 12, entities: 202, enums: 23, referenceTypes: 2, valueObjects: 8, diagrams: 12 };
for (const [k, v] of Object.entries(expect)) if (s[k] !== v) problems.push(`expected ${v} ${k}, built ${s[k]}`);

if (problems.length) {
  console.error(`check-model: ${problems.length} problem(s); run node tools/build-model.mjs to rebuild the committed model`);
  for (const p of problems.slice(0, 40)) console.error("  " + p);
  process.exit(1);
}
console.log(`check-model: the committed model matches domain/*.yaml (${first.files.size} files; ${JSON.stringify(s)})`);
