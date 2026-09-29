// @vitest-environment node
// The §5 scripts: pack-site.mjs (zip contents, Directives.cs stamping, the functions hash) and
// deploy.mjs (the deploy URL, when _functions/ is sent, and one real POST to a stand-in host).
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { unzipSync } from "fflate";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

interface Packed {
  zip: Uint8Array;
  entries: string[];
  functions: string | null;
}
interface PackSiteModule {
  stampDirectives(text: string, version: string | null): string;
  functionsHash(dir: string): string | null;
  packSite(options: { dist: string; functions: string; variables: string; engineVersion?: string | null; includeFunctions?: boolean }): Packed;
  DEFAULT_VARIABLES: Record<string, { default: string; secret?: boolean }>;
}
interface DeployModule {
  deployUrl(env: Record<string, string | undefined>): string;
  shouldIncludeFunctions(hash: string | null, state: { functions?: string }, force?: boolean): boolean;
}

const scripts = path.resolve(import.meta.dirname, "../../scripts");
const load = <T>(name: string) => import(/* @vite-ignore */ path.join(scripts, name)) as Promise<T>;

let tmp: string;
let dist: string;
let functions: string;
const DIRECTIVES = "#:package Maquettiste.Engine@0.1.0\n#:package StaticSiteHost.Abstractions@*\n";

beforeAll(() => {
  tmp = fs.mkdtempSync(path.join(os.tmpdir(), "mq-pack-"));
  dist = path.join(tmp, "dist");
  functions = path.join(tmp, "_functions");
  fs.mkdirSync(path.join(dist, "assets"), { recursive: true });
  fs.writeFileSync(path.join(dist, "index.html"), "<!doctype html><div id=root></div>");
  fs.writeFileSync(path.join(dist, "_headers"), "/index.html\n  Cache-Control: no-cache\n");
  fs.writeFileSync(path.join(dist, "assets/app-abc.js"), "console.log(1)");
  fs.mkdirSync(path.join(functions, "nested"), { recursive: true });
  fs.writeFileSync(path.join(functions, "Directives.cs"), DIRECTIVES);
  fs.writeFileSync(path.join(functions, "Model.cs"), "class ModelEndpoints {}");
  fs.writeFileSync(path.join(functions, "nested/Ignored.cs"), "class Ignored {}");
});
afterAll(() => fs.rmSync(tmp, { recursive: true, force: true }));

