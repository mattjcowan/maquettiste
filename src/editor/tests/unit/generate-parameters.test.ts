// The Parameters form's model (generation-ui.md 3.2) and the Outputs states (2.1, 3.4).
import { describe, expect, it } from "vitest";
import { displayValue, packSection, parameterControl, parameterRows, parseParameter, undeclared, withParameter } from "@/workspaces/generate/parametersModel";
import { groupLine, groupOutputs, outputRows } from "@/workspaces/generate/outputsModel";
import { newPackError, startFromText } from "@/workspaces/generate/NewPackDialog";

const params = [
  { name: "comments", default: true, value: null, schema: null, required: false },
  {
    name: "quoting",
    default: "",
    value: null,
    schema: { type: "string", enum: ["", "always", "reserved"], title: "Quoting", description: "When to quote identifiers." },
    required: false,
  },
  { name: "indent", default: 2, value: null, schema: { type: "integer", minimum: 0, maximum: 8 }, required: true },
  { name: "strategyMap", default: { check: "check" }, value: null, schema: null, required: false },
  { name: "prefix", default: "", value: null, schema: { type: "string", pattern: "^[a-z]*$", maxLength: 4 }, required: false },
];

describe("parameters form", () => {
  it("infers the control from the schema, else from the default", () => {
    expect(parameterControl(null, true)).toBe("switch");
    expect(parameterControl(null, "x")).toBe("text");
    expect(parameterControl(null, 3)).toBe("number");
    expect(parameterControl(null, { a: 1 })).toBe("json");
    expect(parameterControl(null, [1])).toBe("json");
    expect(parameterControl({ enum: ["a"] }, "a")).toBe("select");
    expect(parameterControl({ type: "integer" }, "3")).toBe("number");
    expect(parameterControl({ type: ["boolean", "null"] }, null)).toBe("switch");
  });

  it("shows the project's values beside the defaults, in name order", () => {
    const rows = parameterRows(params, { quoting: "always" });
    expect(rows.map((r) => r.name)).toEqual(["comments", "indent", "prefix", "quoting", "strategyMap"]);
    const quoting = rows.find((r) => r.name === "quoting")!;
    expect(quoting).toMatchObject({
      control: "select",
      options: ["", "always", "reserved"],
      set: true,
      value: "always",
      title: "Quoting",
      description: "When to quote identifiers.",
    });
    expect(rows.find((r) => r.name === "comments")).toMatchObject({ control: "switch", set: false, value: undefined, defaultValue: true });
    expect(undeclared(params, { quoting: "always", legacy: 1 })).toEqual(["legacy"]);
  });

  it("parses and checks values against the schema subset", () => {
    const rows = parameterRows(params, {});
    const row = (n: string) => rows.find((r) => r.name === n)!;
    expect(parseParameter(row("comments"), false)).toEqual({ ok: true, value: false });
    expect(parseParameter(row("indent"), "4")).toEqual({ ok: true, value: 4 });
    expect(parseParameter(row("indent"), "4.5")).toMatchObject({ ok: false });
    expect(parseParameter(row("indent"), "9")).toEqual({ ok: false, error: "At most 8." });
    expect(parseParameter(row("indent"), "")).toMatchObject({ ok: false });
    expect(parseParameter(row("quoting"), "never")).toMatchObject({ ok: false });
    expect(parseParameter(row("strategyMap"), '{"check":"native"}')).toEqual({ ok: true, value: { check: "native" } });
    expect(parseParameter(row("strategyMap"), "{")).toMatchObject({ ok: false });
    expect(parseParameter(row("prefix"), "abcde")).toEqual({ ok: false, error: "At most 4 characters." });
    expect(parseParameter(row("prefix"), "A")).toMatchObject({ ok: false });
    expect(displayValue("json", { a: 1 })).toBe('{\n  "a": 1\n}');
  });

  it("sets and resets values and builds the section to save", () => {
    const set = withParameter({}, "quoting", "always");
    expect(set).toEqual({ quoting: "always" });
    expect(withParameter(set, "quoting", undefined)).toEqual({});
    expect(packSection({ enabled: true, output: "db", parameters: { a: 1 } }, { quoting: "always" })).toEqual({
      enabled: true,
      output: "db",
      parameters: { quoting: "always" },
    });
    expect(packSection({ enabled: false, parameters: { a: 1 } }, {})).toEqual({ enabled: false });
  });
});

describe("outputs and new pack", () => {
  it("adds the orphan state and groups by unit or root", () => {
    const rows = outputRows(
      [
        { path: "db/b.sql", unit: "table", elementId: "e1", companion: false, root: "db", commit: true, mode: "overwrite", state: "intact" },
        { path: "db/a.sql", unit: "table", elementId: "e2", companion: false, root: "db", commit: true, mode: "overwrite", state: "missing" },
        { path: "src/x.cs", unit: "old", elementId: null, companion: false, root: "src", commit: false, mode: "once", state: "intact" },
      ],
      ["table"],
      (id) => id === "e1",
    );
    expect(rows.map((r) => r.display)).toEqual(["clean", "orphan", "orphan"]);
    const byUnit = groupOutputs(rows, "unit");
    expect(byUnit.map((g) => g.key)).toEqual(["old", "table"]);
    expect(byUnit[1].rows.map((r) => r.path)).toEqual(["db/a.sql", "db/b.sql"]);
    expect(groupLine(byUnit[1])).toBe("2 files · 1 orphan");
    expect(groupOutputs(rows, "root").map((g) => g.key)).toEqual(["db", "src"]);
  });

  it("checks a new pack's name", () => {
    expect(newPackError("", [])).toBe("Enter a name.");
    expect(newPackError("My Pack", [])).toMatch(/Lowercase/);
    expect(newPackError("sql-ddl", ["sql-ddl"])).toMatch(/exists/);
    expect(newPackError("api-docs", ["sql-ddl"])).toBeNull();
    expect(startFromText("empty", 2, "api-docs")).toBe("One each-entity unit and its template, ready to edit.");
    expect(startFromText("sql-ddl", 9, "my-pack")).toBe("All 9 files of sql-ddl, renamed to my-pack.");
    expect(startFromText("sql-ddl", 1, "")).toBe("All 1 file of sql-ddl, renamed to <name>.");
  });
});
