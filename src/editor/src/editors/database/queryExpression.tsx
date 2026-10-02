// The expression cell of the query editor (query.json $defs/expression): a form picker, then what the form needs. A column is
// picked from the aliases in scope (`alias.column`, written as `alias.<column key>`), a parameter from the query's own; a value is
// typed; a function or an operation takes arguments, each an expression of its own; a conversion its operand and type; the SQL
// form one text per dialect; a case is edited as JSON for now. Every commit hands the whole new expression to `onChange`, which the
// editor saves at once: one gesture, one undo step.
import { useEffect, useState } from "react";
import { Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { BUILTIN_TYPES } from "@/model/model";
import {
  ARITHMETIC_OPS,
  columnRef,
  EXPRESSION_KIND_LABELS,
  EXPRESSION_KINDS,
  expressionKind,
  expressionProblem,
  literal,
  literalExpression,
  valueText,
  resolveColumn,
  withExpressionKind,
  type ArithmeticOp,
  type DialectMap,
  type Expression,
  type ExpressionKind,
  type QueryParameter,
  type ScopeAlias,
} from "@/model/queryTree";
import { CommitInput } from "./fields";

/** What an expression may name: the aliases in scope, the query's parameters, the database's routines and its dialect. */
export interface QueryEnv {
  scope: readonly ScopeAlias[];
  parameters: readonly QueryParameter[];
  routines: readonly { id: string; name: string }[];
  /** The database's dialect, the one an SQL text starts for. */
  dialect: string;
}

/** The functions the renderer spells per dialect (QuerySql.FunctionName); any other name is written as given. */
export const KNOWN_FUNCTIONS = ["lower", "upper", "coalesce", "count", "sum", "min", "max", "avg", "length", "now"] as const;

const SQL_DIALECTS = ["postgresql", "sqlserver", "mysql", "sqlite", "oracle", "*"] as const;

export const cellSelect = "h-6 rounded-[4px] border border-input bg-surface px-1 font-mono text-12";
const cellInput = "h-6 text-12";

/** The first column in scope (as `alias.<key>`), for a new column reference. */
export function firstColumn(env: QueryEnv): string | undefined {
  const own = env.scope.find((s) => !s.outer && s.columns.length);
  return own ? columnRef(own.alias, own.columns[0]) : undefined;
}

/** The column picker: the columns of every alias in scope, grouped by alias, labelled `alias.name`. */
export function ColumnSelect({ value, onChange, env, label }: { value: string; onChange: (v: string) => void; env: QueryEnv; label: string }) {
  const hit = value ? resolveColumn(env.scope, value) : null;
  const selected = hit ? columnRef(hit.alias.alias, hit.column) : value;
  const known = !value || !!hit;
  return (
    <select aria-label={label} className={`${cellSelect} min-w-32 max-w-64`} value={selected} onChange={(e) => onChange(e.target.value)} title={value}>
      {value ? null : <option value="">(pick a column)</option>}
      {known ? null : (
        <option value={value} disabled>
          {value} (not a column in scope)
        </option>
      )}
      {env.scope.map((s) => (
        <optgroup key={s.alias} label={s.outer ? `${s.alias} (outer query)` : `${s.alias}: ${s.source?.name ?? "unknown source"}`}>
          {s.columns.map((c) => (
            <option key={c.ref} value={columnRef(s.alias, c)}>
              {s.alias}.{c.name}
            </option>
          ))}
        </optgroup>
      ))}
    </select>
  );
}

/** A parameter picker over the query's parameters. */
export function ParamSelect({ value, onChange, env, label }: { value: string; onChange: (v: string) => void; env: QueryEnv; label: string }) {
  const known = !value || env.parameters.some((p) => p.name === value);
  return (
    <select aria-label={label} className={`${cellSelect} min-w-24`} value={value} onChange={(e) => onChange(e.target.value)}>
      {value ? null : <option value="">(pick a parameter)</option>}
      {known ? null : (
        <option value={value} disabled>
          {value} (not declared)
        </option>
      )}
      {env.parameters.map((p) => (
        <option key={p.name} value={p.name}>
          @{p.name}
          {p.collection ? " (list)" : ""}
        </option>
      ))}
    </select>
  );
}

/** A JSON text committed on blur when it parses (and differs); what does not parse stays in the box with the reason. */
export function JsonCommit<T>({ value, onCommit, label, rows = 6 }: { value: T; onCommit: (next: T) => void; label: string; rows?: number }) {
  const saved = JSON.stringify(value, null, 2);
  const [text, setText] = useState(saved);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setText(saved);
    setError(null);
  }, [saved]);
  return (
    <div className="flex min-w-0 flex-1 flex-col gap-0.5">
      <textarea
        aria-label={label}
        rows={rows}
        spellCheck={false}
        className="w-full min-w-64 rounded-[4px] border border-input bg-surface p-1 font-mono text-12 aria-[invalid=true]:border-danger"
        aria-invalid={!!error || undefined}
        value={text}
        onChange={(e) => setText(e.target.value)}
        onBlur={() => {
          if (text === saved) return;
          try {
            const parsed = JSON.parse(text) as T;
            setError(null);
            if (JSON.stringify(parsed) !== JSON.stringify(value)) onCommit(parsed);
          } catch (e) {
            setError(`Not valid JSON yet: ${(e as Error).message}`);
          }
        }}
      />
      {error ? <p className="text-11 text-danger">{error}</p> : null}
    </div>
  );
}

