// The sections of the query editor, shared by the query and by its named queries: Sources (from and joins, a join's condition
// from a foreign key), Select (the fields of the row), Filter (the where tree), Group and order (grouping, having, ordering,
// distinct and, at the top, paging) and Parameters. Each gesture is one `update` (one save, one undo step).
import { Fragment, useState } from "react";
import { ArrowDown, ArrowUp, KeyRound, Plus, Trash2 } from "lucide-react";
import type { DatabaseView, Diagnostic, ElementSummary, QueryView } from "@/api/types";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/menu";
import { EmptyState, SectionTitle } from "@/components/ui/misc";
import { BUILTIN_TYPES } from "@/model/model";
import {
  columnOptions,
  defaultAlias,
  fieldName,
  findSource,
  foreignKeyJoins,
  JOIN_KINDS,
  matchColumn,
  nameProblem,
  renameAlias,
  renameParameter,
  scopeOf,
  sourceOptions,
  tidy,
  type ColumnOption,
  type Expression,
  type Field,
  type JoinKind,
  type Order,
  type QueryDoc,
  type QueryParameter,
  type ScopeAlias,
  type SourceOption,
  type Subquery,
  type TableFileRow,
} from "@/model/queryTree";
import { facetProblem, setFacet, type TypeFacet } from "./databaseDocs";
import { CommitInput } from "./fields";
import { cellSelect, ExpressionEditor, firstColumn, ParamSelect, type QueryEnv } from "./queryExpression";
import { PredicateEditor } from "./queryPredicate";
import { TypeSelect, useDatabaseTypes } from "./TypedRowsGrid";

/** Edits a query body (the query, or a named query's part) as one gesture. */
export type BodyUpdate = (mutate: (body: Subquery) => void) => void;

/** What the sections read besides the body: the resolved database, its table files, and the query's environment. */
export interface QueryContext {
  view: DatabaseView | null;
  files: readonly TableFileRow[];
  /** The query's parameters, routines and dialect; `scope` is replaced per body. */
  env: QueryEnv;
  /** The aliases of the queries around this body. */
  outer: readonly ScopeAlias[];
  /** The named queries (of the query's `with`) a source here may read, with their columns. */
  named?: readonly SourceOption[];
}

const head = "h-6 px-1 text-left text-11 font-semibold text-secondary";
const cell = "px-1 py-0.5 align-top";

function Problem({ text }: { text: string | null }) {
  return text ? (
    <p role="alert" className="text-12 text-danger">
      {text}
    </p>
  ) : null;
}

/** The index's table files of a database (a synthesized table's overlay names its entity), for reading a source id. */
export function tableFiles(rows: readonly ElementSummary[] | undefined, database: string): TableFileRow[] {
  return (rows ?? [])
    .filter((r) => r.kind === "table" && r.database === database)
    .map((r) => ({ id: r.id, entity: r.entity ?? null, relation: (r as { relation?: string | null }).relation ?? null }));
}

// ------------------------------------------------------------------ sources

/** A source as the pickers name it: `schema.name`, a view marked. */
const sourceLabel = (o: SourceOption | null): string | null =>
  o ? `${o.schema ? `${o.schema}.${o.name}` : o.name}${o.kind === "view" ? " (view)" : ""}${o.kind === "named" ? " (named query)" : ""}` : null;

/** Rewrites the `alias.column` references in a body (not its named queries or repeating part) from one source's column refs to another's, by name. */
function retargetColumns(body: unknown, alias: string, from: readonly ColumnOption[], to: readonly ColumnOption[]) {
  const walk = (node: unknown) => {
    if (Array.isArray(node)) return node.forEach(walk);
    if (!node || typeof node !== "object") return;
    for (const [key, value] of Object.entries(node)) {
      if (key === "with" || key === "recursive") continue;
      if (key === "column" && typeof value === "string" && value.startsWith(`${alias}.`)) {
        const old = value.slice(alias.length + 1);
        const named = from.find((c) => c.ref === old);
        const match = named ? matchColumn(to, named.name) : null;
        if (match) (node as Record<string, unknown>)[key] = `${alias}.${match.ref}`;
      } else walk(value);
    }
  };
  walk(body);
}

