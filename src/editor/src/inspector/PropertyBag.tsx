// The property bag every element (and every table column) shows under its declared custom properties: free keys with text
// values by default, a number or true/false when the row's type says so. A row commits on blur or Enter (a type or a
// true/false pick at once), a removal at once, a new row once it has a key; each commit is one `onEdit`, which the owner
// turns into one save and one undo step. A key must be present, unique, and not one an extension schema declares (those keep
// their typed form above); a bad key or value is said under its row and nothing is saved until it is fixed.
import { useState } from "react";
import { Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Select } from "@/components/ui/input";
import { SectionTitle } from "@/components/ui/misc";
import { jsonEqual } from "@/lib/json";
import type { ModelJson } from "@/api/types";
import {
  convertPropertyValue,
  editProperties,
  parsePropertyValue,
  propertyKeyProblem,
  propertyRows,
  propertyText,
  propertyTypeOf,
  PROPERTY_TYPE_LABELS,
  type PropertyEdit,
  type PropertyRow,
  type PropertyType,
} from "./propertyBag";

/** What a bag holds when it is not an element's free properties (a table's storage parameters, a routine's settings). */
export interface BagOptions {
  /** The row's noun: "Add parameter", "Remove parameter". */
  noun?: string;
  /** What the bag says when it is empty. */
  empty?: string;
  /** The value types a row may take (all but JSON by default). */
  types?: readonly PropertyType[];
  /** A problem with a key beyond presence and uniqueness (its form), or null. */
  keyProblem?: (key: string) => string | null;
  /** Names offered as the key is typed. */
  suggestions?: readonly string[];
  /** A new row's type follows what its value reads as: a number, true or false, else text. */
  infer?: boolean;
}

/** The type a value's text reads as: a number, true or false, else text. */
export function inferredType(text: string): PropertyType {
  const t = text.trim();
  if (/^-?\d+(\.\d+)?$/.test(t)) return "number";
  if (t === "true" || t === "false") return "boolean";
  return "text";
}

export function PropertyBag({
  idPrefix,
  properties,
  declared = [],
  onEdit,
  title = "Properties",
  options = {},
}: {
  /** Distinguishes the bag's field ids (an element id, a column's dom id). */
  idPrefix: string;
  properties: unknown;
  /** The keys an applicable extension schema declares: shown typed elsewhere, never as rows here. */
  declared?: readonly string[];
  onEdit: (edit: PropertyEdit) => void;
  title?: string;
  options?: BagOptions;
}) {
  const rows = propertyRows(properties, declared);
  const [adding, setAdding] = useState(false);
  // The key the new row committed, until the rows list it (at once through a draft, later through a queued file write).
  const [landing, setLanding] = useState<string | null>(null);
  if (landing !== null && rows.some((r) => r.key === landing)) {
    setLanding(null);
    setAdding(false);
  }
  const showNew = adding && !(landing !== null && rows.some((r) => r.key === landing));
  const dom = idPrefix.replace(/[^A-Za-z0-9_-]/g, "_");
  const keys = rows.map((r) => r.key);
  const noun = options.noun ?? "property";
  return (
    <div className="flex flex-col gap-1" data-testid="property-bag">
      {title ? <SectionTitle>{title}</SectionTitle> : null}
      {!rows.length && !showNew ? (
        <p className="text-12 text-secondary">{options.empty ?? "No free properties: Add property sets a key and a text value."}</p>
      ) : null}
      {options.suggestions?.length ? (
        <datalist id={`${dom}-suggestions`}>
          {options.suggestions.map((s) => (
            <option key={s} value={s} />
          ))}
        </datalist>
      ) : null}
      {/* One list, the new row last: once its key is saved it is the same row (and keeps its focus and its typed value). */}
      {[
        ...rows.map((row, i) => (
          <BagRow
            key={i}
            dom={`${dom}-bag-${i}`}
            row={row}
            others={keys.filter((k) => k !== row.key)}
            declared={declared}
            options={options}
            list={options.suggestions?.length ? `${dom}-suggestions` : undefined}
            onEdit={onEdit}
            onRemove={() => onEdit({ op: "remove", key: row.key })}
          />
        )),
        showNew ? (
          <BagRow
            key={rows.length}
            dom={`${dom}-bag-${rows.length}`}
            row={null}
            others={keys}
            declared={declared}
            options={options}
            list={options.suggestions?.length ? `${dom}-suggestions` : undefined}
            onEdit={(edit) => {
              if (edit.op === "set") setLanding(edit.key);
              onEdit(edit);
            }}
            onRemove={() => {
              setAdding(false);
              setLanding(null);
            }}
          />
        ) : null,
      ]}
      <div>
        <Button
          size="sm"
          data-testid="property-bag-add"
          onClick={() => {
            if (adding) document.getElementById(`${dom}-bag-${rows.length}-key`)?.focus();
            else setAdding(true);
          }}
        >
          <Plus /> Add {noun}
        </Button>
      </div>
    </div>
  );
}

