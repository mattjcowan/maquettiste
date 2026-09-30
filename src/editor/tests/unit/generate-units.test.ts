// The pack editor's Units model, the path summary and the example path (generation-ui.md 1, 2.2 and 3.1).
import { describe, expect, it } from "vitest";
import { NO_OUTPUT, pathSummary } from "@/workspaces/generate/pathSummary";
import {
  duplicateUnit,
  examplePath,
  filesLabel,
  filterWords,
  insertUnit,
  modeLabel,
  moveUnit,
  removeUnit,
  sameDocument,
  SCOPE_OPTIONS,
  scopeHelp,
  setUnitField,
  toPackUnit,
  unitIdError,
  unitLine,
  unitsOf,
} from "@/workspaces/generate/unitsModel";
import { closePackTab, openPackTab } from "@/workspaces/generate/packTabs";
import { packRows, packTotals } from "@/workspaces/generate/explorerModel";
import type { components } from "@/api/schema";

type S = components["schemas"];

const doc = () => ({
  name: "sql-ddl",
  version: "1.0.0",
  "x-owner": "platform",
  units: [
    {
      id: "table",
      template: "table.scriban",
      for: "each table",
      output: "{{ kebab table.database.name }}/{{ if table.schema }}{{ table.schema }}/{{ end }}tables/{{ table.name }}.sql",
      "x-note": "kept",
    },
    { id: "schema", template: "schema.scriban", for: "select databases", output: "{{ kebab database.name }}/schema.sql" },
    { id: "migration", template: "migration.scriban", for: "select databases", mode: "once" },
  ],
});

describe("path summary", () => {
  it.each([
    [
      "{{ kebab table.database.name }}/{{ if table.schema }}{{ table.schema }}/{{ end }}tables/{{ table.name }}.sql",
      "<database>/[<schema>/]tables/<table>.sql",
    ],
    ["{{ kebab database.name }}/schema.sql", "<database>/schema.sql"],
    ["{{~ entity.name ~}}.cs", "<entity>.cs"],
    ["{{ name }}.txt", "<name>.txt"],
    ["static/all.sql", "static/all.sql"],
  ])("reads %s aloud", (pattern, text) => {
    expect(pathSummary(pattern)).toEqual({ text, raw: false });
  });

  it("gives back the raw pattern for what it cannot read", () => {
    for (const p of [
      "{{ for x in model.entities }}{{ x }}{{ end }}",
      "{{ a + b }}.sql",
      "{{ entity.name | string.downcase }}.cs",
      "{{ if a }}x",
      "{{ unclosed",
    ])
      expect(pathSummary(p)).toEqual({ text: p, raw: true });
  });

  it("says when the template writes its files, and reads custom delimiters", () => {
    expect(pathSummary(undefined).text).toBe(NO_OUTPUT);
    expect(pathSummary("<% entity.name %>.cs", { open: "<%", close: "%>" }).text).toBe("<entity>.cs");
  });
});