/** One expression: its form, then the form's own inputs (nested expressions indent below). */
export function ExpressionEditor({
  value,
  onChange,
  env,
  label,
  depth = 0,
}: {
  value: Expression | undefined;
  onChange: (next: Expression) => void;
  env: QueryEnv;
  /** The accessible name the cell's controls build on ("Field id", "Where 1 left"). */
  label: string;
  depth?: number;
}) {
  const kind = expressionKind(value);
  const e = value ?? {};
  const problem = value ? expressionProblem(value) : null;
  const setKind = (next: ExpressionKind) =>
    onChange(withExpressionKind(value, next, { column: firstColumn(env), param: env.parameters[0]?.name, dialect: env.dialect }));
  const args = e.args ?? [];
  const setArg = (i: number, arg: Expression) => onChange({ ...e, args: args.map((a, j) => (j === i ? arg : a)) });
  const removeArg = (i: number) => {
    const next = args.filter((_, j) => j !== i);
    const copy = { ...e };
    if (next.length) copy.args = next;
    else delete copy.args;
    onChange(copy);
  };
  const addArg = () => onChange({ ...e, args: [...args, kind === "call" && !args.length && firstColumn(env) ? { column: firstColumn(env)! } : literal(0)] });
  const nested = kind === "call" || kind === "op" || kind === "cast";
  return (
    <div className="flex min-w-0 flex-col gap-0.5" data-testid="query-expression" data-kind={kind ?? "none"}>
      <div className="flex min-w-0 flex-wrap items-center gap-1">
        <select
          aria-label={`${label}: form`}
          className={`${cellSelect} w-28 font-sans`}
          value={kind ?? ""}
          onChange={(ev) => setKind(ev.target.value as ExpressionKind)}
        >
          {kind ? null : <option value="">(choose)</option>}
          {EXPRESSION_KINDS.map((k) => (
            <option key={k} value={k} disabled={k === "param" && !env.parameters.length && kind !== "param"}>
              {EXPRESSION_KIND_LABELS[k]}
            </option>
          ))}
        </select>
        {kind === "column" ? <ColumnSelect value={e.column ?? ""} env={env} label={`${label}: column`} onChange={(column) => onChange({ column })} /> : null}
        {kind === "param" ? <ParamSelect value={e.param ?? ""} env={env} label={`${label}: parameter`} onChange={(p) => onChange({ param: p })} /> : null}
        {kind === "value" ? (
          <span title="A number, true or false, or a text; quote a text that reads as a number ('1'). A number with a fractional point keeps its digits (2.0).">
            <CommitInput
              label={`${label}: value`}
              mono
              className={`${cellInput} w-36`}
              value={valueText(e)}
              onCommit={(text) => onChange(literalExpression(text))}
            />
          </span>
        ) : null}
        {kind === "call" ? (
          <>
            <CommitInput
              label={`${label}: function`}
              mono
              list={FUNCTION_LIST}
              className={`${cellInput} w-32`}
              value={env.routines.find((r) => r.id === e.call)?.name ?? e.call ?? ""}
              onCommit={(text) => {
                const name = text.trim();
                if (!name) return;
                onChange({ ...e, call: env.routines.find((r) => r.name === name)?.id ?? name });
              }}
            />
          </>
        ) : null}
        {kind === "op" ? (
          <select
            aria-label={`${label}: operator`}
            className={cellSelect}
            value={e.op}
            onChange={(ev) => onChange({ ...e, op: ev.target.value as ArithmeticOp })}
          >
            {ARITHMETIC_OPS.map((o) => (
              <option key={o} value={o}>
                {o === "concat" ? "concat (||)" : o}
              </option>
            ))}
          </select>
        ) : null}
        {kind === "cast" ? (
          <select aria-label={`${label}: type`} className={cellSelect} value={e.type ?? "string"} onChange={(ev) => onChange({ ...e, type: ev.target.value })}>
            {BUILTIN_TYPES.map((t) => (
              <option key={t} value={t}>
                as {t}
              </option>
            ))}
          </select>
        ) : null}
        {kind === "call" || kind === "op" ? (
          <Button size="icon-row" variant="ghost" label={`Add an argument to ${label}`} onClick={addArg}>
            <Plus />
          </Button>
        ) : null}
        {kind === "column" && e.column && resolveColumn(env.scope, e.column)?.alias.outer ? (
          <span className="text-11 text-secondary" title="A column of the query around this one: the correlation">
            outer
          </span>
        ) : null}
      </div>
      {nested ? (
        <div className="ml-3 flex flex-col gap-0.5 border-l border-default pl-2">
          {kind === "cast" ? (
            <ExpressionEditor value={e.cast} env={env} label={`${label} operand`} depth={depth + 1} onChange={(c) => onChange({ ...e, cast: c })} />
          ) : (
            args.map((a, i) => (
              <div key={i} className="flex min-w-0 items-start gap-1">
                <ExpressionEditor value={a} env={env} label={`${label} argument ${i + 1}`} depth={depth + 1} onChange={(x) => setArg(i, x)} />
                <Button size="icon-row" variant="ghost" label={`Remove argument ${i + 1} of ${label}`} onClick={() => removeArg(i)}>
                  <X />
                </Button>
              </div>
            ))
          )}
        </div>
      ) : null}
      {kind === "sql" ? <SqlTexts value={e.sql ?? {}} env={env} label={label} onChange={(sql) => onChange({ sql })} /> : null}
      {kind === "case" ? (
        <JsonCommit
          value={{ case: e.case, ...(e.else ? { else: e.else } : {}) }}
          label={`${label}: case as JSON`}
          rows={5}
          onCommit={(next) => onChange(next as Expression)}
        />
      ) : null}
      {problem && kind ? <p className="text-11 text-danger">{problem}</p> : null}
    </div>
  );
}

