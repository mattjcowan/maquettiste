// The query editor (schemas/v1/query.json; engine-design.md section 7, "Queries"): General (the common fields and the result
// entity), Sources, Select, Filter, Group and order, Parameters, Collections, SQL (the statements the engine renders for a dialect,
// with what stops them), JSON, Code generation and References. The trees are edited as data, never as SQL text; every gesture is
// one edit of the document and one save, so one undo step.
import { useMemo, useState, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { CircleAlert } from "lucide-react";
import { useDatabaseView, useElements, useIndex, useQuerySql } from "@/api/queries";
import type { DatabaseDoc, Diagnostic, ModelJson, StereotypeDoc } from "@/api/types";
import { CodeView } from "@/code";
import { Field, Select } from "@/components/ui/input";
import { EmptyState, Spinner } from "@/components/ui/misc";
import { CommonFields, DIALECTS, useVocabularies } from "@/inspector/fields";
import { JsonTab, References } from "@/inspector/Inspector";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { scopeOf, setResultEntity, tidy, type QueryDoc } from "@/model/queryTree";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { DatabaseLine } from "./fields";
import { FunctionList, type QueryEnv } from "./queryExpression";
import {
  CollectionsSection,
  FilterSection,
  GroupOrderSection,
  loadEntityShape,
  ParametersSection,
  SelectSection,
  SourcesSection,
  tableFiles,
  useEntityShape,
  type BodyUpdate,
  type QueryContext,
} from "./QueryParts";

export const QUERY_EDITOR_TABS = {
  general: "General",
  sources: "Sources",
  select: "Select",
  filter: "Filter",
  group: "Group and order",
  parameters: "Parameters",
  collections: "Collections",
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
  const files = useMemo(() => tableFiles(index.data, database), [index.data, database]);
  const routines = useMemo(
    () => (index.data ?? []).filter((r) => r.kind === "routine" && r.database === database).map((r) => ({ id: r.id, name: r.name })),
    [index.data, database],
  );
  const dialect = view?.dialect ?? dbDoc?.dialect ?? "postgresql";
  const env: QueryEnv = { scope: scopeOf(doc, view, files), parameters: doc.parameters ?? [], routines, dialect };
  const q: QueryContext = { view, files, env, outer: [] };
  const resolved = view?.queries?.find((x) => x.id === ctx.id);
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
              <ResultEntityField doc={doc} id={form.id} update={update} />
            </div>
          ),
        },
        { value: "sources", label: QUERY_EDITOR_TABS.sources, content: withFunctions(<SourcesSection body={doc} update={bodyUpdate} q={q} label="Query" />) },
        {
          value: "select",
          label: QUERY_EDITOR_TABS.select,
          content: withFunctions(<SelectSection body={doc} update={bodyUpdate} q={q} entity={doc.entity ?? null} label="Query" resolved={resolved?.select} />),
        },
        { value: "filter", label: QUERY_EDITOR_TABS.filter, content: withFunctions(<FilterSection body={doc} update={bodyUpdate} q={q} label="Where" />) },
        {
          value: "group",
          label: QUERY_EDITOR_TABS.group,
          content: withFunctions(<GroupOrderSection body={doc} update={bodyUpdate} q={q} label="Query" paging={{ value: doc.paging, update }} />),
        },
        { value: "parameters", label: QUERY_EDITOR_TABS.parameters, content: <ParametersSection doc={doc} update={update} /> },
        {
          value: "collections",
          label: QUERY_EDITOR_TABS.collections,
          content: withFunctions(<CollectionsSection doc={doc} update={update} q={q} resolved={resolved?.collections} />),
        },
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

/**
 * The result entity: each row has its shape; none: the select list is the row. A field naming what the new entity does not have
 * becomes a named field (its own name, else the old attribute's) with its expression, so nothing is lost (setResultEntity).
 */
function ResultEntityField({ doc, id, update }: { doc: QueryDoc; id: string; update: (mutate: (d: QueryDoc) => void) => void }) {
  const index = useIndex();
  const qc = useQueryClient();
  const vocab = useVocabularies("entity");
  const entities = useMemo(() => (index.data ?? []).filter((r) => r.kind === "entity").sort((a, b) => a.name.localeCompare(b.name)), [index.data]);
  const shape = useEntityShape(doc.entity);
  const choose = async (next: string | null) => {
    const target = next ? await loadEntityShape(qc, next, index.data, vocab.allStereotypes as StereotypeDoc[]) : null;
    const has = new Set([...(target?.attributes.map((a) => a.id) ?? []), ...(target?.fills.map((f) => f.id) ?? [])]);
    const name = (a: string) => shape.attributes.find((x) => x.id === a)?.name ?? shape.fills.find((x) => x.id === a)?.fieldName;
    update((d) => setResultEntity(d, next, name, (a) => has.has(a)));
  };
  return (
    <Field
      label="Result entity"
      htmlFor={`${id}-entity`}
      hint={
        doc.entity
          ? "Each row has this entity's shape: the Select tab fills its attributes."
          : `No entity: each row is the select list, an ad hoc shape of ${doc.select.length} ${doc.select.length === 1 ? "field" : "fields"}.`
      }
    >
      <Select id={`${id}-entity`} value={doc.entity ?? ""} onChange={(e) => void choose(e.target.value || null)}>
        <option value="">(none: an ad hoc row)</option>
        {entities.map((e) => (
          <option key={e.id} value={e.id}>
            {e.name}
          </option>
        ))}
      </Select>
    </Field>
  );
}

/** The SQL tab: the query's statement and its collections' for a dialect (the database's first), and what stops them. */
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
          <div className="flex flex-col gap-2">
            <div className="h-40 rounded-control border border-default" data-testid="query-sql-statement">
              <CodeView language="sql" readOnly label={`SQL of ${name}`} value={preview.sql} />
            </div>
            {preview.collections.map((c) => (
              <section key={c.name} className="flex flex-col gap-1" aria-label={`Collection ${c.name}`} data-testid={`query-sql-collection-${c.name}`}>
                <p className="text-12 text-secondary">
                  Collection <span className="font-medium text-primary">{c.name}</span>: run once for all the parent rows, with{" "}
                  {c.keys.map((k) => `@${k.parameter} (the parents' ${k.hidden ? `hidden column ${k.parentField}` : k.parentField} values)`).join(", ")}.
                </p>
                <div className="h-32 rounded-control border border-default">
                  <CodeView language="sql" readOnly label={`SQL of collection ${c.name}`} value={c.sql} />
                </div>
              </section>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