/** The from source and the joins: what each reads, its alias, the join's kind and condition. */
export function SourcesSection({ body, update, q, label }: { body: Subquery; update: BodyUpdate; q: QueryContext; label: string }) {
  const options = sourceOptions(q.view);
  const named = q.named ?? [];
  const joins = body.joins ?? [];
  const [problem, setProblem] = useState<string | null>(null);
  const scopeUpTo = (n: number) => scopeOf({ from: body.from, joins: joins.slice(0, n) }, q.view, q.files, q.outer, q.named);
  const aliases = [body.from, ...joins].map((s, i) => s.alias ?? scopeUpTo(i + 1)[i]?.alias ?? s.source);
  const sourceSelect = (value: string, aria: string, onPick: (v: string) => void) => {
    const listed = (o: SourceOption) => (
      <option key={o.value} value={o.value}>
        {sourceLabel(o)}
      </option>
    );
    return (
      <select aria-label={aria} className={`${cellSelect} w-56 font-sans`} value={value} onChange={(e) => onPick(e.target.value)}>
        {options.some((o) => o.value === value) || named.some((o) => o.value === value) ? null : (
          <option value={value}>{sourceLabel(findSource(q.view, value, q.files, named)) ?? `${value || "(pick a source)"}`}</option>
        )}
        {named.length ? (
          <>
            <optgroup label="Named queries">{named.map(listed)}</optgroup>
            <optgroup label="Tables and views">{options.map(listed)}</optgroup>
          </>
        ) : (
          options.map(listed)
        )}
      </select>
    );
  };
  const aliasInput = (i: number, aria: string) => (
    <CommitInput
      label={aria}
      mono
      className="h-6 w-24 text-12"
      value={aliases[i]}
      onCommit={(v) => {
        const next = v.trim();
        const why = nameProblem(
          next,
          aliases.filter((_, j) => j !== i),
          "alias",
        );
        setProblem(why);
        if (why) return;
        update((b) => {
          const from = aliases[i];
          const target = i === 0 ? b.from : b.joins![i - 1];
          target.alias = from;
          renameAlias(b, from, next);
          target.alias = next;
        });
      }}
    />
  );
  const addJoin = () => {
    // A table that shares a foreign key with one already read comes first; else the first source not read yet.
    const scope = scopeUpTo(joins.length + 1);
    const read = new Set([body.from.source, ...joins.map((j) => j.source)]);
    const related = options.find(
      (o) =>
        !read.has(o.value) &&
        o.table &&
        foreignKeyJoins(
          { alias: "x", source: o, columns: [], outer: false },
          scope.filter((s) => !s.outer),
        ).length > 0,
    );
    const pick = related ?? options.find((o) => !read.has(o.value)) ?? options[0];
    if (!pick) return;
    update((b) => void (b.joins = [...(b.joins ?? []), { source: pick.value, alias: defaultAlias(pick.name, aliases) }]));
  };
  return (
    <div className="flex flex-col gap-2" data-testid="query-sources">
      <div className="flex flex-wrap items-center gap-2" data-testid="query-from">
        <span className="w-12 text-12 font-medium text-secondary">From</span>
        {sourceSelect(body.from.source, `${label} from source`, (v) =>
          update((b) => {
            const before = findSource(q.view, b.from.source, q.files, named);
            const after = findSource(q.view, v, q.files, named);
            b.from = { ...b.from, source: v };
            // The columns read through the from alias follow to the same-named column of the new source, when it has one: pointing a
            // query at another table (or a named query) does not leave its fields reading the old table's column keys.
            if (before && after) retargetColumns(b, aliases[0], columnOptions(before), columnOptions(after));
          }),
        )}
        <span className="text-12 text-secondary">as</span>
        {aliasInput(0, `${label} from alias`)}
      </div>
      {joins.map((j, i) => {
        const joined = scopeUpTo(i + 1).find((s) => s.alias === aliases[i + 1]);
        const earlier = scopeUpTo(i + 1).filter((s) => !s.outer && s.alias !== aliases[i + 1]);
        const candidates = joined ? foreignKeyJoins(joined, earlier) : [];
        const kind = j.kind ?? "inner";
        const setOn = (on: QueryDoc["where"]) =>
          update((b) => {
            const target = b.joins![i];
            if (on) target.on = on;
            else delete target.on;
          });
        const env: QueryEnv = { ...q.env, scope: scopeUpTo(i + 2) };
        return (
          <div key={i} className="flex flex-col gap-1 rounded-[4px] border border-default p-1" data-testid={`query-join-${i + 1}`}>
            <div className="flex flex-wrap items-center gap-2">
              <select
                aria-label={`${label} join ${i + 1} kind`}
                className={`${cellSelect} w-20 font-sans`}
                value={kind}
                onChange={(e) =>
                  update((b) => {
                    const target = b.joins![i];
                    if (e.target.value === "inner") delete target.kind;
                    else target.kind = e.target.value as JoinKind;
                    if (target.kind === "cross") delete target.on;
                  })
                }
              >
                {JOIN_KINDS.map((k) => (
                  <option key={k} value={k}>
                    {k} join
                  </option>
                ))}
              </select>
              {sourceSelect(j.source, `${label} join ${i + 1} source`, (v) =>
                update((b) => {
                  const before = findSource(q.view, b.joins![i].source, q.files, named);
                  const after = findSource(q.view, v, q.files, named);
                  b.joins![i] = { ...b.joins![i], source: v };
                  // As for the from source: the columns read through this join's alias follow to the same-named column of the new source.
                  if (before && after) retargetColumns(b, aliases[i + 1], columnOptions(before), columnOptions(after));
                }),
              )}
              <span className="text-12 text-secondary">as</span>
              {aliasInput(i + 1, `${label} join ${i + 1} alias`)}
              {kind !== "cross" ? (
                candidates.length > 1 ? (
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <Button size="sm" variant="ghost" title="Fill the condition from a foreign key between this source and one before it">
                        <KeyRound /> Join by foreign key
                      </Button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="start">
                      {candidates.map((c) => (
                        <DropdownMenuItem key={c.label} onSelect={() => setOn(c.on)}>
                          {c.label}
                        </DropdownMenuItem>
                      ))}
                    </DropdownMenuContent>
                  </DropdownMenu>
                ) : (
                  <Button
                    size="sm"
                    variant="ghost"
                    disabled={!candidates.length}
                    title={
                      candidates.length
                        ? `Fill the condition from the foreign key: ${candidates[0].label}`
                        : "No foreign key links this source with the ones before it"
                    }
                    onClick={() => setOn(candidates[0].on)}
                  >
                    <KeyRound /> Join by foreign key
                  </Button>
                )
              ) : null}
              <span className="flex-1" />
              <Button
                size="icon-row"
                variant="ghost"
                label={`Remove join ${i + 1}`}
                onClick={() =>
                  update((b) => {
                    b.joins = b.joins!.filter((_, x) => x !== i);
                    tidy(b);
                  })
                }
              >
                <Trash2 />
              </Button>
            </div>
            {kind !== "cross" ? (
              <div className="ml-2 flex items-start gap-2">
                <span className="w-6 pt-1 text-12 text-secondary">on</span>
                <PredicateEditor value={j.on} env={env} label={`${label} join ${i + 1} on`} empty="No condition yet." onChange={setOn} />
              </div>
            ) : null}
          </div>
        );
      })}
      <Problem text={problem} />
      <Button size="sm" variant="ghost" className="self-start" onClick={addJoin} disabled={!options.length} data-testid="query-add-join">
        <Plus /> Add join
      </Button>
    </div>
  );
}

