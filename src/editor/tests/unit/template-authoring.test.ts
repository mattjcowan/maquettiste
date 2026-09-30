// Template authoring close-out (generation-ui.md section 8, steps 6 and 7): completion and hover from the template
// context, the text-match line map between a template and its preview, the file-tree path checks, and the plan summary
// grouped by cause and by output root with the causes' links.
import { describe, expect, it } from "vitest";
import type { FileChange, PlanUnit } from "@/api/types";
import { completionsAt, hoverFor, inCodeBlock, pathBefore, wordAt, type TemplateCompletionData } from "@/code/scribanCompletion";
import { literalPieces, matchLines } from "@/workspaces/generate/lineMap";
import { namedByUnits, packPathProblem } from "@/workspaces/generate/templatesModel";
import { causeGroups, causeLink, causeSentence, rootGroups } from "@/workspaces/generate/planModel";

const context: TemplateCompletionData = {
  unit: "table",
  scope: "each entity",
  variables: [
    { name: "element", detail: "The element this unit renders for." },
    { name: "entity", detail: "The element, by its kind." },
    { name: "model", detail: "The resolved model." },
    { name: "pack", detail: "The pack: name, version and params." },
    { name: "pack.params.quoting", detail: "Parameter quoting." },
  ],
  members: {
    model: [{ name: "entities", type: "list" }],
    element: [
      { name: "name", type: "string" },
      { name: "attributes", type: "list" },
    ],
  },
  helpers: ["pascal_case", "sql_type"],
  registrations: [{ kind: "helper", name: "sql_type", declaredIn: "helpers.js" }],
};

const labels = (before: string) => completionsAt(context, before).map((s) => s.label);

describe("template completion", () => {
  it("offers nothing outside a code block and in a raw block", () => {
    expect(inCodeBlock("CREATE TABLE ")).toBe(false);
    expect(inCodeBlock("{{ x }} text")).toBe(false);
    expect(inCodeBlock("text {{ mo")).toBe(true);
    expect(labels("CREATE TABLE ")).toEqual([]);
  });

  it("offers the top-level variables, helpers, function objects and keywords", () => {
    const all = labels("{{ ");
    expect(all).toEqual(expect.arrayContaining(["element", "entity", "model", "pack", "pascal_case", "sql_type", "string", "for", "end"]));
    expect(all.filter((l) => l === "pack")).toHaveLength(1);
    expect([...all].sort()).toEqual(all);
  });

  it("offers the members after a dot: the model's, the element's under its scope name, parameters and function objects", () => {
    expect(labels("{{ model.")).toEqual(["entities"]);
    expect(labels("{{ entity.")).toEqual(["attributes", "name"]);
    expect(labels("{{ element.na")).toEqual(["attributes", "name"]);
    expect(labels("{{ pack.params.")).toEqual(["quoting"]);
    expect(labels("{{ string.")).toContain("upcase");
  });

  it("offers helpers and pipe functions after a pipe, saying where a pack helper comes from", () => {
    const items = completionsAt(context, "{{ entity.name | ");
    expect(items.map((i) => i.label)).toEqual(expect.arrayContaining(["pascal_case", "sql_type", "string.downcase", "array.size"]));
    expect(items.find((i) => i.label === "sql_type")?.detail).toBe("helper from helpers.js");
    expect(items.find((i) => i.label === "pascal_case")?.detail).toBe("built-in helper");
    expect(pathBefore("{{ x | string.up")).toEqual({ path: "string.up", afterPipe: true });
  });

  it("documents a word on hover from the same data", () => {
    expect(wordAt("{{ entity.name }}", 11)).toBe("entity.name");
    expect(hoverFor(context, "model")).toBe("**model**: The resolved model.");
    expect(hoverFor(context, "entity.name")).toBe("**entity.name** (string)");
    expect(hoverFor(context, "sql_type")).toBe("**sql_type**: helper from helpers.js");
    expect(hoverFor(context, "string.upcase")).toContain("Upper-cases");
    expect(hoverFor(context, "nothing_here")).toBeNull();
  });
});

describe("template and output lines", () => {
  it("keeps the literal text of a line and matches it in order", () => {
    expect(literalPieces("CREATE TABLE {{ name }} (")).toEqual(["CREATE TABLE", "("]);
    expect(literalPieces("{{ for a in b }}")).toEqual([]);
    const template = "CREATE TABLE {{ t }} (\n{{ for c in cols }}\n  {{ c.name }} {{ c.type }} NOT NULL,\n{{ end }}\n);";
    const output = "CREATE TABLE customers (\n  id int NOT NULL,\n  name text NOT NULL,\n);";
    const map = matchLines(template, output);
    expect(map.toOutput.get(1)).toEqual([1]);
    expect(map.toOutput.get(3)).toEqual([2, 3]);
    expect(map.toOutput.has(2)).toBe(false);
    expect(map.toOutput.has(5)).toBe(false); // ");" is too short to tell lines apart
    expect(map.toTemplate.get(3)).toEqual([3]);
  });
});

