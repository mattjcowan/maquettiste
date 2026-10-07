// The view editor: General (name, schema, comment, the marks and the DDL options: the column list, WITH CHECK OPTION, materialized,
// what the view depends on, and the views its body names, which the scripts order by without being told), Body (one SQL editor per dialect the body holds, with Add
// dialect for the others; a body is saved when its editor loses focus, on Ctrl+S or with Save, one undo step each), Columns (the
// optional declared columns; renaming one renames it in the bindings that read the view by that name, in the same batch), Code
// generation and References.
import { useMemo } from "react";
import { Plus, Trash2 } from "lucide-react";
import { useDatabaseView } from "@/api/queries";
import { CheckboxField } from "@/components/ui/checkbox";
import { SectionTitle } from "@/components/ui/misc";
import { useQueryClient } from "@tanstack/react-query";
import { useServices } from "@/app/context";
import { renameViewColumn } from "@/workspaces/database/columnRename";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/misc";
import { CommonFields } from "@/inspector/fields";
import { References } from "@/inspector/Inspector";
import { BUILTIN_TYPES } from "@/model/model";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { viewBodyTemplate } from "@/explorer/databaseCreate";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { addViewColumn, inferredViewDependencies, removeViewColumn, setViewOption, viewOptionNote, type ViewOption } from "./databaseDocs";
import { DependsOnField } from "./DependsOn";
import { useProblems } from "@/editors/process/shared";
import { DialectBodies } from "./DialectBodies";
import { CommentField, CommitInput, DatabaseLine, SchemaField } from "./fields";

type Rec = Record<string, unknown>;

export const VIEW_EDITOR_TABS = { general: "General", body: "Body", columns: "Columns" } as const;

export function ViewEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "view");
  if (!ctx) return <>{fallback}</>;
  return <ViewBody ctx={ctx} draft={draft} />;
}

function ViewBody({ ctx, draft }: { ctx: EditorContext; draft: Parameters<typeof EditorLayout>[0]["draft"] }) {
  const codeGeneration = useCodeGenerationTab(ctx);
  const form = { ...ctx, id: domIdOf(ctx.id) };
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={<DatabaseLine json={ctx.json} />}
      tabs={[
        {
          value: "general",
          label: VIEW_EDITOR_TABS.general,
          content: (
            <div className="flex max-w-xl flex-col gap-2" data-testid="view-editor-general">
              <CommonFields {...form} inEditorHeader />
              <SchemaField {...form} />
              <CommentField {...form} />
              <ViewDdlOptions ctx={ctx} />
            </div>
          ),
        },
        {
          value: "body",
          label: VIEW_EDITOR_TABS.body,
          content: (
            <DialectBodies
              ctx={ctx}
              member="body"
              prefix="view"
              noun="View"
              intro="The SELECT the view runs, per dialect; a body for any dialect (*) serves the dialects without one of their own."
              template={viewBodyTemplate}
              required
            />
          ),
        },
        { value: "columns", label: VIEW_EDITOR_TABS.columns, content: <ColumnsTab ctx={ctx} /> },
        codeGeneration,
        { value: "references", label: EDITOR_TAB_LABELS.references, content: <References id={ctx.id} /> },
      ]}
    />
  );
}

