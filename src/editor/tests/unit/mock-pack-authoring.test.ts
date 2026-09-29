// The mock backend's unit paths, template context, file move, pack outputs, pack settings save and explain
// (generation-ui.md sections 4.3 and 5.1), each JSON answer validated against the contract, with the ETag and 409 paths.
import { describe, expect, it } from "vitest";
import { validatorAt } from "@/mocks/contract";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;
const esc = (s: string) => s.replace(/~/g, "~0").replace(/\//g, "~1");

describe("mock pack authoring", () => {
  const mock = useMockApi();

  async function call(method: string, path: string, template?: string, body?: unknown, headers: Record<string, string> = {}) {
    const response = await fetch(mock.url(path), {
      method,
      headers: { "Content-Type": "application/json", ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const json = /json/.test(response.headers.get("Content-Type") ?? "") ? ((await response.json()) as Json) : ({} as Json);
    if (template && response.status !== 400 && response.status !== 428) {
      const validate = validatorAt(`/paths/${esc(template)}/${method.toLowerCase()}/responses/${response.status}/content/application~1json/schema`);
      expect(validate(json), JSON.stringify(validate.errors?.slice(0, 3))).toBe(true);
    }
    return { status: response.status, json, etag: response.headers.get("ETag") };
  }

  it("lists a unit's paths and reports MQ6020 for a constant unsaved pattern", async () => {
    const paths = await call("POST", "/api/templates/paths", "/api/templates/paths", { pack: "sql-ddl", unit: "table" });
    expect(paths.status).toBe(200);
    expect(paths.json.count as number).toBeGreaterThan(1);
    const unitOverride = { id: "table", template: "table.scriban", for: "each table", output: "same.sql" };
    const same = await call("POST", "/api/templates/paths", "/api/templates/paths", { pack: "sql-ddl", unit: "table", unitOverride });
    expect((same.json.diagnostics as Json[]).map((d) => d.rule)).toContain("MQ6020");
    expect((await call("POST", "/api/templates/paths", undefined, { pack: "sql-ddl" })).status).toBe(400);
  });

  it("serves the template context of a unit", async () => {
    const context = await call("GET", "/api/templates/context?pack=sql-ddl&unit=table", "/api/templates/context");
    expect((context.json.variables as Json[]).map((v) => v.name)).toContain("table");
    expect((context.json.members as Record<string, Json[]>).element.map((m) => m.name)).toContain("columns");
    expect((await call("GET", "/api/templates/context?pack=sql-ddl&unit=ghost")).status).toBe(404);
  });

  it("moves a file with both hashes and rewrites the units", async () => {
    const pack = await call("GET", "/api/packs/sql-ddl");
    const file = await call("GET", "/api/packs/sql-ddl/file?path=table.scriban");
    const move = { from: "table.scriban", to: "tables/table.scriban" };
    expect((await call("POST", "/api/packs/sql-ddl/file/move", undefined, move)).status).toBe(428);
    const referenced = await call("POST", "/api/packs/sql-ddl/file/move", "/api/packs/{pack}/file/move", move, { "If-Match": `"${file.json.hash as string}"` });
    expect(referenced.json.outcome).toBe("referenced");
    const stale = await call(
      "POST",
      "/api/packs/sql-ddl/file/move",
      "/api/packs/{pack}/file/move",
      { ...move, updateUnits: true, expectedPackHash: "0".repeat(64) },
      {
        "If-Match": `"${file.json.hash as string}"`,
      },
    );
    expect(stale.status).toBe(409);
    const moved = await call(
      "POST",
      "/api/packs/sql-ddl/file/move",
      "/api/packs/{pack}/file/move",
      { ...move, updateUnits: true, expectedPackHash: pack.json.hash },
      {
        "If-Match": `"${file.json.hash as string}"`,
      },
    );
    expect(moved.status).toBe(200);
    const after = await call("GET", "/api/packs/sql-ddl");
    expect(((after.json.document as Json).units as Json[]).find((u) => u.id === "table")!.template).toBe("tables/table.scriban");
  });

  it("lists outputs, saves one pack's settings with the settings ETag and explains a unit", async () => {
    expect((await call("GET", "/api/packs/sql-ddl/outputs", "/api/packs/{pack}/outputs")).json.pack).toBe("sql-ddl");
    expect((await call("GET", "/api/packs/ghost/outputs")).status).toBe(404);

    const explained = await call("POST", "/api/generate/explain", "/api/generate/explain", { pack: "sql-ddl", unit: "ghost" });
    expect(explained.json.reason).toBe("unknown-unit");
    const notSelected = await call("POST", "/api/generate/explain", "/api/generate/explain", {
      pack: "sql-ddl",
      unit: "schema",
      packs: ["csharp-dapper"],
    });
    expect(notSelected.json.reason).toBe("not-selected");

    const settings = await call("GET", "/api/project/settings");
    const hash = settings.json.hash as string;
    const saved = await call(
      "PUT",
      "/api/project/settings/packs/sql-ddl",
      "/api/project/settings/packs/{pack}",
      { enabled: false },
      { "If-Match": `"${hash}"` },
    );
    expect(saved.status).toBe(200);
    const stale = await call(
      "PUT",
      "/api/project/settings/packs/sql-ddl",
      "/api/project/settings/packs/{pack}",
      { enabled: true },
      { "If-Match": `"${hash}"` },
    );
    expect(stale.status).toBe(409);
    const disabled = await call("POST", "/api/generate/explain", "/api/generate/explain", { pack: "sql-ddl", unit: "table" });
    expect(disabled.json.reason).toBe("pack-disabled");
  });
});
