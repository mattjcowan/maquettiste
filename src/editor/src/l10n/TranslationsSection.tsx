// The Translations section of the inspector and the element editors' headers (reference-types-seeds-localization.md
// 3.10): collapsed by default and remembered per user, one compact row per translated locale with the display name,
// plural and description, the fallback text as a muted italic placeholder, and a stale marker with Confirm when the
// default text changed since the translation. Nothing renders while the project declares fewer than two locales.
// FieldTranslations is the same for an element's fields (a reference type's code, label and user fields): per locale,
// one compact line per field with its display name and description.
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

function TranslationInput({
  locale,
  entry,
  all,
  subject,
  compact = false,
}: {
  locale: string;
  entry: TranslationEntry;
  all: TranslationEntry[];
  /** The sub-element the text belongs to (a field's name), named in the control's label. */
  subject?: string;
  /** Only the control (a table cell): its label is the column heading. */
  compact?: boolean;
}) {
  const qc = useQueryClient();
  const { store } = useServices();
  const [value, setValue] = useState(entry.translation ?? "");
  useEffect(() => setValue(entry.translation ?? ""), [entry.translation]);
  const label = subject ? `${FIELD_LABELS[entry.field]} of ${subject} (${locale})` : `${FIELD_LABELS[entry.field]} (${locale})`;
  const inputId = `tr-${locale}-${entry.id}-${entry.field}`;
  const write = async (confirm = false) => {
    if (!confirm && value === (entry.translation ?? "")) return;
    const result = await writeTranslations(qc, locale, [{ id: entry.id, field: entry.field, value: value || null, confirm }], all);
    if (!result.ok) store.getState().notify(result.message, "error");
  };
  const placeholder = entry.effective ?? entry.source ?? "";
  const Control = entry.field === "description" ? Textarea : Input;
  const stale =
    entry.state === "stale" ? (
      <Badge tone="warning" className="ml-1" title="The default text changed since this was translated">
        stale
      </Badge>
    ) : null;
  if (compact)
    return (
      <div className="flex items-start gap-1">
        <Control
          id={inputId}
          aria-label={label}
          value={value}
          placeholder={placeholder}
          className={cn("flex-1 text-12 placeholder:italic placeholder:text-secondary", entry.field === "description" ? "min-h-0 py-0.5" : "h-6")}
          onChange={(e: { target: { value: string } }) => setValue(e.target.value)}
          onBlur={() => void write()}
          onKeyDown={(e: React.KeyboardEvent) => {
            if (e.key === "Enter" && entry.field !== "description") void write();
          }}
          {...(entry.field === "description" ? { rows: 1 } : {})}
        />
        {stale}
        {entry.state === "stale" ? (
          <Button size="sm" variant="secondary" onClick={() => void write(true)} aria-label={`Confirm ${label}`}>
            Confirm
          </Button>
        ) : null}
      </div>
    );
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

const FIELDS_OPEN_KEY = "maquettiste.fieldTranslationsOpen";

/**
 * The translations of an element's fields (RT 3.1: every attribute and a reference type's code and label have a
 * localizable display name and description), collapsed by default and remembered per user: per translated locale, one
 * line per field with its display name and description, the default text as the placeholder. Nothing renders while
 * the project declares fewer than two locales.
 */
export function FieldTranslations({ owner, fields }: { owner: string; fields: readonly { id: string; name: string }[] }) {
  const l10n = useLocalization();
  const [open, setOpen] = useState(() => local.get(FIELDS_OPEN_KEY) === "1");
  if (!l10n.enabled || !fields.length) return null;
  const toggle = () => {
    local.set(FIELDS_OPEN_KEY, open ? "0" : "1");
    setOpen(!open);
  };
  return (
    <section className="flex flex-col gap-2" aria-label="Translations of the fields" data-testid="field-translations">
      <button type="button" className="flex items-center gap-1 self-start text-12 font-medium text-secondary" aria-expanded={open} onClick={toggle}>
        {open ? <ChevronDown className="size-3.5" aria-hidden /> : <ChevronRight className="size-3.5" aria-hidden />}
        Translations of the fields
        <span className="font-normal">({l10n.locales.join(", ")})</span>
      </button>
      {open ? <FieldTranslationTables owner={owner} fields={fields} locales={l10n.locales} /> : null}
    </section>
  );
}

function FieldTranslationTables({ owner, fields, locales }: { owner: string; fields: readonly { id: string; name: string }[]; locales: string[] }) {
  const results = useOwnerTranslations(owner, locales);
  return (
    <div className="flex flex-col gap-2">
      {locales.map((locale, i) => {
        const entries = (results[i]?.data?.entries ?? []).filter((e) => fields.some((f) => f.id === e.id));
        return (
          <table
            key={locale}
            className="w-full border-collapse rounded-control border border-default text-12"
            aria-label={`Fields in ${localeName(locale)}`}
            data-testid={`field-translations-${locale}`}
          >
            <thead>
              <tr className="bg-app text-left text-11 font-semibold text-secondary">
                <th className="h-7 w-40 px-1.5">{localeName(locale)}</th>
                <th className="px-1.5">Display name</th>
                <th className="px-1.5">Description</th>
              </tr>
            </thead>
            <tbody>
              {results[i]?.isPending ? (
                <tr>
                  <td colSpan={3} className="px-1.5 text-secondary">
                    Loading…
                  </td>
                </tr>
              ) : null}
              {fields.map((f) => {
                const own = entries.filter((e) => e.id === f.id);
                const cell = (field: TranslationField) => {
                  const entry = own.find((e) => e.field === field);
                  return entry ? (
                    <TranslationInput locale={locale} entry={entry} all={entries} subject={f.name} compact />
                  ) : (
                    <span className="text-11 text-secondary">no {FIELD_LABELS[field].toLowerCase()} to translate</span>
                  );
                };
                return (
                  <tr key={f.id} className="border-t border-default align-top">
                    <th scope="row" className="h-[var(--mq-row-h)] px-1.5 py-0.5 text-left font-mono font-normal">
                      {f.name}
                    </th>
                    <td className="px-1.5 py-0.5">{cell("displayName")}</td>
                    <td className="px-1.5 py-0.5">{cell("description")}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        );
      })}
    </div>
  );
}
