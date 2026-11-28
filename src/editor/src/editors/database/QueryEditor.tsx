// The query editor (schemas/v1/query.json; engine-design.md section 7, "Queries"): General (the common fields), Sources, Select, Filter, Group and order, Parameters, SQL (the statement the engine renders for a dialect,
// with what stops it), JSON, Code generation and References. The trees are edited as data, never as SQL text; every gesture is
// one edit of the document and one save, so one undo step. A query is the database's: it knows no entities (the entity side binds
// an entity to a query on the entity's Storage tab).
import { useMemo, useState, type ReactNode } from "react";
import { CircleAlert } from "lucide-react";
import { useDatabaseView, useElements, useIndex, useQuerySql } from "@/api/queries";
import type { DatabaseDoc, Diagnostic, ModelJson } from "@/api/types";
import { CodeView } from "@/code";
import { Select } from "@/components/ui/input";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { CommonFields, DIALECTS } from "@/inspector/fields";
import { JsonTab, References } from "@/inspector/Inspector";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { isSqlTextQuery } from "@/model/sqlQuery";
import { useProblems } from "@/editors/process/shared";
import { namedOptions, scopeOf, tidy, type QueryDoc } from "@/model/queryTree";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { DatabaseLine } from "./fields";
import { SqlColumnsTab, SqlTablesTab, SqlTextTab } from "./SqlQueryParts";
import { NamedQueriesSection } from "./NamedQueries";
import { FunctionList, type QueryEnv } from "./queryExpression";
import { FilterSection, GroupOrderSection, ParametersSection, SelectSection, SourcesSection, type BodyUpdate, type QueryContext } from "./QueryParts";

export const QUERY_EDITOR_TABS = {
  general: "General",
  sources: "Sources",
  named: "Named queries (WITH)",
  select: "Select",
  filter: "Filter",
  group: "Group and order",
  parameters: "Parameters",
  sqltext: "SQL text",
  columns: "Columns",
  tables: "Tables",
  sql: "SQL",
  json: "JSON",
} as const;

export function QueryEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "query");
  if (!ctx) return <>{fallback}</>;
  return <QueryBody ctx={ctx} draft={draft} />;
}

function QueryBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  const doc = ctx.json as unknown as QueryDoc;
  const database = String(doc.database ?? "");
  const dbView = useDatabaseView(database || null);
  const view = dbView.data?.view ?? null;
  const dbDoc = useElements(database ? [database] : []).byId.get(database)?.json as DatabaseDoc | undefined;
  const index = useIndex();
  const routines = useMemo(
    () => (index.data ?? []).filter((r) => r.kind === "routine" && r.database === database).map((r) => ({ id: r.id, name: r.name })),
    [index.data, database],
  );
  const dialect = view?.dialect ?? dbDoc?.dialect ?? "postgresql";
  const resolved = view?.queries?.find((x) => x.id === ctx.id);
  const named = useMemo(() => namedOptions(doc.with, doc.with?.length ?? 0, resolved?.with), [doc.with, resolved?.with]);
  const env: QueryEnv = { scope: scopeOf(doc, view, [], named), parameters: doc.parameters ?? [], routines, dialect };
  const q: QueryContext = { view, env, outer: [], named };
  // One gesture, one save: the edit and its flush together (RoutineEditor's pattern).
  const update = (mutate: (d: QueryDoc) => void) => {
    ctx.edit((j) => {
      const d = j as unknown as QueryDoc;
      mutate(d);
      tidy(d);
    });
    ctx.flush();
  };
  const bodyUpdate: BodyUpdate = (mutate) => update((d) => mutate(d));
  // A query written as SQL text has its own tabs; the findings it gets show on the rows they are about.
  const sqlForm = isSqlTextQuery(ctx.json as unknown as Record<string, unknown>);
  const findings = useProblems(ctx.id, ctx.diagnostics);
  // The function cell's suggestions, once per tab (one tab is mounted at a time).
  const withFunctions = (content: ReactNode) => (
    <>
      <FunctionList env={env} />
      {content}
    </>
  );
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={<DatabaseLine json={ctx.json} />}
      tabs={[
        {
          value: "general",
          label: QUERY_EDITOR_TABS.general,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="query-editor-general">
              <CommonFields {...form} inEditorHeader />
            </div>
          ),
        },
        sqlForm ? { value: "sqltext", label: QUERY_EDITOR_TABS.sqltext, content: <SqlTextTab ctx={ctx} dialect={dialect} findings={findings} /> } : null,
        sqlForm
          ? {
              value: "parameters",
              label: QUERY_EDITOR_TABS.parameters,
              content: <ParametersSection doc={doc} update={update} findings={findings} dialect={dialect} />,
            }
          : null,
        sqlForm ? { value: "columns", label: QUERY_EDITOR_TABS.columns, content: <SqlColumnsTab ctx={ctx} findings={findings} /> } : null,
        sqlForm ? { value: "tables", label: QUERY_EDITOR_TABS.tables, content: <SqlTablesTab ctx={ctx} dialect={dialect} findings={findings} /> } : null,
        sqlForm
          ? null
          : {
              value: "sources",
              label: QUERY_EDITOR_TABS.sources,
              content: withFunctions(<SourcesSection body={doc} update={bodyUpdate} q={q} label="Query" />),
            },
        sqlForm
          ? null
          : {
              value: "named",
              label: QUERY_EDITOR_TABS.named,
              content: withFunctions(<NamedQueriesSection doc={doc} update={update} q={q} findings={findings} resolved={resolved?.with} />),
            },
        sqlForm
          ? null
          : {
              value: "select",
              label: QUERY_EDITOR_TABS.select,
              content: withFunctions(<SelectSection body={doc} update={bodyUpdate} q={q} label="Query" resolved={resolved?.select} />),
            },
        sqlForm
          ? null
          : { value: "filter", label: QUERY_EDITOR_TABS.filter, content: withFunctions(<FilterSection body={doc} update={bodyUpdate} q={q} label="Where" />) },
        sqlForm
          ? null
          : {
              value: "group",
              label: QUERY_EDITOR_TABS.group,
              content: withFunctions(<GroupOrderSection body={doc} update={bodyUpdate} q={q} label="Query" paging={{ value: doc.paging, update }} />),
            },
        sqlForm ? null : { value: "parameters", label: QUERY_EDITOR_TABS.parameters, content: <ParametersSection doc={doc} update={update} /> },
        { value: "sql", label: QUERY_EDITOR_TABS.sql, content: <SqlTab id={ctx.id} name={ctx.name} databaseDialect={dialect} />, fill: true },
        {
          value: "json",
          label: QUERY_EDITOR_TABS.json,
          fill: true,
          content: <JsonTab json={ctx.json} onChange={(next: ModelJson) => ctx.edit(() => next)} />,
        },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}