describe("pack-site.mjs", () => {
  it("stamps only the engine's #:package line", async () => {
    const { stampDirectives } = await load<PackSiteModule>("pack-site.mjs");
    expect(stampDirectives(DIRECTIVES, "0.1.0-b0123456789ab")).toBe(
      "#:package Maquettiste.Engine@0.1.0-b0123456789ab\n#:package StaticSiteHost.Abstractions@*\n",
    );
    expect(stampDirectives(DIRECTIVES, null)).toBe(DIRECTIVES);
    expect(() => stampDirectives("// nothing", "2.0.0")).toThrow(/no '#:package Maquettiste.Engine/);
  });

  it("packs the build, _variables.json and the top-level function files", async () => {
    const { packSite } = await load<PackSiteModule>("pack-site.mjs");
    const packed = packSite({ dist, functions, variables: path.join(tmp, "missing.json"), engineVersion: "9.9.9" });
    expect(packed.entries).toEqual(["_functions/Directives.cs", "_functions/Model.cs", "_headers", "_variables.json", "assets/app-abc.js", "index.html"]);
    const files = unzipSync(packed.zip);
    expect(new TextDecoder().decode(files["_functions/Directives.cs"])).toContain("Maquettiste.Engine@9.9.9");
    const variables = JSON.parse(new TextDecoder().decode(files["_variables.json"])) as PackSiteModule["DEFAULT_VARIABLES"];
    expect(variables.MAQUETTISTE_EDITOR_TOKEN.secret).toBe(true);
    expect(variables.MAQUETTISTE_LOCAL_USER.default).toBe("local");
    expect(packed.functions).toMatch(/^[0-9a-f]{64}$/);
  });

  it("leaves _functions/ out on request and hashes content, not the stamp", async () => {
    const { packSite, functionsHash } = await load<PackSiteModule>("pack-site.mjs");
    const packed = packSite({ dist, functions, variables: path.join(tmp, "missing.json"), includeFunctions: false });
    expect(packed.entries.some((e) => e.startsWith("_functions/"))).toBe(false);
    expect(packed.functions).toBeNull();
    const before = functionsHash(functions);
    packSite({ dist, functions, variables: path.join(tmp, "missing.json"), engineVersion: "1.2.3" });
    expect(functionsHash(functions)).toBe(before);
    fs.writeFileSync(path.join(functions, "Model.cs"), "class ModelEndpoints { }");
    expect(functionsHash(functions)).not.toBe(before);
    expect(functionsHash(path.join(tmp, "none"))).toBeNull();
  });

  it("refuses a missing build", async () => {
    const { packSite } = await load<PackSiteModule>("pack-site.mjs");
    expect(() => packSite({ dist: path.join(tmp, "nope"), functions, variables: "" })).toThrow(/npm run build/);
  });
});

describe("deploy.mjs", () => {
  it("posts to the host deploy API of the editor's site", async () => {
    const { deployUrl } = await load<DeployModule>("deploy.mjs");
    expect(deployUrl({})).toBe("http://localhost:8080/api/v1/sites/maquettiste.localhost/deploy");
    expect(deployUrl({ MAQUETTISTE_HOST_URL: "http://127.0.0.1:9000/" })).toBe("http://127.0.0.1:9000/api/v1/sites/maquettiste.localhost/deploy");
  });

  it("sends _functions/ only when their hash changed, or when forced", async () => {
    const { shouldIncludeFunctions } = await load<DeployModule>("deploy.mjs");
    expect(shouldIncludeFunctions("a", {})).toBe(true);
    expect(shouldIncludeFunctions("a", { functions: "a" })).toBe(false);
    expect(shouldIncludeFunctions("a", { functions: "a" }, true)).toBe(true);
    expect(shouldIncludeFunctions("b", { functions: "a" })).toBe(true);
    expect(shouldIncludeFunctions(null, {}, true)).toBe(false);
  });

  it("uploads the zip with the API key as application/zip", async () => {
    const seen: { url?: string; type?: string; key?: string; bytes: number } = { bytes: 0 };
    const server = http.createServer((req, res) => {
      seen.url = req.url;
      seen.type = req.headers["content-type"];
      seen.key = req.headers["x-api-key"] as string;
      req.on("data", (c: Buffer) => (seen.bytes += c.length));
      req.on("end", () => res.writeHead(200, { "content-type": "application/json" }).end('{"release":"r7"}'));
    });
    await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
    const port = (server.address() as { port: number }).port;
    const { execFile } = await import("node:child_process");
    const out = await new Promise<string>((resolve, reject) =>
      execFile(
        process.execPath,
        [path.join(scripts, "deploy.mjs"), "--dist", dist],
        {
          env: {
            ...process.env,
            MAQUETTISTE_HOST_URL: `http://127.0.0.1:${port}`,
            MAQUETTISTE_DEPLOY_KEY: "k-123",
            MAQUETTISTE_ENGINE_VERSION: "1.0.0",
            MAQUETTISTE_DEPLOY_STATE: path.join(tmp, "state.json"),
          },
        },
        (error, stdout, stderr) => (error ? reject(new Error(stderr || error.message)) : resolve(stdout)),
      ),
    );
    server.close();
    expect(seen.url).toBe("/api/v1/sites/maquettiste.localhost/deploy");
    expect(seen.type).toBe("application/zip");
    expect(seen.key).toBe("k-123");
    expect(seen.bytes).toBeGreaterThan(100);
    expect(out).toContain("release r7");
  });
});
