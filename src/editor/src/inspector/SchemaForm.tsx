// Custom properties rendered from the extension schemas that apply to the element (by kind and
// stereotype): a small in-house JSON-Schema form for string, number, integer, boolean, enum and
// arrays of those (phase2-design.md 4.8).
import type { ExtensionSchema, ModelJson } from "@/api/types";
import { Field, Input, Select } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";

type Schema = { type?: string | string[]; enum?: unknown[]; items?: Schema; minimum?: number; maximum?: number; description?: string };

export function applicableExtensions(extensions: ExtensionSchema[], kind: string, stereotypes: string[]): ExtensionSchema[] {
  return extensions.filter(
    (e) =>
      (e.appliesTo.kinds.length === 0 || e.appliesTo.kinds.includes(kind)) &&
      (e.appliesTo.stereotypes.length === 0 || e.appliesTo.stereotypes.some((s) => stereotypes.includes(s))),
  );
}

export function SchemaForm({
  extensions,
  json,
  defaults,
  onChange,
  idPrefix = "prop",
}: {
  extensions: ExtensionSchema[];
  json: ModelJson;
  /** Effective defaults (stereotype defaultProperties), shown as placeholders. */
  defaults: Record<string, { value: unknown; from: string }>;
  onChange: (name: string, value: unknown) => void;
  /** Distinguishes the form's field ids when the inspector and an editor show the same element. */
  idPrefix?: string;
}) {
  const properties = ((json as { properties?: Record<string, unknown> }).properties ?? {}) as Record<string, unknown>;
  const fields = extensions.flatMap((ext) => Object.entries(ext.properties as Record<string, Schema>).map(([name, schema]) => ({ ext, name, schema })));
  if (!fields.length) return null;
  return (
    <div className="flex flex-col gap-2" data-testid="custom-properties">
      {fields.map(({ ext, name, schema }) => {
        const id = `${idPrefix}-${ext.name}-${name}`;
        const value = properties[name];
        const fallback = defaults[name];
        const placeholder = fallback ? `${JSON.stringify(fallback.value)} (from «${fallback.from}»)` : "";
        const type = Array.isArray(schema.type) ? schema.type[0] : schema.type;
        if (type === "boolean")
          return <CheckboxField key={id} id={id} label={name} checked={value === true} onChange={(v) => onChange(name, v ? true : undefined)} />;
        if (Array.isArray(schema.enum))
          return (
            <Field key={id} label={name} htmlFor={id} hint={ext.description ?? undefined}>
              <Select
                id={id}
                value={value === undefined ? "" : String(value)}
                onChange={(e) => onChange(name, e.target.value === "" ? undefined : schema.enum!.find((x) => String(x) === e.target.value))}
              >
                <option value="">{placeholder || "(not set)"}</option>
                {schema.enum.map((v) => (
                  <option key={String(v)} value={String(v)}>
                    {String(v)}
                  </option>
                ))}
              </Select>
            </Field>
          );
        if (type === "array")
          return (
            <Field key={id} label={name} htmlFor={id} hint="Comma-separated values">
              <Input
                id={id}
                value={Array.isArray(value) ? value.join(", ") : ""}
                placeholder={placeholder}
                onChange={(e) => {
                  const parts = e.target.value
                    .split(",")
                    .map((s) => s.trim())
                    .filter(Boolean);
                  const itemType = schema.items?.type;
                  onChange(name, parts.length ? parts.map((p) => (itemType === "integer" || itemType === "number" ? Number(p) : p)) : undefined);
                }}
              />
            </Field>
          );
        const numeric = type === "integer" || type === "number";
        return (
          <Field key={id} label={name} htmlFor={id} hint={ext.description ?? undefined}>
            <Input
              id={id}
              inputMode={numeric ? "numeric" : undefined}
              value={value === undefined ? "" : String(value)}
              placeholder={placeholder}
              onChange={(e) => {
                const raw = e.target.value;
                if (raw === "") onChange(name, undefined);
                else if (numeric) {
                  const n = Number(raw);
                  onChange(name, Number.isFinite(n) ? n : raw);
                } else onChange(name, raw);
              }}
            />
          </Field>
        );
      })}
    </div>
  );
}