/** The SQL tab: the query's statement for a dialect (the database's first), and what stops it. */
function SqlTab({ id, name, databaseDialect }: { id: string; name: string; databaseDialect: string }) {
  const [dialect, setDialect] = useState<string | null>(null);
  const shown = dialect ?? databaseDialect;
  const sql = useQuerySql(id, shown === databaseDialect ? null : shown);
  const preview = sql.data?.preview ?? null;
  const diagnostics = (sql.data?.diagnostics ?? []).filter((d) => d.elementId === id || !preview);
  const own = diagnostics.filter((d) => d.elementId === id);
  const listed: Diagnostic[] = own.length ? own : diagnostics.slice(0, 5);
  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="query-sql">
      <div className="flex items-center gap-2 border-b border-default px-2 py-1">
        <label htmlFor={`query-sql-dialect-${id}`} className="text-12 text-secondary">
          Dialect
        </label>
        <Select id={`query-sql-dialect-${id}`} className="w-56" value={shown} onChange={(e) => setDialect(e.target.value)}>
          {DIALECTS.map((d) => (
            <option key={d} value={d}>
              {d === databaseDialect ? `${d} (the database's)` : d}
            </option>
          ))}
        </Select>
        {sql.isFetching ? <Spinner label="Rendering the SQL" /> : null}
        <span className="min-w-0 flex-1 truncate text-right text-12 text-secondary" data-testid="query-sql-parameters">
          {preview ? (preview.parameters.length ? `Parameters: ${preview.parameters.map((p) => `@${p}`).join(", ")}` : "No parameters") : ""}
        </span>
      </div>
      <div className="min-h-0 flex-1 overflow-auto p-2">
        {listed.length ? (
          <ul role="alert" className="mb-2 flex flex-col gap-0.5 text-12" data-testid="query-sql-problems">
            {listed.map((d, i) => (
              <li key={i} className={`flex gap-1 ${d.severity === "error" ? "text-danger" : "text-secondary"}`}>
                <CircleAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
                <span>
                  <span className="font-mono">{d.rule}</span> {d.message}
                  {d.jsonPointer ? <span className="font-mono text-secondary"> at {d.jsonPointer}</span> : null}
                </span>
              </li>
            ))}
          </ul>
        ) : null}
        {sql.isPending ? (
          <Spinner label="Rendering the SQL" />
        ) : !preview ? (
          <EmptyState title={`The SQL of ${name} cannot be rendered`}>
            {sql.error ? String((sql.error as Error).message) : "The model has errors: fix the problems above and it renders again."}
          </EmptyState>
        ) : (
          <div className="h-40 rounded-control border border-default" data-testid="query-sql-statement">
            <CodeView language="sql" readOnly label={`SQL of ${name}`} value={preview.sql} />
          </div>
        )}
      </div>
    </div>
  );
}
