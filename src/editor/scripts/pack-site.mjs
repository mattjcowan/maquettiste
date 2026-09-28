#!/usr/bin/env node
// Packs the editor's site zip (phase2-design.md §5): the Vite build (index.html, assets/, _headers,
// theme-init.js), _variables.json (§3.2) and _functions/ (§3.1), with the engine version stamped
// into the zip's copy of _functions/Directives.cs (PD24). The host falls back to index.html for
// unknown paths, so no _redirects is written.
//
//   node scripts/pack-site.mjs [--out site.zip] [--dist dist] [--functions <dir>] [--variables <file>]
//                              [--engine-version <v>] [--no-functions]
//
// Without --engine-version the committed Directives.cs line is kept. A missing _functions/ folder
// (the functions workstream not merged yet) packs a static-only zip and says so.
import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { zipSync } from "fflate";
import { distDir, editorRoot, functionsDir, functionsVariables } from "./paths.mjs";

/** Used when src/Maquettiste.Functions/_variables.json does not exist; mirrors the §3.2 table. */
export const DEFAULT_VARIABLES = {
  MAQUETTISTE_MODE: { default: "${env:MAQUETTISTE_MODE}", description: "local (also when empty) or hosted." },
  MAQUETTISTE_REPO_ROOT: { default: "${env:MAQUETTISTE_REPO_ROOT}", description: "EngineOptions.RepoRoot." },
  MAQUETTISTE_EDITOR_TOKEN: {
    default: "${env:MAQUETTISTE_EDITOR_TOKEN}",
    description: "Bearer token and cookie sign-in; empty means local peers only.",
    secret: true,
  },
  MAQUETTISTE_CACHE_DIR: { default: "${env:MAQUETTISTE_CACHE_DIR}", description: "Index cache root on the host volume." },
  MAQUETTISTE_LOCAL_PEERS: {
    default: "${env:MAQUETTISTE_LOCAL_PEERS}",
    description: "Comma-separated IP addresses trusted as the local developer in local mode.",
  },
  MAQUETTISTE_LOCAL_TRUST: { default: "${env:MAQUETTISTE_LOCAL_TRUST}", description: "on (also when empty) or off." },
  MAQUETTISTE_LOCAL_USER: { default: "local", description: "User name of the local developer (presence)." },
};

const ENGINE_LINE = /^#:package\s+Maquettiste\.Engine@\S+\s*$/m;

/** Rewrites only the Maquettiste.Engine #:package line of Directives.cs. */
export function stampDirectives(text, version) {
  if (!version) return text;
  if (!ENGINE_LINE.test(text)) throw new Error("Directives.cs has no '#:package Maquettiste.Engine@<version>' line to stamp.");
  return text.replace(ENGINE_LINE, `#:package Maquettiste.Engine@${version}`);
}

function walk(root, rel = "") {
  const out = [];
  for (const entry of fs.readdirSync(path.join(root, rel), { withFileTypes: true })) {
    const child = rel ? `${rel}/${entry.name}` : entry.name;
    if (entry.isDirectory()) out.push(...walk(root, child));
    else if (entry.isFile()) out.push(child);
  }
  return out.sort();
}

/** The files of _functions/ that go into the zip: those directly inside the folder (§3.1). */
export function functionFiles(dir = functionsDir) {
  if (!fs.existsSync(dir)) return null;
  return fs
    .readdirSync(dir, { withFileTypes: true })
    .filter((e) => e.isFile())
    .map((e) => e.name)
    .sort();
}

/** SHA-256 over the function files' names and bytes (before stamping), to skip unchanged uploads. */
export function functionsHash(dir = functionsDir) {
  const files = functionFiles(dir);
  if (!files) return null;
  const hash = createHash("sha256");
  for (const name of files)
    hash
      .update(name)
      .update("\0")
      .update(fs.readFileSync(path.join(dir, name)))
      .update("\0");
  return hash.digest("hex");
}

/**
 * Builds the zip in memory. Returns { zip, entries, functions } where functions is the hash of the
 * packed _functions/ (null when none was packed).
 */
export function packSite({ dist = distDir, functions = functionsDir, variables = functionsVariables, engineVersion = null, includeFunctions = true } = {}) {
  if (!fs.existsSync(path.join(dist, "index.html"))) throw new Error(`${dist}/index.html is missing; run npm run build first.`);
  const files = {};
  for (const rel of walk(dist)) files[rel] = fs.readFileSync(path.join(dist, rel));
  if (!files["_headers"]) throw new Error("_headers is missing from the build (public/_headers).");

  files["_variables.json"] = fs.existsSync(variables) ? fs.readFileSync(variables) : Buffer.from(JSON.stringify(DEFAULT_VARIABLES, null, 2) + "\n");

  let packed = null;
  if (includeFunctions) {
    const names = functionFiles(functions);
    if (names && names.length) {
      for (const name of names) {
        let bytes = fs.readFileSync(path.join(functions, name));
        if (name === "Directives.cs") bytes = Buffer.from(stampDirectives(bytes.toString("utf8"), engineVersion));
        files[`_functions/${name}`] = bytes;
      }
      packed = functionsHash(functions);
    }
  }
  const zip = zipSync(Object.fromEntries(Object.entries(files).map(([k, v]) => [k, new Uint8Array(v)])), { level: 6, mtime: new Date("2020-01-01T00:00:00Z") });
  return { zip, entries: Object.keys(files).sort(), functions: packed };
}

function parseArgs(argv) {
  const args = { out: path.join(editorRoot, "site.zip"), includeFunctions: true };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const value = () => {
      const v = argv[++i];
      if (v === undefined) throw new Error(`${a} needs a value`);
      return v;
    };
    if (a === "--out") args.out = path.resolve(value());
    else if (a === "--dist") args.dist = path.resolve(value());
    else if (a === "--functions") args.functions = path.resolve(value());
    else if (a === "--variables") args.variables = path.resolve(value());
    else if (a === "--engine-version") args.engineVersion = value();
    else if (a === "--no-functions") args.includeFunctions = false;
    else throw new Error(`Unknown argument ${a}`);
  }
  return args;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  try {
    const args = parseArgs(process.argv.slice(2));
    const { zip, entries, functions } = packSite(args);
    fs.mkdirSync(path.dirname(args.out), { recursive: true });
    fs.writeFileSync(args.out, zip);
    const fn = entries.filter((e) => e.startsWith("_functions/")).length;
    console.log(
      `pack-site: ${path.relative(process.cwd(), args.out) || args.out} (${(zip.length / 1024).toFixed(0)} KB, ${entries.length} files, ${fn} in _functions/)`,
    );
    if (args.includeFunctions && !functions) console.log("pack-site: no _functions/ folder found; the zip is static only.");
    if (!fs.existsSync(args.variables ?? functionsVariables)) console.log("pack-site: _variables.json is the built-in default (§3.2).");
  } catch (error) {
    console.error(`pack-site: ${error.message}`);
    process.exit(1);
  }
}
