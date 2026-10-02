// The condition editor of the query editor (query.json $defs/predicate): a tree of groups (all of: and, any of: or), negations,
// comparisons (left, operator, right: nothing, one value, a list or the two bounds of between) and exists conditions. An exists
// condition's nested query is edited as JSON for now (its own sources, joins and where). Every gesture hands the whole new tree
// to `onChange` (undefined: no condition), which the editor saves at once: one gesture, one undo step.
import { Ban, FolderPlus, Plus, SearchCheck, Trash2, Undo2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  and as allOf,
  COMPARISON_LABELS,
  COMPARISON_OPS,
  compare,
  expressionProblem,
  literal,
  not,
  param,
  predicateKind,
  predicateProblem,
  rightShape,
  rightValues,
  withComparison,
  type ComparisonOp,
  type Expression,
  type Predicate,
  type Subquery,
} from "@/model/queryTree";
import { cellSelect, ExpressionEditor, firstColumn, JsonCommit, type QueryEnv } from "./queryExpression";

/** A new comparison: the first column in scope equals the first parameter (or an empty text). */
export function newComparison(env: QueryEnv): Predicate {
  const left: Expression = firstColumn(env) ? { column: firstColumn(env)! } : literal("");
  return compare("eq", left, env.parameters[0] ? param(env.parameters[0].name) : literal(""));
}

/** A new exists condition: a nested query over the first source in scope. */
function newExists(env: QueryEnv): Predicate {
  const first = env.scope.find((s) => !s.outer);
  const sub: Subquery = { from: { source: first?.source?.value ?? "", alias: `${first?.alias ?? "x"}2` } };
  return { exists: sub };
}

/** Appends a condition: to the root group, or by grouping the root with it (all of). */
function appended(root: Predicate | undefined, item: Predicate): Predicate {
  if (!root) return item;
  const kind = predicateKind(root);
  if (kind === "and") return { and: [...root.and!, item] };
  if (kind === "or") return { or: [...root.or!, item] };
  return allOf(root, item);
}

/** A whole condition (a where, a having, a join's on): the tree, and the buttons that add to it. */
export function PredicateEditor({
  value,
  onChange,
  env,
  label,
  empty = "No condition: every row.",
}: {
  value: Predicate | undefined;
  onChange: (next: Predicate | undefined) => void;
  env: QueryEnv;
  /** The accessible name the controls build on ("Where", "Having", "Join 1 on"). */
  label: string;
  empty?: string;
}) {
  const kind = predicateKind(value);
  const group = kind === "and" || kind === "or";
  return (
    <div className="flex min-w-0 flex-col gap-1" data-testid="query-predicate" aria-label={label} role="group">
      {value ? (
        <PredicateNode value={value} onChange={onChange} onRemove={() => onChange(undefined)} env={env} label={label} />
      ) : (
        <p className="text-12 text-secondary">{empty}</p>
      )}
      {group ? null : (
        <div className="flex flex-wrap gap-1">
          <Button size="sm" variant="ghost" onClick={() => onChange(appended(value, newComparison(env)))} title={`Add a comparison to ${label}`}>
            <Plus /> Add condition
          </Button>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onChange(appended(value, { or: [newComparison(env)] }))}
            title={`Add a group of conditions, any of which holds, to ${label}`}
          >
            <FolderPlus /> Add group
          </Button>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => onChange(appended(value, newExists(env)))}
            title={`Add an exists condition (a nested query) to ${label}`}
          >
            <SearchCheck /> Add exists
          </Button>
        </div>
      )}
    </div>
  );
}

function PredicateNode({
  value,
  onChange,
  onRemove,
  env,
  label,
}: {
  value: Predicate;
  onChange: (next: Predicate) => void;
  onRemove: () => void;
  env: QueryEnv;
  label: string;
}) {
  const kind = predicateKind(value);
  if (kind === "and" || kind === "or") {
    const items = (value.and ?? value.or)!;
    const set = (next: Predicate[]) => (next.length ? onChange(kind === "and" ? { and: next } : { or: next }) : onRemove());
    return (
      <div className="flex min-w-0 flex-col gap-1 rounded-[4px] border border-default p-1" data-testid="query-predicate-group">
        <div className="flex flex-wrap items-center gap-1">
          <select
            aria-label={`${label}: group`}
            className={`${cellSelect} font-sans`}
            value={kind}
            onChange={(e) => onChange(e.target.value === "and" ? { and: items } : { or: items })}
          >
            <option value="and">All of (and)</option>
            <option value="or">Any of (or)</option>
          </select>
          <Button size="sm" variant="ghost" onClick={() => set([...items, newComparison(env)])} title={`Add a comparison to ${label}`}>
            <Plus /> Add condition
          </Button>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => set([...items, { [kind === "and" ? "or" : "and"]: [newComparison(env)] } as Predicate])}
            title={`Add a nested group to ${label}`}
          >
            <FolderPlus /> Add group
          </Button>
          <Button size="sm" variant="ghost" onClick={() => set([...items, newExists(env)])} title={`Add an exists condition (a nested query) to ${label}`}>
            <SearchCheck /> Add exists
          </Button>
          <span className="flex-1" />
          <Button size="icon-row" variant="ghost" label={`Negate ${label}`} onClick={() => onChange(not(value))}>
            <Ban />
          </Button>
          <Button size="icon-row" variant="ghost" label={`Remove ${label}`} onClick={onRemove}>
            <Trash2 />
          </Button>
        </div>
        <div className="ml-2 flex flex-col gap-1">
          {items.map((item, i) => (
            <PredicateNode
              key={i}
              value={item}
              env={env}
              label={`${label} ${i + 1}`}
              onChange={(next) => set(items.map((x, j) => (j === i ? next : x)))}
              onRemove={() => set(items.filter((_, j) => j !== i))}
            />
          ))}
        </div>
      </div>
    );
  }
  if (kind === "not")
    return (
      <div className="flex min-w-0 flex-col gap-1 rounded-[4px] border border-dashed border-default p-1" data-testid="query-predicate-not">
        <div className="flex items-center gap-1">
          <span className="text-12 font-medium text-secondary">Not</span>
          <span className="flex-1" />
          <Button size="icon-row" variant="ghost" label={`Remove the negation of ${label}`} onClick={() => onChange(value.not!)}>
            <Undo2 />
          </Button>
          <Button size="icon-row" variant="ghost" label={`Remove ${label}`} onClick={onRemove}>
            <Trash2 />
          </Button>
        </div>
        <div className="ml-2">
          <PredicateNode value={value.not!} env={env} label={`${label} negated`} onChange={(next) => onChange(not(next))} onRemove={onRemove} />
        </div>
      </div>
    );
  if (kind === "exists")
    return (
      <div className="flex min-w-0 flex-col gap-1 rounded-[4px] border border-default p-1" data-testid="query-predicate-exists">
        <div className="flex items-center gap-1">
          <span
            className="text-12 font-medium text-secondary"
            title="A nested query (its from, joins and where, as JSON for now) that may name the aliases around it"
          >
            Exists (a nested query, as JSON)
          </span>
          <span className="flex-1" />
          <Button size="icon-row" variant="ghost" label={`Negate ${label}`} onClick={() => onChange(not(value))}>
            <Ban />
          </Button>
          <Button size="icon-row" variant="ghost" label={`Remove ${label}`} onClick={onRemove}>
            <Trash2 />
          </Button>
        </div>
        <JsonCommit value={value.exists!} label={`${label}: nested query as JSON`} rows={8} onCommit={(sub) => onChange({ exists: sub })} />
      </div>
    );
  return <ComparisonRow value={value} onChange={onChange} onRemove={onRemove} env={env} label={label} />;
}