// ------------------------------------------------------------------ select

/** The select list: the fields of the row, each with its name, expression and, when the expression does not say, its type. */
export function SelectSection({
  body,
  update,
  q,
  label,
  resolved,
  renameField,
}: {
  body: Subquery;
  update: BodyUpdate;
  q: QueryContext;
  label: string;
  /** The resolved fields (the database view's), for their inferred types. */
  resolved?: QueryView["select"];
  /** Renames a named field and the references to it (a named query's fields are columns other sources read). */
  renameField?: (index: number, to: string) => void;
}) {
  const select = body.select ?? [];
  const [problem, setProblem] = useState<string | null>(null);
  const scope = scopeOf(body, q.view, q.files, q.outer, q.named);
  const env: QueryEnv = { ...q.env, scope };
  const own = scope.filter((s) => !s.outer);
  const typeOf = (f: Field) => {
    const r = resolved?.find((x) => x.name === f.name);
    return r ? `${r.type ?? "?"}${r.nullable ? "?" : ""}` : "";
  };
  const setField = (i: number, field: Field) => update((b) => void (b.select = (b.select ?? []).map((f, j) => (j === i ? field : f))));
  const removeField = (i: number) => update((b) => void (b.select = (b.select ?? []).filter((_, j) => j !== i)));
  const extra = select.map((f, i) => ({ f, i }));
  const addField = () =>
    update((b) => {
      const taken = (b.select ?? []).map((f) => f.name ?? "");
      const first = firstColumn(env);
      const column = first ? own.flatMap((s) => s.columns.map((c) => ({ s, c }))).find(({ s, c }) => `${s.alias}.${c.ref}` === first) : undefined;
      b.select = [...(b.select ?? []), { name: fieldName(column?.c.name ?? "value", taken), expression: first ? { column: first } : { value: 1 } }];
    });
  return (
    <div className="flex flex-col gap-2" data-testid="query-select">
      <div className="flex flex-wrap items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          Each field is a member of the row: its name, its expression and, when the expression does not say, its type.
        </p>
        <Button size="sm" variant="ghost" onClick={addField} title="Add a field to the row">
          <Plus /> Add field
        </Button>
      </div>
      {extra.length ? (
        <table className="w-full table-fixed border-collapse text-12" aria-label={`${label} fields`}>
          <thead>
            <tr>
              <th scope="col" className={`${head} w-40`}>
                Name
              </th>
              <th scope="col" className={head}>
                Expression
              </th>
              <th scope="col" className={`${head} w-28`}>
                Declared type
              </th>
              <th scope="col" className={`${head} w-20`}>
                Nulls
              </th>
              <th scope="col" className={`${head} w-24`}>
                Type
              </th>
              <th scope="col" className="w-[4.5rem]">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {extra.map(({ f, i }, n) => {
              const name = f.name ?? String(i + 1);
              return (
                <tr key={i} className="border-t border-default" data-testid={`query-field-${name}`}>
                  <td className={cell}>
                    <CommitInput
                      label={`Name of field ${n + 1}`}
                      mono
                      className="h-6 text-12"
                      value={f.name ?? ""}
                      onCommit={(v) => {
                        const next = v.trim();
                        const why = nameProblem(
                          next,
                          select.filter((_, j) => j !== i).map((x) => x.name ?? ""),
                          "field",
                        );
                        setProblem(why);
                        if (!why) {
                          if (renameField && f.name) renameField(i, next);
                          else setField(i, { ...f, name: next });
                        }
                      }}
                    />
                  </td>
                  <td className={cell}>
                    <ExpressionEditor value={f.expression} env={env} label={`Field ${name}`} onChange={(e) => setField(i, { ...f, expression: e })} />
                  </td>
                  <td className={cell}>
                    <select
                      aria-label={`Declared type of field ${name}`}
                      className={`${cellSelect} w-full`}
                      value={f.type ?? ""}
                      onChange={(e) => {
                        const next = { ...f };
                        if (e.target.value) next.type = e.target.value;
                        else delete next.type;
                        setField(i, next);
                      }}
                    >
                      <option value="">(inferred)</option>
                      {BUILTIN_TYPES.map((t) => (
                        <option key={t} value={t}>
                          {t}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td className={cell}>
                    <select
                      aria-label={`Nulls of field ${name}`}
                      className={`${cellSelect} w-full font-sans`}
                      value={f.nullable === undefined ? "" : String(f.nullable)}
                      onChange={(e) => {
                        const next = { ...f };
                        if (e.target.value === "") delete next.nullable;
                        else next.nullable = e.target.value === "true";
                        setField(i, next);
                      }}
                    >
                      <option value="">(inferred)</option>
                      <option value="true">may be null</option>
                      <option value="false">never null</option>
                    </select>
                  </td>
                  <td className={`${cell} pt-1 font-mono text-11 text-secondary`}>{typeOf(f)}</td>
                  <td className={`${cell} whitespace-nowrap`}>
                    <MoveButtons
                      noun={`field ${name}`}
                      first={n === 0}
                      last={n === extra.length - 1}
                      move={(by) =>
                        update((b) => {
                          const list = [...(b.select ?? [])];
                          const to = extra[n + by]?.i;
                          if (to === undefined) return;
                          [list[i], list[to]] = [list[to], list[i]];
                          b.select = list;
                        })
                      }
                    />
                    <Button size="icon-row" variant="ghost" label={`Remove field ${name}`} onClick={() => removeField(i)}>
                      <Trash2 />
                    </Button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      ) : (
        <EmptyState title="No fields">Add a field: each is a member of the result row.</EmptyState>
      )}
      <Problem text={problem} />
    </div>
  );
}

function MoveButtons({ noun, first, last, move }: { noun: string; first: boolean; last: boolean; move: (by: -1 | 1) => void }) {
  return (
    <>
      <Button size="icon-row" variant="ghost" label={`Move ${noun} up`} disabled={first} onClick={() => move(-1)}>
        <ArrowUp />
      </Button>
      <Button size="icon-row" variant="ghost" label={`Move ${noun} down`} disabled={last} onClick={() => move(1)}>
        <ArrowDown />
      </Button>
    </>
  );
}

// ------------------------------------------------------------------ filter

export function FilterSection({ body, update, q, label }: { body: Subquery; update: BodyUpdate; q: QueryContext; label: string }) {
  const env: QueryEnv = { ...q.env, scope: scopeOf(body, q.view, q.files, q.outer, q.named) };
  return (
    <div className="flex flex-col gap-1" data-testid="query-filter">
      <p className="text-12 text-secondary">
        The rows the query keeps. A comparison takes a column, a parameter, a value, a function call or SQL per dialect on each side
        {q.outer.length ? "; the aliases of the query around this one tie its rows to the parent's (equalities at the top)" : ""}.
      </p>
      <PredicateEditor
        value={body.where}
        env={env}
        label={label}
        empty="No condition: every row."
        onChange={(next) =>
          update((b) => {
            if (next) b.where = next;
            else delete b.where;
          })
        }
      />
    </div>
  );
}

// ------------------------------------------------------------------ group and order

const PAGING_LABELS = { offset: "Offset (rows to skip)", limit: "Limit (most rows)" } as const;

export function GroupOrderSection({
  body,
  update,
  q,
  label,
  paging,
}: {
  body: Subquery;
  update: BodyUpdate;
  q: QueryContext;
  label: string;
  /** The query's paging (the top level only), with its own update. */
  paging?: { value: QueryDoc["paging"]; update: (mutate: (doc: QueryDoc) => void) => void };
}) {
  const env: QueryEnv = { ...q.env, scope: scopeOf(body, q.view, q.files, q.outer, q.named) };
  const groupBy = body.groupBy ?? [];
  const orderBy = body.orderBy ?? [];
  const blank = (): Expression => (firstColumn(env) ? { column: firstColumn(env)! } : { value: 1 });
  const setOrder = (i: number, order: Order) => update((b) => void (b.orderBy = (b.orderBy ?? []).map((o, j) => (j === i ? order : o))));
  return (
    <div className="flex flex-col gap-2" data-testid="query-group-order">
      <label className="flex items-center gap-2 text-13">
        <input
          type="checkbox"
          checked={body.distinct === true}
          onChange={(e) =>
            update((b) => {
              if (e.target.checked) b.distinct = true;
              else delete b.distinct;
            })
          }
        />
        Distinct (remove duplicate rows)
      </label>
      <SectionTitle>Group by</SectionTitle>
      {groupBy.map((g, i) => (
        <div key={i} className="flex items-start gap-1" data-testid={`query-group-${i + 1}`}>
          <ExpressionEditor
            value={g}
            env={env}
            label={`${label} group ${i + 1}`}
            onChange={(e) => update((b) => void (b.groupBy = (b.groupBy ?? []).map((x, j) => (j === i ? e : x))))}
          />
          <Button
            size="icon-row"
            variant="ghost"
            label={`Remove grouping ${i + 1}`}
            onClick={() =>
              update((b) => {
                b.groupBy = (b.groupBy ?? []).filter((_, j) => j !== i);
                tidy(b);
              })
            }
          >
            <Trash2 />
          </Button>
        </div>
      ))}
      <Button size="sm" variant="ghost" className="self-start" onClick={() => update((b) => void (b.groupBy = [...(b.groupBy ?? []), blank()]))}>
        <Plus /> Add grouping
      </Button>
      <SectionTitle>Having</SectionTitle>
      <PredicateEditor
        value={body.having}
        env={env}
        label={`${label} having`}
        empty="No condition on the groups."
        onChange={(next) =>
          update((b) => {
            if (next) b.having = next;
            else delete b.having;
          })
        }
      />
      <SectionTitle>Order by</SectionTitle>
      {orderBy.map((o, i) => (
        <div key={i} className="flex flex-wrap items-start gap-1" data-testid={`query-order-${i + 1}`}>
          <ExpressionEditor value={o.expression} env={env} label={`${label} order ${i + 1}`} onChange={(e) => setOrder(i, { ...o, expression: e })} />
          <select
            aria-label={`${label} order ${i + 1} direction`}
            className={`${cellSelect} font-sans`}
            value={o.direction ?? "asc"}
            onChange={(e) => {
              const next = { ...o };
              if (e.target.value === "desc") next.direction = "desc";
              else delete next.direction;
              setOrder(i, next);
            }}
          >
            <option value="asc">ascending</option>
            <option value="desc">descending</option>
          </select>
          <select
            aria-label={`${label} order ${i + 1} nulls`}
            className={`${cellSelect} font-sans`}
            value={o.nulls ?? ""}
            onChange={(e) => {
              const next = { ...o };
              if (e.target.value) next.nulls = e.target.value as "first" | "last";
              else delete next.nulls;
              setOrder(i, next);
            }}
          >
            <option value="">nulls: the database&apos;s order</option>
            <option value="first">nulls first</option>
            <option value="last">nulls last</option>
          </select>
          <MoveButtons
            noun={`order ${i + 1}`}
            first={i === 0}
            last={i === orderBy.length - 1}
            move={(by) =>
              update((b) => {
                const list = [...(b.orderBy ?? [])];
                [list[i], list[i + by]] = [list[i + by], list[i]];
                b.orderBy = list;
              })
            }
          />
          <Button
            size="icon-row"
            variant="ghost"
            label={`Remove order ${i + 1}`}
            onClick={() =>
              update((b) => {
                b.orderBy = (b.orderBy ?? []).filter((_, j) => j !== i);
                tidy(b);
              })
            }
          >
            <Trash2 />
          </Button>
        </div>
      ))}
      <Button
        size="sm"
        variant="ghost"
        className="self-start"
        onClick={() => update((b) => void (b.orderBy = [...(b.orderBy ?? []), { expression: blank() }]))}
        data-testid="query-add-order"
      >
        <Plus /> Add order
      </Button>
      {paging ? (
        <>
          <SectionTitle>Paging</SectionTitle>
          <div className="flex flex-wrap gap-4" data-testid="query-paging">
            {(["offset", "limit"] as const).map((bound) => (
              <PagingBound
                key={bound}
                label={PAGING_LABELS[bound]}
                aria={`Paging ${bound}`}
                value={paging.value?.[bound]}
                env={env}
                onChange={(v) =>
                  paging.update((d) => {
                    const next = { ...(d.paging ?? {}) };
                    if (v === undefined) delete next[bound];
                    else next[bound] = v;
                    if (Object.keys(next).length) d.paging = next;
                    else delete d.paging;
                  })
                }
              />
            ))}
          </div>
        </>
      ) : null}
    </div>
  );
}

/** One paging bound: none, a number, or an integer parameter's name. */
function PagingBound({
  label,
  aria,
  value,
  env,
  onChange,
}: {
  label: string;
  aria: string;
  value: string | number | undefined;
  env: QueryEnv;
  onChange: (v: string | number | undefined) => void;
}) {
  const mode = value === undefined ? "none" : typeof value === "number" ? "number" : "param";
  const integers = env.parameters.filter((p) => ["int16", "int32", "int64"].includes(p.type) && !p.collection);
  return (
    <div className="flex items-center gap-1">
      <span className="text-12 text-secondary">{label}</span>
      <select
        aria-label={aria}
        className={`${cellSelect} font-sans`}
        value={mode}
        onChange={(e) =>
          onChange(e.target.value === "none" ? undefined : e.target.value === "number" ? (aria.endsWith("limit") ? 50 : 0) : (integers[0]?.name ?? ""))
        }
      >
        <option value="none">none</option>
        <option value="number">a number</option>
        <option value="param" disabled={!integers.length && mode !== "param"}>
          a parameter
        </option>
      </select>
      {mode === "number" ? (
        <CommitInput
          label={`${aria} number`}
          mono
          className="h-6 w-20 text-12"
          value={String(value)}
          onCommit={(t) => {
            if (/^\d+$/.test(t.trim())) onChange(Number(t.trim()));
          }}
        />
      ) : null}
      {mode === "param" ? (
        <ParamSelect value={String(value ?? "")} env={{ ...env, parameters: integers }} label={`${aria} parameter`} onChange={(p) => onChange(p)} />
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ parameters

const FACETS: TypeFacet[] = ["length", "precision", "scale"];
const FACET_LABELS: Record<TypeFacet, string> = { length: "Len", precision: "Prec", scale: "Scale" };
const NUMERIC = new Set(["int16", "int32", "int64", "decimal", "float", "double"]);

/** A default typed in a cell, as the parameter's type takes it: a number, true or false, else the text; empty clears it. */
export function parameterDefault(type: string, text: string): string | number | boolean | undefined {
  const t = text.trim();
  if (!t) return undefined;
  if (NUMERIC.has(type) && /^-?\d+(\.\d+)?$/.test(t)) return Number(t);
  if (type === "bool" && (t === "true" || t === "false")) return t === "true";
  return text;
}

/** The parameters grid: name (a rename follows every use), type and facets, list, default and description. */
export function ParametersSection({
  doc,
  update,
  findings = [],
  dialect,
}: {
  doc: QueryDoc;
  update: (mutate: (d: QueryDoc) => void) => void;
  /** The query's findings: those at `/parameters/<i>` show in that parameter's row (a hand-written query's MQ4096). */
  findings?: readonly Diagnostic[];
  /** The database's dialect, for a query written as SQL text: renaming a parameter rewrites its `:name` in the texts. */
  dialect?: string;
}) {
  const parameters = doc.parameters ?? [];
  const [problem, setProblem] = useState<string | null>(null);
  const types = useDatabaseTypes(doc.database);
  const setParameter = (i: number, change: (p: QueryParameter) => void) =>
    update((d) => {
      const list = d.parameters ?? [];
      change(list[i]);
    });
  const add = () =>
    update((d) => {
      const names = new Set((d.parameters ?? []).map((p) => p.name));
      let n = (d.parameters ?? []).length + 1;
      while (names.has(`param${n}`)) n++;
      d.parameters = [...(d.parameters ?? []), { name: `param${n}`, type: "string" }];
    });
  return (
    <div className="flex flex-col gap-2" data-testid="query-parameters">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">
          The values the caller passes, by name: a built-in type or a database type of this database; a list (for in and not in); the default the generated
          method uses when the caller passes none.
        </p>
        <Button size="sm" variant="ghost" onClick={add} data-testid="query-add-parameter">
          <Plus /> Add parameter
        </Button>
      </div>
      <Problem text={problem} />
      {parameters.length ? (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[44rem] table-fixed border-collapse text-12">
            <thead>
              <tr>
                <th scope="col" className={`${head} w-32`}>
                  Name
                </th>
                <th scope="col" className={`${head} w-32`}>
                  Type
                </th>
                {FACETS.map((f) => (
                  <th key={f} scope="col" className={`${head} w-12`}>
                    {FACET_LABELS[f]}
                  </th>
                ))}
                <th scope="col" className={`${head} w-10`}>
                  List
                </th>
                <th scope="col" className={`${head} w-24`}>
                  Default
                </th>
                <th scope="col" className={head}>
                  Description
                </th>
                <th scope="col" className="w-[4.5rem]">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {parameters.map((p, i) => (
                <Fragment key={i}>
                  <tr className="border-t border-default" data-testid={`query-parameter-${p.name}`}>
                    <td className={cell}>
                      <CommitInput
                        label={`Name of parameter ${i + 1}`}
                        mono
                        className="h-6 text-12"
                        value={p.name}
                        onCommit={(v) => {
                          const next = v.trim();
                          const why = nameProblem(
                            next,
                            parameters.filter((_, j) => j !== i).map((x) => x.name),
                            "parameter",
                          );
                          setProblem(why);
                          if (!why) update((d) => void renameParameter(d, p.name, next, dialect));
                        }}
                      />
                    </td>
                    <td className={cell}>
                      <TypeSelect
                        label={`Type of parameter ${p.name}`}
                        value={p.type}
                        types={types}
                        allowNative={false}
                        onChange={(v) => v && setParameter(i, (x) => void (x.type = v))}
                      />
                    </td>
                    {FACETS.map((f) => (
                      <td key={f} className={cell}>
                        <CommitInput
                          label={`${f[0].toUpperCase()}${f.slice(1)} of parameter ${p.name}`}
                          mono
                          className="h-6 text-12"
                          value={typeof p[f] === "number" ? String(p[f]) : ""}
                          onCommit={(v) => {
                            const why = facetProblem(f, v);
                            setProblem(why ? `${f[0].toUpperCase()}${f.slice(1)}: ${why}` : null);
                            if (!why) setParameter(i, (x) => void setFacet(x as unknown as Record<string, unknown>, f, v));
                          }}
                        />
                      </td>
                    ))}
                    <td className={cell}>
                      <input
                        type="checkbox"
                        aria-label={`Parameter ${p.name} is a list`}
                        checked={p.collection === true}
                        onChange={(e) =>
                          setParameter(i, (x) => {
                            if (e.target.checked) x.collection = true;
                            else delete x.collection;
                          })
                        }
                      />
                    </td>
                    <td className={cell}>
                      <CommitInput
                        label={`Default of parameter ${p.name}`}
                        mono
                        className="h-6 text-12"
                        value={p.default === undefined ? "" : String(p.default)}
                        placeholder="none"
                        onCommit={(v) =>
                          setParameter(i, (x) => {
                            const value = parameterDefault(x.type, v);
                            if (value === undefined) delete x.default;
                            else x.default = value;
                          })
                        }
                      />
                    </td>
                    <td className={cell}>
                      <CommitInput
                        label={`Description of parameter ${p.name}`}
                        className="h-6 text-12"
                        value={p.description ?? ""}
                        onCommit={(v) =>
                          setParameter(i, (x) => {
                            if (v.trim()) x.description = v.trim();
                            else delete x.description;
                          })
                        }
                      />
                    </td>
                    <td className={`${cell} whitespace-nowrap`}>
                      <MoveButtons
                        noun={`parameter ${p.name}`}
                        first={i === 0}
                        last={i === parameters.length - 1}
                        move={(by) =>
                          update((d) => {
                            const list = [...(d.parameters ?? [])];
                            [list[i], list[i + by]] = [list[i + by], list[i]];
                            d.parameters = list;
                          })
                        }
                      />
                      <Button
                        size="icon-row"
                        variant="ghost"
                        label={`Delete parameter ${p.name}`}
                        onClick={() =>
                          update((d) => {
                            d.parameters = (d.parameters ?? []).filter((_, j) => j !== i);
                            if (!d.parameters.length) delete d.parameters;
                          })
                        }
                      >
                        <Trash2 />
                      </Button>
                    </td>
                  </tr>
                  {findings
                    .filter((d) => d.jsonPointer === `/parameters/${i}` || (d.jsonPointer ?? "").startsWith(`/parameters/${i}/`))
                    .map((d, j) => (
                      <tr key={j} data-testid={`query-parameter-finding-${p.name}`}>
                        <td colSpan={FACETS.length + 5} className={`px-1 pb-0.5 text-12 ${d.severity === "error" ? "text-danger" : "text-secondary"}`}>
                          <span className="font-mono">{d.rule}</span> {d.message}
                        </td>
                      </tr>
                    ))}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <EmptyState title="No parameters">The query takes no values from its caller.</EmptyState>
      )}
    </div>
  );
}
