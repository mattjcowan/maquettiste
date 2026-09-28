#!/usr/bin/env node
// Deploys the editor to a running Maquettiste container (phase2-design.md §5): packs site.zip with
// pack-site.mjs and posts it to the host's deploy API. The host then sends `site.deployed` and open
// editors reload (or show the new-version banner while a draft is unsaved).
//
//   node scripts/deploy.mjs [--watch] [--functions] [--dist <dir>]
//
// Environment:
//   MAQUETTISTE_HOST_URL        default http://localhost:8080
//   MAQUETTISTE_DEPLOY_KEY      else read with `docker compose ... exec -T maquettiste cat /data/maquettiste/deploy.key`
//   MAQUETTISTE_ENGINE_VERSION  else read with the same exec from /opt/maquettiste/engine.version (PD24)
//   MAQUETTISTE_PROJECT_DIR     the folder the editor was started on, default ../../tmp/billing
//   MAQUETTISTE_SITE            default maquettiste.localhost
//
// _functions/ is included only when its content hash changed since the last deploy from this
// checkout (or with --functions): a zip without _functions/ keeps the live functions and skips a
// rebuild. --watch redeploys 300 ms after the last change under dist/ or _functions/.
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { editorRoot, distDir, functionsDir, repoRoot } from "./paths.mjs";
import { functionsHash, packSite } from "./pack-site.mjs";

// Remembers the hash of the last _functions/ sent (MAQUETTISTE_DEPLOY_STATE overrides the path).
const stateFile = process.env.MAQUETTISTE_DEPLOY_STATE || path.join(editorRoot, "node_modules/.cache/maquettiste-deploy.json");

export function deployUrl(env = process.env) {
  const base = (env.MAQUETTISTE_HOST_URL || "http://localhost:8080").replace(/\/+$/, "");
  return `${base}/api/v1/sites/${env.MAQUETTISTE_SITE || "maquettiste.localhost"}/deploy`;
}

function composeExec(command, env = process.env) {
  const projectDir = path.resolve(editorRoot, env.MAQUETTISTE_PROJECT_DIR || "../../tmp/billing");
  const compose = path.join(repoRoot, "docker/compose.yaml");
  return execFileSync("docker", ["compose", "-f", compose, "--project-directory", projectDir, "exec", "-T", "maquettiste", ...command], {
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  }).trim();
}

let cachedKey = null;
function deployKey() {
  if (process.env.MAQUETTISTE_DEPLOY_KEY) return process.env.MAQUETTISTE_DEPLOY_KEY;
  cachedKey ??= composeExec(["cat", "/data/maquettiste/deploy.key"]);
  return cachedKey;
}

let cachedVersion = null;
function engineVersion() {
  if (process.env.MAQUETTISTE_ENGINE_VERSION) return process.env.MAQUETTISTE_ENGINE_VERSION;
  cachedVersion ??= composeExec(["cat", "/opt/maquettiste/engine.version"]);
  return cachedVersion;
}

function readState() {
  try {
    return JSON.parse(fs.readFileSync(stateFile, "utf8"));
  } catch {
    return {};
  }
}

function writeState(state) {
  fs.mkdirSync(path.dirname(stateFile), { recursive: true });
  fs.writeFileSync(stateFile, JSON.stringify(state, null, 2));
}

/** Whether the next deploy should carry _functions/: forced, or their hash changed since the last one. */
export function shouldIncludeFunctions(currentHash, state, force = false) {
  if (!currentHash) return false;
  return force || state.functions !== currentHash;
}

export async function deployOnce({ forceFunctions = false, dist = distDir } = {}) {
  const state = readState();
  const current = functionsHash(functionsDir);
  const includeFunctions = shouldIncludeFunctions(current, state, forceFunctions);
  const { zip, entries, functions } = packSite({ dist, includeFunctions, engineVersion: includeFunctions ? engineVersion() : null });
  const url = deployUrl();
  const started = Date.now();
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/zip", "X-Api-Key": deployKey() },
    body: zip,
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${response.status} ${response.statusText} from ${url}: ${text.slice(0, 2000)}`);
  if (functions) writeState({ ...state, functions });
  let release = "";
  try {
    release = JSON.parse(text).release ?? "";
  } catch {
    /* a non-JSON answer still means success */
  }
  const fn = functions
    ? `, with _functions/ (${entries.filter((e) => e.startsWith("_functions/")).length} files)`
    : current
      ? ", functions unchanged"
      : ", static only (no _functions/ folder)";
  console.log(`deploy: ${release ? `release ${release}` : "deployed"} in ${Date.now() - started} ms (${(zip.length / 1024).toFixed(0)} KB${fn})`);
}

function watch({ forceFunctions }) {
  let timer = null;
  let running = false;
  let again = false;
  const run = async () => {
    if (running) {
      again = true;
      return;
    }
    running = true;
    try {
      if (fs.existsSync(path.join(distDir, "index.html"))) await deployOnce({ forceFunctions });
    } catch (error) {
      console.error(`deploy: ${error.message}`);
    } finally {
      forceFunctions = false;
      running = false;
      if (again) {
        again = false;
        schedule();
      }
    }
  };
  const schedule = () => {
    clearTimeout(timer);
    timer = setTimeout(() => void run(), 300);
  };
  for (const dir of [distDir, functionsDir]) {
    if (!fs.existsSync(dir)) continue;
    fs.watch(dir, { recursive: true }, schedule);
  }
  // dist/ may be emptied and recreated by a fresh build: watch its parent for that too.
  fs.watch(editorRoot, (_, name) => name === "dist" && schedule());
  console.log(`deploy: watching dist/ and _functions/, posting to ${deployUrl()}`);
  schedule();
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  const argv = process.argv.slice(2);
  const forceFunctions = argv.includes("--functions");
  const at = argv.indexOf("--dist");
  const dist = at >= 0 && argv[at + 1] ? path.resolve(argv[at + 1]) : distDir;
  if (argv.includes("--watch")) watch({ forceFunctions });
  else
    deployOnce({ forceFunctions, dist }).catch((error) => {
      console.error(`deploy: ${error.message}`);
      process.exit(1);
    });
}
