// Paths shared by the editor's scripts. Every path is absolute and derived from this file's
// location, so the scripts behave the same whatever the current directory is.
import path from "node:path";
import { fileURLToPath } from "node:url";

export const editorRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const repoRoot = path.resolve(editorRoot, "../..");
export const openapiYaml = path.join(repoRoot, "docs/api/openapi.yaml");
export const schemaDts = path.join(editorRoot, "src/api/schema.d.ts");
export const openapiJson = path.join(editorRoot, "src/mocks/openapi.json");
export const billingFixture = path.join(repoRoot, "tests/fixtures/models/billing/.maquettiste");
export const fixtureCopy = path.join(editorRoot, "src/mocks/fixture/billing");
export const functionsDir = path.join(repoRoot, "src/Maquettiste.Functions/_functions");
export const functionsVariables = path.join(repoRoot, "src/Maquettiste.Functions/_variables.json");
export const distDir = path.join(editorRoot, "dist");
