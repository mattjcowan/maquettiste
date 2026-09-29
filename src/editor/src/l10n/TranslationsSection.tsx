// The Translations section of the inspector and the element editors' headers (reference-types-seeds-localization.md
// 3.10): collapsed by default and remembered per user, one compact row per translated locale with the display name,
// plural and description, the fallback text as a muted italic placeholder, and a stale marker with Confirm when the
// default text changed since the translation. Nothing renders while the project declares fewer than two locales.
import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { ChevronDown, ChevronRight } from "lucide-react";
import type { TranslationEntry, TranslationField } from "@/api/types";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Input, Textarea } from "@/components/ui/input";
import { Badge } from "@/components/ui/misc";
import { local } from "@/lib/storage";
import { cn } from "@/lib/cn";
import { localeName } from "./model";
import { useLocalization, useOwnerTranslations, writeTranslations } from "./queries";

const OPEN_KEY = "maquettiste.translationsOpen";
const FIELD_LABELS: Record<TranslationField, string> = { displayName: "Display name", pluralName: "Plural name", label: "Label", description: "Description" };
/** Standard-field localization covers the domain model only (RT 3.1): not databases, tables, mappings or diagrams. */
const LOCALIZED_KINDS = new Set(["package", "entity", "value-object", "scalar-type", "enum", "relation", "reference-type", "seed", "category"]);

export function TranslationsSection({ id, kind, className }: { id: string; kind: string; className?: string }) {
  const l10n = useLocalization();
  const [open, setOpen] = useState(() => local.get(OPEN_KEY) === "1");
  if (!l10n.enabled || !LOCALIZED_KINDS.has(kind)) return null;
  const toggle = () => {
    local.set(OPEN_KEY, open ? "0" : "1");
    setOpen(!open);
  };
  return (
    <section className={cn("flex flex-col gap-2", className)} aria-label="Translations" data-testid="translations-section">
      <button type="button" className="flex items-center gap-1 self-start text-12 font-medium text-secondary" aria-expanded={open} onClick={toggle}>
        {open ? <ChevronDown className="size-3.5" aria-hidden /> : <ChevronRight className="size-3.5" aria-hidden />}
        Translations
        <span className="font-normal">({l10n.locales.join(", ")})</span>
      </button>
      {open ? <TranslationRows id={id} locales={l10n.locales} /> : null}
    </section>
  );
}

function TranslationRows({ id, locales }: { id: string; locales: string[] }) {
  const results = useOwnerTranslations(id, locales);
  return (
    <div className="flex flex-col gap-2">
      {locales.map((locale, i) => {
        const entries = (results[i]?.data?.entries ?? []).filter((e) => e.id === id);
        return <LocaleRow key={locale} locale={locale} entries={entries} loading={results[i]?.isPending ?? true} />;
      })}
    </div>
  );
}

function LocaleRow({ locale, entries, loading }: { locale: string; entries: TranslationEntry[]; loading: boolean }) {
  return (
    <fieldset className="flex flex-col gap-1 rounded-control border border-default p-2" data-testid={`translations-${locale}`}>
      <legend className="px-1 text-12 font-medium">{localeName(locale)}</legend>
      {loading ? <p className="text-12 text-secondary">Loading…</p> : null}
      {entries.map((e) => (
        <TranslationInput key={e.field} locale={locale} entry={e} all={entries} />
      ))}
    </fieldset>
  );
}

function TranslationInput({ locale, entry, all }: { locale: string; entry: TranslationEntry; all: TranslationEntry[] }) {
  const qc = useQueryClient();
  const { store } = useServices();
  const [value, setValue] = useState(entry.translation ?? "");
  useEffect(() => setValue(entry.translation ?? ""), [entry.translation]);
  const label = `${FIELD_LABELS[entry.field]} (${locale})`;
  const inputId = `tr-${locale}-${entry.id}-${entry.field}`;
  const write = async (confirm = false) => {
    if (!confirm && value === (entry.translation ?? "")) return;
    const result = await writeTranslations(qc, locale, [{ id: entry.id, field: entry.field, value: value || null, confirm }], all);
    if (!result.ok) store.getState().notify(result.message, "error");
  };
  const placeholder = entry.effective ?? entry.source ?? "";
  const Control = entry.field === "description" ? Textarea : Input;
  return (
    <div className="grid grid-cols-[7rem_1fr] items-start gap-2">
      <label htmlFor={inputId} className="pt-1 text-12 text-secondary">
        {FIELD_LABELS[entry.field]}
        {entry.state === "stale" ? (
          <Badge tone="warning" className="ml-1" title="The default text changed since this was translated">
            stale
          </Badge>
        ) : null}
      </label>
      <div className="flex items-start gap-1">
        <Control
          id={inputId}
          aria-label={label}
          value={value}
          placeholder={placeholder}
          className="flex-1 placeholder:italic placeholder:text-secondary"
          onChange={(e: { target: { value: string } }) => setValue(e.target.value)}
          onBlur={() => void write()}
          onKeyDown={(e: React.KeyboardEvent) => {
            if (e.key === "Enter" && entry.field !== "description") void write();
          }}
          {...(entry.field === "description" ? { rows: 2 } : {})}
        />
        {entry.state === "stale" ? (
          <Button size="sm" variant="secondary" onClick={() => void write(true)} aria-label={`Confirm ${label}`}>
            Confirm
          </Button>
        ) : null}
      </div>
    </div>
  );
}
