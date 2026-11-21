// Custom properties rendered from the extension schemas that apply to the element (by kind and
// stereotype): a small in-house JSON-Schema form (phase2-design.md 4.8). It draws string, number, integer,
// boolean, enum, arrays of those, and objects with declared properties (a nested group, one field per
// sub-property, recursively). Any other shape (an object without declared properties, an array of objects,
// oneOf/anyOf, mixed type lists, unknown types) and any stored value whose runtime type does not match its
// schema is shown as a JSON text box: the text is passed on only while it parses, so a half-typed value never
// reaches the server and nothing is ever shown through String() as "[object Object]".
import { useEffect, useRef, useState } from "react";
import type { ExtensionSchema, ModelJson } from "@/api/types";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";

type Schema = {
  type?: string | string[];
  enum?: unknown[];
  items?: Schema;
  properties?: Record<string, Schema>;
  required?: string[];
  oneOf?: unknown;
  anyOf?: unknown;
  allOf?: unknown;
  minimum?: number;
  maximum?: number;
  description?: string;
};

type Draw = { onChange: (value: unknown) => void; placeholder: string; hint?: string };

const isPlain = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const isScalar = (v: unknown) => typeof v === "string" || typeof v === "number" || typeof v === "boolean";

/** The one JSON type a schema draws as, or null when the form cannot decide (type lists that mix kinds, combinators, unknown types). */
function typeOf(schema: Schema): string | null {
  if (schema.oneOf || schema.anyOf || schema.allOf) return null;
  const types = (Array.isArray(schema.type) ? schema.type : schema.type ? [schema.type] : []).filter((t) => t !== "null");
  if (types.length > 1) return null;
  if (!types.length) {
    if (isPlain(schema.properties)) return "object";
    if (schema.items) return "array";
  }
  return types[0] ?? "string";
}

export function applicableExtensions(extensions: ExtensionSchema[], kind: string, stereotypes: string[]): ExtensionSchema[] {
  return extensions.filter(
    (e) =>
      (e.appliesTo.kinds.length === 0 || e.appliesTo.kinds.includes(kind)) &&
      (e.appliesTo.stereotypes.length === 0 || e.appliesTo.stereotypes.some((s) => stereotypes.includes(s))),
  );
}

/** A multi-line JSON box: valid JSON calls onChange with the parsed value, empty text clears it, invalid text only shows an error. */
function JsonBox({
  id,
  name,
  value,
  onChange,
  hint,
  onEditing,
}: {
  id: string;
  name: string;
  value: unknown;
  onChange: (value: unknown) => void;
  hint?: string;
  onEditing?: (editing: boolean) => void;
}) {
  const format = (v: unknown) => (v === undefined ? "" : JSON.stringify(v, null, 2));
  const [text, setText] = useState(() => format(value));
  const [error, setError] = useState<string | null>(null);
  const emitted = useRef(JSON.stringify(value));
  // A value that changed elsewhere (undo, the JSON tab) replaces the text; a value this box sent itself does not.
  useEffect(() => {
    if (JSON.stringify(value) === emitted.current) return;
    emitted.current = JSON.stringify(value);
    setText(format(value));
    setError(null);
  }, [value]);
  return (
    <Field label={name} htmlFor={id} hint={error ? undefined : (hint ?? "JSON")}>
      <Textarea
        id={id}
        className="font-mono text-12"
        rows={Math.min(12, Math.max(3, text.split("\n").length))}
        spellCheck={false}
        aria-invalid={error ? true : undefined}
        value={text}
        onFocus={() => onEditing?.(true)}
        onBlur={() => onEditing?.(false)}
        onChange={(e) => {
          const raw = e.target.value;
          setText(raw);
          if (raw.trim() === "") {
            setError(null);
            emitted.current = JSON.stringify(undefined);
            onChange(undefined);
            return;
          }
          try {
            const parsed: unknown = JSON.parse(raw);
            setError(null);
            emitted.current = JSON.stringify(parsed);
            onChange(parsed);
          } catch (err) {
            setError(`Not valid JSON yet: ${(err as Error).message}`);
          }
        }}
      />
      {error ? (
        <p role="alert" className="text-11 text-danger">
          {error}
        </p>
      ) : null}
    </Field>
  );
}

/** A comma-separated list of scalars; a part that is not a valid item is flagged and nothing is passed on until every part is. */
function ScalarList({
  id,
  label,
  hint,
  value,
  placeholder,
  parse,
  onChange,
}: {
  id: string;
  label: string;
  hint: string;
  value: unknown;
  placeholder: string;
  parse: (part: string) => { ok: true; value: unknown } | { ok: false; message: string };
  onChange: (value: unknown) => void;
}) {
  const format = (v: unknown) => (Array.isArray(v) ? v.join(", ") : "");
  const [text, setText] = useState(() => format(value));
  const [error, setError] = useState<string | null>(null);
  const emitted = useRef(JSON.stringify(value));
  // A value changed elsewhere (undo, the JSON tab) replaces the text; one this field sent itself does not.
  useEffect(() => {
    if (JSON.stringify(value) === emitted.current) return;
    emitted.current = JSON.stringify(value);
    setText(format(value));
    setError(null);
  }, [value]);
  return (
    <Field label={label} htmlFor={id} hint={error ? undefined : hint}>
      <Input
        id={id}
        value={text}
        placeholder={placeholder}
        aria-invalid={error ? true : undefined}
        onChange={(e) => {
          const raw = e.target.value;
          setText(raw);
          const parts = raw
            .split(",")
            .map((s) => s.trim())
            .filter(Boolean);
          const items: unknown[] = [];
          for (const part of parts) {
            const parsed = parse(part);
            if (!parsed.ok) {
              setError(parsed.message);
              return;
            }
            items.push(parsed.value);
          }
          setError(null);
          const next = items.length ? items : undefined;
          emitted.current = JSON.stringify(next);
          onChange(next);
        }}
      />
      {error ? (
        <p role="alert" className="text-11 text-danger">
          {error}
        </p>
      ) : null}
    </Field>
  );
}

