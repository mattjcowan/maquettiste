// The mock backend's pack authoring operations, extended preview and plan unit (generation-ui.md section 5),
// each JSON answer validated against the contract, with the ETag and 409 paths.
import { describe, expect, it } from "vitest";
import { validatorAt } from "@/mocks/contract";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;
const esc = (s: string) => s.replace(/~/g, "~0").replace(/\//g, "~1");

describe("mock packs", () => {
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

  it("lists and reads packs with files, roles, users and an ETag", async () => {
    const list = await call("GET", "/api/packs", "/api/packs");
    expect((list.json.packs as Json[]).map((p) => p.name)).toEqual(["csharp-dapper", "sql-ddl"]);
    const pack = await call("GET", "/api/packs/sql-ddl", "/api/packs/{pack}");
    expect(pack.etag).toBe(`"${pack.json.hash as string}"`);
    const files = pack.json.files as Json[];
    expect(files.find((f) => f.path === "table.scriban")).toMatchObject({ role: "template", usedBy: ["unit:table"] });
    expect(files.find((f) => f.path === "_shared.scriban")!.role).toBe("partial");
    expect((await call("GET", "/api/packs/ghost")).status).toBe(404);
  });

  it("writes files with If-Match, answers 409 with the disk text, creates with If-None-Match and refuses pack.json", async () => {
    const read = await call("GET", "/api/packs/sql-ddl/file?path=table.scriban", "/api/packs/{pack}/file");
    const hash = read.json.hash as string;
    expect((await call("PUT", "/api/packs/sql-ddl/file?path=table.scriban", undefined, { text: "x" })).status).toBe(428);
    const saved = await call("PUT", "/api/packs/sql-ddl/file?path=table.scriban", "/api/packs/{pack}/file", { text: "{{ x }}" }, { "If-Match": `"${hash}"` });
    expect(saved.status).toBe(200);
    const stale = await call("PUT", "/api/packs/sql-ddl/file?path=table.scriban", "/api/packs/{pack}/file", { text: "y" }, { "If-Match": `"${hash}"` });
    expect(stale.status).toBe(409);
    expect(stale.json.current).toBe("{{ x }}");
    const created = await call("PUT", "/api/packs/sql-ddl/file?path=notes/a.scriban", "/api/packs/{pack}/file", { text: "{{ if" }, { "If-None-Match": "*" });
    expect(created.status).toBe(201);
    expect((created.json.diagnostics as Json[])[0].rule).toBe("MQ6003");
    expect((await call("PUT", "/api/packs/sql-ddl/file?path=pack.json", undefined, { text: "{}" }, { "If-None-Match": "*" })).status).toBe(400);
    const inUse = await call("DELETE", "/api/packs/sql-ddl/file?path=_shared.scriban", "/api/packs/{pack}/file", undefined, {
      "If-Match": `"${"0".repeat(64)}"`,
    });
    expect(inUse.json.outcome).toBe("referenced");
    const gone = await call("DELETE", "/api/packs/sql-ddl/file?path=notes/a.scriban", "/api/packs/{pack}/file", undefined, {
      "If-Match": `"${created.json.hash as string}"`,
    });
    expect(gone.status).toBe(200);
  });

  it("saves pack.json whole, refuses an unknown scope and creates new packs", async () => {
    const pack = await call("GET", "/api/packs/sql-ddl");
    const document: Json = { ...(pack.json.document as Json), description: "Edited." };
    const saved = await call("PUT", "/api/packs/sql-ddl", "/api/packs/{pack}", document, { "If-Match": `"${pack.json.hash as string}"` });
    expect(saved.status).toBe(200);
    expect((await call("PUT", "/api/packs/sql-ddl", "/api/packs/{pack}", document, { "If-Match": `"${pack.json.hash as string}"` })).status).toBe(409);
    const units = (document.units as Json[]).map((u, i) => (i === 0 ? { ...u, for: "each tables" } : u));
    const invalid = await call("PUT", "/api/packs/sql-ddl", "/api/packs/{pack}", { ...document, units }, { "If-Match": `"${saved.json.hash as string}"` });
    expect(invalid.status).toBe(422);
    expect((invalid.json.diagnostics as Json[])[0].rule).toBe("MQ6021");
    expect((await call("POST", "/api/packs", "/api/packs", { name: "docs", from: "empty" })).status).toBe(201);
    expect((await call("POST", "/api/packs", "/api/packs", { name: "docs" })).status).toBe(409);
    expect((await call("POST", "/api/packs", "/api/packs", { name: "ddl-copy", from: "sql-ddl" })).status).toBe(201);
  });
});