/** What a row last showed from its owner or committed to it: a change from outside resets the row's local text. */
const signature = (key: string, value: unknown) => JSON.stringify([key, value === undefined ? null : value]);

function BagRow({
  dom,
  row,
  others,
  declared,
  options,
  list,
  onEdit,
  onRemove,
}: {
  dom: string;
  /** null: the new row, not saved until it has a key. */
  row: PropertyRow | null;
  others: readonly string[];
  declared: readonly string[];
  options: BagOptions;
  /** The datalist of key suggestions, if any. */
  list?: string;
  onEdit: (edit: PropertyEdit) => void;
  onRemove: () => void;
}) {
  const [keyText, setKeyText] = useState(row?.key ?? "");
  const [valueText, setValueText] = useState(row?.text ?? "");
  const [type, setType] = useState<PropertyType>(row?.type ?? "text");
  // Whether the type was picked by hand: until then a bag that infers reads each value as what it looks like.
  const [typeChosen, setTypeChosen] = useState(false);
  const [error, setError] = useState<{ on: "key" | "value"; message: string } | null>(null);
  // The key and value the row shows as saved: its owner's, or the last it committed (a queued write lands later).
  const [seen, setSeen] = useState(row ? signature(row.key, row.value) : null);
  const [saved, setSaved] = useState<{ key: string; value: unknown } | null>(row ? { key: row.key, value: row.value } : null);
  if (row && signature(row.key, row.value) !== seen) {
    setSeen(signature(row.key, row.value));
    setSaved({ key: row.key, value: row.value });
    setKeyText(row.key);
    setValueText(row.text);
    setType(row.type);
    setError(null);
  }

  const commit = (key: string, value: unknown) => {
    if (saved && saved.key === key && jsonEqual(saved.value, value)) return;
    setSaved({ key, value });
    setSeen(signature(key, value));
    setError(null);
    onEdit({ op: "set", key, value, ...(saved && saved.key !== key ? { from: saved.key } : {}) });
  };

  /** The value as typed under the row's type, or the problem said under the row. */
  const parsedValue = (text = valueText, as = type): { ok: true; value: unknown } | { ok: false } => {
    const parsed = parsePropertyValue(text, as);
    if (parsed.error !== undefined) {
      setError({ on: "value", message: parsed.error });
      return { ok: false };
    }
    return { ok: true, value: parsed.value };
  };

  const commitKey = () => {
    const key = keyText.trim();
    if (saved && key === saved.key) {
      if (error?.on === "key") setError(null);
      if (keyText !== key) setKeyText(key);
      return;
    }
    const problem = propertyKeyProblem(key, others, declared) ?? options.keyProblem?.(key) ?? null;
    if (problem) {
      setError({ on: "key", message: problem });
      return;
    }
    // A rename keeps the saved value; a new row saves what its value says (read as its inferred type when the bag infers).
    if (saved) commit(key, saved.value);
    else {
      const as = options.infer && !typeChosen ? inferredType(valueText) : type;
      if (as !== type) setType(as);
      const value = parsedValue(valueText, as);
      if (value.ok) commit(key, value.value);
    }
    setKeyText(key);
  };

  const commitValue = (text = valueText, as = type) => {
    if (!saved) {
      // The new row: its value waits for a key.
      if (keyText.trim() && !propertyKeyProblem(keyText, others, declared) && !options.keyProblem?.(keyText.trim())) commitKey();
      return;
    }
    const read = options.infer && !typeChosen && as === type ? inferredType(text) : as;
    if (read !== type) setType(read);
    const value = parsedValue(text, read);
    if (value.ok) commit(saved.key, value.value);
  };

  const changeType = (next: PropertyType) => {
    setType(next);
    setTypeChosen(true);
    // The value's text read under the new type: a number must read as one; true/false takes "true", any other text is false.
    const converted = next === "boolean" ? convertPropertyValue(valueText, next) : parsePropertyValue(valueText, next);
    if (converted.error !== undefined) {
      setError({ on: "value", message: converted.error });
      return;
    }
    const text = propertyText(converted.value);
    setValueText(text);
    setError(null);
    if (saved) commit(saved.key, converted.value);
  };

  const noun = options.noun ?? "property";
  const label = row?.key || `the new ${noun}`;
  const types: PropertyType[] = [
    ...(options.types ?? (["text", "number", "boolean"] as const)),
    ...(type === "json" || (saved && propertyTypeOf(saved.value) === "json") ? (["json"] as const) : []),
  ];
  return (
    <div className="flex flex-col gap-0.5" data-testid={`property-row-${row?.key ?? "new"}`}>
      <div className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto_auto] items-center gap-1">
        <Input
          id={`${dom}-key`}
          aria-label={`Key of ${label}`}
          className="h-6 font-mono text-12"
          placeholder="key"
          list={list}
          autoFocus={!row}
          value={keyText}
          aria-invalid={error?.on === "key" || undefined}
          onChange={(e) => setKeyText(e.target.value)}
          onBlur={commitKey}
          onKeyDown={(e) => {
            if (e.key === "Enter") commitKey();
            else if (e.key === "Escape") {
              setKeyText(saved?.key ?? "");
              setError(null);
            }
          }}
        />
        {type === "boolean" ? (
          <Select
            id={`${dom}-value`}
            aria-label={`Value of ${label}`}
            className="h-6 text-12"
            value={valueText === "true" ? "true" : "false"}
            onChange={(e) => {
              setValueText(e.target.value);
              commitValue(e.target.value, "boolean");
            }}
          >
            <option value="true">true</option>
            <option value="false">false</option>
          </Select>
        ) : (
          <Input
            id={`${dom}-value`}
            aria-label={`Value of ${label}`}
            className={type === "text" ? "h-6 text-12" : "h-6 font-mono text-12"}
            placeholder="value"
            inputMode={type === "number" ? "decimal" : undefined}
            value={valueText}
            aria-invalid={error?.on === "value" || undefined}
            onChange={(e) => setValueText(e.target.value)}
            onBlur={() => commitValue()}
            onKeyDown={(e) => {
              if (e.key === "Enter") commitValue();
              else if (e.key === "Escape") {
                setValueText(saved ? propertyText(saved.value) : "");
                setError(null);
              }
            }}
          />
        )}
        {types.length > 1 ? (
          <Select
            id={`${dom}-type`}
            aria-label={`Type of ${label}`}
            title="The value's type: text by default"
            className="h-6 w-auto text-12"
            value={type}
            onChange={(e) => changeType(e.target.value as PropertyType)}
          >
            {types.map((t) => (
              <option key={t} value={t}>
                {PROPERTY_TYPE_LABELS[t]}
              </option>
            ))}
          </Select>
        ) : (
          <span />
        )}
        <Button size="icon-row" variant="ghost" label={`Remove ${noun}`} onClick={onRemove}>
          <X />
        </Button>
      </div>
      {error ? (
        <p role="alert" className="text-11 text-danger">
          {error.message}
        </p>
      ) : null}
    </div>
  );
}

/** The bag of an element's own document: each commit is one draft edit saved at once (one undo step). */
export function ElementPropertyBag({
  id,
  json,
  edit,
  flush,
  declared,
}: {
  id: string;
  json: unknown;
  edit: (update: (json: ModelJson) => ModelJson | void) => void;
  flush: () => void;
  declared?: readonly string[];
}) {
  return (
    <PropertyBag
      idPrefix={id}
      properties={(json as { properties?: unknown } | undefined)?.properties}
      declared={declared}
      onEdit={(change) => {
        edit((j) => editProperties(j as unknown as Record<string, unknown>, change));
        flush();
      }}
    />
  );
}
