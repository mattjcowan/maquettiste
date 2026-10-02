// The view editor: General (name, schema, comment and the marks), Body (one SQL editor per dialect the body holds, with Add
// dialect for the others; a body is saved when its editor loses focus, on Ctrl+S or with Save, one undo step each), Columns (the
// optional declared columns), Code generation and References.
import { Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/misc";
import { CommonFields } from "@/inspector/fields";
import { References } from "@/inspector/Inspector";
import { BUILTIN_TYPES } from "@/model/model";
import { EDITOR_TAB_LABELS } from "@/model/labels";
import { viewBodyTemplate } from "@/explorer/databaseCreate";
import { domIdOf, EditorLayout, useCodeGenerationTab, useEditorContext, type EditorContext } from "../EditorFrame";
import { addViewColumn, removeViewColumn } from "./databaseDocs";
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

function ColumnsTab({ ctx }: { ctx: EditorContext }) {
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
                      if (v.trim()) update((j) => void (at(j, i).name = v.trim()));
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
