// The Fields tab (reference-types-seeds-localization.md 4.3): the built-in code, label and description pinned at the top
// (their names fixed; code's type, length and pattern and label's length editable; the description is text of any
// length; label and description are marked as translated per locale), then the user fields in the entity
// attribute grid, whose type picker offers built-ins, custom types, enums and reference types (not value objects or
// entities, so a row stays one flat line, RS2).
import { useIndex } from "@/api/queries";
import { useBatchEdit } from "@/explorer/marks";
import { clone } from "@/lib/json";
import { dropSeedColumns, removedFieldIds } from "./rowsModel";
import { Languages, Lock } from "lucide-react";
import type { ModelJson, ReferenceTypeDoc } from "@/api/types";
import { AttributeGrid } from "@/inspector/AttributeGrid";
import { useDefinition } from "@/inspector/definition";
import { useVocabularies } from "@/inspector/fields";
import { useDraftDocument } from "@/inspector/useDraft";
import { Input, Select } from "@/components/ui/input";
import { SectionTitle, Spinner } from "@/components/ui/misc";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";

const CODE_TYPES = ["string", "int16", "int32", "int64", "uuid"] as const;
const FIELD_KINDS = ["scalar-type", "enum", "reference-type"] as const;

type Builtin = "code" | "label";

export function FieldsTab({ typeId }: { typeId: string }) {
  const { store } = useServices();
  const { json, edit, flush } = useDraftDocument(typeId);
  const vocab = useVocabularies("reference-type");
  const definition = useDefinition();
  const diagnostics = useEditor(store, (s) => s.drafts[typeId]?.diagnostics) ?? [];
  const index = useIndex();
  const batch = useBatchEdit();
  if (!json) return <Spinner />;
  const type = json as unknown as ReferenceTypeDoc;
  const typeOptions = FIELD_KINDS.flatMap((k) => vocab.lookup.ofKind(k));

  const setBuiltin = (field: Builtin, key: "type" | "length" | "pattern", raw: string) =>
    edit((j) => {
      const f = ((j as unknown as ReferenceTypeDoc)[field] ?? {}) as Record<string, unknown>;
      if (key === "length") {
        if (/^\d+$/.test(raw)) f.length = Number(raw);
        else delete f.length;
      } else if (raw === "" || (key === "type" && raw === "string")) delete f[key];
      else f[key] = raw;
      (j as Record<string, unknown>)[field] = f;
    });

  const cell = "h-[var(--mq-row-h)] px-1.5 align-middle";
  return (
    <div className="flex max-w-4xl flex-col gap-2" data-testid="reference-fields">
      <SectionTitle>Built-in fields</SectionTitle>
      <table className="w-full border-collapse rounded-control border border-default text-12" aria-label="Built-in fields">
        <thead>
          <tr className="bg-app text-left text-11 font-semibold text-secondary">
            <th className="h-7 px-1.5">Name</th>
            <th className="px-1.5">Type</th>
            <th className="w-20 px-1.5">Len</th>
            <th className="px-1.5">Pattern</th>
            <th className="w-24 px-1.5">Translated</th>
          </tr>
        </thead>
        <tbody>
          {(["code", "label", "description"] as const).map((field) => {
            if (field === "description")
              return (
                <tr key={field} className="border-t border-default" data-testid="builtin-description">
                  <th scope="row" className={`${cell} text-left font-normal`}>
                    <span className="inline-flex items-center gap-1 font-mono">
                      <Lock className="size-3 text-secondary" aria-label="built-in, name fixed" />
                      description
                    </span>
                  </th>
                  <td className={cell}>
                    <span className="font-mono">text</span>
                  </td>
                  <td className={`${cell} text-right font-mono text-secondary`} aria-label="Length of description">
                    any
                  </td>
                  <td className={`${cell} text-11 text-secondary`}>Optional, one per row; may span lines.</td>
                  <td className={cell}>
                    <Translated />
                  </td>
                </tr>
              );
            const f = (type[field] ?? {}) as { type?: string; length?: number; pattern?: string };
            return (
              <tr key={field} className="border-t border-default" data-testid={`builtin-${field}`}>
                <th scope="row" className={`${cell} text-left font-normal`}>
                  <span className="inline-flex items-center gap-1 font-mono">
                    <Lock className="size-3 text-secondary" aria-label="built-in, name fixed" />
                    {field}
                  </span>
                </th>
                <td className={cell}>
                  {field === "code" ? (
                    <Select
                      aria-label="Type of code"
                      className="h-6 font-mono text-12"
                      value={f.type ?? "string"}
                      onChange={(e) => (setBuiltin("code", "type", e.target.value), void flush())}
                    >
                      {CODE_TYPES.map((t) => (
                        <option key={t} value={t}>
                          {t}
                        </option>
                      ))}
                    </Select>
                  ) : (
                    <span className="font-mono">string</span>
                  )}
                </td>
                <td className={cell}>
                  <Input
                    aria-label={`Length of ${field}`}
                    className="h-6 text-right font-mono text-12"
                    value={f.length === undefined ? "" : String(f.length)}
                    onChange={(e) => setBuiltin(field, "length", e.target.value)}
                    onBlur={() => void flush()}
                  />
                </td>
                <td className={cell}>
                  {field === "code" ? (
                    <Input
                      aria-label="Pattern of code"
                      className="h-6 font-mono text-12"
                      value={f.pattern ?? ""}
                      placeholder="^[a-z0-9_]+$"
                      onChange={(e) => setBuiltin("code", "pattern", e.target.value)}
                      onBlur={() => void flush()}
                    />
                  ) : null}
                </td>
                <td className={cell}>{field === "label" ? <Translated /> : <span className="text-11 text-secondary">no</span>}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <SectionTitle>Fields</SectionTitle>
      <AttributeGrid
        label={`Fields of ${type.name}`}
        attributes={type.attributes ?? []}
        typeOptions={typeOptions}
        definition={definition}
        diagnostics={diagnostics}
        withKey={false}
        onChange={(update, commit) => {
          // A removed field drops its column from the type's seeds in the same save (§2.7): one batch, one undo step.
          const probe = clone(json) as ModelJson;
          update(probe);
          const removed = removedFieldIds(type, probe as unknown as ReferenceTypeDoc);
          const seeds = removed.size ? (index.data ?? []).filter((r) => r.kind === "seed" && r.target === typeId).map((r) => r.id) : [];
          if (commit && seeds.length) {
            void batch(removed.size === 1 ? "Remove field" : "Remove fields", [typeId, ...seeds], (j) => {
              if (j.id === typeId) update(j as unknown as ModelJson);
              else dropSeedColumns(j as unknown as Parameters<typeof dropSeedColumns>[0], removed);
            });
            return;
          }
          edit((j) => update(j as ModelJson));
          if (commit) void flush();
        }}
      />
    </div>
  );
}

/** The marker of a built-in field whose text is translated per locale (a row's label and description). */
function Translated() {
  return (
    <span className="inline-flex items-center gap-1 text-11 text-secondary" title="Translated per locale under Settings › Locales">
      <Languages className="size-3" aria-hidden />
      yes
    </span>
  );
}
