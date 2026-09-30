// Display name, Plural name and Description of an element's header (the editor header and the inspector's common
// fields). With the default content locale they edit the element; with another one in effect they edit that locale's
// translation (written to its shard), the default text shown as the placeholder (step 8 of section 5).
import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import type { TranslationEntry } from "@/api/types";
import { useServices } from "@/app/context";
import { Field, Input, Textarea } from "@/components/ui/input";
import { useContentLocale } from "./contentLocale";
import { headerEdit, headerEntry, type HeaderField } from "./header";
import { useOwnerTranslations, writeTranslations } from "./queries";

type Rec = Record<string, unknown>;

export interface HeaderTextFieldsProps {
  id: string;
  dom: string;
  rec: Rec;
  edit(change: (json: Rec) => void): void;
  flush(): void;
  /** The sidecar description text when the description lives in a file (shown, not edited). */
  sidecar?: { file: string; text: string } | null;
  rows?: number;
}

const LABELS: Record<HeaderField, string> = { displayName: "Display name", pluralName: "Plural name", description: "Description" };

export function HeaderTextFields({ id, dom, rec, edit, flush, sidecar, rows = 2 }: HeaderTextFieldsProps) {
  const { effective } = useContentLocale();
  const results = useOwnerTranslations(id, effective ? [effective] : []);
  const entries = effective ? (results[0]?.data?.entries ?? []) : [];
  const field = (name: HeaderField, multiline: boolean) => {
    const entry = headerEntry(effective, entries, id, name);
    const fallback = typeof rec[name] === "string" ? (rec[name] as string) : "";
    if (entry && effective)
      return (
        <LocaleField
          key={`${name}-${effective}`}
          dom={dom}
          name={name}
          locale={effective}
          entry={entry}
          all={entries}
          fallback={fallback}
          multiline={multiline}
          rows={rows}
        />
      );
    const inputId = `${dom}-${name === "displayName" ? "display" : name === "pluralName" ? "plural" : "description"}`;
    const set = (v: string) =>
      edit((j) => {
        if (v === "") delete j[name];
        else j[name] = v;
      });
    return (
      <Field label={LABELS[name]} htmlFor={inputId}>
        {multiline ? (
          <Textarea id={inputId} value={fallback} onChange={(e) => set(e.target.value)} onBlur={flush} rows={rows} />
        ) : (
          <Input id={inputId} value={fallback} onChange={(e) => set(e.target.value)} onBlur={flush} />
        )}
      </Field>
    );
  };
  return (
    <>
      <div className="grid grid-cols-2 gap-2">
        {field("displayName", false)}
        {field("pluralName", false)}
      </div>
      {sidecar ? (
        <Field label="Description" hint={`Kept in ${sidecar.file} next to the model file; edit that file on disk.`}>
          <pre className="max-h-32 overflow-auto whitespace-pre-wrap rounded-control border border-default bg-app p-2 text-12">{sidecar.text}</pre>
        </Field>
      ) : (
        field("description", true)
      )}
    </>
  );
}

function LocaleField(props: {
  dom: string;
  name: HeaderField;
  locale: string;
  entry: TranslationEntry;
  all: readonly TranslationEntry[];
  fallback: string;
  multiline: boolean;
  rows: number;
}) {
  const { dom, name, locale, entry, all, fallback, multiline, rows } = props;
  const qc = useQueryClient();
  const { store } = useServices();
  const [value, setValue] = useState(entry.translation ?? "");
  useEffect(() => setValue(entry.translation ?? ""), [entry.translation]);
  const inputId = `${dom}-${name}-${locale}`;
  const write = async () => {
    const change = headerEdit(entry, value);
    if (!change) return;
    const result = await writeTranslations(qc, locale, [change], all);
    if (!result.ok) store.getState().notify(result.message, "error");
  };
  const common = {
    id: inputId,
    value,
    placeholder: fallback,
    "data-locale": locale,
    onBlur: () => void write(),
  };
  return (
    <Field label={`${LABELS[name]} (${locale})`} htmlFor={inputId}>
      {multiline ? (
        <Textarea {...common} rows={rows} onChange={(e) => setValue(e.target.value)} />
      ) : (
        <Input {...common} onChange={(e) => setValue(e.target.value)} onKeyDown={(e) => e.key === "Enter" && void write()} />
      )}
    </Field>
  );
}
