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

  it("names each path's element: a table with its database, an entity by name", async () => {
    const tables = await call("POST", "/api/templates/paths", "/api/templates/paths", { pack: "sql-ddl", unit: "table" });
    const table = (tables.json.paths as Json[]).find((p) => String(p.elementId).includes("@"))!;
    expect(table.elementKind).toBe("table");
    expect(table.elementName as string).toMatch(/^[^@]+ \(main\)$/);
    const entities = await call("POST", "/api/templates/paths", "/api/templates/paths", { pack: "csharp-dapper", unit: "entity" });
    expect((entities.json.paths as Json[]).map((p) => [p.elementName, p.elementKind])).toContainEqual(["Customer", "entity"]);
  });

  it("removes a pack with the pack.json hash and leaves its generated files untracked", async () => {
    const pack = await call("GET", "/api/packs/sql-ddl");
    expect((await call("DELETE", "/api/packs/sql-ddl")).status).toBe(428);
    const stale = await call("DELETE", "/api/packs/sql-ddl", "/api/packs/{pack}", undefined, { "If-Match": `"${"0".repeat(64)}"` });
    expect(stale.status).toBe(409);
    expect(stale.json.hash).toBe(pack.json.hash);
    expect((await call("DELETE", "/api/packs/ghost", "/api/packs/{pack}", undefined, { "If-Match": `"${String(pack.json.hash)}"` })).status).toBe(404);
    expect((await call("DELETE", "/api/packs/Bad%20Name", undefined, undefined, { "If-Match": `"${String(pack.json.hash)}"` })).status).toBe(400);

    const removed = await call("DELETE", "/api/packs/sql-ddl", "/api/packs/{pack}", undefined, { "If-Match": `"${String(pack.json.hash)}"` });
    expect(removed.status).toBe(200);
    expect(removed.json.files as string[]).toContain("pack.json");
    expect(removed.json.settingsHash).not.toBeNull();
    expect((await call("GET", "/api/packs/sql-ddl")).status).toBe(404);
    const list = await call("GET", "/api/packs");
    expect((list.json.packs as Json[]).map((p) => p.name)).not.toContain("sql-ddl");
    const settings = await call("GET", "/api/project/settings");
    expect((settings.json.json as { packs?: Json }).packs?.["sql-ddl"]).toBeUndefined();
  });

  it("serves the template context of a unit", async () => {
    const context = await call("GET", "/api/templates/context?pack=sql-ddl&unit=table", "/api/templates/context");
    expect((context.json.variables as Json[]).map((v) => v.name)).toContain("table");
    expect((context.json.members as Record<string, Json[]>).element.map((m) => m.name)).toContain("columns");
    expect((await call("GET", "/api/templates/context?pack=sql-ddl&unit=ghost")).status).toBe(404);
  });

  it("serves the template context of a process, actor or scenario unit", async () => {
    const pack = await call("GET", "/api/packs/sql-ddl");
    const document = pack.json.document as Json;
    const units = [
      ...(document.units as Json[]),
      { id: "flow", template: "table.scriban", for: "each process" },
      { id: "who", template: "table.scriban", for: "each actor" },
      { id: "walk", template: "table.scriban", for: "each scenario" },
    ];
    const saved = await call("PUT", "/api/packs/sql-ddl", "/api/packs/{pack}", { ...document, units }, { "If-Match": `"${pack.json.hash as string}"` });
    expect(saved.status).toBe(200);
    const cases: [string, string, string][] = [
      ["flow", "process", "all_states"],
      ["who", "actor", "gates"],
      ["walk", "scenario", "steps"],
    ];
    for (const [unit, alias, member] of cases) {
      const context = await call("GET", `/api/templates/context?pack=sql-ddl&unit=${unit}`, "/api/templates/context");
      expect((context.json.variables as Json[]).map((v) => v.name)).toContain(alias);
      expect((context.json.members as Record<string, Json[]>).element.map((m) => m.name)).toContain(member);
      expect(context.json.helpers as string[]).toEqual(expect.arrayContaining(["state_path", "iso_duration_ms"]));
    }
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