describe("units model", () => {
  it("labels files, scopes and modes in the UI's words", () => {
    expect(filesLabel("each table")).toBe("Each table");
    expect(filesLabel("model")).toBe("Once");
    expect(filesLabel("each package")).toBe("Once per package");
    expect(filesLabel("each database")).toBe("Once per database");
    expect(filesLabel("select databases")).toBe("Selector");
    expect(modeLabel("once")).toBe("Create only if missing");
    expect(modeLabel(undefined)).toBe("Overwrite");
    expect(scopeHelp("model")).toMatch(/^Runs once\./);
    expect(scopeHelp("each entity")).toMatch(/once per element of that kind/);
    for (const scope of ["each process", "each actor", "each scenario"]) {
      expect(scopeHelp(scope)).toBe("Runs once per process, actor or scenario; one file each.");
      expect(SCOPE_OPTIONS).toContain(scope);
    }
    expect(filesLabel("each scenario")).toBe("Each scenario");
    expect(scopeHelp("select databases")).toContain("selector databases");
    expect(unitLine(doc().units[0])).toBe("table · each table → table.scriban → <database>/[<schema>/]tables/<table>.sql");
    expect(unitLine({ id: "all", for: "model", template: "all.scriban" })).toBe(`all · once → all.scriban → ${NO_OUTPUT}`);
  });

  it("edits one field and keeps every member it does not show", () => {
    const next = setUnitField(doc(), 0, "output", "{{ table.name }}.sql");
    expect(next["x-owner"]).toBe("platform");
    expect(unitsOf(next)[0]).toMatchObject({ output: "{{ table.name }}.sql", "x-note": "kept" });
    expect(unitsOf(setUnitField(doc(), 1, "output", ""))[1]).not.toHaveProperty("output");
    expect(unitsOf(setUnitField(doc(), 2, "mode", "overwrite"))[2]).not.toHaveProperty("mode");
    expect(unitsOf(doc())[0]).toHaveProperty("output"); // the input is untouched
  });

  it("validates ids", () => {
    expect(unitIdError(doc(), 0, "table")).toBeNull();
    expect(unitIdError(doc(), 0, "schema")).toMatch(/already/);
    expect(unitIdError(doc(), 0, "Table")).toMatch(/Lowercase/);
    expect(unitIdError(doc(), 0, "9x")).toMatch(/Lowercase/);
  });

  it("inserts, duplicates, moves and removes", () => {
    const inserted = insertUnit(doc(), 0);
    expect(inserted.index).toBe(1);
    expect(unitsOf(inserted.doc)[1]).toEqual({ id: "unit", template: "unit.scriban", for: "each entity" });
    expect(unitsOf(insertUnit(inserted.doc, -1).doc)[4].id).toBe("unit-2");
    const dup = duplicateUnit(doc(), 0);
    expect(unitsOf(dup.doc)[1]).toMatchObject({ id: "table-copy", "x-note": "kept" });
    expect(unitsOf(duplicateUnit(dup.doc, 0).doc)[1].id).toBe("table-copy-2");
    const moved = moveUnit(doc(), 0, 1);
    expect(unitsOf(moved.doc).map((u) => u.id)).toEqual(["schema", "table", "migration"]);
    expect(moveUnit(doc(), 0, -1).index).toBe(0);
    expect(unitsOf(removeUnit(doc(), 1)).map((u) => u.id)).toEqual(["table", "migration"]);
  });

  it("maps a raw row to the contract's unit", () => {
    expect(toPackUnit(doc().units[2])).toMatchObject({ id: "migration", output: null, mode: "once", where: null, transforms: [] });
  });

  it("keeps the pair write mode, which renders a companion", () => {
    expect(toPackUnit({ id: "svc", template: "svc.scriban", for: "each entity", mode: "pair" }).mode).toBe("pair");
    expect(toPackUnit({ id: "svc", template: "svc.scriban", for: "each entity", mode: "bogus" }).mode).toBe("overwrite");
  });

  it("compares documents ignoring key order, nulls and the default mode, as the server writes them", () => {
    let d = insertUnit({ name: "p", units: [] }, 0).doc;
    d = setUnitField(d, 0, "mode", "once");
    d = setUnitField(d, 0, "output", "x/{{ name }}.sql");
    const u = (d.units as Record<string, unknown>[])[0];
    const canonical = { name: "p", units: [{ id: u.id, template: u.template, for: u.for, output: "x/{{ name }}.sql", mode: "once" }] };
    expect(sameDocument(d, canonical)).toBe(true);
    expect(sameDocument({ units: [{ id: "a", mode: "overwrite", formatter: null }] }, { units: [{ id: "a" }] })).toBe(true);
    expect(sameDocument({ units: [{ id: "a", mode: "once" }] }, { units: [{ id: "a" }] })).toBe(false);
    expect(sameDocument({ units: [{ id: "a" }, { id: "b" }] }, { units: [{ id: "b" }, { id: "a" }] })).toBe(false);
  });

  it("puts a filter in words", () => {
    expect(filterWords({ tags: ["api"], abstract: false })).toBe("tags: api; not abstract");
    expect(filterWords({ notPackages: ["legacy", "old"], database: "main" })).toBe("not packages: legacy, old; database: main");
    expect(filterWords(null)).toBe("");
  });
});

