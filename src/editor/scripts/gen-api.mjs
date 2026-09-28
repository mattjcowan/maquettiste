#!/usr/bin/env node
// Generates src/api/schema.d.ts from docs/api/openapi.yaml with openapi-typescript (PD4).
// The output is committed. `--check` regenerates in memory and exits 1 when the committed file
// differs, which is what CI (editor.yml) and the Vitest suite (tests/unit/generated.test.ts) run.
import fs from "node:fs";
import { generateApiTypes } from "./generate.mjs";
import { schemaDts } from "./paths.mjs";

const check = process.argv.includes("--check");
const text = await generateApiTypes();

if (check) {
  const current = fs.existsSync(schemaDts) ? fs.readFileSync(schemaDts, "utf8") : "";
  if (current !== text) {
    console.error("src/api/schema.d.ts is out of date with docs/api/openapi.yaml. Run `npm run gen:api` and commit the result.");
    process.exit(1);
  }
  console.log("src/api/schema.d.ts is up to date.");
} else {
  fs.writeFileSync(schemaDts, text);
  console.log(`Wrote ${schemaDts}`);
}