/** One comparison: left, operator and the right side its operator takes. */
function ComparisonRow({
  value,
  onChange,
  onRemove,
  env,
  label,
}: {
  value: Predicate;
  onChange: (next: Predicate) => void;
  onRemove: () => void;
  env: QueryEnv;
  label: string;
}) {
  const op = value.op ?? "eq";
  const shape = rightShape(op);
  const values = rightValues(value);
  const problem = predicateProblem(value);
  const fill = () => (env.parameters[0] ? param(env.parameters[0].name) : literal(""));
  const setValues = (next: Expression[]) => onChange(compare(op, value.left ?? literal(""), next));
  // A cell's own problem shows under the cell; this line says what is wrong with the comparison's shape.
  const shown = problem && ![value.left, ...values].some((x) => x && expressionProblem(x) === problem) ? problem : null;
  return (
    <div className="flex min-w-0 flex-col gap-0.5" data-testid="query-comparison">
      <div className="flex min-w-0 items-start gap-1">
        <div className="flex min-w-0 flex-1 flex-wrap items-start gap-1">
          <ExpressionEditor value={value.left} env={env} label={`${label} left`} onChange={(left) => onChange({ ...value, left })} />
          <select
            aria-label={`${label} operator`}
            className={`${cellSelect} font-sans`}
            value={op}
            onChange={(e) => onChange(withComparison(value, e.target.value as ComparisonOp, fill))}
          >
            {COMPARISON_OPS.map((o) => (
              <option key={o} value={o}>
                {COMPARISON_LABELS[o]}
              </option>
            ))}
          </select>
          {shape === "one" ? (
            <ExpressionEditor value={values[0]} env={env} label={`${label} right`} onChange={(right) => onChange({ ...value, right })} />
          ) : null}
          {shape === "two" ? (
            <>
              <ExpressionEditor
                value={values[0]}
                env={env}
                label={`${label} low`}
                onChange={(low) => onChange({ ...value, right: [low, values[1] ?? fill()] })}
              />
              <span className="text-12 text-secondary">and</span>
              <ExpressionEditor
                value={values[1]}
                env={env}
                label={`${label} high`}
                onChange={(high) => onChange({ ...value, right: [values[0] ?? fill(), high] })}
              />
            </>
          ) : null}
          {shape === "list" ? (
            <div className="flex min-w-0 flex-col gap-0.5">
              {values.map((v, i) => (
                <div key={i} className="flex items-start gap-1">
                  <ExpressionEditor
                    value={v}
                    env={env}
                    label={`${label} value ${i + 1}`}
                    onChange={(x) => setValues(values.map((y, j) => (j === i ? x : y)))}
                  />
                  <Button
                    size="icon-row"
                    variant="ghost"
                    label={values.length === 1 ? `${label} keeps at least one value` : `Remove value ${i + 1} of ${label}`}
                    disabled={values.length === 1}
                    onClick={() => setValues(values.filter((_, j) => j !== i))}
                  >
                    <Trash2 />
                  </Button>
                </div>
              ))}
              <Button
                size="sm"
                variant="ghost"
                className="self-start"
                onClick={() => setValues([...values, literal("")])}
                title={`Add a value to the list of ${label}`}
              >
                <Plus /> Add value
              </Button>
            </div>
          ) : null}
        </div>
        <div className="flex shrink-0 gap-0.5 pt-0.5">
          <Button size="icon-row" variant="ghost" label={`Negate ${label}`} onClick={() => onChange(not(value))}>
            <Ban />
          </Button>
          <Button size="icon-row" variant="ghost" label={`Remove ${label}`} onClick={onRemove}>
            <Trash2 />
          </Button>
        </div>
      </div>
      {shown ? <p className="text-11 text-danger">{shown}</p> : null}
    </div>
  );
}