describe("file tree actions", () => {
  it("refuses a path outside the pack, pack.json and a taken name", () => {
    const files = ["table.scriban", "partials/header.scriban"];
    expect(packPathProblem("", files)).toMatch(/Name the file/);
    expect(packPathProblem("../x.scriban", files)).toMatch(/inside the pack folder/);
    expect(packPathProblem("/abs.scriban", files)).toMatch(/inside the pack folder/);
    expect(packPathProblem("pack.json", files)).toMatch(/Units tab/);
    expect(packPathProblem("table.scriban", files)).toBe("table.scriban already exists.");
    expect(packPathProblem("partials/footer.scriban", files)).toBeNull();
  });

  it("rewrites the units only for a file a unit names", () => {
    const info = (usedBy: string[]) => ({ path: "x", role: "template", size: 1, hash: "h", usedBy }) as never;
    expect(namedByUnits(info(["unit:table"]))).toBe(true);
    expect(namedByUnits(info(["companion:table"]))).toBe(true);
    expect(namedByUnits(info(["include:table.scriban"]))).toBe(false);
    expect(namedByUnits(undefined)).toBe(false);
  });
});

function unit(key: string, patch: Partial<PlanUnit> = {}): PlanUnit {
  const [head, element = null] = key.split(":");
  const [pack, name] = head.split("/");
  return {
    key,
    inputHash: "h",
    readKeys: [],
    skipped: false,
    outputs: [],
    pack,
    unit: name,
    template: `${name}.scriban`,
    elementId: element,
    reason: "inputs",
    causes: [],
    causeCount: 0,
    ...patch,
  };
}
const change = (path: string, kind: FileChange["kind"], unitKey: string): FileChange => ({
  path,
  kind,
  pack: unitKey.split("/")[0],
  unitKey,
  oldHash: null,
  newHash: null,
  diff: null,
});
const template = { kind: "template" as const, key: "t:sql-ddl/table.scriban", detail: "Template table.scriban changed", elementId: null, path: null };
const edited = { kind: "element" as const, key: "e:customer", detail: "Customer (entity) changed", elementId: "customer", path: null };
const setting = { kind: "setting" as const, key: "s:conventions", detail: "Setting conventions changed", elementId: null, path: null };

describe("plan summary by cause and by root", () => {
  const plan = {
    units: [
      unit("sql-ddl/table:a", { causes: [template, edited], causeCount: 2 }),
      unit("sql-ddl/table:b", { causes: [template], causeCount: 1 }),
      unit("sql-ddl/table:c", { causes: [setting], causeCount: 1 }),
      unit("sql-ddl/table:d", { skipped: true, reason: "unchanged" }),
      unit("csharp/entity:a", { reason: "new" }),
    ],
    changes: [
      change("db/main/tables/a.sql", "modified", "sql-ddl/table:a"),
      change("db/main/tables/b.sql", "modified", "sql-ddl/table:b"),
      change("db/main/tables/c.sql", "unchanged", "sql-ddl/table:c"),
      change("src/Gen/A.cs", "added", "csharp/entity:a"),
      change("README.md", "added", "csharp/entity:a"),
    ],
  };

  it("groups the written files by cause, most files first, with a link per cause", () => {
    const groups = causeGroups(plan);
    expect(groups.map(causeSentence)).toEqual([
      "New: no recorded state from an earlier run: 2 files",
      "Template table.scriban changed: 2 files",
      "Customer (entity) changed: 1 file",
    ]);
    expect(groups[1].link).toEqual({ type: "template", pack: "sql-ddl", path: "table.scriban" });
    expect(groups[2].link).toEqual({ type: "element", id: "customer" });
  });

  it("links settings, parameters and units, and not a deleted element or an output", () => {
    const u = { pack: "sql-ddl", unit: "table" };
    expect(causeLink(setting, u)).toEqual({ type: "setting", tab: "conventions" });
    expect(causeLink({ ...setting, key: "s:typeMaps" }, u)).toEqual({ type: "setting", tab: "project" });
    expect(causeLink({ kind: "parameter", key: "quoting", detail: "", elementId: null, path: null }, u)).toEqual({
      type: "parameter",
      pack: "sql-ddl",
      name: "quoting",
    });
    expect(causeLink({ kind: "unit", key: "table", detail: "", elementId: null, path: null }, u)).toEqual({ type: "unit", pack: "sql-ddl", unit: "table" });
    expect(causeLink({ kind: "absent", key: "e:x", detail: "", elementId: "x", path: null }, u)).toBeNull();
    expect(causeLink({ kind: "output-missing", key: "a.sql", detail: "", elementId: null, path: "a.sql" }, u)).toBeNull();
  });

  it("groups the written files by the longest known output root, else the first folder", () => {
    expect(rootGroups(plan, ["db/main", "db", "src/Gen/"]).map((g) => [g.root, g.files])).toEqual([
      ["(project root)", 1],
      ["db/main", 2],
      ["src/Gen", 1],
    ]);
    expect(rootGroups(plan, []).map((g) => g.root)).toEqual(["(project root)", "db", "src"]);
  });
});
