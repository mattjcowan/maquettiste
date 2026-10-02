// The Extensions tab's data (extensionsModel.ts), the script rule completion (ruleCompletion.ts), the script rules in
// Settings › Validation, and the mock's extensions folder: the New templates are valid as written, a rule written through
// the mock reports x/<id> findings on the model, a broken one is MQ5002 on its file, and an invalid schema writes nothing.
import { describe, expect, it } from "vitest";
import Ajv2020 from "ajv/dist/2020";
import extensionSchema from "@/code/extensionSchema.json";
import { isRuleModel, ruleCompletions, ruleHover, ruleSignatureAt } from "@/code/ruleCompletion";
import { MockBackend } from "@/mocks/backend";
import {
  EXTENSIONS_KEY,
  extensionPathOf,
  extensionPathProblem,
  extensionRows,
  findingsFor,
  freePath,
  kindOfPath,
  ruleTemplate,
  schemaTemplate,
  type ExtensionFile,
} from "@/workspaces/generate/extensionsModel";
import { EXTENSIONS_TAB, closePackTab, openExtensionsTab } from "@/workspaces/generate/packTabs";
import { scriptRuleEntries } from "@/workspaces/settings/validationRules";
import type { Diagnostic } from "@/api/types";

const finding = (rule: string, filePath: string | null, extra: Partial<Diagnostic> = {}): Diagnostic => ({
  rule,
  severity: "error",
  message: rule,
  elementId: null,
  filePath,
  jsonPointer: null,
  line: null,
  column: null,
  ...extra,
});

const files: ExtensionFile[] = [
  { path: "retention.json", kind: "schema", size: 10, hash: "a".repeat(64), diagnostics: [], rules: [] },
  {
    path: "rules/naming.js",
    kind: "rule",
    size: 10,
    hash: "b".repeat(64),
    diagnostics: [finding("MQ5002", ".maquettiste/extensions/rules/naming.js", { line: 2 })],
    rules: [{ id: "x/entity-names", severity: "warning" }],
  },
];

describe("extension files", () => {
  it("accepts the two shapes the engine loads and keeps a file's kind", () => {
    expect(extensionPathProblem("audit.json", "schema", [])).toBeNull();
    expect(extensionPathProblem("rules/audit.js", "rule", [])).toBeNull();
    expect(extensionPathProblem("rules/audit.json", "schema", [])).toMatch(/<name>\.json/);
    expect(extensionPathProblem("audit.js", "rule", [])).toMatch(/rules\/<name>\.js/);
    expect(extensionPathProblem("rules/audit.js", "schema", [])).toMatch(/stays at the top/);
    expect(extensionPathProblem("retention.json", "schema", ["retention.json"])).toMatch(/already exists/);
    expect(extensionPathProblem("../x.json", "schema", [])).not.toBeNull();
    expect(freePath("rule", ["rules/new-rule.js"])).toBe("rules/new-rule-2.js");
    expect(kindOfPath("rules/a.js")).toBe("rule");
    expect(extensionPathOf(finding("MQ5002", ".maquettiste/extensions/rules/naming.js"))).toBe("rules/naming.js");
    expect(extensionPathOf(finding("MQ3001", ".maquettiste/model/entities/a.json"))).toBeNull();
  });

  it("writes New files that are valid as they are", () => {
    const check = new Ajv2020({ strict: false }).compile(extensionSchema as object);
    expect(check(JSON.parse(schemaTemplate("audit-trail.json")))).toBe(true);
    const backend = new MockBackend();
    const written = backend.extensions.write("rules/my-rule.js", ruleTemplate("rules/my-rule.js"), null);
    expect(written.body.outcome).toBe("saved");
    expect(written.body.diagnostics).toEqual([]);
    expect(backend.extensions.list().files.find((f) => f.path === "rules/my-rule.js")?.rules).toEqual([{ id: "x/my-rule", severity: "warning" }]);
    expect(backend.model.validate().diagnostics.filter((d) => d.rule === "x/my-rule")).toEqual([]);
  });

  it("shows the Extensions node with a row per file and its problem count", () => {
    const closed = extensionRows(files, new Set(), 3, 3);
    expect(closed).toHaveLength(1);
    expect(closed[0]).toMatchObject({ key: EXTENSIONS_KEY, label: "Extensions", detail: "1 schema · 1 rule", warnings: 1, setsize: 3, posinset: 3 });
    const open = extensionRows(files, new Set([EXTENSIONS_KEY]), 3, 3);
    expect(open.map((r) => [r.label, r.detail, r.level])).toEqual([
      ["Extensions", "1 schema · 1 rule", 1],
      ["retention.json", "custom properties", 2],
      ["rules/naming.js", "x/entity-names", 2],
    ]);
  });

  it("lists a file's findings once, from the list, the save and the report", () => {
    const report = [finding("MQ5002", ".maquettiste/extensions/rules/naming.js", { line: 2 }), finding("MQ3001", ".maquettiste/model/entities/a.json")];
    const found = findingsFor("rules/naming.js", ".maquettiste/extensions", { listed: files[1].diagnostics, report });
    expect(found.map((d) => [d.rule, d.line])).toEqual([["MQ5002", 2]]);
  });

  it("opens and closes the Extensions tab beside the pack tabs", () => {
    const state = { packTabs: ["sql-ddl"], packTab: "sql-ddl", packPane: {}, packFocus: null, extensionFile: null };
    const opened = openExtensionsTab(state, "rules/naming.js");
    expect(opened).toEqual({ packTabs: ["sql-ddl", EXTENSIONS_TAB], packTab: EXTENSIONS_TAB, extensionFile: "rules/naming.js" });
    expect(closePackTab({ ...state, ...opened }, EXTENSIONS_TAB)).toMatchObject({ packTabs: ["sql-ddl"], packTab: "sql-ddl" });
  });

  it("puts the project's script rules in Settings › Validation after the built-in families", () => {
    expect(scriptRuleEntries(files)).toEqual([
      {
        id: "x/entity-names",
        defaultSeverity: "warning",
        description: "Script rule in extensions/rules/naming.js.",
        family: "x/",
        familyLabel: "Script rules",
        canBeOff: true,
      },
    ]);
  });
});

