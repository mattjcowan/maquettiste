#!/usr/bin/env node
// Copies the billing fixture (tests/fixtures/models/billing/.maquettiste) and the manifests of the
// two example packs into src/mocks/fixture/, inside the editor's own tree, where the mocks import
// them with import.meta.glob. Keeping the copy inside src/editor means Vite's dev-server file
// allow list (server.fs.allow) never has to reach outside the project (phase2-design.md 4.4).
// Runs on `npm install` (postinstall) and before dev, build and test; the copy is gitignored.
import fs from "node:fs";
import path from "node:path";
import { billingFixture, editorRoot, fixtureCopy, repoRoot } from "./paths.mjs";

const packsCopy = path.join(editorRoot, "src/mocks/fixture/packs");
const packs = ["sql-ddl", "csharp-dapper"];

function copyTree(from, to) {
  fs.mkdirSync(to, { recursive: true });
  for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
    const source = path.join(from, entry.name);
    const target = path.join(to, entry.name);
    if (entry.isDirectory()) {
      // Engine-owned working folders never belong to the model the mocks serve.
      if ([".cache", "manifest", "snapshots", ".schema", "templates"].includes(entry.name)) continue;
      copyTree(source, target);
    } else if (/\.(json|md)$/.test(entry.name)) {
      fs.copyFileSync(source, target);
    }
  }
}

if (!fs.existsSync(billingFixture)) {
  console.error(`copy-fixture: ${path.relative(repoRoot, billingFixture)} is missing; the mocks need the billing fixture.`);
  process.exit(1);
}

fs.rmSync(path.join(editorRoot, "src/mocks/fixture"), { recursive: true, force: true });
copyTree(billingFixture, fixtureCopy);
for (const pack of packs) {
  const manifest = path.join(repoRoot, "packs", pack, "pack.json");
  if (!fs.existsSync(manifest)) continue;
  fs.mkdirSync(path.join(packsCopy, pack), { recursive: true });
  fs.copyFileSync(manifest, path.join(packsCopy, pack, "pack.json"));
}
console.log(`copy-fixture: copied the billing fixture and ${packs.length} pack manifests into src/mocks/fixture/`);
