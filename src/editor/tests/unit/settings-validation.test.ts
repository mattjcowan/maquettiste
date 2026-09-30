// Settings › Validation: the overrides derived from maquettiste.json validation.rules, set and reset, the MQ1 rules
// without "off", the payload written; and the mock's catalog route and localization rules that the tab drives.
import { describe, expect, it } from "vitest";
import * as endpoints from "@/api/endpoints";
import type { RuleCatalogEntry, SettingsJson } from "@/api/types";
import { choicesOf, groupRules, overridesOf, sameOverrides, setOverride, withOverrides } from "@/workspaces/settings/validationRules";
import { useMockApi } from "./harness";

const rule = (id: string, family: string, familyLabel: string, description = "A rule."): RuleCatalogEntry => ({
  id,
  defaultSeverity: "info",
  description,
  family,
  familyLabel,
  canBeOff: !id.startsWith("MQ1"),
});
const MQ1003 = rule("MQ1003", "MQ10xx", "Model files", "File is not in canonical form.");
const MQ7204 = rule("MQ7204", "MQ72xx", "Localization", "A locale is incomplete in a shard.");
const MQ7205 = rule("MQ7205", "MQ72xx", "Localization", "A missing translation.");
const settings = (rules?: Record<string, string>) => ({ formatVersion: 1, ...(rules ? { validation: { rules } } : {}) }) as unknown as SettingsJson;

describe("validation rule overrides", () => {
  it("are derived from validation.rules, unknown values left out", () => {
    expect(overridesOf(settings())).toEqual({});
    expect(overridesOf(settings({ MQ7204: "off", MQ3017: "error", MQ2006: "loud" }))).toEqual({ MQ7204: "off", MQ3017: "error" });
  });

  it("set a severity, and Default removes the key", () => {
    const set = setOverride({}, MQ7204, "off");
    expect(set).toEqual({ MQ7204: "off" });
    expect(setOverride(set, MQ7205, "warning")).toEqual({ MQ7204: "off", MQ7205: "warning" });
    expect(setOverride(set, MQ7204, "default")).toEqual({});
    expect(sameOverrides({ a: "off" } as never, { a: "off" } as never)).toBe(true);
    expect(sameOverrides({ a: "off" } as never, {})).toBe(false);
  });

  it("offer no off for an MQ1 rule, and refuse it", () => {
    expect(choicesOf(MQ1003)).toEqual(["default", "error", "warning", "info"]);
    expect(choicesOf(MQ7204)).toEqual(["default", "error", "warning", "info", "off"]);
    expect(setOverride({}, MQ1003, "off")).toEqual({});
    expect(setOverride({}, MQ1003, "error")).toEqual({ MQ1003: "error" });
  });

  it("write only the overrides, in id order, and leave validation out when there are none", () => {
    const base = settings({ MQ3017: "error" });
    expect(withOverrides(base, { MQ7205: "warning", MQ7204: "off" })).toEqual({
      formatVersion: 1,
      validation: { rules: { MQ7204: "off", MQ7205: "warning" } },
    });
    expect(Object.keys((withOverrides(base, { MQ7205: "warning", MQ7204: "off" }) as { validation: { rules: object } }).validation.rules)).toEqual([
      "MQ7204",
      "MQ7205",
    ]);
    expect(withOverrides(base, {})).toEqual({ formatVersion: 1 });
    expect(base).toEqual(settings({ MQ3017: "error" }));
  });

  it("group by family and filter by id or text", () => {
    const all = [MQ1003, MQ7204, MQ7205];
    expect(groupRules(all, "").map((f) => [f.family, f.label, f.rules.length])).toEqual([
      ["MQ10xx", "Model files", 1],
      ["MQ72xx", "Localization", 2],
    ]);
    expect(groupRules(all, "mq7204").flatMap((f) => f.rules.map((r) => r.id))).toEqual(["MQ7204"]);
    expect(groupRules(all, "CANONICAL").flatMap((f) => f.rules.map((r) => r.id))).toEqual(["MQ1003"]);
    expect(groupRules(all, "nothing like it")).toEqual([]);
  });
});

describe("the mock's rule catalog and localization rules", () => {
  useMockApi({ scenarios: ["locales"] });

  it("lists every rule with its family, MQ1 rules not off", async () => {
    const rules = await endpoints.listValidationRules();
    expect(rules.length).toBeGreaterThan(100);
    expect(rules.find((r) => r.id === "MQ1001")).toMatchObject({ family: "MQ10xx", canBeOff: false });
    expect(rules.find((r) => r.id === "MQ7204")).toMatchObject({ family: "MQ72xx", familyLabel: "Localization", canBeOff: true });
  });

  it("reports MQ7204, drops it when off and reports MQ7205 when it is given a severity", async () => {
    const rules = (report: Awaited<ReturnType<typeof endpoints.validate>>) => new Set(report.diagnostics.map((d) => d.rule));
    expect(rules(await endpoints.validate({}))).toContain("MQ7204");
    expect(rules(await endpoints.validate({}))).not.toContain("MQ7205");

    const doc = await endpoints.getSettings();
    const saved = await endpoints.saveSettings(withOverrides(doc.json as SettingsJson, { MQ7204: "off", MQ7205: "warning" }), doc.hash);
    expect(saved.outcome).toBe("saved");
    const after = await endpoints.validate({});
    expect(rules(after)).not.toContain("MQ7204");
    expect(after.diagnostics.filter((d) => d.rule === "MQ7205").every((d) => d.severity === "warning")).toBe(true);
    expect(rules(after)).toContain("MQ7205");
  });
});
