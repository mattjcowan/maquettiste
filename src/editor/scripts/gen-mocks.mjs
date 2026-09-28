#!/usr/bin/env node
// Bundles docs/api/openapi.yaml, with the external schemas/v1/*.json it references, into one
// JSON document: src/mocks/openapi.json. The mock baseline answers every operation from it and
// the ajv contract suite validates every mock response against it (phase2-design.md 4.4).
// The output is committed. `--check` exits 1 when the committed file differs.
import fs from "node:fs";
import { generateMockDocument } from "./generate.mjs";
import { openapiJson } from "./paths.mjs";

const check = process.argv.includes("--check");
const text = await generateMockDocument();

if (check) {
  const current = fs.existsSync(openapiJson) ? fs.readFileSync(openapiJson, "utf8") : "";
  if (current !== text) {
    console.error("src/mocks/openapi.json is out of date with docs/api/openapi.yaml. Run `npm run gen:mocks` and commit the result.");
    process.exit(1);
  }
  console.log("src/mocks/openapi.json is up to date.");
} else {
  fs.writeFileSync(openapiJson, text);
  console.log(`Wrote ${openapiJson}`);
}
