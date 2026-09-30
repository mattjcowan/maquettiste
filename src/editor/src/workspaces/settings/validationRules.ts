// Settings › Validation as data: the rule catalog (GET /api/validation/rules) and the overrides of maquettiste.json
// `validation.rules` (rule id → error | warning | info | off). "Default" is no key; MQ1xxx rules cannot be off. Pure, so
// the tab and the tests share it.
import type { RuleCatalogEntry, SettingsJson } from "@/api/types";
import { clone } from "@/lib/json";

export type RuleSeverity = "error" | "warning" | "info" | "off";
export type RuleOverrides = Record<string, RuleSeverity>;
/** The picker's value: "default" removes the override. */
export type RuleChoice = RuleSeverity | "default";

const SEVERITIES: readonly RuleSeverity[] = ["error", "warning", "info", "off"];

/** The overrides in the settings, only the values the schema allows. */
export function overridesOf(json: SettingsJson): RuleOverrides {
  const rules = (json as { validation?: { rules?: Record<string, string> } }).validation?.rules ?? {};
  const out: RuleOverrides = {};
  for (const [id, value] of Object.entries(rules)) if ((SEVERITIES as readonly string[]).includes(value)) out[id] = value as RuleSeverity;
  return out;
}

/** The choices of one rule's picker, "off" left out for a rule that cannot be off. */
export function choicesOf(rule: Pick<RuleCatalogEntry, "canBeOff">): RuleChoice[] {
  return ["default", ...SEVERITIES.filter((s) => s !== "off" || rule.canBeOff)];
}

/** The overrides with one rule set; "default" (or "off" on a rule that cannot be off) removes its key. */
export function setOverride(overrides: RuleOverrides, rule: Pick<RuleCatalogEntry, "id" | "canBeOff">, choice: RuleChoice): RuleOverrides {
  const next = { ...overrides };
  if (choice === "default" || (choice === "off" && !rule.canBeOff)) delete next[rule.id];
  else next[rule.id] = choice;
  return next;
}

export function sameOverrides(a: RuleOverrides, b: RuleOverrides): boolean {
  const ka = Object.keys(a);
  return ka.length === Object.keys(b).length && ka.every((k) => a[k] === b[k]);
}

/** The settings with the overrides written to `validation.rules`, keys in ordinal order; none leaves `validation` out. */
export function withOverrides(json: SettingsJson, overrides: RuleOverrides): SettingsJson {
  const next = clone(json) as SettingsJson & { validation?: { rules?: Record<string, string> } };
  const ids = Object.keys(overrides).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
  if (ids.length) next.validation = { ...next.validation, rules: Object.fromEntries(ids.map((id) => [id, overrides[id]])) };
  else if (next.validation) {
    delete next.validation.rules;
    if (Object.keys(next.validation).length === 0) delete next.validation;
  }
  return next;
}

export interface RuleFamily {
  family: string;
  label: string;
  rules: RuleCatalogEntry[];
}

/** The rules matching the filter (id or description, ignoring case), grouped by family in id order. */
export function groupRules(rules: readonly RuleCatalogEntry[], filter: string): RuleFamily[] {
  const q = filter.trim().toLowerCase();
  const out: RuleFamily[] = [];
  for (const rule of rules) {
    if (q && !rule.id.toLowerCase().includes(q) && !rule.description.toLowerCase().includes(q)) continue;
    const last = out[out.length - 1];
    if (last && last.family === rule.family) last.rules.push(rule);
    else out.push({ family: rule.family, label: rule.familyLabel, rules: [rule] });
  }
  return out;
}