/** The view's DDL options, its dependencies and the views its body names; each change one save, so one undo step. */
function ViewDdlOptions({ ctx }: { ctx: EditorContext }) {
  const doc = ctx.json as unknown as Rec;
  const id = domIdOf(ctx.id);
  const database = String(doc.database ?? "");
  const view = useDatabaseView(database || null).data?.view;
  const dialect = view?.dialect ?? "postgresql";
  const body = (doc.body as Record<string, string> | undefined) ?? {};
  const text = body[dialect] ?? body["*"] ?? "";
  const views = view?.views;
  const inferred = useMemo(() => inferredViewDependencies(text, views ?? [], ctx.id), [text, views, ctx.id]);
  const chosen = Array.isArray(doc.dependsOn) ? (doc.dependsOn as string[]) : [];
  // The last save's problems, else the model's validation of the view (its warnings too).
  const all = useProblems(ctx.id, ctx.diagnostics);
  const set = (option: ViewOption, on: boolean) => {
    ctx.edit((j) => void setViewOption(j as unknown as Rec, option, on));
    ctx.flush();
  };
  const problems = all.filter((d) =>
    ["/columnList", "/withCheckOption", "/materialized", "/securityInvoker", "/securityBarrier", "/dependsOn"].some((p) => (d.jsonPointer ?? "").startsWith(p)),
  );
  const note = viewOptionNote(dialect, doc);
  const options: [ViewOption, string][] = [
    ["columnList", "Column list (CREATE VIEW names the Columns tab's columns)"],
    ["withCheckOption", "With check option (an insert or update through the view must satisfy its WHERE)"],
    ["materialized", "Materialized (the view stores its rows: PostgreSQL and Oracle)"],
    ["securityInvoker", "Security invoker (reads its tables with the caller's rights, so row-level security applies to the caller)"],
    ["securityBarrier", "Security barrier (its filter runs before the query's functions that are not leakproof)"],
  ];
  return (
    <div className="flex flex-col gap-2" data-testid="view-ddl-options">
      <SectionTitle>DDL</SectionTitle>
      {options.map(([option, label]) => (
        <CheckboxField key={option} id={`${id}-${option}`} label={label} checked={doc[option] === true} onChange={(v) => set(option, v)} />
      ))}
      {note ? (
        <p className="text-11 text-secondary" data-testid="view-ddl-note">
          {note}
        </p>
      ) : null}
      {problems.length ? (
        <ul role="alert" className="flex flex-col gap-0.5 text-12 text-danger" data-testid="view-ddl-problems">
          {problems.map((d, i) => (
            <li key={i} className={d.severity === "error" ? undefined : "text-secondary"}>
              <span className="font-mono">{d.rule}</span> {d.message}
            </li>
          ))}
        </ul>
      ) : null}
      <DependsOnField ctx={ctx} id={id} />
      <div className="flex flex-col gap-1" data-testid="view-inferred-dependencies">
        <span className="text-12 font-medium text-secondary">Found in the body</span>
        {inferred.length ? (
          <ul className="flex flex-wrap gap-1">
            {inferred.map((v) => (
              <li key={v.id} className="rounded-control border border-default px-1 font-mono text-12" data-testid={`view-inferred-${v.name}`}>
                {v.schema ? `${v.schema}.` : ""}
                {v.name}
                {chosen.includes(v.id) ? <span className="ml-1 font-sans text-11 text-secondary">(also named)</span> : null}
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-12 text-secondary">The body names no other view of the database.</p>
        )}
        <p className="text-11 text-secondary">
          Views the body names are created first without being listed under Depends on; list what the body reaches in a way this reading misses.
        </p>
      </div>
    </div>
  );
}

function ColumnsTab({ ctx }: { ctx: EditorContext }) {
  const services = useServices();
  const qc = useQueryClient();
  const doc = ctx.json as unknown as Rec;
  const columns = (Array.isArray(doc.columns) ? doc.columns : []) as Rec[];
  const update = (mutate: (doc: Rec) => void) => {
    ctx.edit((j) => void mutate(j as unknown as Rec));
    ctx.flush();
  };
  const at = (j: Rec, i: number) => (j.columns as Rec[])[i];
  return (
    <div className="flex flex-col gap-2" data-testid="view-columns">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 text-12 text-secondary">Optional: the columns the view returns, for generators that need their names and types.</p>
        <Button size="sm" variant="ghost" onClick={() => update(addViewColumn)} data-testid="view-add-column">
          <Plus /> Add column
        </Button>
      </div>
      {columns.length ? (
        <table className="w-full border-collapse text-12" data-testid="view-columns-grid">
          <thead>
            <tr>
              <th scope="col" className="h-6 px-1 text-left text-11 font-semibold text-secondary">
                Name
              </th>
              <th scope="col" className="h-6 w-36 px-1 text-left text-11 font-semibold text-secondary">
                Type
              </th>
              <th scope="col" className="h-6 w-12 px-1 text-left text-11 font-semibold text-secondary">
                Null
              </th>
              <th scope="col" className="w-7">
                <span className="sr-only">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {columns.map((c, i) => (
              <tr key={i} className="border-t border-default" data-testid={`view-column-${String(c.name ?? i)}`}>
                <td className="h-[var(--mq-row-h)] px-1">
                  <CommitInput
                    label={`Name of column ${i + 1}`}
                    mono
                    className="h-6 text-12"
                    value={String(c.name ?? "")}
                    onCommit={(v) => {
                      // The bindings that read the view by this column's name follow, in the same batch.
                      if (v.trim() && v.trim() !== String(c.name ?? "")) void renameViewColumn(services, qc, ctx.id, i, v);
                    }}
                  />
                </td>
                <td className="px-1">
                  <select
                    aria-label={`Type of column ${String(c.name ?? i + 1)}`}
                    className="h-6 w-full rounded-[4px] border border-input bg-surface px-1 font-mono text-12"
                    value={String(c.type ?? "")}
                    onChange={(e) =>
                      update((j) => {
                        if (e.target.value) at(j, i).type = e.target.value;
                        else delete at(j, i).type;
                      })
                    }
                  >
                    <option value="">(not said)</option>
                    {BUILTIN_TYPES.map((t) => (
                      <option key={t} value={t}>
                        {t}
                      </option>
                    ))}
                  </select>
                </td>
                <td className="px-1">
                  <input
                    type="checkbox"
                    aria-label={`Column ${String(c.name ?? i + 1)} accepts nulls`}
                    checked={c.nullable !== false}
                    onChange={(e) =>
                      update((j) => {
                        if (e.target.checked) delete at(j, i).nullable;
                        else at(j, i).nullable = false;
                      })
                    }
                  />
                </td>
                <td className="px-1">
                  <Button
                    size="icon-row"
                    variant="ghost"
                    label={`Delete column ${String(c.name ?? i + 1)}`}
                    onClick={() => update((j) => removeViewColumn(j, i))}
                  >
                    <Trash2 />
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <EmptyState title="No declared columns">The view&apos;s columns are what its body selects; declaring them is optional.</EmptyState>
      )}
    </div>
  );
}
