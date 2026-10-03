// A column's DDL facets and how it gets its values (schemas/v1 table.json), everything a column can say beyond its name, type
// and nullability: the text encoding (`unicode`, string and text columns), a fixed length (`fixedLength`, string and binary
// columns), the default constraint's name (`defaultName`), identity or a sequence of the database (`generated`, `sequence`), a
// computed expression stored or virtual (`computed`, `computedStored`), an identity's seed, increment and generated always
// (`identity`), the collation, and a default SQL expression per dialect (`defaultSql`, `*` for any). Shown in the column inspector and under the table editor's column grid for the picked column;
// each change is one save of the table, one undo step. A facet its type does not have is not offered (MQ4057 otherwise); what
// does not fit together (a computed identity, a sequence not picked) is said under the fields, with the column's other problems.
import { useState } from "react";
import type { ColumnView, Diagnostic } from "@/api/types";
import { Field, Select } from "@/components/ui/input";
import { CheckboxField } from "@/components/ui/checkbox";
import { SectionTitle } from "@/components/ui/misc";
import {
  columnFieldProblem,
  columnGeneration,
  columnGenerationProblems,
  identityNote,
  FIXED_LENGTH_TYPES,
  UNICODE_TYPES,
  unicodeChoice,
  type ColumnFacetField,
  type ColumnGenerationField,
} from "@/workspaces/database/columnEdits";
import { CommitInput } from "./fields";

type Rec = Record<string, unknown>;

/** The column's problems among the table's (by its place in the file): what its own pointer names. */
export function columnProblems(problems: readonly Diagnostic[], doc: Rec | null | undefined, column: Pick<ColumnView, "key">): Diagnostic[] {
  const columns = Array.isArray(doc?.columns) ? (doc!.columns as Rec[]) : [];
  const at = columns.findIndex((c) => c.id === column.key || c.attribute === column.key);
  if (at < 0) return [];
  const prefix = `/columns/${at}`;
  return problems.filter((d) => d.jsonPointer === prefix || (d.jsonPointer ?? "").startsWith(`${prefix}/`));
}

const DIALECT_WORDS: Record<string, string> = {
  postgresql: "PostgreSQL",
  sqlserver: "SQL Server",
  mysql: "MySQL",
  sqlite: "SQLite",
  oracle: "Oracle",
  "*": "Any dialect",
};

export type ColumnFacetValue = string | boolean | { dialect: string; text: string };

