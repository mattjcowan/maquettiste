// The fields the database object editors (and the inspector, for views, sequences, routines, database types and SQL objects)
// share: the schema picker, the comment, a sequence's definition, the kind pickers of routines, database types and SQL objects,
// and each kind's summary. Every commit is one edit and one save, so one undo step.
import { useEffect, useRef, useState } from "react";
import { useElements, useIndex } from "@/api/queries";
import { Field, Input, Select } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";
import { setOptional, type FormProps } from "@/inspector/fields";
import { defaultSchemaName, schemasOf } from "@/model/databaseSchemas";
import {
  OBJECT_KIND_SUGGESTIONS,
  OBJECT_PHASE_LABELS,
  OBJECT_PHASES,
  ROUTINE_KINDS,
  SEQUENCE_TYPES,
  TYPE_KIND_LABELS,
  TYPE_KINDS,
  typeKindDefaults,
  type TypeKind,
} from "@/explorer/databaseCreate";
import { dialectKeys, sequenceNumberProblem, setRoutineKind, setSequenceNumber, setTypeKind, viewDialects, type SequenceNumber } from "./databaseDocs";

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
  list,
}: {
  id?: string;
  /** A datalist's id: suggestions while typing. */
  list?: string;
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
      list={list}
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

/** The dialects of a per-dialect member, as a summary says them. */
const dialectWords = (rec: Rec, member: string) =>
  dialectKeys(rec, member)
    .map((d) => (d === "*" ? "any dialect" : d))
    .join(", ");

/** A typed slot (a parameter, a result, a field) as one short text: `decimal(18,2)`, a database type's name, the native type. */
export function typeText(row: Rec | undefined, nameOf: (id: string) => string | undefined): string {
  if (!row) return "";
  const type = typeof row.type === "string" ? (nameOf(row.type) ?? row.type) : "";
  const facets = [row.length ?? row.precision, row.scale].filter((n) => typeof n === "number").join(",");
  const base = type ? `${type}${facets ? `(${facets})` : ""}` : "";
  return typeof row.nativeType === "string" ? (base ? `${base} as ${row.nativeType}` : row.nativeType) : base;
}

/** Names of the elements of the index, for the summaries' database type ids. */
function useNameOf(): (id: string) => string | undefined {
  const index = useIndex();
  const rows = index.data ?? [];
  return (id) => rows.find((r) => r.id === id)?.name;
}

/** A routine's kind: a function returns a value or a table; a procedure is called for its effects. */
export function RoutineKindField({ id, json, edit, flush }: FormProps) {
  const rec = json as Rec;
  return (
    <Field label="Routine kind" htmlFor={`${id}-routine-kind`} hint="A function returns a value or a table; a procedure is called for its effects.">
      <Select
        id={`${id}-routine-kind`}
        value={String(rec.routineKind ?? "function")}
        onChange={(e) => {
          edit((j) => setRoutineKind(j as Rec, e.target.value as (typeof ROUTINE_KINDS)[number]));
          flush();
        }}
      >
        {ROUTINE_KINDS.map((k) => (
          <option key={k} value={k}>
            {k === "function" ? "Function" : "Procedure"}
          </option>
        ))}
      </Select>
    </Field>
  );
}

/** A routine's summary in the inspector: schema, kind, comment, then its parameters, result and the dialects of its body. */
export function RoutineFields(props: FormProps) {
  const rec = props.json as Rec;
  const nameOf = useNameOf();
  const parameters = Array.isArray(rec.parameters) ? (rec.parameters as Rec[]) : [];
  const returns = rec.returns as Rec | undefined;
  const result = !returns
    ? "returns nothing"
    : Array.isArray(returns.table)
      ? `returns a table of ${returns.table.length} columns`
      : `returns ${typeText(returns, nameOf)}`;
  return (
    <div className="flex flex-col gap-2" data-testid="routine-fields">
      <SchemaField {...props} />
      <RoutineKindField {...props} />
      <CommentField {...props} />
      <p className="text-12 text-secondary">
        {parameters.length} {parameters.length === 1 ? "parameter" : "parameters"}; {result}; body for {dialectWords(rec, "body") || "no dialect"}. The
        routine&apos;s editor edits them (Parameters, Definition and Body tabs).
      </p>
    </div>
  );
}

/** A database type's kind; switching it keeps only what the new kind uses and starts it with something to create. */
export function TypeKindField({ id, json, edit, flush }: FormProps) {
  const rec = json as Rec;
  return (
    <Field label="Type kind" htmlFor={`${id}-type-kind`}>
      <Select
        id={`${id}-type-kind`}
        value={String(rec.typeKind ?? "domain")}
        onChange={(e) => {
          const next = e.target.value as TypeKind;
          edit((j) => setTypeKind(j as Rec, next, typeKindDefaults(next)));
          flush();
        }}
      >
        {TYPE_KINDS.map((k) => (
          <option key={k} value={k}>
            {TYPE_KIND_LABELS[k]}
          </option>
        ))}
      </Select>
    </Field>
  );
}

/** What a database type is, in one line: "Domain over string(320)", "Enum: draft, issued", "Composite of 2 fields". */
export function typeSummary(rec: Rec, nameOf: (id: string) => string | undefined = () => undefined): string {
  switch (rec.typeKind) {
    case "enum": {
      const members = Array.isArray(rec.members) ? (rec.members as string[]) : [];
      return members.length ? `Enum: ${members.join(", ")}` : "Enum without labels";
    }
    case "composite": {
      const fields = Array.isArray(rec.fields) ? (rec.fields as Rec[]) : [];
      return `Composite of ${fields.length} ${fields.length === 1 ? "field" : "fields"}`;
    }
    case "range":
      return `Range over ${String(rec.subtype ?? "?")}`;
    default:
      return `Domain over ${typeText({ type: rec.base, length: rec.length, precision: rec.precision, scale: rec.scale }, nameOf) || "?"}${typeof rec.check === "string" ? ` with a check` : ""}`;
  }
}

/** A database type's summary in the inspector: schema, kind, comment and what it is. */
export function DatabaseTypeFields(props: FormProps) {
  const rec = props.json as Rec;
  const nameOf = useNameOf();
  const definition = dialectWords(rec, "definition");
  return (
    <div className="flex flex-col gap-2" data-testid="database-type-fields">
      <SchemaField {...props} />
      <TypeKindField {...props} />
      <CommentField {...props} />
      <p className="text-12 text-secondary">
        {typeSummary(rec, nameOf)}
        {definition ? `; its own definition for ${definition}` : ""}. The type&apos;s editor edits it (Definition and Dialects tabs).
      </p>
    </div>
  );
}

/** A SQL object's kind (free text, with suggestions). */
export function ObjectKindField({ id, json, edit, flush }: FormProps) {
  const rec = json as Rec;
  const [problem, setProblem] = useState(false);
  return (
    <Field
      label="Object kind"
      htmlFor={`${id}-object-kind`}
      hint={problem ? "Say what the object is; it cannot be empty." : "What the object is, in your words."}
    >
      <CommitInput
        id={`${id}-object-kind`}
        mono
        list={`${id}-object-kinds`}
        invalid={problem}
        value={String(rec.objectKind ?? "")}
        onCommit={(v) => {
          setProblem(!v.trim());
          if (!v.trim()) return;
          edit((j) => void ((j as Rec).objectKind = v.trim()));
          flush();
        }}
      />
      <datalist id={`${id}-object-kinds`}>
        {OBJECT_KIND_SUGGESTIONS.map((k) => (
          <option key={k} value={k} />
        ))}
      </datalist>
    </Field>
  );
}

/** When a SQL object's statements run: before the database types and tables, or after the routines and views. */
export function PhaseField({ id, json, edit, flush }: FormProps) {
  const rec = json as Rec;
  return (
    <Field label="Runs" htmlFor={`${id}-phase`}>
      <Select
        id={`${id}-phase`}
        value={String(rec.phase ?? "after")}
        onChange={(e) => {
          edit((j) => setOptional(j as Rec, "phase", e.target.value === "after" ? undefined : e.target.value));
          flush();
        }}
      >
        {OBJECT_PHASES.map((p) => (
          <option key={p} value={p}>
            {OBJECT_PHASE_LABELS[p]}
          </option>
        ))}
      </Select>
    </Field>
  );
}

/** A SQL object's summary in the inspector: schema, kind, phase, then the dialects of its statements and its dependencies. */
export function SqlObjectFields(props: FormProps) {
  const rec = props.json as Rec;
  const depends = Array.isArray(rec.dependsOn) ? rec.dependsOn.length : 0;
  return (
    <div className="flex flex-col gap-2" data-testid="sql-object-fields">
      <SchemaField {...props} />
      <ObjectKindField {...props} />
      <PhaseField {...props} />
      <p className="text-12 text-secondary">
        Statements for {dialectWords(rec, "body") || "no dialect"}; depends on {depends} {depends === 1 ? "object" : "objects"}. The object&apos;s editor edits
        them (Definition and Body tabs).
      </p>
    </div>
  );
}
