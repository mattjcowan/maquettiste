// Settings › Locales (reference-types-seeds-localization.md 3.2 and 3.10; explorer-redesign.md 5): the default locale,
// the supported locales, fallbacks and the node kinds completeness counts, saved in maquettiste.json `localization`
// through PUT /api/project/settings. With two or more locales it also shows the completeness matrix (locales × domain
// shards, plus Reference data); a cell opens the translation queue for that locale and shard. The declaration form is
// always here, since it is where a second locale is added; the matrix and the queue are not shown with one locale.
import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2 } from "lucide-react";
import * as endpoints from "@/api/endpoints";
import { keys, useSettings } from "@/api/queries";
import type { LocalizationSettings, SettingsJson } from "@/api/types";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { Field, Input, Select } from "@/components/ui/input";
import { SectionTitle, Spinner } from "@/components/ui/misc";
import { clone } from "@/lib/json";
import { cn } from "@/lib/cn";
import {
  addLocale,
  completenessMatrix,
  isLocaleTag,
  localeName,
  localizationProblems,
  REQUIRE_KINDS,
  removeLocale,
  setFallbacks,
  toggleRequire,
} from "./model";
import { l10nKeys, useLocalization } from "./queries";
import { TranslationQueue } from "./TranslationQueue";

export function LocalesSettings() {
  const settings = useSettings();
  const l10n = useLocalization();
  const [queue, setQueue] = useState<{ locale: string; shard: string } | null>(null);
  if (settings.isPending || !settings.data) return <Spinner label="Loading settings" />;
  return (
    <div className="flex max-w-5xl flex-col gap-2">
      <LocalesForm key={settings.data.hash} />
      {l10n.enabled ? <Matrix onOpen={setQueue} current={queue} /> : null}
      {l10n.enabled && queue ? (
        <TranslationQueue key={`${queue.locale}|${queue.shard}`} locale={queue.locale} shard={queue.shard} onClose={() => setQueue(null)} />
      ) : null}
    </div>
  );
}

