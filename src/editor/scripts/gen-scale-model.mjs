// Generates the explorer's large mock model (explorer-redesign.md section 5, items 1 and 2): runs the
// bench app's write-model verb into tmp/scale, then packs the model into one gzipped JSON seed,
// src/mocks/data/large.json.gz (gitignored), which `?mock=large` loads. When `dotnet` is not on the
// path (or with --docker), the verb runs in the mcr.microsoft.com/dotnet/sdk:10.0 image with the repo
// mounted: the container installs the SDK global.json pins (the image carries a newer one, as
// docker/Dockerfile notes), runs as the calling user, and builds into a container-local artifacts
// folder, so the host's bin/ and obj/ are untouched and tmp/scale is owned by the caller.
//
//   node scripts/gen-scale-model.mjs [--out <dir>] [--skip-write] [--docker] [write-model options...]
//
// Extra options go to write-model unchanged (for example --entities 2000 --seed 7); its defaults
// are the explorer's large mock (5,000 entities, 20,000 relations, 40 nested domains, 150 diagrams).
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";
import { editorRoot, repoRoot } from "./paths.mjs";

export const seedFile = path.join(editorRoot, "src/mocks/data/large.json.gz");
export const SEED_FORMAT = "maquettiste-mock-seed/1";

function parseArgs(argv) {
  let out = path.join(repoRoot, "tmp/scale");
  let skipWrite = false;
  let docker = false;
  const pass = [];
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--out") out = path.resolve(argv[++i]);
    else if (argv[i] === "--skip-write") skipWrite = true;
    else if (argv[i] === "--docker") docker = true;
    else pass.push(argv[i]);
  }
  return { out, skipWrite, docker, pass };
}

function hasDotnet() {
  const probe = spawnSync("dotnet", ["--version"], { encoding: "utf8" });
  return probe.status === 0;
}

/** The shell script the SDK container runs: install the pinned SDK, then run write-model from a container-local build. */
function dockerScript(relative, pass) {
  const quote = (a) => `'${String(a).replace(/'/g, "'\\''")}'`;
  return [
    "set -e",
    "curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh",
    "bash /tmp/dotnet-install.sh --jsonfile /repo/global.json --install-dir /tmp/dotnet --no-path",
    "export DOTNET_ROOT=/tmp/dotnet",
    [
      "/tmp/dotnet/dotnet run -c Release --artifacts-path /tmp/mq-artifacts --project bench/Maquettiste.Bench --",
      "write-model --out",
      quote(relative),
      ...pass.map(quote),
    ].join(" "),
  ].join("\n");
}

function writeModel(out, pass, forceDocker) {
  const started = Date.now();
  let result;
  if (!forceDocker && hasDotnet()) {
    const project = path.join(repoRoot, "bench/Maquettiste.Bench");
    result = spawnSync("dotnet", ["run", "-c", "Release", "--project", project, "--", "write-model", "--out", out, ...pass], {
      stdio: "inherit",
      cwd: repoRoot,
    });
  } else {
    const relative = path.relative(repoRoot, out).split(path.sep).join("/");
    if (relative.startsWith("..") || path.isAbsolute(relative)) throw new Error(`Without dotnet, --out must be inside the repo (${repoRoot}).`);
    console.log("running write-model in mcr.microsoft.com/dotnet/sdk:10.0 (installs the SDK global.json pins)");
    // Run as the calling user (POSIX) so nothing root-owned lands in the checkout; HOME, the NuGet
    // cache and the build output all live in the container's /tmp.
    const user = typeof process.getuid === "function" ? ["--user", `${process.getuid()}:${process.getgid()}`] : [];
    const env = ["HOME=/tmp", "DOTNET_CLI_HOME=/tmp", "NUGET_PACKAGES=/tmp/nuget", "DOTNET_NOLOGO=1", "DOTNET_CLI_TELEMETRY_OPTOUT=1"].flatMap((e) => [
      "-e",
      e,
    ]);
    const args = [
      "run",
      "--rm",
      ...user,
      ...env,
      "-v",
      `${repoRoot}:/repo`,
      "-w",
      "/repo",
      "mcr.microsoft.com/dotnet/sdk:10.0",
      "bash",
      "-c",
      dockerScript(relative, pass),
    ];
    result = spawnSync("docker", args, { stdio: "inherit" });
  }
  if (result.status !== 0) throw new Error(`write-model failed (exit ${result.status ?? result.signal}).`);
  console.log(`write-model: ${((Date.now() - started) / 1000).toFixed(1)} s`);
}

function walk(dir, base, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, base, out);
    else if (entry.name.endsWith(".json") || entry.name.endsWith(".md")) out.push(path.relative(base, full).split(path.sep).join("/"));
  }
  return out;
}

/**
 * The mock's settings: the bench settings name the fanout pack, which the mock does not have, so
 * the seed keeps the conventions and points only at the packs the mock carries (sql-ddl, csharp-dapper).
 */
function mockSettings(benchSettings) {
  return {
    $schema: ".schema/v1/maquettiste.json",
    formatVersion: 1,
    name: "scale",
    outputs: { allow: [{ path: "db" }, { path: "src/Generated" }] },
    conventions: benchSettings.conventions,
    packs: { "csharp-dapper": { output: "src/Generated" }, "sql-ddl": { output: "db" } },
  };
}

export function pack(out) {
  const modelRoot = path.join(out, ".maquettiste");
  const settingsPath = path.join(modelRoot, "maquettiste.json");
  if (!fs.existsSync(path.join(modelRoot, "model"))) throw new Error(`No model under ${modelRoot}; run without --skip-write.`);
  const files = [{ path: "maquettiste.json", text: JSON.stringify(mockSettings(JSON.parse(fs.readFileSync(settingsPath, "utf8")))) }];
  const paths = walk(path.join(modelRoot, "model"), modelRoot, []).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
  for (const p of paths) files.push({ path: p, text: fs.readFileSync(path.join(modelRoot, p), "utf8") });
  const json = JSON.stringify({ format: SEED_FORMAT, files });
  const gz = zlib.gzipSync(json, { level: 9 });
  fs.mkdirSync(path.dirname(seedFile), { recursive: true });
  const ignore = path.join(path.dirname(seedFile), ".gitignore");
  if (!fs.existsSync(ignore)) fs.writeFileSync(ignore, "# generated by scripts/gen-scale-model.mjs\n*\n!.gitignore\n");
  fs.writeFileSync(seedFile, gz);
  const mb = (n) => (n / 1024 / 1024).toFixed(1);
  console.log(`packed ${files.length} files: ${mb(json.length)} MB of JSON, ${mb(gz.length)} MB gzipped -> ${path.relative(editorRoot, seedFile)}`);
}

// Compare real paths: import.meta.url is percent-encoded (and /C:/... on Windows) and not symlink-resolved.
const isMain = Boolean(process.argv[1]) && fs.realpathSync(process.argv[1]) === fs.realpathSync(fileURLToPath(import.meta.url));
if (isMain) {
  const { out, skipWrite, docker, pass } = parseArgs(process.argv.slice(2));
  try {
    if (!skipWrite) writeModel(out, pass, docker);
    pack(out);
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  }
}
