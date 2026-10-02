// Settings › Validation: every built-in rule (GET /api/validation/rules) grouped by family, then the project's script
// rules (the rules extensions/rules/*.js register, GET /api/extensions/files), with a severity picker per rule. Save writes only the overrides to maquettiste.json `validation.rules` through PUT /api/project/settings ("Default"
// removes the key; MQ1xxx rules cannot be off), then revalidates so the Problems panel follows.
import { useState, type KeyboardEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { keys, useExtensionFiles, useSettings, useValidationRules } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import type { RuleCatalogEntry, SettingsJson } from "@/api/types";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Input, Select } from "@/components/ui/input";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { cn } from "@/lib/cn";
import {
  choicesOf,
  groupRules,
  overridesOf,
  sameOverrides,
  scriptRuleEntries,
  setOverride,
  withOverrides,
  type RuleChoice,
  type RuleOverrides,
} from "./validationRules";

const CHOICE_LABEL: Record<RuleChoice, string> = { default: "Default", error: "error", warning: "warning", info: "info", off: "off" };

export function ValidationSettings() {
  const settings = useSettings();
  const catalog = useValidationRules();
  const extensions = useExtensionFiles();
  const qc = useQueryClient();
  const { store } = useServices();
  const [draft, setDraft] = useState<RuleOverrides | null>(null);
  const [filter, setFilter] = useState("");
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);

  if (settings.isPending || !settings.data || catalog.isPending) return <Spinner label="Loading the validation rules" />;
  if (!catalog.data) return <EmptyState title="The validation rules could not be loaded." />;

  const saved = overridesOf(settings.data.json as SettingsJson);
  const value = draft ?? saved;
  const dirty = draft !== null && !sameOverrides(draft, saved);
  const count = Object.keys(value).length;
  const families = groupRules([...catalog.data, ...scriptRuleEntries(extensions.data?.files)], filter);

  const save = async () => {
    if (!settings.data) return;
    setBusy(true);
    setErrors([]);
    try {
      const result = await endpoints.saveSettings(withOverrides(settings.data.json as SettingsJson, value), settings.data.hash);
      if (result.outcome === "saved" && result.current) {
        qc.setQueryData(keys.settings, result.current);
        void qc.invalidateQueries({ queryKey: keys.project });
        void qc.invalidateQueries({ queryKey: keys.validation, exact: true });
        setDraft(null);
        store.getState().notify("Validation settings saved.");
      } else if (result.outcome === "conflict") {
        await qc.invalidateQueries({ queryKey: keys.settings });
        store.getState().notify("maquettiste.json changed on disk. Your changes are kept; review them and save again.", "error");
      } else setErrors(result.diagnostics.map((d) => `${d.rule} ${d.jsonPointer ?? ""} ${d.message}`));
    } finally {
      setBusy(false);
    }
  };

  const pick = (rule: RuleCatalogEntry, choice: RuleChoice) => setDraft(setOverride(value, rule, choice));

  // Rows are focusable: ArrowUp and ArrowDown move between them, Enter moves into the row's picker.
  const onRowKey = (e: KeyboardEvent<HTMLTableRowElement>) => {
    if (e.target !== e.currentTarget) return;
    const rows = [...(e.currentTarget.closest("tbody")?.querySelectorAll<HTMLTableRowElement>("tr[data-rule]") ?? [])];
    const at = rows.indexOf(e.currentTarget);
    const to =
      e.key === "ArrowDown" ? rows[at + 1] : e.key === "ArrowUp" ? rows[at - 1] : e.key === "Home" ? rows[0] : e.key === "End" ? rows[rows.length - 1] : null;
    if (to) {
      e.preventDefault();
      to.focus();
    } else if (e.key === "Enter") {
      e.preventDefault();
      e.currentTarget.querySelector("select")?.focus();
    }
  };

  return (
    <section className="flex max-w-5xl flex-col gap-2" aria-label="Validation" data-testid="validation-settings">
      <div className="flex items-center gap-2">
        <p className="text-12 text-secondary">
          The severity of each built-in rule and of the project&apos;s script rules, saved in maquettiste.json validation.rules. Default keeps the engine&apos;s
          severity (a script rule&apos;s own); model file rules (MQ1xxx) cannot be turned off.
        </p>
        <span className="ml-auto flex shrink-0 items-center gap-2">
          {dirty ? <span className="text-12 text-secondary">Unsaved changes</span> : null}
          <Button
            onClick={() => {
              setDraft(null);
              setErrors([]);
            }}
            disabled={!dirty || busy}
          >
            Discard
          </Button>
          <Button variant="primary" onClick={() => void save()} disabled={!dirty || busy} data-testid="save-validation">
            Save
          </Button>
        </span>
      </div>
      {errors.length ? (
        <ul role="alert" className="text-12 text-danger">
          {errors.map((d, i) => (
            <li key={i}>{d}</li>
          ))}
        </ul>
      ) : null}
      <div className="flex items-center gap-2">
        <Input
          className="h-6 w-72 text-12"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder="Filter by id or text"
          aria-label="Filter rules"
        />
        <span className="text-12 text-secondary" data-testid="validation-override-count">
          {count === 1 ? "1 override" : `${count} overrides`}
        </span>
        <Button size="sm" variant="ghost" onClick={() => setDraft({})} disabled={count === 0 || busy}>
          Reset all
        </Button>
      </div>
      {families.length === 0 ? (
        <p className="text-12 text-secondary">No rule matches the filter.</p>
      ) : (
        <table className="w-full table-fixed text-12" aria-label="Validation rules">
          <colgroup>
            <col className="w-4" />
            <col className="w-16" />
            <col />
            <col className="w-16" />
            <col className="w-28" />
          </colgroup>
          <thead>
            <tr className="h-6 text-left text-secondary">
              <th className="font-medium">
                <span className="sr-only">Changed</span>
              </th>
              <th className="font-medium">Rule</th>
              <th className="font-medium">Description</th>
              <th className="font-medium">Default</th>
              <th className="font-medium">Severity</th>
            </tr>
          </thead>
          <tbody>
            {families.map((f) => [
              <tr key={f.family} className="h-6 border-t border-default bg-surface">
                <th colSpan={5} scope="colgroup" className="px-1 text-left font-medium text-primary">
                  {f.label} <span className="font-normal text-secondary">{f.family}</span>
                </th>
              </tr>,
              ...f.rules.map((rule) => {
                const current: RuleChoice = (value[rule.id] as RuleChoice | undefined) ?? "default";
                const changed = ((saved[rule.id] as RuleChoice | undefined) ?? "default") !== current;
                return (
                  <tr
                    key={rule.id}
                    data-rule={rule.id}
                    data-testid={`rule-row-${rule.id}`}
                    tabIndex={0}
                    onKeyDown={onRowKey}
                    className="h-6 border-t border-default focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent"
                  >
                    <td className="text-center">
                      {changed ? (
                        <span
                          className="inline-block size-1.5 rounded-full bg-accent"
                          role="img"
                          aria-label="Changed"
                          title="Changed, not saved"
                          data-testid="rule-changed"
                        />
                      ) : null}
                    </td>
                    <td className="font-mono">{rule.id}</td>
                    <td className="truncate pr-2" title={rule.description}>
                      {rule.description}
                    </td>
                    <td className="text-secondary">{rule.defaultSeverity}</td>
                    <td>
                      <Select
                        className={cn("h-6 py-0 text-12", current !== "default" && "font-medium")}
                        value={current}
                        onChange={(e) => pick(rule, e.target.value as RuleChoice)}
                        aria-label={`Severity of ${rule.id}`}
                      >
                        {choicesOf(rule).map((c) => (
                          <option key={c} value={c}>
                            {CHOICE_LABEL[c]}
                          </option>
                        ))}
                      </Select>
                    </td>
                  </tr>
                );
              }),
            ])}
          </tbody>
        </table>
      )}
    </section>
  );
}