export function ColumnFacets({
  id,
  column,
  entry,
  problems,
  disabled,
  dialect = "postgresql",
  sequences = [],
  onSet,
}: {
  id: string;
  column: Pick<ColumnView, "key" | "name" | "type"> &
    Partial<Pick<ColumnView, "identity" | "sequenceId" | "computed" | "computedStored" | "collation" | "defaultSql" | "default">>;
  /** The column's own entry in the table's file (undefined before the table is stored as a file). */
  entry: Rec | null | undefined;
  /** The column's problems (columnProblems). */
  problems: readonly Diagnostic[];
  disabled?: boolean;
  /** The database's dialect (the default SQL row it starts with). */
  dialect?: string;
  /** The database's sequences (what a sequence-generated column picks from). */
  sequences?: readonly { id: string; name: string; schema?: string | null }[];
  onSet: (field: ColumnFacetField | ColumnGenerationField, value: ColumnFacetValue) => void;
}) {
  const unicode = UNICODE_TYPES.has(column.type);
  const fixed = FIXED_LENGTH_TYPES.has(column.type);
  const g = columnGeneration(
    entry,
    {
      identity: column.identity ?? false,
      sequenceId: column.sequenceId ?? null,
      computed: column.computed ?? null,
      computedStored: column.computedStored ?? false,
      collation: column.collation ?? null,
      defaultSql: column.defaultSql ?? null,
    },
    dialect,
  );
  const inline = columnGenerationProblems(g, { type: column.type, default: entry ? (entry.default as ColumnView["default"]) : (column.default ?? null) });
  const dialects = [...new Set([dialect, "*", ...Object.keys(g.defaultSql)])];
  const knownSequence = !g.sequence || sequences.some((s) => s.id === g.sequence);
  const note = identityNote(dialect);
  return (
    <div className="flex flex-col gap-2" data-testid="column-facets">
      {problems.length || inline.length ? (
        <ul role="alert" className="flex flex-col gap-0.5 text-12 text-danger" data-testid="column-facet-problems">
          {inline.map((p) => (
            <li key={p} data-testid="column-value-problem">
              {p}
            </li>
          ))}
          {problems.map((d, i) => (
            <li key={i} className={d.severity === "error" ? undefined : "text-secondary"}>
              <span className="font-mono">{d.rule}</span> {d.message}
            </li>
          ))}
        </ul>
      ) : null}
      {unicode || fixed ? (
        <div className="grid grid-cols-2 items-end gap-2">
          {unicode ? (
            <Field
              label="Text encoding"
              htmlFor={`${id}-unicode`}
              hint="Unicode (nvarchar on SQL Server, nvarchar2 on Oracle, utf8mb4 on MySQL) or single-byte (varchar); PostgreSQL and SQLite keep every text in the database's encoding."
            >
              <Select id={`${id}-unicode`} value={unicodeChoice(entry ?? undefined)} disabled={disabled} onChange={(e) => onSet("unicode", e.target.value)}>
                <option value="">The dialect&apos;s default</option>
                <option value="true">Unicode</option>
                <option value="false">Single-byte</option>
              </Select>
            </Field>
          ) : (
            <span />
          )}
          {fixed ? (
            <CheckboxField
              id={`${id}-fixed`}
              label={column.type === "binary" ? "Fixed width (binary(n))" : "Fixed width (char(n))"}
              checked={entry?.fixedLength === true}
              disabled={disabled}
              onChange={(v) => onSet("fixedLength", v)}
            />
          ) : null}
        </div>
      ) : null}
      <Field
        label="Collation"
        htmlFor={`${id}-collation`}
        hint="The collation the database sorts and compares this column with, such as C or Latin1_General_CI_AS; empty: the database's."
      >
        <CommitInput id={`${id}-collation`} mono value={g.collation} placeholder="the database's" onCommit={(v) => onSet("collation", v)} />
      </Field>
      <SectionTitle>Values</SectionTitle>
      <div className="grid grid-cols-2 items-end gap-2">
        <Field
          label="Generated by"
          htmlFor={`${id}-generated`}
          hint="Identity: the database numbers new rows. Sequence: a sequence of the database supplies the values."
        >
          <Select
            id={`${id}-generated`}
            value={g.generated}
            disabled={disabled}
            onChange={(e) => onSet("generated", e.target.value)}
            data-testid="column-generated"
          >
            <option value="">Nothing (values are given)</option>
            <option value="identity">Identity (auto increment)</option>
            <option value="sequence">A sequence</option>
          </Select>
        </Field>
        {g.generated === "sequence" ? (
          <Field label="Sequence" htmlFor={`${id}-sequence`}>
            <Select
              id={`${id}-sequence`}
              value={g.sequence}
              disabled={disabled}
              onChange={(e) => onSet("sequence", e.target.value)}
              data-testid="column-sequence"
            >
              <option value="">Choose a sequence</option>
              {knownSequence ? null : <option value={g.sequence}>{g.sequence}</option>}
              {sequences.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.schema ? `${s.schema}.` : ""}
                  {s.name}
                </option>
              ))}
            </Select>
          </Field>
        ) : (
          <span />
        )}
      </div>
      {g.generated === "identity" ? (
        <div className="flex flex-col gap-1" data-testid="column-identity">
          <div className="grid grid-cols-2 items-start gap-2">
            <CheckedInput
              id={`${id}-identity-seed`}
              label="Identity seed"
              hint="The first value; empty: the database's (1)."
              field="identitySeed"
              value={g.identitySeed}
              placeholder="1"
              disabled={disabled}
              onSet={onSet}
            />
            <CheckedInput
              id={`${id}-identity-increment`}
              label="Identity increment"
              hint="The step between values, not 0; empty: 1."
              field="identityIncrement"
              value={g.identityIncrement}
              placeholder="1"
              disabled={disabled}
              onSet={onSet}
            />
          </div>
          <CheckboxField
            id={`${id}-identity-always`}
            label="Generated always (an insert cannot give a value)"
            checked={g.identityAlways}
            disabled={disabled}
            onChange={(v) => onSet("identityAlways", v)}
          />
          {note ? (
            <p className="text-11 text-secondary" data-testid="column-identity-note">
              {note}
            </p>
          ) : null}
        </div>
      ) : null}
      <Field label="Computed from" htmlFor={`${id}-computed`} hint="An SQL expression over the table's other columns; empty: an ordinary column.">
        <CommitInput id={`${id}-computed`} mono value={g.computed} placeholder="not computed" onCommit={(v) => onSet("computed", v)} />
      </Field>
      {g.computed ? (
        <CheckboxField
          id={`${id}-computed-stored`}
          label="Stored (written to disk; unticked: virtual, computed when read)"
          checked={g.computedStored}
          disabled={disabled}
          onChange={(v) => onSet("computedStored", v)}
        />
      ) : null}
      <fieldset className="flex flex-col gap-1" aria-label="Default SQL per dialect" data-testid="column-default-sql">
        <legend className="mb-1 text-12 font-medium text-secondary">Default SQL (per dialect)</legend>
        {dialects.map((d) => (
          <div key={d} className="grid grid-cols-[7rem_1fr] items-center gap-2">
            <span className="text-12 text-secondary">{DIALECT_WORDS[d] ?? d}</span>
            <CommitInput
              label={`Default SQL for ${DIALECT_WORDS[d] ?? d}`}
              mono
              value={g.defaultSql[d] ?? ""}
              placeholder={d === "*" ? "for every dialect" : "such as now() or gen_random_uuid()"}
              onCommit={(v) => onSet("defaultSql", { dialect: d, text: v })}
            />
          </div>
        ))}
      </fieldset>
      <Field
        label="Default constraint name"
        htmlFor={`${id}-default-name`}
        hint="Where the dialect names defaults (SQL Server); empty: df_<table>_<column>. Other dialects ignore it."
      >
        <CommitInput
          id={`${id}-default-name`}
          mono
          value={typeof entry?.defaultName === "string" ? entry.defaultName : ""}
          placeholder={`df_…_${column.name}`}
          onCommit={(v) => onSet("defaultName", v)}
        />
      </Field>
    </div>
  );
}

/** A whole-number input that says inline why a typed value cannot be saved (and does not save it). */
function CheckedInput({
  id,
  label,
  hint,
  field,
  value,
  placeholder,
  disabled,
  onSet,
}: {
  id: string;
  label: string;
  hint: string;
  field: "identitySeed" | "identityIncrement";
  value: string;
  placeholder?: string;
  disabled?: boolean;
  onSet: (field: ColumnGenerationField, value: ColumnFacetValue) => void;
}) {
  const [problem, setProblem] = useState<string | null>(null);
  return (
    <Field label={label} htmlFor={id} hint={problem ? undefined : hint}>
      <CommitInput
        id={id}
        mono
        value={value}
        placeholder={placeholder}
        invalid={!!problem}
        onCommit={(v) => {
          const why = columnFieldProblem(field, v);
          setProblem(why);
          if (!why && !disabled) onSet(field, v.trim());
        }}
      />
      {problem ? (
        <p role="alert" className="text-11 text-danger" data-testid="column-value-problem">
          {problem}
        </p>
      ) : null}
    </Field>
  );
}
