#!/usr/bin/env node
// Writes src/code/extensionSchema.json: schemas/v1/extension.json with its references into common.json inlined, so the
// Extensions tab validates a custom property schema as it is typed without loading another file. The output is committed;
// `--check` exits 1 when it differs from what the schemas say.
import fs from "node:fs";
import path from "node:path";
import { editorRoot, repoRoot } from "./paths.mjs";

const folder = path.join(repoRoot, "schemas/v1");
const output = path.join(editorRoot, "src/code/extensionSchema.json");
const read = (name) => JSON.parse(fs.readFileSync(path.join(folder, name), "utf8"));
const common = read("common.json");

function inline(node, file) {
  if (Array.isArray(node)) return node.map((n) => inline(n, file));
  if (!node || typeof node !== "object") return node;
  if (typeof node.$ref === "string") {
    const [target, pointer] = node.$ref.split("#");
    const source = target ? read(target) : file === "common.json" ? common : null;
    if (!source) throw new Error(`Unresolved reference ${node.$ref}`);
    const resolved = pointer
      .split("/")
      .filter(Boolean)
      .reduce((n, key) => n[key], source);
    const rest = { ...node };
    delete rest.$ref;
    return { ...inline(resolved, target || file), ...inline(rest, file) };
  }
  return Object.fromEntries(Object.entries(node).map(([k, v]) => [k, inline(v, file)]));
}

const schema = inline(read("extension.json"), "extension.json");
delete schema.$schema;
const text = JSON.stringify(schema, null, 2) + "\n";
if (process.argv.includes("--check")) {
  const current = fs.existsSync(output) ? fs.readFileSync(output, "utf8") : "";
  if (current !== text) {
    console.error("src/code/extensionSchema.json is out of date with schemas/v1/extension.json. Run `npm run gen` and commit the result.");
    process.exit(1);
  }
  console.log("src/code/extensionSchema.json is up to date.");
} else {
  fs.writeFileSync(output, text);
  console.log(`Wrote ${output}`);
}