/** The SQL form's texts: one per dialect (or * for any), each committed on its own. */
function SqlTexts({ value, onChange, env, label }: { value: DialectMap; onChange: (next: DialectMap) => void; env: QueryEnv; label: string }) {
  const entries = Object.entries(value) as [keyof DialectMap, string][];
  const free = SQL_DIALECTS.filter((d) => !(d in value));
  return (
    <div className="ml-3 flex flex-col gap-0.5 border-l border-default pl-2" title="An opaque expression per dialect, for what the tree cannot say">
      {entries.map(([d, text]) => (
        <div key={d} className="flex items-center gap-1">
          <span className="w-20 shrink-0 font-mono text-11 text-secondary">{d === "*" ? "* (any)" : d}</span>
          <CommitInput
            label={`${label}: SQL for ${d}`}
            mono
            className={`${cellInput} min-w-64`}
            value={text}
            onCommit={(t) => {
              // A blank text is no text: it leaves the dialect out (the form keeps at least one text).
              if (t.trim() !== "") return onChange({ ...value, [d]: t });
              if (entries.length === 1) return;
              const next = { ...value };
              delete next[d];
              onChange(next);
            }}
          />
          <Button
            size="icon-row"
            variant="ghost"
            label={entries.length === 1 ? "The SQL form keeps at least one text" : `Remove the ${d} text of ${label}`}
            disabled={entries.length === 1}
            onClick={() => {
              const next = { ...value };
              delete next[d];
              onChange(next);
            }}
          >
            <X />
          </Button>
        </div>
      ))}
      {free.length ? (
        <select
          aria-label={`${label}: add a dialect`}
          className={`${cellSelect} w-40 font-sans`}
          value=""
          onChange={(ev) => ev.target.value && onChange({ ...value, [ev.target.value]: "NULL" })}
        >
          <option value="">Add a dialect…</option>
          {free.map((d) => (
            <option key={d} value={d}>
              {d === "*" ? "* (any dialect)" : d === env.dialect ? `${d} (the database's)` : d}
            </option>
          ))}
        </select>
      ) : null}
    </div>
  );
}

/** The suggestions of a function cell, rendered once per editor: the functions spelled per dialect, then the routines. */
export const FUNCTION_LIST = "query-functions";
export function FunctionList({ env }: { env: QueryEnv }) {
  return (
    <datalist id={FUNCTION_LIST}>
      {KNOWN_FUNCTIONS.map((f) => (
        <option key={f} value={f} />
      ))}
      {env.routines.map((r) => (
        <option key={r.id} value={r.name} label="routine of this database" />
      ))}
    </datalist>
  );
}