describe("script rule completion", () => {
  it("offers the maquettiste.rule API, the model methods, severities and kinds", () => {
    expect(ruleCompletions("maquettiste.").map((s) => s.label)).toEqual(["rule"]);
    expect(ruleCompletions("  check(element, model, report) {\n    const x = model.").map((s) => s.label)).toEqual(["get", "all", "referencesTo"]);
    expect(ruleCompletions('  severity: "').map((s) => s.label)).toEqual(["error", "warning", "info"]);
    expect(ruleCompletions('  kinds: ["entity", "').map((s) => s.label)).toContain("relation");
    expect(ruleCompletions("element.").map((s) => s.label)).toContain("attributes");
    expect(ruleCompletions("    rep").map((s) => s.label)).toEqual(expect.arrayContaining(["maquettiste.rule", "report", "element", "model"]));
    expect(ruleCompletions("foo.")).toEqual([]);
  });

  it("shows report's signature and the argument the cursor is on", () => {
    const first = ruleSignatureAt('    report("Too long", ');
    expect(first?.label).toMatch(/^report\(message: string, options\?/);
    expect(first?.activeParameter).toBe(1);
    expect(ruleSignatureAt('    report(`a ${[1, 2].join(",")}`')?.activeParameter).toBe(0);
    expect(ruleSignatureAt("    model.all(")?.label).toBe("model.all(kind: string)");
    expect(ruleSignatureAt("maquettiste.rule({ id: 'a', ")?.label).toMatch(/^maquettiste\.rule/);
    expect(ruleSignatureAt("foo(")).toBeNull();
    expect(ruleHover("report")).toMatch(/JSON pointer/);
    expect(isRuleModel("file:///extensions/rules/naming.js")).toBe(true);
    expect(isRuleModel("file:///templates/sql-ddl/helpers.js")).toBe(false);
  });
});

describe("the mock's extensions folder", () => {
  it("runs a saved rule over the model and reports a broken one on its file", () => {
    const backend = new MockBackend();
    const list = backend.extensions.list();
    expect(list.files.map((f) => [f.path, f.kind])).toEqual([
      ["persona.json", "schema"],
      ["retention.json", "schema"],
      ["rules/naming.js", "rule"],
    ]);
    expect(backend.model.validate().diagnostics.filter((d) => d.rule.startsWith("x/"))).toEqual([]);

    const rule = backend.extensions.read("rules/naming.js")!;
    const strict = backend.extensions.write("rules/naming.js", rule.text.replace("> 30", "> 6"), rule.hash);
    expect(strict.status).toBe(200);
    const names = backend.model.validate().diagnostics.filter((d) => d.rule === "x/entity-names");
    expect(names.length).toBeGreaterThan(0);
    expect(names.every((d) => d.severity === "warning" && d.jsonPointer === "/name")).toBe(true);

    const broken = backend.extensions.write("rules/naming.js", "maquettiste.rule({\n  id: 'x',,\n});\n", strict.body.hash);
    expect(broken.body.diagnostics.map((d) => [d.rule, d.filePath])).toEqual([["MQ5002", ".maquettiste/extensions/rules/naming.js"]]);
    expect(backend.model.validate().diagnostics.some((d) => d.rule === "MQ5002")).toBe(true);
    expect(backend.extensions.write("rules/naming.js", "//", strict.body.hash).status).toBe(409);
  });

  it("refuses a schema that is not valid and follows a valid one in the project", () => {
    const backend = new MockBackend();
    const invalid = backend.extensions.write("audit.json", '{ "name": "audit" }', null);
    expect(invalid.status).toBe(422);
    expect(invalid.body.diagnostics[0].rule).toBe("MQ5004");
    expect(backend.extensions.read("audit.json")).toBeNull();
    const saved = backend.extensions.write("audit.json", schemaTemplate("audit.json"), null);
    expect(saved.status).toBe(201);
    expect(backend.model.project().extensions.map((e) => e.name)).toEqual(["audit", "persona", "retention"]);
    expect(backend.extensions.move("audit.json", "rules/audit.js", saved.body.hash!).status).toBe(422);
    expect(backend.extensions.delete("audit.json", saved.body.hash!).status).toBe(200);
    expect(backend.model.project().extensions.map((e) => e.name)).toEqual(["persona", "retention"]);
  });
});