describe("example path", () => {
  const result = (paths: [string | null, string][], extra: Partial<S["UnitPath"]> = {}): S["UnitPathsResult"] => ({
    count: paths.length,
    rendered: paths.length,
    paths: paths.map(([elementId, path]) => ({ elementId, path, role: "main", root: "db", allowed: true, rule: null, ...extra })),
    diagnostics: [],
    elapsedMs: 1,
  });

  it("shows the chosen element's path, else the first in scope order", () => {
    const r = result([
      ["a", "db/main/tables/customers.sql"],
      ["b", "db/main/tables/invoices.sql"],
    ]);
    expect(examplePath(r, "b")).toMatchObject({ path: "db/main/tables/invoices.sql", elementId: "b", count: 2, collisions: [], rule: null });
    expect(examplePath(r, null)?.elementId).toBe("a");
    expect(examplePath(r, "gone")?.elementId).toBe("a");
    expect(examplePath(undefined, null)).toBeNull();
  });

  it("names the collision (MQ6020) and a path outside every root (MQ6019)", () => {
    const same = result([
      ["a", "db/all.sql"],
      ["b", "db/all.sql"],
      ["c", "db/all.sql"],
    ]);
    expect(examplePath(same, "b")).toMatchObject({ rule: "MQ6020", collisions: ["a", "c"] });
    expect(examplePath(result([["a", "../x.sql"]], { allowed: false, rule: "MQ6019" }), null)).toMatchObject({ rule: "MQ6019", allowed: false });
    expect(examplePath(result([]), null)).toMatchObject({ path: null, count: 0 });
  });
});

describe("pack tabs and explorer rows", () => {
  it("opens, shows and closes pack tabs", () => {
    let s: Parameters<typeof openPackTab>[0] = { packTabs: [], packTab: null, packPane: {}, packFocus: null };
    s = { ...s, ...openPackTab(s, "sql-ddl", "units", { unit: "table" }) };
    s = { ...s, ...openPackTab(s, "csharp-dapper") };
    expect(s.packTabs).toEqual(["sql-ddl", "csharp-dapper"]);
    s = { ...s, ...openPackTab(s, "sql-ddl", "outputs") };
    expect(s.packTab).toBe("sql-ddl");
    expect(s.packPane).toEqual({ "sql-ddl": "outputs" });
    s = { ...s, ...closePackTab(s, "sql-ddl") };
    expect(s).toMatchObject({ packTabs: ["csharp-dapper"], packTab: "csharp-dapper" });
    expect(closePackTab(s, "csharp-dapper").packTab).toBeNull();
  });

  it("flattens packs with units, parameters and outputs", () => {
    const pack: S["PackSummary"] = {
      name: "sql-ddl",
      version: "1.0.0",
      description: null,
      enabled: true,
      output: "db",
      fileCount: 5,
      diagnostics: [],
      units: unitsOf(doc()).map(toPackUnit),
    };
    const outputs: S["PackOutputs"] = {
      pack: "sql-ddl",
      lastWritten: "2026-09-29T14:02:00Z",
      outputs: [
        { path: "db/main/tables/customers.sql", unit: "table", elementId: "c", companion: false, root: "db", commit: true, mode: "overwrite", state: "edited" },
        { path: "db/old.sql", unit: "gone", elementId: null, companion: false, root: "db", commit: true, mode: "overwrite", state: "intact" },
      ],
    };
    const closed = packRows([pack], new Set(), new Map([["sql-ddl", { outputs }]]));
    expect(closed).toHaveLength(1);
    expect(closed[0]).toMatchObject({ label: "sql-ddl", detail: "3 units · 1 root", expandable: true, expanded: false, level: 1 });
    const open = packRows([pack], new Set(["p:sql-ddl", "p:sql-ddl/units", "p:sql-ddl/outputs", "p:sql-ddl/o:gone"]), new Map([["sql-ddl", { outputs }]]));
    expect(open.map((r) => r.kind)).toEqual([
      "pack",
      "units",
      "unit",
      "unit",
      "unit",
      "templates",
      "parameters",
      "outputs",
      "output-group",
      "output",
      "output-group",
    ]);
    expect(open[4]).toMatchObject({ detail: "Create only if missing", unit: "migration", level: 3, posinset: 3, setsize: 3 });
    expect(open[7].detail).toBe("last run 2026-09-29 14:02");
    expect(open[8]).toMatchObject({ label: "gone", detail: "1 file · 1 orphan" });
    expect(open[9]).toMatchObject({ state: "orphan", level: 4 });
    expect(open[10]).toMatchObject({ label: "table", detail: "1 file · 1 hand-edited" });
    expect(packTotals([pack])).toBe("1 pack · 3 units");
  });
});