function LocalesForm() {
  const settings = useSettings();
  const qc = useQueryClient();
  const { store } = useServices();
  const base = settings.data!.json as SettingsJson & { localization?: LocalizationSettings };
  const [draft, setDraft] = useState<LocalizationSettings | null>(base.localization ? clone(base.localization) : null);
  const [adding, setAdding] = useState("");
  const [saving, setSaving] = useState(false);
  const [diagnostics, setDiagnostics] = useState<string[]>([]);
  const dirty = JSON.stringify(draft ?? null) !== JSON.stringify(base.localization ?? null);
  const problems = draft ? localizationProblems(draft) : [];

  const save = async () => {
    setSaving(true);
    try {
      const json = clone(base) as SettingsJson & { localization?: LocalizationSettings };
      if (draft) json.localization = draft;
      else delete json.localization;
      const result = await endpoints.saveSettings(json, settings.data!.hash);
      if (result.outcome === "saved" && result.current) {
        qc.setQueryData(keys.settings, result.current);
        void qc.invalidateQueries({ queryKey: keys.project });
        void qc.invalidateQueries({ queryKey: l10nKeys.status });
        void qc.invalidateQueries({ queryKey: l10nKeys.all });
        setDiagnostics([]);
        store.getState().notify("Locales saved.");
      } else if (result.outcome === "conflict") {
        void qc.invalidateQueries({ queryKey: keys.settings });
        store.getState().notify("maquettiste.json changed on disk; the locales were reloaded. Make your change again.", "error");
      } else setDiagnostics(result.diagnostics.map((d) => `${d.rule} ${d.jsonPointer ?? ""} ${d.message}`));
    } finally {
      setSaving(false);
    }
  };

  if (!draft)
    return (
      <section className="flex flex-col gap-2" aria-label="Locales">
        <SectionTitle>Locales</SectionTitle>
        <p className="text-13 text-secondary">
          The project declares no locales: every display name, plural and description is in one language. Declare the language the model is written in to add
          others later.
        </p>
        <Button className="self-start" onClick={() => setDraft({ defaultLocale: "en", locales: ["en"] })}>
          Declare locales
        </Button>
      </section>
    );

  const locales = draft.locales ?? [draft.defaultLocale];
  const tag = adding.trim();
  return (
    <section className="flex flex-col gap-2" aria-label="Locales">
      <SectionTitle
        actions={
          <Button onClick={() => void save()} disabled={!dirty || problems.length > 0 || saving} data-testid="save-locales">
            Save
          </Button>
        }
      >
        Locales
      </SectionTitle>
      <Field label="Default locale" htmlFor="l10n-default" hint="The language of the texts inside the element files.">
        <Select id="l10n-default" className="max-w-xs" value={draft.defaultLocale} onChange={(e) => setDraft({ ...draft, defaultLocale: e.target.value })}>
          {locales.map((l) => (
            <option key={l} value={l}>
              {localeName(l)}
            </option>
          ))}
        </Select>
      </Field>
      <div className="flex flex-col gap-1">
        <span className="text-12 font-medium text-secondary" id="l10n-supported">
          Supported locales and fallbacks
        </span>
        <table className="w-full max-w-3xl text-13" aria-labelledby="l10n-supported">
          <thead>
            <tr className="text-left text-12 text-secondary">
              <th className="py-1 font-medium">Locale</th>
              <th className="py-1 font-medium">Falls back to (comma-separated; empty: the default chain)</th>
              <th className="py-1">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {locales.map((l) => (
              <tr key={l} className="border-t border-default">
                <td className="py-1 pr-2">
                  {localeName(l)}
                  {l === draft.defaultLocale ? <span className="ml-1 text-11 text-secondary">default</span> : null}
                </td>
                <td className="py-1 pr-2">
                  {l === draft.defaultLocale ? null : (
                    <Input
                      aria-label={`Fallbacks of ${l}`}
                      defaultValue={(draft.fallbacks?.[l] ?? []).join(", ")}
                      onBlur={(e) => setDraft(setFallbacks(draft, l, e.target.value))}
                    />
                  )}
                </td>
                <td className="py-1 text-right">
                  {l === draft.defaultLocale ? null : (
                    <Button variant="ghost" size="icon" aria-label={`Remove locale ${l}`} onClick={() => setDraft(removeLocale(draft, l))}>
                      <Trash2 />
                    </Button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <form
          className="flex items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (!isLocaleTag(tag)) return;
            setDraft(addLocale(draft, tag));
            setAdding("");
          }}
        >
          <Input
            aria-label="New locale (BCP 47 tag)"
            placeholder="fr, fr-CA, de…"
            className="max-w-40"
            value={adding}
            onChange={(e) => setAdding(e.target.value)}
          />
          <Button type="submit" variant="secondary" size="sm" disabled={!tag || !isLocaleTag(tag)}>
            <Plus className="size-3.5" aria-hidden /> Add locale
          </Button>
        </form>
      </div>
      <fieldset className="flex flex-col gap-1">
        <legend className="text-12 font-medium text-secondary">Completeness counts (none checked: every translatable field)</legend>
        <div className="grid grid-cols-[repeat(auto-fill,minmax(10rem,1fr))] gap-1">
          {REQUIRE_KINDS.map((k) => (
            <CheckboxField
              key={k}
              id={`l10n-require-${k}`}
              label={k}
              checked={(draft.require ?? []).includes(k)}
              onChange={(on) => setDraft(toggleRequire(draft, k, on))}
            />
          ))}
        </div>
      </fieldset>
      {[...problems, ...diagnostics].length ? (
        <ul role="alert" className="text-12 text-danger" data-testid="locales-problems">
          {[...problems, ...diagnostics].map((p) => (
            <li key={p}>{p}</li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}

function Matrix({ onOpen, current }: { onOpen(q: { locale: string; shard: string }): void; current: { locale: string; shard: string } | null }) {
  const l10n = useLocalization();
  const { locales, rows } = completenessMatrix(l10n.data);
  return (
    <section className="flex flex-col gap-2" aria-label="Completeness">
      <SectionTitle>Completeness</SectionTitle>
      <p className="text-12 text-secondary">Percent translated per domain; stale translations count as not done. Open a cell to work through its queue.</p>
      <table className="w-full max-w-3xl text-13" aria-label="Completeness by domain and locale" data-testid="completeness-matrix">
        <thead>
          <tr className="text-left text-12 text-secondary">
            <th className="py-1 font-medium">Domain</th>
            {locales.map((l) => (
              <th key={l} className="py-1 font-medium">
                {localeName(l)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.stem} className="border-t border-default">
              <th scope="row" className="py-1 pr-2 text-left font-normal">
                {r.label}
              </th>
              {locales.map((l) => {
                const c = r.cells[l];
                if (!c)
                  return (
                    <td key={l} className="py-1 text-secondary">
                      —
                    </td>
                  );
                const active = current?.locale === l && current.shard === c.shard;
                return (
                  <td key={l} className="py-1">
                    <button
                      type="button"
                      className={cn(
                        "rounded-control border px-2 py-0.5 text-12 hover:border-accent",
                        active ? "border-accent bg-accent-subtle" : "border-default",
                        c.percent === 100 && c.stale === 0 ? "text-success" : "text-primary",
                      )}
                      aria-label={`${r.label} in ${l}: ${c.percent} % translated, ${c.missing} missing, ${c.stale} stale. Open the queue`}
                      aria-pressed={active}
                      onClick={() => onOpen({ locale: l, shard: c.shard })}
                    >
                      {c.percent} %{c.stale ? ` · ${c.stale} stale` : ""}
                    </button>
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