/**
 * One property, drawn by its schema. While the JSON box of a mismatched value has focus it stays up, even once the text
 * parses to a value the typed field could draw: the field form is decided again on blur, not on each keystroke.
 */
function PropertyField(props: PropertyFieldProps) {
  const [editingJson, setEditingJson] = useState(false);
  return <TypedField {...props} editingJson={editingJson} onEditingJson={setEditingJson} />;
}

type PropertyFieldProps = {
  id: string;
  name: string;
  schema: Schema;
  value: unknown;
  required?: boolean;
  draw: Draw;
};

/** One property, drawn by its schema; recursive for objects with declared properties. */
function TypedField({
  id,
  name,
  schema,
  value,
  required,
  draw,
  editingJson,
  onEditingJson,
}: PropertyFieldProps & { editingJson: boolean; onEditingJson: (editing: boolean) => void }) {
  const { onChange, placeholder, hint } = draw;
  const label = required ? `${name} *` : name;
  const json = <JsonBox key={id} id={id} name={label} value={value} onChange={onChange} hint={hint} onEditing={onEditingJson} />;
  if (editingJson) return json;
  const type = typeOf(schema);
  if (Array.isArray(schema.enum)) {
    if (value !== undefined && !isScalar(value)) return json;
    return (
      <Field key={id} label={label} htmlFor={id} hint={hint}>
        <Select
          id={id}
          value={value === undefined ? "" : String(value)}
          onChange={(e) => onChange(e.target.value === "" ? undefined : schema.enum!.find((x) => String(x) === e.target.value))}
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
  }
  if (type === "boolean") {
    if (value !== undefined && typeof value !== "boolean") return json;
    return <CheckboxField key={id} id={id} label={label} checked={value === true} onChange={(v) => onChange(v ? true : undefined)} />;
  }
  if (type === "array") {
    const itemType = schema.items ? typeOf(schema.items) : "string";
    const members = Array.isArray(schema.items?.enum) ? schema.items.enum : null;
    const scalarItems = members !== null || itemType === "string" || itemType === "number" || itemType === "integer" || itemType === "boolean";
    if (!scalarItems || (value !== undefined && !(Array.isArray(value) && value.every(isScalar)))) return json;
    const parse = (part: string): { ok: true; value: unknown } | { ok: false; message: string } => {
      if (members) {
        const member = members.find((m) => String(m) === part);
        return member === undefined ? { ok: false, message: `"${part}" is not one of: ${members.map(String).join(", ")}` } : { ok: true, value: member };
      }
      if (itemType === "boolean") {
        const lower = part.toLowerCase();
        return lower === "true" || lower === "false" ? { ok: true, value: lower === "true" } : { ok: false, message: `"${part}" is not true or false` };
      }
      return { ok: true, value: itemType === "integer" || itemType === "number" ? Number(part) : part };
    };
    return (
      <ScalarList
        key={id}
        id={id}
        label={label}
        hint={
          members
            ? `Comma-separated; allowed: ${members.map(String).join(", ")}`
            : itemType === "boolean"
              ? "Comma-separated true or false"
              : "Comma-separated values"
        }
        value={value}
        placeholder={placeholder}
        parse={parse}
        onChange={onChange}
      />
    );
  }
  if (type === "object") {
    const declared = isPlain(schema.properties) ? Object.entries(schema.properties) : [];
    if (!declared.length || (value !== undefined && !isPlain(value))) return json;
    const current = isPlain(value) ? value : {};
    return (
      <fieldset key={id} className="flex flex-col gap-2 rounded-control border border-default p-2" data-testid={`custom-object-${name}`}>
        <legend className="px-1 text-12 font-medium text-secondary">{label}</legend>
        {hint ? <p className="text-11 text-secondary">{hint}</p> : null}
        {declared.map(([sub, subSchema]) => (
          <PropertyField
            key={sub}
            id={`${id}-${sub}`}
            name={sub}
            schema={subSchema ?? {}}
            value={current[sub]}
            required={schema.required?.includes(sub)}
            draw={{
              placeholder: "",
              hint: subSchema?.description,
              onChange: (v) => {
                const next = { ...current };
                if (v === undefined) delete next[sub];
                else next[sub] = v;
                onChange(Object.keys(next).length ? next : undefined);
              },
            }}
          />
        ))}
      </fieldset>
    );
  }
  const numeric = type === "integer" || type === "number";
  if ((type !== "string" && !numeric) || (value !== undefined && !isScalar(value))) return json;
  return (
    <Field key={id} label={label} htmlFor={id} hint={hint}>
      <Input
        id={id}
        inputMode={numeric ? "numeric" : undefined}
        value={value === undefined ? "" : String(value)}
        placeholder={placeholder}
        onChange={(e) => {
          const raw = e.target.value;
          if (raw === "") onChange(undefined);
          else if (numeric) {
            const n = Number(raw);
            onChange(Number.isFinite(n) ? n : raw);
          } else onChange(raw);
        }}
      />
    </Field>
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
        const fallback = defaults[name];
        return (
          <PropertyField
            key={`${ext.name}-${name}`}
            id={`${idPrefix}-${ext.name}-${name}`}
            name={name}
            schema={schema}
            value={properties[name]}
            draw={{
              onChange: (v) => onChange(name, v),
              placeholder: fallback ? `${JSON.stringify(fallback.value)} (from «${fallback.from}»)` : "",
              hint: ext.description ?? undefined,
            }}
          />
        );
      })}
    </div>
  );
}
