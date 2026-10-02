// The fields the table, view and sequence editors (and the inspector, for views and sequences) share: the schema picker, the
// comment, a sequence's definition and a view's summary. Every commit is one edit and one save, so one undo step.
import { useEffect, useRef, useState } from "react";
import { useElements } from "@/api/queries";
import { Field, Input, Select } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";
import { setOptional, type FormProps } from "@/inspector/fields";
import { defaultSchemaName, schemasOf } from "@/model/databaseSchemas";
import { SEQUENCE_TYPES } from "@/explorer/databaseCreate";
import { sequenceNumberProblem, setSequenceNumber, viewDialects, type SequenceNumber } from "./databaseDocs";

type Rec = Record<string, unknown>;

/** A text kept locally while typed and committed on blur or Enter (once); Escape restores the saved value. */
export function CommitInput({
  id,
  value,
  onCommit,
  mono,
  invalid,
  placeholder,
  label,
  className,
}: {
  id?: string;
  value: string;
  onCommit: (value: string) => void;
  mono?: boolean;
  invalid?: boolean;
  placeholder?: string;
  /** The accessible name when there is no visible label (a grid cell). */
  label?: string;
  className?: string;
}) {
  const [text, setText] = useState(value);
  const committed = useRef<string | null>(null);
  useEffect(() => {
    setText(value);
    committed.current = null;
  }, [value]);
  const commit = () => {
    if (text === value || text === committed.current) return;
    committed.current = text;
    onCommit(text);
  };
  return (
    <Input
      id={id}
      aria-label={label}
      value={text}
      placeholder={placeholder}
      aria-invalid={invalid || undefined}
      className={[mono ? "font-mono" : "", className ?? ""].join(" ").trim() || undefined}
      onChange={(e) => setText(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === "Enter") commit();
        else if (e.key === "Escape") setText(value);
      }}
    />
  );
}

/** The database a table, view or sequence belongs to, as a read-only line. */
export function DatabaseLine({ json }: { json: unknown }) {
  const database = String((json as Rec).database ?? "");
  const doc = useElements(database ? [database] : []).byId.get(database)?.json as Rec | undefined;
  return (
    <p className="text-12 text-secondary" data-testid="database-object-database">
      In database <span className="font-medium text-primary">{String(doc?.name ?? database)}</span>
    </p>
  );
}

/** The schema of a table, view or sequence: one of its database's schemas (none: the default). */
export function SchemaField({ id, json, edit, flush }: FormProps) {
  const rec = json as Rec;
  const database = String(rec.database ?? "");
  const db = useElements(database ? [database] : []).byId.get(database)?.json as Rec | undefined;
  const schemas = schemasOf(db);
  const defaultName = defaultSchemaName(db);
  const value = String(rec.schema ?? "");
  return (
    <Field label="Schema" htmlFor={`${id}-schema`}>
      <Select
        id={`${id}-schema`}
        value={value}
        onChange={(e) => {
          edit((j) => setOptional(j as Rec, "schema", e.target.value));
          flush();
        }}
      >
        <option value="">{defaultName ? `${defaultName} (default)` : "The default schema"}</option>
        {schemas
          .filter((s) => s.name !== defaultName || s.id === value)
          .map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
            </option>
          ))}
        {value && !schemas.some((s) => s.id === value) ? (
          <option value={value} disabled>
            {value} (not a schema of the database)
          </option>
        ) : null}
      </Select>
    </Field>
  );
}

/** The comment the database gets (a table's or a view's). */
export function CommentField({ id, json, edit, flush }: FormProps) {
  return (
    <Field
      label="Comment"
      htmlFor={`${id}-comment`}
      hint="The comment the database stores; without one, the description may be used (the comments convention)."
    >
      <CommitInput
        id={`${id}-comment`}
        value={String((json as Rec).comment ?? "")}
        onCommit={(v) => {
          edit((j) => setOptional(j as Rec, "comment", v));
          flush();
        }}
      />
    </Field>
  );
}

const NUMBER_LABELS: Record<SequenceNumber, { label: string; hint?: string }> = {
  start: { label: "Start", hint: "The first value (1 when empty)." },
  increment: { label: "Increment", hint: "Added each time; negative counts down (1 when empty)." },
  min: { label: "Minimum", hint: "Empty: the type's least value." },
  max: { label: "Maximum", hint: "Empty: the type's greatest value." },
  cache: { label: "Cache", hint: "Values kept in memory ahead (empty: the database's default)." },
};

/** A sequence's definition: type, start, increment, minimum, maximum, cycle and cache. */
export function SequenceFields({ id, json, edit, flush }: FormProps) {
  const rec = json as Rec;
  const [problem, setProblem] = useState<string | null>(null);
  const commitNumber = (field: SequenceNumber, raw: string) => {
    const why = sequenceNumberProblem(field, raw);
    setProblem(why ? `${NUMBER_LABELS[field].label}: ${why}` : null);
    if (why) return;
    edit((j) => void setSequenceNumber(j as Rec, field, raw));
    flush();
  };
  return (
    <div className="flex flex-col gap-2" data-testid="sequence-fields">
      <div className="grid grid-cols-[repeat(auto-fill,minmax(10rem,1fr))] gap-2">
        <Field label="Type" htmlFor={`${id}-type`}>
          <Select
            id={`${id}-type`}
            className="font-mono"
            value={String(rec.type ?? "int64")}
            onChange={(e) => {
              edit((j) => setOptional(j as Rec, "type", e.target.value === "int64" ? undefined : e.target.value));
              flush();
            }}
          >
            {SEQUENCE_TYPES.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </Select>
        </Field>
        {(Object.keys(NUMBER_LABELS) as SequenceNumber[]).map((field) => (
          <Field key={field} label={NUMBER_LABELS[field].label} htmlFor={`${id}-${field}`} hint={NUMBER_LABELS[field].hint}>
            <CommitInput
              id={`${id}-${field}`}
              mono
              value={typeof rec[field] === "number" ? String(rec[field]) : ""}
              placeholder={field === "start" || field === "increment" ? "1" : undefined}
              onCommit={(v) => commitNumber(field, v)}
            />
          </Field>
        ))}
      </div>
      <CheckboxField
        id={`${id}-cycle`}
        label="Cycle (start again after the last value)"
        checked={rec.cycle === true}
        onChange={(on) => {
          edit((j) => setOptional(j as Rec, "cycle", on ? true : undefined));
          flush();
        }}
      />
      {problem ? (
        <p role="alert" className="text-12 text-danger">
          {problem}
        </p>
      ) : null}
    </div>
  );
}

/** A view's summary in the inspector: its schema, comment, the dialects its body is written for and its declared columns. */
export function ViewFields(props: FormProps) {
  const rec = props.json as Rec;
  const dialects = viewDialects(rec);
  const columns = Array.isArray(rec.columns) ? (rec.columns as Rec[]) : [];
  return (
    <div className="flex flex-col gap-2" data-testid="view-fields">
      <SchemaField {...props} />
      <CommentField {...props} />
      <p className="text-12 text-secondary">
        Body for {dialects.map((d) => (d === "*" ? "any dialect" : d)).join(", ") || "no dialect"}; {columns.length}{" "}
        {columns.length === 1 ? "declared column" : "declared columns"}. The view&apos;s editor edits them (Body and Columns tabs).
      </p>
    </div>
  );
}
